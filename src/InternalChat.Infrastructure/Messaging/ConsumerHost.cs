using System.Diagnostics;
using System.Text;
using InternalChat.Application.Telemetry;
using InternalChat.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace InternalChat.Infrastructure.Messaging;

/// <summary>
/// Runs a consumer with manual acknowledgement, idempotency, bounded retry, and dead-lettering.
/// </summary>
/// <remarks>
/// <para>
/// Constitution Principle VI: "Every consumer MUST be idempotent. Delivery is at-least-once;
/// consumers MUST deduplicate on the message's idempotency key and MUST produce the same outcome
/// when redelivered." That is implemented once, here, rather than in every handler.
/// </para>
/// <para>
/// <b>The load-bearing detail</b> is that the deduplication record is inserted in the <em>same
/// transaction</em> as the handler's work. If they were separate, a crash between them would
/// either mark a message processed that was not, or reprocess one that was — which for
/// notification fan-out means an employee notified twice for one message.
/// </para>
/// <para>
/// Failure never nacks with <c>requeue: true</c>. That redelivers immediately and without limit,
/// so one poison message spins the consumer at full speed forever. Instead the message is
/// republished to a TTL retry queue and the original acknowledged; once retries are exhausted it
/// goes to the queue's dead-letter queue where it can be seen and alerted on.
/// </para>
/// </remarks>
public sealed partial class ConsumerHost : IAsyncDisposable
{
    private readonly IRabbitMqConnectionProvider _connectionProvider;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RabbitMqOptions _options;
    private readonly ILogger<ConsumerHost> _logger;
    private readonly List<IChannel> _channels = [];
    private bool _disposed;

