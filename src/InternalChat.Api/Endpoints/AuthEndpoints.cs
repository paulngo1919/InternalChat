using InternalChat.Api.Authorization;
using InternalChat.Api.RateLimiting;
using InternalChat.Application.Abstractions;
using InternalChat.Application.Behaviors;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace InternalChat.Api.Endpoints;

/// <summary>
/// T064 — the Keycloak back-channel logout callback.
/// </summary>
/// <remarks>
/// Half of the revocation mechanism in research.md D5. When an employee signs out — or an
/// administrator ends their session in Keycloak — the identity provider posts here, and this is
/// what turns that into a refusal on the next request and a closed WebSocket on the next sweep.
/// Without it, sign-out would leave a working token in the browser for its full remaining lifetime.
/// </remarks>
public static class AuthEndpoints
{
    /// <summary>Route Keycloak posts logout tokens to. Declared in the realm export.</summary>
    public const string BackChannelLogoutRoute = "/auth/backchannel-logout";

    /// <summary>Maps the authentication callbacks.</summary>
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapPost(BackChannelLogoutRoute, BackChannelLogout)
            .WithTags("Directory")
            .WithName("BackChannelLogout")
            .WithSummary("Keycloak back-channel logout callback (research.md D5)")

            // ANONYMOUS BY NECESSITY: Keycloak calls this server-to-server with no user context,
            // so there is no token to present other than the logout token in the body. That token
            // is validated in full — signature, issuer, audience, lifetime, and the back-channel
            // logout event claim — before anything is revoked. The route is listed in
            // AuthorizationCoverageTests.JustifiedAnonymousRoutes with this reasoning.
            .AllowAnonymous()

            // Anonymous and side-effecting, so it is rate limited by IP on the authentication
            // policy. An unlimited anonymous endpoint that performs work is a free amplifier.
            .RequireRateLimiting(RateLimitPolicies.Authentication);

        return routes;
    }

    /// <summary>
    /// Revokes the session or subject named by a validated logout token.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Returns 200 for an invalid token as well as a valid one. The specification asks for 400 on a
    /// malformed logout token, but distinguishing them here would let an unauthenticated caller
    /// probe which sessions exist by reading the status code. Keycloak logs the delivery either
    /// way, and a genuinely broken integration shows up as sessions that never end — which is
    /// visible in this endpoint's own logs.
    /// </para>
    /// <para>
    /// No audit event carries an actor: the actor is the identity provider, not an employee. The
    /// subject is recorded, which is what an investigator needs.
    /// </para>
    /// </remarks>
    private static async Task<IResult> BackChannelLogout(
        HttpContext http,
        LogoutTokenValidator validator,
        IRevocationStore revocations,
        IEmployeeDirectory directory,
        ISecurityAuditor auditor,
        IOptions<ChatAuthenticationOptions> options,
        CancellationToken cancellationToken)
    {
        if (!http.Request.HasFormContentType)
        {
            return Results.Ok();
        }

        IFormCollection form = await http.Request.ReadFormAsync(cancellationToken).ConfigureAwait(false);

        LogoutRequest? request = await validator
            .ValidateAsync(form["logout_token"], cancellationToken)
            .ConfigureAwait(false);

        if (request is null)
        {
            return Results.Ok();
        }

        TimeSpan timeToLive = options.Value.MaxAccessTokenLifetime;

        if (request.SessionId is not null)
        {
            await revocations
                .RevokeSessionAsync(request.SessionId, timeToLive, cancellationToken)
                .ConfigureAwait(false);
        }
        else if (request.Subject is not null)
        {
            // No session id means "every session for this subject" — which is what Keycloak sends
            // when an administrator ends all of an employee's sessions at once.
            await revocations
                .RevokeSubjectAsync(request.Subject, timeToLive, cancellationToken)
                .ConfigureAwait(false);
        }

        if (request.Subject is not null)
        {
            EmployeeProfile? employee = await directory
                .FindBySubjectAsync(request.Subject, cancellationToken)
                .ConfigureAwait(false);

            if (employee is not null)
            {
                await auditor
                    .SignOutAsync(
                        employee.Id,
                        request.SessionId ?? "(all sessions)",
                        SignOutReason.BackChannelLogout,
                        http.Connection.RemoteIpAddress?.ToString(),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        return Results.Ok();
    }
}
