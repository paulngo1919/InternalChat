namespace InternalChat.Infrastructure.Messaging;

/// <summary>A message delivered from a queue.</summary>
/// <param name="MessageId">
/// The outbox row id, and therefore the idempotency key. The consumer host deduplicates on it;
/// handlers never need to.
/// </param>
/// <param name="Type">Versioned contract name, for example <c>chat.message.sent.v1</c>.</param>
/// <param name="Payload">Serialized event body — identifiers only, never message content.</param>
/// <param name="TraceParent">W3C trace context from the originating request.</param>
/// <param name="Attempt">Delivery attempt, starting at 1.</param>
public sealed record MessageEnvelope(
    Guid MessageId,
    string Type,
    string Payload,
    string? TraceParent,
    int Attempt);

/// <summary>
/// Handles messages from one queue.
/// </summary>
/// <remarks>
/// <para>
/// Implementations do their work and throw on failure. They do not acknowledge, retry, or
/// deduplicate — <see cref="ConsumerHost"/> owns all three, so every consumer gets identical
/// semantics rather than each reimplementing them slightly differently.
/// </para>
/// <para>
/// Handlers run inside a transaction that also contains the deduplication record, so throwing
/// rolls back both: the message is genuinely unprocessed and will be retried.
/// </para>
/// </remarks>
public interface IMessageConsumer
{
    /// <summary>Queue to consume, also the consumer name used for deduplication.</summary>
    string QueueName { get; }

    /// <summary>Handles one message. Throw to trigger retry.</summary>
    Task HandleAsync(MessageEnvelope envelope, CancellationToken cancellationToken = default);
}
