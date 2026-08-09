using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace InternalChat.Api.Hubs;

/// <summary>
/// The real-time hub (<c>contracts/signalr-hub.md</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>US1 scope only.</b> The connection lifecycle exists here because FR-003 requires an open
/// connection to stop working within five minutes of deactivation, and that cannot be built or
/// tested without a connection to close. The client-callable methods — <c>Resync</c>,
/// <c>StartTyping</c>, <c>StopTyping</c>, <c>SetPresence</c> — arrive with US2 (T097), along with
/// group membership and fan-out.
/// </para>
/// <para>
/// The hub holds no business logic and never will: Constitution Principle I requires every method
/// to delegate to an Application use case. What it does hold is <see cref="AuthorizeAttribute"/>,
/// without which the hub accepts any connection —
/// <c>tests/Architecture/AuthorizationCoverageTests.cs</c> fails the build if it is removed.
/// </para>
/// <para>
/// Authentication is not the whole story here. <c>[Authorize]</c> is checked once, at connect; the
/// re-checking that FR-003 needs is
/// <see cref="Authorization.HubAuthorizationFilter"/> plus
/// <see cref="Authorization.RevocationSweepService"/>.
/// </para>
/// </remarks>
[Authorize]
public sealed class ChatHub : Hub
{
    /// <summary>Path this hub is mapped at.</summary>
    public const string Path = Authorization.AuthenticationExtensions.HubPath;
}
