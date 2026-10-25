using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Contrapost.Infrastructure.Persistence;

/// <summary>
/// Ready only when the database is reachable and every migration has been applied,
/// so the API takes no traffic before the migrate step has finished.
/// </summary>
internal sealed class SchemaHealthCheck(ContrapostDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
            return pending.Count == 0
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy($"Pending migrations: {string.Join(", ", pending)}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("Database is unreachable.", ex);
        }
    }
}
