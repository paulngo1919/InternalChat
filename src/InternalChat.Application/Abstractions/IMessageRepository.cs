using InternalChat.Domain.Messages;

namespace InternalChat.Application.Abstractions;

/// <summary>
/// Which message already holds a client message key.
/// </summary>
/// <param name="SentAt">
/// Carried because <c>message</c>'s primary key is <c>(id, sent_at)</c>. Without it, reading the
/// message back would have to scan every monthly partition — on a table holding 125 million rows,
/// on the send path, for what is supposed to be the cheap answer.
/// </param>
public sealed record ClaimedMessage(Guid MessageId, DateTimeOffset SentAt);

/// <summary>
/// One page of history, as the query needs to express it.
/// </summary>
/// <param name="VisibleFromSeq">
/// The caller's history floor. Passed in and applied inside the query, never filtered afterwards —
/// filtering a fetched page would make the page size depend on how much of it the caller may see,
/// so a member with a high floor would get short pages and eventually an empty one that looks like
/// the end of the conversation.
/// </param>
/// <param name="BeforeSeq">Exclusive upper bound, for paging backwards. Newest first.</param>
/// <param name="AfterSeq">Exclusive lower bound, for catching up. Oldest first.</param>
public sealed record MessageHistoryQuery(
    Guid ConversationId,
    long VisibleFromSeq,
    long? BeforeSeq,
    long? AfterSeq,
    int Limit);

/// <summary>Message persistence. Implemented by Infrastructure, owned by this layer.</summary>
public interface IMessageRepository
{
    /// <summary>Stages a new message for the current transaction.</summary>
    Task AddAsync(Message message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads one message from a conversation.
    /// </summary>
    /// <param name="sentAt">
    /// The partition key when known. Supplying it turns a scan of every partition into a single
    /// primary-key lookup; <c>null</c> is for callers that genuinely only have an id, such as an
    /// edit request naming a message the client saw days ago.
    /// </param>
    Task<Message?> FindAsync(
        Guid conversationId,
        Guid messageId,
        DateTimeOffset? sentAt = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads a page of history, ordered and bounded entirely in SQL.
    /// </summary>
    Task<IReadOnlyList<Message>> GetHistoryAsync(
        MessageHistoryQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Claims a client message key for a message about to be inserted.
    /// </summary>
    /// <returns>
    /// <c>null</c> when the key was claimed and the caller may proceed, or the
    /// <see cref="ClaimedMessage"/> that already holds it.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>This is FR-011.</b> Implemented as
    /// <c>INSERT ... ON CONFLICT DO NOTHING</c> against <c>message_dedup</c>, whose primary key is
    /// <c>(conversation_id, client_message_key)</c>. A <c>SELECT</c>-then-<c>INSERT</c> would not do:
    /// two clicks racing would both find nothing and both insert, which is the one outcome the
    /// requirement forbids.
    /// </para>
    /// <para>
    /// Called <em>before</em> the sequence is allocated, deliberately. A duplicate that had already
    /// consumed a sequence would leave a permanent gap, and a gap makes "everything above seq N"
    /// ambiguous for every client that reconnects afterwards.
    /// </para>
    /// </remarks>
    Task<ClaimedMessage?> TryClaimClientKeyAsync(
        Guid conversationId,
        ClientMessageKey clientMessageKey,
        Guid messageId,
        DateTimeOffset sentAt,
        CancellationToken cancellationToken = default);
}
