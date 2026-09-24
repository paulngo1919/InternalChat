using System.Diagnostics;
using System.Net;
using InternalChat.Domain.Common;
using InternalChat.Domain.Conversations;
using InternalChat.Infrastructure.Persistence;
using InternalChat.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;

namespace InternalChat.IntegrationTests.Conversations;

/// <summary>
/// T113 — sending into a 500-member group stays inside the accept budget (US3 scenario 6, SC-013).
/// </summary>
/// <remarks>
/// <para>
/// <b>What this actually verifies.</b> <c>SendMessageHandler</c> never reads the membership list —
/// it allocates a sequence, writes the message, and writes one outbox row, all independent of how
/// many members the conversation has. Delivery to those members is a separate, asynchronous fan-out
/// (<c>RealtimeFanoutConsumer</c>) that publishes once to a SignalR group rather than once per
/// member. A regression that introduced a per-member loop on the send path would show up here as
/// send latency scaling with member count; it would not show up in any test that only checks
/// correctness.
/// </para>
/// <para>
/// Asserted as a ratio against a same-shape 3-member send rather than an absolute millisecond
/// ceiling, because an absolute number is a load-test assertion (T107, T218) and flakes on whatever
/// hardware happens to run CI that day. A ratio isolates the property this test exists to check.
/// </para>
/// </remarks>
public sealed class LargeGroupDeliveryTests : MessagingTestBase
{
    private const int LargeGroupMemberCount = 500;

    public LargeGroupDeliveryTests(StackFixture stack)
        : base(stack)
    {
    }

    [Fact]
    public async Task Send_accept_latency_for_a_500_member_group_does_not_scale_with_member_count()
    {
        const string Sender = "an.nguyen";

        Guid senderId;
        Guid smallGroupId;
        Guid largeGroupId;

        await using (ChatDbContext context = CreateDbContext())
        {
            senderId = await TestData.SeedEmployeeAsync(context, Sender);
        }

        smallGroupId = await SeedGroupAsync(senderId, memberCount: 3);
        largeGroupId = await SeedGroupAsync(senderId, memberCount: LargeGroupMemberCount);

        using HttpClient client = await AuthenticatedClientAsync(Sender);

        TimeSpan smallGroupLatency = await MeasureSendAsync(client, smallGroupId, ClientKey(9400));
        TimeSpan largeGroupLatency = await MeasureSendAsync(client, largeGroupId, ClientKey(9401));

        // Generous multiple, not a tight bound: this is a structural check (no O(member count) work
        // on the accept path), not a load test. T107's k6 script and T218 measure the actual budget.
        Assert.True(
            largeGroupLatency <= smallGroupLatency * 5 + TimeSpan.FromSeconds(1),
            $"Sending into a {LargeGroupMemberCount}-member group took {largeGroupLatency.TotalMilliseconds} ms "
            + $"against {smallGroupLatency.TotalMilliseconds} ms for a 3-member group — accept latency should "
            + "not scale with member count (SC-013).");
    }

    private static async Task<TimeSpan> MeasureSendAsync(HttpClient client, Guid conversationId, string key)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();

        using HttpResponseMessage response = await SendAsync(client, conversationId, key, "measuring accept latency");

        stopwatch.Stop();

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return stopwatch.Elapsed;
    }

    /// <summary>
    /// Seeds a group with the sender as admin plus <paramref name="memberCount"/> other members, in
    /// one bulk insert rather than one HTTP call per member.
    /// </summary>
    private async Task<Guid> SeedGroupAsync(Guid senderId, int memberCount)
    {
        await using ChatDbContext context = CreateDbContext();

        FixedClock clock = new();

        Conversation conversation = Conversation.CreateGroup(
            Guid.CreateVersion7(), $"large-group-{memberCount}", senderId, HistoryVisibility.FromJoin, clock);
        conversation.ClearDomainEvents();

        context.Conversations.Add(conversation);
        context.Memberships.Add(Membership.Join(conversation.Id, senderId, MembershipRole.Admin, 0, clock));

        for (int i = 0; i < memberCount - 1; i++)
        {
            Guid employeeId = Guid.CreateVersion7();

            InternalChat.Domain.Employees.Employee employee = InternalChat.Domain.Employees.Employee.Project(
                employeeId,
                $"synthetic-{conversation.Id:N}-{i}",
                $"Synthetic Member {i}",
                $"synthetic-{conversation.Id:N}-{i}@internalchat.local",
                clock);
            employee.ClearDomainEvents();

            context.Employees.Add(employee);
            context.Memberships.Add(Membership.Join(conversation.Id, employeeId, MembershipRole.Member, 0, clock));
        }

        await context.SaveChangesAsync();

        return conversation.Id;
    }
}
