using InternalChat.Application.Abstractions;
using InternalChat.Application.Behaviors;
using InternalChat.Domain.Common;
using InternalChat.Domain.Conversations;
using InternalChat.Domain.Messages;

namespace InternalChat.Application.Messages;

/// <summary>Accepts one message into a conversation (FR-009, FR-011).</summary>
/// <param name="ClientMessageKey">The sender's ULID. Validated before anything is written.</param>
/// <param name="Mentions">
/// Candidates from the client. Narrowed to active members before storage — a client cannot cause a
/// notification for someone who is not in the conversation by naming them.
/// </param>
/// <param name="AttachmentIds">
/// Uploads already reserved in this conversation by this sender. Bound to the message here, which
/// is the moment an attachment stops being an orphaned upload and becomes conversation content.
/// </param>
public sealed record SendMessage(
    Guid ConversationId,
    Guid AuthorId,
    string ClientMessageKey,
    string Body,
    IReadOnlyList<Guid>? Mentions,
    IReadOnlyList<Guid>? AttachmentIds = null) : ITransactionalRequest;

/// <summary>Outcome of a send.</summary>
/// <param name="WasCreated">
/// <c>false</c> when this was an idempotent replay. The endpoint turns it into 201 or 200, which is
/// how the client distinguishes a new message from its own retry landing twice.
/// </param>
/// <param name="Attachments">The attachments now bound to this message, in upload order.</param>
public sealed record SendMessageResult(
    Message Message,
    bool WasCreated,
    IReadOnlyList<Domain.Attachments.Attachment> Attachments);

/// <summary>
/// Allocates a sequence, writes the message, and records the event — all in one transaction.
/// </summary>
/// <remarks>
/// <para>
/// <b>The order of the first two steps is the whole design.</b> The client key is claimed
/// <em>before</em> the sequence is allocated. Doing it the other way round works for every
/// single-request test and is wrong: a duplicate send would consume a sequence number and then
/// return the original message, leaving a permanent gap. A gap makes "everything above seq N"
/// ambiguous, so every client that reconnects afterwards either misses a message or waits for one
/// that will never arrive.
/// </para>
/// <para>
/// <b>The instant is captured once</b> and handed to the domain as a fixed clock. The claim row and
/// the message row must agree on <c>sent_at</c> exactly, because that column is the partition key
/// and the claim is what a later retry uses to find the message again. Two separate reads of
/// <c>IClock.UtcNow</c> would differ by microseconds and send that lookup to the wrong partition —
/// where it would find nothing and report the original message as missing.
/// </para>
/// <para>
/// Nothing here touches RabbitMQ. The event goes to the outbox inside this transaction
/// (Principle VI), so a message and the notification of it either both exist or neither does.
/// </para>
/// </remarks>
public sealed class SendMessageHandler : IUseCase<SendMessage, SendMessageResult>
{
    private readonly IConversationRepository _conversations;
    private readonly IMessageRepository _messages;
    private readonly IMembershipRepository _memberships;
    private readonly IEventPublisher _events;
    private readonly IAttachmentRepository _attachments;
    private readonly IClock _clock;

    /// <summary>Creates the handler.</summary>
    public SendMessageHandler(
        IConversationRepository conversations,
        IMessageRepository messages,
        IMembershipRepository memberships,
        IEventPublisher events,
        IAttachmentRepository attachments,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(conversations);
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(attachments);
        ArgumentNullException.ThrowIfNull(clock);

        _conversations = conversations;
        _messages = messages;
        _memberships = memberships;
        _events = events;
        _attachments = attachments;
        _clock = clock;
    }

