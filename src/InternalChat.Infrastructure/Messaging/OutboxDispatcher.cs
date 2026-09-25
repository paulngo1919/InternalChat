using System.Diagnostics;
using System.Text;
using InternalChat.Application.Abstractions;
using InternalChat.Application.Telemetry;
using InternalChat.Infrastructure.Persistence;
using InternalChat.Infrastructure.Persistence.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace InternalChat.Infrastructure.Messaging;

/// <summary>
/// Publishes outbox rows to RabbitMQ and marks them dispatched.
/// </summary>
/// <remarks>
/// <para>
/// The second half of the transactional outbox (Constitution Principle VI, research.md D2). The
/// use case commits the state change and the outbox row together; this drains the outbox.
/// </para>
/// <para>
/// Two properties make it safe:
/// </para>
/// <list type="bullet">
///   <item><description>
///     Rows are claimed with <c>FOR UPDATE SKIP LOCKED</c>, so several dispatcher instances can
///     run without coordination and without publishing the same row twice. Without SKIP LOCKED
///     they would serialise behind one another; without FOR UPDATE they would double-publish.
///   </description></item>
///   <item><description>
///     A row is marked dispatched only after the broker <em>confirms</em> the publish. Marking on
///     send would reintroduce the loss the outbox exists to prevent, one layer lower down.
///   </description></item>
/// </list>
/// <para>
/// Delivery is therefore at-least-once, never at-most-once: a crash between confirm and commit
/// republishes on the next pass. That is the correct trade, and it is why every consumer
/// deduplicates on the message id.
/// </para>
/// </remarks>
public sealed partial class OutboxDispatcher
{
    private readonly ChatDbContext _context;
    private readonly IRabbitMqConnectionProvider _connectionProvider;
    private readonly RabbitMqOptions _options;
    private readonly ILogger<OutboxDispatcher> _logger;
    private readonly IDeliveryMetrics? _metrics;
    private readonly TimeProvider _time;

    /// <summary>Creates the dispatcher.</summary>
    /// <param name="context">The outbox's database context.</param>
    /// <param name="connectionProvider">The broker connection.</param>
    /// <param name="options">Batch size and attempt limits.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="metrics">
    /// Records commit-to-confirm lag per row (002 FR-011). Optional so tests that only care about
    /// publishing need not supply one; the hosts always register it.
    /// </param>
    /// <param name="time">Clock for <c>dispatched_at</c> and the lag. Defaults to the system clock.</param>
    public OutboxDispatcher(
        ChatDbContext context,
        IRabbitMqConnectionProvider connectionProvider,
        IOptions<RabbitMqOptions> options,
        ILogger<OutboxDispatcher> logger,
        IDeliveryMetrics? metrics = null,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(connectionProvider);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _context = context;
        _connectionProvider = connectionProvider;
        _options = options.Value;
        _logger = logger;
        _metrics = metrics;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// Claims and publishes one batch.
    /// </summary>
    /// <returns>How many rows were confirmed published.</returns>
    public async Task<int> DispatchBatchAsync(CancellationToken cancellationToken = default)
    {
        await using var transaction = await _context.Database
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // SKIP LOCKED is what makes concurrent dispatchers safe. Rows already claimed by another
        // instance are stepped over rather than waited on.
        List<OutboxMessage> batch = await _context.OutboxMessages
            .FromSql(
                $"""
                SELECT * FROM outbox_message
                WHERE dispatched_at IS NULL
                  AND attempts < {_options.MaxDispatchAttempts}
                ORDER BY occurred_at
                LIMIT {_options.DispatchBatchSize}
                FOR UPDATE SKIP LOCKED
                """)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        if (batch.Count == 0)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return 0;
        }

        IChannel channel = await _connectionProvider
            .GetPublishingChannelAsync(cancellationToken).ConfigureAwait(false);

        // Pipelined (002 research R2): every publish in the batch is started before any confirm is
        // awaited, so the broker confirms them together instead of one round trip per row. Each
        // task still completes only when the broker has confirmed that row, and a row is marked
        // dispatched only by its own task — so a batch that half-succeeds records exactly which
        // half, as the serial version did.
        List<(OutboxMessage Row, Task Publish)> inFlight = new(batch.Count);

        foreach (OutboxMessage row in batch)
        {
            cancellationToken.ThrowIfCancellationRequested();
            inFlight.Add((row, PublishAsync(channel, row, cancellationToken)));
        }

        int confirmed = 0;

        foreach ((OutboxMessage row, Task publish) in inFlight)
        {
            try
            {
                await publish.ConfigureAwait(false);

                DateTimeOffset now = _time.GetUtcNow();
                row.DispatchedAt = now;
                row.LastError = null;
                confirmed++;

                _metrics?.RecordOutboxLag(row.Type, now - row.OccurredAt);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The row stays undispatched and is retried next pass. Recording the reason
                // matters because a row stuck at max attempts is otherwise a silent hole:
                // the state change happened and nobody was ever told.
                row.Attempts++;
                row.LastError = Truncate(ex.Message, 2000);
                DispatchFailed(_logger, row.Id, row.Type, row.Attempts, ex);
            }
        }

        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        if (confirmed > 0)
        {
            BatchDispatched(_logger, confirmed, batch.Count);
        }

        return confirmed;
    }

