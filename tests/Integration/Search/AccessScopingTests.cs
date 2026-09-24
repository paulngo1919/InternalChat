using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using InternalChat.Api.Contracts;
using InternalChat.Domain.Common;
using InternalChat.Domain.Messages;
using InternalChat.Infrastructure.Persistence;
using InternalChat.IntegrationTests.Fixtures;

namespace InternalChat.IntegrationTests.Search;

/// <summary>
/// T161 — no results, and indistinguishable timing, for content in conversations the caller is not
/// in (FR-029, SC-017).
/// </summary>
/// <remarks>
/// <para>
/// <b>The timing assertion is the unusual one and needs its limits stated honestly.</b> A search
/// that found a match and then discarded it would take measurably longer than one that found
/// nothing, and that difference is a side channel: an outsider could learn that a phrase exists
/// somewhere by timing the refusal. The implementation avoids it structurally — the membership
/// filter is inside the query, so a non-member's search never has the row as a candidate — and the
/// test below checks the consequence.
/// </para>
/// <para>
/// It is a smoke alarm, not a proof. A single-machine timing comparison on a shared CI runner
/// cannot establish the absence of a channel, so the bound is deliberately loose: it catches the
/// implementation being rewritten to filter <em>after</em> ranking, which would be an order of
/// magnitude, and it does not pretend to catch a microsecond. The structural argument is what makes
/// the property true; this stops it being quietly lost.
/// </para>
/// </remarks>
public sealed class AccessScopingTests : MessagingTestBase
{
    /// <summary>A phrase that exists only in the conversation the searcher is not in.</summary>
    private const string SecretPhrase = "pomegranate reconciliation quarterly";

    /// <summary>A phrase in the searcher's own conversation, so the happy path is proved too.</summary>
    private const string VisiblePhrase = "deployment runbook";

    public AccessScopingTests(StackFixture stack)
        : base(stack)
    {
    }

    [Fact]
    public async Task A_member_finds_a_phrase_from_their_own_conversation()
    {
        await ArrangeAsync();

        SearchResultPageResponse page = await SearchAsync("an.nguyen", VisiblePhrase);

        SearchResultResponse hit = Assert.Single(page.Items);

        Assert.Contains("runbook", hit.Highlight, StringComparison.OrdinalIgnoreCase);

        // FR-031: the result carries what jump-to-context needs.
        Assert.True(hit.Seq > 0);
        Assert.False(page.Truncated);
    }

    [Fact]
    public async Task A_non_member_finds_nothing_for_a_phrase_that_exists_only_elsewhere()
    {
        await ArrangeAsync();

        // chi.le is a real, active employee. The phrase genuinely exists — it simply exists
        // somewhere she cannot read.
        SearchResultPageResponse page = await SearchAsync("chi.le", SecretPhrase);

        Assert.Empty(page.Items);
        Assert.Null(page.NextCursor);
        Assert.False(page.Truncated);
    }

    [Fact]
    public async Task A_member_of_the_other_conversation_does_find_it()
    {
        await ArrangeAsync();

        // The control for the test above. Without this, an empty result set would be equally
        // consistent with the phrase never having been indexed at all.
        SearchResultPageResponse page = await SearchAsync("binh.tran", SecretPhrase);

        Assert.Single(page.Items);
    }

    [Fact]
    public async Task Naming_an_inaccessible_conversation_narrows_rather_than_widens()
    {
        Arrangement arrangement = await ArrangeAsync();

        SearchResultPageResponse page = await SearchAsync(
            "chi.le", SecretPhrase, $"&conversationId={arrangement.PrivateConversationId}");

        // The conversation filter is intersected with what the caller may read, never substituted
        // for it. Asking about a conversation you are not in is the same as asking about one with
        // no matches.
        Assert.Empty(page.Items);
    }

