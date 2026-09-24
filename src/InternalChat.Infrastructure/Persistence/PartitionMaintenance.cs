using InternalChat.Application.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace InternalChat.Infrastructure.Persistence;

/// <summary>
/// Calls the partition-management functions the initial migration installed (T026, T089).
/// </summary>
/// <remarks>
/// The SQL functions are the implementation, not this class. They live in a migration because
/// creating a partition is DDL that has to be available to the seeder, the retention sweep, and this
/// job alike — three callers that must not each have their own idea of how a partition is named.
/// </remarks>
public sealed class PartitionMaintenance : IPartitionMaintenance
{
    /// <summary>The partitioned table this maintains.</summary>
    private const string PartitionedTable = "message";

    private readonly ChatDbContext _context;

    /// <summary>Creates the maintenance helper.</summary>
    public PartitionMaintenance(ChatDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
    }

    /// <inheritdoc />
    public async Task<int> EnsureMonthPartitionsAsync(
        int monthsAhead,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(monthsAhead);

        // Starts at the current month rather than at a fixed date: the function is idempotent, so
        // re-covering this month costs a catalog lookup and guarantees the current month exists even
        // if the job has never run before.
        List<int> created = await _context.Database
            .SqlQuery<int>(
                $"""
                SELECT internalchat_ensure_month_partitions(
                    {PartitionedTable},
                    date_trunc('month', CURRENT_DATE)::date,
                    {monthsAhead}) AS "Value"
                """)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // The function returns how many months it covered, not how many it newly created — it
        // increments its counter per month either way. Reporting it as "created" would be a lie in
        // the steady state, where the answer is always the full window and nothing was created, so
        // the job logs the runway instead.
        return created.Count == 1 ? created[0] : 0;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Counted from the catalog rather than tracked, because the catalog is the only thing that
    /// actually decides whether an insert succeeds. A counter maintained by this job would agree with
    /// itself after somebody dropped a partition by hand.
    /// </remarks>
    public async Task<int> CountMonthsAheadAsync(CancellationToken cancellationToken = default)
    {
        List<int> months = await _context.Database
            .SqlQuery<int>(
                $"""
                SELECT count(*)::int AS "Value"
                FROM pg_inherits i
                JOIN pg_class child ON child.oid = i.inhrelid
                WHERE i.inhparent = {PartitionedTable}::regclass
                  AND child.relname > format('%s_%s', {PartitionedTable}, to_char(CURRENT_DATE, 'YYYY_MM'))
                """)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return months.Count == 1 ? months[0] : 0;
    }
}
