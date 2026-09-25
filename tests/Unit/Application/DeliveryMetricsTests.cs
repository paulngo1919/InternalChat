using System.Diagnostics.Metrics;
using InternalChat.Application.Telemetry;

namespace InternalChat.UnitTests.Application;

/// <summary>
/// The delivery-latency instruments behind 002 FR-011 and the alert in FR-012.
/// </summary>
/// <remarks>
/// <para>
/// <b>The failure these guard against is a dashboard that looks right and measures nothing.</b> A
/// lag recorded in seconds instead of milliseconds, or under a name the alert rule does not query,
/// produces a flat green line — and the delay this feature exists to remove could return with the
/// monitoring still reporting all clear.
/// </para>
/// <para>
/// Each test uses its own <see cref="Meter"/>, so a listener cannot pick up measurements from any
/// other test running in parallel against the shared <see cref="ChatTelemetry.Meter"/>.
/// </para>
/// </remarks>
public sealed class DeliveryMetricsTests : UnitTestBase
{
    private readonly Meter _meter = new($"test.{Guid.NewGuid():N}");
    private readonly MeterListener _listener = new();
    private readonly List<(string Instrument, double Value, KeyValuePair<string, object?>[] Tags)> _recorded = [];

    public DeliveryMetricsTests()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (ReferenceEquals(instrument.Meter, _meter))
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };

        _listener.SetMeasurementEventCallback<double>(
            (instrument, value, tags, _) => _recorded.Add((instrument.Name, value, tags.ToArray())));
        _listener.SetMeasurementEventCallback<long>(
            (instrument, value, tags, _) => _recorded.Add((instrument.Name, value, tags.ToArray())));

        _listener.Start();
    }

    [Fact]
    public void Outbox_lag_is_recorded_in_milliseconds_tagged_by_event_type_only()
    {
        new DeliveryMetrics(_meter).RecordOutboxLag("chat.message.sent.v1", TimeSpan.FromMilliseconds(42));

        var (instrument, value, tags) = Assert.Single(_recorded);

        Assert.Equal(ChatTelemetry.Metrics.OutboxLag, instrument);
        Assert.Equal(42, value);
        var tag = Assert.Single(tags);
        Assert.Equal(ChatTelemetry.Metrics.EventType, tag.Key);
        Assert.Equal("chat.message.sent.v1", tag.Value);
    }

    [Fact]
    public void Fanout_lag_is_recorded_in_milliseconds_tagged_by_event_type_only()
    {
        new DeliveryMetrics(_meter).RecordFanoutLag("chat.message.edited.v1", TimeSpan.FromSeconds(1.5));

        var (instrument, value, tags) = Assert.Single(_recorded);

        Assert.Equal(ChatTelemetry.Metrics.FanoutLag, instrument);
        Assert.Equal(1500, value);
        Assert.Equal(ChatTelemetry.Metrics.EventType, Assert.Single(tags).Key);
    }

    [Fact]
    public void A_negative_lag_from_clock_skew_is_clamped_to_zero_rather_than_dropped()
    {
        // Dropped, it would bias the distribution towards slow samples — the fast deliveries are
        // exactly the ones that clock skew pushes below zero.
        new DeliveryMetrics(_meter).RecordFanoutLag("chat.message.sent.v1", TimeSpan.FromMilliseconds(-8));

        Assert.Equal(0, Assert.Single(_recorded).Value);
    }

    [Fact]
    public void A_client_bucket_is_recorded_once_per_counted_delivery_inside_that_bucket()
    {
        new DeliveryMetrics(_meter).RecordClientLag("webSockets", upperBoundMs: 100, count: 3);

        Assert.Equal(3, _recorded.Count);
        Assert.All(_recorded, r =>
        {
            Assert.Equal(ChatTelemetry.Metrics.ClientLag, r.Instrument);

            // At the bound, not past it: histogram buckets are upper-inclusive, so the bound
            // itself lands in the bucket the browser counted it in.
            Assert.Equal(100, r.Value);
            var tag = Assert.Single(r.Tags);
            Assert.Equal(ChatTelemetry.Metrics.Transport, tag.Key);
            Assert.Equal("webSockets", tag.Value);
        });
    }

    [Fact]
    public void The_overflow_bucket_lands_beyond_the_largest_finite_bound()
    {
        new DeliveryMetrics(_meter).RecordClientLag("longPolling", double.PositiveInfinity, count: 1);

        Assert.True(Assert.Single(_recorded).Value > DeliveryMetrics.BucketBoundariesMs[^1]);
    }

    [Fact]
    public void A_zero_count_records_nothing()
    {
        new DeliveryMetrics(_meter).RecordClientLag("webSockets", 50, count: 0);

        Assert.Empty(_recorded);
    }

    [Fact]
    public void Connections_are_counted_up_and_down_by_transport()
    {
        DeliveryMetrics metrics = new(_meter);

        metrics.ConnectionOpened("webSockets");
        metrics.ConnectionClosed("webSockets");

        Assert.Equal(2, _recorded.Count);
        Assert.All(_recorded, r => Assert.Equal(ChatTelemetry.Metrics.HubConnections, r.Instrument));
        Assert.Equal([1d, -1d], _recorded.Select(r => r.Value));
    }

    [Fact]
    public void Bucket_boundaries_match_the_data_model()
    {
        // data-model §6 and contracts/delivery-telemetry.openapi.yaml name these exact bounds; the
        // browser buckets into them, so a drift here would misfile every client sample.
        Assert.Equal(
            [5d, 10d, 25d, 50d, 100d, 200d, 300d, 500d, 1000d, 2000d, 5000d],
            DeliveryMetrics.BucketBoundariesMs);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _listener.Dispose();
            _meter.Dispose();
        }

        base.Dispose(disposing);
    }
}
