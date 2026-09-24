using System.Globalization;
using InternalChat.Application.Abstractions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace InternalChat.Infrastructure.Persistence;

/// <summary>
/// PostgreSQL implementation of the retention sweep (T206, FR-052, research.md D11).
/// </summary>
/// <remarks>
/// Raw SQL throughout: dropping a partition is DDL EF cannot express, and finding which partitions
/// exist means reading <c>pg_inherits</c>. Every value is a parameter — the only inputs are a date
/// and a batch size, and both are bound rather than formatted.
/// </remarks>
public sealed class RetentionSweep : IRetentionSweep
{
    private readonly ChatDbContext _context;
    private readonly IObjectStore _objects;

    /// <summary>Creates the sweep.</summary>
    public RetentionSweep(ChatDbContext context, IObjectStore objects)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(objects);

        _context = context;
        _objects = objects;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> DropExpiredMessagePartitionsAsync(
        DateOnly cutoff,
        CancellationToken cancellationToken = default)
    {
        NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        List<string> candidates = [];

        // pg_get_expr renders the partition bound as "FOR VALUES FROM ('..') TO ('..')". The upper
        // bound is what matters: a partition whose TO is at or before the cutoff holds nothing
        // inside the retention window and can go whole.
        const string FindSql =
            """
            SELECT child.relname,
                   pg_get_expr(child.relpartbound, child.oid) AS bound
            FROM pg_inherits
            JOIN pg_class parent ON parent.oid = pg_inherits.inhparent
            JOIN pg_class child ON child.oid = pg_inherits.inhrelid
            WHERE parent.relname = 'message'
            ORDER BY child.relname
            """;

        await using (NpgsqlCommand find = new(FindSql, connection))
        await using (NpgsqlDataReader reader = await find.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                string name = reader.GetString(0);
                string bound = reader.GetString(1);

                if (UpperBoundOf(bound) is { } upper && upper <= cutoff)
                {
                    candidates.Add(name);
                }
            }
        }

        List<string> dropped = [];

        foreach (string partition in candidates)
        {
            // Identifier, not a value, so it cannot be a parameter. Safe because the name came from
            // pg_class moments ago rather than from any input — and quoted regardless, so a
            // partition named by some future convention cannot become an injection.
            string quoted = "\"" + partition.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

            await using NpgsqlCommand drop = new($"DROP TABLE IF EXISTS {quoted}", connection);
            await drop.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            dropped.Add(partition);
        }

        return dropped;
    }

    /// <inheritdoc />
    public async Task<long> CountMessagesBeforeAsync(
        DateOnly cutoff,
        CancellationToken cancellationToken = default)
    {
        NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        await using NpgsqlCommand command = new(
            "SELECT count(*) FROM message WHERE sent_at < @cutoff", connection);

        command.Parameters.AddWithValue("cutoff", cutoff.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));

        object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return result is long count ? count : 0;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <b>Object first, row second.</b> The reverse order loses the key that identifies the object
    /// if the delete fails between the two, leaving an orphan nothing can find — which is what
    /// <c>OrphanReclaimJob</c> (T210) exists to clean up, and this order keeps that job's work rare
    /// rather than routine.
    /// </remarks>
    public async Task<int> DeleteExpiredAttachmentBatchAsync(
        DateOnly cutoff,
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);

        DateTimeOffset before = new(cutoff.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        List<Domain.Attachments.Attachment> expired = await _context.Attachments
            .Where(a => a.CreatedAt < before)
            .OrderBy(a => a.CreatedAt)
            .Take(batchSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (expired.Count == 0)
        {
            return 0;
        }

        foreach (Domain.Attachments.Attachment attachment in expired)
        {
            // Both buckets, plus the poster. DeleteAsync already covers both buckets; the poster
            // key is derived rather than read, matching how the scan consumer wrote it.
            await _objects.DeleteAsync(attachment.ObjectKey, cancellationToken).ConfigureAwait(false);
            await _objects.DeleteAsync($"{attachment.ObjectKey}.poster.jpg", cancellationToken)
                .ConfigureAwait(false);
        }

        _context.Attachments.RemoveRange(expired);
        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return expired.Count;
    }

    /// <summary>
    /// Parses the upper bound out of a range partition's definition.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>MAXVALUE</c> yields <c>null</c>, which keeps the default partition — if one is ever added
    /// — from being read as expiring at the beginning of time and dropped on the first sweep.
    /// </para>
    /// <para>
    /// Public because it is the single decision the whole sweep turns on and it is a pure function
    /// of a string. A misparse either spares everything forever or deletes a month early, and both
    /// look like the sweep working — so it is worth asserting directly rather than only through its
    /// effect on a database.
    /// </para>
    /// </remarks>
    public static DateOnly? UpperBoundOf(string partitionBound)
    {
        ArgumentNullException.ThrowIfNull(partitionBound);

        int to = partitionBound.IndexOf("TO (", StringComparison.OrdinalIgnoreCase);

        if (to < 0)
        {
            return null;
        }

        int open = partitionBound.IndexOf('\'', to);
        int close = open < 0 ? -1 : partitionBound.IndexOf('\'', open + 1);

        if (open < 0 || close < 0)
        {
            return null;
        }

        string value = partitionBound[(open + 1)..close];

        // The bound renders as a timestamp; only the date part decides whether the whole range is
        // outside the window.
        return DateTime.TryParse(
            value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out DateTime parsed)
            ? DateOnly.FromDateTime(parsed)
            : null;
    }

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        // The context's own connection. Database.GetConnectionString() returns it with the password
        // redacted, so a connection opened from that string fails authentication at runtime while
        // looking correct — the defect found and fixed in T164.
        NpgsqlConnection connection = (NpgsqlConnection)_context.Database.GetDbConnection();

        if (connection.State is not System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        return connection;
    }
}
