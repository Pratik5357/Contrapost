using Contrapost.Infrastructure.Messaging;
using Dapper;
using Npgsql;

namespace Contrapost.Consumers;

public interface IEventConsumer
{
    /// <summary>The queue this handler consumes, and the consumer name its inbox rows are recorded under.</summary>
    ConsumerBinding Binding { get; }

    /// <summary>Applies the event's effect inside <paramref name="transaction"/>, which also records it in the inbox.</summary>
    Task HandleAsync(EventEnvelope envelope, NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken);
}

/// <summary>
/// Consumer-side deduplication (design doc §9.3). The inbox row and the handler's effect commit in one
/// transaction, so a redelivered message is recognised and skipped, and a crash before commit leaves
/// neither behind. Concurrent duplicates serialise on the inbox primary key.
/// </summary>
public sealed class InboxProcessor(NpgsqlDataSource dataSource)
{
    /// <returns>True if the event was handled now; false if this consumer had already handled it.</returns>
    public async Task<bool> ProcessAsync(IEventConsumer handler, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(envelope);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var fresh = await connection.ExecuteScalarAsync<int?>(new CommandDefinition(
            """
            INSERT INTO inbox_messages (consumer, message_id) VALUES (@consumer, @messageId)
            ON CONFLICT DO NOTHING
            RETURNING 1
            """,
            new { consumer = handler.Binding.Name, messageId = envelope.Id },
            transaction,
            cancellationToken: cancellationToken));
        if (fresh is null)
        {
            return false;
        }

        await handler.HandleAsync(envelope, connection, transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }
}
