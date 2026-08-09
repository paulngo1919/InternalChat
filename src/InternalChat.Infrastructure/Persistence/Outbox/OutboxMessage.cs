namespace InternalChat.Infrastructure.Persistence.Outbox;

/// <summary>
/// One domain event awaiting publication to RabbitMQ.
/// </summary>
/// <remarks>
/// <para>
/// The mechanism behind Constitution Principle VI: "Database writes and message publishes MUST
/// NOT be dual-written without atomicity." The row is inserted in the same transaction as the
/// state change it describes, so the two commit or roll back together.
/// </para>
/// <para>
/// Without it, <c>SendMessage</c> would commit the message and then publish. A crash in between
/// loses the notification permanently, and nothing anywhere records that it happened — the
/// message is in the database, the recipient is never told, and no error is logged.
/// </para>
/// <para>
/// An Infrastructure persistence type, not a Domain entity. It describes how events reach the
/// broker, which is a transport concern the Domain must know nothing about.
/// </para>
/// </remarks>
public sealed class OutboxMessage
{
    /// <summary>
    /// Identity of this occurrence. Becomes the AMQP message id, and therefore the idempotency
    /// key consumers deduplicate on.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>Versioned contract name, for example <c>chat.message.sent.v1</c>.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Routing key used when publishing.</summary>
    public string RoutingKey { get; set; } = string.Empty;

    /// <summary>
    /// Serialized event payload.
    /// </summary>
    /// <remarks>
    /// Carries identifiers, never content. Message bodies must not appear in queue payloads —
    /// they would end up in broker logs, the management UI, and DLQ dumps, which FR-056 forbids.
    /// Consumers that need the body read it from PostgreSQL.
    /// </remarks>
    public string Payload { get; set; } = "{}";

    /// <summary>W3C trace context, so a trace spans request → outbox → consumer.</summary>
    public string? TraceParent { get; set; }

    /// <summary>Server time the event occurred.</summary>
    public DateTimeOffset OccurredAt { get; set; }

    /// <summary>
    /// When the broker confirmed the publish. <c>null</c> until then.
    /// </summary>
    /// <remarks>
    /// Set only after a publisher confirm, never merely after a send. Marking a row dispatched
    /// on send would reintroduce the loss this table exists to prevent, just one layer lower.
    /// </remarks>
    public DateTimeOffset? DispatchedAt { get; set; }

    /// <summary>Publish attempts so far, for backoff and for spotting a poison event.</summary>
    public int Attempts { get; set; }

    /// <summary>Last publish failure, for diagnosis. Never contains payload content.</summary>
    public string? LastError { get; set; }
}
