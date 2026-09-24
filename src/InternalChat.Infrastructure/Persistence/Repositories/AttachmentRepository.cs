using InternalChat.Application.Abstractions;
using InternalChat.Domain.Attachments;
using Microsoft.EntityFrameworkCore;

namespace InternalChat.Infrastructure.Persistence.Repositories;

/// <summary>
/// EF Core implementation of attachment persistence (T149).
/// </summary>
public sealed class AttachmentRepository : IAttachmentRepository
{
    private readonly ChatDbContext _context;

    /// <summary>Creates the repository.</summary>
    public AttachmentRepository(ChatDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
    }

    /// <inheritdoc />
    public async Task AddAsync(Attachment attachment, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attachment);

        await _context.Attachments.AddAsync(attachment, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Attachment?> FindAsync(
        Guid attachmentId,
        CancellationToken cancellationToken = default) =>
        await _context.Attachments
            .FirstOrDefaultAsync(a => a.Id == attachmentId, cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyList<Attachment>> GetForMessagesAsync(
        IReadOnlyCollection<Guid> messageIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messageIds);

        if (messageIds.Count == 0)
        {
            // The overwhelmingly common case on a text-only conversation. Short-circuited so a page
            // of history with no attachments costs no query at all, rather than one that returns
            // nothing — the query-count budget is per request, not per useful result.
            return [];
        }

        // AsNoTracking: these are projected straight into a response and never modified. Tracking
        // 50 messages' worth of attachments would put them all in the change tracker for a request
        // that writes nothing.
        return await _context.Attachments
            .AsNoTracking()
            .Where(a => a.MessageId != null && messageIds.Contains(a.MessageId.Value))
            .OrderBy(a => a.CreatedAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Tracked, unlike <see cref="GetForMessagesAsync"/> — the caller binds these to a message and
    /// the change must be saved.
    /// </remarks>
    public async Task<IReadOnlyList<Attachment>> GetAttachableAsync(
        Guid conversationId,
        Guid uploadedBy,
        IReadOnlyCollection<Guid> attachmentIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attachmentIds);

        if (attachmentIds.Count == 0)
        {
            return [];
        }

        return await _context.Attachments
            .Where(a => attachmentIds.Contains(a.Id)

                // Both scoping conditions are in the query, not applied afterwards. An attachment
                // from another conversation or another uploader must not be loaded at all, because
                // a caller that holds the entity is one mistake away from binding it.
                && a.ConversationId == conversationId
                && a.UploadedBy == uploadedBy

                // Already bound: AttachTo is write-once, and re-sending the same id would throw
                // rather than no-op if the row came back here.
                && a.MessageId == null)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
