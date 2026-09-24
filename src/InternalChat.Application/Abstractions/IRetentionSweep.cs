namespace InternalChat.Application.Abstractions;

/// <summary>What one retention pass removed.</summary>
/// <param name="SweptThrough">The last day now guaranteed absent from the platform.</param>
/// <param name="PartitionsDropped">Message partitions dropped whole, newest first.</param>
/// <param name="MessagesDeleted">
/// Rows removed. Counted before the drop, because a dropped partition cannot be counted afterwards.
/// </param>
/// <param name="AttachmentsDeleted">Attachment rows removed, and objects purged with them.</param>
public sealed record RetentionSweepResult(
    DateOnly SweptThrough,
    IReadOnlyList<string> PartitionsDropped,
    long MessagesDeleted,
    long AttachmentsDeleted);

/// <summary>
/// Expires content past the retention window (FR-052, research.md D11).
/// </summary>
/// <remarks>
/// <para>
/// <b>Messages go by <c>DROP TABLE</c> on a whole partition, not by <c>DELETE</c>.</b> That is the
/// entire reason <c>message</c> is partitioned at all. At 125 million rows a monthly
/// <c>DELETE ... WHERE sent_at &lt; ...</c> would rewrite tens of millions of rows, bloat the table,
/// hold locks for hours, and generate a WAL volume that dwarfs the data. A partition drop is a
/// catalogue operation measured in milliseconds.
/// </para>
/// <para>
/// <b>Attachments cannot work that way and are deleted in batches.</b> The <c>attachment</c> table
/// is not partitioned — its rows are found by conversation and by message, not by date range — and
/// each one owns an object in MinIO that has to be removed individually. Batching bounds the
/// transaction and lets the sweep be interrupted and resumed without losing its place.
/// </para>
/// <para>
/// <b>Everything here is audited</b> (FR-052: "MUST record every retention deletion in the audit
/// log"). The audit record is the only evidence that data which no longer exists ever did, and it
/// is what answers "was this deleted on schedule or by someone".
/// </para>
/// </remarks>
public interface IRetentionSweep
{
    /// <summary>
    /// Drops every message partition entirely older than the cutoff.
    /// </summary>
    /// <returns>The names of the partitions dropped.</returns>
    /// <remarks>
    /// <b>Entirely older.</b> A partition covering a range that straddles the cutoff is left alone:
    /// dropping it would take messages that are still inside the retention window with it, which is
    /// data loss rather than retention. The consequence is that content survives up to a month past
    /// its nominal expiry, which is the tolerance FR-052 is measured against and T211 asserts.
    /// </remarks>
    Task<IReadOnlyList<string>> DropExpiredMessagePartitionsAsync(
        DateOnly cutoff,
        CancellationToken cancellationToken = default);

    /// <summary>Counts the rows a drop is about to remove, so the audit record can state it.</summary>
    /// <remarks>
    /// Run before the drop, necessarily: a dropped partition holds nothing to count. An estimate
    /// from the planner would be cheaper and is not used — an audit record saying "approximately
    /// ten million messages were deleted" is not an audit record.
    /// </remarks>
    Task<long> CountMessagesBeforeAsync(DateOnly cutoff, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes one batch of expired attachments, purging their objects.
    /// </summary>
    /// <returns>How many were removed. Zero means the sweep is finished.</returns>
    Task<int> DeleteExpiredAttachmentBatchAsync(
        DateOnly cutoff,
        int batchSize,
        CancellationToken cancellationToken = default);
}
