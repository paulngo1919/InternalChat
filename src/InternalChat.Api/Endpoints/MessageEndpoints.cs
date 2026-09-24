using InternalChat.Api.Authorization;
using InternalChat.Api.Contracts;
using InternalChat.Api.Mapping;
using InternalChat.Api.RateLimiting;
using InternalChat.Application.Behaviors;
using InternalChat.Application.Messages;
using InternalChat.Domain.Messages;

namespace InternalChat.Api.Endpoints;

/// <summary>
/// T096 — send, read, edit, and delete messages (FR-009 through FR-014).
/// </summary>
/// <remarks>
/// Every route here carries the membership filter. That is the resource-scoped half of Principle IV:
/// the policy establishes that the caller is an authenticated employee, and the filter ties the
/// request to a live membership row in the conversation named in the route.
/// </remarks>
public static class MessageEndpoints
{
    /// <summary>Maps the message endpoints.</summary>
    public static IEndpointRouteBuilder MapMessageEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapGet("/conversations/{conversationId:guid}/messages", GetHistory)
            .WithTags("Messages")
            .WithName("GetMessageHistory")
            .WithSummary("Keyset-paginated history (FR-013)")
            .RequireAuthorization(AuthorizationPolicies.ConversationMember)
            .RequireConversationMembership();

        routes.MapPost("/conversations/{conversationId:guid}/messages", SendMessage)
            .WithTags("Messages")
            .WithName("SendMessage")
            .WithSummary("Send a message, idempotent on clientMessageKey (FR-009, FR-011)")
            .RequireAuthorization(AuthorizationPolicies.ConversationMember)
            .RequireConversationMembership()

            // Token bucket rather than a fixed window: typing three messages in a row is a
            // legitimate burst, and a fixed window would refuse the third for no reason a person
            // could understand.
            .RequireRateLimiting(RateLimitPolicies.MessageSend);

        routes.MapPatch("/conversations/{conversationId:guid}/messages/{messageId:guid}", EditMessage)
            .WithTags("Messages")
            .WithName("EditMessage")
            .WithSummary("Edit own message within 24 hours (FR-014)")
            .RequireAuthorization(AuthorizationPolicies.ConversationMember)
            .RequireConversationMembership();

        routes.MapDelete("/conversations/{conversationId:guid}/messages/{messageId:guid}", DeleteMessage)
            .WithTags("Messages")
            .WithName("DeleteMessage")
            .WithSummary("Delete own message within 24 hours, leaving a tombstone (FR-014)")
            .RequireAuthorization(AuthorizationPolicies.ConversationMember)
            .RequireConversationMembership();

        return routes;
    }

    /// <summary>One page of history.</summary>
    private static async Task<IResult> GetHistory(
        Guid conversationId,
        CurrentEmployee currentEmployee,
        IUseCaseDispatcher dispatcher,
        long? beforeSeq,
        long? afterSeq,
        int? limit,
        CancellationToken cancellationToken)
    {
        HistoryPage page = await dispatcher
            .SendAsync<GetHistory, HistoryPage>(
                new GetHistory(
                    conversationId,
                    currentEmployee.Id,
                    beforeSeq,
                    afterSeq,
                    limit ?? GetHistoryHandler.DefaultLimit),
                cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(new MessagePageResponse(
            [.. page.Messages.Select(m => m.ToResponse(
                page.Attachments.TryGetValue(m.Id, out var carried) ? carried : null))],
            page.NextCursor?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            page.HasMore));
    }

    /// <summary>
    /// Accepts a message, or returns the one this key already produced.
    /// </summary>
    /// <remarks>
    /// <b>201 for a new message, 200 for a replay</b> — the status code is how a client tells its
    /// retry landing twice from a second message. A 409 would be worse than either: a client that
    /// receives one has no way to know whether its original send succeeded, so it cannot decide
    /// whether to show the message or the failure.
    /// </remarks>
    private static async Task<IResult> SendMessage(
        Guid conversationId,
        SendMessageRequest request,
        CurrentEmployee currentEmployee,
        IUseCaseDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        SendMessageResult result = await dispatcher
            .SendAsync<SendMessage, SendMessageResult>(
                new SendMessage(
                    conversationId,
                    currentEmployee.Id,
                    request.ClientMessageKey,
                    request.Body,
                    request.Mentions,
                    request.AttachmentIds),
                cancellationToken)
            .ConfigureAwait(false);

        MessageResponse body = result.Message.ToResponse(result.Attachments);

        return result.WasCreated
            ? Results.Created(
                $"/api/v1/conversations/{conversationId}/messages/{result.Message.Id}",
                body)
            : Results.Ok(body);
    }

    /// <summary>Edits the caller's own message.</summary>
    private static async Task<IResult> EditMessage(
        Guid conversationId,
        Guid messageId,
        EditMessageRequest request,
        CurrentEmployee currentEmployee,
        IUseCaseDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        Message message = await dispatcher
            .SendAsync<EditMessage, Message>(
                new EditMessage(conversationId, messageId, currentEmployee.Id, request.Body),
                cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(message.ToResponse());
    }

    /// <summary>Deletes the caller's own message, leaving a tombstone.</summary>
    private static async Task<IResult> DeleteMessage(
        Guid conversationId,
        Guid messageId,
        CurrentEmployee currentEmployee,
        IUseCaseDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        await dispatcher
            .SendAsync<DeleteMessage, Message>(
                new DeleteMessage(conversationId, messageId, currentEmployee.Id),
                cancellationToken)
            .ConfigureAwait(false);

        // 204, as the contract documents. The tombstone reaches other clients over the hub rather
        // than in this response — the caller already knows what it deleted.
        return Results.NoContent();
    }
}
