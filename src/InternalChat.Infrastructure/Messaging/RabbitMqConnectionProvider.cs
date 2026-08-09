using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace InternalChat.Infrastructure.Messaging;

/// <summary>Supplies RabbitMQ connections and channels.</summary>
public interface IRabbitMqConnectionProvider : IAsyncDisposable
{
    /// <summary>Gets the shared connection, opening it on first use.</summary>
    Task<IConnection> GetConnectionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens a channel with publisher confirmations enabled, so a publish does not complete
    /// until the broker has confirmed it.
    /// </summary>
    Task<IChannel> CreateConfirmingChannelAsync(CancellationToken cancellationToken = default);

    /// <summary>Opens a plain channel for consuming.</summary>
    Task<IChannel> CreateChannelAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Shared RabbitMQ connection with lazily opened channels.
/// </summary>
/// <remarks>
/// <para>
/// One connection per process, many channels. An AMQP connection is a TCP connection and is
/// expensive; channels are cheap multiplexed sessions over it. Opening a connection per publish
/// exhausts sockets under load, which at 100 messages/second arrives quickly.
/// </para>
/// <para>
/// Channels, by contrast, are <em>not</em> thread-safe and must not be shared across concurrent
/// operations — hence a channel per dispatcher pass and per consumer rather than one cached
/// channel.
/// </para>
/// </remarks>
public sealed class RabbitMqConnectionProvider : IRabbitMqConnectionProvider
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConnectionFactory _factory;
    private IConnection? _connection;
    private bool _disposed;

    /// <summary>Creates the provider.</summary>
    public RabbitMqConnectionProvider(IOptions<RabbitMqOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _factory = new ConnectionFactory
        {
            Uri = options.Value.ToUri(),
            // The client reconnects on its own; without this a broker restart would leave the
            // dispatcher permanently broken until the process was restarted too.
            AutomaticRecoveryEnabled = true,
            TopologyRecoveryEnabled = true,
        };
    }

    /// <inheritdoc />
    public async Task<IConnection> GetConnectionAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_connection is { IsOpen: true })
        {
            return _connection;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_connection is { IsOpen: true })
            {
                return _connection;
            }

            if (_connection is not null)
            {
                await _connection.DisposeAsync().ConfigureAwait(false);
            }

            _connection = await _factory.CreateConnectionAsync(cancellationToken).ConfigureAwait(false);
            return _connection;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IChannel> CreateConfirmingChannelAsync(CancellationToken cancellationToken = default)
    {
        IConnection connection = await GetConnectionAsync(cancellationToken).ConfigureAwait(false);

        // Tracking enabled so BasicPublishAsync completes only once the broker confirms. This is
        // what lets the dispatcher mark a row dispatched and mean it.
        CreateChannelOptions options = new(
            publisherConfirmationsEnabled: true,
            publisherConfirmationTrackingEnabled: true);

        return await connection.CreateChannelAsync(options, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IChannel> CreateChannelAsync(CancellationToken cancellationToken = default)
    {
        IConnection connection = await GetConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.CreateChannelAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_connection is not null)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
        }

        _gate.Dispose();
    }
}
