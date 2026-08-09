using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;

namespace InternalChat.Api.Authorization;

/// <summary>One open hub connection, and what is needed to decide whether it may stay open.</summary>
/// <param name="ConnectionId">SignalR connection id.</param>
/// <param name="Subject">Token <c>sub</c>. The employee's <c>external_subject</c>.</param>
/// <param name="SessionId">Token <c>sid</c>, when the token carried one.</param>
/// <param name="ExpiresAt">
/// Token expiry. Held so the sweep can close a connection whose token has run out without needing
/// to re-parse it.
/// </param>
/// <param name="Context">The live connection, so it can be aborted.</param>
public sealed record OpenConnection(
    string ConnectionId,
    string Subject,
    string? SessionId,
    DateTimeOffset ExpiresAt,
    HubCallerContext Context);

/// <summary>
/// Tracks open hub connections so they can be re-evaluated on a timer.
/// </summary>
/// <remarks>
/// <para>
/// SignalR does not offer a way to enumerate connections, and there is a good reason it does not:
/// at 7,000 concurrent connections a naive registry is 7,000 objects of avoidable state. This one
/// is kept deliberately small — five fields per connection, no user data, no buffered messages —
/// because the alternative is failing FR-003 for idle connections, which is the case SC-018
/// actually tests.
/// </para>
/// <para>
/// <b>Per process, not shared.</b> Each API replica tracks its own connections and sweeps them
/// against the shared Redis revocation set. A replica can only abort a socket it holds, so there is
/// nothing to coordinate — and putting this in Redis would add a distributed data structure to
/// solve a problem that does not cross a process boundary.
/// </para>
/// </remarks>
public sealed class HubConnectionRegistry
{
    private readonly ConcurrentDictionary<string, OpenConnection> _connections =
        new(StringComparer.Ordinal);

    /// <summary>How many connections this process is holding. Exposed for metrics and tests.</summary>
    public int Count => _connections.Count;

    /// <summary>
    /// Records a connection, or returns <c>false</c> when its principal cannot be identified.
    /// </summary>
    /// <remarks>
    /// A connection with no subject is not registered and must not be allowed to proceed. It could
    /// never be swept — there would be nothing to look up in the revocation set — so it would be
    /// exactly the immortal connection FR-003 exists to prevent.
    /// </remarks>
    public bool TryAdd(HubCallerContext context, ClaimsPrincipal? principal)
    {
        ArgumentNullException.ThrowIfNull(context);

        string? subject = ChatClaims.SubjectOf(principal);
        if (subject is null)
        {
            return false;
        }

        // No `exp` means a token that never expires. Treated as already expired rather than
        // trusted: the sweep would otherwise never close it.
        DateTimeOffset expiresAt = ChatClaims.ExpiresAtOf(principal) ?? DateTimeOffset.MinValue;

        _connections[context.ConnectionId] = new OpenConnection(
            context.ConnectionId,
            subject,
            ChatClaims.SessionIdOf(principal),
            expiresAt,
            context);

        return true;
    }

    /// <summary>Forgets a connection that has closed.</summary>
    public void Remove(string connectionId) => _connections.TryRemove(connectionId, out _);

    /// <summary>Snapshot of the currently open connections.</summary>
    /// <remarks>
    /// A copy, so the sweep can abort connections — which removes them — without invalidating what
    /// it is iterating.
    /// </remarks>
    public IReadOnlyList<OpenConnection> Snapshot() => [.. _connections.Values];
}
