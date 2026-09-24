using InternalChat.Domain.Meetings;

namespace InternalChat.Application.Abstractions;

/// <summary>
/// Screen-share sessions. Implemented by Infrastructure, owned by this layer.
/// </summary>
/// <remarks>
/// Separate from <c>IMeetingRepository</c> rather than folded into it, because a share session has
/// a different lifetime and a much higher write rate than the meeting it belongs to: people start
/// and stop sharing repeatedly within one call. Loading a meeting's whole share history to add one
/// row would make every claim proportional to how much sharing had already happened.
/// </remarks>
public interface IShareSessionStore
{
    /// <summary>
    /// The live share in a meeting, or <c>null</c> when nobody is sharing.
    /// </summary>
    /// <remarks>
    /// Singular, and that signature IS the FR-050 rule. A method returning a list would make "two
    /// people are sharing" a representable state the caller has to decide what to do about; there
    /// is one slot, and this either finds it occupied or not.
    /// </remarks>
    Task<ShareSession?> FindActiveAsync(Guid meetingId, CancellationToken cancellationToken = default);

    /// <summary>Stages a new share session.</summary>
    Task AddAsync(ShareSession session, CancellationToken cancellationToken = default);

    /// <summary>Persists a change to an existing session — in practice, its stop.</summary>
    Task UpdateAsync(ShareSession session, CancellationToken cancellationToken = default);

    /// <summary>
    /// Total seconds each participant spent sharing in a meeting (FR-051).
    /// </summary>
    /// <remarks>
    /// Summed in the database rather than by loading sessions, because this is read when a meeting
    /// ends and a long call can hold dozens of short shares.
    /// </remarks>
    Task<IReadOnlyDictionary<Guid, int>> SumSharedSecondsAsync(
        Guid meetingId,
        CancellationToken cancellationToken = default);
}
