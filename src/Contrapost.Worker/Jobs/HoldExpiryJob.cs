using Contrapost.Application.Holds;

namespace Contrapost.Worker.Jobs;

/// <summary>
/// Expires overdue holds (design doc §10). Safe to run on several workers at once: each batch takes
/// its holds with SKIP LOCKED, and expiry decisions use the database clock, not this machine's.
/// </summary>
internal sealed partial class HoldExpiryJob(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<HoldExpiryJob> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(configuration.GetValue("Jobs:HoldExpiry:IntervalSeconds", 30));
        var batchSize = configuration.GetValue("Jobs:HoldExpiry:BatchSize", 100);
        using var timer = new PeriodicTimer(interval);

        do
        {
            try
            {
                var expired = await RunOnceAsync(batchSize, stoppingToken);
                if (expired > 0)
                {
                    LogExpired(expired);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Keep the loop alive; the next tick retries. Holds stay pending meanwhile and
                // captures still refuse them, because capture checks expires_at itself.
                LogFailed(ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>Expires batches until a short one shows the backlog is cleared.</summary>
    public async Task<int> RunOnceAsync(int batchSize, CancellationToken cancellationToken)
    {
        var total = 0;
        int expired;
        do
        {
            await using var scope = scopes.CreateAsyncScope();
            expired = await scope.ServiceProvider.GetRequiredService<IHoldStore>().ExpireDueAsync(batchSize, cancellationToken);
            total += expired;
        }
        while (expired == batchSize && !cancellationToken.IsCancellationRequested);

        return total;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Expired {Count} holds")]
    private partial void LogExpired(int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Hold expiry run failed")]
    private partial void LogFailed(Exception exception);
}
