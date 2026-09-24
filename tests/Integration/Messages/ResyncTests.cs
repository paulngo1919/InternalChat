using InternalChat.Api.Contracts;
using InternalChat.Infrastructure.Persistence;
using InternalChat.IntegrationTests.Fixtures;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;

namespace InternalChat.IntegrationTests.Messages;

/// <summary>
/// T084 — FR-018 and SC-022: <c>Resync</c> returns everything above <c>lastSeenSeq</c>, exactly once.
/// </summary>
/// <remarks>
/// <para>
/// <c>Resync</c> is what makes the hub disposable. Nothing is buffered per connection — buffering
/// would make server memory a function of how many clients are disconnected, which breaks at 7,000
/// connections — so a reconnecting client recovers its gap with a query instead. That means the
/// correctness of every "message never lost" claim in the spec rests on this one method.
/// </para>
/// <para>
/// "Exactly once" is asserted as a property of the returned set, not of a single call: the same
/// <c>lastSeenSeq</c> asked twice must produce the same answer, and the boundary must be exclusive
/// so the message the client already has is not re-applied. Both are ways an off-by-one here shows
/// up as a duplicated message in the UI rather than as an error anywhere.
/// </para>
/// </remarks>
public sealed class ResyncTests : MessagingTestBase
{
    public ResyncTests(StackFixture stack)
        : base(stack)
    {
    }

    /// <summary>The core assertion: everything above the floor, nothing at or below it.</summary>
    [Fact]
    public async Task Resync_returns_every_message_above_the_last_seen_sequence()
    {
        const string Reader = "an.nguyen";
        const int Total = 25;
        const long LastSeen = 10;

        (Guid conversationId, Guid readerId) = await SeedConversationWithMessagesAsync(Reader, "binh.tran", Total);

        await using HubConnection hub = await ConnectAsync(Reader);

        IReadOnlyDictionary<Guid, List<MessageResponse>> result = await ResyncAsync(
            hub,
            new Dictionary<Guid, long> { [conversationId] = LastSeen });

        List<MessageResponse> recovered = result[conversationId];

        Assert.Equal(Total - (int)LastSeen, recovered.Count);

        // Strictly above. An inclusive boundary would re-deliver the message the client cited as
        // already seen, which is the duplicate SC-022 forbids.
        Assert.DoesNotContain(recovered, m => m.Seq <= LastSeen);

        Assert.Equal(
            Enumerable.Range((int)LastSeen + 1, Total - (int)LastSeen).Select(i => (long)i),
            recovered.Select(m => m.Seq));

        Assert.All(recovered, m => Assert.Equal(readerId, m.AuthorId));
    }

    /// <summary>
    /// Asking twice with the same floor returns the same messages, not more.
    /// </summary>
    /// <remarks>
    /// The idempotency half. A reconnect storm — a laptop waking on a flaky network — calls
    /// <c>Resync</c> repeatedly with the same floor, and an implementation that advanced a
    /// server-side cursor as a side effect would return the gap once and then nothing, losing
    /// messages for a client that had not finished applying the first batch.
    /// </remarks>
    [Fact]
    public async Task Resync_is_repeatable_and_has_no_side_effect_on_the_floor()
    {
        const string Reader = "chi.le";
        const int Total = 15;
        const long LastSeen = 5;

        (Guid conversationId, _) = await SeedConversationWithMessagesAsync(Reader, "dung.pham", Total);

        await using HubConnection hub = await ConnectAsync(Reader);

        Dictionary<Guid, long> request = new() { [conversationId] = LastSeen };

        IReadOnlyDictionary<Guid, List<MessageResponse>> first = await ResyncAsync(hub, request);
        IReadOnlyDictionary<Guid, List<MessageResponse>> second = await ResyncAsync(hub, request);

        Assert.Equal(
            first[conversationId].Select(m => m.Seq),
            second[conversationId].Select(m => m.Seq));

        Assert.Equal(Total - (int)LastSeen, second[conversationId].Count);
    }

    /// <summary>
    /// A floor of zero returns the whole visible history.
    /// </summary>
    /// <remarks>
    /// What a client sends on its very first connect. Zero must mean "I have nothing", not "I have
    /// message zero" — and since sequences start at one, an implementation using <c>&gt;=</c>
    /// somewhere would be indistinguishable here from the correct one except that it also breaks the
    /// case above.
    /// </remarks>
    [Fact]
    public async Task A_floor_of_zero_returns_the_whole_conversation()
    {
        const string Reader = "hai.vo";
        const int Total = 12;

        (Guid conversationId, _) = await SeedConversationWithMessagesAsync(Reader, "khanh.dang", Total);

        await using HubConnection hub = await ConnectAsync(Reader);

        IReadOnlyDictionary<Guid, List<MessageResponse>> result = await ResyncAsync(
            hub,
            new Dictionary<Guid, long> { [conversationId] = 0 });

        Assert.Equal(Total, result[conversationId].Count);
        Assert.Equal(1, result[conversationId].Min(m => m.Seq));
    }

