using System.Diagnostics.Metrics;
using InternalChat.Application.Abstractions;

namespace InternalChat.Application.Telemetry;

/// <summary>
/// <see cref="IDeliveryMetrics"/> over BCL <see cref="Meter"/> instruments.
/// </summary>
/// <remarks>
/// <para>
/// In Application, beside <see cref="ChatTelemetry"/>, for the same reason that class is here: it
/// depends on nothing but the BCL, and the Api, the Worker, and Infrastructure all need to record
/// through it. Which exporter the measurements reach is decided by each host.
/// </para>
/// <para>
/// Explicit bucket boundaries, because the default OpenTelemetry boundaries jump from 250 ms to
/// 500 ms to 750 ms — too coarse to tell a 300 ms p95 from a 450 ms one, which is the whole
/// question the 002 alert asks.
/// </para>
/// </remarks>
public sealed class DeliveryMetrics : IDeliveryMetrics
{
    /// <summary>Histogram bucket upper bounds, in milliseconds (002 data-model §6).</summary>
    public static readonly IReadOnlyList<double> BucketBoundariesMs =
        [5, 10, 25, 50, 100, 200, 300, 500, 1000, 2000, 5000];

    /// <summary>
    /// Where an overflow-bucket client sample is recorded: past the last bound, so it falls into
    /// the histogram's own overflow bucket.
    /// </summary>
    private const double OverflowSampleMs = 10_000;

    private readonly Histogram<double> _outboxLag;
    private readonly Histogram<double> _fanoutLag;
    private readonly Histogram<double> _clientLag;
    private readonly UpDownCounter<long> _connections;

    /// <summary>Creates the instruments on the platform meter.</summary>
    public DeliveryMetrics()
        : this(ChatTelemetry.Meter)
    {
    }

    /// <summary>Creates the instruments on <paramref name="meter"/>. Tests pass their own.</summary>
    public DeliveryMetrics(Meter meter)
    {
        ArgumentNullException.ThrowIfNull(meter);

        InstrumentAdvice<double> advice = new() { HistogramBucketBoundaries = BucketBoundariesMs };

        _outboxLag = meter.CreateHistogram(
            ChatTelemetry.Metrics.OutboxLag,
            unit: "ms",
            description: "Outbox row commit to broker confirm.",
            tags: null,
            advice: advice);

        _fanoutLag = meter.CreateHistogram(
            ChatTelemetry.Metrics.FanoutLag,
            unit: "ms",
            description: "Message commit to the hub send completing.",
            tags: null,
            advice: advice);

        _clientLag = meter.CreateHistogram(
            ChatTelemetry.Metrics.ClientLag,
            unit: "ms",
            description: "Send to receipt, as observed and reported by browsers.",
            tags: null,
            advice: advice);

        _connections = meter.CreateUpDownCounter<long>(
            ChatTelemetry.Metrics.HubConnections,
            unit: "{connection}",
            description: "Open hub connections by negotiated transport.");
    }

    /// <inheritdoc />
    public void RecordOutboxLag(string eventType, TimeSpan lag) =>
        _outboxLag.Record(Milliseconds(lag), new KeyValuePair<string, object?>(ChatTelemetry.Metrics.EventType, eventType));

    /// <inheritdoc />
    public void RecordFanoutLag(string eventType, TimeSpan lag) =>
        _fanoutLag.Record(Milliseconds(lag), new KeyValuePair<string, object?>(ChatTelemetry.Metrics.EventType, eventType));

    /// <inheritdoc />
    public void RecordClientLag(string transport, double upperBoundMs, long count)
    {
        double sample = double.IsPositiveInfinity(upperBoundMs) ? OverflowSampleMs : upperBoundMs;
        KeyValuePair<string, object?> tag = new(ChatTelemetry.Metrics.Transport, transport);

        // One measurement per delivery the browser counted. The histogram has no "add N" call, and
        // the count is bounded by request validation, so this is a small loop rather than a risk.
        for (long i = 0; i < count; i++)
        {
            _clientLag.Record(sample, tag);
        }
    }

    /// <inheritdoc />
    public void ConnectionOpened(string transport) =>
        _connections.Add(1, new KeyValuePair<string, object?>(ChatTelemetry.Metrics.Transport, transport));

    /// <inheritdoc />
    public void ConnectionClosed(string transport) =>
        _connections.Add(-1, new KeyValuePair<string, object?>(ChatTelemetry.Metrics.Transport, transport));

    private static double Milliseconds(TimeSpan lag) => Math.Max(0, lag.TotalMilliseconds);
}
