using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using InternalChat.Api.Contracts;
using InternalChat.Domain.Common;
using InternalChat.Domain.Conversations;
using InternalChat.Domain.Messages;
using InternalChat.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace InternalChat.IntegrationTests.Fixtures;

/// <summary>
/// Base for the US2 messaging tests: the real API over HTTP against the containerised stack.
/// </summary>
/// <remarks>
/// <para>
/// The send and history helpers deliberately speak HTTP rather than calling a use case directly.
/// FR-011's idempotency is expressed partly in status codes — 201 for a new message, 200 for a
/// replay — and a test that invoked the handler would have to assert on a return value instead,
/// leaving the mapping from result to status code the one part nothing covers. That mapping is
/// exactly where a retry starts looking like a failure to a client.
/// </para>
/// <para>
/// Responses are deserialized into the API's own DTOs rather than into anonymous shapes, so a
/// renamed property fails here as well as in the contract suite.
/// </para>
/// </remarks>
public abstract class MessagingTestBase : IntegrationTestBase
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private ApiFactory? _api;

    protected MessagingTestBase(StackFixture stack)
        : base(stack)
    {
    }

    /// <summary>The hosted API.</summary>
    protected ApiFactory Api => _api ?? throw new InvalidOperationException(
        "The API host is created in InitializeAsync. A test reaching it earlier is running outside "
        + "the xUnit lifecycle.");

    /// <inheritdoc />
    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        _api = new ApiFactory(Stack);
    }

    /// <inheritdoc />
    public override async Task DisposeAsync()
    {
        if (_api is not null)
        {
            await _api.DisposeAsync();
        }

        await base.DisposeAsync();
    }

    /// <summary>An HTTP client carrying a real Keycloak token for <paramref name="username"/>.</summary>
    protected async Task<HttpClient> AuthenticatedClientAsync(string username)
    {
        string token = await Stack.IssueAccessTokenAsync(username);

        HttpClient client = Api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return client;
    }

    /// <summary>
    /// Seeds a direct conversation between two employees, with both memberships.
    /// </summary>
    /// <remarks>
    /// Built through <see cref="Conversation.CreateDirect"/> so the <c>direct_key</c> is the one the
    /// application would compute. A hand-written key would let a test pass while deduplication
    /// looked for a different string.
    /// </remarks>
    protected async Task<Guid> SeedDirectConversationAsync(Guid firstEmployeeId, Guid secondEmployeeId)
    {
        await using ChatDbContext context = CreateDbContext();

        FixedClock clock = new();

        Conversation conversation = Conversation.CreateDirect(
            Guid.CreateVersion7(),
            firstEmployeeId,
            secondEmployeeId,
            clock);

        conversation.ClearDomainEvents();

        context.Conversations.Add(conversation);

        context.Memberships.Add(
            Membership.Join(conversation.Id, firstEmployeeId, MembershipRole.Member, 0, clock));
        context.Memberships.Add(
            Membership.Join(conversation.Id, secondEmployeeId, MembershipRole.Member, 0, clock));

        await context.SaveChangesAsync();

        return conversation.Id;
    }

    /// <summary>
    /// Inserts messages at chosen instants, allocating sequences through the conversation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The send endpoint stamps <c>sent_at</c> from the server clock, which is exactly right for
    /// production and useless for a test that needs rows on either side of a month boundary. These
    /// go in directly — but still through <see cref="Message.Send"/> and
    /// <see cref="Conversation.AllocateSequence"/>, so the sequence numbering and the
    /// sequence-to-timestamp relationship are the ones the application produces.
    /// </para>
    /// <para>
    /// Timestamps are supplied in ascending order by every caller. That is the invariant the real
    /// send path maintains, and a test that seeded them out of order would be asserting pagination
    /// against a state the system cannot reach.
    /// </para>
    /// </remarks>
    protected async Task<IReadOnlyList<Guid>> SeedMessagesAsync(
        Guid conversationId,
        Guid authorId,
        IEnumerable<DateTimeOffset> sentAt)
    {
        ArgumentNullException.ThrowIfNull(sentAt);

        await using ChatDbContext context = CreateDbContext();

        Conversation conversation = await context.Conversations.SingleAsync(c => c.Id == conversationId);

        List<Guid> ids = [];
        FixedClock clock = new();

        foreach (DateTimeOffset instant in sentAt)
        {
            clock.UtcNow = instant;

            Message message = Message.Send(
                Guid.CreateVersion7(),
                conversationId,
                conversation.AllocateSequence(),
                authorId,
                ClientMessageKey.New(),
                MessageBody.Create($"seeded at {instant:O}"),
                clock);

            message.ClearDomainEvents();

            context.Messages.Add(message);

            // The dedup row is written by the send use case in the same transaction. Seeding
            // without it would leave a state the application never produces, and a later retry of
            // one of these keys would then be admitted as a new message.
            context.MessageDeduplication.Add(new Infrastructure.Persistence.Messages.MessageDeduplicationRecord
            {
                ConversationId = conversationId,
                ClientMessageKey = message.ClientMessageKey.Value,
                MessageId = message.Id,
                SentAt = message.SentAt,
            });

            ids.Add(message.Id);
        }

        conversation.ClearDomainEvents();

        await context.SaveChangesAsync();

        return ids;
    }

    /// <summary>Posts a message and returns the raw response, so a test can assert on the status.</summary>
    protected static Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        Guid conversationId,
        string clientMessageKey,
        string body)
    {
        ArgumentNullException.ThrowIfNull(client);

        return client.PostAsJsonAsync(
            new Uri($"/api/v1/conversations/{conversationId}/messages", UriKind.Relative),
            new SendMessageRequest(clientMessageKey, body, AttachmentIds: null, Mentions: null),
            SerializerOptions);
    }

    /// <summary>Reads one page of history.</summary>
    /// <param name="query">
    /// Appended verbatim, so a test can exercise the documented parameters — <c>beforeSeq</c>,
    /// <c>afterSeq</c>, <c>limit</c> — rather than a hand-rolled paging scheme.
    /// </param>
    protected static async Task<MessagePageResponse> HistoryAsync(
        HttpClient client,
        Guid conversationId,
        string? query = null)
    {
        ArgumentNullException.ThrowIfNull(client);

        string suffix = string.IsNullOrEmpty(query) ? string.Empty : $"?{query}";

        using HttpResponseMessage response = await client.GetAsync(
            new Uri($"/api/v1/conversations/{conversationId}/messages{suffix}", UriKind.Relative));

        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<MessagePageResponse>(SerializerOptions)
            ?? throw new InvalidOperationException("The history endpoint returned an empty body.");
    }

    /// <summary>Deserializes a message from a send response.</summary>
    protected static async Task<MessageResponse> ReadMessageAsync(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        return await response.Content.ReadFromJsonAsync<MessageResponse>(SerializerOptions)
            ?? throw new InvalidOperationException("The send endpoint returned an empty body.");
    }

    /// <summary>How many message rows exist for a conversation, read straight from PostgreSQL.</summary>
    /// <remarks>
    /// Counted in the database rather than inferred from a history page. FR-011 is about rows, and
    /// a history endpoint that deduplicated on read would make a duplicated row invisible to any
    /// assertion made through the API.
    /// </remarks>
    protected async Task<int> CountMessagesAsync(Guid conversationId)
    {
        await using ChatDbContext context = CreateDbContext();

        return await context.Messages.CountAsync(m => m.ConversationId == conversationId);
    }

    /// <summary>A syntactically valid ULID, distinct per <paramref name="seed"/>.</summary>
    /// <remarks>
    /// Derived rather than random so a failure reproduces with the same keys, and uppercase
    /// Crockford base32 because <c>ClientMessageKey</c> validates the alphabet.
    /// </remarks>
    protected static string ClientKey(int seed) =>
        string.Concat("01J", seed.ToString("D23", System.Globalization.CultureInfo.InvariantCulture));
}
