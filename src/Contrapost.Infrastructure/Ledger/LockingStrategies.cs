using System.Data;
using Contrapost.Application.Transfers;
using Contrapost.Domain;
using Contrapost.Domain.Ledger;
using Dapper;
using Npgsql;

namespace Contrapost.Infrastructure.Ledger;

/// <param name="Result">The transfer, or why it was refused.</param>
/// <param name="Attempts">How many times the transaction ran (1 unless it hit a conflict and retried).</param>
/// <param name="GaveUp">True when every attempt conflicted and the transfer was abandoned.</param>
public sealed record StrategyOutcome(Result<TransferView>? Result, int Attempts, bool GaveUp);

/// <summary>
/// The three concurrency-control strategies compared in design doc §8.1. They share the domain rules and
/// the write SQL; only how they keep concurrent writers from corrupting balances differs. Production uses
/// <see cref="Pessimistic"/> (through <see cref="TransferStore"/>); the others exist to be measured.
/// </summary>
public abstract class LockingStrategy(NpgsqlDataSource dataSource)
{
    public const int MaxAttempts = 10;

    public abstract string Name { get; }

    public static LockingStrategy Create(string name, NpgsqlDataSource dataSource) => name switch
    {
        "A" or "pessimistic" => new Pessimistic(dataSource),
        "B" or "optimistic" => new Optimistic(dataSource),
        "C" or "serializable" => new Serializable(dataSource),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Expected A, B or C."),
    };

