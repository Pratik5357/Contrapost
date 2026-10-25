using System.Net;
using Contrapost.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace Contrapost.IntegrationTests;

// The fixture's database starts unmigrated, and the test below migrates it.
// Tests that need a migrated database belong in their own class.
public sealed class HealthAndMigrationTests : IClassFixture<PostgresFixture>, IDisposable
{
    private readonly PostgresFixture _postgres;
    private readonly WebApplicationFactory<Program> _factory;

    public HealthAndMigrationTests(PostgresFixture postgres)
    {
        _postgres = postgres;
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseSetting("ConnectionStrings:Postgres", postgres.ConnectionString));
    }

    [Fact]
    public async Task Ready_is_unhealthy_until_migrations_are_applied_and_live_is_always_healthy()
    {
        using var client = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(new Uri("/health/live", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync(new Uri("/health/ready", UriKind.Relative))).StatusCode);

        await _factory.Services.MigrateDatabaseAsync();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(new Uri("/health/ready", UriKind.Relative))).StatusCode);

        // The migration applies db/schema.sql including its guards, not just the tables.
        await using var connection = new NpgsqlConnection(_postgres.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("UPDATE postings SET amount = 1", connection);
        var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal("CP002", error.SqlState);
    }

    public void Dispose() => _factory.Dispose();
}
