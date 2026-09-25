using System.Globalization;
using Npgsql;
using NpgsqlTypes;

namespace InternalChat.Seeder;

/// <summary>
/// Seeds a small, coherent development dataset: 20 employees, direct conversations, two groups,
/// and enough message history to exercise pagination.
/// </summary>
/// <remarks>
/// <para>
/// The constitution requires a new developer to reach a running platform in three steps. A
/// platform you can sign into but which shows an empty conversation list is not that — the first
/// thing anyone does is create test data by hand, which is the fourth step, and everyone's differs.
/// </para>
/// <para>
/// The roster here is the same one T061's Keycloak realm export must contain. The join between the
/// two is <c>external_subject</c> = the Keycloak <c>sub</c> claim, so
/// <see cref="Roster"/> is the single definition both sides copy from. If they drift, sign-in
/// succeeds and the employee has no conversations, which is a confusing way to discover a typo.
/// </para>
/// </remarks>
internal static class DevelopmentSeeder
{
    /// <summary>Tables this seeder writes to.</summary>
    private static readonly string[] RequiredTables = ["employee", "conversation", "membership", "message"];

    /// <summary>
    /// The development roster. Usernames are positional and stable so the Keycloak realm export
    /// can be generated from the same list.
    /// </summary>
    internal static readonly (string Username, string DisplayName)[] Roster =
    [
        ("an.nguyen", "Nguyễn Thị Vân An"),
        ("binh.tran", "Trần Quốc Bình"),
        ("chi.le", "Lê Bảo Chi"),
        ("dung.pham", "Phạm Tiến Dũng"),
        ("giang.hoang", "Hoàng Thu Giang"),
        ("hai.vo", "Võ Minh Hải"),
        ("khanh.dang", "Đặng Gia Khánh"),
        ("lan.bui", "Bùi Ngọc Lan"),
        ("minh.do", "Đỗ Nhật Minh"),
        ("nga.ngo", "Ngô Thúy Nga"),
        ("phuc.duong", "Dương Hồng Phúc"),
        ("quyen.ly", "Lý Thanh Quyên"),
        ("son.truong", "Trương Hoài Sơn"),
        ("thao.dinh", "Đinh Phương Thảo"),
        ("tuan.mai", "Mai Anh Tuấn"),
        ("uyen.cao", "Cao Diệu Uyên"),
        ("viet.ha", "Hà Đức Việt"),
        ("xuan.luong", "Lương Thị Xuân"),
        ("yen.trinh", "Trịnh Hải Yến"),
        ("duc.nguyen", "Nguyễn Trung Đức"),
    ];

    /// <summary>Group conversations created for development.</summary>
    private static readonly (string Key, string Name, int[] MemberIndexes)[] Groups =
    [
        ("engineering", "Engineering", [0, 1, 2, 3, 4, 5, 8, 12, 14, 19]),
        ("product-launch", "Product Launch", [0, 4, 7, 9, 11, 13, 15]),
    ];

    /// <summary>Direct conversations, as pairs of roster indexes.</summary>
    private static readonly (int First, int Second)[] DirectPairs =
    [
        (0, 1), (0, 4), (1, 2), (2, 3), (3, 5), (4, 7), (5, 8), (6, 9), (7, 10), (8, 11),
    ];

    /// <summary>Seeds the development dataset. Safe to run repeatedly.</summary>
    public static async Task RunAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        await using NpgsqlDataSource dataSource = NpgsqlDataSource.Create(connectionString);

        await SchemaPreflight.EnsureAsync(dataSource, RequiredTables, cancellationToken).ConfigureAwait(false);

        Console.WriteLine("Seeding development data…");

        await using NpgsqlConnection connection =
            await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        // Seeded history is backdated so the message list has more than one day in it, and
        // `message` is partitioned by month (research.md D11). Without this the very first insert
        // fails with "no partition of relation message found" — on a fresh database the partition
        // for two months ago has never been created, because the maintenance job in T089 only
        // works forward.
        await EnsurePartitionsAsync(connection, cancellationToken).ConfigureAwait(false);

        // One transaction for the whole dataset. A half-seeded database — employees but no
        // conversations — looks like a working platform with a bug in it, which is worse than an
        // obvious failure.
        await using NpgsqlTransaction transaction =
            await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        Guid[] employeeIds = await SeedEmployeesAsync(connection, cancellationToken).ConfigureAwait(false);
        Console.WriteLine($"  employees      {employeeIds.Length}");

        int conversations = 0;
        int messages = 0;

