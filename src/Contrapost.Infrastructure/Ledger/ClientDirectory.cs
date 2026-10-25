using Contrapost.Application.Clients;
using Dapper;
using Npgsql;

namespace Contrapost.Infrastructure.Ledger;

internal sealed class ClientDirectory(NpgsqlDataSource dataSource) : IClientDirectory
{
    public async Task<bool> IsActiveAsync(Guid clientId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM api_clients WHERE id = @clientId AND revoked_at IS NULL)",
            new { clientId },
            cancellationToken: cancellationToken));
    }
}
