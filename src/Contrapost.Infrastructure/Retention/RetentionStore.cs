using Contrapost.Application.Retention;
using Dapper;
using Npgsql;

namespace Contrapost.Infrastructure.Retention;

/// <summary>Batched deletes by age, using the database clock. SKIP LOCKED lets several Workers share the work.</summary>
internal sealed class RetentionStore(NpgsqlDataSource dataSource) : IRetentionStore
{
    public Task<int> DeleteExpiredIdempotencyKeysAsync(TimeSpan retention, int batchSize, CancellationToken cancellationToken) =>
        DeleteAsync(
            """
            DELETE FROM idempotency_keys WHERE (client_id, key) IN (
                SELECT client_id, key FROM idempotency_keys
                WHERE created_at < now() - @retention AND response_code IS NOT NULL
                ORDER BY created_at LIMIT @batchSize FOR UPDATE SKIP LOCKED)
            """,
            retention, batchSize, cancellationToken);

    public Task<int> DeletePublishedOutboxMessagesAsync(TimeSpan retention, int batchSize, CancellationToken cancellationToken) =>
        DeleteAsync(
            """
            DELETE FROM outbox_messages WHERE id IN (
                SELECT id FROM outbox_messages
                WHERE published_at < now() - @retention
                ORDER BY published_at LIMIT @batchSize FOR UPDATE SKIP LOCKED)
            """,
            retention, batchSize, cancellationToken);

    private async Task<int> DeleteAsync(string sql, TimeSpan retention, int batchSize, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteAsync(new CommandDefinition(sql, new { retention, batchSize }, cancellationToken: cancellationToken));
    }
}
