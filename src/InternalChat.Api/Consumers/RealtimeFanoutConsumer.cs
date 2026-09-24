using System.Text.Json;
using InternalChat.Api.Contracts;
using InternalChat.Api.Mapping;
using InternalChat.Api.Hubs;
using InternalChat.Application.Abstractions;
using InternalChat.Domain.Messages;
using Microsoft.AspNetCore.SignalR;

namespace InternalChat.Api.Consumers;

/// <summary>
/// T098 — delivers message events to connected clients over SignalR (FR-009, FR-014).
/// </summary>
/// <remarks>
/// <para>
/// <b>Hosted in the API, not the Worker</b>, and that is the whole reason this consumer exists
/// rather than the send path calling <c>IHubContext</c> directly. Only a process holding a WebSocket
/// can write to it; the Redis backplane extends that across replicas, but the event still has to
/// arrive at an API process. Publishing through RabbitMQ also decouples delivery from the sender's
/// 150 ms accept budget, which is what makes a 500-member group survivable (SC-013).
/// </para>
/// <para>
/// <b>The queue payload carries no message body</b>, deliberately — a body in a queue payload ends up
/// in broker logs, the management UI, and DLQ dumps, which FR-056 forbids. So the message is read
/// back from PostgreSQL here. That is one query per event, which is affordable because it is off the
/// request path, and it has a second benefit: what clients receive is what is stored, so an edit that
/// landed between the send and the fan-out cannot be delivered as stale text.
/// </para>
/// <para>
/// <b>Idempotency is the client's job, and it is cheap.</b> Delivery is at-least-once, so a client
/// may see the same event twice; every event carries <c>seq</c>, and a client that has already
/// applied that sequence ignores the repeat (contracts/signalr-hub.md). Attempting exactly-once on
/// the wire would mean per-connection acknowledgement state, which is the buffering that breaks at
/// 7,000 connections.
/// </para>
/// </remarks>
public sealed partial class RealtimeFanoutConsumer : IMessageConsumer
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly IHubContext<ChatHub> _hub;
    private readonly IMessageRepository _messages;
    private readonly ILogger<RealtimeFanoutConsumer> _logger;

    /// <summary>Creates the consumer.</summary>
    public RealtimeFanoutConsumer(
        IHubContext<ChatHub> hub,
        IMessageRepository messages,
        ILogger<RealtimeFanoutConsumer> logger)
    {
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(logger);

        _hub = hub;
        _messages = messages;
        _logger = logger;
    }

    /// <inheritdoc />
    public string QueueName => "realtime.fanout";

    /// <inheritdoc />
    /// <remarks>
    /// The idempotency key is <c>(realtime.fanout, envelope.MessageId)</c>, enforced by
    /// <c>ConsumerHost</c>'s <c>processed_message</c> insert. It is worth being explicit that this is
    /// a <em>best-effort</em> guard here rather than a correctness requirement: a duplicate delivery
    /// re-sends a SignalR event the client will ignore by <c>seq</c>, so the cost of a redelivery is
    /// one wasted frame rather than a duplicated message.
    /// </remarks>
    public async Task HandleAsync(MessageEnvelope envelope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        MessageEventPayload payload = Deserialize(envelope);

        string group = ChatHub.GroupFor(payload.ConversationId);

        // Deletion is handled without loading the message, and not as an optimisation: the event
        // carries only ids by design, and re-reading the row to build a full DTO would put the body
        // of the message being deleted back onto the wire.
        if (string.Equals(envelope.Type, MessageEventTypes.Deleted, StringComparison.Ordinal))
        {
            await _hub.Clients
                .Group(group)
                .SendAsync(
                    ChatHubEvents.MessageDeleted,
                    new
                    {
                        conversationId = payload.ConversationId,
                        messageId = payload.MessageId,
                        seq = payload.Seq,
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            Delivered(_logger, envelope.Type, payload.ConversationId, payload.Seq);
            return;
        }

        Message? message = await _messages
            .FindAsync(payload.ConversationId, payload.MessageId, payload.SentAt, cancellationToken)
            .ConfigureAwait(false);

        if (message is null)
        {
            // Not an error, and specifically not a retry. The message is gone — hard-deleted by a
            // retention partition drop, most plausibly — and no number of retries will bring it
            // back. Retrying would send this event around the capped retry chain and then to the
            // DLQ, where it would page somebody about a message that is supposed to be absent.
            MessageMissing(_logger, envelope.Type, payload.ConversationId, payload.MessageId);
            return;
        }

        string eventName = envelope.Type switch
        {
            MessageEventTypes.Sent => ChatHubEvents.MessageReceived,
            MessageEventTypes.Edited => ChatHubEvents.MessageEdited,

            // Thrown rather than ignored. An unknown message event means the binding widened without
            // this consumer following, and dropping it silently is how a new event type comes to be
            // "delivered" everywhere except to clients.
            _ => throw new InvalidOperationException(
                $"'{envelope.Type}' is not a message event this consumer knows how to deliver. The "
                + "realtime.fanout binding matches every chat.message.* event, so a new one needs a "
                + "hub event name here and in ChatHubEvents."),
        };

        await _hub.Clients
            .Group(group)
            .SendAsync(eventName, message.ToResponse(), cancellationToken)
            .ConfigureAwait(false);

        Delivered(_logger, envelope.Type, payload.ConversationId, payload.Seq);
    }

    private static MessageEventPayload Deserialize(MessageEnvelope envelope) =>
        JsonSerializer.Deserialize<MessageEventPayload>(envelope.Payload, SerializerOptions)
        ?? throw new InvalidOperationException(
            $"Message {envelope.MessageId} of type '{envelope.Type}' carried an unreadable payload. "
            + "A malformed payload is not retryable — it will be malformed on every attempt — so it "
            + "belongs in the DLQ for a person to look at.");

    [LoggerMessage(
        EventId = 4000,
        Level = LogLevel.Debug,
        Message = "Delivered {EventType} for conversation {ConversationId} at seq {Seq}")]
    private static partial void Delivered(
        ILogger logger,
        string eventType,
        Guid conversationId,
        long seq);

    [LoggerMessage(
        EventId = 4001,
        Level = LogLevel.Information,
        Message = "Skipped {EventType}: message {MessageId} in conversation {ConversationId} no longer exists")]
    private static partial void MessageMissing(
        ILogger logger,
        string eventType,
        Guid conversationId,
        Guid messageId);
}

/// <summary>The event types <c>realtime.fanout</c> receives.</summary>
/// <remarks>
/// Constants shared with the domain events that produce them. The strings are the versioned contract
/// names from <c>contracts/messaging.md</c>, so a version bump is a deliberate edit in one place
/// rather than a literal to find in several.
/// </remarks>
internal static class MessageEventTypes
{
    public const string Sent = "chat.message.sent.v1";
    public const string Edited = "chat.message.edited.v1";
    public const string Deleted = "chat.message.deleted.v1";
}

/// <summary>
/// The queue payload for a message event.
/// </summary>
/// <remarks>
/// Carries no body, per <c>contracts/messaging.md</c>. <c>SentAt</c> is present because
/// <c>message</c>'s primary key is <c>(id, sent_at)</c> — without it, reading the message back would
/// scan every monthly partition.
/// </remarks>
internal sealed record MessageEventPayload(
    Guid ConversationId,
    Guid MessageId,
    long Seq,
    Guid AuthorId,
    DateTimeOffset SentAt,
    IReadOnlyList<Guid>? Mentions);