        foreach ((int first, int second) in DirectPairs)
        {
            Guid id = await SeedDirectConversationAsync(
                connection, employeeIds[first], employeeIds[second], cancellationToken).ConfigureAwait(false);

            conversations++;
            messages += await SeedMessagesAsync(
                connection, id, [employeeIds[first], employeeIds[second]], count: 12, cancellationToken)
                .ConfigureAwait(false);
        }

        foreach ((string key, string name, int[] memberIndexes) in Groups)
        {
            Guid[] members = [.. memberIndexes.Select(i => employeeIds[i])];

            Guid id = await SeedGroupConversationAsync(connection, key, name, members, cancellationToken)
                .ConfigureAwait(false);

            conversations++;

            // Deliberately more than one page (research.md D10 sets the history page at 50) so the
            // keyset pagination in T093 is exercised by simply opening the group.
            messages += await SeedMessagesAsync(connection, id, members, count: 140, cancellationToken)
                .ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        Console.WriteLine($"  conversations  {conversations}");
        Console.WriteLine($"  messages       {messages}");
        Console.WriteLine("Done. Sign in as any roster username; the development password is set by the Keycloak realm.");
    }

    /// <summary>
    /// Creates the monthly partitions the seeded history lands in, plus one ahead.
    /// </summary>
    private static async Task EnsurePartitionsAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "SELECT internalchat_ensure_month_partitions('message', $1, $2)";
        command.Parameters.AddWithValue(NpgsqlDbType.Date, DateTime.UtcNow.Date.AddMonths(-3));

        // Three months back plus one ahead. The month ahead matters more than it looks: seeding on
        // the 31st and sending a message an hour later would otherwise cross into a month with no
        // partition and fail, which reads as a broken platform rather than a missing partition.
        command.Parameters.AddWithValue(4);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Inserts the roster and returns each employee's id in roster order.
    /// </summary>
    /// <remarks>
    /// Reads the ids back rather than trusting the derived ones. Directory sync (T065) may have
    /// created the same employee first from Keycloak with an id of its own, and in that case the
    /// seeder must attach conversations to the row that already exists rather than silently do
    /// nothing and leave the platform empty.
    /// </remarks>
    private static async Task<Guid[]> SeedEmployeesAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        Guid[] ids = new Guid[Roster.Length];

        for (int i = 0; i < Roster.Length; i++)
        {
            (string username, string displayName) = Roster[i];
            string subject = $"dev-{username}";

            await using NpgsqlCommand command = connection.CreateCommand();
            command.CommandText =
                """
                WITH inserted AS (
                    INSERT INTO employee (id, external_subject, display_name, email, status, created_at, updated_at)
                    VALUES ($1, $2, $3, $4, 'active', timezone('utc', now()), timezone('utc', now()))
                    ON CONFLICT (external_subject) DO NOTHING
                    RETURNING id
                )
                SELECT id FROM inserted
                UNION ALL
                SELECT id FROM employee WHERE external_subject = $2
                LIMIT 1
                """;

            command.Parameters.AddWithValue(SeedIdentity.Id("employee", subject));
            command.Parameters.AddWithValue(subject);
            command.Parameters.AddWithValue(displayName);
            command.Parameters.AddWithValue($"{username}@internalchat.local");

            ids[i] = (Guid)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }

