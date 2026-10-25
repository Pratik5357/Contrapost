using Contrapost.Infrastructure.Messaging;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Contrapost.Consumers.Handlers;

/// <summary>
/// Raises a low-balance alert when a posting takes an account below the threshold (design doc §9.3).
/// The alert is a side effect that must happen once per event; the inbox guarantees that.
/// </summary>
public sealed partial class LowBalanceNotifier(IConfiguration configuration, ILogger<LowBalanceNotifier> logger) : IEventConsumer
{
    private readonly long _threshold = configuration.GetValue("Consumers:LowBalanceThreshold", 1_000L);

    public ConsumerBinding Binding => Topology.Notifier;

    public async Task HandleAsync(EventEnvelope envelope, NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        var posted = PostedEvent.From(envelope);

        foreach (var leg in posted.Legs.Where(l => l.Decreases && l.BalanceAfter < _threshold))
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO low_balance_alerts (account_id, transaction_id, balance_after, threshold)
                VALUES (@AccountId, @transactionId, @BalanceAfter, @threshold)
                """,
                new { leg.AccountId, transactionId = posted.TransactionId, leg.BalanceAfter, threshold = _threshold },
                transaction,
                cancellationToken: cancellationToken));
            LogLowBalance(leg.AccountId, leg.BalanceAfter, _threshold);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Low balance on account {AccountId}: {Balance} is below {Threshold}")]
    private partial void LogLowBalance(Guid accountId, long balance, long threshold);
}
