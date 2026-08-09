using System.Text;
using System.Text.Json;
using InternalChat.Domain.Common;
using InternalChat.Infrastructure.Messaging;
using InternalChat.Infrastructure.Persistence;
using InternalChat.Infrastructure.Persistence.Outbox;
using InternalChat.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace InternalChat.IntegrationTests.Messaging;

/// <summary>
/// Verifies the transactional outbox against real PostgreSQL and real RabbitMQ.
/// </summary>
/// <remarks>
/// T039. Constitution Principle VI forbids dual-writing a database change and a publish. These
/// tests assert the property that matters: an event is never published for a transaction that
/// rolled back, and never lost for one that committed.
/// </remarks>
public sealed class OutboxTests : IntegrationTestBase, IAsyncLifetime
{
    private RabbitMqConnectionProvider _connectionProvider = null!;
    private RabbitMqOptions _options = null!;

    public OutboxTests(StackFixture stack)
        : base(stack)
    {
    }

    private sealed record ProbeEvent(Guid EventId, DateTimeOffset OccurredAt, Guid ConversationId)
        : DomainEvent(EventId, OccurredAt)
    {
        public override string EventType => "chat.message.sent.direct";
    }

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();

        Uri uri = new(Stack.RabbitMqConnectionString);
        _options = new RabbitMqOptions
        {
            Host = uri.Host,
            Port = uri.Port,
            User = "internalchat",
            Password = "internalchat",
            VHost = "/",
        };

        _connectionProvider = new RabbitMqConnectionProvider(Options.Create(_options));

