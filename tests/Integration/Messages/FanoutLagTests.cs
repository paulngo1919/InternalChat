using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using InternalChat.Api.Contracts;
using InternalChat.Api.Hubs;
using InternalChat.Application.Telemetry;
using InternalChat.Infrastructure.Messaging;
using InternalChat.Infrastructure.Persistence;
using InternalChat.IntegrationTests.Fixtures;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace InternalChat.IntegrationTests.Messages;

/// <summary>
/// 002 T022 — the fan-out stage of FR-011: commit to hub send, recorded per delivered event.
/// </summary>
/// <remarks>
/// <para>
/// <c>chat.delivery.fanout_lag</c> is the series the latency alert (FR-012) is written against, so
/// the assertion that matters is that a real message, sent over HTTP and delivered over SignalR,
/// produces exactly the observation the alert reads: the right name, the event type tag, and
/// nothing identifying.
/// </para>
/// <para>
/// Observed through a <see cref="MeterListener"/> on the platform meter. The API host runs in this
/// process, so its measurements are visible here without replacing any registration.
/// </para>
/// </remarks>
public sealed class FanoutLagTests : MessagingTestBase
{
    public FanoutLagTests(StackFixture stack)
        : base(stack)
    {
    }

    [Fact]
    public async Task A_delivered_message_records_its_fanout_lag_tagged_only_by_event_type()
    {
        ConcurrentQueue<(double Value, KeyValuePair<string, object?>[] Tags)> recorded = new();

        using MeterListener listener = new();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == ChatTelemetry.MeterName && instrument.Name == ChatTelemetry.Metrics.FanoutLag)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((_, value, tags, _) => recorded.Enqueue((value, tags.ToArray())));
        listener.Start();

        Guid conversationId = await SeedPairAsync("an.nguyen", "binh.tran");

        await using HubConnection recipient = HubClient.Create(Api, await Stack.IssueAccessTokenAsync("binh.tran"));
        TaskCompletionSource<MessageResponse> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        recipient.On<MessageResponse>(ChatHubEvents.MessageReceived, message => received.TrySetResult(message));
        await recipient.StartAsync();

        // A barrier, as the real client's first call is: StartAsync can return before the server's
        // OnConnectedAsync has joined this connection to its conversation groups, and SignalR does not
        // dispatch an invocation until OnConnectedAsync has finished. Without this the message can
        // fan out to a group the connection has not joined yet.
        await recipient.InvokeAsync<IReadOnlyDictionary<Guid, List<MessageResponse>>>(
            "Resync", new Dictionary<Guid, long>());

        using HttpClient sender = await AuthenticatedClientAsync("an.nguyen");
        using (HttpResponseMessage response = await SendAsync(sender, conversationId, ClientKey(9700), "fan-out lag probe"))
        {
            response.EnsureSuccessStatusCode();
        }

        await DispatchOutboxAsync();
        await received.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // The consumer records after SendAsync returns, which can be a moment after the client
        // has the frame.
        for (int i = 0; i < 50 && recorded.IsEmpty; i++)
        {
            await Task.Delay(20);
        }

        var (value, tags) = Assert.Single(recorded);
        Assert.True(value >= 0 && value < 10_000, $"Implausible fan-out lag {value} ms.");

        var tag = Assert.Single(tags);
        Assert.Equal(ChatTelemetry.Metrics.EventType, tag.Key);
        Assert.Equal("chat.message.sent.v1", tag.Value);
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
