namespace InternalChat.Infrastructure.Persistence.Outbox;

/// <summary>
/// Record that one consumer has already handled one message.
/// </summary>
/// <remarks>
/// <para>
/// Constitution Principle VI: "Every consumer MUST be idempotent. Delivery is at-least-once;
/// consumers MUST deduplicate on the message's idempotency key."
/// </para>
/// <para>
/// At-least-once is the only guarantee the broker offers, so correctness has to come from here.
/// A consumer that acknowledges after a crash, or a broker that redelivers on a channel reset,
/// will hand over the same message again — and for notification fan-out that means an employee
/// notified twice for one message.
/// </para>
/// <para>
/// Keyed per consumer, not globally: every consumer must process each message once, so
/// <c>notifications.fanout</c> having handled a message must not stop <c>search.index</c> from
/// handling it.
/// </para>
/// </remarks>
public sealed class ProcessedMessage
{
    /// <summary>Which consumer handled it, for example <c>notifications.fanout</c>.</summary>
    public string ConsumerName { get; set; } = string.Empty;

    /// <summary>The message's idempotency key — the outbox row id.</summary>
    public Guid MessageId { get; set; }

    /// <summary>When it was handled. Rows are pruned after 30 days.</summary>
    public DateTimeOffset ProcessedAt { get; set; }
}
