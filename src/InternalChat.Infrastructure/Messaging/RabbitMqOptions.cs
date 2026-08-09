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

    /// <summary>Pause between passes when the outbox was empty.</summary>
    public TimeSpan IdlePollInterval { get; set; } = TimeSpan.FromSeconds(1);

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
