using InternalChat.Application.Abstractions;
using InternalChat.Application.Search;

namespace InternalChat.UnitTests.Application;

/// <summary>
/// T159 — query parsing and filter validation (FR-030, FR-033).
/// </summary>
/// <remarks>
/// <para>
/// The validator is the only part of search that can be unit-tested honestly. Everything else —
/// ranking, the membership scope, diacritic folding — is a property of PostgreSQL's behaviour, and
/// a test that asserted it against a fake would be asserting the fake. Those live in
/// <c>tests/Integration/Search/</c>.
/// </para>
/// <para>
/// What is worth testing here is the set of requests the system refuses to attempt, and why. Each
/// one below prevents a specific bad outcome rather than merely covering a branch.
/// </para>
/// </remarks>
public sealed class SearchQueryTests : UnitTestBase
{
    private static readonly Guid Requester = Guid.CreateVersion7();

    private static SearchMessages Query(
        string text = "runbook",
        int limit = 25,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        string? hasAttachment = null) =>
        new(Requester, text, null, null, from, to, hasAttachment, limit);

    private static async Task<ValidationResult> ValidateAsync(SearchMessages request) =>
        await new SearchMessagesValidator().ValidateAsync(request);

    [Fact]
    public async Task An_ordinary_query_is_accepted()
    {
        Assert.True((await ValidateAsync(Query())).IsValid);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("a")]
    [InlineData("  a  ")]
    public async Task A_query_shorter_than_the_minimum_is_refused(string text)
    {
        // A one-character query matches an appreciable fraction of a year of messages: the work is
        // real, the result is useless, and it is trivially issued by holding a key down in a search
        // box. Trimmed before measuring, so a padded single character is still a single character.
        ValidationResult result = await ValidateAsync(Query(text));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Field == "q");
    }

    [Fact]
    public async Task A_query_at_the_minimum_length_is_accepted()
    {
        Assert.True((await ValidateAsync(Query("ke"))).IsValid);
        Assert.Equal(2, SearchMessagesHandler.MinimumQueryLength);
    }

    [Fact]
    public async Task A_page_larger_than_the_maximum_is_refused_rather_than_clamped()
    {
        // Refused, not silently reduced. A client asking for 500 results and receiving 100 without
        // being told will conclude there were only 100 — which is the same wrong conclusion FR-033
        // exists to prevent for truncation.
        ValidationResult result = await ValidateAsync(Query(limit: SearchMessagesHandler.MaximumLimit + 1));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Field == "limit");
    }

    [Fact]
    public async Task A_page_at_the_maximum_is_accepted()
    {
        Assert.True((await ValidateAsync(Query(limit: SearchMessagesHandler.MaximumLimit))).IsValid);
    }

    [Fact]
    public async Task A_reversed_date_range_is_refused_rather_than_swapped()
    {
        DateTimeOffset later = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        DateTimeOffset earlier = later.AddMonths(-3);

        ValidationResult result = await ValidateAsync(Query(from: later, to: earlier));

        // Swapping would "helpfully" return results for a range the caller did not ask for, hiding
        // the bug in whatever built the request. An empty result set at least prompts them to look.
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Field == "from");
    }

    [Fact]
    public async Task A_single_day_range_is_accepted()
    {
        DateTimeOffset day = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

        // from == to is a one-day search, not a reversed range. The endpoint widens `to` to the end
        // of that day, so this is the common case rather than an edge one.
        Assert.True((await ValidateAsync(Query(from: day, to: day))).IsValid);
    }

    [Fact]
    public async Task An_open_ended_date_range_is_accepted()
    {
        DateTimeOffset day = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

        Assert.True((await ValidateAsync(Query(from: day))).IsValid);
        Assert.True((await ValidateAsync(Query(to: day))).IsValid);
    }

    [Theory]
    [InlineData("any")]
    [InlineData("image")]
    [InlineData("video")]
    [InlineData(null)]
    public async Task The_documented_attachment_filters_are_accepted(string? filter)
    {
        Assert.True((await ValidateAsync(Query(hasAttachment: filter))).IsValid);
    }

    [Theory]
    [InlineData("pdf")]
    [InlineData("Image")]
    [InlineData("all")]
    [InlineData("")]
    public async Task An_undocumented_attachment_filter_is_refused(string filter)
    {
        // Case-sensitive on purpose: the contract declares lowercase values, and silently accepting
        // "Image" would let a client ship a spelling the documented API does not have.
        ValidationResult result = await ValidateAsync(Query(hasAttachment: filter));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Field == "hasAttachment");
    }

    [Fact]
    public async Task Every_problem_is_reported_at_once()
    {
        ValidationResult result = await ValidateAsync(
            Query(text: "a", limit: 1000, hasAttachment: "pdf"));

        // Fixing three fields across three round trips is a worse experience than being told all
        // three now — the same reasoning ProblemDetailsHandler applies to its `errors` map.
        Assert.False(result.IsValid);
        Assert.Equal(3, result.Errors.Count);
    }

    [Fact]
    public void A_search_query_carries_its_scope_rather_than_deriving_it()
    {
        // The scope is a required constructor parameter on SearchQuery, not an optional filter. A
        // default of "all conversations" is the one mistake in this feature that would be a data
        // breach rather than a bug, so the type is shaped to make it unwritable.
        SearchQuery query = new("runbook", [Guid.CreateVersion7()]);

        Assert.Single(query.ConversationIds);
        Assert.Null(query.AuthorId);
        Assert.Null(query.HasAttachment);
    }
}
