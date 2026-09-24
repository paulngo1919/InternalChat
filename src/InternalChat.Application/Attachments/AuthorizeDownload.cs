using InternalChat.Application.Abstractions;
using InternalChat.Application.Behaviors;
using InternalChat.Domain.Attachments;

namespace InternalChat.Application.Attachments;

/// <summary>Decides whether one caller may retrieve one attachment's bytes, right now (FR-025).</summary>
public sealed record AuthorizeDownload(Guid AttachmentId, Guid RequestedBy);

/// <summary>
/// The storage location the reverse proxy should stream, once the request is authorized.
/// </summary>
/// <param name="ObjectKey">The key in the clean bucket. Never handed to a client.</param>
/// <param name="ContentType">The stored media type, for the response header.</param>
/// <param name="FileName">For the <c>Content-Disposition</c> filename hint.</param>
public sealed record AuthorizedContent(
    string ObjectKey,
    string ContentType,
    long ByteSize,
    string FileName);

/// <summary>
/// Resolves an attachment, checks membership against the database, and returns where the bytes are.
/// </summary>
/// <remarks>
/// <para>
/// <b>This runs on every single retrieval.</b> FR-025: membership is checked "regardless of how the
/// retrieval address was obtained". There is no signed URL and no token — the address is a plain
/// route, and holding it proves nothing. The check goes through <see cref="IMembershipReader"/>,
/// the same seam every other authorization decision uses, whose implementation is backed by
/// PostgreSQL through a 30-second cache. That is inside the 60-second ceiling Principle VII sets
/// for authorization data, and it is what makes FR-030's "revocation within 5 minutes" hold here
/// as well: no decision outlives the cache entry that produced it.
/// </para>
/// <para>
/// <b>Every refusal is the same refusal.</b> A non-member, a deleted message, a pending scan, and
/// an attachment that never existed all produce <see cref="UnauthorizedAccessException"/>, which
/// the API renders as a 404-shaped body (SC-017). Distinguishing them would answer the question
/// "does an attachment with this id exist?" for anyone willing to enumerate — and a 403 on a real
/// id versus a 404 on a fake one is exactly that answer. The one exception is
/// <see cref="AttachmentInfectedException"/>, and it is safe precisely because the caller is
/// already established as a member of the conversation it was posted in.
/// </para>
/// </remarks>
public sealed class AuthorizeDownloadHandler : IUseCase<AuthorizeDownload, AuthorizedContent>
{
    private readonly IAttachmentRepository _attachments;
    private readonly IMessageRepository _messages;
    private readonly IMembershipReader _memberships;

    /// <summary>Creates the handler.</summary>
    public AuthorizeDownloadHandler(
        IAttachmentRepository attachments,
        IMessageRepository messages,
        IMembershipReader memberships)
    {
        ArgumentNullException.ThrowIfNull(attachments);
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(memberships);

        _attachments = attachments;
        _messages = messages;
        _memberships = memberships;
    }

    /// <inheritdoc />
    public async Task<AuthorizedContent> HandleAsync(
        AuthorizeDownload request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        Attachment? attachment = await _attachments
            .FindAsync(request.AttachmentId, cancellationToken)
            .ConfigureAwait(false);

        if (attachment is null)
        {
            throw Refuse(request.AttachmentId);
        }

        // The conversation comes from the row, never from the caller. This is the whole reason
        // FindAsync is not scoped by conversation: the attachment decides which membership is
        // checked, so a caller cannot nominate one they happen to belong to.
        MembershipSnapshot? membership = await _memberships
            .FindGrantingAsync(attachment.ConversationId, request.RequestedBy, cancellationToken)
            .ConfigureAwait(false);

        if (membership is null)
        {
            throw Refuse(request.AttachmentId);
        }

        // Only now, once the caller is established as a member, is it safe to say anything more
        // specific than "no" — everything below describes the state of content they may see.
        if (attachment.ScanStatus is ScanVerdict.Infected)
        {
            throw new AttachmentInfectedException(attachment.Id);
        }

        bool messageIsDeleted = await IsCarryingMessageDeletedAsync(attachment, cancellationToken)
            .ConfigureAwait(false);

        if (!attachment.IsRetrievable(messageIsDeleted))
        {
            // Pending or failed scan (FR-024), or a tombstoned message (FR-027). Same refusal:
            // there is nothing here to serve.
            throw Refuse(request.AttachmentId);
        }

        return new AuthorizedContent(
            attachment.ObjectKey,
            attachment.ContentType,
            attachment.ByteSize,
            attachment.FileName);
    }

    /// <summary>
    /// Whether the message carrying this attachment is a tombstone (FR-027).
    /// </summary>
    /// <remarks>
    /// An unattached attachment — one whose upload completed but whose message was never sent — has
    /// no message to be deleted, so it is not blocked here. It is still unreachable in practice:
    /// nothing renders an id the sender never published, and the retention sweep collects it.
    /// </remarks>
    private async Task<bool> IsCarryingMessageDeletedAsync(
        Attachment attachment,
        CancellationToken cancellationToken)
    {
        if (attachment.MessageId is not { } messageId)
        {
            return false;
        }

        Domain.Messages.Message? message = await _messages
            .FindAsync(
                attachment.ConversationId,
                messageId,

                // The partition key, carried on the attachment row precisely so this lookup is a
                // primary-key hit rather than a scan of twelve months of partitions — on the path
                // that serves every image in every conversation.
                attachment.MessageSentAt,
                cancellationToken)
            .ConfigureAwait(false);

        // A message that has vanished entirely is treated as deleted. Retention drops whole
        // partitions, so this is the normal end state for an attachment older than the window.
        return message is null || message.IsDeleted;
    }

    private static UnauthorizedAccessException Refuse(Guid attachmentId) =>
        new($"Attachment {attachmentId} is not retrievable by this caller.");
}

/// <summary>
/// Thrown when a member requests an attachment the scan found malicious (FR-024). Maps to 451.
/// </summary>
/// <remarks>
/// Distinguished from the generic refusal deliberately, and only reachable after membership has
/// been confirmed. Telling a member "this file was blocked as malicious" is useful — they may have
/// been expecting it, and silence would look like a broken upload. Telling a non-member the same
/// thing would confirm the file exists, which is why the membership check comes first.
/// </remarks>
public sealed class AttachmentInfectedException : InvalidOperationException
{
    /// <summary>Creates the exception.</summary>
    public AttachmentInfectedException(Guid attachmentId)
        : base("This file was blocked because a malware scan found it malicious (FR-024).") =>
        AttachmentId = attachmentId;

    /// <summary>Creates the exception.</summary>
    public AttachmentInfectedException()
    {
    }

    /// <summary>Creates the exception.</summary>
    public AttachmentInfectedException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public AttachmentInfectedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The blocked attachment.</summary>
    public Guid AttachmentId { get; }
}
