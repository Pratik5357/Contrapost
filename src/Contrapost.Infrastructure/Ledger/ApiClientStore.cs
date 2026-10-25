using Contrapost.Application.Clients;
using Dapper;
using Npgsql;

namespace Contrapost.Infrastructure.Ledger;

internal sealed class ApiClientStore(NpgsqlDataSource dataSource) : IApiClientStore
{
    public async Task<ApiClientCredentials?> FindByPrefixAsync(string prefix, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<ClientRow>(new CommandDefinition(
            "SELECT id, key_hash, scopes, rate_limit_rpm FROM api_clients WHERE key_prefix = @prefix AND revoked_at IS NULL",
            new { prefix },
            cancellationToken: cancellationToken));
        return row is null ? null : new ApiClientCredentials(row.Id, row.KeyHash, row.Scopes, row.RateLimitRpm);
    }

    public async Task CreateAsync(Guid clientId, string name, ApiKeys.Generated key, IReadOnlyList<string> scopes, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO api_clients (id, name, key_prefix, key_hash, scopes)
            VALUES (@clientId, @name, @prefix, @hash, @scopes)
            """,
            new { clientId, name, prefix = key.Prefix, hash = key.SecretHash, scopes = scopes.ToArray() },
            cancellationToken: cancellationToken));
    }

    private sealed class ClientRow
    {
        public Guid Id { get; init; }
        public byte[] KeyHash { get; init; } = [];
        public string[] Scopes { get; init; } = [];
        public int RateLimitRpm { get; init; }
    }
}
