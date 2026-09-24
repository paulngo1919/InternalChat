namespace InternalChat.Application.Abstractions;

/// <summary>
/// The platform-wide concurrent-participant ceiling (FR-043, FR-044).
/// </summary>
/// <remarks>
/// <para>
/// <b>A capacity guard, not an authorization decision — and the distinction is what makes a cached
/// counter acceptable here.</b> Constitution Principle VII forbids serving authorization from
/// cache, because a stale <c>yes</c> is unauthorized access. A stale capacity reading is different
/// in kind: the worst outcome is admitting a few participants over the ceiling or refusing a few
/// under it, and neither lets anyone reach content they may not see. data-model.md says so
/// explicitly: "it is a capacity guard, not an authorization decision, so cache authority here is
/// acceptable under Principle VII."
/// </para>
/// <para>
/// <b>It lives in Redis rather than in PostgreSQL</b> because it is read on every join and written
/// on every join and leave across every room — a counter with the write rate of the whole
/// platform's meeting churn, whose value is worthless the moment it is a minute old. Putting it in
/// the source of truth would be paying durability for something that must not be durable.
/// </para>
/// <para>
/// <b>Everything here is reconciled, never trusted indefinitely.</b> The counter drifts: a
/// participant whose leave webhook is lost leaves a phantom behind. <see cref="ReconcileAsync"/>
/// replaces the count with LiveKit's own figure, which is the only authority on who is actually
/// connected, and a TTL means even an unreconciled counter expires rather than accumulating
/// phantoms forever.
/// </para>
/// </remarks>
public interface IMeetingCapacityGuard
{
    /// <summary>
    /// Whether the platform can accept <paramref name="additionalParticipants"/> more.
    /// </summary>
    Task<bool> HasCapacityAsync(int additionalParticipants = 1, CancellationToken cancellationToken = default);

    /// <summary>Current platform-wide concurrent participant count.</summary>
    Task<int> GetCountAsync(CancellationToken cancellationToken = default);

    /// <summary>Records a participant joining any meeting.</summary>
    Task IncrementAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a participant leaving any meeting.
    /// </summary>
    /// <remarks>
    /// Never goes below zero. A decrement without a matching increment — a leave webhook for a join
    /// this instance never saw, which happens across a restart — would otherwise drive the counter
    /// negative and hide a genuinely full platform behind a false surplus.
    /// </remarks>
    Task DecrementAsync(CancellationToken cancellationToken = default);

    /// <summary>Replaces the count with an authoritative figure from the media server.</summary>
    Task ReconcileAsync(int actualCount, CancellationToken cancellationToken = default);
}
