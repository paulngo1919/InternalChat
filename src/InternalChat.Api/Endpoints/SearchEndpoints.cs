using System.Globalization;
using InternalChat.Api.Authorization;
using InternalChat.Api.Contracts;
using InternalChat.Api.RateLimiting;
using InternalChat.Application.Behaviors;
using InternalChat.Application.Search;

namespace InternalChat.Api.Endpoints;

/// <summary>
/// T166 — full-text search across the caller's own conversations (FR-029 – FR-033).
/// </summary>
/// <remarks>
/// <para>
/// <b>No membership filter on the route, and that is not an omission.</b> Every other
/// conversation-scoped endpoint names a conversation in its path, so the filter can tie the request
/// to a membership row. A search spans conversations by definition — the scope is computed inside
/// the handler from the caller's own memberships, which is the only place it can be. A route-level
/// filter here would have nothing to check.
/// </para>
/// <para>
/// <b>Rate limited by a fixed window.</b> Search is the most expensive read in the platform and the
/// easiest to issue repeatedly by holding a key down in a search box. A token bucket would let a
/// burst through, which is right for sending a few messages and wrong for a query that touches a
/// GIN index over a year of history.
/// </para>
/// </remarks>
public static class SearchEndpoints
{
    /// <summary>Maps the search endpoints.</summary>
    public static IEndpointRouteBuilder MapSearchEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapGet("/search/messages", SearchMessages)
            .WithTags("Search")
            .WithName("SearchMessages")
            .WithSummary("Search message text and attachment names in your own conversations (FR-029)")
            .RequireAuthorization(AuthorizationPolicies.Employee)
            .RequireRateLimiting(RateLimitPolicies.Search);

        return routes;
    }

    /// <summary>One page of search results.</summary>
    private static async Task<IResult> SearchMessages(
        string q,
        CurrentEmployee currentEmployee,
        IUseCaseDispatcher dispatcher,
        CancellationToken cancellationToken,
        Guid? conversationId = null,
        Guid? authorId = null,
        string? from = null,
        string? to = null,
        string? hasAttachment = null,
        string? cursor = null,
        int? limit = null)
    {
        SearchResultPage page = await dispatcher
            .SendAsync<Application.Search.SearchMessages, SearchResultPage>(
                new Application.Search.SearchMessages(
                    currentEmployee.Id,
                    q,
                    conversationId,
                    authorId,
                    ParseDate(from, endOfDay: false),
                    ParseDate(to, endOfDay: true),
                    hasAttachment,
                    limit ?? SearchMessagesHandler.DefaultLimit,
                    cursor),
                cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(new SearchResultPageResponse(
            [.. page.Items.Select(item => new SearchResultResponse(
                item.Hit.MessageId,
                item.ConversationId,
                item.ConversationName,
                item.Hit.Seq,
                item.Hit.Highlight,
                item.Hit.Rank))],
            page.NextCursor,
            page.Truncated));
    }

    /// <summary>
    /// Parses a <c>date</c> query parameter into an instant.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The contract types these as <c>format: date</c> — a day, not an instant. A day has to become
    /// a range to be useful: <c>to=2026-03-01</c> means "up to the end of that day", and treating it
    /// as midnight would silently exclude everything sent on the day the person asked for. That
    /// off-by-a-day is invisible in testing and infuriating in use.
    /// </para>
    /// <para>
    /// Interpreted as UTC, matching <c>sent_at</c>. A per-employee time zone would be more correct
    /// and needs the preference the notification settings already carry; until search actually reads
    /// it, one consistent interpretation beats two inconsistent ones.
    /// </para>
    /// </remarks>
    private static DateTimeOffset? ParseDate(string? value, bool endOfDay)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !DateOnly.TryParse(value, CultureInfo.InvariantCulture, out DateOnly date))
        {
            return null;
        }

        DateTimeOffset start = new(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        return endOfDay ? start.AddDays(1).AddTicks(-1) : start;
    }
}
