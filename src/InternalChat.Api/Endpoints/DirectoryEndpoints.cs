using InternalChat.Api.Authorization;
using InternalChat.Api.Contracts;
using InternalChat.Api.RateLimiting;
using InternalChat.Application.Abstractions;
using InternalChat.Domain.Employees;

namespace InternalChat.Api.Endpoints;

/// <summary>
/// T071 — <c>GET /directory/employees</c>, the people picker behind FR-007.
/// </summary>
public static class DirectoryEndpoints
{
    /// <summary>Contract default when <c>limit</c> is omitted.</summary>
    private const int DefaultLimit = 20;

    /// <summary>Contract maximum, matching <c>openapi.yaml</c>.</summary>
    private const int MaximumLimit = 50;

    /// <summary>
    /// Contract minimum for <c>q</c>.
    /// </summary>
    /// <remarks>
    /// One character matches most of a 10,000-person directory. Rejecting it is a performance
    /// guard, not a validation nicety — the trigram index cannot help a query that matches
    /// everything.
    /// </remarks>
    private const int MinimumQueryLength = 2;

    /// <summary>Maps the directory endpoints.</summary>
    public static IEndpointRouteBuilder MapDirectoryEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapGet("/directory/employees", SearchEmployees)
            .WithTags("Directory")
            .WithName("SearchEmployees")
            .WithSummary("Search active employees to start a conversation (FR-007)")
            .RequireAuthorization(AuthorizationPolicies.Employee)

            // Shares the search limiter: this endpoint fires on every keystroke of the people
            // picker, which is the same traffic shape the search policy was measured for.
            .RequireRateLimiting(RateLimitPolicies.Search);

        return routes;
    }

    /// <summary>
    /// Searches active employees.
    /// </summary>
    /// <remarks>
    /// Deliberately unrestricted across the organisation, unlike everything else in this platform:
    /// FR-007 is "find a colleague to message", and a directory scoped to conversations you already
    /// belong to could never start a first conversation. It exposes no conversation, message, or
    /// membership — only what an internal address book would.
    /// </remarks>
    private static async Task<IResult> SearchEmployees(
        string q,
        IEmployeeDirectory directory,
        CancellationToken cancellationToken,
        int? limit = null)
    {
        if (string.IsNullOrWhiteSpace(q) || q.Trim().Length < MinimumQueryLength)
        {
            return Results.BadRequest(new
            {
                error = $"Provide at least {MinimumQueryLength} characters to search.",
            });
        }

        int take = Math.Clamp(limit ?? DefaultLimit, 1, MaximumLimit);

        IReadOnlyList<EmployeeProfile> matches = await directory
            .SearchAsync(q, take, cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(matches
            .Select(e => new EmployeeSummaryResponse(
                e.Id,
                e.DisplayName,
                e.Email,
                e.AvatarUrl,
                e.Status == EmployeeStatus.Active ? "active" : "deactivated"))
            .ToArray());
    }
}
