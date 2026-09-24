using System.Net;
using System.Net.Http.Json;
using InternalChat.Api.Contracts;
using InternalChat.Api.Hubs;
using InternalChat.Infrastructure.Messaging;
using InternalChat.Infrastructure.Persistence;
using InternalChat.IntegrationTests.Fixtures;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace InternalChat.IntegrationTests.Notifications;

/// <summary>
/// T126 — reading on one device clears unread state on another (FR-036).
/// </summary>
/// <remarks>
/// Two things are asserted, because either one alone is not FR-036: the read endpoint's own
/// response reports the merged position, and a second, already-open connection for the <em>same</em>
/// employee hears about it over SignalR without polling — the real-time half
/// <see cref="Api.Consumers.ReadStateConsumer"/> exists for.
/// </remarks>
public sealed class CrossDeviceReadTests : MessagingTestBase
{
    public CrossDeviceReadTests(StackFixture stack)
        : base(stack)
    {
    }

    [Fact]
    public async Task Marking_read_on_one_device_broadcasts_to_the_employees_other_open_connection()
    {
        const string Sender = "an.nguyen";
        const string Reader = "binh.tran";

        Guid conversationId = await SeedPairAsync(Sender, Reader);

        using HttpClient senderClient = await AuthenticatedClientAsync(Sender);
        using HttpClient readerDeviceOne = await AuthenticatedClientAsync(Reader);

        for (int i = 0; i < 3; i++)
        {
            using HttpResponseMessage response = await SendAsync(
                senderClient, conversationId, ClientKey(9500 + i), $"cross device message {i}");
            response.EnsureSuccessStatusCode();
        }

        // The reader's "other device" — a second, already-open SignalR connection for the same
        // employee, watching before the read happens rather than reconnecting afterwards.
        string readerToken = await Stack.IssueAccessTokenAsync(Reader);
        await using HubConnection deviceTwo = HubClient.Create(Api, readerToken);

        TaskCompletionSource<ReadStateResponse> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        deviceTwo.On<ReadStateResponse>(ChatHubEvents.ReadStateUpdated, state => received.TrySetResult(state));

        await deviceTwo.StartAsync();

        using HttpResponseMessage markRead = await readerDeviceOne.PutAsJsonAsync(
            new Uri($"/api/v1/conversations/{conversationId}/read-state", UriKind.Relative),
            new MarkReadRequest(3));

        Assert.Equal(HttpStatusCode.OK, markRead.StatusCode);

        ReadStateResponse fromHttp = await markRead.Content.ReadFromJsonAsync<ReadStateResponse>()
            ?? throw new InvalidOperationException("The read-state endpoint returned an empty body.");

        Assert.Equal(3, fromHttp.LastReadSeq);
        Assert.Equal(0, fromHttp.UnreadCount);

        // OutboxDispatcherService — the loop that actually drains outbox_message to RabbitMQ —
        // is hosted only by the Worker (Program.cs), which this test never starts; ApiFactory
        // hosts only the API. The row MarkReadHandler wrote is real and already committed, so
        // dispatching it by hand here stands in for that loop's next tick, exactly as
        // OutboxTests does for the dispatcher itself.
        await DispatchOutboxAsync();

        ReadStateResponse fromHub = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(conversationId, fromHub.ConversationId);
        Assert.Equal(3, fromHub.LastReadSeq);
        Assert.Equal(0, fromHub.UnreadCount);
    }

    /// <summary>A device reporting a stale, lower position must not move it backwards (FR-036).</summary>
    [Fact]
    public async Task An_out_of_order_lower_report_does_not_move_the_position_backwards()
    {
        const string Sender = "chi.le";
        const string Reader = "dung.pham";

        Guid conversationId = await SeedPairAsync(Sender, Reader);

        using HttpClient senderClient = await AuthenticatedClientAsync(Sender);
        using HttpClient readerClient = await AuthenticatedClientAsync(Reader);

        for (int i = 0; i < 5; i++)
        {
            using HttpResponseMessage response = await SendAsync(
                senderClient, conversationId, ClientKey(9600 + i), $"ordering message {i}");
            response.EnsureSuccessStatusCode();
        }

        using HttpResponseMessage first = await readerClient.PutAsJsonAsync(
            new Uri($"/api/v1/conversations/{conversationId}/read-state", UriKind.Relative),
            new MarkReadRequest(5));
        first.EnsureSuccessStatusCode();

        // A stale device report — generated before the one above, delivered after.
        using HttpResponseMessage stale = await readerClient.PutAsJsonAsync(
            new Uri($"/api/v1/conversations/{conversationId}/read-state", UriKind.Relative),
            new MarkReadRequest(2));
        stale.EnsureSuccessStatusCode();

        ReadStateResponse afterStale = await stale.Content.ReadFromJsonAsync<ReadStateResponse>()
            ?? throw new InvalidOperationException("The read-state endpoint returned an empty body.");

        Assert.Equal(5, afterStale.LastReadSeq);

        await using ChatDbContext context = CreateDbContext();

        Guid readerId = await context.Employees
            .Where(e => e.ExternalSubject == TestData.SubjectFor(Reader))
            .Select(e => e.Id)
            .SingleAsync();

        long stored = await context.ReadStates
            .Where(r => r.EmployeeId == readerId && r.ConversationId == conversationId)
            .Select(r => r.LastReadSeq)
            .SingleAsync();

        Assert.Equal(5, stored);
    }

    /// <summary>Drains pending outbox rows once, standing in for <c>OutboxDispatcherService</c>.</summary>
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
