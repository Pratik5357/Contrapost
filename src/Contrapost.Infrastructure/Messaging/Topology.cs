using RabbitMQ.Client;

namespace Contrapost.Infrastructure.Messaging;

public sealed record ConsumerBinding(string Name, IReadOnlyList<string> RoutingKeys)
{
    public string DeadLetterQueue => $"{Name}.dlq";
}

/// <summary>
/// Broker topology (design doc §9). Declared idempotently by both the relay and the consumers, so
/// queues exist before the first publish and no event is dropped as unroutable.
/// </summary>
public static class Topology
{
    public const string Exchange = "ledger.events";
    public const string DeadLetterExchange = "ledger.events.dlx";

    /// <summary>After this many deliveries a message is dead-lettered instead of redelivered (poison).</summary>
    public const int DeliveryLimit = 5;

    private static readonly string[] PostedMoney = ["transfer.posted", "transfer.reversed", "hold.captured"];

    public static readonly ConsumerBinding Statements = new("statements", PostedMoney);
    public static readonly ConsumerBinding Notifier = new("notifier", PostedMoney);

    public static readonly IReadOnlyList<ConsumerBinding> Queues = [Statements, Notifier];

    public static async Task DeclareAsync(IChannel channel, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);

        await channel.ExchangeDeclareAsync(Exchange, ExchangeType.Topic, durable: true, autoDelete: false, cancellationToken: cancellationToken);
        await channel.ExchangeDeclareAsync(DeadLetterExchange, ExchangeType.Direct, durable: true, autoDelete: false, cancellationToken: cancellationToken);

        foreach (var queue in Queues)
        {
            // Quorum queues replicate, and enforce the delivery limit that turns poison messages into dead letters.
            await channel.QueueDeclareAsync(queue.Name, durable: true, exclusive: false, autoDelete: false,
                arguments: new Dictionary<string, object?>
                {
                    ["x-queue-type"] = "quorum",
                    ["x-delivery-limit"] = DeliveryLimit,
                    ["x-dead-letter-exchange"] = DeadLetterExchange,
                    ["x-dead-letter-routing-key"] = queue.DeadLetterQueue,
                },
                cancellationToken: cancellationToken);
            foreach (var key in queue.RoutingKeys)
            {
                await channel.QueueBindAsync(queue.Name, Exchange, key, cancellationToken: cancellationToken);
            }

            await channel.QueueDeclareAsync(queue.DeadLetterQueue, durable: true, exclusive: false, autoDelete: false,
                arguments: new Dictionary<string, object?> { ["x-queue-type"] = "quorum" },
                cancellationToken: cancellationToken);
            await channel.QueueBindAsync(queue.DeadLetterQueue, DeadLetterExchange, queue.DeadLetterQueue, cancellationToken: cancellationToken);
        }
    }
}
