using System.Text;
using InternalChat.Infrastructure.Messaging;
using InternalChat.IntegrationTests.Fixtures;
using RabbitMQ.Client;

namespace InternalChat.IntegrationTests.Messaging;

/// <summary>
/// Asserts that every declared queue actually receives the routing keys the platform publishes.
/// </summary>
/// <remarks>
/// <para>
/// <b>This suite exists because nothing else checks the bindings.</b> Every other messaging test
/// either publishes straight to a queue or calls a consumer's <c>HandleAsync</c> directly, so the
/// topic patterns in <see cref="ChatTopology.Queues"/> were never exercised against a real broker.
/// A binding that matches nothing is silent: the publisher confirms, the outbox row is marked
/// dispatched, and the message is discarded by the exchange because no queue wanted it.
/// </para>
/// <para>
/// The trap is AMQP's two wildcards. <c>*</c> matches <b>exactly one</b> word and <c>#</c> matches
/// zero or more, so <c>chat.directory.*</c> does not match
/// <c>chat.directory.employee.changed.v1</c> — the intent was "every directory event" and the
/// pattern says "directory events with exactly one more word". Routing keys here are event types
/// (<c>OutboxEventPublisher</c> sets <c>RoutingKey = domainEvent.EventType</c>), and those carry a
/// version suffix, so the word count is never what a first reading suggests.
/// </para>
/// </remarks>
[Collection(StackCollectionDefinition.Name)]
public sealed class TopologyRoutingTests : IAsyncLifetime
{
    private readonly StackFixture _stack;
    private IConnection _connection = null!;
    private IChannel _channel = null!;

    public TopologyRoutingTests(StackFixture stack)
    {
        ArgumentNullException.ThrowIfNull(stack);
        _stack = stack;
    }

    /// <summary>
    /// Every event type the platform publishes, and the queues that must receive it.
    /// </summary>
    /// <remarks>
    /// Written from <c>contracts/messaging.md</c>'s consumer column, as event types rather than as
    /// hand-shortened patterns — these are the exact strings <c>DomainEvent.EventType</c> produces,
    /// which is what ends up on the wire.
    /// </remarks>
    public static TheoryData<string, string[]> ExpectedRouting() => new()
    {
        { "chat.directory.employee.changed.v1", ["directory.sync"] },
        { "chat.message.sent.v1", ["notifications.fanout", "search.index", "realtime.fanout"] },
        { "chat.message.edited.v1", ["search.index", "realtime.fanout"] },
        { "chat.message.deleted.v1", ["search.index", "realtime.fanout"] },
        { "chat.membership.changed.v1", ["membership.fanout"] },
        { "chat.read_state.updated.v1", ["readstate.fanout"] },
        { "chat.attachment.uploaded.v1", ["attachments.scan"] },
        // Two queues, deliberately (T192). meetings.lifecycle announces the meeting over SignalR
        // from the API; meetings.audit writes the FR-051 record from the Worker. One shared queue
        // would hand each event to whichever host reached it first, so half the meetings would be
        // announced and the other half audited — the failure a topic exchange exists to prevent.
        { "chat.meeting.started.v1", ["meetings.lifecycle", "meetings.audit"] },
        { "chat.meeting.ended.v1", ["meetings.lifecycle", "meetings.audit"] },
    };

    public async Task InitializeAsync()
    {
        ConnectionFactory factory = new() { Uri = new Uri(_stack.RabbitMqConnectionString) };

        _connection = await factory.CreateConnectionAsync();
        _channel = await _connection.CreateChannelAsync();

        await ChatTopology.DeclareAsync(_channel);
    }

    public async Task DisposeAsync()
    {
        await _channel.DisposeAsync();
        await _connection.DisposeAsync();
    }

    /// <summary>
    /// A published event reaches exactly the queues that are supposed to consume it.
    /// </summary>
    /// <remarks>
    /// Both directions are asserted. A queue missing the event is a message silently discarded; a
    /// queue receiving one it should not is a consumer doing work for an event it has no handler
    /// for, which ends up in that queue's DLQ and pages somebody.
    /// </remarks>
    [Theory]
    [MemberData(nameof(ExpectedRouting))]
    public async Task An_event_reaches_exactly_the_queues_that_consume_it(string routingKey, string[] expected)
    {
        ArgumentNullException.ThrowIfNull(expected);

        // Drained first: these queues are shared with the rest of the suite, and a message left by
        // another test would be counted as a delivery this one caused.
        foreach (string queue in AllQueues)
        {
            await _channel.QueuePurgeAsync(queue);
        }

        await _channel.BasicPublishAsync(
            exchange: ChatTopology.EventsExchange,
            routingKey: routingKey,
            mandatory: false,
            basicProperties: new BasicProperties { Persistent = true, Type = routingKey },
            body: Encoding.UTF8.GetBytes("""{"probe":true}"""));

        List<string> received = [];

        foreach (string queue in AllQueues)
        {
            BasicGetResult? result = await _channel.BasicGetAsync(queue, autoAck: true);

            if (result is not null)
            {
                received.Add(queue);
            }
        }

        Assert.Equal([.. expected.Order()], [.. received.Order()]);
    }

