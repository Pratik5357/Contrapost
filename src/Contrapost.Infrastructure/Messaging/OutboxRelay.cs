using System.Diagnostics;
using System.Text.Json;
using Contrapost.Infrastructure.Observability;
using Dapper;
using Npgsql;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace Contrapost.Infrastructure.Messaging;

public sealed record RelayResult(int Claimed, int Published, int Failed);

/// <summary>
/// Publishes outbox rows to RabbitMQ (design doc §9.2). Delivery is at-least-once: a row is marked
/// published only after the broker confirms it, so a crash in between republishes it and consumers
/// deduplicate on the message id.
/// </summary>
public sealed class OutboxRelay(NpgsqlDataSource dataSource, RabbitMqConnection broker)
{
    /// <summary>
    /// Claims up to <paramref name="batchSize"/> due rows with SKIP LOCKED (so several relays never
    /// claim the same row), publishes them with confirms, and records the outcome.
    /// Throws <see cref="BrokerUnavailableException"/> without touching any row if the broker is down:
    /// an outage is not the messages' fault, so it must not push their next attempt into backoff.
    /// </summary>
    public async Task<RelayResult> RelayOnceAsync(int batchSize, CancellationToken cancellationToken)
    {
        await using var channel = await broker.CreateChannelAsync(confirms: true, cancellationToken);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var batch = (await connection.QueryAsync<OutboxRow>(new CommandDefinition(
            """
            SELECT id, aggregate_id, type, payload::text AS payload, created_at, traceparent
            FROM outbox_messages
            WHERE published_at IS NULL AND next_attempt_at <= now()
            ORDER BY created_at
            LIMIT @batchSize
            FOR UPDATE SKIP LOCKED
            """,
            new { batchSize },
            transaction,
            cancellationToken: cancellationToken))).ToList();
        if (batch.Count == 0)
        {
            return new RelayResult(0, 0, 0);
        }

        // Publish the whole batch, then wait for every confirm.
        var outcomes = await Task.WhenAll(batch.Select(row => PublishAsync(channel, row, cancellationToken)));

        // If the channel died, the outcome of every unconfirmed publish is unknown. Roll back so the
        // whole batch is retried; anything that did get through is deduplicated by consumers.
        if (!channel.IsOpen)
        {
            throw new BrokerUnavailableException("The broker connection closed during publishing.",
                outcomes.FirstOrDefault(o => o.Error is not null).Error ?? new AlreadyClosedException(channel.CloseReason!));
        }

        var published = outcomes.Where(o => o.Error is null).Select(o => o.Id).ToArray();
        var failed = outcomes.Where(o => o.Error is not null).ToList();

        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE outbox_messages SET published_at = now(), attempts = attempts + 1 WHERE id = ANY(@published)",
            new { published },
            transaction,
            cancellationToken: cancellationToken));

        // Rejected by the broker (nack, or unroutable): back off exponentially with jitter, capped at 5 min.
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE outbox_messages o
            SET attempts = o.attempts + 1,
                last_error = f.error,
                next_attempt_at = now() + make_interval(secs => least(power(2, o.attempts + 1), 300) * (0.5 + random() / 2))
            FROM unnest(@ids, @errors) AS f(id, error)
            WHERE o.id = f.id
            """,
            new { ids = failed.Select(f => f.Id).ToArray(), errors = failed.Select(f => f.Error!.Message).ToArray() },
            transaction,
            cancellationToken: cancellationToken));

        await transaction.CommitAsync(cancellationToken);
        return new RelayResult(batch.Count, published.Length, failed.Count);
    }

    private static async Task<(Guid Id, Exception? Error)> PublishAsync(IChannel channel, OutboxRow row, CancellationToken cancellationToken)
    {
        // Continue the trace of the request that wrote the row; the span's id travels in the message headers.
        using var activity = Telemetry.StartLinked($"{row.Type} publish", ActivityKind.Producer, row.Traceparent);
        activity?.SetTag("messaging.system", "rabbitmq");
        activity?.SetTag("messaging.destination.name", Topology.Exchange);
        activity?.SetTag("messaging.message.id", row.Id.ToString());

        var traceparent = activity?.Id ?? row.Traceparent;

        using var payload = JsonDocument.Parse(row.Payload);
        var envelope = new EventEnvelope(row.Id, row.Type, EventEnvelope.ApiSource, row.AggregateId, row.CreatedAt, payload.RootElement);
        var properties = new BasicProperties
        {
            MessageId = row.Id.ToString(),
            Type = row.Type,
            ContentType = "application/json",
            DeliveryMode = DeliveryModes.Persistent,
            Timestamp = new AmqpTimestamp(row.CreatedAt.ToUnixTimeSeconds()),
            Headers = traceparent is null ? null : new Dictionary<string, object?> { ["traceparent"] = traceparent },
        };

        try
        {
            // Not mandatory: event types nobody subscribes to (account.created, hold.voided, ...) are
            // dropped by the broker, which is normal pub/sub; the outbox row remains the record. Topology
            // is declared before the first publish, so subscribed types always have a queue.
            await channel.BasicPublishAsync(Topology.Exchange, row.Type, mandatory: false, properties, envelope.ToBytes(), cancellationToken);
            return (row.Id, null);
        }
        catch (Exception ex) when (ex is PublishException or AlreadyClosedException or OperationInterruptedException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            return (row.Id, ex);
        }
    }

    private sealed class OutboxRow
    {
        public Guid Id { get; init; }
        public Guid AggregateId { get; init; }
        public string Type { get; init; } = "";
        public string Payload { get; init; } = "";
        public DateTimeOffset CreatedAt { get; init; }
        public string? Traceparent { get; init; }
    }
}
