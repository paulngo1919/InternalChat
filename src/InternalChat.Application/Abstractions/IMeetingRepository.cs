using InternalChat.Domain.Meetings;

namespace InternalChat.Application.Abstractions;

/// <summary>Meeting persistence. Implemented by Infrastructure, owned by this layer.</summary>
public interface IMeetingRepository
{
    /// <summary>Stages a new meeting for the current transaction.</summary>
    Task AddAsync(Meeting meeting, CancellationToken cancellationToken = default);

    /// <summary>
    /// The live meeting in a conversation, or <c>null</c> when there is none.
    /// </summary>
    /// <remarks>
    /// Singular by design. One live meeting per conversation is what stops two people clicking
    /// "start" and producing two rooms with half the participants in each — a state the data model
    /// permits and the application does not.
    /// </remarks>
    Task<Meeting?> FindActiveForConversationAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// One meeting by id, with its participations loaded.
    /// </summary>
    /// <remarks>
    /// <b>Not scoped by conversation</b>, for the same reason <c>IAttachmentRepository.FindAsync</c>
    /// is not: a join request and a webhook both name only a meeting, and the conversation it
    /// belongs to is precisely what the caller must not be allowed to assert. The row's own
    /// <c>ConversationId</c> is what membership is then checked against.
    /// </remarks>
    Task<Meeting?> FindAsync(Guid meetingId, CancellationToken cancellationToken = default);
}
