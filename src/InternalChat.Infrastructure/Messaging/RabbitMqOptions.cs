namespace InternalChat.Infrastructure.Messaging;

/// <summary>RabbitMQ connection and dispatcher settings.</summary>
public sealed class RabbitMqOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "RabbitMq";

    /// <summary>Broker host.</summary>
    public string Host { get; set; } = "localhost";

    /// <summary>Broker port.</summary>
    public int Port { get; set; } = 5672;

    /// <summary>Username.</summary>
    public string User { get; set; } = "guest";

    /// <summary>Password.</summary>
    public string Password { get; set; } = "guest";

    /// <summary>Virtual host.</summary>
    public string VHost { get; set; } = "/";

    /// <summary>Rows claimed per dispatcher pass.</summary>
    /// <remarks>
    /// Bounded so one pass cannot hold a transaction open across an unbounded number of network
    /// round trips. At 100 messages/second, 100 per pass keeps up comfortably.
    /// </remarks>
    public int DispatchBatchSize { get; set; } = 100;

    /// <summary>
    /// Backstop poll: how long the dispatcher waits for an <c>outbox_ready</c> notification before
    /// looking at the table anyway.
    /// </summary>
    /// <remarks>
    /// Not what determines delivery latency. A committed outbox row wakes the dispatcher through
    /// PostgreSQL <c>NOTIFY</c> within a round trip (002 research R1); this interval only bounds how
    /// long a row can wait if that notification is lost — the listener reconnecting, say. It was
    /// 1 s when it was the only wake-up, which added a mean of half a second to every message.
    /// </remarks>
    public TimeSpan IdlePollInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Handlers run concurrently on <c>realtime.fanout</c>. Every other queue stays at 1.
    /// </summary>
    /// <remarks>
    /// Safe because clients apply message events by <c>seq</c> with the tombstone and
    /// latest-version rules of hub contract 1.1.0, so arrival order does not matter (002 research
    /// R3). A serial fan-out consumer caps a replica at a few hundred deliveries per second, which a
    /// 1,000 message/second burst overruns.
    /// </remarks>
    public int FanoutConsumerConcurrency { get; set; } = 8;

    /// <summary>Upper bound on the outbox listener's reconnect backoff.</summary>
    public TimeSpan ListenerMaxReconnectDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Publish attempts before a row is left for investigation.
    /// </summary>
    /// <remarks>
    /// An outbox row that will never publish must stop consuming dispatcher capacity, or it
    /// blocks every event behind it. After this many attempts it is skipped and alerted on.
    /// </remarks>
    public int MaxDispatchAttempts { get; set; } = 10;

    /// <summary>
    /// Backoff schedule for failed consumer deliveries. One retry queue is declared per entry.
    /// </summary>
    /// <remarks>
    /// Configurable rather than constant so integration tests can use millisecond delays. A test
    /// for the dead-letter path would otherwise have to wait out the real 1s + 5s + 25s schedule,
    /// and a 31-second test is one people learn to skip.
    /// </remarks>
    public IReadOnlyList<TimeSpan> RetryDelays { get; set; } = ChatTopology.DefaultRetryDelays;

    /// <summary>Builds the AMQP URI.</summary>
    public Uri ToUri() =>
        new($"amqp://{Uri.EscapeDataString(User)}:{Uri.EscapeDataString(Password)}@{Host}:{Port}/{Uri.EscapeDataString(VHost.TrimStart('/'))}");
}
