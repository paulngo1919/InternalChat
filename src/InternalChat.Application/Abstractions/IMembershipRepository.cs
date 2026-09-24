using InternalChat.Domain.Conversations;

namespace InternalChat.Application.Abstractions;

/// <summary>
/// Membership persistence for the mutating paths.
/// </summary>
/// <remarks>
/// Deliberately separate from <see cref="IMembershipReader"/>, which looks similar and is not the
/// same thing. The reader answers one question — "may this employee reach this conversation?" — and
/// its implementation is cached for 30 seconds (T068). Nothing here may be served from that cache:
/// a use case that added a member and then read a stale membership list would decide who to notify
/// from state 30 seconds out of date.
/// </remarks>
public interface IMembershipRepository
{
    /// <summary>Stages a new membership for the current transaction.</summary>
    Task AddAsync(Membership membership, CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads one membership, including a removed one.
    /// </summary>
    /// <remarks>
    /// Removed rows are returned rather than filtered out, because re-adding a member has to find
    /// the old row to reuse it — <see cref="Membership.Rejoin"/> takes the higher of the old and new
    /// history floors, so a re-add that inserted a fresh row would hand back history the member was
    /// removed from.
    /// </remarks>
    Task<Membership?> FindAsync(
        Guid conversationId,
        Guid employeeId,
        CancellationToken cancellationToken = default);

    /// <summary>Every membership of a conversation, removed ones included.</summary>
    Task<IReadOnlyList<Membership>> ListForConversationAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default);
}
