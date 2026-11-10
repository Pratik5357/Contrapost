using Contrapost.Application.Retention;

namespace Contrapost.Worker.Jobs;

/// <summary>
/// The cleanup jobs (design doc §10): idempotency keys older than 24 h, hourly; published outbox messages
/// older than 7 days, daily. Each runs once at startup, then on its interval, deleting in batches until a
/// short batch shows nothing is left.
/// </summary>
internal sealed partial class RetentionJob(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<RetentionJob> logger)
    : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var batchSize = configuration.GetValue("Jobs:Retention:BatchSize", 1000);
        var keyRetention = TimeSpan.FromHours(configuration.GetValue("Jobs:IdempotencyCleanup:RetentionHours", 24));
        var outboxRetention = TimeSpan.FromDays(configuration.GetValue("Jobs:OutboxCleanup:RetentionDays", 7));

        return Task.WhenAll(
            RunEveryAsync("idempotency keys", TimeSpan.FromMinutes(configuration.GetValue("Jobs:IdempotencyCleanup:IntervalMinutes", 60)),
                (store, ct) => store.DeleteExpiredIdempotencyKeysAsync(keyRetention, batchSize, ct), batchSize, stoppingToken),
            RunEveryAsync("published outbox messages", TimeSpan.FromMinutes(configuration.GetValue("Jobs:OutboxCleanup:IntervalMinutes", 24 * 60)),
                (store, ct) => store.DeletePublishedOutboxMessagesAsync(outboxRetention, batchSize, ct), batchSize, stoppingToken));
    }

    private async Task RunEveryAsync(
        string what, TimeSpan interval, Func<IRetentionStore, CancellationToken, Task<int>> deleteBatch, int batchSize, CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(interval);
        do
        {
            try
            {
                var total = 0;
                int deleted;
                do
                {
                    await using var scope = scopes.CreateAsyncScope();
                    deleted = await deleteBatch(scope.ServiceProvider.GetRequiredService<IRetentionStore>(), stoppingToken);
                    total += deleted;
                }
                while (deleted == batchSize && !stoppingToken.IsCancellationRequested);

                if (total > 0)
                {
                    LogDeleted(total, what);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Keep the loop alive; the rows are still there for the next run.
                LogFailed(ex, what);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleted {Count} expired {What}")]
    private partial void LogDeleted(int count, string what);

    [LoggerMessage(Level = LogLevel.Error, Message = "Cleanup of {What} failed")]
    private partial void LogFailed(Exception exception, string what);
}
