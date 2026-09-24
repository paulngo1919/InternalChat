using InternalChat.Domain.Common;

namespace InternalChat.Domain.Messages;

/// <summary>
/// One message in a conversation.
/// </summary>
/// <remarks>
/// <para>
/// Two identifiers, both load-bearing and neither replaceable by the other (research.md D1).
/// <see cref="ClientMessageKey"/> comes from the sender and makes a retry idempotent;
/// <see cref="Seq"/> comes from the server and is the <em>only</em> ordering authority. FR-012
/// forbids ordering by any client clock, so there is deliberately no constructor parameter for a
/// timestamp — <see cref="SentAt"/> always comes from the injected clock.
/// </para>
/// <para>
/// <b>Deletion is soft and terminal.</b> The row survives with its body cleared, because ordering,
/// sequence continuity, and the audit trail all depend on it still being there — a gap in the
/// sequence would make "everything after seq N" ambiguous on every reconnect. Nothing transitions
/// out of deleted.
/// </para>
/// </remarks>
public sealed class Message : Entity<Guid>
{
    private readonly List<Guid> _mentions = [];

    private Message(
        Guid id,
        Guid conversationId,
        long seq,
        Guid authorId,
        ClientMessageKey clientMessageKey,
        MessageBody body,
        DateTimeOffset sentAt)
        : base(id)
    {
        ConversationId = conversationId;
        Seq = seq;
        AuthorId = authorId;
        ClientMessageKey = clientMessageKey;
        Body = body;
        SentAt = sentAt;
    }

    /// <summary>The conversation this belongs to.</summary>
    public Guid ConversationId { get; private set; }

    /// <summary>
    /// Server-assigned, gapless within the conversation. The ordering authority (FR-012).
    /// </summary>
    /// <remarks>
    /// Allocated by the conversation before the message is constructed, and never changed
    /// afterwards — not by an edit, not by a delete. A moving sequence would reorder the
    /// conversation for everyone reading it.
    /// </remarks>
    public long Seq { get; private set; }

    /// <summary>Who sent it. The only employee permitted to edit or delete it (FR-014).</summary>
    public Guid AuthorId { get; private set; }

    /// <summary>The sender's idempotency key (FR-011).</summary>
    public ClientMessageKey ClientMessageKey { get; private set; }

    /// <summary>The text, or <c>null</c> once deleted.</summary>
    public MessageBody? Body { get; private set; }

    /// <summary>
    /// When the server accepted it. The partition key, and never a client's clock (FR-012).
    /// </summary>
    public DateTimeOffset SentAt { get; private set; }

    /// <summary>When it was last edited, or <c>null</c>.</summary>
    public DateTimeOffset? EditedAt { get; private set; }

    /// <summary>When it was deleted, or <c>null</c>. A set value makes this a tombstone.</summary>
    public DateTimeOffset? DeletedAt { get; private set; }

    /// <summary>True once deleted. A tombstone renders as such and has no readable body.</summary>
    public bool IsDeleted => DeletedAt is not null;

    /// <summary>
    /// Employees mentioned, resolved at send time (FR-015).
    /// </summary>
    /// <remarks>
    /// Resolved once and stored, rather than re-derived from the body on read. A mention resolves
    /// against the membership at the moment of sending; re-resolving later would change who a past
    /// message notified as people join and leave.
    /// </remarks>
    public IReadOnlyList<Guid> Mentions => _mentions.AsReadOnly();

    /// <summary>
    /// Records a new message.
    /// </summary>
    /// <param name="seq">
    /// Allocated by the conversation. Passed in rather than derived here because allocation needs a
    /// row lock the entity cannot take.
    /// </param>
    public static Message Send(
        Guid id,
        Guid conversationId,
        long seq,
        Guid authorId,
        ClientMessageKey clientMessageKey,
        MessageBody body,
        IClock clock,
        IEnumerable<Guid>? mentions = null)
    {
        ArgumentNullException.ThrowIfNull(clientMessageKey);
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentOutOfRangeException.ThrowIfLessThan(seq, 1);

        Message message = new(id, conversationId, seq, authorId, clientMessageKey, body, clock.UtcNow);

        if (mentions is not null)
        {
            // Distinct: mentioning someone twice in one message is one notification, and a
            // duplicated id would produce two.
            message._mentions.AddRange(mentions.Distinct());
        }

        message.Raise(new MessageSent(
            Guid.CreateVersion7(),
            message.SentAt,
            conversationId,
            id,
            seq,
            authorId,
            message.SentAt,
            [.. message._mentions]));

        return message;
    }

    /// <summary>
    /// Replaces the body, within the edit window, by the author.
    /// </summary>
    /// <exception cref="NotTheAuthorException">Someone other than the author tried to edit.</exception>
    /// <exception cref="MessageDeletedException">The message is a tombstone.</exception>
    /// <exception cref="EditWindowExpiredException">More than 24 hours have passed (FR-014).</exception>
    public void Edit(Guid editorId, MessageBody body, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(clock);

        // Author check first: someone who is not the author should be told they may not edit this
        // message, not that the window has closed on a message that was never theirs.
        if (editorId != AuthorId)
        {
            throw new NotTheAuthorException(Id, editorId);
        }

        if (IsDeleted)
        {
            throw new MessageDeletedException(Id);
        }

        if (!EditWindow.IsOpen(SentAt, clock))
        {
            throw new EditWindowExpiredException(Id, EditWindow.ClosesAt(SentAt));
        }

        Body = body;
        EditedAt = clock.UtcNow;

        Raise(new MessageEdited(
            Guid.CreateVersion7(), clock.UtcNow, ConversationId, Id, Seq, editorId, SentAt));
    }

