using System.Data;
using System.Globalization;
using Contrapost.Application.Idempotency;
using Dapper;
using Npgsql;

namespace Contrapost.Infrastructure.Ledger;

internal sealed record IdempotencySettings(TimeSpan LockTimeout);

/// <summary>
/// The request's database transaction when it runs under an idempotency key (design doc §5.1).
/// Scoped per request; stores use <see cref="Transaction"/> when it is set.
/// </summary>
internal sealed class LedgerSession(NpgsqlDataSource dataSource, IdempotencySettings settings) : IIdempotencyScope
{
    private NpgsqlConnection? _connection;
    private IdempotencyKey? _key;

    /// <summary>The active transaction, or null when no idempotent request is in progress.</summary>
    public NpgsqlTransaction? Transaction { get; private set; }

    public async Task<ClaimResult> BeginAsync(IdempotencyKey key, CancellationToken cancellationToken)
    {
        if (_connection is not null)
        {
            throw new InvalidOperationException("The idempotency scope has already begun.");
        }

        _connection = await dataSource.OpenConnectionAsync(cancellationToken);
        Transaction = await _connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        // A duplicate of an in-flight request blocks on the uncommitted key in the unique index;
        // lock_timeout bounds that wait. SET doesn't take parameters, so format the integer.
        var timeoutMs = ((long)settings.LockTimeout.TotalMilliseconds).ToString(CultureInfo.InvariantCulture);
        await _connection.ExecuteAsync($"SET LOCAL lock_timeout = '{timeoutMs}ms'", transaction: Transaction);

        int? claimed;
        try
        {
            claimed = await _connection.ExecuteScalarAsync<int?>(new CommandDefinition(
                """
                INSERT INTO idempotency_keys (client_id, key, request_method, request_path, request_hash)
                VALUES (@ClientId, @Key, @Method, @Path, @RequestHash)
                ON CONFLICT (client_id, key) DO NOTHING
                RETURNING 1
                """,
                key,
                Transaction,
                cancellationToken: cancellationToken));
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.LockNotAvailable)
        {
            await EndAsync();
            return new ClaimResult.InProgress();
        }

        if (claimed == 1)
        {
            _key = key;
            return new ClaimResult.Claimed();
        }

        // The conflicting row is committed (READ COMMITTED: this statement sees it), so it is complete.
        var stored = await _connection.QuerySingleAsync<StoredKey>(new CommandDefinition(
            """
            SELECT request_hash, response_code, response_body::text AS response_body
            FROM idempotency_keys
            WHERE client_id = @ClientId AND key = @Key
            """,
            key,
            Transaction,
            cancellationToken: cancellationToken));
        await EndAsync();

        if (!stored.RequestHash.AsSpan().SequenceEqual(key.RequestHash))
        {
            return new ClaimResult.KeyReused();
        }

        return stored.ResponseCode is { } code
            ? new ClaimResult.Replay(new StoredResponse(code, stored.ResponseBody ?? "null"))
            : new ClaimResult.InProgress();
    }

    public async Task CompleteAsync(StoredResponse response, CancellationToken cancellationToken)
    {
        if (_key is null || Transaction is null)
        {
            throw new InvalidOperationException("No claimed idempotency key to complete.");
        }

        await Transaction.Connection!.ExecuteAsync(new CommandDefinition(
            """
            UPDATE idempotency_keys
            SET response_code = @StatusCode, response_body = @Body::jsonb
            WHERE client_id = @ClientId AND key = @Key
            """,
            new { response.StatusCode, response.Body, _key.ClientId, _key.Key },
            Transaction,
            cancellationToken: cancellationToken));
        await Transaction.CommitAsync(cancellationToken);
        await EndAsync();
    }

    public ValueTask DisposeAsync() => new(EndAsync());

    /// <summary>Rolls back anything uncommitted and releases the connection.</summary>
    private async Task EndAsync()
    {
        if (Transaction is not null)
        {
            await Transaction.DisposeAsync();
            Transaction = null;
        }

        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }

        _key = null;
    }

    private sealed class StoredKey
    {
        public byte[] RequestHash { get; init; } = [];
        public int? ResponseCode { get; init; }
        public string? ResponseBody { get; init; }
    }
}
