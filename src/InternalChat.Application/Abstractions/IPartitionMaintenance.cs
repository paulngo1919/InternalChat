namespace InternalChat.Application.Abstractions;

/// <summary>
/// Keeps the monthly partitions of <c>message</c> ahead of the clock (research.md D11).
/// </summary>
/// <remarks>
/// <para>
/// <b>This is not housekeeping.</b> An <c>INSERT</c> into a partitioned table whose range has no
/// partition does not fall back to the parent — it fails outright with
/// <c>no partition of relation "message" found for row</c>. So a month boundary crossing at 00:00
/// with no partition ready rejects <em>every send on the platform</em> until someone notices.
/// </para>
/// <para>
/// An Application port rather than raw SQL in the Worker, because <c>InternalChat.Worker</c> may not
/// name an Infrastructure type outside its composition root (Principle I) — and because the job's
/// interesting behaviour is "run ahead, and say how far", which is worth being able to test without
/// a database.
/// </para>
/// </remarks>
public interface IPartitionMaintenance
{
    /// <summary>
    /// Ensures partitions exist for the current month and <paramref name="monthsAhead"/> beyond it.
    /// </summary>
    /// <returns>How many partitions the call created.</returns>
    /// <remarks>
    /// Idempotent: a partition that already exists is left alone, so this is safe to run on every
    /// startup and on every tick.
    /// </remarks>
    Task<int> EnsureMonthPartitionsAsync(
        int monthsAhead,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// How many months ahead of the current one already have partitions.
    /// </summary>
    /// <remarks>
    /// Exposed so the job can log and alert on the actual runway rather than on the fact that it
    /// ran. "Ensured partitions" in a log line is true even when the runway is zero and the next
    /// month is hours away.
    /// </remarks>
    Task<int> CountMonthsAheadAsync(CancellationToken cancellationToken = default);
}
