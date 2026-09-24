using InternalChat.Domain.Common;
using System.Globalization;
using InternalChat.Application.Abstractions;
using Microsoft.Extensions.Options;

namespace InternalChat.Worker.Jobs;

/// <summary>How long content is kept (FR-052, FR-054).</summary>
public sealed class RetentionOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Retention";

    /// <summary>
    /// Months of history kept.
    /// </summary>
    /// <remarks>
    /// FR-054 requires this to be changeable by an administrator without a code change, which is
    /// why it is configuration rather than a constant. Twelve is the answer spec.md records for the
    /// retention question; there is deliberately no legal-hold exception.
    /// </remarks>
    public int Months { get; set; } = 12;

    /// <summary>Attachments removed per transaction.</summary>
    /// <remarks>
    /// Bounded so one pass cannot hold a long transaction or a large amount of memory. Each row
    /// costs two object-store deletes, so this is an I/O batch rather than a database one.
    /// </remarks>
    public int AttachmentBatchSize { get; set; } = 500;

    /// <summary>Most batches per run, so a first sweep over a large backlog does not run for hours.</summary>
    public int MaximumBatchesPerRun { get; set; } = 40;
}

/// <summary>
/// T206 — expires content past the retention window, by partition drop (FR-052).
/// </summary>
/// <remarks>
/// <para>
/// <b>Daily rather than monthly, and that is the same reasoning as
/// <see cref="PartitionMaintenanceJob"/>.</b> A monthly schedule has exactly one chance to fire; if
/// the Worker happens to be restarting then, the window is missed silently and nothing notices
/// until a compliance question is asked. Daily makes the operation idempotent and self-healing —
/// most runs find nothing to do and say so.
/// </para>
/// <para>
/// <b>Every pass writes an audit record, including the ones that delete nothing</b> (FR-052). The
/// record is the only evidence that data which no longer exists ever did, and "the sweep ran and
/// found nothing expired" is a different and equally necessary statement from silence — which is
/// indistinguishable from the job being broken.
/// </para>
/// <para>
/// <b>Messages and attachments expire on different clocks by construction.</b> Messages go when
/// their whole partition is outside the window, so they survive up to a month past nominal expiry;
/// attachments go by row and leave on the day. That asymmetry is the price of the partition drop
/// and is the tolerance T211 asserts rather than a defect.
/// </para>
/// </remarks>
public sealed partial class RetentionSweepJob : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    private readonly IServiceScopeFactory _scopes;
    private readonly IOptionsMonitor<RetentionOptions> _options;
    private readonly ILogger<RetentionSweepJob> _logger;

    /// <summary>Creates the job.</summary>
    public RetentionSweepJob(
        IServiceScopeFactory scopes,
        IOptionsMonitor<RetentionOptions> options,
        ILogger<RetentionSweepJob> logger)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _scopes = scopes;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(Interval);

        do
        {
            try
            {
                await SweepAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // Swallowed, like every other job here: an unhandled exception in a
                // BackgroundService stops the host, and a retention hiccup must not take the outbox
                // dispatcher and every consumer down with it. The next tick retries.
                SweepFailed(_logger, exception);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    private async Task SweepAsync(CancellationToken cancellationToken)
    {
        // IOptionsMonitor, not IOptions: FR-054 lets an administrator change the period without a
        // code change, and a snapshot taken at construction would ignore that until a restart.
        RetentionOptions options = _options.CurrentValue;

        using IServiceScope scope = _scopes.CreateScope();

        IRetentionSweep sweep = scope.ServiceProvider.GetRequiredService<IRetentionSweep>();
        IAuditLog audit = scope.ServiceProvider.GetRequiredService<IAuditLog>();
        IClock clock = scope.ServiceProvider.GetRequiredService<IClock>();

        DateOnly cutoff = DateOnly.FromDateTime(
            clock.UtcNow.UtcDateTime.AddMonths(-Math.Max(1, options.Months)));

        // Counted BEFORE the drop. A dropped partition holds nothing to count, and an audit record
        // that says "some messages were deleted" is not an audit record.
        long messages = await sweep.CountMessagesBeforeAsync(cutoff, cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<string> partitions = await sweep
            .DropExpiredMessagePartitionsAsync(cutoff, cancellationToken)
            .ConfigureAwait(false);

        long attachments = 0;

        for (int batch = 0; batch < options.MaximumBatchesPerRun; batch++)
        {
            int removed = await sweep
                .DeleteExpiredAttachmentBatchAsync(cutoff, options.AttachmentBatchSize, cancellationToken)
                .ConfigureAwait(false);

            if (removed == 0)
            {
                break;
            }

            attachments += removed;
        }

        // Written even when nothing expired. Silence is indistinguishable from the job being
        // broken, and FR-052 asks for a record of the deletion rather than of the interesting ones.
        await audit.RecordAsync(
            new AuditEntry(
                Action: "retention.swept",

                // No actor: nobody triggered this. Attributing a scheduled deletion to a person
                // would make the audit log say someone deleted a year of messages.
                ActorId: null,
                SubjectType: "retention",
                SubjectId: null,
                SourceIp: null,
                Outcome: AuditOutcome.Success,
                Detail: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["sweptThrough"] = cutoff.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    ["retentionMonths"] = options.Months.ToString(CultureInfo.InvariantCulture),
                    ["messagesDeleted"] = messages.ToString(CultureInfo.InvariantCulture),
                    ["attachmentsDeleted"] = attachments.ToString(CultureInfo.InvariantCulture),
                    ["partitionsDropped"] = partitions.Count == 0
                        ? "none"
                        : string.Join(",", partitions),
                }),
            cancellationToken)
            .ConfigureAwait(false);

        if (partitions.Count > 0 || attachments > 0)
        {
            SweepCompleted(_logger, cutoff, partitions.Count, messages, attachments);
        }
        else
        {
            NothingExpired(_logger, cutoff);
        }
    }

    [LoggerMessage(EventId = 4801, Level = LogLevel.Information, Message = "Retention swept through {Cutoff}: {Partitions} partitions, {Messages} messages, {Attachments} attachments")]
    private static partial void SweepCompleted(ILogger logger, DateOnly cutoff, int partitions, long messages, long attachments);

    [LoggerMessage(EventId = 4802, Level = LogLevel.Debug, Message = "Retention swept through {Cutoff}; nothing had expired")]
    private static partial void NothingExpired(ILogger logger, DateOnly cutoff);

    [LoggerMessage(EventId = 4803, Level = LogLevel.Error, Message = "The retention sweep failed and will retry on the next tick")]
    private static partial void SweepFailed(ILogger logger, Exception exception);
}