    /// <inheritdoc />
    public async Task<SendMessageResult> HandleAsync(
        SendMessage request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Already validated by ValidationBehavior; parsed here to get the value object.
        ClientMessageKey key = ClientMessageKey.Parse(request.ClientMessageKey);
        MessageBody body = MessageBody.Create(request.Body);

        // The membership filter has already confirmed the conversation is reachable by this caller,
        // so a missing row here means it was deleted between the two — refused, not created.
        Conversation conversation = await _conversations
            .FindAsync(request.ConversationId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new UnauthorizedAccessException(
                $"Conversation {request.ConversationId} does not exist.");

        Guid messageId = Guid.CreateVersion7();
        DateTimeOffset sentAt = _clock.UtcNow;

        ClaimedMessage? existing = await _messages
            .TryClaimClientKeyAsync(request.ConversationId, key, messageId, sentAt, cancellationToken)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            // Someone already sent under this key — either this same client retrying, or its own
            // earlier attempt that succeeded after the client stopped waiting. Return what is
            // stored, unchanged: the key identifies the send attempt, not the text, so a differing
            // body is not an edit and must not overwrite anything.
            Message original = await _messages
                .FindAsync(request.ConversationId, existing.MessageId, existing.SentAt, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"Client message key '{key.Value}' is claimed by message {existing.MessageId}, "
                    + "which does not exist. The claim and the message are written in one "
                    + "transaction, so this means something deleted the message row without "
                    + "releasing its key.");

            // The attachments already bound to the original. A replay must return the same
            // message *and* the same attachments — a client that retried and got an empty list
            // would render the image as having failed to send.
            IReadOnlyList<Domain.Attachments.Attachment> bound = await _attachments
                .GetForMessagesAsync([original.Id], cancellationToken)
                .ConfigureAwait(false);

            return new SendMessageResult(original, WasCreated: false, bound);
        }

        // Row-locked, and therefore the serialization point for concurrent sends. Also advances the
        // conversation's updated_at, so the conversation list orders by real activity.
        long seq = await _conversations
            .AllocateSequenceAsync(request.ConversationId, cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<Guid> mentions = await ResolveMentionsAsync(request, cancellationToken)
            .ConfigureAwait(false);

        Message message = Message.Send(
            messageId,
            request.ConversationId,
            seq,
            request.AuthorId,
            key,
            body,
            new FixedInstantClock(sentAt),
            mentions);

        await _messages.AddAsync(message, cancellationToken).ConfigureAwait(false);

        IReadOnlyList<Domain.Attachments.Attachment> attached =
            await BindAttachmentsAsync(request, messageId, sentAt, cancellationToken).ConfigureAwait(false);

        await _events.PublishAsync(message.DomainEvents, cancellationToken).ConfigureAwait(false);
        message.ClearDomainEvents();

        // The attachments' own events — chat.attachment.uploaded.v1, raised by AttachTo — go to the
        // same outbox inside the same transaction. This is what starts the scan, so publishing them
        // separately (or after the commit) would mean a message could exist carrying an attachment
        // nothing ever scans, which would sit at "scanning…" forever.
        foreach (Domain.Attachments.Attachment attachment in attached)
        {
            await _events.PublishAsync(attachment.DomainEvents, cancellationToken).ConfigureAwait(false);
            attachment.ClearDomainEvents();
        }

        // Keeps the loaded entity consistent with the row the raw UPDATE just changed. Without it,
        // anything else in this transaction reading conversation.LastSeq would see the stale value.
        conversation.SynchroniseSequence(seq);

        return new SendMessageResult(message, WasCreated: true, attached);
    }

    /// <summary>
    /// Binds the sender's already-uploaded attachments to the message being sent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Refuses rather than filters, which is the opposite of how mentions are handled.</b> The
    /// difference is what silence would mean to the sender. A dropped mention costs a notification
    /// nobody was promised; a dropped attachment means the person believes they shared a screenshot
    /// that simply is not there, and they will not find out until someone asks about it.
    /// </para>
    /// <para>
    /// The repository applies the conversation and uploader scoping inside the query, so an id
    /// belonging to another conversation or another employee comes back as *absent* and is refused
    /// here — indistinguishable from an id that never existed. That is deliberate: distinguishing
    /// them would tell a caller whether an attachment id is real, which is the enumeration answer
    /// the download path also refuses to give.
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyList<Domain.Attachments.Attachment>> BindAttachmentsAsync(
        SendMessage request,
        Guid messageId,
        DateTimeOffset sentAt,
        CancellationToken cancellationToken)
    {
        if (request.AttachmentIds is not { Count: > 0 } requested)
        {
            return [];
        }

        // Distinct: naming the same attachment twice is one attachment, and the duplicate would
        // otherwise be reported missing on the second AttachTo — which is write-once.
        List<Guid> ids = [.. requested.Distinct()];

        IReadOnlyList<Domain.Attachments.Attachment> attachments = await _attachments
            .GetAttachableAsync(request.ConversationId, request.AuthorId, ids, cancellationToken)
            .ConfigureAwait(false);

        if (attachments.Count != ids.Count)
        {
            HashSet<Guid> found = [.. attachments.Select(a => a.Id)];

            throw new AttachmentNotAttachableException([.. ids.Where(id => !found.Contains(id))]);
        }

        foreach (Domain.Attachments.Attachment attachment in attachments)
        {
            // sentAt is the same instant the message was constructed with, not a fresh clock read.
            // It is the message's partition key, and the attachment stores it to locate that row —
            // a value microseconds apart would point at the right id in the wrong partition.
            attachment.AttachTo(messageId, sentAt);
        }

        return attachments;
    }

    /// <summary>
    /// Narrows client-supplied mentions to employees who are actually active members (FR-015).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Skipped entirely when nothing was mentioned, which is the overwhelming majority of messages
    /// and would otherwise pay for a membership query on every send against a 150 ms budget.
    /// </para>
    /// <para>
    /// Filtered rather than rejected. A mention of someone who just left is a stale client, not an
    /// attack, and failing the send would lose a message the sender already typed over a detail they
    /// cannot see. T117 formalises this for groups; the filter is here because it is the send path
    /// that decides who gets notified.
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyList<Guid>> ResolveMentionsAsync(
        SendMessage request,
        CancellationToken cancellationToken)
    {
        if (request.Mentions is null || request.Mentions.Count == 0)
        {
            return [];
        }

        IReadOnlyList<Membership> members = await _memberships
            .ListForConversationAsync(request.ConversationId, cancellationToken)
            .ConfigureAwait(false);

        HashSet<Guid> active = [.. members.Where(m => m.IsActive).Select(m => m.EmployeeId)];

        return [.. request.Mentions.Distinct().Where(active.Contains)];
    }
}

/// <summary>
/// A clock frozen at one instant, so several writes in a transaction agree on it.
/// </summary>
/// <remarks>
/// Not a testing seam. <c>Message.Send</c> takes an <see cref="IClock"/> rather than a timestamp
/// precisely so a client can never supply one (FR-012); this keeps that property while making the
/// instant deterministic across the claim row and the message row, which must match on
/// <c>sent_at</c> because it is the partition key.
/// </remarks>
internal sealed class FixedInstantClock : IClock
{
    public FixedInstantClock(DateTimeOffset instant) => UtcNow = instant;

