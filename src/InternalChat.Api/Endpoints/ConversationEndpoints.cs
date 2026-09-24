using System.Globalization;
using InternalChat.Api.Authorization;
using InternalChat.Api.Contracts;
using InternalChat.Api.Mapping;
using InternalChat.Application.Abstractions;
using InternalChat.Application.Behaviors;
using InternalChat.Application.Conversations;
using InternalChat.Domain.Conversations;

namespace InternalChat.Api.Endpoints;

/// <summary>
/// T095 — the conversation endpoints (FR-007, FR-008).
/// </summary>
/// <remarks>
/// Each handler delegates to a use case through the dispatcher rather than calling a repository.
/// That is not ceremony: the dispatcher is what applies validation, the transaction, and the audit
/// record, so an endpoint that reached past it would write without any of the three.
/// </remarks>
public static class ConversationEndpoints
{
    /// <summary>Maps the conversation endpoints.</summary>
    public static IEndpointRouteBuilder MapConversationEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapGet("/conversations", ListConversations)
            .WithTags("Conversations")
            .WithName("ListConversations")
            .WithSummary("List conversations this employee belongs to, with unread counts")
            .RequireAuthorization(AuthorizationPolicies.Employee);

        routes.MapPost("/conversations", CreateConversation)
            .WithTags("Conversations")
            .WithName("CreateConversation")
            .WithSummary("Create a direct or group conversation (FR-007, FR-008)")
            .RequireAuthorization(AuthorizationPolicies.Employee);

        // No membership filter here, deliberately — there is no conversation yet to be a member of.
        // Authorization for a create is "are you an employee", plus the use case's own check that
        // every named participant may be added.

        routes.MapGet("/conversations/{conversationId:guid}", GetConversation)
            .WithTags("Conversations")
            .WithName("GetConversation")
            .WithSummary("Conversation detail")
            .RequireAuthorization(AuthorizationPolicies.ConversationMember)

            // The resource-scoped half. The policy above establishes only that the caller is
            // authenticated; this is what ties the request to a membership row (Principle IV).
            .RequireConversationMembership();

        return routes;
    }

    /// <summary>The caller's conversations, newest activity first.</summary>
    /// <remarks>
    /// The employee id comes from the resolved identity, never from the query string. There is
    /// therefore no id to substitute in order to read someone else's list.
    /// </remarks>
    private static async Task<IResult> ListConversations(
        CurrentEmployee currentEmployee,
        IUseCaseDispatcher dispatcher,
        string? cursor,
        int? limit,
        CancellationToken cancellationToken)
    {
        if (!TryParseCursor(cursor, out DateTimeOffset? parsedCursor))
        {
            return Results.BadRequest(new { error = "The cursor is not a valid page cursor." });
        }

        ConversationPage page = await dispatcher
            .SendAsync<ListConversations, ConversationPage>(
                new ListConversations(
                    currentEmployee.Id,
                    parsedCursor,
                    limit ?? ListConversationsHandler.DefaultLimit),
                cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(new ConversationPageResponse(
            [.. page.Items.Select(item => item.ToResponse())],
            FormatCursor(page.NextCursor)));
    }

    /// <summary>
    /// Creates a conversation, or returns the existing direct one for the pair.
    /// </summary>
    /// <remarks>
    /// 201 when this call created it and 200 when an existing direct conversation was returned, as
    /// the contract documents. The distinction matters to the client: two colleagues clicking
    /// "message" simultaneously both need to be taken into the same conversation, and only one of
    /// them created it.
    /// </remarks>
    private static async Task<IResult> CreateConversation(
        CreateConversationRequest request,
        CurrentEmployee currentEmployee,
        IUseCaseDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!MessagingMapper.TryParseKind(request.Kind, out ConversationKind kind))
        {
            return Results.BadRequest(new
            {
                error = $"'{request.Kind}' is not a conversation kind. Use "
                    + $"'{ConversationKinds.Direct}' or '{ConversationKinds.Group}'.",
            });
        }

        if (!MessagingMapper.TryParseHistoryVisibility(request.HistoryVisibility, out HistoryVisibility visibility))
        {
            return Results.BadRequest(new
            {
                error = $"'{request.HistoryVisibility}' is not a history-visibility rule.",
            });
        }

        CreateConversationResult result = await dispatcher
            .SendAsync<CreateConversation, CreateConversationResult>(
                new CreateConversation(
                    currentEmployee.Id,
                    kind,
                    request.Name,
                    request.MemberIds ?? [],
                    visibility),
                cancellationToken)
            .ConfigureAwait(false);

        // Read back through the projection so the response carries the member count and, for an
        // existing conversation, its real lastSeq. Returning a hand-built DTO from the write path
        // would report memberCount 0 for a conversation that has two members.
        ConversationResponse body = await ReadBackAsync(
            dispatcher,
            result.Conversation.Id,
            currentEmployee.Id,
            cancellationToken).ConfigureAwait(false);

        return result.WasCreated
            ? Results.Created($"/api/v1/conversations/{result.Conversation.Id}", body)
            : Results.Ok(body);
    }

    /// <summary>Conversation detail for a member.</summary>
    private static async Task<IResult> GetConversation(
        Guid conversationId,
        CurrentEmployee currentEmployee,
        IUseCaseDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        ConversationResponse body = await ReadBackAsync(
            dispatcher,
            conversationId,
            currentEmployee.Id,
            cancellationToken).ConfigureAwait(false);

        return Results.Ok(body);
    }

    private static async Task<ConversationResponse> ReadBackAsync(
        IUseCaseDispatcher dispatcher,
        Guid conversationId,
        Guid employeeId,
        CancellationToken cancellationToken)
    {
        ConversationSummary summary = await dispatcher
            .SendAsync<GetConversation, ConversationSummary>(
                new GetConversation(conversationId, employeeId),
                cancellationToken)
            .ConfigureAwait(false);

        return summary.ToResponse();
    }

    /// <summary>
    /// Parses the opaque list cursor.
    /// </summary>
    /// <remarks>
    /// A round-trip timestamp rather than an encoded blob. It is not a secret — it identifies a point
    /// in the caller's own list — and an opaque encoding would only make it harder to debug a
    /// pagination complaint. Round-trip format ("O") because a cursor that lost sub-second precision
    /// would skip or repeat conversations updated in the same second.
    /// </remarks>
    private static bool TryParseCursor(string? cursor, out DateTimeOffset? parsed)
    {
        parsed = null;

        if (string.IsNullOrWhiteSpace(cursor))
        {
            return true;
        }

        if (!DateTimeOffset.TryParse(
                cursor,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out DateTimeOffset value))
        {
            return false;
        }

        parsed = value;
        return true;
    }

    private static string? FormatCursor(DateTimeOffset? cursor) =>
        cursor?.ToString("O", CultureInfo.InvariantCulture);
}
