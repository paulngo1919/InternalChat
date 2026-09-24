using InternalChat.Api.Authorization;
using InternalChat.Api.Contracts;
using InternalChat.Application.Abstractions;
using InternalChat.Application.Behaviors;
using Microsoft.Extensions.Options;

namespace InternalChat.Api.Endpoints;

/// <summary>
/// T069, T070 — the signed-in employee's own profile and sessions.
/// </summary>
public static class MeEndpoints
{
    /// <summary>Maps <c>/me</c> and <c>/me/sessions</c>.</summary>
    public static IEndpointRouteBuilder MapMeEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        // `/me` is mapped directly rather than as the root of a `/me` group. A group's root route
        // comes out as "/api/v1/me/" with a trailing slash — a different route from the
        // "/api/v1/me" the contract documents, so a generated client calls the documented one and
        // gets a 404. The contract suite caught exactly that.
        routes.MapGet("/me", GetMe)
            .WithTags("Directory")
            .WithName("GetCurrentEmployee")
            .WithSummary("Current employee profile and notification capability")
            .RequireAuthorization(AuthorizationPolicies.Employee);

        routes.MapGet("/me/sessions", GetSessions)
            .WithTags("Directory")
            .WithName("GetCurrentEmployeeSessions")
            .WithSummary("List this employee's active sessions (FR-005)")
            .RequireAuthorization(AuthorizationPolicies.Employee);

        routes.MapDelete("/me/sessions/{sessionId}", DeleteSession)
            .WithTags("Directory")
            .WithName("RevokeCurrentEmployeeSession")
            .WithSummary("Revoke one of this employee's own sessions (FR-005)")
            .RequireAuthorization(AuthorizationPolicies.Employee);

        return routes;
    }

    /// <summary>
    /// The signed-in employee.
    /// </summary>
    /// <remarks>
    /// <c>canReceiveNotifications</c> reflects whether this employee holds at least one live push
    /// subscription (T130, T136) — the half of FR-040 the server can actually know. It says nothing
    /// about browser permission state or installation, which only the client can see
    /// (<c>NotificationCapability.tsx</c>, T139).
    /// </remarks>
    private static async Task<IResult> GetMe(
        CurrentEmployee currentEmployee,
        IPushSubscriptionStore subscriptions,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        EmployeeProfile employee = currentEmployee.Profile!;

        bool hasSubscription = await subscriptions
            .HasAnyAsync(employee.Id, cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(new CurrentEmployeeResponse(
            employee.Id,
            employee.DisplayName,
            employee.Email,
            employee.AvatarUrl,
            http.User.IsInRole(AuthorizationPolicies.AdminRole),
            CanReceiveNotifications: hasSubscription,
            hasSubscription ? null : NotificationBlockReasons.NoSubscription));
    }

    /// <summary>Sessions this platform has seen for the caller (FR-005).</summary>
    private static async Task<IResult> GetSessions(
        CurrentEmployee currentEmployee,
        ISessionRegistry sessions,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<SessionDescriptor> live = await sessions
            .ListAsync(currentEmployee.Id, cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(live
            .Select(s => new SessionResponse(
                s.Id,
                s.UserAgent,
                s.CreatedAt,
                s.LastSeenAt,

                // Compared against the caller's own token, so the flag is a fact about this
                // request rather than a guess from recency.
                IsCurrent: string.Equals(s.Id, currentEmployee.SessionId, StringComparison.Ordinal)))
            .ToArray());
    }

    /// <summary>
    /// Ends one of the caller's own sessions.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Own sessions only.</b> The employee id comes from the resolved identity and never from the
    /// request, so there is no id to tamper with — an employee cannot end a colleague's session by
    /// guessing a session id, because the lookup is scoped to their own list before anything is
    /// revoked.
    /// </para>
    /// <para>
    /// Forgetting the session is not enough: the token in the browser stays cryptographically valid
    /// for up to its remaining lifetime. Revocation is what actually ends it, and a 404 for an
    /// unknown session is returned <em>without</em> revoking anything, so a caller cannot use this
    /// endpoint to revoke session ids belonging to someone else by guessing.
    /// </para>
    /// </remarks>
    private static async Task<IResult> DeleteSession(
        string sessionId,
        CurrentEmployee currentEmployee,
        ISessionRegistry sessions,
        IRevocationStore revocations,
        ISecurityAuditor auditor,
        IOptions<ChatAuthenticationOptions> options,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        bool wasKnown = await sessions
            .RemoveAsync(currentEmployee.Id, sessionId, cancellationToken)
            .ConfigureAwait(false);

        if (!wasKnown)
        {
            return Results.NotFound();
        }

        await revocations
            .RevokeSessionAsync(sessionId, options.Value.MaxAccessTokenLifetime, cancellationToken)
            .ConfigureAwait(false);

        await auditor
            .SignOutAsync(
                currentEmployee.Id,
                sessionId,
                SignOutReason.SelfService,
                http.Connection.RemoteIpAddress?.ToString(),
                cancellationToken)
            .ConfigureAwait(false);

        return Results.NoContent();
    }
}
