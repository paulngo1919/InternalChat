using System.Net.Http.Json;
using System.Text.Json;
using InternalChat.Api.Contracts;
using InternalChat.Application.Abstractions;
using InternalChat.Infrastructure.Persistence;
using InternalChat.Infrastructure.Persistence.Notifications;
using InternalChat.IntegrationTests.Fixtures;
using InternalChat.Worker.Consumers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace InternalChat.IntegrationTests.Notifications;

/// <summary>
/// T128 — a rejected push subscription is deleted rather than retried (data-model.md, FR-040).
/// </summary>
/// <remarks>
/// The push service itself is external and cannot run in Docker Compose, so <see cref="IPushSender"/>
/// is the one seam substituted here — see <c>MentionOnlyTests</c>'s remarks for why that is the
/// narrow, deliberate exception to this suite's real-dependencies rule rather than a habit.
/// </remarks>
public sealed class SubscriptionPruningTests : MessagingTestBase
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public SubscriptionPruningTests(StackFixture stack)
        : base(stack)
    {
    }

    [Fact]
    public async Task A_subscription_the_push_service_reports_gone_is_deleted()
    {
        (Guid senderId, Guid recipientId, Guid conversationId, MessageResponse message) =
            await SeedAndSendAsync("an.nguyen", "binh.tran");

        Guid subscriptionId = await RegisterSubscriptionAsync(recipientId);

        await RunFanoutAsync(
            conversationId, message, senderId, new ScriptedPushSender(PushDeliveryResult.SubscriptionExpired));

        await using ChatDbContext context = CreateDbContext();
        bool stillExists = await context.PushSubscriptions.AnyAsync(s => s.Id == subscriptionId);

        Assert.False(stillExists, "A subscription reported gone by the push service must be deleted, not kept for a retry.");
    }

    /// <summary>The other half: a transient failure must NOT prune a subscription that may recover.</summary>
    [Fact]
    public async Task A_subscription_that_fails_transiently_is_left_in_place()
    {
        (Guid senderId, Guid recipientId, Guid conversationId, MessageResponse message) =
            await SeedAndSendAsync("chi.le", "dung.pham");

        Guid subscriptionId = await RegisterSubscriptionAsync(recipientId);

        await RunFanoutAsync(
            conversationId, message, senderId, new ScriptedPushSender(PushDeliveryResult.TransientFailure));

        await using ChatDbContext context = CreateDbContext();
        bool stillExists = await context.PushSubscriptions.AnyAsync(s => s.Id == subscriptionId);

        Assert.True(stillExists, "A transient failure must not delete a subscription that may work on the next message.");
    }

    /// <summary>A successful delivery records when, so a future device list can show it.</summary>
    [Fact]
    public async Task A_successful_delivery_records_when_it_last_succeeded()
    {
        (Guid senderId, Guid recipientId, Guid conversationId, MessageResponse message) =
            await SeedAndSendAsync("giang.hoang", "hai.vo");

        Guid subscriptionId = await RegisterSubscriptionAsync(recipientId);

        await RunFanoutAsync(
            conversationId, message, senderId, new ScriptedPushSender(PushDeliveryResult.Delivered));

        await using ChatDbContext context = CreateDbContext();
        PushSubscriptionRecord subscription = await context.PushSubscriptions.SingleAsync(s => s.Id == subscriptionId);

        Assert.NotNull(subscription.LastSuccessAt);
    }

    private async Task<(Guid SenderId, Guid RecipientId, Guid ConversationId, MessageResponse Message)> SeedAndSendAsync(
        string sender, string recipient)
    {
        Guid senderId;
        Guid recipientId;

        await using (ChatDbContext context = CreateDbContext())
        {
            senderId = await TestData.SeedEmployeeAsync(context, sender);
            recipientId = await TestData.SeedEmployeeAsync(context, recipient);
        }

        Guid conversationId = await SeedDirectConversationAsync(senderId, recipientId);

        using HttpClient senderClient = await AuthenticatedClientAsync(sender);

        using HttpResponseMessage response = await SendAsync(
            senderClient, conversationId, ClientKey(Random.Shared.Next()), "a message to notify about");
        response.EnsureSuccessStatusCode();

        MessageResponse message = await ReadMessageAsync(response);

        return (senderId, recipientId, conversationId, message);
    }

    private async Task<Guid> RegisterSubscriptionAsync(Guid employeeId)
    {
        await using ChatDbContext context = CreateDbContext();

        Infrastructure.Persistence.Repositories.PushSubscriptionStore store = new(context, new FixedClock());

        await store.RegisterAsync(
            employeeId,
            new Uri($"https://push.example.invalid/{Guid.CreateVersion7():N}"),
            p256dh: "test-p256dh-key",
            auth: "test-auth-secret",
            userAgent: "integration-test",
            CancellationToken.None);

        await context.SaveChangesAsync();

        return await context.PushSubscriptions
            .Where(s => s.EmployeeId == employeeId)
            .Select(s => s.Id)
            .SingleAsync();
    }

    /// <summary>Runs the real consumer for one already-sent message, with a scripted push sender.</summary>
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

    /// <summary>Answers every delivery attempt with the same scripted outcome.</summary>
    private sealed class ScriptedPushSender : IPushSender
    {
        private readonly PushDeliveryResult _result;

        public ScriptedPushSender(PushDeliveryResult result) => _result = result;

        public Task<PushDeliveryResult> SendAsync(
            PushSubscriptionDescriptor subscription,
            PushPayload payload,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_result);
    }
}
