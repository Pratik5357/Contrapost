using System.Globalization;
using Contrapost.Application.Reconciliation;

namespace Contrapost.Worker.Jobs;

/// <summary>
/// Runs reconciliation nightly (design doc §10) and alerts on drift. Several Workers may run this job;
/// the store's advisory lock lets only one of them reconcile at a time.
/// </summary>
internal sealed partial class ReconciliationJob(
    IServiceScopeFactory scopes, IConfiguration configuration, TimeProvider clock, LedgerHealthMetrics health, ILogger<ReconciliationJob> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var dailyAt = TimeOnly.Parse(configuration.GetValue("Jobs:Reconciliation:DailyAtUtc", "02:00")!, CultureInfo.InvariantCulture);

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(UntilNext(dailyAt), clock, stoppingToken);
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var run = await scope.ServiceProvider.GetRequiredService<IReconciliationStore>().RunAsync(stoppingToken);
                if (run?.FindingsCount is { } findings)
                {
                    health.SetReconciliationFindings(findings);
                }

                switch (run)
                {
                    case null:
                        LogSkipped();
                        break;
                    case { Status: "clean" }:
                        LogClean(run.Id, run.AccountsChecked ?? 0);
                        break;
                    default:
                        // The alert: drift means the ledger disagrees with itself, which must never happen.
                        LogDrift(run.Id, run.FindingsCount ?? 0);
                        break;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogFailed(ex);
            }
        }
    }

    private TimeSpan UntilNext(TimeOnly dailyAt)
    {
        var now = clock.GetUtcNow();
        var next = new DateTimeOffset(DateOnly.FromDateTime(now.UtcDateTime).ToDateTime(dailyAt), TimeSpan.Zero);
        return (next > now ? next : next.AddDays(1)) - now;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Reconciliation skipped: another run is in progress")]
    private partial void LogSkipped();

    [LoggerMessage(Level = LogLevel.Information, Message = "Reconciliation run {RunId} clean: {Accounts} accounts checked")]
    private partial void LogClean(Guid runId, int accounts);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Reconciliation run {RunId} found drift: {Findings} findings")]
    private partial void LogDrift(Guid runId, int findings);

    [LoggerMessage(Level = LogLevel.Error, Message = "Reconciliation run failed")]
    private partial void LogFailed(Exception exception);
}
