using InternalChat.Domain.Attachments;

namespace InternalChat.Application.Abstractions;

/// <summary>Attachment persistence. Implemented by Infrastructure, owned by this layer.</summary>
public interface IAttachmentRepository
{
    /// <summary>Stages a newly reserved attachment for the current transaction.</summary>
    Task AddAsync(Attachment attachment, CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads one attachment by id, without reference to a conversation.
    /// </summary>
    /// <remarks>
    /// <b>Deliberately not scoped by conversation.</b> The download path receives only an
    /// attachment id, and the conversation it belongs to is precisely what the caller must not be
    /// allowed to assert — the row's own <c>ConversationId</c> is what membership is then checked
    /// against. A signature taking both would invite a caller to pass the conversation it wished
    /// the attachment were in.
    /// </remarks>
    Task<Attachment?> FindAsync(Guid attachmentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads the attachments carried by a set of messages, for rendering a page of history.
    /// </summary>
    /// <remarks>
    /// Batched by message id rather than fetched per message: a page is 50 messages, and one query
    /// per message would put the history endpoint straight over the query-count budget
    /// <c>QueryCountInterceptor</c> enforces, quite apart from its 250 ms target.
    /// </remarks>
    Task<IReadOnlyList<Attachment>> GetForMessagesAsync(
        IReadOnlyCollection<Guid> messageIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads attachments the sender reserved in this conversation and has not yet bound to a message.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every argument is a constraint the caller must not be able to bypass, which is why they are
    /// all parameters rather than a post-filter:
    /// </para>
    /// <list type="bullet">
    /// <item><paramref name="conversationId"/> — an attachment cannot cross conversations.</item>
    /// <item><paramref name="uploadedBy"/> — a member cannot attach someone else's upload.</item>
    /// </list>
    /// <para>
    /// Ids that match nothing are simply absent from the result; the caller decides whether that is
    /// a refusal.
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<Attachment>> GetAttachableAsync(
        Guid conversationId,
        Guid uploadedBy,
        IReadOnlyCollection<Guid> attachmentIds,
        CancellationToken cancellationToken = default);
}
