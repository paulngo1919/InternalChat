using Microsoft.AspNetCore.Authorization;

namespace InternalChat.Api.Authorization;

/// <summary>
/// The named authorization policies every endpoint and hub declares.
/// </summary>
/// <remarks>
/// Named rather than inline so <c>tests/Architecture/AuthorizationCoverageTests.cs</c> can assert
/// that a conversation-scoped endpoint carries one. An endpoint with a bare
/// <c>.RequireAuthorization()</c> asks only "is anyone signed in", which is a role-shaped question
/// and Principle IV forbids answering access with one.
/// </remarks>
public static class AuthorizationPolicies
{
    /// <summary>An active employee. The floor for everything that is not a health probe.</summary>
    public const string Employee = "employee";

    /// <summary>
    /// A live member of the conversation named in the route.
    /// </summary>
    /// <remarks>
    /// The policy itself only requires authentication. It cannot do more: an
    /// <see cref="IAuthorizationRequirement"/> is evaluated without the route, so it has no way to
    /// know which conversation is being asked for. The resource-scoped half is
    /// <see cref="MembershipEndpointFilter"/>, applied by
    /// <c>RequireConversationMembership()</c> — the two are always used together, and the
    /// architecture test is what keeps that pairing honest.
    /// </remarks>
    public const string ConversationMember = "conversation-member";

    /// <summary>Platform administration: retention policy and exports (FR-053, FR-055).</summary>
    public const string PlatformAdmin = "platform-admin";

    /// <summary>Realm role granting platform administration.</summary>
    public const string AdminRole = "chat-admin";

    /// <summary>Registers the policies.</summary>
    public static IServiceCollection AddChatAuthorization(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddAuthorizationBuilder()

            // Deny-by-default at the framework level too, so an endpoint that somehow reached the
            // routing table without a declared policy is refused rather than served. The
            // architecture test should catch that first; this is what happens if it ever does not.
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
            .AddPolicy(Employee, policy => policy.RequireAuthenticatedUser())
            .AddPolicy(ConversationMember, policy => policy.RequireAuthenticatedUser())
            .AddPolicy(PlatformAdmin, policy => policy
                .RequireAuthenticatedUser()
                .RequireRole(AdminRole));

        return services;
    }
}
