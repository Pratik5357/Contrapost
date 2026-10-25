using System.Diagnostics.Metrics;
using Contrapost.Application.Observability;
using Dapper;
using Npgsql;

namespace Contrapost.Worker.Jobs;

/// <summary>
/// Gauges for the state the Worker looks after (design doc §12): outbox backlog and lag, and the last
/// reconciliation's findings. Observable gauges are read on every export, so the values are refreshed
/// by <see cref="OutboxMonitorJob"/> rather than queried in the callback.
/// </summary>
internal sealed class LedgerHealthMetrics
{
    private long _unpublished;
    private double _lagSeconds;
    private long _reconciliationFindings = -1; // no run yet

    public LedgerHealthMetrics(IMeterFactory meters)
    {
        var meter = meters.Create(LedgerMetrics.MeterName);
        meter.CreateObservableGauge("ledger.outbox.unpublished", () => Interlocked.Read(ref _unpublished), "{message}",
            "Outbox messages not yet published.");
        meter.CreateObservableGauge("ledger.outbox.lag", () => Volatile.Read(ref _lagSeconds), "s",
            "Age of the oldest unpublished outbox message.");
        meter.CreateObservableGauge("ledger.reconciliation.findings", () => Interlocked.Read(ref _reconciliationFindings), "{finding}",
            "Findings of the most recent reconciliation run; -1 before the first run.");
    }

    public void SetOutbox(long unpublished, double lagSeconds)
    {
        Interlocked.Exchange(ref _unpublished, unpublished);
        Volatile.Write(ref _lagSeconds, lagSeconds);
    }

    public void SetReconciliationFindings(long findings) => Interlocked.Exchange(ref _reconciliationFindings, findings);
}

/// <summary>Samples the outbox backlog every few seconds for <see cref="LedgerHealthMetrics"/>.</summary>
internal sealed partial class OutboxMonitorJob(NpgsqlDataSource dataSource, LedgerHealthMetrics metrics, ILogger<OutboxMonitorJob> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        do
        {
            try
            {
                await using var connection = await dataSource.OpenConnectionAsync(stoppingToken);
                var (unpublished, lag) = await connection.QuerySingleAsync<(long, double)>(new CommandDefinition(
                    """
                    SELECT count(*), coalesce(extract(epoch FROM now() - min(created_at)), 0)::float8
                    FROM outbox_messages WHERE published_at IS NULL
                    """,
                    cancellationToken: stoppingToken));
                metrics.SetOutbox(unpublished, lag);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogFailed(ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not sample the outbox backlog")]
    private partial void LogFailed(Exception exception);
}
