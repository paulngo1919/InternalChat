using InternalChat.Application.Authorization;
using InternalChat.Application.Behaviors;
using InternalChat.Domain.Conversations;

namespace InternalChat.Api.Authorization;

/// <summary>
/// T067 — applies the conversation membership policy to a conversation-scoped endpoint.
/// </summary>
/// <remarks>
/// <para>
/// Constitution Principle IV requires authorization to be resource-scoped: a role says nothing
/// about <em>which</em> conversation. This filter is where that becomes true for HTTP.
/// <c>tests/Architecture/AuthorizationCoverageTests.cs</c> fails the build on any endpoint carrying
/// <c>{conversationId}</c> that does not name a policy, so forgetting it is a build error rather
/// than a review finding.
/// </para>
/// <para>
/// <b>A refusal is thrown, not returned.</b> <see cref="UnauthorizedAccessException"/> is mapped by
/// <c>ProblemDetailsHandler</c> to a body identical to a not-found — same shape, same wording, same
/// status. Returning a distinct 403 here would let anyone enumerate conversations by reading which
/// error came back, which is exactly what SC-017 forbids. Doing it in one place means no endpoint
/// can get the shape subtly wrong.
/// </para>
/// </remarks>
public sealed class MembershipEndpointFilter : IEndpointFilter
{
    /// <summary>Route parameter naming the conversation.</summary>
    public const string RouteParameter = "conversationId";

    private readonly MembershipRole? _minimumRole;

    /// <summary>Requires live membership, at a minimum role when one is given.</summary>
    public MembershipEndpointFilter(MembershipRole? minimumRole = null) => _minimumRole = minimumRole;

    /// <inheritdoc />
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        HttpContext http = context.HttpContext;

        CurrentEmployee currentEmployee = http.RequestServices.GetRequiredService<CurrentEmployee>();
        IConversationMembershipEvaluator evaluator =
            http.RequestServices.GetRequiredService<IConversationMembershipEvaluator>();
        ISecurityAuditor auditor = http.RequestServices.GetRequiredService<ISecurityAuditor>();

        // Not resolved means the access gate refused or never ran. Either way this request has no
        // identity to make a resource-scoped decision about, so it is refused rather than evaluated
        // against Guid.Empty.
        if (!currentEmployee.IsResolved)
        {
            throw new UnauthorizedAccessException("No employee is resolved for this request.");
        }

        if (!TryReadConversationId(http, out Guid conversationId))
        {
            throw new UnauthorizedAccessException("The request named no conversation.");
        }

        MembershipDecision decision = await evaluator
            .EvaluateAsync(
                currentEmployee.Id,
                new ConversationMembershipRequirement(conversationId, _minimumRole),
                http.RequestAborted)
            .ConfigureAwait(false);

        if (!decision.IsAllowed)
        {
            await auditor
                .AccessDeniedAsync(
                    currentEmployee.Id,
                    "conversation",
                    conversationId,
                    decision.Reason.ToString(),
                    http.Connection.RemoteIpAddress?.ToString(),
                    http.RequestAborted)
                .ConfigureAwait(false);

            throw new UnauthorizedAccessException("Access to this conversation was refused.");
        }

        return await next(context).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the conversation id from the route.
    /// </summary>
    /// <remarks>
    /// Route values only — never the query string or a header. A conversation id accepted from
    /// somewhere other than the path would let a caller pass the check for one conversation and be
    /// served another, which is the resource-scoping failure this whole filter exists to prevent.
    /// </remarks>
    private static bool TryReadConversationId(HttpContext http, out Guid conversationId)
    {
        conversationId = Guid.Empty;

        return http.Request.RouteValues.TryGetValue(RouteParameter, out object? raw)
            && Guid.TryParse(raw?.ToString(), out conversationId);
    }
}

/// <summary>Applies the membership policy to an endpoint.</summary>
public static class MembershipEndpointFilterExtensions
{
    /// <summary>
    /// Requires the caller to hold a live membership of the conversation named in the route.
    /// </summary>
    /// <remarks>
    /// Pairs with <c>.RequireAuthorization(AuthorizationPolicies.ConversationMember)</c>: the policy
    /// is what the architecture test looks for on the endpoint's metadata, and this filter is what
    /// performs the resource-scoped check. Both are needed — the policy alone cannot see the route.
    /// </remarks>
    public static TBuilder RequireConversationMembership<TBuilder>(
        this TBuilder builder,
        MembershipRole? minimumRole = null)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.AddEndpointFilter(new MembershipEndpointFilter(minimumRole));
        return builder;
    }
}