    public DateTimeOffset UtcNow { get; }
}

/// <summary>Rejects a send before it can write anything.</summary>
/// <remarks>
/// The key is checked here rather than left to <see cref="ClientMessageKey.Parse"/> so the caller
/// gets a named field and a 400, instead of an <see cref="ArgumentException"/> that the problem
/// handler can only report as an internal error.
/// </remarks>
public sealed class SendMessageValidator : IValidator<SendMessage>
{
    /// <inheritdoc />
    public ValueTask<ValidationResult> ValidateAsync(
        SendMessage request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        List<ValidationError> errors = [];

        if (!ClientMessageKey.TryParse(request.ClientMessageKey, out _))
        {
            errors.Add(new ValidationError(
                "clientMessageKey",
                $"A client message key is a {ClientMessageKey.Length}-character ULID. It is the "
                + "sender's idempotency key, so a malformed one is refused rather than stored."));
        }

        if (string.IsNullOrWhiteSpace(request.Body))
        {
            errors.Add(new ValidationError("body", "A message needs a body."));
        }
        else if (request.Body.Trim().Length > MessageBody.MaximumLength)
        {
            errors.Add(new ValidationError(
                "body",
                $"A message body is at most {MessageBody.MaximumLength} characters."));
        }

        return ValueTask.FromResult(errors.Count == 0 ? ValidationResult.Valid : new ValidationResult(errors));
    }
}

/// <summary>
/// Thrown when a send names an attachment that cannot be bound to it. Maps to 422.
/// </summary>
/// <remarks>
/// One exception for every cause — never uploaded, already bound to another message, uploaded by
/// someone else, or reserved in a different conversation. The causes are deliberately not
/// distinguished: telling a caller which of their guesses was a real attachment id is the same
/// enumeration answer <c>AuthorizeDownload</c> refuses to give.
/// </remarks>
public sealed class AttachmentNotAttachableException : InvalidOperationException
{
    /// <summary>Creates the exception.</summary>
    public AttachmentNotAttachableException(IReadOnlyList<Guid> attachmentIds)
        : base("One or more attachments cannot be attached to this message. They may have already "
            + "been sent, may not have finished uploading, or may not belong to this conversation.") =>
        AttachmentIds = attachmentIds ?? [];

    /// <summary>Creates the exception.</summary>
    public AttachmentNotAttachableException()
    {
    }

    /// <summary>Creates the exception.</summary>
    public AttachmentNotAttachableException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public AttachmentNotAttachableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The ids that could not be bound.</summary>
    public IReadOnlyList<Guid> AttachmentIds { get; } = [];
}
