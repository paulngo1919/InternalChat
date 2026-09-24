using System.Net;
using InternalChat.Api.Contracts;
using InternalChat.Infrastructure.Persistence;
using InternalChat.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;

namespace InternalChat.IntegrationTests.Messages;

/// <summary>
/// T082 — FR-011: a duplicate <c>clientMessageKey</c> returns 200 and leaves exactly one row.
/// </summary>
/// <remarks>
/// <para>
/// This is the requirement the whole send path is shaped around. A client that sends, loses the
/// response to a dropped connection, and retries must not produce two messages — and must not be
/// told its retry failed either, because a client told "conflict" has no way to know whether the
/// original landed.
/// </para>
/// <para>
/// Row counts are read from PostgreSQL rather than from a history page throughout. An
/// implementation that deduplicated on read would satisfy every API-level assertion while quietly
/// storing two rows, and the second one would surface a year later in an export or a search result.
/// </para>
/// </remarks>
public sealed class IdempotentSendTests : MessagingTestBase
{
    public IdempotentSendTests(StackFixture stack)
        : base(stack)
    {
    }

    /// <summary>The core assertion: same key twice, 201 then 200, one row.</summary>
    [Fact]
    public async Task A_retried_send_returns_the_original_message_and_creates_no_second_row()
    {
        const string Sender = "an.nguyen";
        Guid conversationId = await SeedPairAsync(Sender, "binh.tran");

        using HttpClient client = await AuthenticatedClientAsync(Sender);

        string key = ClientKey(1);

        using HttpResponseMessage first = await SendAsync(client, conversationId, key, "hello once");
        using HttpResponseMessage second = await SendAsync(client, conversationId, key, "hello once");

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        // 200, not 201 and not 409. The status is how the client learns this was a replay rather
        // than a new message, which is what lets it reconcile its optimistic copy.
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        MessageResponse original = await ReadMessageAsync(first);
        MessageResponse replay = await ReadMessageAsync(second);

        // The same message, not merely an equivalent one. A new id or a new sequence would make the
        // client apply it twice, because dedup on the client is keyed by id.
        Assert.Equal(original.Id, replay.Id);
        Assert.Equal(original.Seq, replay.Seq);
        Assert.Equal(original.SentAt, replay.SentAt);

        Assert.Equal(1, await CountMessagesAsync(conversationId));
    }

    /// <summary>
    /// A replay whose body differs still returns the original, unchanged.
    /// </summary>
    /// <remarks>
    /// The key identifies the send attempt, not the text. Treating a changed body as an edit would
    /// turn a network retry into a silent content rewrite — and there is no legitimate client that
    /// reuses a key with new text, so the case can only arise from a bug or an attack.
    /// </remarks>
    [Fact]
    public async Task A_replay_with_a_different_body_does_not_rewrite_the_original()
    {
        const string Sender = "chi.le";
        Guid conversationId = await SeedPairAsync(Sender, "dung.pham");

        using HttpClient client = await AuthenticatedClientAsync(Sender);

        string key = ClientKey(2);

        using HttpResponseMessage first = await SendAsync(client, conversationId, key, "the original text");
        using HttpResponseMessage second = await SendAsync(client, conversationId, key, "substituted text");

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        MessageResponse replay = await ReadMessageAsync(second);

        Assert.Equal("the original text", replay.Body);
        Assert.Null(replay.EditedAt);

        await using ChatDbContext context = CreateDbContext();

        string? stored = await context.Messages
            .Where(m => m.ConversationId == conversationId)
            .Select(m => m.Body!.Value)
            .SingleAsync();

        Assert.Equal("the original text", stored);
    }

    /// <summary>
    /// Two sends racing on the same key produce one message.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The case a <c>SELECT</c>-then-<c>INSERT</c> implementation fails and a single-request test
    /// never reaches: both attempts look up the key, both find nothing, and both insert. It is also
    /// the realistic shape of a retry — a client that gave up waiting and resent while the first
    /// request was still in flight.
    /// </para>
    /// <para>
    /// Both responses are accepted as either 201 or 200, because which request wins is genuinely
    /// undefined. What is not undefined is that exactly one row exists and both callers were handed
    /// the same message id.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Two_concurrent_sends_with_the_same_key_produce_one_message()
    {
        const string Sender = "hai.vo";
        Guid conversationId = await SeedPairAsync(Sender, "khanh.dang");

        using HttpClient client = await AuthenticatedClientAsync(Sender);

        string key = ClientKey(3);

        Task<HttpResponseMessage>[] attempts =
        [
            SendAsync(client, conversationId, key, "raced"),
            SendAsync(client, conversationId, key, "raced"),
        ];

        HttpResponseMessage[] responses = await Task.WhenAll(attempts);

        try
        {
            foreach (HttpResponseMessage response in responses)
            {
                Assert.True(
                    response.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK,
                    $"""
                    A concurrent duplicate send returned {(int)response.StatusCode}
                    {response.StatusCode}. Both racers must be served: one creates the message and
                    the other is told it already exists. A 409 or a 500 here means the unique
                    violation reached the caller instead of being turned into the idempotent answer
                    FR-011 requires.
                    """);
            }

            Assert.Equal(1, await CountMessagesAsync(conversationId));

            MessageResponse[] bodies = await Task.WhenAll(responses.Select(ReadMessageAsync));

            Assert.Equal(bodies[0].Id, bodies[1].Id);
            Assert.Equal(bodies[0].Seq, bodies[1].Seq);
        }
        finally
        {
            foreach (HttpResponseMessage response in responses)
            {
                response.Dispose();
            }
        }
    }