    /// <summary>
    /// No binding uses a single-word wildcard where the routing keys are longer than it.
    /// </summary>
    /// <remarks>
    /// A structural companion to the routing theory above, and the one that would have caught the
    /// original defect on the day it was written rather than on the day something was deployed. It
    /// reads the declared patterns and the published event types and reports any pattern that can
    /// never match anything — an empty binding is otherwise indistinguishable from a working one
    /// until a message goes missing.
    /// </remarks>
    [Fact]
    public void No_declared_binding_is_unmatchable()
    {
        string[] eventTypes = [.. ExpectedRouting().Select(row => (string)row[0]!)];

        List<string> unmatchable = [];

        foreach (QueueDefinition queue in ChatTopology.Queues)
        {
            if (QueuesAwaitingAProducer.ContainsKey(queue.Name))
            {
                continue;
            }

            if (!eventTypes.Any(type => Matches(queue.BindingPattern, type)))
            {
                unmatchable.Add($"{queue.Name} bound to '{queue.BindingPattern}'");
            }
        }

        Assert.True(
            unmatchable.Count == 0,
            $"""
            These bindings cannot match any event this platform publishes:

              {string.Join("\n  ", unmatchable)}

            Known event types:
              {string.Join("\n  ", eventTypes)}

            AMQP '*' matches exactly one word and '#' matches zero or more. A binding that matches
            nothing does not fail: the exchange discards the message, the publisher confirm still
            succeeds, and the outbox row is marked dispatched.
            """);
    }

    /// <summary>
    /// Queues declared ahead of the code that will publish to them, and the task that will.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Listed explicitly rather than skipped by a general rule, so the exemption is a decision with a
    /// name attached. A queue that sits here forever is a queue nobody is filling, and the entry is
    /// what makes that visible at review.
    /// </para>
    /// <para>
    /// <c>audit.write</c> exists because <c>contracts/messaging.md</c> declares it, but nothing
    /// publishes <c>chat.audit.*</c> today: T047's <c>AuditLog</c> writes to PostgreSQL directly and
    /// synchronously, which is what makes an action and its audit record commit atomically. The queue
    /// is for T192, where meeting lifecycle audit arrives from LiveKit webhooks rather than from a
    /// use case and so has no ambient transaction to join.
    /// </para>
    /// </remarks>
    private static readonly Dictionary<string, string> QueuesAwaitingAProducer = new(StringComparer.Ordinal)
    {
        ["audit.write"] = "T192 — meeting lifecycle audit, published from the LiveKit webhook path",
    };

    /// <summary>Applies AMQP topic-matching rules to a pattern and a routing key.</summary>
    /// <remarks>
    /// Reimplemented rather than asked of the broker, because this test has to be able to say
    /// <em>which</em> pattern is wrong without declaring a queue per candidate. The theory above is
    /// what confirms these rules against the real broker.
    /// </remarks>
    private static bool Matches(string pattern, string routingKey)
    {
        string[] patternWords = pattern.Split('.');
        string[] keyWords = routingKey.Split('.');

        return Matches(patternWords, 0, keyWords, 0);
    }

    private static bool Matches(string[] pattern, int patternIndex, string[] key, int keyIndex)
    {
        while (true)
        {
            if (patternIndex == pattern.Length)
            {
                return keyIndex == key.Length;
            }

            if (pattern[patternIndex] == "#")
            {
                // Zero or more words. Try every possible consumption, shortest first.
                for (int consumed = 0; keyIndex + consumed <= key.Length; consumed++)
                {
                    if (Matches(pattern, patternIndex + 1, key, keyIndex + consumed))
                    {
                        return true;
                    }
                }

                return false;
            }

            if (keyIndex == key.Length)
            {
                return false;
            }

            if (pattern[patternIndex] != "*"
                && !string.Equals(pattern[patternIndex], key[keyIndex], StringComparison.Ordinal))
            {
                return false;
            }

            patternIndex++;
            keyIndex++;
        }
    }

    private static string[] AllQueues => [.. ChatTopology.Queues.Select(q => q.Name)];
}
