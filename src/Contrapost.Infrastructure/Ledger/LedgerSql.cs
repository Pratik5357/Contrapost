using System.Text.Json;
using Contrapost.Application.Observability;
using Contrapost.Application.Transfers;
using Contrapost.Domain.Ledger;
using Contrapost.Infrastructure.Observability;
using Dapper;

namespace Contrapost.Infrastructure.Ledger;

/// <summary>The write-path SQL shared by transfers, captures, reversals and holds.</summary>
internal static class LedgerSql
{
    /// <summary>
    /// Locks account_balances rows in account-id order: Postgres takes the locks in ORDER BY order
    /// (LockRows sits above Sort), so writers touching the same accounts can't deadlock (design doc §8.1).
    /// With a <paramref name="clientId"/>, other clients' accounts are filtered out and surface as
    /// "not found"; the expiry job passes null to work across clients.
    /// </summary>
    public static async Task<Dictionary<Guid, AccountRow>> LockAccountsAsync(
        Enlistment db, LedgerMetrics metrics, Guid? clientId, IEnumerable<Guid> accountIds, CancellationToken cancellationToken)
    {
        var started = LedgerMetrics.Start();
        var rows = await db.Connection.QueryAsync<AccountRow>(new CommandDefinition(
            $"""
            SELECT {AccountRow.Columns}
            FROM account_balances b
            JOIN accounts a ON a.id = b.account_id
            WHERE b.account_id = ANY(@ids) AND (@clientId::uuid IS NULL OR a.client_id = @clientId)
            ORDER BY b.account_id
            FOR UPDATE OF b
            """,
            new { ids = accountIds.Distinct().ToArray(), clientId },
            db.Transaction,
            cancellationToken: cancellationToken));
        metrics.RecordLockWait(LedgerMetrics.Elapsed(started));
        return rows.ToDictionary(r => r.Id);
    }

    /// <summary>Inserts a transactions row and returns its created_at.</summary>
    public static Task<DateTimeOffset> InsertTransactionAsync(
        Enlistment db, Guid id, Guid clientId, TransactionKind kind, string currency, string? reference, string? description,
        Guid? holdId, Guid? reversesTransactionId, CancellationToken cancellationToken) =>
        db.Connection.ExecuteScalarAsync<DateTimeOffset>(new CommandDefinition(
            """
            INSERT INTO transactions (id, client_id, kind, currency, reference, description, hold_id, reverses_transaction_id)
            VALUES (@id, @clientId, @kind::txn_kind, @currency, @reference, @description, @holdId, @reversesTransactionId)
            RETURNING created_at
            """,
            new { id, clientId, kind = PgEnum.ToLabel(kind), currency, reference, description, holdId, reversesTransactionId },
            db.Transaction,
            cancellationToken: cancellationToken));

    /// <summary>
    /// Builds each leg's view with the account's next entry_seq. Every account appears once per
    /// transaction, so that is the locked entry_seq plus one.
    /// </summary>
    public static List<LegView> NumberLegs(IEnumerable<TransferLeg> legs, IReadOnlyDictionary<Guid, AccountRow> locked) =>
        legs.Select(l => new LegView(l.AccountId, l.Direction, l.Amount, locked[l.AccountId].EntrySeq + 1)).ToList();

    public static Task InsertPostingsAsync(
        Enlistment db, Guid transactionId, Guid clientId, string currency, IReadOnlyList<LegView> legs, CancellationToken cancellationToken) =>
        db.Connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO postings (transaction_id, client_id, currency, account_id, account_seq, direction, amount)
            SELECT @transactionId, @clientId, @currency, l.account_id, l.account_seq, l.direction::direction, l.amount
            FROM unnest(@accountIds, @seqs, @directions, @amounts) AS l(account_id, account_seq, direction, amount)
            """,
            new
            {
                transactionId,
                clientId,
                currency,
                accountIds = legs.Select(l => l.AccountId).ToArray(),
                seqs = legs.Select(l => l.AccountSeq).ToArray(),
                directions = legs.Select(l => PgEnum.ToLabel(l.Direction)).ToArray(),
                amounts = legs.Select(l => l.Amount).ToArray(),
            },
            db.Transaction,
            cancellationToken: cancellationToken));

    /// <summary>Writes new totals computed under the row locks, and bumps version on every row.</summary>
    public static Task UpdateBalancesAsync(
        Enlistment db, IReadOnlyCollection<(Guid AccountId, Balance Balance, long EntrySeq)> updates, CancellationToken cancellationToken) =>
        db.Connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE account_balances b
            SET debits_posted   = v.debits_posted,
                credits_posted  = v.credits_posted,
                debits_pending  = v.debits_pending,
                credits_pending = v.credits_pending,
                entry_seq       = v.entry_seq,
                version         = b.version + 1,
                updated_at      = now()
            FROM unnest(@ids, @debitsPosted, @creditsPosted, @debitsPending, @creditsPending, @entrySeqs)
                 AS v(account_id, debits_posted, credits_posted, debits_pending, credits_pending, entry_seq)
            WHERE b.account_id = v.account_id
            """,
            new
            {
                ids = updates.Select(u => u.AccountId).ToArray(),
                debitsPosted = updates.Select(u => u.Balance.DebitsPosted).ToArray(),
                creditsPosted = updates.Select(u => u.Balance.CreditsPosted).ToArray(),
                debitsPending = updates.Select(u => u.Balance.DebitsPending).ToArray(),
                creditsPending = updates.Select(u => u.Balance.CreditsPending).ToArray(),
                entrySeqs = updates.Select(u => u.EntrySeq).ToArray(),
            },
            db.Transaction,
            cancellationToken: cancellationToken));

    /// <summary>Adds an event to the transactional outbox (design doc §9), with the current trace context (§12).</summary>
    public static Task EnqueueAsync(Enlistment db, Guid aggregateId, string type, object payload, CancellationToken cancellationToken) =>
        db.Connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO outbox_messages (id, aggregate_id, type, payload, traceparent)
            VALUES (@id, @aggregateId, @type, @payload::jsonb, @traceparent)
            """,
            new { id = Guid.CreateVersion7(), aggregateId, type, payload = JsonSerializer.Serialize(payload), traceparent = Telemetry.CurrentTraceparent() },
            db.Transaction,
            cancellationToken: cancellationToken));

    /// <summary>
    /// The event payload shape for posted legs. Each leg carries the account's normal side and its
    /// posted balance after the leg, so consumers needn't query the ledger (design doc §9.1).
    /// </summary>
    public static object LegsPayload(
        IEnumerable<LegView> legs, IReadOnlyDictionary<Guid, AccountRow> locked, IReadOnlyDictionary<Guid, Balance> balances) =>
        legs.Select(l =>
        {
            var side = PgEnum.ToNormalSide(locked[l.AccountId].NormalSide);
            return new
            {
                account_id = l.AccountId,
                direction = PgEnum.ToLabel(l.Direction),
                amount = l.Amount,
                account_seq = l.AccountSeq,
                normal_side = PgEnum.ToLabel(side),
                balance_after = balances[l.AccountId].Posted(side),
            };
        });
}
