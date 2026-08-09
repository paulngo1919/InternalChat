using System.Globalization;
using RabbitMQ.Client;

namespace InternalChat.Infrastructure.Messaging;

/// <summary>One consumer queue and how it is fed.</summary>
/// <param name="Name">Queue name, also the consumer name used for deduplication.</param>
/// <param name="BindingPattern">Topic pattern bound to the events exchange.</param>
/// <param name="PrefetchCount">Unacknowledged messages allowed in flight.</param>
public sealed record QueueDefinition(string Name, string BindingPattern, ushort PrefetchCount);

/// <summary>
/// Declares the exchanges, queues, retry path, and dead-letter queues from
/// <c>contracts/messaging.md</c>.
/// </summary>
/// <remarks>
/// <para>
/// Constitution Principle VI: "Every queue MUST have a dead-letter queue, a bounded retry policy
/// with exponential backoff, and an alert on DLQ depth. Silent message loss is a Sev-1 defect."
/// </para>
/// <para>
/// <b>Why the retry path looks like this.</b> RabbitMQ has no built-in delayed redelivery — the
/// usual answer is the delayed-message-exchange plugin, which is not in the base image and would
/// add a component to install and keep current. Instead each queue gets a set of retry queues
/// that hold a message for a fixed TTL and then dead-letter it back. A message that fails is
/// republished into the retry queue for its attempt number, waits there, and returns to its
/// original queue automatically. No plugin, no timer, no scheduler.
/// </para>
/// <para>
/// The alternative — nacking with <c>requeue: true</c> — is what people reach for first and is
/// actively harmful: the broker redelivers immediately and without limit, so one poison message
/// spins a consumer at full speed forever.
/// </para>
/// </remarks>
public static class ChatTopology
{
    /// <summary>Topic exchange every domain event is published to.</summary>
    public const string EventsExchange = "chat.events";

    /// <summary>
    /// Direct exchange used to return a retried message to its own queue.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="EventsExchange"/> on purpose. Dead-lettering a retry back to the
    /// topic exchange would re-fan it out to <em>every</em> queue matching the pattern, so one
    /// consumer's failure would make all the others process the message a second time.
    /// </remarks>
    public const string RequeueExchange = "chat.events.requeue";

    /// <summary>Topic exchange holding messages that exhausted their retries.</summary>
    public const string DeadLetterExchange = "chat.events.dlx";

    /// <summary>
    /// Production backoff schedule. Attempt 1 waits 1s, attempt 2 waits 5s, attempt 3 waits 25s;
    /// after that the message is dead-lettered rather than retried forever.
    /// </summary>
    /// <remarks>
    /// Bounded on purpose. Unbounded retry is how a single poison message keeps a consumer busy
    /// indefinitely while the queue behind it grows — Principle VI requires the retry policy to
    /// be capped and the remainder to be dead-lettered where it can be seen.
    /// </remarks>
    public static readonly IReadOnlyList<TimeSpan> DefaultRetryDelays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(25),
    ];

    /// <summary>Header carrying how many times delivery has been attempted.</summary>
    public const string AttemptHeader = "x-internalchat-attempt";

    /// <summary>Header recording why the last attempt failed, for DLQ triage.</summary>
    public const string FailureReasonHeader = "x-internalchat-failure";

    /// <summary>Queues from <c>contracts/messaging.md</c>.</summary>
    public static readonly IReadOnlyList<QueueDefinition> Queues =
    [
        // Prefetch 1, unlike every other queue. Ordering matters here in a way it does not
        // elsewhere: a deactivation overtaken by a stale attribute update would silently restore
        // access to an employee who has left, which is the one failure FR-003 is written against.
        new("directory.sync", "chat.directory.*", PrefetchCount: 1),
        new("notifications.fanout", "chat.message.sent.*", PrefetchCount: 4),
        new("attachments.scan", "chat.attachment.uploaded.*", PrefetchCount: 2),
        new("search.index", "chat.message.*", PrefetchCount: 4),
        new("audit.write", "chat.audit.*", PrefetchCount: 2),
        new("realtime.fanout", "chat.message.sent.*", PrefetchCount: 8),
        new("meetings.lifecycle", "chat.meeting.*", PrefetchCount: 1),
    ];

    /// <summary>Retry queue name for a given queue and attempt.</summary>
    public static string RetryQueueName(string queue, TimeSpan delay) =>
        $"{queue}.retry.{delay.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture)}ms";

    /// <summary>Dead-letter queue name for a given queue.</summary>
    public static string DeadLetterQueueName(string queue) => $"{queue}.dlq";

    /// <summary>
    /// Declares the whole topology. Idempotent, so every process may call it at startup.
    /// </summary>
    /// <param name="channel">Channel to declare on.</param>
    /// <param name="retryDelays">
    /// Backoff schedule, defaulting to <see cref="DefaultRetryDelays"/>. Overridden by tests so
    /// the dead-letter path can be exercised in milliseconds rather than half a minute.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task DeclareAsync(
        IChannel channel,
        IReadOnlyList<TimeSpan>? retryDelays = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channel);
        retryDelays ??= DefaultRetryDelays;

        // Durable throughout: Principle VI requires messages to survive a broker restart.
        await channel.ExchangeDeclareAsync(
            EventsExchange, ExchangeType.Topic, durable: true, autoDelete: false,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        await channel.ExchangeDeclareAsync(
            RequeueExchange, ExchangeType.Direct, durable: true, autoDelete: false,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        await channel.ExchangeDeclareAsync(
            DeadLetterExchange, ExchangeType.Topic, durable: true, autoDelete: false,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        foreach (QueueDefinition queue in Queues)
        {
            await DeclareQueueAsync(channel, queue, retryDelays, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task DeclareQueueAsync(
        IChannel channel,
        QueueDefinition queue,
        IReadOnlyList<TimeSpan> retryDelays,
        CancellationToken cancellationToken)
    {
        await channel.QueueDeclareAsync(
            queue.Name, durable: true, exclusive: false, autoDelete: false,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        await channel.QueueBindAsync(
            queue.Name, EventsExchange, queue.BindingPattern,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        // Second binding, on the direct exchange, keyed by queue name. This is the address a
        // retried message comes back to — reaching this one queue and no other.
        await channel.QueueBindAsync(
            queue.Name, RequeueExchange, queue.Name,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        foreach (TimeSpan delay in retryDelays)
        {
            // A retry queue has no consumer. Messages sit here until the TTL expires, at which
            // point the broker dead-letters them — which is what delivers them back to the main
            // queue. The waiting is the whole mechanism.
            Dictionary<string, object?> retryArguments = new(StringComparer.Ordinal)
            {
                ["x-message-ttl"] = (int)delay.TotalMilliseconds,
                ["x-dead-letter-exchange"] = RequeueExchange,
                ["x-dead-letter-routing-key"] = queue.Name,
            };

            await channel.QueueDeclareAsync(
                RetryQueueName(queue.Name, delay), durable: true, exclusive: false, autoDelete: false,
                arguments: retryArguments, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        string deadLetterQueue = DeadLetterQueueName(queue.Name);

        await channel.QueueDeclareAsync(
            deadLetterQueue, durable: true, exclusive: false, autoDelete: false,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        // Routing key is the queue name, so DLQ depth can be alerted on per consumer. A single
        // shared dead-letter queue would tell you something is broken but not what.
        await channel.QueueBindAsync(
            deadLetterQueue, DeadLetterExchange, queue.Name,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
