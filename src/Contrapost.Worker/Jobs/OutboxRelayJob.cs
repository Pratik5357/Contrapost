using Contrapost.Infrastructure.Messaging;

namespace Contrapost.Worker.Jobs;

/// <summary>
/// Runs the outbox relay continuously (design doc §10): full batches are followed immediately by the
/// next one, otherwise it polls. While the broker is down, rows simply accumulate (chaos run C2).
/// </summary>
internal sealed partial class OutboxRelayJob(OutboxRelay relay, IConfiguration configuration, ILogger<OutboxRelayJob> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var batchSize = configuration.GetValue("Jobs:OutboxRelay:BatchSize", 100);
        var pollInterval = TimeSpan.FromMilliseconds(configuration.GetValue("Jobs:OutboxRelay:PollIntervalMs", 200));
        var brokerRetry = TimeSpan.FromSeconds(1);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = await relay.RelayOnceAsync(batchSize, stoppingToken);
                brokerRetry = TimeSpan.FromSeconds(1);
                if (result.Failed > 0)
                {
                    LogRejected(result.Failed);
                }

                if (result.Claimed == batchSize)
                {
                    continue; // likely a backlog: keep draining
                }

                await Task.Delay(pollInterval, stoppingToken);
            }
            catch (BrokerUnavailableException ex)
            {
                LogBrokerUnavailable(brokerRetry.TotalSeconds, ex.Message);
                await Task.Delay(brokerRetry, stoppingToken);
                brokerRetry = TimeSpan.FromSeconds(Math.Min(brokerRetry.TotalSeconds * 2, 10));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogFailed(ex);
                await Task.Delay(brokerRetry, stoppingToken);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Broker rejected {Count} outbox messages; they will be retried with backoff")]
    private partial void LogRejected(int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Broker unavailable, retrying in {Seconds}s: {Reason}")]
    private partial void LogBrokerUnavailable(double seconds, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox relay run failed")]
    private partial void LogFailed(Exception exception);
}
