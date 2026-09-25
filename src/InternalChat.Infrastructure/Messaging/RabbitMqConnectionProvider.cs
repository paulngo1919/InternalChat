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

    /// <summary>
    /// Opens a consuming channel whose handlers may run up to <paramref name="dispatchConcurrency"/>
    /// at a time (002 research R3).
    /// </summary>
    Task<IChannel> CreateChannelAsync(ushort dispatchConcurrency, CancellationToken cancellationToken = default);

    /// <summary>
    /// The process's long-lived publishing channel: confirmations tracked, and many publishes allowed
    /// in flight at once (002 research R2). Reopened transparently if the broker closed it.
    /// </summary>
    Task<IChannel> GetPublishingChannelAsync(CancellationToken cancellationToken = default);
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
/// Channels are cheap but not free: opening one is a broker round trip plus confirm-select. Consumers
/// get a channel each. Publishing shares one long-lived channel (002 research R2), created with
/// publisher-confirmation tracking and an outstanding-confirm limit — the configuration under which
/// RabbitMQ.Client 7 supports concurrent publishes on one channel, and the one that lets a batch be
/// pipelined instead of waiting out a confirm per row.
/// </para>
/// </remarks>
public sealed class RabbitMqConnectionProvider : IRabbitMqConnectionProvider
{
    /// <summary>Publishes allowed in flight on the publishing channel before a publish waits.</summary>
    private const int MaxOutstandingConfirms = 256;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _publishingGate = new(1, 1);
    private readonly ConnectionFactory _factory;
    private IConnection? _connection;
    private IChannel? _publishing;
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
    public async Task<IChannel> CreateChannelAsync(ushort dispatchConcurrency, CancellationToken cancellationToken = default)
    {
        IConnection connection = await GetConnectionAsync(cancellationToken).ConfigureAwait(false);

        CreateChannelOptions options = new(
            publisherConfirmationsEnabled: false,
            publisherConfirmationTrackingEnabled: false,
            consumerDispatchConcurrency: Math.Max((ushort)1, dispatchConcurrency));

        return await connection.CreateChannelAsync(options, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IChannel> GetPublishingChannelAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_publishing is { IsOpen: true })
        {
            return _publishing;
        }

        await _publishingGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_publishing is { IsOpen: true })
            {
                return _publishing;
            }

            if (_publishing is not null)
            {
                await _publishing.DisposeAsync().ConfigureAwait(false);
            }

            IConnection connection = await GetConnectionAsync(cancellationToken).ConfigureAwait(false);

            // Tracking on, so each publish still completes only when the broker confirms that
            // message — the dispatcher's "never marked before confirmed" rule is per row, not per
            // batch. The limiter bounds how many are outstanding at once.
            CreateChannelOptions options = new(
                publisherConfirmationsEnabled: true,
                publisherConfirmationTrackingEnabled: true,
                outstandingPublisherConfirmationsRateLimiter: new ThrottlingRateLimiter(MaxOutstandingConfirms));

            _publishing = await connection.CreateChannelAsync(options, cancellationToken).ConfigureAwait(false);
            return _publishing;
        }
        finally
        {
            _publishingGate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_publishing is not null)
        {
            await _publishing.DisposeAsync().ConfigureAwait(false);
        }

        if (_connection is not null)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
        }

        _gate.Dispose();
        _publishingGate.Dispose();
    }
}
