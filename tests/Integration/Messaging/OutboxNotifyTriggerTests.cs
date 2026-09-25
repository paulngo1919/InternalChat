using InternalChat.Domain.Common;
using InternalChat.Infrastructure.Messaging;
using InternalChat.Infrastructure.Persistence;
using InternalChat.IntegrationTests.Fixtures;
using Npgsql;

namespace InternalChat.IntegrationTests.Messaging;

/// <summary>
/// 002 T012 — the <c>outbox_ready</c> doorbell (data-model §1, contracts/messaging-delta.md).
/// </summary>
/// <remarks>
/// <para>
/// The whole speed-up rests on one property of PostgreSQL <c>NOTIFY</c>: it is delivered when the
/// inserting transaction commits and not before, and never for one that rolls back. If that were
/// not true, the dispatcher could wake, find nothing, and go back to sleep while the row was still
/// uncommitted — or, worse, a rejected send would ring the bell (002 FR-009). These tests pin that
/// property against a real server rather than trusting the documentation.
/// </para>
/// <para>
/// Listened for on a raw connection rather than through the listener service, so a defect in the
/// trigger cannot be masked by, or blamed on, the service.
/// </para>
/// </remarks>
public sealed class OutboxNotifyTriggerTests : IntegrationTestBase
{
    private static readonly TimeSpan NoNotificationWindow = TimeSpan.FromMilliseconds(500);

    public OutboxNotifyTriggerTests(StackFixture stack)
        : base(stack)
    {
    }

    private sealed record ProbeEvent(Guid EventId, DateTimeOffset OccurredAt)
        : DomainEvent(EventId, OccurredAt)
    {
        public override string EventType => "chat.message.sent.v1";
    }

    [Fact]
    public async Task A_committed_outbox_insert_rings_the_doorbell_once_with_an_empty_payload()
    {
        await using NpgsqlConnection listener = await ListenAsync();
        List<NpgsqlNotificationEventArgs> received = Collect(listener);

        await InsertAsync(count: 1, commit: true);

        Assert.True(await listener.WaitAsync(TimeSpan.FromSeconds(5)), "No outbox_ready notification arrived.");
        await DrainAsync(listener);

        NpgsqlNotificationEventArgs notification = Assert.Single(received);
        Assert.Equal(OutboxNotificationListener.Channel, notification.Channel);

        // A doorbell, not a data path: nothing about the row travels on the channel (FR-056).
        Assert.Equal(string.Empty, notification.Payload);
    }

    [Fact]
    public async Task Several_inserts_in_one_transaction_ring_once()
    {
        await using NpgsqlConnection listener = await ListenAsync();
        List<NpgsqlNotificationEventArgs> received = Collect(listener);

        // A send with attachments writes several outbox rows in one transaction. One wake-up is
        // enough: the dispatcher reads the table, not the notification.
        await InsertAsync(count: 3, commit: true);

        Assert.True(await listener.WaitAsync(TimeSpan.FromSeconds(5)), "No outbox_ready notification arrived.");
        await DrainAsync(listener);

        Assert.Single(received);
    }

    [Fact]
    public async Task A_rolled_back_insert_rings_nothing()
    {
        await using NpgsqlConnection listener = await ListenAsync();
        List<NpgsqlNotificationEventArgs> received = Collect(listener);

        await InsertAsync(count: 1, commit: false);

        Assert.False(
            await listener.WaitAsync(NoNotificationWindow),
            "A rolled-back outbox insert produced a notification. A rejected send must not wake anything (002 FR-009).");
        Assert.Empty(received);
    }

    private async Task<NpgsqlConnection> ListenAsync()
    {
        NpgsqlConnection connection = new(Stack.PostgresConnectionString);
        await connection.OpenAsync();

        await using NpgsqlCommand listen = new($"LISTEN {OutboxNotificationListener.Channel}", connection);
        await listen.ExecuteNonQueryAsync();

        return connection;
    }

    private static List<NpgsqlNotificationEventArgs> Collect(NpgsqlConnection connection)
    {
        List<NpgsqlNotificationEventArgs> received = [];
        connection.Notification += (_, args) => received.Add(args);
        return received;
    }

    /// <summary>Picks up any further notification already in flight, so a count is final.</summary>
    private static async Task DrainAsync(NpgsqlConnection connection)
    {
        while (await connection.WaitAsync(TimeSpan.FromMilliseconds(200)))
        {
        }
    }

    private async Task InsertAsync(int count, bool commit)
    {
        await using ChatDbContext context = CreateDbContext();
        await using var transaction = await context.Database.BeginTransactionAsync();

        OutboxEventPublisher publisher = new(context);

        for (int i = 0; i < count; i++)
        {
            await publisher.PublishAsync(new ProbeEvent(Guid.CreateVersion7(), DateTimeOffset.UtcNow));
        }

        // One SaveChanges, so EF batches the rows into a single statement where it can — the
        // statement-level trigger then fires once per statement EF actually sends.
        await context.SaveChangesAsync();

        if (commit)
        {
            await transaction.CommitAsync();
        }
        else
        {
            await transaction.RollbackAsync();
        }
    }
}
