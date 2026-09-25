using System.Globalization;
using InternalChat.Application.Abstractions;
using InternalChat.Application.Behaviors;

namespace InternalChat.Application.Telemetry;

/// <summary>
/// A browser's aggregated view of delivery lag over one reporting window (002 FR-011).
/// </summary>
/// <param name="Transport"><c>webSockets</c> or <c>longPolling</c>.</param>
/// <param name="WindowSeconds">How long the counts cover, 1–300.</param>
/// <param name="Buckets">
/// Non-cumulative counts keyed by upper bound in milliseconds (the <see cref="DeliveryMetrics"/>
/// bounds) or <c>+Inf</c>.
/// </param>
/// <remarks>
/// Deliberately has no employee, conversation, or message field: there is nothing identifying to
/// record, so nothing identifying can be recorded (Principle IV). The caller is authenticated only
/// so that the endpoint is not an open write into the metrics pipeline.
/// </remarks>
public sealed record RecordDeliveryLag(string Transport, int WindowSeconds, IReadOnlyDictionary<string, long> Buckets);

/// <summary>Adds a browser's report to <c>chat.delivery.client_lag</c>.</summary>
public sealed class RecordDeliveryLagHandler : IUseCase<RecordDeliveryLag, bool>
{
    private readonly IDeliveryMetrics _metrics;

    /// <summary>Creates the handler.</summary>
    public RecordDeliveryLagHandler(IDeliveryMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        _metrics = metrics;
    }

    /// <inheritdoc />
    public Task<bool> HandleAsync(RecordDeliveryLag request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        foreach ((string bucket, long count) in request.Buckets)
        {
            if (count > 0)
            {
                _metrics.RecordClientLag(request.Transport, RecordDeliveryLagValidator.BoundOf(bucket), count);
            }
        }

        return Task.FromResult(true);
    }
}

/// <summary>Refuses anything that is not exactly a report the contract describes.</summary>
public sealed class RecordDeliveryLagValidator : IValidator<RecordDeliveryLag>
{
    /// <summary>The overflow bucket's name.</summary>
    public const string OverflowBucket = "+Inf";

    /// <summary>Largest count accepted in one bucket (contracts/delivery-telemetry.openapi.yaml).</summary>
    public const long MaximumCount = 100_000;

    private static readonly HashSet<string> Transports = new(StringComparer.Ordinal) { "webSockets", "longPolling" };

    private static readonly HashSet<string> BucketNames = new(
        DeliveryMetrics.BucketBoundariesMs
            .Select(b => b.ToString(CultureInfo.InvariantCulture))
            .Append(OverflowBucket),
        StringComparer.Ordinal);

    /// <summary>The upper bound, in milliseconds, a validated bucket name stands for.</summary>
    public static double BoundOf(string bucket) =>
        string.Equals(bucket, OverflowBucket, StringComparison.Ordinal)
            ? double.PositiveInfinity
            : double.Parse(bucket, CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public ValueTask<ValidationResult> ValidateAsync(RecordDeliveryLag request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        List<ValidationError> errors = [];

        if (!Transports.Contains(request.Transport ?? string.Empty))
        {
            errors.Add(new ValidationError("transport", "Transport must be webSockets or longPolling."));
        }

        if (request.WindowSeconds is < 1 or > 300)
        {
            errors.Add(new ValidationError("windowSeconds", "A report covers between 1 and 300 seconds."));
        }

        if (request.Buckets is null || request.Buckets.Count == 0)
        {
            errors.Add(new ValidationError("buckets", "A report must count at least one delivery."));
        }
        else
        {
            if (request.Buckets.Keys.Any(k => !BucketNames.Contains(k)))
            {
                errors.Add(new ValidationError("buckets", $"Buckets must be one of: {string.Join(", ", BucketNames)}."));
            }

            if (request.Buckets.Values.Any(c => c is < 0 or > MaximumCount))
            {
                errors.Add(new ValidationError("buckets", $"Each count must be between 0 and {MaximumCount}."));
            }
        }

        return ValueTask.FromResult(errors.Count == 0 ? ValidationResult.Valid : new ValidationResult(errors));
    }
}
