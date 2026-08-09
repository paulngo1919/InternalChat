namespace InternalChat.Application.Abstractions;

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
/// deduplicate — the consumer host owns all three, so every consumer gets identical semantics
/// rather than each reimplementing them slightly differently.
/// </para>
/// <para>
/// Handlers run inside a transaction that also contains the deduplication record, so throwing
/// rolls back both: the message is genuinely unprocessed and will be retried.
/// </para>
/// <para>
/// <b>Lives in Application, not Infrastructure.</b> It is an inbound port — the shape the
/// application offers to whatever delivers messages — and the Worker implements it. Were it an
/// Infrastructure type, every consumer in <c>InternalChat.Worker</c> would be naming Infrastructure
/// outside its composition root, which Principle I forbids and
/// <c>tests/Architecture/CompositionRootTests.cs</c> fails the build over. The broker stays behind
/// the boundary; only the envelope crosses it, and it carries no RabbitMQ type.
/// </para>
/// </remarks>
public interface IMessageConsumer
{
    /// <summary>Queue to consume, also the consumer name used for deduplication.</summary>
    string QueueName { get; }

    /// <summary>Handles one message. Throw to trigger retry.</summary>
    Task HandleAsync(MessageEnvelope envelope, CancellationToken cancellationToken = default);
}
