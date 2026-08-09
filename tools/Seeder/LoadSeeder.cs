using System.Diagnostics;
using System.Globalization;
using Npgsql;
using NpgsqlTypes;

namespace InternalChat.Seeder;

/// <summary>
/// Seeds bulk message volume for performance verification.
/// </summary>
/// <remarks>
/// <para>
/// This exists because of one sentence in research.md D9: PostgreSQL full-text search is accepted
/// for v1 <em>on condition</em> that the search budget is measured against a full-retention corpus
/// of roughly 125 million messages, with OpenSearch as the fallback if it does not hold. A budget
/// measured on an empty database is not a measurement, and the decision not to run a second search
/// engine rests entirely on this corpus existing. T108, T169, and T218 all consume it.
/// </para>
/// <para>
/// Rows go in through binary <c>COPY</c> rather than <c>INSERT</c>. At 125 million rows the
/// difference is not a percentage — round-tripping each row would take days.
/// </para>
/// <para>
/// <b>Two things it deliberately does not do.</b> It does not disable the full-text index during
/// the load: the generated <c>body_tsv</c> column and its GIN index are the cost being measured,
/// and loading without them would produce a corpus whose write cost is unrepresentative. And it
/// does not truncate first — it appends, so a corpus can be grown in stages rather than rebuilt
/// from scratch each time.
/// </para>
/// </remarks>
internal static class LoadSeeder
{
    /// <summary>Tables this seeder writes to.</summary>
    private static readonly string[] RequiredTables = ["employee", "conversation", "membership", "message"];

    /// <summary>Employees in the load pool — the platform's stated scale (spec.md, SC-001).</summary>
    private const int EmployeeCount = 10_000;

    /// <summary>Members per load conversation.</summary>
    private const int MembersPerConversation = 20;

    /// <summary>
    /// Target messages per conversation. Sized so 125 million rows spread across a plausible
    /// number of conversations rather than a handful of impossibly large ones — search cost
    /// depends on how selective the membership pre-filter is, so the shape matters as much as the
    /// row count.
    /// </summary>
    private const long MessagesPerConversation = 25_000;

    /// <summary>Rows per COPY operation.</summary>
    private const int BatchSize = 50_000;

    /// <summary>Retention window the corpus spans, matching FR-052.</summary>
    private const int RetentionMonths = 12;