    /// <summary>Creates the host.</summary>
    public ConsumerHost(
        IRabbitMqConnectionProvider connectionProvider,
        IServiceScopeFactory scopeFactory,
        IOptions<RabbitMqOptions> options,
        ILogger<ConsumerHost> logger)
    {
        ArgumentNullException.ThrowIfNull(connectionProvider);
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _connectionProvider = connectionProvider;
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>Starts consuming for one consumer.</summary>
    public async Task StartAsync(IMessageConsumer consumer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        ObjectDisposedException.ThrowIf(_disposed, this);

        QueueDefinition definition = ChatTopology.Queues.FirstOrDefault(q => q.Name == consumer.QueueName)
            ?? throw new InvalidOperationException(
                $"'{consumer.QueueName}' is not declared in ChatTopology.Queues. A consumer on an "
                + "undeclared queue would receive nothing and fail silently.");

        IChannel channel = await _connectionProvider.CreateChannelAsync(cancellationToken).ConfigureAwait(false);
        _channels.Add(channel);

        // Bound in-flight work. Without a prefetch limit the broker pushes the whole queue at
        // once and a restart loses every unacknowledged message back onto the queue at the same
        // moment — a thundering herd exactly when the system is least healthy.
        await channel.BasicQosAsync(0, definition.PrefetchCount, global: false, cancellationToken)
            .ConfigureAwait(false);

        AsyncEventingBasicConsumer eventingConsumer = new(channel);
        eventingConsumer.ReceivedAsync += (_, delivery) => HandleDeliveryAsync(channel, consumer, delivery);

        await channel.BasicConsumeAsync(
            queue: consumer.QueueName,
            autoAck: false, // Principle VI: acknowledge only after the work is committed.
            consumer: eventingConsumer,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        ConsumerStarted(_logger, consumer.QueueName, definition.PrefetchCount);
    }

    /// <summary>
    /// Processes one delivery. Exposed so tests can drive it deterministically rather than
    /// racing the broker's push.
    /// </summary>
    public async Task HandleDeliveryAsync(
        IChannel channel,
        IMessageConsumer consumer,
        BasicDeliverEventArgs delivery)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(consumer);
        ArgumentNullException.ThrowIfNull(delivery);

        MessageEnvelope envelope = ReadEnvelope(delivery);

        using Activity? activity = StartConsumeActivity(consumer, envelope);

        try
        {
            bool processed = await TryProcessAsync(consumer, envelope).ConfigureAwait(false);

            if (!processed)
            {
                // Already handled by this consumer on an earlier delivery. Acknowledge and move
                // on — this is the at-least-once guarantee being absorbed, not an error.
                activity?.SetTag(ChatTelemetry.Attributes.Duplicate, true);
                DuplicateSkipped(_logger, consumer.QueueName, envelope.MessageId);
            }

            await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Recorded on the span as well as the log, so a trace that ends in the DLQ says so
            // rather than simply stopping.
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            await HandleFailureAsync(channel, consumer, delivery, envelope, ex).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Opens the consume span as a child of whatever produced the message.
    /// </summary>
    /// <remarks>
    /// This is where the header stops being a string and starts being a trace. The publisher wrote
    /// <c>traceparent</c> at dispatch time; parsing it here is what makes "employee sent a message"
    /// and "notification delivered ninety seconds later in another process" one timeline instead of
    /// two unrelated ones.
    /// </remarks>
    private static Activity? StartConsumeActivity(IMessageConsumer consumer, MessageEnvelope envelope)
    {
        ActivityContext parent = default;
        if (!string.IsNullOrEmpty(envelope.TraceParent)
            && ActivityContext.TryParse(envelope.TraceParent, traceState: null, out ActivityContext parsed))
        {
            parent = parsed;
        }

        Activity? activity = ChatTelemetry.ActivitySource.StartActivity(
            $"{consumer.QueueName} {ChatTelemetry.Spans.ConsumeMessage}",
            ActivityKind.Consumer,
            parent);

        if (activity is not null)
        {
            activity.SetTag(ChatTelemetry.Attributes.MessagingSystem, "rabbitmq");
            activity.SetTag(ChatTelemetry.Attributes.Destination, consumer.QueueName);
            activity.SetTag(ChatTelemetry.Attributes.MessageId, envelope.MessageId);
            activity.SetTag(ChatTelemetry.Attributes.MessageType, envelope.Type);
            activity.SetTag(ChatTelemetry.Attributes.Attempt, envelope.Attempt);
        }

        return activity;
    }

    /// <summary>
    /// Runs the handler and records deduplication in one transaction.
    /// </summary>
    /// <returns><c>false</c> when this consumer already handled the message.</returns>
    private async Task<bool> TryProcessAsync(IMessageConsumer consumer, MessageEnvelope envelope)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        ChatDbContext context = scope.ServiceProvider.GetRequiredService<ChatDbContext>();

        await using var transaction = await context.Database.BeginTransactionAsync().ConfigureAwait(false);

        // ON CONFLICT DO NOTHING rather than "check then insert". The check-then-insert version
        // has a race: two deliveries of the same message on different channels both see nothing
        // and both proceed. Here the database decides, atomically, and the loser gets 0 rows.
        int inserted = await context.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO processed_message (consumer_name, message_id, processed_at)
            VALUES ({consumer.QueueName}, {envelope.MessageId}, {DateTimeOffset.UtcNow})
            ON CONFLICT DO NOTHING
            """).ConfigureAwait(false);

        if (inserted == 0)
        {
            await transaction.RollbackAsync().ConfigureAwait(false);
            return false;
        }

        await consumer.HandleAsync(envelope).ConfigureAwait(false);
        await context.SaveChangesAsync().ConfigureAwait(false);

        // Handler work and deduplication record commit together. A handler that throws takes the
        // deduplication record with it, so the retry genuinely reprocesses.
        await transaction.CommitAsync().ConfigureAwait(false);
        return true;
    }

    private async Task HandleFailureAsync(
        IChannel channel,
        IMessageConsumer consumer,
        BasicDeliverEventArgs delivery,
        MessageEnvelope envelope,
        Exception failure)
    {
        IReadOnlyList<TimeSpan> delays = _options.RetryDelays;
        bool retriesRemain = envelope.Attempt <= delays.Count;

        BasicProperties properties = CloneProperties(delivery, envelope.Attempt + 1, failure);

        if (retriesRemain)
        {
            TimeSpan delay = delays[envelope.Attempt - 1];

            // Published to the retry queue, whose TTL expiry dead-letters it back to this queue.
            // The wait is the broker's job; nothing here sleeps or schedules.
            await channel.BasicPublishAsync(
                exchange: string.Empty,
                routingKey: ChatTopology.RetryQueueName(consumer.QueueName, delay),
                mandatory: false,
                basicProperties: properties,
                body: delivery.Body.ToArray()).ConfigureAwait(false);

            RetryScheduled(_logger, consumer.QueueName, envelope.MessageId, envelope.Attempt, delay, failure);
        }
        else
        {
            await channel.BasicPublishAsync(
                exchange: ChatTopology.DeadLetterExchange,
                routingKey: consumer.QueueName,
                mandatory: false,
                basicProperties: properties,
                body: delivery.Body.ToArray()).ConfigureAwait(false);

            // Sev-1 territory per Principle VI. The message is not lost — it is parked where DLQ
            // depth alerting will surface it — but nothing downstream has happened.
            DeadLettered(_logger, consumer.QueueName, envelope.MessageId, envelope.Attempt, failure);
        }

        // Acknowledge the original only after the replacement is safely published. Acknowledging
        // first would drop the message entirely if the republish failed.
        await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false).ConfigureAwait(false);
    }

    private static MessageEnvelope ReadEnvelope(BasicDeliverEventArgs delivery)
    {
        IReadOnlyBasicProperties properties = delivery.BasicProperties;

        // A message with no parseable id cannot be deduplicated. Rather than guess, give it a
        // deterministic id derived from nothing — it will be treated as new, processed once, and
        // its absence of an id is visible in the log.
        Guid messageId = Guid.TryParse(properties.MessageId, out Guid parsed) ? parsed : Guid.Empty;

        int attempt = 1;
        if (properties.Headers?.TryGetValue(ChatTopology.AttemptHeader, out object? raw) == true)
        {
            attempt = raw switch
            {
                int i => i,
                long l => (int)l,
                byte[] bytes => int.TryParse(Encoding.UTF8.GetString(bytes), out int b) ? b : 1,
                _ => 1,
            };
        }

        string? traceParent = null;
        if (properties.Headers?.TryGetValue("traceparent", out object? trace) == true && trace is byte[] traceBytes)
        {
            traceParent = Encoding.UTF8.GetString(traceBytes);
        }

        return new MessageEnvelope(
            messageId,
            properties.Type ?? string.Empty,
            Encoding.UTF8.GetString(delivery.Body.ToArray()),
            traceParent,
            attempt);
    }

    private static BasicProperties CloneProperties(
        BasicDeliverEventArgs delivery,
        int nextAttempt,
        Exception failure)
    {
        Dictionary<string, object?> headers = new(StringComparer.Ordinal)
        {
            [ChatTopology.AttemptHeader] = nextAttempt,
            // Truncated: a stack trace in a header bloats every retry, and the full detail is in
            // the log with the same message id.
            [ChatTopology.FailureReasonHeader] = failure.Message.Length <= 500
                ? failure.Message
                : failure.Message[..500],
        };

        if (delivery.BasicProperties.Headers?.TryGetValue("traceparent", out object? trace) == true)
        {
            headers["traceparent"] = trace;
        }

        return new BasicProperties
        {
            MessageId = delivery.BasicProperties.MessageId,
            Type = delivery.BasicProperties.Type,
            ContentType = delivery.BasicProperties.ContentType,
            DeliveryMode = DeliveryModes.Persistent,
            Headers = headers,
        };
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (IChannel channel in _channels)
        {
            await channel.DisposeAsync().ConfigureAwait(false);
        }

        _channels.Clear();
    }

    [LoggerMessage(EventId = 2100, Level = LogLevel.Information, Message = "Consuming {Queue} with prefetch {Prefetch}")]
    private static partial void ConsumerStarted(ILogger logger, string queue, ushort prefetch);

    [LoggerMessage(EventId = 2101, Level = LogLevel.Debug, Message = "{Queue} skipped already-processed message {MessageId}")]
    private static partial void DuplicateSkipped(ILogger logger, string queue, Guid messageId);

    [LoggerMessage(EventId = 2102, Level = LogLevel.Warning, Message = "{Queue} message {MessageId} failed on attempt {Attempt}, retrying in {Delay}")]
    private static partial void RetryScheduled(ILogger logger, string queue, Guid messageId, int attempt, TimeSpan delay, Exception exception);

    [LoggerMessage(EventId = 2103, Level = LogLevel.Error, Message = "{Queue} message {MessageId} dead-lettered after {Attempt} attempts")]
    private static partial void DeadLettered(ILogger logger, string queue, Guid messageId, int attempt, Exception exception);
}
