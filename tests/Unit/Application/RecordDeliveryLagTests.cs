using InternalChat.Application.Abstractions;
using InternalChat.Application.Telemetry;

namespace InternalChat.UnitTests.Application;

/// <summary>
/// 002 T045 — browsers reporting what delivery felt like (FR-011, contracts/delivery-telemetry.openapi.yaml).
/// </summary>
/// <remarks>
/// <para>
/// The only new external input in feature 002, so it is validated hard: a fixed set of bucket
/// names, bounded counts, a bounded window, a known transport. Anything else is refused rather than
/// clamped — a report that has to be guessed at is not a measurement.
/// </para>
/// <para>
/// And it carries nothing that identifies anyone. There is no field for a conversation, a message,
/// or an employee, so there is nothing to leak (Principle IV).
/// </para>
/// </remarks>
public sealed class RecordDeliveryLagTests : UnitTestBase
{
    private readonly RecordingMetrics _metrics = new();

    private static RecordDeliveryLag Report(
        string transport = "webSockets",
        int windowSeconds = 60,
        Dictionary<string, long>? buckets = null) =>
        new(transport, windowSeconds, buckets ?? new Dictionary<string, long>(StringComparer.Ordinal) { ["100"] = 3 });

    [Fact]
    public async Task Each_non_empty_bucket_is_recorded_once_with_its_bound_and_count()
    {
        await new RecordDeliveryLagHandler(_metrics).HandleAsync(Report(buckets: new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["50"] = 4,
            ["300"] = 1,
            ["1000"] = 0,
            ["+Inf"] = 2,
        }));

        Assert.Equal(
            [("webSockets", 50d, 4L), ("webSockets", 300d, 1L), ("webSockets", double.PositiveInfinity, 2L)],
            _metrics.Recorded.OrderBy(r => r.Bound));
    }

    [Fact]
    public async Task A_valid_report_passes_validation()
    {
        Assert.True((await new RecordDeliveryLagValidator().ValidateAsync(Report())).IsValid);
    }

    [Theory]
    [InlineData("150")]
    [InlineData("-5")]
    [InlineData("inf")]
    [InlineData("")]
    public async Task A_bucket_name_outside_the_fixed_bounds_is_refused(string bucket)
    {
        ValidationResult result = await new RecordDeliveryLagValidator().ValidateAsync(
            Report(buckets: new Dictionary<string, long>(StringComparer.Ordinal) { [bucket] = 1 }));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Field == "buckets");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(100_001)]
    public async Task A_count_outside_bounds_is_refused(long count)
    {
        ValidationResult result = await new RecordDeliveryLagValidator().ValidateAsync(
            Report(buckets: new Dictionary<string, long>(StringComparer.Ordinal) { ["100"] = count }));

        Assert.False(result.IsValid);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(301)]
    public async Task A_window_outside_one_to_three_hundred_seconds_is_refused(int windowSeconds)
    {
        ValidationResult result = await new RecordDeliveryLagValidator().ValidateAsync(Report(windowSeconds: windowSeconds));

        Assert.Contains(result.Errors, e => e.Field == "windowSeconds");
    }

    [Theory]
    [InlineData("serverSentEvents")]
    [InlineData("WEBSOCKETS")]
    [InlineData("")]
    public async Task An_unknown_transport_is_refused(string transport)
    {
        ValidationResult result = await new RecordDeliveryLagValidator().ValidateAsync(Report(transport: transport));

        Assert.Contains(result.Errors, e => e.Field == "transport");
    }

    [Fact]
    public async Task An_empty_report_is_refused()
    {
        // The client sends nothing when it received nothing; an empty report is a client bug.
        ValidationResult result = await new RecordDeliveryLagValidator().ValidateAsync(
            Report(buckets: new Dictionary<string, long>(StringComparer.Ordinal)));

        Assert.False(result.IsValid);
    }

    private sealed class RecordingMetrics : IDeliveryMetrics
    {
        public List<(string Transport, double Bound, long Count)> Recorded { get; } = [];

        public void RecordOutboxLag(string eventType, TimeSpan lag)
        {
        }

        public void RecordFanoutLag(string eventType, TimeSpan lag)
        {
        }

        public void RecordClientLag(string transport, double upperBoundMs, long count) =>
            Recorded.Add((transport, upperBoundMs, count));

        public void ConnectionOpened(string transport)
        {
        }

        public void ConnectionClosed(string transport)
        {
        }
    }
}