    /// <summary>Seeds <paramref name="messageCount"/> messages.</summary>
    public static async Task RunAsync(
        string connectionString,
        long messageCount,
        CancellationToken cancellationToken = default)
    {
        // Multiplexing off: this is one connection doing sustained COPY, and multiplexing adds
        // batching machinery that helps many small queries and does nothing for a bulk load.
        NpgsqlDataSourceBuilder builder = new(connectionString);
        await using NpgsqlDataSource dataSource = builder.Build();

        await SchemaPreflight.EnsureAsync(dataSource, RequiredTables, cancellationToken).ConfigureAwait(false);

        int conversationCount = (int)Math.Clamp(
            (messageCount + MessagesPerConversation - 1) / MessagesPerConversation, 10, 50_000);

        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"""
            Load corpus
              messages       {messageCount:N0}
              conversations  {conversationCount:N0}
              employees      {EmployeeCount:N0}
              spread over    {RetentionMonths} months
            """));

        await using NpgsqlConnection connection =
            await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        DateTimeOffset windowStart = DateTimeOffset.UtcNow.AddMonths(-RetentionMonths);

        await EnsurePartitionsAsync(connection, windowStart, cancellationToken).ConfigureAwait(false);
        await EnsureEmployeesAsync(connection, cancellationToken).ConfigureAwait(false);
        await EnsureConversationsAsync(connection, conversationCount, cancellationToken).ConfigureAwait(false);
        await EnsureMembershipsAsync(connection, conversationCount, cancellationToken).ConfigureAwait(false);

        long perConversation = (messageCount + conversationCount - 1) / conversationCount;
        TimeSpan window = DateTimeOffset.UtcNow - windowStart;

        Stopwatch stopwatch = Stopwatch.StartNew();
        long written = 0;

        for (int c = 1; c <= conversationCount && written < messageCount; c++)
        {
            long startSeq = await CurrentSeqAsync(connection, c, cancellationToken).ConfigureAwait(false);
            long remaining = Math.Min(perConversation, messageCount - written);

            written += await CopyConversationAsync(
                connection, c, startSeq, remaining, windowStart, window, cancellationToken).ConfigureAwait(false);

            await AdvanceSeqAsync(connection, c, startSeq + remaining, cancellationToken).ConfigureAwait(false);

            Report(written, messageCount, stopwatch);
        }

        stopwatch.Stop();

        Console.WriteLine();
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Wrote {written:N0} messages in {stopwatch.Elapsed:hh\\:mm\\:ss} " +
            $"({written / Math.Max(1, stopwatch.Elapsed.TotalSeconds):N0} rows/s)."));
        Console.WriteLine("Run ANALYZE before measuring — the planner will otherwise use stale statistics.");
    }

    /// <summary>
    /// Creates every monthly partition the corpus will land in, before a single row is written.
    /// </summary>
    /// <remarks>
    /// An INSERT into a range-partitioned table with no matching partition fails outright. Calling
    /// the T026 helper here means a 12-month spread does not fail on its first row from eleven
    /// months ago — and it exercises the same function the maintenance job in T089 calls.
    /// </remarks>
    private static async Task EnsurePartitionsAsync(
        NpgsqlConnection connection,
        DateTimeOffset windowStart,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "SELECT internalchat_ensure_month_partitions('message', $1, $2)";
        command.Parameters.AddWithValue(NpgsqlDbType.Date, windowStart.UtcDateTime.Date);
        command.Parameters.AddWithValue(RetentionMonths + 1);

        object? created = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        Console.WriteLine($"  partitions     {created} ensured");
    }

    private static async Task EnsureEmployeesAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO employee (id, external_subject, display_name, email, status)
            SELECT
                ('00000000-0000-8000-8000-' || lpad(to_hex(i), 12, '0'))::uuid,
                'load-' || i,
                'Load Employee ' || i,
                'load' || i || '@internalchat.local',
                'active'
            FROM generate_series(1, $1) AS i
            ON CONFLICT (external_subject) DO NOTHING
            """;
        command.Parameters.AddWithValue(EmployeeCount);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureConversationsAsync(
        NpgsqlConnection connection,
        int conversationCount,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO conversation (id, kind, name, created_by, history_visibility, last_seq, direct_key)
            SELECT
                ('00000000-0000-8000-8001-' || lpad(to_hex(c), 12, '0'))::uuid,
                'group',
                'Load Conversation ' || c,
                ('00000000-0000-8000-8000-' || lpad(to_hex(1 + (c % $2)), 12, '0'))::uuid,
                'from_join',
                0,
                NULL
            FROM generate_series(1, $1) AS c
            ON CONFLICT (id) DO NOTHING
            """;
        command.Parameters.AddWithValue(conversationCount);
        command.Parameters.AddWithValue(EmployeeCount);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Spreads members across conversations so search scoping has something to filter on.
    /// </summary>
    /// <remarks>
    /// The stride of 17 is what stops every conversation drawing the same twenty employees. If it
    /// did, one employee would be a member of everything and the membership pre-filter in T164
    /// would exclude nothing — search would measure as fast because it was never actually scoped.
    /// </remarks>
    private static async Task EnsureMembershipsAsync(
        NpgsqlConnection connection,
        int conversationCount,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO membership (conversation_id, employee_id, role, joined_at, visible_from_seq)
            SELECT
                ('00000000-0000-8000-8001-' || lpad(to_hex(c), 12, '0'))::uuid,
                ('00000000-0000-8000-8000-' || lpad(to_hex(1 + ((c * 17 + m) % $2)), 12, '0'))::uuid,
                'member',
                now() - interval '13 months',
                0
            FROM generate_series(1, $1) AS c
            CROSS JOIN generate_series(0, $3 - 1) AS m
            ON CONFLICT (conversation_id, employee_id) DO NOTHING
            """;
        command.Parameters.AddWithValue(conversationCount);
        command.Parameters.AddWithValue(EmployeeCount);
        command.Parameters.AddWithValue(MembersPerConversation);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Streams one conversation's messages in binary COPY batches.</summary>
    private static async Task<long> CopyConversationAsync(
        NpgsqlConnection connection,
        int conversationIndex,
        long startSeq,
        long count,
        DateTimeOffset windowStart,
        TimeSpan window,
        CancellationToken cancellationToken)
    {
        Guid conversationId = LoadId(family: 1, conversationIndex);
        long written = 0;

        while (written < count)
        {
            int batch = (int)Math.Min(BatchSize, count - written);

            await using NpgsqlBinaryImporter writer = await connection.BeginBinaryImportAsync(
                """
                COPY message (id, conversation_id, seq, author_id, client_message_key, body, sent_at)
                FROM STDIN (FORMAT BINARY)
                """,
                cancellationToken).ConfigureAwait(false);

            for (int i = 0; i < batch; i++)
            {
                long seq = startSeq + written + i + 1;

                // Timestamps ascend with the sequence within a conversation. Ordering is defined by
                // seq (FR-012) and never by the clock, but a corpus where the two disagree would
                // make a genuine ordering bug invisible in a load test.
                DateTimeOffset sentAt = windowStart + (window * ((double)(written + i) / Math.Max(1, count)));

                int authorIndex = 1 + (((conversationIndex * 17) + (int)(seq % MembersPerConversation)) % EmployeeCount);

                await writer.StartRowAsync(cancellationToken).ConfigureAwait(false);
                await writer.WriteAsync(MessageId(conversationIndex, seq), NpgsqlDbType.Uuid, cancellationToken).ConfigureAwait(false);
                await writer.WriteAsync(conversationId, NpgsqlDbType.Uuid, cancellationToken).ConfigureAwait(false);
                await writer.WriteAsync(seq, NpgsqlDbType.Bigint, cancellationToken).ConfigureAwait(false);
                await writer.WriteAsync(LoadId(family: 0, authorIndex), NpgsqlDbType.Uuid, cancellationToken).ConfigureAwait(false);
                await writer.WriteAsync(ClientKey(conversationIndex, seq), NpgsqlDbType.Text, cancellationToken).ConfigureAwait(false);
                await writer.WriteAsync(Corpus.Sentence((conversationIndex * 1_000_003) + (int)seq), NpgsqlDbType.Text, cancellationToken).ConfigureAwait(false);
                await writer.WriteAsync(sentAt, NpgsqlDbType.TimestampTz, cancellationToken).ConfigureAwait(false);
            }

            await writer.CompleteAsync(cancellationToken).ConfigureAwait(false);
            written += batch;
        }

        return written;
    }

    private static async Task<long> CurrentSeqAsync(
        NpgsqlConnection connection,
        int conversationIndex,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "SELECT last_seq FROM conversation WHERE id = $1";
        command.Parameters.AddWithValue(LoadId(family: 1, conversationIndex));

        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    private static async Task AdvanceSeqAsync(
        NpgsqlConnection connection,
        int conversationIndex,
        long lastSeq,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "UPDATE conversation SET last_seq = $2 WHERE id = $1";
        command.Parameters.AddWithValue(LoadId(family: 1, conversationIndex));
        command.Parameters.AddWithValue(lastSeq);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the same ids the SQL above builds, so C# and PostgreSQL agree without a round trip.
    /// </summary>
    /// <remarks>
    /// Family 0 is employees, family 1 conversations — matching the <c>8000</c> and <c>8001</c>
    /// groups in the generate_series expressions. Keep the two in step; a mismatch produces a
    /// foreign key violation on the first COPY, which is at least loud.
    /// </remarks>
    private static Guid LoadId(int family, long index) =>
        Guid.Parse(
            string.Create(
                CultureInfo.InvariantCulture,
                $"00000000-0000-8000-800{family:x}-{index:x12}"));

    /// <summary>Message id derived from its conversation and sequence — unique, and cheap.</summary>
    /// <remarks>
    /// A hash would also work but costs a SHA-256 per row, which at 125 million rows is minutes of
    /// pure overhead. Composition is exact rather than probabilistic and costs nothing.
    /// </remarks>
    private static Guid MessageId(int conversationIndex, long seq)
    {
        Span<byte> bytes = stackalloc byte[16];
        BitConverter.TryWriteBytes(bytes[..4], conversationIndex);
        BitConverter.TryWriteBytes(bytes[4..12], seq);
        BitConverter.TryWriteBytes(bytes[12..], 0x8002);
        return new Guid(bytes);
    }

    /// <summary>
    /// Stand-in for the sender's ULID. Unique per conversation, which is the constraint that
    /// matters; the real value arrives from the browser and is never generated server-side.
    /// </summary>
    private static string ClientKey(int conversationIndex, long seq) =>
        string.Create(CultureInfo.InvariantCulture, $"LOAD{conversationIndex:D8}{seq:D14}");

    private static void Report(long written, long target, Stopwatch stopwatch)
    {
        double rate = written / Math.Max(1, stopwatch.Elapsed.TotalSeconds);
        TimeSpan remaining = rate > 0 ? TimeSpan.FromSeconds((target - written) / rate) : TimeSpan.Zero;

        Console.Write(string.Create(
            CultureInfo.InvariantCulture,
            $"\r  {written:N0} / {target:N0}  ({rate:N0} rows/s, ~{remaining:hh\\:mm\\:ss} left)   "));
    }
}
