using InternalChat.Application.Abstractions;
using InternalChat.Application.Behaviors;

namespace InternalChat.Application.Search;

/// <summary>Searches message text and attachment names across the caller's conversations (FR-029).</summary>
/// <param name="ConversationId">
/// Optional narrowing to one conversation. Never widening: it is intersected with what the caller
/// may already read, so naming a conversation they are not in yields nothing rather than access.
/// </param>
public sealed record SearchMessages(
    Guid RequestedBy,
    string Text,
    Guid? ConversationId = null,
    Guid? AuthorId = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    string? HasAttachment = null,
    int Limit = 25,
    string? Cursor = null);

/// <summary>One result, with the conversation it came from resolved for display.</summary>
public sealed record SearchResultItem(
    SearchHit Hit,
    Guid ConversationId,
    string? ConversationName);

/// <summary>A page of results.</summary>
/// <param name="Truncated">
/// FR-033. <c>true</c> when the search was cut short to meet its budget — a caller who is not told
/// concludes the content does not exist.
/// </param>
public sealed record SearchResultPage(
    IReadOnlyList<SearchResultItem> Items,
    string? NextCursor,
    bool Truncated);

/// <summary>
/// Resolves what the caller may read, then searches only that.
/// </summary>
/// <remarks>
/// <para>
/// <b>The scope is computed here, from the database, on every request.</b> It is never taken from
/// the request and never cached beyond the membership reader's own short TTL. That is what makes
/// FR-032's "membership removals are reflected in results" true without any re-indexing: someone
/// removed from a group stops matching it on their next search because the scope no longer contains
/// it, not because anything was rebuilt.
/// </para>
/// <para>
/// <b>An empty scope short-circuits.</b> A caller in no conversations gets an empty page without a
/// query — which is also the same answer, in the same shape, as a search that matched nothing.
/// </para>
/// <para>
/// <b>Not <see cref="ITransactionalRequest"/>.</b> This writes nothing, and wrapping a read in a
/// transaction would hold a connection for the length of the search for no benefit.
/// </para>
/// </remarks>
public sealed class SearchMessagesHandler : IUseCase<SearchMessages, SearchResultPage>
{
    /// <summary>Default page size, matching <c>openapi.yaml</c>.</summary>
    public const int DefaultLimit = 25;

    /// <summary>Largest page the API will serve.</summary>
    public const int MaximumLimit = 100;

    /// <summary>Shortest query accepted, matching the contract's <c>minLength</c>.</summary>
    /// <remarks>
    /// A single character matches an appreciable fraction of a year of messages, so the work is
    /// real and the result is useless. Two is not a strong filter either, but it is the contract's.
    /// </remarks>
    public const int MinimumQueryLength = 2;

    private readonly IConversationReader _conversations;
    private readonly ISearchIndex _index;

    /// <summary>Creates the handler.</summary>
    public SearchMessagesHandler(IConversationReader conversations, ISearchIndex index)
    {
        ArgumentNullException.ThrowIfNull(conversations);
        ArgumentNullException.ThrowIfNull(index);

        _conversations = conversations;
        _index = index;
    }

    /// <inheritdoc />
    public async Task<SearchResultPage> HandleAsync(
        SearchMessages request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        IReadOnlyList<Guid> accessible = await _conversations
            .ListAccessibleIdsAsync(request.RequestedBy, cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyCollection<Guid> scope = Narrow(accessible, request.ConversationId);

        if (scope.Count == 0)
        {
            return new SearchResultPage([], null, Truncated: false);
        }

        SearchResults results = await _index
            .SearchAsync(
                new SearchQuery(
                    request.Text,
                    scope,
                    request.AuthorId,
                    request.From,
                    request.To,
                    Math.Clamp(request.Limit <= 0 ? DefaultLimit : request.Limit, 1, MaximumLimit),
                    request.Cursor,
                    request.HasAttachment),
                cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyDictionary<Guid, string?> names = await ResolveNamesAsync(
            results.Hits, request.RequestedBy, cancellationToken).ConfigureAwait(false);

        return new SearchResultPage(
            [.. results.Hits.Select(hit => new SearchResultItem(
                hit,
                hit.ConversationId,
                names.TryGetValue(hit.ConversationId, out string? name) ? name : null))],
            results.NextCursor,
            results.Truncated);
    }

    /// <summary>
    /// Intersects the caller's accessible conversations with an optional requested one.
    /// </summary>
    /// <remarks>
    /// Intersection, never substitution. If the requested conversation is not in the accessible set
    /// the result is empty — which is the same answer as a conversation with no matches, so the
    /// filter cannot be used to probe for a conversation's existence (FR-029, SC-017).
    /// </remarks>
    private static IReadOnlyCollection<Guid> Narrow(
        IReadOnlyList<Guid> accessible,
        Guid? requested) =>
        requested is { } only
            ? accessible.Where(id => id == only).ToArray()
            : accessible;

    /// <summary>
    /// Resolves conversation names for display, one query for the whole page.
    /// </summary>
    /// <remarks>
    /// Scoped by the caller again rather than by id alone. It is redundant — every id here came out
    /// of their own accessible set moments ago — and it is kept because the alternative is a lookup
    /// that would return a name for any id it was handed, which is one refactor away from being
    /// called with an id that did not come from that set.
    /// </remarks>
    private async Task<IReadOnlyDictionary<Guid, string?>> ResolveNamesAsync(
        IReadOnlyList<SearchHit> hits,
        Guid requestedBy,
        CancellationToken cancellationToken)
    {
        Dictionary<Guid, string?> names = [];

        foreach (Guid conversationId in hits.Select(h => h.ConversationId).Distinct())
        {
            ConversationSummary? summary = await _conversations
                .FindForEmployeeAsync(conversationId, requestedBy, cancellationToken)
                .ConfigureAwait(false);

            // A direct conversation has no name; the client renders the other participant.
            names[conversationId] = summary?.Conversation.Name;
        }

        return names;
    }
}

/// <summary>Rejects a search that could not be served sensibly.</summary>
public sealed class SearchMessagesValidator : IValidator<SearchMessages>
{
    /// <inheritdoc />
    public ValueTask<ValidationResult> ValidateAsync(
        SearchMessages request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        List<ValidationError> errors = [];

        if (string.IsNullOrWhiteSpace(request.Text)
            || request.Text.Trim().Length < SearchMessagesHandler.MinimumQueryLength)
        {
            errors.Add(new ValidationError(
                "q",
                $"A search needs at least {SearchMessagesHandler.MinimumQueryLength} characters."));
        }

        if (request.Limit > SearchMessagesHandler.MaximumLimit)
        {
            errors.Add(new ValidationError(
                "limit",
                $"A search page is at most {SearchMessagesHandler.MaximumLimit} results."));
        }

        if (request.From is { } from && request.To is { } to && from > to)
        {
            // Refused rather than swapped. A reversed range is a client defect, and silently
            // correcting it would hide the bug while returning results the caller did not ask for.
            errors.Add(new ValidationError("from", "The start of the date range is after its end."));
        }

        if (request.HasAttachment is { } kind and not ("any" or "image" or "video"))
        {
            errors.Add(new ValidationError(
                "hasAttachment", $"'{kind}' is not a filter. Use 'any', 'image', or 'video'."));
        }

        return ValueTask.FromResult(
            errors.Count == 0 ? ValidationResult.Valid : new ValidationResult(errors));
    }
}