    /// <summary>
    /// A conversation the caller does not belong to yields nothing, and does not fail the batch.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Resync</c> takes a batch, and the contract scopes authorization "per entry". A stale
    /// client will routinely include a conversation it was just removed from, so throwing would make
    /// removal break the reconnect for every <em>other</em> conversation the client is still in.
    /// </para>
    /// <para>
    /// Silently omitting it is also the SC-017-consistent answer: the response is identical to one
    /// for a conversation that does not exist, so the batch cannot be used to probe which
    /// conversations are real.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Resync_omits_conversations_the_caller_is_not_a_member_of()
    {
        const string Reader = "giang.hoang";
        const int Total = 8;

        (Guid ownConversation, _) = await SeedConversationWithMessagesAsync(Reader, "minh.do", Total);

        Guid strangersConversation;
        Guid nonexistent = Guid.CreateVersion7();

        await using (ChatDbContext context = CreateDbContext())
        {
            Guid strangerOne = await TestData.SeedEmployeeAsync(context, "nga.ngo");
            Guid strangerTwo = await TestData.SeedEmployeeAsync(context, "phuc.duong");

            strangersConversation = await SeedDirectConversationAsync(strangerOne, strangerTwo);

            await SeedMessagesAsync(
                strangersConversation,
                strangerOne,
                Enumerable.Range(0, 5).Select(i => DateTimeOffset.UtcNow.AddMinutes(-30 + i)));
        }

        await using HubConnection hub = await ConnectAsync(Reader);

        IReadOnlyDictionary<Guid, List<MessageResponse>> result = await ResyncAsync(
            hub,
            new Dictionary<Guid, long>
            {
                [ownConversation] = 0,
                [strangersConversation] = 0,
                [nonexistent] = 0,
            });

        Assert.Equal(Total, result[ownConversation].Count);

        // A refusal and a not-found must be the same shape here too — either absent, or present and
        // empty, but identical to each other.
        bool strangerEmpty = !result.TryGetValue(strangersConversation, out List<MessageResponse>? stranger)
            || stranger.Count == 0;
        bool missingEmpty = !result.TryGetValue(nonexistent, out List<MessageResponse>? missing)
            || missing.Count == 0;

        Assert.True(strangerEmpty, "Resync returned messages from a conversation the caller is not in.");
        Assert.True(missingEmpty);

        Assert.Equal(
            result.ContainsKey(strangersConversation),
            result.ContainsKey(nonexistent));
    }

    /// <summary>
    /// A very stale floor is capped rather than returning unbounded rows.
    /// </summary>
    /// <remarks>
    /// A client offline for a month would otherwise ask for tens of thousands of messages in one
    /// hub invocation, on a connection with no backpressure and a budget measured in hundreds of
    /// milliseconds. The cap is what turns that into several bounded round trips; a client detects
    /// it by comparing the highest returned sequence against the conversation's <c>lastSeq</c> and
    /// asking again.
    /// </remarks>
    [Fact]
    public async Task A_very_stale_floor_is_capped_to_a_bounded_page()
    {
        const string Reader = "quyen.ly";
        const int Total = 260;

        (Guid conversationId, _) = await SeedConversationWithMessagesAsync(Reader, "son.truong", Total);

        await using HubConnection hub = await ConnectAsync(Reader);

        IReadOnlyDictionary<Guid, List<MessageResponse>> result = await ResyncAsync(
            hub,
            new Dictionary<Guid, long> { [conversationId] = 0 });

        List<MessageResponse> recovered = result[conversationId];

        Assert.True(
            recovered.Count < Total,
            $"Resync returned all {Total} messages in one invocation. An unbounded catch-up is a "
            + "denial of service a returning laptop performs by accident.");

        // Capped from the *oldest* end, so the client can page forward by raising its floor. Capping
        // from the newest end would leave a permanent hole the client has no way to name.
        Assert.Equal(1, recovered.Min(m => m.Seq));

        Assert.Equal(
            Enumerable.Range(1, recovered.Count).Select(i => (long)i),
            recovered.Select(m => m.Seq));
    }

    /// <summary>
    /// An empty batch is accepted and returns nothing.
    /// </summary>
    /// <remarks>
    /// What a client with no conversations sends on connect. It must not be an error — a new
    /// employee's first sign-in would otherwise fail at the hub.
    /// </remarks>
    [Fact]
    public async Task An_empty_batch_returns_an_empty_result()
    {
        const string Reader = "tuan.mai";

        await using (ChatDbContext context = CreateDbContext())
        {
            await TestData.SeedEmployeeAsync(context, Reader);
        }

        await using HubConnection hub = await ConnectAsync(Reader);

        IReadOnlyDictionary<Guid, List<MessageResponse>> result = await ResyncAsync(hub, []);

        Assert.Empty(result);
    }

    private async Task<HubConnection> ConnectAsync(string username)
    {
        string token = await Stack.IssueAccessTokenAsync(username);

        HubConnection hub = HubClient.Create(Api, token);
        await hub.StartAsync();

        return hub;
    }

    private static Task<IReadOnlyDictionary<Guid, List<MessageResponse>>> ResyncAsync(
        HubConnection hub,
        Dictionary<Guid, long> lastSeen) =>
        hub.InvokeAsync<IReadOnlyDictionary<Guid, List<MessageResponse>>>("Resync", lastSeen);

    private async Task<(Guid ConversationId, Guid ReaderId)> SeedConversationWithMessagesAsync(
        string reader,
        string other,
        int messageCount)
    {
        Guid readerId;
        Guid otherId;

        await using (ChatDbContext context = CreateDbContext())
        {
            readerId = await TestData.SeedEmployeeAsync(context, reader);
            otherId = await TestData.SeedEmployeeAsync(context, other);
        }

        Guid conversationId = await SeedDirectConversationAsync(readerId, otherId);

        await SeedMessagesAsync(
            conversationId,
            readerId,
            Enumerable.Range(0, messageCount)
                .Select(i => DateTimeOffset.UtcNow.AddMinutes(-messageCount + i)));

        return (conversationId, readerId);
    }
}
