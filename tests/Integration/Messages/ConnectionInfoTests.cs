using InternalChat.Api.Hubs;
using InternalChat.Infrastructure.Persistence;
using InternalChat.IntegrationTests.Fixtures;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;

namespace InternalChat.IntegrationTests.Messages;

/// <summary>
/// 002 T042 — the hub tells each connection which transport it negotiated (FR-010, hub contract 1.1.0).
/// </summary>
/// <remarks>
/// Reported by the server from what it actually negotiated, not echoed from what the client asked
/// for — a proxy that strips <c>Upgrade</c> changes the former and not the latter, and the former is
/// what decides whether messages lag.
/// </remarks>
public sealed class ConnectionInfoTests : MessagingTestBase
{
    public ConnectionInfoTests(StackFixture stack)
        : base(stack)
    {
    }

    public sealed record ConnectionInfo(string Transport);

    [Theory]
    [InlineData(HttpTransportType.WebSockets, "webSockets")]
    [InlineData(HttpTransportType.LongPolling, "longPolling")]
    public async Task The_caller_is_told_its_negotiated_transport(HttpTransportType transport, string expected)
    {
        await using (ChatDbContext context = CreateDbContext())
        {
            await TestData.SeedEmployeeAsync(context, "an.nguyen");
        }

        string token = await Stack.IssueAccessTokenAsync("an.nguyen");
        await using HubConnection connection = HubClient.Create(Api, () => token, transport);

        TaskCompletionSource<ConnectionInfo> info = new(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<ConnectionInfo>(ChatHubEvents.ConnectionInfo, value => info.TrySetResult(value));

        await connection.StartAsync();

        ConnectionInfo received = await info.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(expected, received.Transport);
    }
}
