namespace InternalChat.Application.Abstractions;

/// <summary>A search request, already scoped to conversations the caller belongs to.</summary>
/// <param name="Text">Free-text query.</param>
/// <param name="ConversationIds">
/// The caller's accessible conversations. Applied as a filter BEFORE ranking — this is both the
/// access control (FR-029) and the reason PostgreSQL full-text search is viable at 125 million
/// rows, since a typical employee belongs to tens of conversations, not thousands.
/// </param>
/// <param name="AuthorId">Optional author filter.</param>
/// <param name="From">Optional inclusive start of the date range.</param>
/// <param name="To">Optional inclusive end of the date range.</param>
/// <param name="Limit">Maximum results. Clamped to 100 by the endpoint.</param>
/// <param name="Cursor">Opaque keyset cursor for the next page.</param>
/// <param name="HasAttachment">
/// Optional attachment filter (FR-030): <c>any</c>, <c>image</c>, or <c>video</c>. <c>null</c> does
/// not filter. Only attachments that passed their scan count — a pending or infected upload is not
/// something the searcher can open, so treating it as a match would produce a result that leads
/// nowhere.
/// </param>
public sealed record SearchQuery(
    string Text,
    IReadOnlyCollection<Guid> ConversationIds,
    Guid? AuthorId = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    int Limit = 25,
    string? Cursor = null,
    string? HasAttachment = null);

/// <summary>One matching message.</summary>
/// <param name="MessageId">Identity of the match.</param>
/// <param name="ConversationId">Which conversation it belongs to.</param>
/// <param name="Seq">Position in the conversation, for jump-to-context.</param>
/// <param name="Highlight">Snippet with the match marked.</param>
/// <param name="Rank">Relevance score.</param>
public sealed record SearchHit(Guid MessageId, Guid ConversationId, long Seq, string Highlight, float Rank);

/// <summary>A page of search results.</summary>
/// <param name="Hits">The matches, ranked.</param>
/// <param name="NextCursor">Cursor for the following page, or <c>null</c> at the end.</param>
/// <param name="Truncated">
/// <c>true</c> when the result set was cut short to meet the latency budget. FR-033 requires
/// saying so rather than silently returning a partial answer — a user who does not know results
/// were truncated concludes the content does not exist.
/// </param>
public sealed record SearchResults(IReadOnlyList<SearchHit> Hits, string? NextCursor, bool Truncated);

/// <summary>
/// Full-text search over message content.
/// </summary>
/// <remarks>
/// <para>
/// Backed by PostgreSQL full-text search for v1 (research.md D9). This interface is the seam
/// that keeps that reversible: if search p95 exceeds 800 ms against a seeded full-retention
/// corpus, OpenSearch goes in behind here without a use case changing.
/// </para>
/// <para>
/// FR-032 requires edits, deletions, and membership removals to be reflected in results, which
/// is why removal is an explicit operation rather than something left to a periodic rebuild —
/// content a user has lost access to must stop appearing immediately, not eventually.
/// </para>
/// </remarks>
public interface ISearchIndex
{
    /// <summary>Runs a search already scoped to the caller's conversations.</summary>
    Task<SearchResults> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default);

    /// <summary>Re-indexes a message after it is sent or edited.</summary>
    Task IndexMessageAsync(Guid messageId, CancellationToken cancellationToken = default);

    /// <summary>Removes a message from the index after deletion.</summary>
    Task RemoveMessageAsync(Guid messageId, CancellationToken cancellationToken = default);
}
