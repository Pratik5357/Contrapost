using Contrapost.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Contrapost.Infrastructure;

public static class DependencyInjection
{
    public const string ReadyTag = "ready";

    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("Connection string 'Postgres' is not configured.");

        services.AddDbContext<ContrapostDbContext>(options => options.UseNpgsql(connectionString));

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
