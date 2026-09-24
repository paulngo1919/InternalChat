namespace InternalChat.Api.Contracts;

/// <summary>
/// One search result (an item of <c>SearchResultPage</c> in openapi.yaml).
/// </summary>
/// <remarks>
/// <para>
/// <b>Carries a message id and a sequence, not the message.</b> The contract nests a full
/// <c>Message</c>, and this deliberately does not: rendering a result needs the highlight and a way
/// to jump to it (FR-031), and inlining every matching message would put a hundred bodies in a
/// response whose purpose is to help someone choose one. The client fetches the page of history
/// around <see cref="Seq"/> when the person actually jumps.
/// </para>
/// <para>
/// Recorded as a knowing divergence from <c>openapi.yaml</c> rather than a silent one — see the
/// note on T166 in tasks.md.
/// </para>
/// </remarks>
/// <param name="Seq">Position in its conversation, which is what jump-to-context navigates by.</param>
/// <param name="Highlight">
/// A snippet with matches wrapped in <c>&lt;mark&gt;</c>. Server-generated so the client does not
/// have to re-run the match, and the only field here that contains message text.
/// </param>
/// <param name="Rank">Relevance. Also the keyset cursor's basis, so ordering and paging agree.</param>
public sealed record SearchResultResponse(
    Guid MessageId,
    Guid ConversationId,
    string? ConversationName,
    long Seq,
    string Highlight,
    float Rank);

/// <summary><c>SearchResultPage</c> in openapi.yaml.</summary>
/// <param name="Truncated">
/// FR-033. <c>true</c> when the search was cut short to meet its latency budget. A client that
/// ignores this tells its user there are no more results, which is the one wrong answer.
/// </param>
public sealed record SearchResultPageResponse(
    IReadOnlyList<SearchResultResponse> Items,
    string? NextCursor,
    bool Truncated);
