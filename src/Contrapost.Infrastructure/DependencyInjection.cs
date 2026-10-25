using Contrapost.Application.Accounts;
using Contrapost.Application.Clients;
using Contrapost.Application.Holds;
using Contrapost.Application.Idempotency;
using Contrapost.Application.Transfers;
using Contrapost.Infrastructure.Ledger;
using Contrapost.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Contrapost.Infrastructure;

public static class DependencyInjection
{
    public const string ReadyTag = "ready";

    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("Connection string 'Postgres' is not configured.");

        DapperSetup.Configure();

        // One pool shared by EF Core (migrations, simple reads) and Dapper (the write path).
        services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
        services.AddDbContext<ContrapostDbContext>((provider, options) =>
            options.UseNpgsql(provider.GetRequiredService<NpgsqlDataSource>()));

        services.AddScoped<IAccountStore, AccountStore>();
        services.AddScoped<ITransferStore, TransferStore>();
        services.AddScoped<IHoldStore, HoldStore>();
        services.AddScoped<IApiClientStore, ApiClientStore>();

        services.AddSingleton(new IdempotencySettings(
            TimeSpan.FromMilliseconds(configuration.GetValue("Idempotency:LockTimeoutMs", 5000))));
        services.AddScoped<LedgerSession>();
        services.AddScoped<IIdempotencyScope>(provider => provider.GetRequiredService<LedgerSession>());

        services.AddHealthChecks()
            .AddCheck<SchemaHealthCheck>("postgres", tags: [ReadyTag]);

        return services;
    }

    /// <summary>Applies pending migrations. Run as its own deploy step, never on API startup.</summary>
    public static async Task MigrateDatabaseAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ContrapostDbContext>();
        await db.Database.MigrateAsync(cancellationToken);
    }
}