    /// <summary>Posts a transfer (no idempotency key; the benchmark measures the ledger write itself).</summary>
    public async Task<StrategyOutcome> PostAsync(NewTransfer transfer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transfer);
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                if (await AttemptAsync(transfer, cancellationToken) is { } result)
                {
                    return new StrategyOutcome(result, attempt, GaveUp: false);
                }
            }
            catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected
                                               || ex is { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "postings_account_id_account_seq_key" })
            {
                // Retried below, like a version conflict. Under SERIALIZABLE, two writers that read the same
                // entry_seq can collide on the (account_id, account_seq) index before Postgres detects the
                // serialization anomaly, so the unique violation is a conflict too: the guard did its job.
            }

            // Jittered backoff that grows with the attempt number, so colliding writers spread out.
            await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(1, 5) * attempt), cancellationToken);
        }

        return new StrategyOutcome(null, MaxAttempts, GaveUp: true);
    }

    /// <summary>One transaction. Returns null when it lost a race and should be retried.</summary>
    private protected abstract Task<Result<TransferView>?> AttemptAsync(NewTransfer transfer, CancellationToken cancellationToken);

    private protected async Task<(NpgsqlConnection Connection, NpgsqlTransaction Transaction)> BeginAsync(IsolationLevel isolation, CancellationToken cancellationToken)
    {
        var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return (connection, await connection.BeginTransactionAsync(isolation, cancellationToken));
    }

    private protected static async Task<Dictionary<Guid, AccountRow>> ReadAsync(
        NpgsqlTransaction transaction, NewTransfer transfer, bool lockRows, CancellationToken cancellationToken)
    {
        var rows = await transaction.Connection!.QueryAsync<AccountRow>(new CommandDefinition(
            $"""
            SELECT {AccountRow.Columns}
            FROM account_balances b JOIN accounts a ON a.id = b.account_id
            WHERE b.account_id = ANY(@ids) AND a.client_id = @ClientId
            ORDER BY b.account_id
            {(lockRows ? "FOR UPDATE OF b" : "")}
            """,
            new { ids = transfer.Legs.Select(l => l.AccountId).ToArray(), transfer.ClientId },
            transaction,
            cancellationToken: cancellationToken));
        return rows.ToDictionary(r => r.Id);
    }

    /// <summary>Inserts the transaction, postings and outbox row: the same writes as <see cref="TransferStore"/>.</summary>
    private protected static async Task<TransferView> WriteAsync(
        NpgsqlTransaction transaction, NewTransfer transfer, List<LegView> legs, CancellationToken cancellationToken)
    {
        var db = new Enlistment(transaction, owned: false);
        var createdAt = await LedgerSql.InsertTransactionAsync(db, transfer.Id, transfer.ClientId, TransactionKind.Transfer,
            transfer.Currency, transfer.Reference, transfer.Description, null, null, cancellationToken);
        await LedgerSql.InsertPostingsAsync(db, transfer.Id, transfer.ClientId, transfer.Currency, legs, cancellationToken);
        await LedgerSql.EnqueueAsync(db, transfer.Id, "transfer.posted", new { transaction_id = transfer.Id }, cancellationToken);
        return new TransferView(transfer.Id, TransactionKind.Transfer, "posted", transfer.Currency, transfer.Reference,
            transfer.Description, null, null, createdAt, legs);
    }

    private protected static Task UpdateBalancesAsync(
        NpgsqlTransaction transaction, List<LegView> legs, IReadOnlyDictionary<Guid, Balance> balances, CancellationToken cancellationToken) =>
        LedgerSql.UpdateBalancesAsync(new Enlistment(transaction, owned: false),
            legs.Select(l => (l.AccountId, balances[l.AccountId], l.AccountSeq)).ToList(), cancellationToken);

    /// <summary>A: lock the balance rows in account-id order, then decide and write. Waits instead of retrying.</summary>
    private sealed class Pessimistic(NpgsqlDataSource dataSource) : LockingStrategy(dataSource)
    {
        public override string Name => "A pessimistic";

        private protected override async Task<Result<TransferView>?> AttemptAsync(NewTransfer transfer, CancellationToken cancellationToken)
        {
            var (connection, transaction) = await BeginAsync(IsolationLevel.ReadCommitted, cancellationToken);
            await using (connection)
            await using (transaction)
            {
                var locked = await ReadAsync(transaction, transfer, lockRows: true, cancellationToken);
                var applied = TransferRules.Apply(transfer.Currency, transfer.Legs, locked.ToDictionary(kv => kv.Key, kv => kv.Value.ToLocked()));
                if (applied.Error is { } rejected)
                {
                    return rejected;
                }

                var legs = LedgerSql.NumberLegs(transfer.Legs, locked);
                var view = await WriteAsync(transaction, transfer, legs, cancellationToken);
                await UpdateBalancesAsync(transaction, legs, applied.Value, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return view;
            }
        }
    }

    /// <summary>
    /// B: read without locks, then update each balance only if its version is unchanged; any miss means
    /// another writer got there first, so roll back and retry. Updates go in account-id order so two
    /// optimistic writers can't deadlock while their UPDATEs briefly hold row locks.
    /// </summary>
    private sealed class Optimistic(NpgsqlDataSource dataSource) : LockingStrategy(dataSource)
    {
        public override string Name => "B optimistic";

        private protected override async Task<Result<TransferView>?> AttemptAsync(NewTransfer transfer, CancellationToken cancellationToken)
        {
            var (connection, transaction) = await BeginAsync(IsolationLevel.ReadCommitted, cancellationToken);
            await using (connection)
            await using (transaction)
            {
                var read = await ReadAsync(transaction, transfer, lockRows: false, cancellationToken);
                var applied = TransferRules.Apply(transfer.Currency, transfer.Legs, read.ToDictionary(kv => kv.Key, kv => kv.Value.ToLocked()));
                if (applied.Error is { } rejected)
                {
                    return rejected;
                }

                var legs = LedgerSql.NumberLegs(transfer.Legs, read);
                foreach (var leg in legs.OrderBy(l => l.AccountId))
                {
                    var balance = applied.Value[leg.AccountId];
                    var updated = await connection.ExecuteAsync(new CommandDefinition(
                        """
                        UPDATE account_balances
                        SET debits_posted = @DebitsPosted, credits_posted = @CreditsPosted,
                            debits_pending = @DebitsPending, credits_pending = @CreditsPending,
                            entry_seq = @entrySeq, version = version + 1, updated_at = now()
                        WHERE account_id = @accountId AND version = @version
                        """,
                        new
                        {
                            balance.DebitsPosted,
                            balance.CreditsPosted,
                            balance.DebitsPending,
                            balance.CreditsPending,
                            entrySeq = leg.AccountSeq,
                            accountId = leg.AccountId,
                            version = read[leg.AccountId].Version,
                        },
                        transaction,
                        cancellationToken: cancellationToken));
                    if (updated == 0)
                    {
                        return null; // a concurrent writer changed this account; disposing rolls back
                    }
                }

                // The UPDATEs now hold the rows, so the account_seq values read above are still the next ones.
                var view = await WriteAsync(transaction, transfer, legs, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return view;
            }
        }
    }

    /// <summary>C: plain reads and writes in a SERIALIZABLE transaction; Postgres aborts conflicting ones (40001) and they are retried.</summary>
    private sealed class Serializable(NpgsqlDataSource dataSource) : LockingStrategy(dataSource)
    {
        public override string Name => "C serializable";

        private protected override async Task<Result<TransferView>?> AttemptAsync(NewTransfer transfer, CancellationToken cancellationToken)
        {
            var (connection, transaction) = await BeginAsync(IsolationLevel.Serializable, cancellationToken);
            await using (connection)
            await using (transaction)
            {
                var read = await ReadAsync(transaction, transfer, lockRows: false, cancellationToken);
                var applied = TransferRules.Apply(transfer.Currency, transfer.Legs, read.ToDictionary(kv => kv.Key, kv => kv.Value.ToLocked()));
                if (applied.Error is { } rejected)
                {
                    return rejected;
                }

                var legs = LedgerSql.NumberLegs(transfer.Legs, read);
                var view = await WriteAsync(transaction, transfer, legs, cancellationToken);
                await UpdateBalancesAsync(transaction, legs, applied.Value, cancellationToken);
                await transaction.CommitAsync(cancellationToken); // serialization failures can surface here too
                return view;
            }
        }
    }
}
