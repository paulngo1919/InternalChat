using InternalChat.Domain.Notifications;

namespace InternalChat.Application.Abstractions;

/// <summary>Persistence for <see cref="ReadState"/> (FR-036).</summary>
public interface IReadStateRepository
{
    /// <summary>Loads one employee's read position for one conversation, tracked, or <c>null</c>.</summary>
    Task<ReadState?> FindAsync(
        Guid employeeId,
        Guid conversationId,
        CancellationToken cancellationToken = default);

    /// <summary>Stages a first read position for the current transaction.</summary>
    Task AddAsync(ReadState readState, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every conversation's unread count for one employee, computed rather than read row by row.
    /// </summary>
    /// <remarks>
    /// <c>last_seq - GREATEST(last_read_seq, visible_from_seq)</c> per conversation (data-model.md),
    /// summed for the digest job — one query rather than one per conversation, which at 10,000
    /// employees each in a handful of conversations is the difference between a sweep and an
    /// incident.
    /// </remarks>
    Task<UnreadSummary> SummariseUnreadAsync(Guid employeeId, CancellationToken cancellationToken = default);
}

/// <summary>An employee's total backlog, for the digest job (FR-038).</summary>
/// <param name="TotalUnread">Sum of unread counts across every conversation, floored at zero each.</param>
/// <param name="ConversationCount">How many conversations contribute at least one unread message.</param>
public sealed record UnreadSummary(long TotalUnread, int ConversationCount);
