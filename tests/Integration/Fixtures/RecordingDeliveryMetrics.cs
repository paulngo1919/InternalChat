using System.Collections.Concurrent;
using InternalChat.Application.Abstractions;

namespace InternalChat.IntegrationTests.Fixtures;

/// <summary>
/// An <see cref="IDeliveryMetrics"/> that keeps what it was told, for asserting that a delivery
/// stage was measured (002 FR-011).
/// </summary>
/// <remarks>
/// A stand-in for the metrics sink, not for PostgreSQL, RabbitMQ, or Redis — those stay real, as
/// Principle III requires. What is under test is that the stage records, and what it records.
/// </remarks>
public sealed class RecordingDeliveryMetrics : IDeliveryMetrics
{
    /// <summary>Every observation, in the order recorded.</summary>
    public ConcurrentQueue<(string Stage, string Tag, TimeSpan Lag)> Observations { get; } = new();

    /// <inheritdoc />
    public void RecordOutboxLag(string eventType, TimeSpan lag) => Observations.Enqueue(("outbox", eventType, lag));

    /// <inheritdoc />
    public void RecordFanoutLag(string eventType, TimeSpan lag) => Observations.Enqueue(("fanout", eventType, lag));

    /// <inheritdoc />
    public void RecordClientLag(string transport, double upperBoundMs, long count) =>
        Observations.Enqueue(("client", transport, TimeSpan.FromMilliseconds(double.IsInfinity(upperBoundMs) ? -1 : upperBoundMs * count)));

    /// <inheritdoc />
    public void ConnectionOpened(string transport) => Observations.Enqueue(("connection+", transport, TimeSpan.Zero));

    /// <inheritdoc />
    public void ConnectionClosed(string transport) => Observations.Enqueue(("connection-", transport, TimeSpan.Zero));
}
