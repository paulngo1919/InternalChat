using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace InternalChat.Infrastructure.Messaging;

/// <summary>Connection settings for <see cref="OutboxNotificationListener"/>.</summary>
public sealed class OutboxListenerOptions
{
    /// <summary>The PostgreSQL connection string — the same database the outbox lives in.</summary>
    public string ConnectionString { get; set; } = string.Empty;
}

/// <summary>
/// Wakes the outbox dispatcher the moment outbox rows commit (002 T018, research R1).
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the fix for the reported delay.</b> Before it, the dispatcher learned about a new
/// message only by polling, once a second — so a message typed on an idle platform waited up to a
/// second, half a second on average, before it was even published. The
/// <c>outbox_message_notify</c> trigger now raises <c>outbox_ready</c> on every commit that wrote
/// outbox rows, and this service turns that into <see cref="IOutboxWakeSignal.Signal"/>.
/// </para>
/// <para>
/// <b>Commit-bound, so the outbox guarantee is untouched.</b> PostgreSQL delivers a notification
/// only when the raising transaction commits. The dispatcher cannot be woken for a row it cannot yet
/// see, and a rolled-back send wakes nothing.
/// </para>
/// <para>
/// <b>Losing this connection is slow, never lossy.</b> The dispatcher keeps its backstop poll, so
/// rows are still drained within <see cref="RabbitMqOptions.IdlePollInterval"/> while this
/// reconnects. The connection is dedicated and unpooled: a pooled connection returned to the pool
/// would silently stop listening, and one held out of the pool forever would shrink it.
/// </para>
/// </remarks>
public sealed partial class OutboxNotificationListener : BackgroundService
{
    /// <summary>The notification channel the trigger raises.</summary>
    public const string Channel = "outbox_ready";

    private static readonly TimeSpan InitialReconnectDelay = TimeSpan.FromMilliseconds(100);

    private readonly IOutboxWakeSignal _signal;
    private readonly OutboxListenerOptions _listener;
    private readonly RabbitMqOptions _options;
    private readonly ILogger<OutboxNotificationListener> _logger;

    /// <summary>Creates the listener.</summary>
    public OutboxNotificationListener(
        IOutboxWakeSignal signal,
        IOptions<OutboxListenerOptions> listener,
        IOptions<RabbitMqOptions> options,
        ILogger<OutboxNotificationListener> logger)
    {
        ArgumentNullException.ThrowIfNull(signal);
        ArgumentNullException.ThrowIfNull(listener);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _signal = signal;
        _listener = listener.Value;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(_listener.ConnectionString))
        {
            throw new InvalidOperationException(
                "OutboxListenerOptions.ConnectionString is not configured. Without it the outbox is "
                + "drained only by the backstop poll, and every message waits for it.");
        }

        TimeSpan delay = InitialReconnectDelay;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // The backoff resets once a connection is established, so a listener that ran for a
                // week and then blipped reconnects in 100 ms rather than at the ceiling.
                await ListenAsync(() => delay = InitialReconnectDelay, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                ListenerLost(_logger, delay, exception);

                // Anything committed while the connection was dying was announced to nobody. One
                // wake makes the dispatcher look now instead of at its next backstop poll.
                _signal.Signal();

                try
                {
                    await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, _options.ListenerMaxReconnectDelay.Ticks));
                continue;
            }
        }
    }

    private async Task ListenAsync(Action connected, CancellationToken stoppingToken)
    {
        NpgsqlConnectionStringBuilder builder = new(_listener.ConnectionString)
        {
            Pooling = false,

            // Detects a half-open connection — a network partition the server never reports —
            // which would otherwise leave this waiting forever on a socket nothing writes to.
            KeepAlive = 15,
            ApplicationName = "internalchat-outbox-listener",
        };

        await using NpgsqlConnection connection = new(builder.ConnectionString);
        connection.Notification += (_, _) => _signal.Signal();

        await connection.OpenAsync(stoppingToken).ConfigureAwait(false);

        await using (NpgsqlCommand listen = new($"LISTEN {Channel}", connection))
        {
            await listen.ExecuteNonQueryAsync(stoppingToken).ConfigureAwait(false);
        }

        Listening(_logger);
        connected();

        // Rows committed before LISTEN took effect were announced to nobody. Close that gap.
        _signal.Signal();

        while (!stoppingToken.IsCancellationRequested)
        {
            // Returns after each notification (the handler above has already signalled); throws
            // when the connection breaks, which is the reconnect path.
            await connection.WaitAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    [LoggerMessage(
        EventId = 2110,
        Level = LogLevel.Information,
        Message = "Outbox listener connected; the dispatcher is woken on commit")]
    private static partial void Listening(ILogger logger);

    [LoggerMessage(
        EventId = 2111,
        Level = LogLevel.Warning,
        Message = "Outbox listener lost its connection; reconnecting in {Delay}. Delivery falls back to the backstop poll meanwhile.")]
    private static partial void ListenerLost(ILogger logger, TimeSpan delay, Exception exception);
}
