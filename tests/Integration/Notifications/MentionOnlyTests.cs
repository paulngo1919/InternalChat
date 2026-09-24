using System.Net.Http.Json;
using System.Text.Json;
using InternalChat.Api.Contracts;
using InternalChat.Application.Abstractions;
using InternalChat.Infrastructure.Persistence;
using InternalChat.IntegrationTests.Fixtures;
using InternalChat.Worker.Consumers;
using Microsoft.Extensions.DependencyInjection;

namespace InternalChat.IntegrationTests.Notifications;

/// <summary>
/// T127 — no push for an ordinary group message, but one for a mention (FR-034, FR-035).
/// </summary>
/// <remarks>
/// Runs <see cref="NotificationFanoutConsumer"/> directly, the same way
/// <c>DirectorySyncTests</c> runs <c>DirectorySyncConsumer</c> — <c>ActivatorUtilities</c> builds it
/// from the real container so every real dependency (membership, message, preference lookups) is
/// exercised, with only <see cref="IPushSender"/> substituted: no self-hostable stand-in exists for
/// a browser vendor's push service, so this is the one seam a fake belongs on, the same way a
/// unit test fakes an outbound HTTP boundary that Testcontainers cannot stand up.
/// </remarks>
public sealed class MentionOnlyTests : MessagingTestBase
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public MentionOnlyTests(StackFixture stack)
        : base(stack)
    {
    }

    [Fact]
    public async Task A_group_message_notifies_the_mentioned_member_but_not_an_unmentioned_one()
    {
        const string Admin = "an.nguyen";
        const string Mentioned = "binh.tran";
        const string NotMentioned = "chi.le";

        Guid adminId, mentionedId, notMentionedId;

        await using (ChatDbContext context = CreateDbContext())
        {
            adminId = await TestData.SeedEmployeeAsync(context, Admin);
            mentionedId = await TestData.SeedEmployeeAsync(context, Mentioned);
            notMentionedId = await TestData.SeedEmployeeAsync(context, NotMentioned);
        }

        await RegisterSubscriptionAsync(mentionedId);
        await RegisterSubscriptionAsync(notMentionedId);

        using HttpClient adminClient = await AuthenticatedClientAsync(Admin);

        Guid conversationId = await CreateGroupAsync(adminClient, "mention-only-test", [mentionedId, notMentionedId]);

        MessageResponse message = await SendMentioningAsync(adminClient, conversationId, [mentionedId]);

        RecordingPushSender recorder = new();
        await RunFanoutAsync(conversationId, message, adminId, recorder);

        Assert.Contains(mentionedId, recorder.NotifiedEmployeeIds);
        Assert.DoesNotContain(notMentionedId, recorder.NotifiedEmployeeIds);
    }

    [Fact]
    public async Task An_ordinary_group_message_with_no_mention_notifies_nobody()
    {
        const string Admin = "dung.pham";
        const string MemberOne = "giang.hoang";
        const string MemberTwo = "hai.vo";

        Guid adminId, memberOneId, memberTwoId;

        await using (ChatDbContext context = CreateDbContext())
        {
            adminId = await TestData.SeedEmployeeAsync(context, Admin);
            memberOneId = await TestData.SeedEmployeeAsync(context, MemberOne);
            memberTwoId = await TestData.SeedEmployeeAsync(context, MemberTwo);
        }

        await RegisterSubscriptionAsync(memberOneId);
        await RegisterSubscriptionAsync(memberTwoId);

        using HttpClient adminClient = await AuthenticatedClientAsync(Admin);

        Guid conversationId = await CreateGroupAsync(adminClient, "no-mention-test", [memberOneId, memberTwoId]);

        MessageResponse message = await SendMentioningAsync(adminClient, conversationId, mentions: []);

        RecordingPushSender recorder = new();
        await RunFanoutAsync(conversationId, message, adminId, recorder);

        Assert.Empty(recorder.NotifiedEmployeeIds);
    }

    /// <summary>The other half of FR-034: a direct message always notifies, mention or not.</summary>
    [Fact]
    public async Task A_direct_message_notifies_the_recipient_without_any_mention()
    {
        const string Sender = "khanh.dang";
        const string Recipient = "lan.bui";

        Guid senderId;
        Guid recipientId;

        await using (ChatDbContext context = CreateDbContext())
        {
            senderId = await TestData.SeedEmployeeAsync(context, Sender);
            recipientId = await TestData.SeedEmployeeAsync(context, Recipient);
        }

        await RegisterSubscriptionAsync(recipientId);

        Guid conversationId = await SeedDirectConversationAsync(senderId, recipientId);

        using HttpClient senderClient = await AuthenticatedClientAsync(Sender);

        using HttpResponseMessage response = await SendAsync(
            senderClient, conversationId, ClientKey(9700), "a plain direct message");
        response.EnsureSuccessStatusCode();

        MessageResponse message = await ReadMessageAsync(response);

        RecordingPushSender recorder = new();
        await RunFanoutAsync(conversationId, message, senderId, recorder);

        Assert.Contains(recipientId, recorder.NotifiedEmployeeIds);
    }

    private async Task RegisterSubscriptionAsync(Guid employeeId)
    {
        await using ChatDbContext context = CreateDbContext();

        Infrastructure.Persistence.Repositories.PushSubscriptionStore store = new(context, new FixedClock());

        await store.RegisterAsync(
            employeeId,
            new Uri($"https://push.example.invalid/{employeeId:N}"),
            p256dh: "test-p256dh-key",
            auth: "test-auth-secret",
            userAgent: "integration-test",
            CancellationToken.None);

        await context.SaveChangesAsync();
    }

    private static async Task<Guid> CreateGroupAsync(HttpClient adminClient, string name, IReadOnlyList<Guid> memberIds)
    {
        using HttpResponseMessage response = await adminClient.PostAsJsonAsync(
            new Uri("/api/v1/conversations", UriKind.Relative),
            new CreateConversationRequest("group", name, memberIds, "from_join"));

        response.EnsureSuccessStatusCode();

        ConversationResponse conversation = await response.Content.ReadFromJsonAsync<ConversationResponse>()
            ?? throw new InvalidOperationException("Conversation creation returned an empty body.");

        return conversation.Id;
    }

    private static async Task<MessageResponse> SendMentioningAsync(
        HttpClient client, Guid conversationId, IReadOnlyList<Guid> mentions)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            new Uri($"/api/v1/conversations/{conversationId}/messages", UriKind.Relative),
            new SendMessageRequest(ClientKey(Random.Shared.Next()), "hello group", AttachmentIds: null, mentions),
            SerializerOptions);

        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<MessageResponse>(SerializerOptions)
            ?? throw new InvalidOperationException("The send endpoint returned an empty body.");
    }

    /// <summary>Runs the real consumer for one already-sent message, with a fake push sender.</summary>
    private async Task RunFanoutAsync(Guid conversationId, MessageResponse message, Guid authorId, IPushSender sender)
    {
        using IServiceScope scope = Api.Services.CreateScope();

        NotificationFanoutConsumer consumer = ActivatorUtilities.CreateInstance<NotificationFanoutConsumer>(
            scope.ServiceProvider, sender);

        string payload = JsonSerializer.Serialize(
            new
            {
                conversationId,
                messageId = message.Id,
                seq = message.Seq,
                authorId,
                sentAt = message.SentAt,
                mentions = message.Mentions,
            },
            SerializerOptions);

        await consumer.HandleAsync(
            new MessageEnvelope(Guid.CreateVersion7(), "chat.message.sent.v1", payload, TraceParent: null, Attempt: 1));

        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
    }

    /// <summary>Records who a push was attempted for, without reaching an external service.</summary>
    private sealed class RecordingPushSender : IPushSender
    {
        private readonly List<Guid> _notified = [];

        public IReadOnlyList<Guid> NotifiedEmployeeIds => _notified;

        public Task<PushDeliveryResult> SendAsync(
            PushSubscriptionDescriptor subscription,
            PushPayload payload,
            CancellationToken cancellationToken = default)
        {
            // The subscription's endpoint carries the employee id — see RegisterSubscriptionAsync.
            string idSegment = subscription.Endpoint.Segments[^1];

            if (Guid.TryParseExact(idSegment, "N", out Guid employeeId))
            {
                _notified.Add(employeeId);
            }

            return Task.FromResult(PushDeliveryResult.Delivered);
        }
    }
}
