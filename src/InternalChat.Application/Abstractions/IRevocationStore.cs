namespace InternalChat.Application.Abstractions;

/// <summary>
/// The set of identities whose still-valid tokens must no longer be honoured.
/// </summary>
/// <remarks>
/// <para>
/// research.md D5. FR-003 requires access to end within five minutes; the constitution caps access
/// tokens at fifteen. A token cannot be un-issued, so the realm shortens its life to five minutes
/// to bound the worst case and this store closes the remaining window. Neither half is sufficient
/// alone — a five-minute token still works for five minutes, and a revocation check nobody consults
/// on an open WebSocket does nothing at all.
/// </para>
/// <para>
/// Revocation is expressed two ways because two different things revoke. <b>Signing out</b> ends
/// one session and leaves the employee's other devices alone, so it is keyed by session id.
/// <b>Deactivation</b> ends every session the employee has, including ones this process has never
/// seen, so it is keyed by subject. Offering only the session form would mean directory sync had to
/// enumerate sessions it cannot know about.
/// </para>
/// <para>
/// Redis-backed and therefore not authoritative (Principle VII). Losing the keyspace re-admits a
/// revoked token for at most its remaining lifetime, which is why the five-minute token lifetime is
/// the mechanism that actually bounds FR-003 and this store is the mechanism that usually beats it.
/// </para>
/// </remarks>
public interface IRevocationStore
{
    /// <summary>Revokes one session — sign-out and back-channel logout.</summary>
    /// <param name="sessionId">The token's <c>sid</c> claim.</param>
    /// <param name="timeToLive">
    /// How long the entry must outlive any token that could still carry this session. Callers pass
    /// the maximum access-token lifetime; a shorter value would let a revoked token become valid
    /// again before it expired.
    /// </param>
    Task RevokeSessionAsync(string sessionId, TimeSpan timeToLive, CancellationToken cancellationToken = default);

    /// <summary>Revokes every session belonging to a subject — deactivation (FR-003).</summary>
    /// <param name="subject">The token's <c>sub</c> claim, which is <c>employee.external_subject</c>.</param>
    /// <param name="timeToLive">As for <see cref="RevokeSessionAsync"/>.</param>
    Task RevokeSubjectAsync(string subject, TimeSpan timeToLive, CancellationToken cancellationToken = default);

    /// <summary>Restores a subject on rehire, so a reactivated employee is not locked out until the entry expires.</summary>
    Task ClearSubjectAsync(string subject, CancellationToken cancellationToken = default);

    /// <summary>
    /// True when this token must be refused.
    /// </summary>
    /// <remarks>
    /// <b>An unavailable store returns <c>false</c>, not an exception.</b> That is the one place in
    /// this system where a dependency failure resolves towards access rather than away from it, and
    /// it is deliberate: the token has already been cryptographically validated and has at most five
    /// minutes left, so the exposure is bounded and known. Failing closed here would sign every
    /// employee out of the platform the moment Redis restarted, which SC-024 explicitly forbids —
    /// a cache-tier outage may cost latency, not availability. Contrast
    /// <see cref="Authorization.IConversationMembershipEvaluator"/>, which fails closed because
    /// nothing has been proven there at all.
    /// </remarks>
    Task<bool> IsRevokedAsync(string subject, string? sessionId, CancellationToken cancellationToken = default);
}