    /// <summary>
    /// The same key in a different conversation is a different message.
    /// </summary>
    /// <remarks>
    /// Uniqueness is scoped to the conversation, not global. A client that generates one key per
    /// composer keystroke could reasonably reuse a key across conversations; deduplicating globally
    /// would silently drop the second message and there would be nothing to see but an absence.
    /// </remarks>
    [Fact]
    public async Task The_same_key_in_another_conversation_is_a_separate_message()
    {
        const string Sender = "giang.hoang";

        Guid firstConversation = await SeedPairAsync(Sender, "minh.do");
        Guid secondConversation;

        await using (ChatDbContext context = CreateDbContext())
        {
            Guid sender = await context.Employees
                .Where(e => e.ExternalSubject == TestData.SubjectFor(Sender))
                .Select(e => e.Id)
                .SingleAsync();

            Guid third = await TestData.SeedEmployeeAsync(context, "nga.ngo");

            secondConversation = await SeedDirectConversationAsync(sender, third);
        }

        using HttpClient client = await AuthenticatedClientAsync(Sender);

        string key = ClientKey(4);

        using HttpResponseMessage first = await SendAsync(client, firstConversation, key, "to one");
        using HttpResponseMessage second = await SendAsync(client, secondConversation, key, "to another");

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);

        Assert.Equal(1, await CountMessagesAsync(firstConversation));
        Assert.Equal(1, await CountMessagesAsync(secondConversation));

        MessageResponse one = await ReadMessageAsync(first);
        MessageResponse other = await ReadMessageAsync(second);

        Assert.NotEqual(one.Id, other.Id);
    }

    /// <summary>
    /// A malformed key is refused before anything is written.
    /// </summary>
    /// <remarks>
    /// FR-011 depends on the client being able to reproduce the key on retry. A key that is not a
    /// ULID is one the client may not reproduce, so accepting it would store a message that
    /// duplicates itself on the next attempt — a 400 now is the only outcome that keeps the
    /// guarantee honest.
    /// </remarks>
    [Fact]
    public async Task A_key_that_is_not_a_ulid_is_refused_and_writes_nothing()
    {
        const string Sender = "phuc.duong";
        Guid conversationId = await SeedPairAsync(Sender, "quyen.ly");

        using HttpClient client = await AuthenticatedClientAsync(Sender);

        using HttpResponseMessage response = await SendAsync(client, conversationId, "not-a-ulid", "rejected");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await CountMessagesAsync(conversationId));
    }

    /// <summary>
    /// Sequences stay gapless when distinct keys are sent concurrently.
    /// </summary>
    /// <remarks>
    /// The other half of the allocator's contract. Idempotency stops duplicates; this stops the
    /// opposite failure, where two concurrent sends allocate the same sequence and one is lost to
    /// the unique index — or worse, both are stored and keyset pagination starts skipping rows.
    /// </remarks>
    [Fact]
    public async Task Concurrent_sends_with_distinct_keys_receive_gapless_sequences()
    {
        const string Sender = "son.truong";
        const int Sends = 12;

        Guid conversationId = await SeedPairAsync(Sender, "tuan.mai");

        using HttpClient client = await AuthenticatedClientAsync(Sender);

        HttpResponseMessage[] responses = await Task.WhenAll(
            Enumerable.Range(100, Sends)
                .Select(i => SendAsync(client, conversationId, ClientKey(i), $"message {i}")));

        try
        {
            Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        }
        finally
        {
            foreach (HttpResponseMessage response in responses)
            {
                response.Dispose();
            }
        }

        await using ChatDbContext context = CreateDbContext();

        List<long> sequences = await context.Messages
            .Where(m => m.ConversationId == conversationId)
            .Select(m => m.Seq)
            .OrderBy(seq => seq)
            .ToListAsync();

        Assert.Equal(Enumerable.Range(1, Sends).Select(i => (long)i), sequences);

        long lastSeq = await context.Conversations
            .Where(c => c.Id == conversationId)
            .Select(c => c.LastSeq)
            .SingleAsync();

        Assert.Equal(Sends, lastSeq);
    }

    /// <summary>Seeds two employees and a direct conversation between them.</summary>
    private async Task<Guid> SeedPairAsync(string sender, string recipient)
    {
        Guid senderId;
        Guid recipientId;

        await using (ChatDbContext context = CreateDbContext())
        {
            senderId = await TestData.SeedEmployeeAsync(context, sender);
            recipientId = await TestData.SeedEmployeeAsync(context, recipient);
        }

        return await SeedDirectConversationAsync(senderId, recipientId);
    }
}
