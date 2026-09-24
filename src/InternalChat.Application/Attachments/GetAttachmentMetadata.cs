using InternalChat.Application.Abstractions;
using InternalChat.Application.Behaviors;
using InternalChat.Domain.Attachments;

namespace InternalChat.Application.Attachments;

/// <summary>Reads one attachment's metadata and scan status, for a member of its conversation.</summary>
public sealed record GetAttachmentMetadata(Guid AttachmentId, Guid RequestedBy);

/// <summary>
/// Returns attachment metadata after the same membership check the byte path performs.
/// </summary>
/// <remarks>
/// <para>
/// <b>Metadata is content.</b> A file name, a size, and a scan status describe what was posted in a
/// conversation, so this is gated exactly as the bytes are — a non-member learns nothing, including
/// whether the id exists (SC-017).
/// </para>
/// <para>
/// <b>Unlike the byte path, this does not require a clean verdict.</b> That is the whole reason the
/// endpoint exists: a client polls it after uploading to find out when <c>pending</c> becomes
/// <c>clean</c>, so it can swap "scanning…" for the image. Refusing anything not yet clean would
/// make the state unobservable and leave the client guessing with a timer.
/// </para>
/// </remarks>
public sealed class GetAttachmentMetadataHandler : IUseCase<GetAttachmentMetadata, Attachment>
{
    private readonly IAttachmentRepository _attachments;
    private readonly IMembershipReader _memberships;

    /// <summary>Creates the handler.</summary>
    public GetAttachmentMetadataHandler(
        IAttachmentRepository attachments,
        IMembershipReader memberships)
    {
        ArgumentNullException.ThrowIfNull(attachments);
        ArgumentNullException.ThrowIfNull(memberships);

        _attachments = attachments;
        _memberships = memberships;
    }

    /// <inheritdoc />
    public async Task<Attachment> HandleAsync(
        GetAttachmentMetadata request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        Attachment? attachment = await _attachments
            .FindAsync(request.AttachmentId, cancellationToken)
            .ConfigureAwait(false);

        if (attachment is null)
        {
            throw new UnauthorizedAccessException($"Attachment {request.AttachmentId} is not visible.");
        }

        MembershipSnapshot? membership = await _memberships
            .FindGrantingAsync(attachment.ConversationId, request.RequestedBy, cancellationToken)
            .ConfigureAwait(false);

        // Identical refusal to the missing case above: a non-member must not be able to tell an
        // attachment that exists elsewhere from one that never existed.
        return membership is null
            ? throw new UnauthorizedAccessException($"Attachment {request.AttachmentId} is not visible.")
            : attachment;
    }
}

/// <summary>Opens the stored bytes of an already-authorized attachment.</summary>
/// <remarks>
/// <b>Takes an object key, not an attachment id, and performs no authorization.</b> That is
/// deliberate and the reason it is a separate request: it is only ever dispatched immediately after
/// <see cref="AuthorizeDownload"/> has returned the key, on the fallback path where no reverse
/// proxy is in front. A key is not guessable from a route, and nothing maps a client-supplied value
/// onto it.
/// </remarks>
public sealed record OpenAttachmentContent(string ObjectKey);

/// <summary>Streams stored bytes, for deployments without the nginx internal redirect.</summary>
public sealed class OpenAttachmentContentHandler : IUseCase<OpenAttachmentContent, Stream>
{
    private readonly IObjectStore _objects;

    /// <summary>Creates the handler.</summary>
    public OpenAttachmentContentHandler(IObjectStore objects)
    {
        ArgumentNullException.ThrowIfNull(objects);
        _objects = objects;
    }

    /// <inheritdoc />
    public async Task<Stream> HandleAsync(
        OpenAttachmentContent request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return await _objects.OpenReadAsync(request.ObjectKey, cancellationToken).ConfigureAwait(false);
    }
}
