using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using InternalChat.Api.Contracts;
using InternalChat.Domain.Conversations;
using InternalChat.Infrastructure.Persistence;
using InternalChat.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;

namespace InternalChat.IntegrationTests.Conversations;

/// <summary>
/// T085 — two concurrent direct-conversation creations produce one conversation.
/// </summary>
/// <remarks>
/// <para>
/// The failure this prevents is not a duplicate row; it is a <em>split conversation</em>. If two
/// colleagues click "message" on each other at the same moment and two rows are created, each ends
/// up in a different one, each sees their own messages and none of the other's, and neither has any
/// indication why. Nothing errors, so nothing alerts.
/// </para>
/// <para>
/// The mechanism under test is the unique partial index on <c>direct_key</c> plus the canonical
/// sorted key that feeds it (data-model.md). Both halves matter and only the combination works: a
/// unique index on a key that depended on argument order would index two different strings for the
/// same pair and permit exactly the duplicate it exists to stop.
/// </para>
/// </remarks>
public sealed class DirectDedupeTests : MessagingTestBase
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public DirectDedupeTests(StackFixture stack)
        : base(stack)
    {
    }

    /// <summary>
    /// Both participants creating at once yields one conversation, and both are served it.
    /// </summary>
    /// <remarks>
    /// Deliberately driven from <em>both</em> sides rather than twice from one. Same-caller races
    /// are the easy case; the interesting one is two different callers, because that is where a key
    /// built from "me and them" in caller order produces two different strings.
    /// </remarks>
    [Fact]
    public async Task Both_participants_creating_at_once_produce_one_conversation()
    {
        const string First = "an.nguyen";
        const string Second = "binh.tran";

        (Guid firstId, Guid secondId) = await SeedPairAsync(First, Second);

        using HttpClient firstClient = await AuthenticatedClientAsync(First);
        using HttpClient secondClient = await AuthenticatedClientAsync(Second);

        Task<HttpResponseMessage>[] attempts =
        [
            CreateDirectAsync(firstClient, secondId),
            CreateDirectAsync(secondClient, firstId),
        ];

        HttpResponseMessage[] responses = await Task.WhenAll(attempts);

        try
        {
            foreach (HttpResponseMessage response in responses)
            {
                Assert.True(
                    response.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK,
                    $"""
                    A concurrent direct-conversation creation returned {(int)response.StatusCode}
                    {response.StatusCode}. Both callers must be served — the contract documents 201
                    for the creator and 200 for the one handed the existing conversation. A 409 or a
                    500 here means the unique violation reached the caller, and a client that sees an
                    error after clicking "message" will click again.
                    """);
            }

            ConversationResponse[] served = await Task.WhenAll(responses.Select(ReadConversationAsync));

            // The same conversation, so both people are in the same room.
            Assert.Equal(served[0].Id, served[1].Id);
            Assert.Equal(ConversationKinds.Direct, served[0].Kind);
        }
        finally
        {
            foreach (HttpResponseMessage response in responses)
            {
                response.Dispose();
            }
        }

        await AssertSingleDirectConversationAsync(firstId, secondId);
    }

    /// <summary>
    /// Many simultaneous attempts still produce one conversation.
    /// </summary>
    /// <remarks>
    /// Two racers can be won by luck. Eight from both sides exercises the retry path repeatedly —
    /// an implementation that catches the unique violation but then fails to re-read the winning row
    /// will surface here even when it survived the two-caller case.
    /// </remarks>
    [Fact]
    public async Task Eight_simultaneous_attempts_still_produce_one_conversation()
    {
        const string First = "chi.le";
        const string Second = "dung.pham";

        (Guid firstId, Guid secondId) = await SeedPairAsync(First, Second);

        using HttpClient firstClient = await AuthenticatedClientAsync(First);
        using HttpClient secondClient = await AuthenticatedClientAsync(Second);

        List<Task<HttpResponseMessage>> attempts = [];

        for (int i = 0; i < 4; i++)
        {
            attempts.Add(CreateDirectAsync(firstClient, secondId));
            attempts.Add(CreateDirectAsync(secondClient, firstId));
        }

        HttpResponseMessage[] responses = await Task.WhenAll(attempts);

        try
        {
            Assert.All(
                responses,
                r => Assert.True(
                    r.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK,
                    $"Attempt returned {(int)r.StatusCode} {r.StatusCode}."));

            ConversationResponse[] served = await Task.WhenAll(responses.Select(ReadConversationAsync));

            Assert.Single(served.Select(c => c.Id).Distinct());

            // Exactly one caller created it. More than one 201 would mean two rows existed at some
            // point, even if only one survives.
            Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        }
        finally
        {
            foreach (HttpResponseMessage response in responses)
            {
                response.Dispose();
            }
        }

        await AssertSingleDirectConversationAsync(firstId, secondId);
    }

    /// <summary>
    /// A later request for the same pair returns the existing conversation with its history intact.
    /// </summary>
    /// <remarks>
    /// The sequential case, and the one a user actually performs: open a chat, close the tab, open
    /// it again next week. Returning a fresh empty conversation would read as the history having
    /// been deleted.
    /// </remarks>
    [Fact]
    public async Task Reopening_a_direct_conversation_returns_the_existing_one_with_its_messages()
    {
        const string First = "hai.vo";
        const string Second = "khanh.dang";

        (Guid firstId, Guid secondId) = await SeedPairAsync(First, Second);

        using HttpClient client = await AuthenticatedClientAsync(First);

        Guid conversationId;

        using (HttpResponseMessage created = await CreateDirectAsync(client, secondId))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            conversationId = (await ReadConversationAsync(created)).Id;
        }

        using (HttpResponseMessage sent = await SendAsync(client, conversationId, ClientKey(7), "still here"))
        {
            Assert.Equal(HttpStatusCode.Created, sent.StatusCode);
        }

        using HttpResponseMessage reopened = await CreateDirectAsync(client, secondId);

        Assert.Equal(HttpStatusCode.OK, reopened.StatusCode);

        ConversationResponse conversation = await ReadConversationAsync(reopened);

        Assert.Equal(conversationId, conversation.Id);
        Assert.Equal(1, conversation.LastSeq);

        await AssertSingleDirectConversationAsync(firstId, secondId);
    }

    /// <summary>
    /// A direct conversation with oneself is refused.
    /// </summary>
    /// <remarks>
    /// It satisfies a naive "exactly two members" count with one row, and the domain refuses it
    /// (<see cref="Conversation.CreateDirect"/>). Asserted through the API because that is where an
    /// unvalidated <c>memberIds</c> would let it through before the domain was ever consulted.
    /// </remarks>
    [Fact]
    public async Task A_direct_conversation_with_oneself_is_refused()
    {
        const string Caller = "giang.hoang";

        Guid callerId;

        await using (ChatDbContext context = CreateDbContext())
        {
            callerId = await TestData.SeedEmployeeAsync(context, Caller);
        }

        using HttpClient client = await AuthenticatedClientAsync(Caller);

        using HttpResponseMessage response = await CreateDirectAsync(client, callerId);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        await using ChatDbContext verification = CreateDbContext();
        Assert.Equal(0, await verification.Conversations.CountAsync());
    }

    /// <summary>
    /// A deactivated counterpart cannot be brought into a new conversation.
    /// </summary>
    /// <remarks>
    /// The contract documents 422 for this. It is checked at creation as well as at add-member
    /// because creation is the path that does not go through <c>AddMember</c>, and FR-007's rule is
    /// about the conversation ending up with a deactivated member either way.
    /// </remarks>
    [Fact]
    public async Task A_deactivated_employee_cannot_be_given_a_new_direct_conversation()
    {
        const string Caller = "minh.do";

        Guid deactivatedId;

        await using (ChatDbContext context = CreateDbContext())
        {
            await TestData.SeedEmployeeAsync(context, Caller);
            deactivatedId = await TestData.SeedEmployeeAsync(context, "nga.ngo", active: false);
        }

        using HttpClient client = await AuthenticatedClientAsync(Caller);

        using HttpResponseMessage response = await CreateDirectAsync(client, deactivatedId);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        await using ChatDbContext verification = CreateDbContext();
        Assert.Equal(0, await verification.Conversations.CountAsync());
    }

    private static Task<HttpResponseMessage> CreateDirectAsync(HttpClient client, Guid counterpartId) =>
        client.PostAsJsonAsync(
            new Uri("/api/v1/conversations", UriKind.Relative),
            new CreateConversationRequest(
                ConversationKinds.Direct,
                Name: null,
                MemberIds: [counterpartId],
                HistoryVisibility: null),
            SerializerOptions);

    private static async Task<ConversationResponse> ReadConversationAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<ConversationResponse>(SerializerOptions)
        ?? throw new InvalidOperationException("The conversation endpoint returned an empty body.");

    /// <summary>
    /// Asserts one conversation exists for the pair, keyed as the domain would key it.
    /// </summary>
    /// <remarks>
    /// The key is recomputed with <see cref="Conversation.BuildDirectKey"/> rather than read back
    /// from the row, so this also catches the case where two rows exist under two different
    /// spellings of the same pair — which a bare "count the rows" assertion would find, but a
    /// "look up by key" assertion would not.
    /// </remarks>
    private async Task AssertSingleDirectConversationAsync(Guid firstId, Guid secondId)
    {
        await using ChatDbContext context = CreateDbContext();

        string expectedKey = Conversation.BuildDirectKey(firstId, secondId);

        List<string?> directKeys = await context.Conversations
            .Where(c => c.Kind == ConversationKind.Direct)
            .Select(c => c.DirectKey)
            .ToListAsync();

        Assert.Single(directKeys);
        Assert.Equal(expectedKey, directKeys[0]);

        Guid conversationId = await context.Conversations
            .Where(c => c.DirectKey == expectedKey)
            .Select(c => c.Id)
            .SingleAsync();

        // Two memberships, both live. A deduplicated conversation that only added the winner would
        // leave the loser outside the conversation they asked for.
        List<Guid> members = await context.Memberships
            .Where(m => m.ConversationId == conversationId && m.RemovedAt == null)
            .Select(m => m.EmployeeId)
            .OrderBy(id => id)
            .ToListAsync();

        Assert.Equal(2, members.Count);
        Assert.Contains(firstId, members);
        Assert.Contains(secondId, members);
    }

    private async Task<(Guid FirstId, Guid SecondId)> SeedPairAsync(string first, string second)
    {
        await using ChatDbContext context = CreateDbContext();

        Guid firstId = await TestData.SeedEmployeeAsync(context, first);
        Guid secondId = await TestData.SeedEmployeeAsync(context, second);

        return (firstId, secondId);
    }
}
