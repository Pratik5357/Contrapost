using System.Text.Json;
using Contrapost.Infrastructure.Messaging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Contrapost.Consumers;

/// <summary>
/// Feeds one queue into one handler through the inbox. A message is acked only after its effect has
/// committed. A failing message is requeued; after <see cref="Topology.DeliveryLimit"/> deliveries the
/// quorum queue dead-letters it. A malformed message goes straight to the dead-letter queue.
/// </summary>
public sealed partial class QueueConsumer<THandler>(
    RabbitMqConnection broker, InboxProcessor inbox, THandler handler, IConfiguration configuration, ILogger<QueueConsumer<THandler>> logger)
    : BackgroundService
    where THandler : IEventConsumer
{
    private IChannel? _channel;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var retry = TimeSpan.FromSeconds(1);
        while (_channel is null)
        {
            try
            {
                _channel = await broker.CreateChannelAsync(confirms: false, stoppingToken);
            }
            catch (BrokerUnavailableException ex)
            {
                LogBrokerUnavailable(handler.Binding.Name, retry.TotalSeconds, ex.Message);
                await Task.Delay(retry, stoppingToken);
                retry = TimeSpan.FromSeconds(Math.Min(retry.TotalSeconds * 2, 10));
            }
        }

        await _channel.BasicQosAsync(0, configuration.GetValue<ushort>("Consumers:Prefetch", 20), global: false, stoppingToken);
        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.ReceivedAsync += (_, delivery) => OnDeliveryAsync(_channel, delivery, stoppingToken);
        await _channel.BasicConsumeAsync(handler.Binding.Name, autoAck: false, consumer, stoppingToken);

        // The client's automatic recovery re-registers this consumer after a broker restart.
        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    private async Task OnDeliveryAsync(IChannel channel, BasicDeliverEventArgs delivery, CancellationToken cancellationToken)
    {
        EventEnvelope envelope;
        try
        {
            envelope = EventEnvelope.Parse(delivery.Body.Span);
        }
        catch (JsonException ex)
        {
            LogMalformed(handler.Binding.Name, delivery.BasicProperties.MessageId, ex.Message);
            await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: false, cancellationToken);
            return;
        }

        try
        {
            if (!await inbox.ProcessAsync(handler, envelope, cancellationToken))
            {
                LogDuplicate(handler.Binding.Name, envelope.Id);
            }

            await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogFailed(ex, handler.Binding.Name, envelope.Id, envelope.Type);
            await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: true, cancellationToken);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        if (_channel is not null)
        {
            await _channel.DisposeAsync();
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Queue}: broker unavailable, retrying in {Seconds}s: {Reason}")]
    private partial void LogBrokerUnavailable(string queue, double seconds, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "{Queue}: malformed message {MessageId} sent to the dead-letter queue: {Reason}")]
    private partial void LogMalformed(string queue, string? messageId, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Queue}: skipped duplicate delivery of {EventId}")]
    private partial void LogDuplicate(string queue, Guid eventId);

    [LoggerMessage(Level = LogLevel.Error, Message = "{Queue}: handling {EventId} ({Type}) failed; requeued")]
    private partial void LogFailed(Exception exception, string queue, Guid eventId, string type);
}