    /// <summary>Counts rows that have exhausted their attempts and need investigation.</summary>
    public Task<int> CountStuckAsync(CancellationToken cancellationToken = default) =>
        _context.OutboxMessages
            .Where(m => m.DispatchedAt == null && m.Attempts >= _options.MaxDispatchAttempts)
            .CountAsync(cancellationToken);

    private static async Task PublishAsync(IChannel channel, OutboxMessage row, CancellationToken cancellationToken)
    {
        // Re-parent onto the request that wrote the row, which may have finished minutes ago.
        // Without this the dispatch shows up as an orphan span and the question the trace exists
        // to answer — "the send returned 200, was the notification ever published?" — needs two
        // traces and a correlation by hand.
        ActivityContext parent = default;
        if (!string.IsNullOrEmpty(row.TraceParent)
            && ActivityContext.TryParse(row.TraceParent, traceState: null, out ActivityContext parsed))
        {
            parent = parsed;
        }

        using Activity? activity = ChatTelemetry.ActivitySource.StartActivity(
            ChatTelemetry.Spans.OutboxPublish,
            ActivityKind.Producer,
            parent);

        if (activity is not null)
        {
            activity.SetTag(ChatTelemetry.Attributes.MessagingSystem, "rabbitmq");
            activity.SetTag(ChatTelemetry.Attributes.MessageId, row.Id);
            activity.SetTag(ChatTelemetry.Attributes.MessageType, row.Type);
            activity.SetTag(ChatTelemetry.Attributes.Destination, row.RoutingKey);
        }

        BasicProperties properties = new()
        {
            MessageId = row.Id.ToString(),
            Type = row.Type,
            ContentType = "application/json",
            // Persistent, so an in-flight message survives a broker restart. A transient message
            // would make the outbox pointless: durable up to the broker, then lost inside it.
            DeliveryMode = DeliveryModes.Persistent,
            Timestamp = new AmqpTimestamp(row.OccurredAt.ToUnixTimeSeconds()),
            Headers = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [ChatTopology.AttemptHeader] = 1,
            },
        };

        // Prefer this span's id so the consumer becomes its child rather than a second child of
        // the original request — the publish and the handling are sequential, and a trace that
        // shows them as siblings hides which one was slow. Falls back to the stored value when no
        // listener is attached and StartActivity returned null.
        string? traceParent = activity?.Id ?? row.TraceParent;
        if (!string.IsNullOrEmpty(traceParent))
        {
            properties.Headers!["traceparent"] = traceParent;
        }

        await channel.BasicPublishAsync(
            exchange: ChatTopology.EventsExchange,
            routingKey: row.RoutingKey,
            mandatory: false,
            basicProperties: properties,
            body: Encoding.UTF8.GetBytes(row.Payload),
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];

    [LoggerMessage(EventId = 2000, Level = LogLevel.Information, Message = "Outbox dispatched {Confirmed} of {Claimed} claimed rows")]
    private static partial void BatchDispatched(ILogger logger, int confirmed, int claimed);

    [LoggerMessage(EventId = 2001, Level = LogLevel.Warning, Message = "Outbox row {OutboxId} of type {EventType} failed to publish (attempt {Attempts})")]
    private static partial void DispatchFailed(ILogger logger, Guid outboxId, string eventType, int attempts, Exception exception);
}
