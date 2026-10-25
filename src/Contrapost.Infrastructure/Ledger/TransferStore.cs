using System.Data;
using System.Text.Json;
using Contrapost.Application.Transfers;
using Contrapost.Domain;
using Contrapost.Domain.Ledger;
using Dapper;
using Npgsql;

namespace Contrapost.Infrastructure.Ledger;

/// <summary>Strategy A from design doc §8.1: pessimistic row locks taken in account-id order.</summary>
internal sealed class TransferStore(NpgsqlDataSource dataSource, LedgerSession session) : ITransferStore
{
    public async Task<Result<TransferView>> PostAsync(NewTransfer transfer, CancellationToken cancellationToken)
    {
        // Under an idempotency key the request's transaction is already open and is committed
        // by the idempotency scope together with the stored response; otherwise use our own.
        await using var own = session.Transaction is null ? await dataSource.OpenConnectionAsync(cancellationToken) : null;
        // READ COMMITTED is enough: every value a decision depends on is read under FOR UPDATE.
        await using var ownTransaction = own is null ? null : await own.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var transaction = session.Transaction ?? ownTransaction!;
        var connection = transaction.Connection!;

        // Postgres locks rows in the ORDER BY order (LockRows sits above Sort), so two
        // transfers over the same accounts always lock them in the same order and can't deadlock.
        // Accounts of other clients are filtered out here and surface as "not found".
        var locked = (await connection.QueryAsync<AccountRow>(new CommandDefinition(
            $"""
            SELECT {AccountRow.Columns}
            FROM account_balances b
            JOIN accounts a ON a.id = b.account_id
            WHERE b.account_id = ANY(@ids) AND a.client_id = @ClientId
            ORDER BY b.account_id
            FOR UPDATE OF b
            """,
            new { ids = transfer.Legs.Select(l => l.AccountId).ToArray(), transfer.ClientId },
            transaction,
            cancellationToken: cancellationToken))).ToDictionary(r => r.Id);

        var applied = TransferRules.Apply(
            transfer.Currency,
            transfer.Legs,
            locked.ToDictionary(kv => kv.Key, kv => kv.Value.ToLocked()));
        if (applied.Error is { } rejected)
        {
            return rejected;
        }

        // Each account appears once per transfer, so its new entry_seq is the old one plus one.
        var legs = transfer.Legs
            .Select(l => new LegView(l.AccountId, l.Direction, l.Amount, locked[l.AccountId].EntrySeq + 1))
            .ToList();

        // A duplicate reference aborts the statement; the savepoint keeps the surrounding
        // transaction usable so the 409 can still be stored against the idempotency key.
        await transaction.SaveAsync("transfer_insert", cancellationToken);
        DateTimeOffset createdAt;
        try
        {
            createdAt = await connection.ExecuteScalarAsync<DateTimeOffset>(new CommandDefinition(
                """
                INSERT INTO transactions (id, client_id, kind, currency, reference, description)
                VALUES (@Id, @ClientId, 'transfer', @Currency, @Reference, @Description)
                RETURNING created_at
                """,
                new { transfer.Id, transfer.ClientId, transfer.Currency, transfer.Reference, transfer.Description },
                transaction,
                cancellationToken: cancellationToken));
        }
        catch (PostgresException ex) when (ex is { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "ux_txn_transfer_reference" })
        {
            await transaction.RollbackAsync("transfer_insert", cancellationToken);
            return new LedgerError(ErrorKind.Conflict, "duplicate-reference", "Duplicate reference",
                $"A transfer with reference '{transfer.Reference}' already exists.");
        }

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO postings (transaction_id, client_id, currency, account_id, account_seq, direction, amount)
            SELECT @Id, @ClientId, @Currency, l.account_id, l.account_seq, l.direction::direction, l.amount
            FROM unnest(@accountIds, @seqs, @directions, @amounts) AS l(account_id, account_seq, direction, amount);

            UPDATE account_balances b
            SET debits_posted  = v.debits_posted,
                credits_posted = v.credits_posted,
                entry_seq      = v.entry_seq,
                version        = b.version + 1,
                updated_at     = now()
            FROM unnest(@accountIds, @debitsPosted, @creditsPosted, @seqs)
                 AS v(account_id, debits_posted, credits_posted, entry_seq)
            WHERE b.account_id = v.account_id;

            INSERT INTO outbox_messages (id, aggregate_id, type, payload)
            VALUES (@outboxId, @Id, 'transfer.posted', @payload::jsonb);
            """,
            new
            {
                transfer.Id,
                transfer.ClientId,
                transfer.Currency,
                accountIds = legs.Select(l => l.AccountId).ToArray(),
                seqs = legs.Select(l => l.AccountSeq).ToArray(),
                directions = legs.Select(l => PgEnum.ToLabel(l.Direction)).ToArray(),
                amounts = legs.Select(l => l.Amount).ToArray(),
                debitsPosted = legs.Select(l => applied.Value[l.AccountId].DebitsPosted).ToArray(),
                creditsPosted = legs.Select(l => applied.Value[l.AccountId].CreditsPosted).ToArray(),
                outboxId = Guid.CreateVersion7(),
                payload = JsonSerializer.Serialize(new
                {
                    transaction_id = transfer.Id,
                    currency = transfer.Currency,
                    reference = transfer.Reference,
                    legs = legs.Select(l => new
                    {
                        account_id = l.AccountId,
                        direction = PgEnum.ToLabel(l.Direction),
                        amount = l.Amount,
                        account_seq = l.AccountSeq,
                    }),
                }),
            },
            transaction,
            cancellationToken: cancellationToken));

        if (ownTransaction is not null)
        {
            // The deferred balance triggers run at commit; a failure there is a bug, not a rejection.
            await ownTransaction.CommitAsync(cancellationToken);
        }

        return new TransferView(transfer.Id, "posted", transfer.Currency, transfer.Reference, transfer.Description, createdAt, legs);
    }

    public async Task<TransferView?> GetAsync(Guid clientId, Guid transferId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var results = await connection.QueryMultipleAsync(new CommandDefinition(
            """
            SELECT id, currency, reference, description, created_at
            FROM transactions
            WHERE id = @transferId AND client_id = @clientId AND kind = 'transfer';

            SELECT account_id, direction::text AS direction, amount, account_seq
            FROM postings
            WHERE transaction_id = @transferId AND client_id = @clientId
            ORDER BY id;
            """,
            new { transferId, clientId },
            cancellationToken: cancellationToken));

        var header = await results.ReadSingleOrDefaultAsync<TransactionRow>();
        if (header is null)
        {
            return null;
        }

        var legs = (await results.ReadAsync<PostingRow>())
            .Select(p => new LegView(p.AccountId, PgEnum.ToDirection(p.Direction), p.Amount, p.AccountSeq))
            .ToList();
        return new TransferView(header.Id, "posted", header.Currency, header.Reference, header.Description, header.CreatedAt, legs);
    }

    private sealed class TransactionRow
    {
        public Guid Id { get; init; }
        public string Currency { get; init; } = "";
        public string? Reference { get; init; }
        public string? Description { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
    }

    private sealed class PostingRow
    {
        public Guid AccountId { get; init; }
        public string Direction { get; init; } = "";
        public long Amount { get; init; }
        public long AccountSeq { get; init; }
    }
}
