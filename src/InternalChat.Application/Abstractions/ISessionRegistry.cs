namespace InternalChat.Application.Abstractions;

/// <summary>One of an employee's live sessions, as shown by <c>GET /me/sessions</c>.</summary>
/// <param name="Id">The token's <c>sid</c> claim.</param>
/// <param name="UserAgent">Browser string captured at first sight, for recognisability.</param>
/// <param name="CreatedAt">When this session was first seen by the platform.</param>
/// <param name="LastSeenAt">When it last presented a token.</param>
public sealed record SessionDescriptor(
    string Id,
    string UserAgent,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastSeenAt);

/// <summary>
/// Tracks which sessions an employee currently has, so FR-005 can show and end them.
/// </summary>
/// <remarks>
/// <para>
/// FR-005 requires an employee to see their own active sessions and end any of them. The identity
/// provider knows this too, but reading it would put a Keycloak admin round trip on a
/// user-facing path and require the API to hold admin credentials — a large privilege for a
/// read-only list.
/// </para>
/// <para>
/// So the platform records what it observes: every authenticated request refreshes the entry for
/// its own session. This makes the list "sessions that have used the chat platform recently",
/// which is what an employee checking for an unrecognised device actually wants to know.
/// </para>
/// <para>
/// Derived state with a TTL (Principle VII). Losing it empties the list until sessions are seen
/// again; it can never cost an authorization decision, because ending a session goes through
/// <see cref="IRevocationStore"/>, not through here.
/// </para>
/// </remarks>
public interface ISessionRegistry
{
    /// <summary>
    /// Records that a session is active, and reports whether this was the first time it was seen.
    /// </summary>
    /// <returns>
    /// <c>true</c> when the session was not previously registered. The caller uses this to emit
    /// exactly one <c>auth.signin</c> audit event per session rather than one per request (FR-006) —
    /// an audit log with a row for every HTTP call is an audit log nobody can read.
    /// </returns>
    Task<bool> TouchAsync(
        Guid employeeId,
        string sessionId,
        string userAgent,
        TimeSpan timeToLive,
        CancellationToken cancellationToken = default);

    /// <summary>Lists the employee's currently known sessions, newest first.</summary>
    Task<IReadOnlyList<SessionDescriptor>> ListAsync(Guid employeeId, CancellationToken cancellationToken = default);

    /// <summary>Forgets one session. Returns <c>false</c> when it was not registered.</summary>
    /// <remarks>
    /// Forgetting is not revoking. The caller MUST also call
    /// <see cref="IRevocationStore.RevokeSessionAsync"/> — removing the row alone would drop the
    /// session from a list while its token kept working, which is the worst of both outcomes.
    /// </remarks>
    Task<bool> RemoveAsync(Guid employeeId, string sessionId, CancellationToken cancellationToken = default);
}