    /// <summary>
    /// Clears the body and leaves a tombstone.
    /// </summary>
    /// <remarks>
    /// Idempotent, and the timestamp never moves. Same reasoning as
    /// <see cref="Employees.Employee.Deactivate"/>: <see cref="DeletedAt"/> is what an auditor reads
    /// to establish when the content stopped being available, and a retried delete must not rewrite
    /// that answer.
    /// </remarks>
    /// <exception cref="NotTheAuthorException">Someone other than the author tried to delete.</exception>
    /// <exception cref="EditWindowExpiredException">More than 24 hours have passed (FR-014).</exception>
    public void Delete(Guid actorId, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        if (actorId != AuthorId)
        {
            throw new NotTheAuthorException(Id, actorId);
        }

        if (IsDeleted)
        {
            return;
        }

        if (!EditWindow.IsOpen(SentAt, clock))
        {
            throw new EditWindowExpiredException(Id, EditWindow.ClosesAt(SentAt));
        }

        Body = null;
        DeletedAt = clock.UtcNow;

        Raise(new MessageDeleted(
            Guid.CreateVersion7(), clock.UtcNow, ConversationId, Id, Seq, actorId, SentAt));
    }
}

/// <summary>
/// Raised when a message is accepted (<c>chat.message.sent.v1</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Carries no body, deliberately.</b> contracts/messaging.md: "Message bodies in queue payloads
/// would end up in broker logs, management UI, and DLQ dumps, which FR-056 forbids." Consumers that
/// need the text read it from PostgreSQL, where access is controlled.
/// </para>
/// <para>
/// <b><see cref="SentAt"/> is carried so consumers can find the row.</b> <c>message</c>'s primary
/// key is <c>(id, sent_at)</c> because <c>sent_at</c> is the partition key, so a consumer holding
/// only the id would have to scan all twelve months of partitions to read one message. Not the same
/// as <see cref="DomainEvent.OccurredAt"/>: for a send the two coincide, but for an edit or a delete
/// <c>OccurredAt</c> is when the change happened and <see cref="SentAt"/> is when the message was
/// originally sent — which is the one that locates the partition.
/// </para>
/// </remarks>
public sealed record MessageSent(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid ConversationId,
    Guid MessageId,
    long Seq,
    Guid AuthorId,
    DateTimeOffset SentAt,
    IReadOnlyList<Guid> Mentions) : DomainEvent(EventId, OccurredAt)
{
    /// <inheritdoc />
    public override string EventType => "chat.message.sent.v1";
}

/// <summary>Raised when a message is edited (<c>chat.message.edited.v1</c>). Carries no body.</summary>
/// <param name="SentAt">The message's original send time — the partition key, not the edit time.</param>
public sealed record MessageEdited(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid ConversationId,
    Guid MessageId,
    long Seq,
    Guid ActorId,
    DateTimeOffset SentAt) : DomainEvent(EventId, OccurredAt)
{
    /// <inheritdoc />
    public override string EventType => "chat.message.edited.v1";
}

/// <summary>Raised when a message is deleted (<c>chat.message.deleted.v1</c>).</summary>
/// <param name="SentAt">The message's original send time — the partition key, not the delete time.</param>
public sealed record MessageDeleted(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid ConversationId,
    Guid MessageId,
    long Seq,
    Guid ActorId,
    DateTimeOffset SentAt) : DomainEvent(EventId, OccurredAt)
{
    /// <inheritdoc />
    public override string EventType => "chat.message.deleted.v1";
}

/// <summary>Thrown when someone other than the author tries to change a message (FR-014).</summary>
public sealed class NotTheAuthorException : InvalidOperationException
{
    /// <summary>Creates the exception.</summary>
    public NotTheAuthorException(Guid messageId, Guid actorId)
        : base($"Employee {actorId} is not the author of message {messageId}.")
    {
        MessageId = messageId;
        ActorId = actorId;
    }

    /// <summary>Creates the exception.</summary>
    public NotTheAuthorException()
    {
    }

    /// <summary>Creates the exception.</summary>
    public NotTheAuthorException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public NotTheAuthorException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The message that was not theirs.</summary>
    public Guid MessageId { get; }

    /// <summary>Who tried.</summary>
    public Guid ActorId { get; }
}

/// <summary>Thrown when an edit or delete arrives after the 24-hour window (FR-014).</summary>
public sealed class EditWindowExpiredException : InvalidOperationException
{
    /// <summary>Creates the exception.</summary>
    public EditWindowExpiredException(Guid messageId, DateTimeOffset closedAt)
        : base($"The edit window for message {messageId} closed at {closedAt:O}.")
    {
        MessageId = messageId;
        ClosedAt = closedAt;
    }

    /// <summary>Creates the exception.</summary>
    public EditWindowExpiredException()
    {
    }

    /// <summary>Creates the exception.</summary>
    public EditWindowExpiredException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public EditWindowExpiredException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The message that can no longer be changed.</summary>
    public Guid MessageId { get; }

    /// <summary>When the window closed.</summary>
    public DateTimeOffset ClosedAt { get; }
}

/// <summary>Thrown when a deleted message is edited. <c>deleted</c> is terminal.</summary>
public sealed class MessageDeletedException : InvalidOperationException
{
    /// <summary>Creates the exception.</summary>
    public MessageDeletedException(Guid messageId)
        : base($"Message {messageId} is deleted and cannot be edited.") => MessageId = messageId;

    /// <summary>Creates the exception.</summary>
    public MessageDeletedException()
    {
    }

    /// <summary>Creates the exception.</summary>
    public MessageDeletedException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public MessageDeletedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The tombstone.</summary>
    public Guid MessageId { get; }
}
