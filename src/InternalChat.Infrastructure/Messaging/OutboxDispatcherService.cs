using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace InternalChat.Infrastructure.Messaging;

/// <summary>
/// Pumps <see cref="OutboxDispatcher"/> continuously (T037 wiring).
/// </summary>
/// <remarks>
/// <para>
/// <b>Without this service running, no domain event ever reaches RabbitMQ.</b> The transactional
/// outbox writes rows inside the use case's transaction and nothing else — that is the point, and it
/// is what makes a message and the notification of it atomic. But it also means every real-time
/// delivery, notification, and search index update in the platform is waiting on this loop. The
/// dispatcher itself was built and tested in T037; it was never hosted, so events accumulated in a
/// table that nothing drained.
/// </para>
/// <para>
/// <b>Woken on commit, not polled.</b> A full batch means there is probably more, so the loop goes
/// straight round again. A partial batch means the table is drained, and the loop waits on
/// <see cref="IOutboxWakeSignal"/> — which <see cref="OutboxNotificationListener"/> raises as soon
/// as a transaction commits outbox rows (002 research R1). The wait also times out after
/// <see cref="RabbitMqOptions.IdlePollInterval"/>, a backstop that drains the table even if a
/// notification is lost.
/// </para>
/// <para>
/// This replaced a fixed one-second sleep after every partial batch. On a normally loaded platform
/// that is almost every batch, so each message waited for the rest of the current second before it
/// was even published — a mean of half a second, which employees saw as chat that lagged (002 R0).
/// </para>
/// <para>
/// A failed batch is logged and retried on the next tick rather than thrown. An unhandled exception
/// in a <see cref="BackgroundService"/> stops the host, which would take every consumer down with it
/// because RabbitMQ was briefly unreachable — turning a transient broker blip into an outage that
/// needs a restart.
/// </para>
/// </remarks>
public sealed partial class OutboxDispatcherService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IOutboxWakeSignal _signal;
    private readonly RabbitMqOptions _options;
    private readonly ILogger<OutboxDispatcherService> _logger;

    /// <summary>Creates the service.</summary>
    public OutboxDispatcherService(
        IServiceScopeFactory scopes,
        IOutboxWakeSignal signal,
        IOptions<RabbitMqOptions> options,
        ILogger<OutboxDispatcherService> logger)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(signal);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _scopes = scopes;
        _signal = signal;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Started(_logger, _options.DispatchBatchSize, _options.IdlePollInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            int dispatched;

            try
            {
                dispatched = await DispatchAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                BatchFailed(_logger, exception);

                // Backs off on failure rather than spinning. A broker that is down would otherwise
                // produce a log line per millisecond and drown everything else.
                await SafeDelayAsync(_options.IdlePollInterval, stoppingToken).ConfigureAwait(false);
                continue;
            }

            if (dispatched < _options.DispatchBatchSize)
            {
                // Not a full batch, so the table is drained for now. Wait for the next commit to
                // ring, or for the backstop poll if a ring was lost.
                try
                {
                    await _signal.WaitAsync(_options.IdlePollInterval, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    /// <summary>Runs one batch in its own scope.</summary>
    /// <remarks>
    /// A fresh scope per batch, because the dispatcher depends on a scoped <c>ChatDbContext</c> and
    /// each batch opens its own transaction. Reusing one context for the life of the process would
    /// accumulate tracked entities from every batch ever dispatched.
    /// </remarks>
    private async Task<int> DispatchAsync(CancellationToken cancellationToken)
    {
        using IServiceScope scope = _scopes.CreateScope();

        OutboxDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<OutboxDispatcher>();

        return await dispatcher.DispatchBatchAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task SafeDelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutdown during the idle wait. Nothing to report.
        }
    }

    [LoggerMessage(
        EventId = 2100,
        Level = LogLevel.Information,
        Message = "Outbox dispatcher started: batches of {BatchSize}, woken on commit, backstop poll {IdleInterval}")]
    private static partial void Started(ILogger logger, int batchSize, TimeSpan idleInterval);

    [LoggerMessage(
        EventId = 2101,
        Level = LogLevel.Error,
        Message = "An outbox batch failed to dispatch. Events remain undispatched and will be retried.")]
    private static partial void BatchFailed(ILogger logger, Exception exception);
}
