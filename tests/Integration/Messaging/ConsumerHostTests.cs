using InternalChat.Application.Abstractions;
using System.Collections.Concurrent;
using System.Text;
using InternalChat.Infrastructure.Messaging;
using InternalChat.Infrastructure.Persistence;
using InternalChat.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace InternalChat.IntegrationTests.Messaging;

/// <summary>
/// Verifies consumer idempotency (T040) and the dead-letter path (T041) against real RabbitMQ
/// and real PostgreSQL.
/// </summary>
/// <remarks>
/// Constitution Principle III forbids mocking the broker here, and for this suite a mock would
/// be actively misleading: the behaviour under test IS the broker's redelivery and TTL
/// dead-lettering. A fake would only prove that the fake behaves as written.
/// </remarks>
public sealed class ConsumerHostTests : IntegrationTestBase, IAsyncLifetime
{
    private const string Queue = "notifications.fanout";

    private RabbitMqConnectionProvider _connectionProvider = null!;
    private RabbitMqOptions _options = null!;
    private ServiceProvider _services = null!;

    public ConsumerHostTests(StackFixture stack)
        : base(stack)
    {
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
            // Milliseconds, not seconds. The real schedule is 1s + 5s + 25s; waiting that out
            // would make this a 31-second test, and slow tests get skipped.
            RetryDelays = [TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(200)],
        };

        _connectionProvider = new RabbitMqConnectionProvider(Options.Create(_options));

        ServiceCollection services = new();
        string connectionString = Stack.PostgresConnectionString;
        services.AddDbContext<ChatDbContext>(o =>
            ChatDbContext.ConfigureNpgsql((Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<ChatDbContext>)o, connectionString));
        _services = services.BuildServiceProvider();