        IChannel channel = await _connectionProvider.CreateChannelAsync();
        await using (channel.ConfigureAwait(false))
        {
            await ChatTopology.DeclareAsync(channel);
            await channel.QueuePurgeAsync("realtime.fanout");
            await channel.QueuePurgeAsync("notifications.fanout");
        }
    }

    public override async Task DisposeAsync()
    {
        if (_connectionProvider is not null)
        {
            await _connectionProvider.DisposeAsync();
        }

        await base.DisposeAsync();
    }

    private OutboxDispatcher CreateDispatcher(ChatDbContext context) =>
        new(context, _connectionProvider, Options.Create(_options), NullLogger<OutboxDispatcher>.Instance);

    [Fact]
    public async Task Event_written_in_a_rolled_back_transaction_is_never_published()
    {
        await using ChatDbContext context = CreateDbContext();

        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            OutboxEventPublisher publisher = new(context);
            await publisher.PublishAsync(
                new ProbeEvent(Guid.CreateVersion7(), DateTimeOffset.UtcNow, Guid.CreateVersion7()));
            await context.SaveChangesAsync();

            // The state change failed after the event was staged. Because both live in one
            // transaction, the event must vanish with it. A direct publish could not do this —
            // the message would already be on the wire.
            await transaction.RollbackAsync();
        }

        await using ChatDbContext verify = CreateDbContext();
        Assert.Empty(await verify.OutboxMessages.ToListAsync());
    }

    [Fact]
    public async Task Committed_event_is_published_and_marked_dispatched()
    {
        Guid eventId = Guid.CreateVersion7();

        await using (ChatDbContext write = CreateDbContext())
        {
            OutboxEventPublisher publisher = new(write);
            await publisher.PublishAsync(new ProbeEvent(eventId, DateTimeOffset.UtcNow, Guid.CreateVersion7()));
            await write.SaveChangesAsync();
        }

        await using ChatDbContext dispatchContext = CreateDbContext();
        int confirmed = await CreateDispatcher(dispatchContext).DispatchBatchAsync();

        Assert.Equal(1, confirmed);

        OutboxMessage row = await dispatchContext.OutboxMessages.SingleAsync(m => m.Id == eventId);
        Assert.NotNull(row.DispatchedAt);
        Assert.Null(row.LastError);

        // Present in the queue, carrying the outbox row id as the message id — which is what
        // consumers deduplicate on.
        BasicGetResult? delivered = await GetAsync("realtime.fanout");
        Assert.NotNull(delivered);
        Assert.Equal(eventId.ToString(), delivered.BasicProperties.MessageId);
    }

    [Fact]
    public async Task Dispatched_rows_are_not_published_a_second_time()
    {
        await using (ChatDbContext write = CreateDbContext())
        {
            OutboxEventPublisher publisher = new(write);
            await publisher.PublishAsync(
                new ProbeEvent(Guid.CreateVersion7(), DateTimeOffset.UtcNow, Guid.CreateVersion7()));
            await write.SaveChangesAsync();
        }

        await using ChatDbContext context = CreateDbContext();
        OutboxDispatcher dispatcher = CreateDispatcher(context);

        Assert.Equal(1, await dispatcher.DispatchBatchAsync());

        // The dispatcher runs continuously; without the dispatched_at filter every pass would
        // republish the entire history of the outbox.
        Assert.Equal(0, await dispatcher.DispatchBatchAsync());
    }

    [Fact]
    public async Task Publish_failure_leaves_the_row_undispatched_for_the_next_pass()
    {
        Guid eventId = Guid.CreateVersion7();

        await using (ChatDbContext write = CreateDbContext())
        {
            OutboxEventPublisher publisher = new(write);
            await publisher.PublishAsync(new ProbeEvent(eventId, DateTimeOffset.UtcNow, Guid.CreateVersion7()));
            await write.SaveChangesAsync();
        }

        // Point the dispatcher at a broker that is not there. The publish fails; the row must
        // survive undispatched rather than being lost or marked done.
        RabbitMqOptions unreachable = new()
        {
            Host = _options.Host,
            Port = 1, // nothing listens here
            User = _options.User,
            Password = _options.Password,
            VHost = _options.VHost,
        };

        await using RabbitMqConnectionProvider broken = new(Options.Create(unreachable));
        await using ChatDbContext failContext = CreateDbContext();

        OutboxDispatcher failing = new(
            failContext, broken, Options.Create(unreachable), NullLogger<OutboxDispatcher>.Instance);

        await Assert.ThrowsAnyAsync<Exception>(() => failing.DispatchBatchAsync());

        await using ChatDbContext verify = CreateDbContext();
        OutboxMessage row = await verify.OutboxMessages.SingleAsync(m => m.Id == eventId);
        Assert.Null(row.DispatchedAt);

        // Now it succeeds — exactly once, on the retry.
        await using ChatDbContext retryContext = CreateDbContext();
        Assert.Equal(1, await CreateDispatcher(retryContext).DispatchBatchAsync());

        Assert.NotNull((await retryContext.OutboxMessages.SingleAsync(m => m.Id == eventId)).DispatchedAt);
    }

    [Fact]
    public async Task Payload_carries_identifiers_and_never_message_content()
    {
        Guid conversationId = Guid.CreateVersion7();

        await using (ChatDbContext write = CreateDbContext())
        {
            OutboxEventPublisher publisher = new(write);
            await publisher.PublishAsync(
                new ProbeEvent(Guid.CreateVersion7(), DateTimeOffset.UtcNow, conversationId));
            await write.SaveChangesAsync();
        }

        await using ChatDbContext read = CreateDbContext();
        OutboxMessage row = await read.OutboxMessages.SingleAsync();

        using JsonDocument payload = JsonDocument.Parse(row.Payload);

        // FR-056: message bodies must never reach a queue payload — they would surface in broker
        // logs, the management UI, and DLQ dumps. Consumers read content from PostgreSQL.
        Assert.True(payload.RootElement.TryGetProperty("conversationId", out _));
        Assert.False(payload.RootElement.TryGetProperty("body", out _));
    }

    [Fact]
    public async Task Batch_is_bounded_so_one_pass_cannot_run_unboundedly_long()
    {
        await using (ChatDbContext write = CreateDbContext())
        {
            OutboxEventPublisher publisher = new(write);

            for (int i = 0; i < 5; i++)
            {
                await publisher.PublishAsync(
                    new ProbeEvent(Guid.CreateVersion7(), DateTimeOffset.UtcNow, Guid.CreateVersion7()));
            }

            await write.SaveChangesAsync();
        }

        RabbitMqOptions smallBatch = new()
        {
            Host = _options.Host,
            Port = _options.Port,
            User = _options.User,
            Password = _options.Password,
            VHost = _options.VHost,
            DispatchBatchSize = 2,
        };

        await using ChatDbContext context = CreateDbContext();
        OutboxDispatcher dispatcher = new(
            context, _connectionProvider, Options.Create(smallBatch), NullLogger<OutboxDispatcher>.Instance);

        Assert.Equal(2, await dispatcher.DispatchBatchAsync());
        Assert.Equal(2, await dispatcher.DispatchBatchAsync());
        Assert.Equal(1, await dispatcher.DispatchBatchAsync());
        Assert.Equal(0, await dispatcher.DispatchBatchAsync());
    }

    private async Task<BasicGetResult?> GetAsync(string queue)
    {
        IChannel channel = await _connectionProvider.CreateChannelAsync();
        await using (channel.ConfigureAwait(false))
        {
            return await channel.BasicGetAsync(queue, autoAck: true);
        }
    }
}
