using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace Contrapost.Infrastructure.Messaging;

/// <summary>The broker could not be reached; nothing was published or consumed.</summary>
public sealed class BrokerUnavailableException(string message, Exception inner) : Exception(message, inner);

/// <summary>
/// One long-lived connection per process, created on first use. The client's automatic recovery
/// reconnects it (and re-registers consumers) after a broker restart.
/// </summary>
public sealed class RabbitMqConnection(string uri, string clientName) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IConnection? _connection;
    private bool _topologyDeclared;

    public async Task<IConnection> GetAsync(CancellationToken cancellationToken)
    {
        if (_connection is { IsOpen: true } open)
        {
            return open;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_connection is { IsOpen: true } raced)
            {
                return raced;
            }

            if (_connection is not null)
            {
                await _connection.DisposeAsync();
                _connection = null;
            }

            var factory = new ConnectionFactory
            {
                Uri = new Uri(uri),
                ClientProvidedName = clientName,
                AutomaticRecoveryEnabled = true,
                TopologyRecoveryEnabled = true,
            };
            try
            {
                _connection = await factory.CreateConnectionAsync(cancellationToken);
            }
            catch (BrokerUnreachableException ex)
            {
                throw new BrokerUnavailableException($"RabbitMQ is unreachable at {factory.Endpoint}.", ex);
            }

            _topologyDeclared = false;
            return _connection;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>A new channel, with publisher confirms awaited per publish when <paramref name="confirms"/> is set.</summary>
    public async Task<IChannel> CreateChannelAsync(bool confirms, CancellationToken cancellationToken)
    {
        var connection = await GetAsync(cancellationToken);
        var channel = await connection.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: confirms, publisherConfirmationTrackingEnabled: confirms),
            cancellationToken);

        if (!_topologyDeclared)
        {
            await Topology.DeclareAsync(channel, cancellationToken);
            _topologyDeclared = true;
        }

        return channel;
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }

        _gate.Dispose();
    }
}
