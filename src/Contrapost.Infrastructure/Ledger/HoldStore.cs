using Contrapost.Application.Holds;
using Contrapost.Application.Observability;
using Contrapost.Application.Transfers;
using Contrapost.Domain;
using Contrapost.Domain.Ledger;
using Dapper;
using Npgsql;

namespace Contrapost.Infrastructure.Ledger;

/// <summary>
/// Hold lifecycle (design doc §4.4). Lock order is always the hold row(s) first, then the balance
/// rows in account-id order, so holds can't deadlock with each other, with transfers or with the expiry job.
/// </summary>
internal sealed class HoldStore(NpgsqlDataSource dataSource, LedgerSession session, LedgerMetrics metrics) : IHoldStore
{
    private const string HoldColumns = """
        id, debit_account_id, credit_account_id, currency, amount, captured_amount,
        status::text AS status, expires_at, created_at, now() AS db_now
        """;

    public async Task<Result<HoldView>> CreateAsync(NewHold hold, CancellationToken cancellationToken)
    {
        await using var db = await session.EnlistAsync(cancellationToken);

        var locked = await LedgerSql.LockAccountsAsync(db, metrics, hold.ClientId, [hold.DebitAccountId, hold.CreditAccountId], cancellationToken);
        var change = HoldRules.Create(hold.Currency, hold.DebitAccountId, hold.CreditAccountId, hold.Amount,
            locked.ToDictionary(kv => kv.Key, kv => kv.Value.ToLocked()));
        if (change.Error is { } rejected)
        {
            return rejected;
        }

        // expires_at comes from the database clock, like every other expiry decision.
        var row = await db.Connection.QuerySingleAsync<HoldRow>(new CommandDefinition(
            $"""
            INSERT INTO holds (id, client_id, debit_account_id, credit_account_id, currency, amount, expires_at)
            VALUES (@Id, @ClientId, @DebitAccountId, @CreditAccountId, @Currency, @Amount, now() + make_interval(secs => @ExpiresInSeconds))
            RETURNING {HoldColumns}
            """,
            hold,
            db.Transaction,
            cancellationToken: cancellationToken));

        await UpdateHoldBalancesAsync(db, row, change.Value, locked, posted: false, cancellationToken);
        await LedgerSql.EnqueueAsync(db, hold.Id, "hold.created", new
        {
            hold_id = hold.Id,
            debit_account_id = hold.DebitAccountId,
            credit_account_id = hold.CreditAccountId,
            currency = hold.Currency,
            amount = hold.Amount,
            expires_at = row.ExpiresAt,
        }, cancellationToken);

        await db.CommitAsync(cancellationToken);
        return row.ToView();
    }

    public async Task<HoldView?> GetAsync(Guid clientId, Guid holdId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<HoldRow>(new CommandDefinition(
            $"SELECT {HoldColumns} FROM holds WHERE id = @holdId AND client_id = @clientId",
            new { holdId, clientId },
            cancellationToken: cancellationToken));
        return row?.ToView();
    }