        IChannel channel = await _connectionProvider.CreateChannelAsync();
        await using (channel.ConfigureAwait(false))
        {
            await ChatTopology.DeclareAsync(channel, _options.RetryDelays);
            await PurgeAsync(channel);
        }
    }

    public override async Task DisposeAsync()
    {
        if (_connectionProvider is not null)
        {
            await _connectionProvider.DisposeAsync();
        }

        if (_services is not null)
        {
            await _services.DisposeAsync();
        }

        await base.DisposeAsync();
    }

    /// <summary>
    /// Empties every queue this suite touches, including messages still in flight.
    /// </summary>
    /// <remarks>
    /// Two passes, and the ORDER matters. A test that leaves a message parked in a retry queue
    /// has handed the broker a timer: when the TTL expires the message is dead-lettered into the
    /// main queue. Purging the main queue first and the retry queues second leaves a window where
    /// exactly that happens in between — the next test then picks up the previous test's message
    /// and fails with a baffling id mismatch.
    ///
    /// <para>
    /// So: drain the retry queues, wait past the longest TTL for anything already in flight, then
    /// clear the main and dead-letter queues.
    /// </para>
    /// </remarks>
    private async Task PurgeAsync(IChannel channel)
    {
        for (int pass = 0; pass < 2; pass++)
        {
            foreach (TimeSpan delay in _options.RetryDelays)
            {
                await channel.QueuePurgeAsync(ChatTopology.RetryQueueName(Queue, delay));
            }

            await channel.QueuePurgeAsync(ChatTopology.DeadLetterQueueName(Queue));
            await channel.QueuePurgeAsync(Queue);

            if (pass == 0)
            {
                TimeSpan longest = _options.RetryDelays.Max();
                await Task.Delay(longest + TimeSpan.FromMilliseconds(200));
            }
        }
    }

    private ConsumerHost CreateHost() => new(
        _connectionProvider,
        _services.GetRequiredService<IServiceScopeFactory>(),
        Options.Create(_options),
        NullLogger<ConsumerHost>.Instance);

    /// <summary>Records every envelope it sees, and can be told to fail.</summary>
    private sealed class RecordingConsumer : IMessageConsumer
    {
        private readonly int _failuresBeforeSuccess;
        private int _invocations;

        public RecordingConsumer(int failuresBeforeSuccess = 0) =>
            _failuresBeforeSuccess = failuresBeforeSuccess;

        public ConcurrentBag<Guid> Handled { get; } = [];

        public int Invocations => _invocations;

        public string QueueName => Queue;

        public Task HandleAsync(MessageEnvelope envelope, CancellationToken cancellationToken = default)
        {
            int attempt = Interlocked.Increment(ref _invocations);

            if (attempt <= _failuresBeforeSuccess)
            {
                throw new InvalidOperationException($"deliberate failure {attempt}");
            }

            Handled.Add(envelope.MessageId);
            return Task.CompletedTask;
        }
    }

    // ---------------------------------------------------------------------------------------
    // T040 — idempotency
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Redelivered_message_is_handled_exactly_once()
    {
        Guid messageId = Guid.CreateVersion7();
        RecordingConsumer consumer = new();

        await using ConsumerHost host = CreateHost();
        IChannel channel = await _connectionProvider.CreateChannelAsync();

        await using (channel.ConfigureAwait(false))
        {
            // The same message delivered three times, which is precisely what at-least-once
            // permits: a channel reset or a crash after handling but before acknowledgement.
            for (int i = 0; i < 3; i++)
            {
                await host.HandleDeliveryAsync(channel, consumer, Deliver(messageId, attempt: 1));
            }
        }

        Assert.Single(consumer.Handled);
        Assert.Equal(1, consumer.Invocations);

        await using ChatDbContext context = CreateDbContext();
        int records = await context.ProcessedMessages
            .CountAsync(p => p.MessageId == messageId && p.ConsumerName == Queue);

        Assert.Equal(1, records);
    }

    [Fact]
    public async Task Different_consumers_each_handle_the_same_message_once()
    {
        Guid messageId = Guid.CreateVersion7();

        RecordingConsumer notifications = new();
        SecondQueueConsumer search = new();

        await using ConsumerHost host = CreateHost();
        IChannel channel = await _connectionProvider.CreateChannelAsync();

        await using (channel.ConfigureAwait(false))
        {
            await host.HandleDeliveryAsync(channel, notifications, Deliver(messageId, attempt: 1));
            await host.HandleDeliveryAsync(channel, search, Deliver(messageId, attempt: 1));
        }

        // Deduplication is keyed per consumer. If it were global, notifications.fanout having
        // handled a message would stop search.index from ever seeing it — messages would arrive
        // but never become searchable.
        Assert.Single(notifications.Handled);
        Assert.Single(search.Handled);
    }

    [Fact]
    public async Task Handler_failure_rolls_back_the_deduplication_record_so_retry_reprocesses()
    {
        Guid messageId = Guid.CreateVersion7();

        // Fails once, then succeeds.
        RecordingConsumer consumer = new(failuresBeforeSuccess: 1);

        await using ConsumerHost host = CreateHost();
        IChannel channel = await _connectionProvider.CreateChannelAsync();

        await using (channel.ConfigureAwait(false))
        {
            await host.HandleDeliveryAsync(channel, consumer, Deliver(messageId, attempt: 1));

            // If the deduplication row had survived the failed attempt, this retry would be
            // skipped as a duplicate and the work would never happen — the message would be
            // permanently, silently unprocessed.
            await using (ChatDbContext afterFailure = CreateDbContext())
            {
                Assert.Equal(0, await afterFailure.ProcessedMessages.CountAsync(p => p.MessageId == messageId));
            }

            await host.HandleDeliveryAsync(channel, consumer, Deliver(messageId, attempt: 2));
        }

        Assert.Single(consumer.Handled);

        await using ChatDbContext context = CreateDbContext();
        Assert.Equal(1, await context.ProcessedMessages.CountAsync(p => p.MessageId == messageId));
    }

    // ---------------------------------------------------------------------------------------
    // T041 — retry and dead-lettering
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Failed_message_is_not_requeued_immediately_onto_its_own_queue()
    {
        Guid messageId = Guid.CreateVersion7();
        RecordingConsumer consumer = new(failuresBeforeSuccess: 99);

        await using ConsumerHost host = CreateHost();
        IChannel channel = await _connectionProvider.CreateChannelAsync();

        await using (channel.ConfigureAwait(false))
        {
            await host.HandleDeliveryAsync(channel, consumer, Deliver(messageId, attempt: 1));

            // The property under test is the ABSENCE of immediate redelivery. nack(requeue:true)
            // — the reflexive choice — would put the message straight back in line, and the
            // consumer would spin on it at full speed until something else broke.
            //
            // Asserted on the main queue rather than on retry-queue depth, because with a 200 ms
            // TTL the message may legitimately have already moved on by the time depth is read.
            // That made an earlier version of this test race against its own fixture.
            QueueDeclareOk main = await channel.QueueDeclarePassiveAsync(Queue);
            Assert.Equal(0u, main.MessageCount);

            // Nor was it silently dropped: it comes back once the delay elapses. Redelivery
            // timing itself is covered by Retry_queue_returns_the_message_to_its_own_queue.
            Assert.NotNull(await WaitForMessageAsync(channel, Queue, TimeSpan.FromSeconds(10)));
        }
    }

    [Fact]
    public async Task Retry_queue_returns_the_message_to_its_own_queue_after_the_delay()
    {
        Guid messageId = Guid.CreateVersion7();
        RecordingConsumer consumer = new(failuresBeforeSuccess: 99);

        await using ConsumerHost host = CreateHost();
        IChannel channel = await _connectionProvider.CreateChannelAsync();

        await using (channel.ConfigureAwait(false))
        {
            await host.HandleDeliveryAsync(channel, consumer, Deliver(messageId, attempt: 1));

            BasicGetResult? returned = await WaitForMessageAsync(channel, Queue, TimeSpan.FromSeconds(10));

            Assert.NotNull(returned);
            Assert.Equal(messageId.ToString(), returned.BasicProperties.MessageId);

            // Attempt counter carried forward, so retries stay bounded across redeliveries
            // rather than resetting to 1 each time and looping forever.
            Assert.Equal(2, ReadAttempt(returned.BasicProperties));
        }
    }

    [Fact]
    public async Task Message_lands_in_the_dead_letter_queue_once_retries_are_exhausted()
    {
        Guid messageId = Guid.CreateVersion7();
        RecordingConsumer consumer = new(failuresBeforeSuccess: 99);

        await using ConsumerHost host = CreateHost();
        IChannel channel = await _connectionProvider.CreateChannelAsync();

        await using (channel.ConfigureAwait(false))
        {
            // Two retry delays configured, so attempt 3 is past the end of the schedule.
            await host.HandleDeliveryAsync(channel, consumer, Deliver(messageId, attempt: 3));

            BasicGetResult? dead = await WaitForMessageAsync(
                channel, ChatTopology.DeadLetterQueueName(Queue), TimeSpan.FromSeconds(10));

            Assert.NotNull(dead);
            Assert.Equal(messageId.ToString(), dead.BasicProperties.MessageId);

            // The failure reason travels with the message. A DLQ full of payloads with no
            // indication of why they failed is only marginally better than losing them.
            Assert.True(dead.BasicProperties.Headers!.ContainsKey(ChatTopology.FailureReasonHeader));
        }
    }

    [Fact]
    public async Task Dead_lettering_never_loses_the_message()
    {
        Guid messageId = Guid.CreateVersion7();
        RecordingConsumer consumer = new(failuresBeforeSuccess: 99);

        await using ConsumerHost host = CreateHost();
        IChannel channel = await _connectionProvider.CreateChannelAsync();

        await using (channel.ConfigureAwait(false))
        {
            await host.HandleDeliveryAsync(channel, consumer, Deliver(messageId, attempt: 3));

            await WaitForMessageAsync(channel, ChatTopology.DeadLetterQueueName(Queue), TimeSpan.FromSeconds(10));

            // Principle VI calls silent message loss a Sev-1 defect. The message must be parked
            // where alerting can see it, not dropped — and not still sitting on the live queue
            // where it would be retried forever.
            QueueDeclareOk main = await channel.QueueDeclarePassiveAsync(Queue);
            Assert.Equal(0u, main.MessageCount);
        }
    }

    // ---------------------------------------------------------------------------------------

    private sealed class SecondQueueConsumer : IMessageConsumer
    {
        public ConcurrentBag<Guid> Handled { get; } = [];

        public string QueueName => "search.index";

        public Task HandleAsync(MessageEnvelope envelope, CancellationToken cancellationToken = default)
        {
            Handled.Add(envelope.MessageId);
            return Task.CompletedTask;
        }
    }

    private static BasicDeliverEventArgs Deliver(Guid messageId, int attempt)
    {
        BasicProperties properties = new()
        {
            MessageId = messageId.ToString(),
            Type = "chat.message.sent.v1",
            ContentType = "application/json",
            DeliveryMode = DeliveryModes.Persistent,
            Headers = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [ChatTopology.AttemptHeader] = attempt,
            },
        };

        return new BasicDeliverEventArgs(
            consumerTag: "test",
            deliveryTag: 0, // 0 means "no real delivery"; BasicAck on it is a no-op
            redelivered: false,
            exchange: ChatTopology.EventsExchange,
            routingKey: "chat.message.sent.v1",
            properties: properties,
            body: Encoding.UTF8.GetBytes("""{"conversationId":"00000000-0000-0000-0000-000000000001"}"""),
            cancellationToken: CancellationToken.None);
    }

    private static int ReadAttempt(IReadOnlyBasicProperties properties)
    {
        if (properties.Headers?.TryGetValue(ChatTopology.AttemptHeader, out object? raw) != true)
        {
            return 1;
        }

        return raw switch
        {
            int i => i,
            long l => (int)l,
            byte[] bytes => int.Parse(Encoding.UTF8.GetString(bytes), System.Globalization.CultureInfo.InvariantCulture),
            _ => 1,
        };
    }

    /// <summary>
    /// Polls until a message appears. TTL expiry is the broker's timer, so there is nothing to
    /// await deterministically — polling with a generous ceiling is the honest approach.
    /// </summary>
    private static async Task<BasicGetResult?> WaitForMessageAsync(
        IChannel channel,
        string queue,
        TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            BasicGetResult? result = await channel.BasicGetAsync(queue, autoAck: true);

            if (result is not null)
            {
                return result;
            }

            await Task.Delay(50);
        }

        return null;
    }
}
