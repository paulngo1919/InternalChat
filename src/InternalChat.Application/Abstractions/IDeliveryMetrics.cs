namespace InternalChat.Application.Abstractions;

/// <summary>
/// Records how long message delivery takes, stage by stage (002 FR-011, research R6).
/// </summary>
/// <remarks>
/// <para>
/// Callers pass an event type or a transport name and nothing else. No conversation, message, or
/// employee identifier may be passed through this interface: a metric series is retained for 30
/// days and scraped into a system with no access control of its own, so an identifier here would
/// be a record of who talked to whom (Principle IV).
/// </para>
/// <para>
/// Negative lags are clamped to zero rather than rejected. They mean two clocks disagree — the
/// browser's and the server's, or two hosts' — and dropping them would bias the distribution
/// towards slow samples.
/// </para>
/// </remarks>
public interface IDeliveryMetrics
{
    /// <summary>An outbox row reached the broker, <paramref name="lag"/> after it was committed.</summary>
    void RecordOutboxLag(string eventType, TimeSpan lag);

    /// <summary>A message event was handed to the hub, <paramref name="lag"/> after the message was committed.</summary>
    void RecordFanoutLag(string eventType, TimeSpan lag);

    /// <summary>
    /// Browsers observed <paramref name="count"/> deliveries in the bucket ending at
    /// <paramref name="upperBoundMs"/> (<see cref="double.PositiveInfinity"/> for the overflow bucket).
    /// </summary>
    void RecordClientLag(string transport, double upperBoundMs, long count);

    /// <summary>A hub connection opened on <paramref name="transport"/>.</summary>
    void ConnectionOpened(string transport);

    /// <summary>A hub connection on <paramref name="transport"/> closed.</summary>
    void ConnectionClosed(string transport);
}
