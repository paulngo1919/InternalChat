using System.Globalization;
using InternalChat.Api.Authorization;
using InternalChat.Api.Contracts;
using InternalChat.Application.Abstractions;
using InternalChat.Application.Behaviors;
using InternalChat.Application.Conversations;
using InternalChat.Application.Notifications;
using InternalChat.Domain.Conversations;
using InternalChat.Domain.Notifications;

namespace InternalChat.Api.Endpoints;

/// <summary>
/// T136 — read-state, preference, subscription, and mute endpoints (FR-034, FR-036–FR-038).
/// </summary>
/// <remarks>
/// The mute endpoint is not in the original openapi.yaml: <c>Membership.MutedUntil</c> (T059) and
/// the contract's read-side <c>Conversation.mutedUntil</c> both existed with nothing to write
/// either — see <c>MuteConversation</c>'s remarks. Added here and in the document together, the
/// same correction pattern T080 and T088 record for earlier gaps found the same way.
/// </remarks>
public static class NotificationEndpoints
{
    /// <summary>Maps the notification and read-state endpoints.</summary>
    public static IEndpointRouteBuilder MapNotificationEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapPut("/conversations/{conversationId:guid}/read-state", MarkConversationRead)
            .WithTags("Notifications")
            .WithName("MarkConversationRead")
            .WithSummary("Advance read position (FR-036)")
            .RequireAuthorization(AuthorizationPolicies.ConversationMember)
            .RequireConversationMembership();

        routes.MapPut("/conversations/{conversationId:guid}/mute", MuteConversationEndpoint)
            .WithTags("Notifications")
            .WithName("MuteConversation")
            .WithSummary("Mute or unmute this conversation for the caller (FR-037)")
            .RequireAuthorization(AuthorizationPolicies.ConversationMember)
            .RequireConversationMembership();

        routes.MapGet("/notifications/preferences", GetPreferencesEndpoint)
            .WithTags("Notifications")
            .WithName("GetNotificationPreferences")
            .WithSummary("Get do-not-disturb and digest preferences (FR-037, FR-038)")
            .RequireAuthorization(AuthorizationPolicies.Employee);

        routes.MapPut("/notifications/preferences", UpdatePreferencesEndpoint)
            .WithTags("Notifications")
            .WithName("UpdateNotificationPreferences")
            .WithSummary("Update preferences")
            .RequireAuthorization(AuthorizationPolicies.Employee);

        routes.MapPost("/notifications/subscriptions", RegisterSubscription)
            .WithTags("Notifications")
            .WithName("RegisterPushSubscription")
            .WithSummary("Register this browser for push (FR-034)")
            .RequireAuthorization(AuthorizationPolicies.Employee);

        routes.MapGet("/notifications/vapid-public-key", GetVapidPublicKey)
            .WithTags("Notifications")
            .WithName("GetVapidPublicKey")
            .WithSummary("The VAPID public key a browser needs to create a push subscription (FR-034)")
            .RequireAuthorization(AuthorizationPolicies.Employee);

        return routes;
    }

    private static async Task<IResult> MarkConversationRead(
        Guid conversationId,
        MarkReadRequest request,
        CurrentEmployee currentEmployee,
        IUseCaseDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        MarkReadResult result = await dispatcher
            .SendAsync<MarkRead, MarkReadResult>(
                new MarkRead(currentEmployee.Id, conversationId, request.LastReadSeq),
                cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(new ReadStateResponse(result.ConversationId, result.LastReadSeq, result.UnreadCount));
    }

    private static async Task<IResult> MuteConversationEndpoint(
        Guid conversationId,
        MuteConversationRequest request,
        CurrentEmployee currentEmployee,
        IUseCaseDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        await dispatcher
            .SendAsync<MuteConversation, Membership>(
                new MuteConversation(conversationId, currentEmployee.Id, request.MutedUntil),
                cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(new { mutedUntil = request.MutedUntil });
    }

    private static async Task<IResult> GetPreferencesEndpoint(
        CurrentEmployee currentEmployee,
        IUseCaseDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        NotificationPreference preference = await dispatcher
            .SendAsync<GetPreferences, NotificationPreference>(
                new GetPreferences(currentEmployee.Id),
                cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(ToResponse(preference));
    }

    private static async Task<IResult> UpdatePreferencesEndpoint(
        NotificationPreferencesResponse request,
        CurrentEmployee currentEmployee,
        IUseCaseDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!TryParseTimeOfDay(request.DndStart, out TimeOnly? dndStart)
            || !TryParseTimeOfDay(request.DndEnd, out TimeOnly? dndEnd))
        {
            return Results.BadRequest(new { error = "dndStart and dndEnd must be 'HH:mm' or null." });
        }

        NotificationPreference preference = await dispatcher
            .SendAsync<UpdatePreferences, NotificationPreference>(
                new UpdatePreferences(
                    currentEmployee.Id, dndStart, dndEnd, request.TimeZone, request.DigestAfterMinutes),
                cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(ToResponse(preference));
    }

    private static async Task<IResult> RegisterSubscription(
        PushSubscriptionRequest request,
        CurrentEmployee currentEmployee,
        IUseCaseDispatcher dispatcher,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!Uri.TryCreate(request.Endpoint, UriKind.Absolute, out Uri? endpoint))
        {
            return Results.BadRequest(new { error = "endpoint must be an absolute URL." });
        }

        await dispatcher
            .SendAsync<RegisterPushSubscription, bool>(
                new RegisterPushSubscription(
                    currentEmployee.Id,
                    endpoint,
                    request.P256dh,
                    request.Auth,
                    http.Request.Headers.UserAgent.ToString()),
                cancellationToken)
            .ConfigureAwait(false);

        // 201 with no body and no Location — openapi.yaml documents only the status. There is no
        // sub-resource URI for a subscription to expose; the endpoint is register-only.
        return Results.StatusCode(StatusCodes.Status201Created);
    }

    private static IResult GetVapidPublicKey(IVapidPublicKeyProvider vapid) =>
        Results.Ok(new { publicKey = vapid.PublicKey });

    private static NotificationPreferencesResponse ToResponse(NotificationPreference preference) => new(
        FormatTimeOfDay(preference.DndStart),
        FormatTimeOfDay(preference.DndEnd),
        preference.TimeZoneId,
        preference.DigestAfterMinutes);

    private static string? FormatTimeOfDay(TimeOnly? value) =>
        value?.ToString("HH:mm", CultureInfo.InvariantCulture);

    private static bool TryParseTimeOfDay(string? value, out TimeOnly? parsed)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            parsed = null;
            return true;
        }

        if (TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out TimeOnly time))
        {
            parsed = time;
            return true;
        }

        parsed = null;
        return false;
    }
}