    public async Task<Result<CaptureView>> CaptureAsync(Guid clientId, Guid holdId, long? amount, CancellationToken cancellationToken)
    {
        await using var db = await session.EnlistAsync(cancellationToken);

        var row = await LockHoldAsync(db, clientId, holdId, cancellationToken);
        if (row is null)
        {
            return HoldNotFound(holdId);
        }

        var locked = await LedgerSql.LockAccountsAsync(db, metrics, clientId, [row.DebitAccountId, row.CreditAccountId], cancellationToken);
        var change = HoldRules.Capture(row.ToState(), amount, row.DbNow,
            locked[row.DebitAccountId].ToLocked(), locked[row.CreditAccountId].ToLocked());
        if (change.Error is { } rejected)
        {
            return rejected;
        }

        var captured = change.Value.CapturedAmount - row.CapturedAmount;
        var transactionId = Guid.CreateVersion7();
        var legs = LedgerSql.NumberLegs(
            [new TransferLeg(row.DebitAccountId, Direction.Debit, captured), new TransferLeg(row.CreditAccountId, Direction.Credit, captured)],
            locked);

        var createdAt = await LedgerSql.InsertTransactionAsync(db, transactionId, clientId, TransactionKind.Capture,
            row.Currency, null, null, holdId, null, cancellationToken);
        await LedgerSql.InsertPostingsAsync(db, transactionId, clientId, row.Currency, legs, cancellationToken);
        await UpdateHoldBalancesAsync(db, row, change.Value, locked, posted: true, cancellationToken);
        var updated = await SetHoldStateAsync(db, holdId, change.Value, cancellationToken);
        await LedgerSql.EnqueueAsync(db, transactionId, "hold.captured", new
        {
            hold_id = holdId,
            transaction_id = transactionId,
            amount = captured,
            captured_amount = updated.CapturedAmount,
            remaining = updated.ToState().Remaining,
            currency = row.Currency,
            legs = LedgerSql.LegsPayload(legs, locked, new Dictionary<Guid, Balance>
            {
                [row.DebitAccountId] = change.Value.Debit,
                [row.CreditAccountId] = change.Value.Credit,
            }),
        }, cancellationToken);

        await db.CommitAsync(cancellationToken);
        var transaction = new TransferView(transactionId, TransactionKind.Capture, "posted", row.Currency, null, null,
            holdId, null, createdAt, legs);
        return new CaptureView(updated.ToView(), transaction);
    }

    public async Task<Result<HoldView>> VoidAsync(Guid clientId, Guid holdId, CancellationToken cancellationToken)
    {
        await using var db = await session.EnlistAsync(cancellationToken);

        var row = await LockHoldAsync(db, clientId, holdId, cancellationToken);
        if (row is null)
        {
            return HoldNotFound(holdId);
        }

        var locked = await LedgerSql.LockAccountsAsync(db, metrics, clientId, [row.DebitAccountId, row.CreditAccountId], cancellationToken);
        var change = HoldRules.Release(row.ToState(), HoldStatus.Voided, row.DbNow,
            locked[row.DebitAccountId].ToLocked(), locked[row.CreditAccountId].ToLocked());
        if (change.Error is { } rejected)
        {
            return rejected;
        }

        await UpdateHoldBalancesAsync(db, row, change.Value, locked, posted: false, cancellationToken);
        var updated = await SetHoldStateAsync(db, holdId, change.Value, cancellationToken);
        await LedgerSql.EnqueueAsync(db, holdId, "hold.voided", new
        {
            hold_id = holdId,
            released = row.ToState().Remaining,
            captured_amount = updated.CapturedAmount,
        }, cancellationToken);

        await db.CommitAsync(cancellationToken);
        return updated.ToView();
    }

    public async Task<int> ExpireDueAsync(int batchSize, CancellationToken cancellationToken)
    {
        await using var db = await session.EnlistAsync(cancellationToken);

        // SKIP LOCKED: holds being captured or voided right now, or taken by another worker, are left alone.
        var due = (await db.Connection.QueryAsync<HoldRow>(new CommandDefinition(
            $"""
            SELECT {HoldColumns}
            FROM holds
            WHERE status IN ('pending', 'partially_captured') AND expires_at <= now()
            ORDER BY expires_at
            LIMIT @batchSize
            FOR UPDATE SKIP LOCKED
            """,
            new { batchSize },
            db.Transaction,
            cancellationToken: cancellationToken))).ToList();
        if (due.Count == 0)
        {
            return 0;
        }

        // All balance rows for the batch in one sorted statement, so the expiry job takes balance
        // locks in the same global order as transfers do.
        var locked = await LedgerSql.LockAccountsAsync(db, metrics, null, due.SelectMany(h => new[] { h.DebitAccountId, h.CreditAccountId }), cancellationToken);
        var accounts = locked.ToDictionary(kv => kv.Key, kv => kv.Value.ToLocked());

        foreach (var hold in due)
        {
            // Several holds can share an account, so each release builds on the previous one.
            var change = HoldRules.Release(hold.ToState(), HoldStatus.Expired, hold.DbNow,
                accounts[hold.DebitAccountId], accounts[hold.CreditAccountId]).Value;
            accounts[hold.DebitAccountId] = accounts[hold.DebitAccountId] with { Balance = change.Debit };
            accounts[hold.CreditAccountId] = accounts[hold.CreditAccountId] with { Balance = change.Credit };

            await LedgerSql.EnqueueAsync(db, hold.Id, "hold.expired", new
            {
                hold_id = hold.Id,
                released = hold.ToState().Remaining,
                captured_amount = hold.CapturedAmount,
            }, cancellationToken);
        }

        await db.Connection.ExecuteAsync(new CommandDefinition(
            "UPDATE holds SET status = 'expired' WHERE id = ANY(@ids)",
            new { ids = due.Select(h => h.Id).ToArray() },
            db.Transaction,
            cancellationToken: cancellationToken));
        await LedgerSql.UpdateBalancesAsync(db,
            locked.Keys.Select(id => (id, accounts[id].Balance, locked[id].EntrySeq)).ToList(), cancellationToken);

        await db.CommitAsync(cancellationToken);
        return due.Count;
    }

