namespace InternalChat.Domain.Common;

/// <summary>
/// Something that happened in the domain and that other parts of the system may need to react
/// to — a message was sent, a member was removed, an attachment was scanned.
/// </summary>
/// <remarks>
/// <para>
/// Domain events are raised, never published directly. The persistence layer writes them to the
/// transactional outbox in the same transaction as the state change, and only then are they
/// dispatched to RabbitMQ (Constitution Principle VI, research.md D2).
/// </para>
/// <para>
/// <see cref="EventId"/> becomes the AMQP message id and therefore the consumer idempotency key.
/// Delivery is at-least-once, so consumers deduplicate on it — correctness comes from idempotent
/// consumers, not from hoping each message arrives exactly once.
/// </para>
/// </remarks>
public interface IDomainEvent
{
    /// <summary>
    /// Identity of this occurrence. Becomes the message id on the bus and the idempotency key
    /// consumers deduplicate on.
    /// </summary>
    Guid EventId { get; }

    /// <summary>Server time the event occurred. Never a client clock (FR-012).</summary>
    DateTimeOffset OccurredAt { get; }

    /// <summary>
    /// Versioned contract name, for example <c>chat.message.sent.v1</c>.
    /// </summary>
    /// <remarks>
    /// The version is part of the name because Principle VI requires changes to be additive:
    /// a breaking change publishes a new version alongside the old until consumers migrate.
    /// </remarks>
    string EventType { get; }
}
