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
        await using var db = await session.EnlistAsync(cancellationToken);

        var locked = await LedgerSql.LockAccountsAsync(db, transfer.ClientId, transfer.Legs.Select(l => l.AccountId), cancellationToken);
        var applied = TransferRules.Apply(transfer.Currency, transfer.Legs, locked.ToDictionary(kv => kv.Key, kv => kv.Value.ToLocked()));
        if (applied.Error is { } rejected)
        {
            return rejected;
        }

        var legs = LedgerSql.NumberLegs(transfer.Legs, locked);

        // A duplicate reference aborts the statement; the savepoint keeps the surrounding
        // transaction usable so the 409 can still be stored against the idempotency key.
        await db.Transaction.SaveAsync("transfer_insert", cancellationToken);
        DateTimeOffset createdAt;
        try
        {
            createdAt = await LedgerSql.InsertTransactionAsync(db, transfer.Id, transfer.ClientId, TransactionKind.Transfer,
                transfer.Currency, transfer.Reference, transfer.Description, null, null, cancellationToken);
        }
        catch (PostgresException ex) when (ex is { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "ux_txn_transfer_reference" })
        {
            await db.Transaction.RollbackAsync("transfer_insert", cancellationToken);
            return new LedgerError(ErrorKind.Conflict, "duplicate-reference", "Duplicate reference",
                $"A transfer with reference '{transfer.Reference}' already exists.");
        }

        await WritePostingsAsync(db, transfer.Id, transfer.ClientId, transfer.Currency, legs, applied.Value, cancellationToken);
        await LedgerSql.EnqueueAsync(db, transfer.Id, "transfer.posted", new
        {
            transaction_id = transfer.Id,
            currency = transfer.Currency,
            reference = transfer.Reference,
            legs = LedgerSql.LegsPayload(legs),
        }, cancellationToken);

        await db.CommitAsync(cancellationToken);
        return new TransferView(transfer.Id, TransactionKind.Transfer, "posted", transfer.Currency, transfer.Reference,
            transfer.Description, null, null, createdAt, legs);
    }

    public async Task<Result<TransferView>> ReverseAsync(Guid clientId, Guid transactionId, Guid reversalId, string? description, CancellationToken cancellationToken)
    {
        await using var db = await session.EnlistAsync(cancellationToken);

        // Transactions and postings are immutable, so they can be read without locks.
        var original = await ReadAsync(db.Connection, db.Transaction, clientId, transactionId, cancellationToken);
        if (original is null)
        {
            return TransferNotFound(transactionId);
        }

        if (original.Kind == TransactionKind.Reversal)
        {
            return new LedgerError(ErrorKind.Rejected, "not-reversible", "Transaction cannot be reversed",
                $"Transaction {transactionId} is itself a reversal. Post a new transfer instead.");
        }

        var reversedLegs = TransferRules.Reversed(original.Legs.Select(l => new TransferLeg(l.AccountId, l.Direction, l.Amount)));
        var locked = await LedgerSql.LockAccountsAsync(db, clientId, reversedLegs.Select(l => l.AccountId), cancellationToken);

        // Checked after the locks: a concurrent reversal of the same transaction holds them until it
        // commits, so this statement sees it. Checking first also means "already reversed" wins over
        // "insufficient funds", which a second reversal would otherwise hit.
        var alreadyReversed = await db.Connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM transactions WHERE reverses_transaction_id = @transactionId)",
            new { transactionId },
            db.Transaction,
            cancellationToken: cancellationToken));
        if (alreadyReversed)
        {
            return AlreadyReversed(transactionId);
        }

        var applied = TransferRules.Apply(original.Currency, reversedLegs, locked.ToDictionary(kv => kv.Key, kv => kv.Value.ToLocked()), isReversal: true);
        if (applied.Error is { } rejected)
        {
            return rejected;
        }

        var legs = LedgerSql.NumberLegs(reversedLegs, locked);

        // ux_txn_single_reversal is the backstop if the check above is ever bypassed.
        await db.Transaction.SaveAsync("reversal_insert", cancellationToken);
        DateTimeOffset createdAt;
        try
        {
            createdAt = await LedgerSql.InsertTransactionAsync(db, reversalId, clientId, TransactionKind.Reversal,
                original.Currency, null, description, null, transactionId, cancellationToken);
        }
        catch (PostgresException ex) when (ex is { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "ux_txn_single_reversal" })
        {
            await db.Transaction.RollbackAsync("reversal_insert", cancellationToken);
            return AlreadyReversed(transactionId);
        }

        await WritePostingsAsync(db, reversalId, clientId, original.Currency, legs, applied.Value, cancellationToken);
        await LedgerSql.EnqueueAsync(db, reversalId, "transfer.reversed", new
        {
            transaction_id = reversalId,
            reverses_transaction_id = transactionId,
            currency = original.Currency,
            legs = LedgerSql.LegsPayload(legs),
        }, cancellationToken);

        await db.CommitAsync(cancellationToken);
        return new TransferView(reversalId, TransactionKind.Reversal, "posted", original.Currency, null, description,
            null, transactionId, createdAt, legs);
    }

    public async Task<TransferView?> GetAsync(Guid clientId, Guid transferId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await ReadAsync(connection, null, clientId, transferId, cancellationToken);
    }

    private static LedgerError AlreadyReversed(Guid id) =>
        new(ErrorKind.Conflict, "already-reversed", "Already reversed", $"Transaction {id} has already been reversed.");

    internal static LedgerError TransferNotFound(Guid id) =>
        new(ErrorKind.NotFound, "transfer-not-found", "Transfer not found", $"Transfer {id} does not exist.");

    private static async Task WritePostingsAsync(
        Enlistment db, Guid transactionId, Guid clientId, string currency, List<LegView> legs,
        IReadOnlyDictionary<Guid, Balance> balances, CancellationToken cancellationToken)
    {
        await LedgerSql.InsertPostingsAsync(db, transactionId, clientId, currency, legs, cancellationToken);
        await LedgerSql.UpdateBalancesAsync(db, legs.Select(l => (l.AccountId, balances[l.AccountId], l.AccountSeq)).ToList(), cancellationToken);
    }

    private static async Task<TransferView?> ReadAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid clientId, Guid transactionId, CancellationToken cancellationToken)
    {
        await using var results = await connection.QueryMultipleAsync(new CommandDefinition(
            """
            SELECT id, kind::text AS kind, currency, reference, description, hold_id, reverses_transaction_id, created_at
            FROM transactions
            WHERE id = @transactionId AND client_id = @clientId;

            SELECT account_id, direction::text AS direction, amount, account_seq
            FROM postings
            WHERE transaction_id = @transactionId AND client_id = @clientId
            ORDER BY id;
            """,
            new { transactionId, clientId },
            transaction,
            cancellationToken: cancellationToken));

        var header = await results.ReadSingleOrDefaultAsync<TransactionRow>();
        if (header is null)
        {
            return null;
        }

        var legs = (await results.ReadAsync<PostingRow>())
            .Select(p => new LegView(p.AccountId, PgEnum.ToDirection(p.Direction), p.Amount, p.AccountSeq))
            .ToList();
        return new TransferView(header.Id, PgEnum.ToTransactionKind(header.Kind), "posted", header.Currency, header.Reference,
            header.Description, header.HoldId, header.ReversesTransactionId, header.CreatedAt, legs);
    }

    private sealed class TransactionRow
    {
        public Guid Id { get; init; }
        public string Kind { get; init; } = "";
        public string Currency { get; init; } = "";
        public string? Reference { get; init; }
        public string? Description { get; init; }
        public Guid? HoldId { get; init; }
        public Guid? ReversesTransactionId { get; init; }
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
