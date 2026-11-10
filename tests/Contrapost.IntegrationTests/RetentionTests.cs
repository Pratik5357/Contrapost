using Contrapost.Application.Retention;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Contrapost.IntegrationTests;

/// <summary>The cleanup jobs (design doc §10) delete only rows past their retention, and never unfinished work.</summary>
[Collection("api")]
public sealed class RetentionTests(ApiFixture api)
{
    [Fact]
    public async Task Only_completed_idempotency_keys_past_retention_are_deleted()
    {
        var client = await api.NewTestClientAsync();
        var tag = Guid.NewGuid().ToString("N");
        await InsertKeyAsync(client.ClientId, $"{tag}-old", "now() - interval '25 hours'");
        await InsertKeyAsync(client.ClientId, $"{tag}-recent", "now() - interval '23 hours'");

        var deleted = await DeleteAllAsync((store, ct) => store.DeleteExpiredIdempotencyKeysAsync(TimeSpan.FromHours(24), 2, ct));

        Assert.True(deleted >= 1);
        Assert.Equal([$"{tag}-recent"], await KeysAsync(client.ClientId));
    }

    [Fact]
    public async Task Only_outbox_messages_published_before_the_retention_window_are_deleted()
    {
        var aggregate = Guid.NewGuid();
        await InsertOutboxAsync(aggregate, "old-published", "now() - interval '8 days'");
        await InsertOutboxAsync(aggregate, "recent-published", "now() - interval '6 days'");
        await InsertOutboxAsync(aggregate, "old-unpublished", null, createdDaysAgo: 30);

        var deleted = await DeleteAllAsync((store, ct) => store.DeletePublishedOutboxMessagesAsync(TimeSpan.FromDays(7), 2, ct));

        Assert.True(deleted >= 1);
        Assert.Equal(["old-unpublished", "recent-published"], await OutboxTypesAsync(aggregate));
    }

    /// <summary>Runs the store the way the job does: batches until a short one, as the least-privilege login.</summary>
    private async Task<int> DeleteAllAsync(Func<IRetentionStore, CancellationToken, Task<int>> batch)
    {
        await using var host = api.CreateApi(new Dictionary<string, string?>());
        var total = 0;
        int deleted;
        do
        {
            await using var scope = host.Services.CreateAsyncScope();
            deleted = await batch(scope.ServiceProvider.GetRequiredService<IRetentionStore>(), CancellationToken.None);
            total += deleted;
        }
        while (deleted == 2);

        return total;
    }

    private Task InsertKeyAsync(Guid clientId, string key, string createdAt) => api.ScalarAsync<int>(
        $$"""
        INSERT INTO idempotency_keys (client_id, key, request_method, request_path, request_hash, response_code, response_body, created_at)
        VALUES (@c, @k, 'POST', '/v1/transfers', '\x00', 201, '{}', {{createdAt}}) RETURNING 1
        """, ("c", clientId), ("k", key));

    private Task InsertOutboxAsync(Guid aggregate, string type, string? publishedAt, int createdDaysAgo = 10) => api.ScalarAsync<int>(
        $$"""
        INSERT INTO outbox_messages (id, aggregate_id, type, payload, created_at, published_at)
        VALUES (gen_random_uuid(), @a, @t, '{}', now() - make_interval(days => {{createdDaysAgo}}), {{publishedAt ?? "NULL"}}) RETURNING 1
        """, ("a", aggregate), ("t", type));

    private async Task<List<string>> KeysAsync(Guid clientId) =>
        await ListAsync("SELECT key FROM idempotency_keys WHERE client_id = @p ORDER BY key", clientId);

    private async Task<List<string>> OutboxTypesAsync(Guid aggregate) =>
        await ListAsync("SELECT type FROM outbox_messages WHERE aggregate_id = @p ORDER BY type", aggregate);

    private async Task<List<string>> ListAsync(string sql, Guid parameter)
    {
        await using var connection = new NpgsqlConnection(api.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("p", parameter);
        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }
}
