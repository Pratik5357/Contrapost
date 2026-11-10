using Contrapost.Application.Accounts;
using Contrapost.Application.Admin;
using Contrapost.Application.Clients;
using Contrapost.Application.Holds;
using Contrapost.Application.Idempotency;
using Contrapost.Application.RateLimiting;
using Contrapost.Application.Reconciliation;
using Contrapost.Application.Retention;
using Contrapost.Application.Transfers;
using Contrapost.Infrastructure.Admin;
using Contrapost.Infrastructure.Ledger;
using Contrapost.Infrastructure.Messaging;
using Contrapost.Infrastructure.Persistence;
using Contrapost.Infrastructure.RateLimiting;
using Contrapost.Infrastructure.Reconciliation;
using Contrapost.Infrastructure.Retention;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using StackExchange.Redis;

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
        services.AddSingleton(new ReconciliationSettings(
            ChunkSize: configuration.GetValue("Reconciliation:ChunkSize", 1000),
            MaxFindingsPerCheck: configuration.GetValue("Reconciliation:MaxFindingsPerCheck", 1000),
            // I7 only looks at events still inside the outbox retention window (7 days), with a day's margin.
            EventWindow: TimeSpan.FromDays(configuration.GetValue("Reconciliation:EventWindowDays", 6))));
        services.AddScoped<IReconciliationStore, ReconciliationStore>();
        services.AddScoped<IAdminStore, AdminStore>();
        services.AddScoped<IRetentionStore, RetentionStore>();

        services.AddScoped<LedgerSession>();
        services.AddScoped<IIdempotencyScope>(provider => provider.GetRequiredService<LedgerSession>());

        services.AddHealthChecks()
            .AddCheck<SchemaHealthCheck>("postgres", tags: [ReadyTag]);

        return services;
    }

    /// <summary>
    /// Redis-backed rate limiting for the API. Returns false, registering nothing, when no Redis is
    /// configured; the caller decides whether that is acceptable.
    /// </summary>
    public static bool AddRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        if (configuration.GetConnectionString("Redis") is not { Length: > 0 } redis)
        {
            return false;
        }

        // Fail fast rather than hold requests: when Redis is slow or down the limiter fails open.
        var options = ConfigurationOptions.Parse(redis);
        options.AbortOnConnectFail = false;
        options.ConnectTimeout = 1000;
        options.AsyncTimeout = 250;
        options.SyncTimeout = 250;
        // Without this, commands queue in a backlog while disconnected and each request waits out the
        // timeout; fail-fast makes an outage cost nothing per request.
        options.BacklogPolicy = BacklogPolicy.FailFast;
        services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(options));
        services.AddSingleton<IRateLimiter, RedisRateLimiter>();
        return true;
    }

    /// <summary>RabbitMQ for the processes that publish or consume events (Worker, Consumers); the API never talks to the broker.</summary>
    public static IServiceCollection AddMessaging(this IServiceCollection services, IConfiguration configuration, string clientName)
    {
        var uri = configuration.GetConnectionString("RabbitMq")
            ?? throw new InvalidOperationException("Connection string 'RabbitMq' is not configured.");

        services.AddSingleton(_ => new RabbitMqConnection(uri, clientName));
        services.AddSingleton<OutboxRelay>();
        return services;
    }

    /// <summary>Applies pending migrations. Run as its own deploy step, never on API startup.</summary>
    public static async Task MigrateDatabaseAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ContrapostDbContext>();
        await db.Database.MigrateAsync(cancellationToken);
    }

    /// <summary>
    /// Creates (or updates the password of) a login that belongs to contrapost_app, the least-privilege role
    /// from migration 005. Run by `migrate`, as the owner, so the services never need the owner's login.
    /// </summary>
    public static async Task EnsureAppLoginAsync(this IServiceProvider services, string user, string password, CancellationToken cancellationToken = default)
    {
        var dataSource = services.GetRequiredService<NpgsqlDataSource>();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var exists = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT FROM pg_roles WHERE rolname = @user)", new { user }, cancellationToken: cancellationToken));

        // DDL takes no parameters, so the server's format() quotes the name (%I) and the password (%L).
        foreach (var template in new[] { exists ? "ALTER ROLE %I LOGIN PASSWORD %L" : "CREATE ROLE %I LOGIN PASSWORD %L", "GRANT contrapost_app TO %I" })
        {
            var ddl = await connection.ExecuteScalarAsync<string>(new CommandDefinition(
                "SELECT format(@template, @user::text, @password::text)", new { template, user, password }, cancellationToken: cancellationToken));
            await connection.ExecuteAsync(new CommandDefinition(ddl!, cancellationToken: cancellationToken));
        }
    }
}
