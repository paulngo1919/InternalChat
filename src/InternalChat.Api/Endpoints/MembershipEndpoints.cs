using InternalChat.Api.Authorization;
using InternalChat.Api.Contracts;
using InternalChat.Api.Mapping;
using InternalChat.Application.Abstractions;
using InternalChat.Application.Behaviors;
using InternalChat.Application.Conversations;
using InternalChat.Domain.Conversations;

namespace InternalChat.Api.Endpoints;

/// <summary>
/// T115 — the membership endpoints (FR-008, US3).
/// </summary>
/// <remarks>
/// Add and remove both require <see cref="MembershipRole.Admin"/>, applied declaratively by
/// <c>.RequireConversationMembership(MembershipRole.Admin)</c> — the same mechanism
/// <c>GetConversation</c> uses for plain membership, at a higher floor. Listing members requires
/// only ordinary membership: any member of a group can see who else is in it.
/// </remarks>
public static class MembershipEndpoints
{
    /// <summary>Maps the membership endpoints.</summary>
    public static IEndpointRouteBuilder MapMembershipEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapGet("/conversations/{conversationId:guid}/members", ListMembers)
            .WithTags("Conversations")
            .WithName("ListMembers")
            .WithSummary("List members")
            .RequireAuthorization(AuthorizationPolicies.ConversationMember)
            .RequireConversationMembership();

        routes.MapPost("/conversations/{conversationId:guid}/members", AddMember)
            .WithTags("Conversations")
            .WithName("AddMember")
            .WithSummary("Add a member (FR-008). Refused for direct conversations.")
            .RequireAuthorization(AuthorizationPolicies.ConversationMember)
            .RequireConversationMembership(MembershipRole.Admin);

        routes.MapDelete("/conversations/{conversationId:guid}/members/{employeeId:guid}", RemoveMember)
            .WithTags("Conversations")
            .WithName("RemoveMember")
            .WithSummary(
                "Remove a member (FR-008). Takes effect immediately for new messages, files, and "
                + "search results.")
            .RequireAuthorization(AuthorizationPolicies.ConversationMember)
            .RequireConversationMembership(MembershipRole.Admin);

        return routes;
    }

    private static async Task<IResult> ListMembers(
        Guid conversationId,
        IUseCaseDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<MemberProjection> members = await dispatcher
            .SendAsync<ListMembers, IReadOnlyList<MemberProjection>>(
                new ListMembers(conversationId),
                cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(members.Select(ToResponse).ToArray());
    }

    /// <summary>
    /// Adds a member. 201 on success — there is no "already exists" replay to distinguish, unlike a
    /// message send, so a duplicate attempt is a genuine conflict rather than an idempotent retry.
    /// </summary>
    private static async Task<IResult> AddMember(
        Guid conversationId,
        AddMemberRequest request,
        CurrentEmployee currentEmployee,
        IUseCaseDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!MessagingMapper.TryParseRole(request.Role, out MembershipRole role))
        {
            return Results.BadRequest(new
            {
                error = $"'{request.Role}' is not a membership role. Use "
                    + $"'{MembershipRoles.Member}' or '{MembershipRoles.Admin}'.",
            });
        }

        await dispatcher
            .SendAsync<AddMember, Membership>(
                new AddMember(conversationId, currentEmployee.Id, request.EmployeeId, role),
                cancellationToken)
            .ConfigureAwait(false);

        return Results.Created($"/api/v1/conversations/{conversationId}/members", (object?)null);
    }

    private static async Task<IResult> RemoveMember(
        Guid conversationId,
        Guid employeeId,
        CurrentEmployee currentEmployee,
        IUseCaseDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        await dispatcher
            .SendAsync<RemoveMember, Membership>(
                new RemoveMember(conversationId, currentEmployee.Id, employeeId),
                cancellationToken)
            .ConfigureAwait(false);

        return Results.NoContent();
    }

    private static MemberResponse ToResponse(MemberProjection member) => new(
        new EmployeeSummaryResponse(
            member.EmployeeId,
            member.DisplayName,
            member.Email ?? string.Empty,
            member.AvatarUrl,
            member.IsActive ? "active" : "deactivated"),
        MessagingMapper.ToWire(member.Role),
        member.JoinedAt);
}
