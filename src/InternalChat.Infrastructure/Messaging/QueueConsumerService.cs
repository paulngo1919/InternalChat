using InternalChat.Application.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace InternalChat.Infrastructure.Messaging;

/// <summary>
/// Declares the broker topology and starts every registered consumer.
/// </summary>
/// <remarks>
/// <para>
/// One hosted service for all consumers rather than one per consumer. The topology must be
/// declared exactly once before anything consumes, and several services racing to declare the same
/// exchanges and queues would work by luck — RabbitMQ tolerates repeated identical declarations and
/// rejects conflicting ones, so the failure mode is an intermittent startup error that depends on
/// which service won.
/// </para>
/// <para>
/// Consumers are resolved once here to learn their queue names. The instance that actually handles
/// each message is resolved from a per-message scope by <see cref="ConsumerHost"/>, so a consumer
/// may depend on scoped services and still share the transaction its deduplication record commits
/// in.
/// </para>
/// </remarks>
public sealed partial class QueueConsumerService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IRabbitMqConnectionProvider _connectionProvider;
    private readonly ConsumerHost _consumerHost;
    private readonly ILogger<QueueConsumerService> _logger;

    /// <summary>Creates the service.</summary>
    public QueueConsumerService(
        IServiceScopeFactory scopeFactory,
        IRabbitMqConnectionProvider connectionProvider,
        ConsumerHost consumerHost,
        ILogger<QueueConsumerService> logger)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(connectionProvider);
        ArgumentNullException.ThrowIfNull(consumerHost);
        ArgumentNullException.ThrowIfNull(logger);

        _scopeFactory = scopeFactory;
        _connectionProvider = connectionProvider;
        _consumerHost = consumerHost;
        _logger = logger;
    }

    /// <summary>Backoff between attempts to reach the broker. Capped, so it keeps trying.</summary>
    private static readonly TimeSpan[] AttachRetryDelays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(30),
    ];

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <b>An unreachable broker must not take the host down.</b> The default
    /// <c>BackgroundServiceExceptionBehavior</c> is <c>StopHost</c>, so letting the
    /// <c>BrokerUnreachableException</c> escape here stopped the whole API — and with it every
    /// SignalR connection — the moment RabbitMQ was briefly unavailable. Sending a message, reading
    /// history, and holding a hub connection do not depend on the broker being up at that instant;
    /// only the side effects behind the outbox do, and those are durable by design and dispatched
    /// when it returns.
    /// </para>
    /// <para>
    /// The constitution's degradation requirement says this in general terms for the media host, and
    /// the reasoning transfers exactly: losing a component must degrade what depends on it, not the
    /// service. So this retries indefinitely with a capped backoff and stays alive.
    /// </para>
    /// </remarks>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        int attempt = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await AttachAsync(stoppingToken).ConfigureAwait(false);
                break;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // Any broker failure is retried; see the remarks above.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                TimeSpan delay = AttachRetryDelays[Math.Min(attempt, AttachRetryDelays.Length - 1)];
                attempt++;

                AttachFailed(_logger, delay.TotalSeconds, ex);

                await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
            }
        }

        // The consumers run on broker callbacks, not on this task. Waiting here keeps the hosted
        // service alive until shutdown; returning would let the host consider it finished.
        await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false);
    }

    /// <summary>Declares the topology and starts every registered consumer.</summary>
    private async Task AttachAsync(CancellationToken stoppingToken)
    {
        await using (RabbitMQ.Client.IChannel channel =
            await _connectionProvider.CreateChannelAsync(stoppingToken).ConfigureAwait(false))
        {
            await ChatTopology.DeclareAsync(channel, cancellationToken: stoppingToken).ConfigureAwait(false);
        }

        using IServiceScope scope = _scopeFactory.CreateScope();

        IMessageConsumer[] consumers = [.. scope.ServiceProvider.GetServices<IMessageConsumer>()];

        foreach (IMessageConsumer consumer in consumers)
        {
            await _consumerHost.StartAsync(consumer, stoppingToken).ConfigureAwait(false);
            ConsumerStarted(_logger, consumer.QueueName);
        }

        if (consumers.Length == 0)
        {
            NoConsumersRegistered(_logger);
        }
    }

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await _consumerHost.DisposeAsync().ConfigureAwait(false);
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    [LoggerMessage(EventId = 3400, Level = LogLevel.Information, Message = "Consuming {Queue}")]
    private static partial void ConsumerStarted(ILogger logger, string queue);

    [LoggerMessage(
        EventId = 3402,
        Level = LogLevel.Warning,
        Message = "Could not attach to the broker; retrying in {DelaySeconds}s. Queued side effects "
            + "are held in the outbox and dispatched once it returns — nothing is lost, and the "
            + "request path is unaffected.")]
    private static partial void AttachFailed(ILogger logger, double delaySeconds, Exception exception);

    [LoggerMessage(
        EventId = 3401,
        Level = LogLevel.Warning,
        Message = "No IMessageConsumer is registered. The broker topology was declared and nothing "
            + "will be consumed, so queues will grow silently.")]
    private static partial void NoConsumersRegistered(ILogger logger);
}
