using System.Globalization;
using InternalChat.Application.Abstractions;
using InternalChat.Application.Attachments;

namespace InternalChat.Worker.Jobs;

/// <summary>
/// T155 — alerts administrators before attachment storage is exhausted (FR-028).
/// </summary>
/// <remarks>
/// <para>
/// <b>The alert has to arrive before the refusal, or it is not an alert.</b>
/// <c>RequestUploadHandler</c> starts refusing uploads at
/// <see cref="RequestUploadHandler.CapacityRefusalThreshold"/>; this warns at
/// <see cref="WarningThreshold"/> and escalates at <see cref="CriticalThreshold"/>, both below it.
/// An alert that fired at the same point as the refusal would tell an administrator about an
/// outage rather than let them prevent one — and provisioning storage is not a thing anyone does
/// in the seconds between.
/// </para>
/// <para>
/// <b>Emitted as a log event at a severity the observability stack already routes</b>, rather than
/// through a bespoke notification path. FR-028 requires administrators to be alerted, and the
/// deployment's Prometheus and Loki configuration (T046) is where alerting is actually wired;
/// inventing a second channel here would create a notification nobody has subscribed to.
/// </para>
/// <para>
/// <b>Listing every object to sum sizes is not free</b>, which is why this runs hourly rather than
/// per upload. Capacity moves at the speed of people uploading video, so an hour of staleness costs
/// nothing against a threshold set twenty-five points below the refusal point.
/// </para>
/// </remarks>
public sealed partial class StorageCapacityJob : BackgroundService
{
    /// <summary>Fraction of capacity at which administrators are first warned.</summary>
    /// <remarks>
    /// 0.75, comfortably below the 0.95 refusal point. The gap is the point: it is meant to be
    /// enough time to provision more storage, not enough time to watch it run out.
    /// </remarks>
    public const double WarningThreshold = 0.75;

    /// <summary>Fraction of capacity at which the warning escalates to an error.</summary>
    public const double CriticalThreshold = 0.90;

    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<StorageCapacityJob> _logger;

    /// <summary>Creates the job.</summary>
    public StorageCapacityJob(IServiceScopeFactory scopes, ILogger<StorageCapacityJob> logger)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(logger);

        _scopes = scopes;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Checked immediately, before the first delay. A Worker restarting into an already-full
        // bucket should say so at once rather than an hour later.
        using PeriodicTimer timer = new(Interval);

        do
        {
            try
            {
                await CheckAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // Swallowed for the same reason PartitionMaintenanceJob swallows: an unhandled
                // exception in a BackgroundService stops the host, and MinIO being briefly
                // unreachable must not take the outbox dispatcher and every consumer down with it.
                CapacityCheckFailed(_logger, exception);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    private async Task CheckAsync(CancellationToken cancellationToken)
    {
        using IServiceScope scope = _scopes.CreateScope();

        IObjectStore objects = scope.ServiceProvider.GetRequiredService<IObjectStore>();

        StorageCapacity capacity = await objects.GetCapacityAsync(cancellationToken).ConfigureAwait(false);

        string used = Percent(capacity.UsedFraction);

        if (capacity.UsedFraction >= RequestUploadHandler.CapacityRefusalThreshold)
        {
            // Already refusing. Reported separately from "critical" because the operational
            // situation is different: people are currently seeing failed uploads.
            UploadsRefused(_logger, used, capacity.UsedBytes, capacity.CapacityBytes);
        }
        else if (capacity.UsedFraction >= CriticalThreshold)
        {
            CapacityCritical(_logger, used, capacity.UsedBytes, capacity.CapacityBytes);
        }
        else if (capacity.UsedFraction >= WarningThreshold)
        {
            CapacityWarning(_logger, used, capacity.UsedBytes, capacity.CapacityBytes);
        }
        else
        {
            CapacityHealthy(_logger, used);
        }
    }

    private static string Percent(double fraction) =>
        (fraction * 100).ToString("F1", CultureInfo.InvariantCulture) + "%";

    [LoggerMessage(EventId = 4201, Level = LogLevel.Debug, Message = "Attachment storage at {Used}")]
    private static partial void CapacityHealthy(ILogger logger, string used);

    [LoggerMessage(EventId = 4202, Level = LogLevel.Warning, Message = "Attachment storage at {Used} ({UsedBytes} of {CapacityBytes} bytes). Provision more before uploads start being refused (FR-028).")]
    private static partial void CapacityWarning(ILogger logger, string used, long usedBytes, long capacityBytes);

    [LoggerMessage(EventId = 4203, Level = LogLevel.Error, Message = "Attachment storage at {Used} ({UsedBytes} of {CapacityBytes} bytes). Uploads will be refused shortly; messaging is unaffected (FR-028).")]
    private static partial void CapacityCritical(ILogger logger, string used, long usedBytes, long capacityBytes);

    [LoggerMessage(EventId = 4204, Level = LogLevel.Critical, Message = "Attachment storage at {Used} ({UsedBytes} of {CapacityBytes} bytes). Uploads are being refused now; text messaging continues (FR-028).")]
    private static partial void UploadsRefused(ILogger logger, string used, long usedBytes, long capacityBytes);

    [LoggerMessage(EventId = 4205, Level = LogLevel.Warning, Message = "Could not read attachment storage capacity; will retry on the next tick")]
    private static partial void CapacityCheckFailed(ILogger logger, Exception exception);
}