    private static LedgerError HoldNotFound(Guid id) =>
        new(ErrorKind.NotFound, "hold-not-found", "Hold not found", $"Hold {id} does not exist.");

    private static Task<HoldRow?> LockHoldAsync(Enlistment db, Guid clientId, Guid holdId, CancellationToken cancellationToken) =>
        db.Connection.QuerySingleOrDefaultAsync<HoldRow>(new CommandDefinition(
            $"SELECT {HoldColumns} FROM holds WHERE id = @holdId AND client_id = @clientId FOR UPDATE",
            new { holdId, clientId },
            db.Transaction,
            cancellationToken: cancellationToken));

    /// <summary>A capture posts one entry on each account; other hold changes only touch pending totals.</summary>
    private static Task UpdateHoldBalancesAsync(
        Enlistment db, HoldRow hold, HoldChange change, IReadOnlyDictionary<Guid, AccountRow> locked, bool posted, CancellationToken cancellationToken)
    {
        var increment = posted ? 1 : 0;
        return LedgerSql.UpdateBalancesAsync(db,
        [
            (hold.DebitAccountId, change.Debit, locked[hold.DebitAccountId].EntrySeq + increment),
            (hold.CreditAccountId, change.Credit, locked[hold.CreditAccountId].EntrySeq + increment),
        ], cancellationToken);
    }

    private static Task<HoldRow> SetHoldStateAsync(Enlistment db, Guid holdId, HoldChange change, CancellationToken cancellationToken) =>
        db.Connection.QuerySingleAsync<HoldRow>(new CommandDefinition(
            $"""
            UPDATE holds SET captured_amount = @CapturedAmount, status = @status::hold_status
            WHERE id = @holdId
            RETURNING {HoldColumns}
            """,
            new { holdId, change.CapturedAmount, status = HoldRules.Label(change.Status) },
            db.Transaction,
            cancellationToken: cancellationToken));

    private sealed class HoldRow
    {
        public Guid Id { get; init; }
        public Guid DebitAccountId { get; init; }
        public Guid CreditAccountId { get; init; }
        public string Currency { get; init; } = "";
        public long Amount { get; init; }
        public long CapturedAmount { get; init; }
        public string Status { get; init; } = "";
        public DateTimeOffset ExpiresAt { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset DbNow { get; init; }

        public HoldState ToState() => new(Id, DebitAccountId, CreditAccountId, Currency, Amount, CapturedAmount,
            PgEnum.ToHoldStatus(Status), ExpiresAt);

        public HoldView ToView()
        {
            var state = ToState();
            return new HoldView(Id, state.Status, DebitAccountId, CreditAccountId, Currency, Amount, CapturedAmount,
                state.Remaining, ExpiresAt, CreatedAt);
        }
    }
}