        return ids;
    }

    private static async Task<Guid> SeedDirectConversationAsync(
        NpgsqlConnection connection,
        Guid first,
        Guid second,
        CancellationToken cancellationToken)
    {
        string directKey = SeedIdentity.DirectKey(first, second);
        Guid id = SeedIdentity.Id("conversation", directKey);

        await using (NpgsqlCommand command = connection.CreateCommand())
        {
            // Conflict on direct_key, not on id: the pair is what must be unique, and the
            // application may well have created this conversation already with its own id.
            command.CommandText =
                """
                WITH inserted AS (
                    INSERT INTO conversation (id, kind, name, created_by, history_visibility, last_seq, direct_key, created_at, updated_at)
                    VALUES ($1, 'direct', NULL, $2, 'full', 0, $3, timezone('utc', now()), timezone('utc', now()))
                    ON CONFLICT (direct_key) WHERE kind = 'direct' DO NOTHING
                    RETURNING id
                )
                SELECT id FROM inserted
                UNION ALL
                SELECT id FROM conversation WHERE direct_key = $3
                LIMIT 1
                """;

            command.Parameters.AddWithValue(id);
            command.Parameters.AddWithValue(first);
            command.Parameters.AddWithValue(directKey);

            id = (Guid)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }

        await AddMembersAsync(connection, id, [first, second], adminIndex: -1, cancellationToken)
            .ConfigureAwait(false);

        return id;
    }

    private static async Task<Guid> SeedGroupConversationAsync(
        NpgsqlConnection connection,
        string key,
        string name,
        Guid[] members,
        CancellationToken cancellationToken)
    {
        Guid id = SeedIdentity.Id("conversation", $"group:{key}");

        await using (NpgsqlCommand command = connection.CreateCommand())
        {
            command.CommandText =
                """
                INSERT INTO conversation (id, kind, name, created_by, history_visibility, last_seq, direct_key, created_at, updated_at)
                VALUES ($1, 'group', $2, $3, 'from_join', 0, NULL, timezone('utc', now()), timezone('utc', now()))
                ON CONFLICT (id) DO NOTHING
                """;

            command.Parameters.AddWithValue(id);
            command.Parameters.AddWithValue(name);
            command.Parameters.AddWithValue(members[0]);

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await AddMembersAsync(connection, id, members, adminIndex: 0, cancellationToken).ConfigureAwait(false);

        return id;
    }

    /// <summary>Adds members with a history floor of zero — they see everything seeded.</summary>
    private static async Task AddMembersAsync(
        NpgsqlConnection connection,
        Guid conversationId,
        Guid[] members,
        int adminIndex,
        CancellationToken cancellationToken)
    {
        for (int i = 0; i < members.Length; i++)
        {
            await using NpgsqlCommand command = connection.CreateCommand();
            command.CommandText =
                $"""
                INSERT INTO membership (conversation_id, employee_id, role, joined_at, visible_from_seq)
                VALUES ($1, $2, '{(i == adminIndex ? "admin" : "member")}', $3, 0)
                ON CONFLICT (conversation_id, employee_id) DO NOTHING
                """;

            command.Parameters.AddWithValue(conversationId);
            command.Parameters.AddWithValue(members[i]);
            command.Parameters.AddWithValue(DateTimeOffset.UtcNow.AddDays(-90));

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Writes <paramref name="count"/> messages and advances the conversation's sequence.
    /// </summary>
    /// <remarks>
    /// The sequence continues from <c>conversation.last_seq</c> rather than restarting at 1, so
    /// running the seeder twice appends rather than colliding — and the second run's rows still
    /// satisfy the unique index on <c>(conversation_id, seq)</c> that T088 adds.
    /// </remarks>
    private static async Task<int> SeedMessagesAsync(
        NpgsqlConnection connection,
        Guid conversationId,
        Guid[] authors,
        int count,
        CancellationToken cancellationToken)
    {
        long startSeq;

        await using (NpgsqlCommand read = connection.CreateCommand())
        {
            read.CommandText = "SELECT last_seq FROM conversation WHERE id = $1";
            read.Parameters.AddWithValue(conversationId);
            startSeq = (long)(await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }

        if (startSeq > 0)
        {
            // Already seeded. Adding another 140 messages on every `docker compose up` would grow
            // the development database without bound.
            return 0;
        }

        DateTimeOffset cursor = DateTimeOffset.UtcNow.AddDays(-60);

        for (int i = 1; i <= count; i++)
        {
            long seq = startSeq + i;
            Guid author = authors[i % authors.Length];

            // Spread over real time so the message list has date separators and the ordering is
            // visibly by sequence rather than by insertion.
            cursor = cursor.AddMinutes(((i * 37) % 240) + 5);

            await using NpgsqlCommand command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO message (id, conversation_id, seq, author_id, client_message_key, body, sent_at)
                VALUES ($1, $2, $3, $4, $5, $6, $7)
                ON CONFLICT DO NOTHING
                """;

            command.Parameters.AddWithValue(SeedIdentity.Id("message", $"{conversationId}:{seq}"));
            command.Parameters.AddWithValue(conversationId);
            command.Parameters.AddWithValue(seq);
            command.Parameters.AddWithValue(author);
            command.Parameters.AddWithValue(SeedIdentity.ClientMessageKey(conversationId, seq));
            command.Parameters.AddWithValue(Corpus.Sentence(conversationId.GetHashCode() + i));
            command.Parameters.AddWithValue(cursor);

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (NpgsqlCommand advance = connection.CreateCommand())
        {
            advance.CommandText = "UPDATE conversation SET last_seq = $2 WHERE id = $1";
            advance.Parameters.AddWithValue(conversationId);
            advance.Parameters.AddWithValue(startSeq + count);
            await advance.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        Console.Write(string.Create(
            CultureInfo.InvariantCulture,
            $"\r  seeding messages… {count} in {conversationId.ToString()[..8]}   "));

        return count;
    }
}