    [Fact]
    public async Task A_refused_search_and_an_empty_one_are_indistinguishable_in_shape_and_timing()
    {
        await ArrangeAsync();

        // A phrase that exists nowhere at all. If the implementation ever filtered after ranking,
        // the SecretPhrase search would do real work that this one does not, and the two would
        // diverge.
        const string NowherePhrase = "chrysanthemum triangulation sediment";

        TimeSpan existsElsewhere = await MeasureAsync("chi.le", SecretPhrase);
        TimeSpan existsNowhere = await MeasureAsync("chi.le", NowherePhrase);

        // Loose by design — see the class remarks. This catches an order-of-magnitude regression
        // from filtering after ranking, not a microsecond of cache behaviour.
        double ratio = existsElsewhere.TotalMilliseconds
            / Math.Max(existsNowhere.TotalMilliseconds, 1);

        Assert.True(
            ratio is > 0.1 and < 10,
            $"Searching for a phrase that exists in an inaccessible conversation took "
            + $"{existsElsewhere.TotalMilliseconds:F1} ms; one that exists nowhere took "
            + $"{existsNowhere.TotalMilliseconds:F1} ms. A large difference means the membership "
            + $"filter is being applied after the match rather than inside the query.");
    }

    [Fact]
    public async Task A_query_below_the_minimum_length_is_refused()
    {
        await ArrangeAsync();

        HttpClient client = await AuthenticatedClientAsync("an.nguyen");
        HttpResponseMessage response = await client.GetAsync("/api/v1/search/messages?q=a");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task An_unauthenticated_search_is_refused()
    {
        await ArrangeAsync();

        HttpResponseMessage response = await Api.CreateClient()
            .GetAsync($"/api/v1/search/messages?q={Uri.EscapeDataString(VisiblePhrase)}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private async Task<TimeSpan> MeasureAsync(string username, string phrase)
    {
        // Warmed first. The first search in a process pays for connection establishment and plan
        // caching, and comparing a cold call against a warm one would measure the runtime.
        await SearchAsync(username, phrase);

        long start = Stopwatch.GetTimestamp();
        await SearchAsync(username, phrase);

        return Stopwatch.GetElapsedTime(start);
    }

    private async Task<SearchResultPageResponse> SearchAsync(
        string username,
        string phrase,
        string extraQuery = "")
    {
        HttpClient client = await AuthenticatedClientAsync(username);

        HttpResponseMessage response = await client.GetAsync(
            $"/api/v1/search/messages?q={Uri.EscapeDataString(phrase)}{extraQuery}");

        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync();

            Assert.Fail(
                $"Search returned {(int)response.StatusCode}. Body: {body}. Logs: {Api.LogReport()}");
        }

        SearchResultPageResponse? page =
            await response.Content.ReadFromJsonAsync<SearchResultPageResponse>();

        Assert.NotNull(page);
        return page;
    }

    /// <summary>
    /// Two conversations: one the searcher is in, one they are not.
    /// </summary>
    private async Task<Arrangement> ArrangeAsync()
    {
        Guid sharedConversationId = Guid.CreateVersion7();
        Guid privateConversationId = Guid.CreateVersion7();

        await using ChatDbContext context = CreateDbContext();

        Guid an = await TestData.SeedEmployeeAsync(context, "an.nguyen");
        Guid binh = await TestData.SeedEmployeeAsync(context, "binh.tran");
        await TestData.SeedEmployeeAsync(context, "chi.le");

        await TestData.SeedMembershipAsync(context, sharedConversationId, an);
        await TestData.SeedMembershipAsync(context, privateConversationId, binh);

        AddMessage(context, sharedConversationId, an, VisiblePhrase, "01JBXQ7ZPT4M9WYFN2VKC3H6R1");
        AddMessage(context, privateConversationId, binh, SecretPhrase, "01JBXQ7ZPT4M9WYFN2VKC3H6R2");

        await context.SaveChangesAsync();

        return new Arrangement(sharedConversationId, privateConversationId);
    }

    private static void AddMessage(
        ChatDbContext context,
        Guid conversationId,
        Guid authorId,
        string body,
        string clientKey)
    {
        FixedClock clock = new();

        Message message = Message.Send(
            Guid.CreateVersion7(),
            conversationId,
            seq: 1,
            authorId,
            ClientMessageKey.Parse(clientKey),
            MessageBody.Create(body),
            clock);

        message.ClearDomainEvents();
        context.Messages.Add(message);
    }

    private sealed record Arrangement(Guid SharedConversationId, Guid PrivateConversationId);
}
