using InternalChat.Api.Authorization;
using InternalChat.Api.Contracts;
using InternalChat.Api.Mapping;
using InternalChat.Application.Behaviors;
using InternalChat.Application.Meetings;
using InternalChat.Domain.Meetings;

namespace InternalChat.Api.Endpoints;

/// <summary>
/// T190 — start a meeting and mint a join token (FR-041 – FR-044).
/// </summary>
/// <remarks>
/// <para>
/// <b>The two routes are authorized differently, for the same reason the attachment routes are.</b>
/// Starting names a conversation, so the membership filter applies. The token route names only a
/// meeting — the conversation is a property of the row — so the filter cannot run and the check
/// happens inside <see cref="IssueJoinTokenHandler"/> against the conversation the meeting actually
/// belongs to. Routing it through the filter would mean trusting a caller-supplied conversation id,
/// which is the one input that must not be trusted here.
/// </para>
/// <para>
/// <b>Both 503s carry <c>retryAfterSeconds</c> and say messaging is unaffected.</b> The whole point
/// of the two-host split is that a media-host failure degrades one feature; a client that cannot
/// tell "meetings are down" from "the platform is down" will tell its user the wrong thing.
/// </para>
/// </remarks>
public static class MeetingEndpoints
{
    /// <summary>Maps the meeting endpoints.</summary>
    public static IEndpointRouteBuilder MapMeetingEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapPost("/conversations/{conversationId:guid}/meetings", StartMeeting)
            .WithTags("Meetings")
            .WithName("StartMeeting")
            .WithSummary("Start a meeting in a conversation (FR-041)")
            .RequireAuthorization(AuthorizationPolicies.ConversationMember)
            .RequireConversationMembership();

        routes.MapPost("/meetings/{meetingId:guid}/token", IssueToken)
            .WithTags("Meetings")
            .WithName("IssueMeetingToken")
            .WithSummary("Mint a short-lived join token; this endpoint IS the access control (FR-041)")

            // No membership filter: see the class remarks. The conversation is on the meeting row.
            .RequireAuthorization(AuthorizationPolicies.Employee);

        routes.MapPost("/meetings/{meetingId:guid}/share", StartShare)
            .WithTags("Meetings")
            .WithName("StartScreenShare")
            .WithSummary("Claim the screen-share slot; last writer wins and the displaced sharer is named (FR-050)")
            .RequireAuthorization(AuthorizationPolicies.Employee);

        routes.MapDelete("/meetings/{meetingId:guid}/share", StopShare)
            .WithTags("Meetings")
            .WithName("StopScreenShare")
            .WithSummary("Release the screen-share slot")
            .RequireAuthorization(AuthorizationPolicies.Employee);

        return routes;
    }

    /// <summary>
    /// Claims the screen-share slot, displacing whoever held it (FR-050).
    /// </summary>
    /// <remarks>
    /// Server-side because the browser will happily let two people publish a screen-share track at
    /// once and LiveKit will forward both — producing two shared screens, which is neither of the
    /// outcomes FR-050 permits.
    /// </remarks>
    private static async Task<IResult> StartShare(
        Guid meetingId,
        StartScreenShareRequest request,
        CurrentEmployee currentEmployee,
        IUseCaseDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        ScreenShareClaim claim = await dispatcher
            .SendAsync<StartScreenShare, ScreenShareClaim>(
                new StartScreenShare(meetingId, currentEmployee.Id, ParseScope(request.Scope)),
                cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(new ScreenShareResponse(
            claim.Session.MeetingId,
            claim.Session.EmployeeId,
            ToWire(claim.Session.Scope),
            claim.Session.StartedAt,
            claim.Displaced));
    }

    /// <summary>Releases the slot, if this caller holds it.</summary>
    private static async Task<IResult> StopShare(
        Guid meetingId,
        CurrentEmployee currentEmployee,
        IUseCaseDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        await dispatcher
            .SendAsync<StopScreenShare, bool>(
                new StopScreenShare(meetingId, currentEmployee.Id),
                cancellationToken)
            .ConfigureAwait(false);

        // 204 whether or not a share was actually stopped. Someone who was superseded still sends a
        // stop when they close their picker, and turning that into a 404 would surface an error
        // nobody caused and nobody can act on.
        return Results.NoContent();
    }

    /// <summary>
    /// Maps the wire spelling to the domain enum.
    /// </summary>
    /// <remarks>
    /// Explicit rather than <c>Enum.TryParse</c>, which is case-insensitive by request and also
    /// accepts <c>"1"</c> and <c>"2"</c> — so a client sending the ordinal would silently get a
    /// scope it never named, and FR-048's window promise would be recorded for a whole-screen share.
    /// </remarks>
    private static ShareScope ParseScope(string? scope) => scope switch
    {
        ShareScopes.Screen => ShareScope.Screen,
        ShareScopes.Window => ShareScope.Window,
        _ => throw new ArgumentOutOfRangeException(
            nameof(scope), scope, "A share scope is 'screen' or 'window'."),
    };

    private static string ToWire(ShareScope scope) => scope switch
    {
        ShareScope.Screen => ShareScopes.Screen,
        ShareScope.Window => ShareScopes.Window,
        _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unknown share scope."),
    };

    /// <summary>Starts a meeting, or returns the one already running.</summary>
    private static async Task<IResult> StartMeeting(
        Guid conversationId,
        CurrentEmployee currentEmployee,
        IUseCaseDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        StartMeetingResult result = await dispatcher
            .SendAsync<Application.Meetings.StartMeeting, StartMeetingResult>(
                new Application.Meetings.StartMeeting(conversationId, currentEmployee.Id),
                cancellationToken)
            .ConfigureAwait(false);

        MeetingResponse body = result.Meeting.ToResponse();

        // 201 for a new meeting, 200 for joining one already running. The distinction tells a
        // client whether to announce "you started a meeting" or "you joined one".
        return result.WasCreated
            ? Results.Created($"/api/v1/meetings/{result.Meeting.Id}", body)
            : Results.Ok(body);
    }

    /// <summary>Issues a join token after the membership check that is the access control.</summary>
    private static async Task<IResult> IssueToken(
        Guid meetingId,
        CurrentEmployee currentEmployee,
        IUseCaseDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        JoinTicket ticket = await dispatcher
            .SendAsync<IssueJoinToken, JoinTicket>(
                new IssueJoinToken(meetingId, currentEmployee.Id),
                cancellationToken)
            .ConfigureAwait(false);

        return Results.Created(
            $"/api/v1/meetings/{meetingId}/token",
            new MeetingTokenResponse(
                ticket.Token.Token,
                ticket.Token.MediaServerUrl.ToString(),
                ticket.Token.ExpiresAt));
    }
}
