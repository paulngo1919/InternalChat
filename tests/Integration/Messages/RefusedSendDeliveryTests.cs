using System.Net;
using InternalChat.Api.Contracts;
using InternalChat.Api.Hubs;
using InternalChat.Infrastructure.Messaging;
using InternalChat.Infrastructure.Persistence;
using InternalChat.IntegrationTests.Fixtures;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace InternalChat.IntegrationTests.Messages;

/// <summary>
/// 002 T028 — a refused message never reaches anyone, even briefly (FR-009, SC-008).
/// </summary>
/// <remarks>
/// <para>
/// Delivery now starts the moment an outbox row commits, so the question is sharper than it was:
/// could a send that is going to be refused ring the doorbell on its way out? It cannot, because a
/// refused send commits nothing — no message row, no outbox row, so no <c>outbox_ready</c> — and
/// this asserts it end to end rather than trusting that reasoning.
/// </para>
/// <para>
/// Written here rather than in the browser: once removed, an employee's UI closes the conversation,
/// so the refused send cannot be produced through the composer. The API is where the refusal
/// happens, and a member's live hub connection is where a leak would show.
/// </para>
/// </remarks>
public sealed class RefusedSendDeliveryTests : MessagingTestBase
{
    public RefusedSendDeliveryTests(StackFixture stack)
        : base(stack)
    {
    }

    [Fact]
    public async Task A_send_from_a_non_member_is_refused_and_no_member_ever_receives_it()
    {
        Guid memberA;
        Guid memberB;

        await using (ChatDbContext context = CreateDbContext())
        {
            memberA = await TestData.SeedEmployeeAsync(context, "an.nguyen");
            memberB = await TestData.SeedEmployeeAsync(context, "binh.tran");
            await TestData.SeedEmployeeAsync(context, "chi.le");
        }

        Guid conversationId = await SeedDirectConversationAsync(memberA, memberB);

        await using HubConnection member = HubClient.Create(Api, await Stack.IssueAccessTokenAsync("binh.tran"));
        TaskCompletionSource<MessageResponse> leaked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        member.On<MessageResponse>(ChatHubEvents.MessageReceived, message => leaked.TrySetResult(message));
        await member.StartAsync();

        // A barrier, as the real client's first call is: StartAsync can return before the server's
        // OnConnectedAsync has joined this connection to its conversation groups, and SignalR does not
        // dispatch an invocation until OnConnectedAsync has finished. Without this the message can
        // fan out to a group the connection has not joined yet.
        await member.InvokeAsync<IReadOnlyDictionary<Guid, List<MessageResponse>>>(
            "Resync", new Dictionary<Guid, long>());

        using HttpClient outsider = await AuthenticatedClientAsync("chi.le");
        using HttpResponseMessage response = await SendAsync(outsider, conversationId, ClientKey(9800), "should never arrive");

        Assert.Contains(response.StatusCode, new[] { HttpStatusCode.Forbidden, HttpStatusCode.NotFound });

        // Nothing was committed for the dispatcher to find — not a message, not an outbox row.
        await using (ChatDbContext verify = CreateDbContext())
        {
            Assert.Equal(0, await verify.OutboxMessages.CountAsync());
        }

        Assert.Equal(0, await CountMessagesAsync(conversationId));

        // Drain anyway, as the Worker would, and give fan-out a generous window to leak.
        await DispatchOutboxAsync();
        Task winner = await Task.WhenAny(leaked.Task, Task.Delay(TimeSpan.FromSeconds(1)));

        Assert.False(ReferenceEquals(winner, leaked.Task), "A refused message was delivered to a member (002 FR-009).");
    }

    private async Task DispatchOutboxAsync()
    {
        await using ChatDbContext context = CreateDbContext();

        OutboxDispatcher dispatcher = new(
            context,
            Api.Services.GetRequiredService<IRabbitMqConnectionProvider>(),
            Api.Services.GetRequiredService<IOptions<RabbitMqOptions>>(),
            Api.Services.GetRequiredService<Microsoft.Extensions.Logging.ILogger<OutboxDispatcher>>());

        await dispatcher.DispatchBatchAsync();
    }
}
