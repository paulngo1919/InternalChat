using InternalChat.Application.Abstractions;
using InternalChat.Domain.Common;
using InternalChat.Domain.Messages;
using InternalChat.Infrastructure.Persistence;
using InternalChat.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace InternalChat.IntegrationTests.Retention;

/// <summary>
/// T211 — the retention sweep expires content within its stated tolerance (FR-052).
/// </summary>
/// <remarks>
/// <para>
/// <b>The tolerance is the point of this file, and it is a consequence of the design rather than
/// slack.</b> Messages are expired by dropping whole monthly partitions, because at 125 million
/// rows a <c>DELETE</c> would rewrite tens of millions of rows and hold locks for hours. A partition
/// is only droppable when its <em>entire</em> range is outside the window — so content survives up
/// to a month past its nominal twelve, and dropping a straddling partition would delete messages
/// that are still inside the window, which is data loss rather than retention.
/// </para>
/// <para>
/// These tests assert both directions: that expired partitions really do go, and that a partition
/// containing anything still in the window really does not. The second is the one that matters —
/// the first failing is a compliance gap, the second failing is deleting people's messages early.
/// </para>
/// <para>
/// Against real PostgreSQL necessarily: partition bounds, <c>pg_inherits</c>, and <c>DROP TABLE</c>
/// on a partition have no in-memory equivalent, and the parsing of a rendered partition bound is
/// precisely what could be wrong.
/// </para>
/// </remarks>
public sealed class RetentionToleranceTests : MessagingTestBase
{
    public RetentionToleranceTests(StackFixture stack)
        : base(stack)
    {
    }

    [Theory]
    [InlineData("FOR VALUES FROM ('2026-01-01 00:00:00+00') TO ('2026-02-01 00:00:00+00')", "2026-02-01")]
    [InlineData("FOR VALUES FROM ('2025-12-01 00:00:00+00') TO ('2026-01-01 00:00:00+00')", "2026-01-01")]
    public void The_upper_bound_of_a_partition_is_read_correctly(string bound, string expected)
    {
        // The whole drop decision turns on this string. Parsed rather than assumed, and tested
        // separately because a misparse would either spare everything forever or delete a month
        // early — and both look like the sweep working.
        DateOnly? parsed = RetentionSweep.UpperBoundOf(bound);

        Assert.Equal(DateOnly.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), parsed);
    }

    [Fact]
    public void A_partition_with_no_upper_bound_is_never_dropped()
    {
        // A DEFAULT partition, if one is ever added. Reading it as expiring at the beginning of
        // time would drop it on the first sweep, taking every row that failed to route anywhere
        // else with it.
        Assert.Null(RetentionSweep.UpperBoundOf("DEFAULT"));
        Assert.Null(RetentionSweep.UpperBoundOf("FOR VALUES FROM ('2026-01-01') TO (MAXVALUE)"));
    }

    [Fact]
    public async Task A_partition_entirely_inside_the_window_is_kept()
    {
        await using ChatDbContext context = CreateDbContext();

        IReadOnlyList<string> before = await PartitionNamesAsync(context);

        // A cutoff before every partition this database has. Nothing is entirely older than it.
        IReadOnlyList<string> dropped = await SweepAsync(
            context, new DateOnly(2000, 1, 1));

        Assert.Empty(dropped);
        Assert.Equal(before, await PartitionNamesAsync(context));
    }

    [Fact]
    public async Task A_partition_straddling_the_cutoff_is_kept_which_is_the_tolerance()
    {
        await using ChatDbContext context = CreateDbContext();

        using IServiceScope scope = Api.Services.CreateScope();

        IPartitionMaintenance maintenance = scope.ServiceProvider
            .GetRequiredService<IPartitionMaintenance>();

        await maintenance.EnsureMonthPartitionsAsync(2);

        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);

        // Mid-month: the current partition covers days on both sides of this.
        DateOnly midMonth = new(today.Year, today.Month, 15);

        IReadOnlyList<string> dropped = await SweepAsync(context, midMonth);

        // THE assertion. Dropping the straddling partition would take messages sent after the
        // cutoff — still inside the retention window — along with the expired ones.
        string currentMonth = $"message_{today.Year:D4}_{today.Month:D2}";

        Assert.DoesNotContain(currentMonth, dropped);
    }

    [Fact]
    public async Task An_entirely_expired_partition_is_dropped_and_its_messages_go_with_it()
    {
        await using ChatDbContext context = CreateDbContext();

        // A partition well in the past, created explicitly so this test owns it.
        const string PartitionName = "message_2020_01";

        await context.Database.ExecuteSqlRawAsync(
            $"""
            CREATE TABLE IF NOT EXISTS {PartitionName}
            PARTITION OF message
            FOR VALUES FROM ('2020-01-01 00:00:00+00') TO ('2020-02-01 00:00:00+00')
            """);

        Guid conversationId = Guid.CreateVersion7();
        Guid author = await TestData.SeedEmployeeAsync(context, "an.nguyen");
        await TestData.SeedMembershipAsync(context, conversationId, author);

        Message old = Message.Send(
            Guid.CreateVersion7(),
            conversationId,
            seq: 1,
            author,
            ClientMessageKey.Parse("01JBXQ7ZPT4M9WYFN2VKC3H6RD"),
            MessageBody.Create("a message from 2020"),
            new FixedClock(DateTimeOffset.Parse("2020-01-15T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture)));

        old.ClearDomainEvents();
        context.Messages.Add(old);
        await context.SaveChangesAsync();

        Assert.Equal(1, await context.Messages.CountAsync(m => m.Id == old.Id));

        IReadOnlyList<string> dropped = await SweepAsync(context, new DateOnly(2021, 1, 1));

        Assert.Contains(PartitionName, dropped);

        // The rows are gone because the table holding them is gone — no DELETE ran, which is the
        // entire reason `message` is partitioned (research.md D11).
        Assert.Equal(0, await context.Messages.CountAsync(m => m.Id == old.Id));
    }

    [Fact]
    public async Task The_count_is_taken_before_the_drop_so_the_audit_record_can_state_it()
    {
        await using ChatDbContext context = CreateDbContext();

        await context.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS message_2019_06
            PARTITION OF message
            FOR VALUES FROM ('2019-06-01 00:00:00+00') TO ('2019-07-01 00:00:00+00')
            """);

        Guid conversationId = Guid.CreateVersion7();
        Guid author = await TestData.SeedEmployeeAsync(context, "an.nguyen");
        await TestData.SeedMembershipAsync(context, conversationId, author);

        for (int index = 0; index < 3; index++)
        {
            Message message = Message.Send(
                Guid.CreateVersion7(),
                conversationId,
                seq: index + 1,
                author,
                ClientMessageKey.Parse($"01JBXQ7ZPT4M9WYFN2VKC3H6R{index}"),
                MessageBody.Create($"old message {index}"),
                new FixedClock(DateTimeOffset.Parse("2019-06-15T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture)));

            message.ClearDomainEvents();
            context.Messages.Add(message);
        }

        await context.SaveChangesAsync();

        RetentionSweep sweep = CreateSweep(context);

        long counted = await sweep.CountMessagesBeforeAsync(new DateOnly(2020, 1, 1));

        // Counted, not estimated. An audit record saying "approximately three messages were
        // deleted" is not an audit record — and after the drop there is nothing left to count.
        Assert.Equal(3, counted);

        await sweep.DropExpiredMessagePartitionsAsync(new DateOnly(2020, 1, 1));

        Assert.Equal(0, await sweep.CountMessagesBeforeAsync(new DateOnly(2020, 1, 1)));
    }

    [Fact]
    public async Task The_sweep_is_idempotent()
    {
        await using ChatDbContext context = CreateDbContext();

        await context.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS message_2018_03
            PARTITION OF message
            FOR VALUES FROM ('2018-03-01 00:00:00+00') TO ('2018-04-01 00:00:00+00')
            """);

        IReadOnlyList<string> first = await SweepAsync(context, new DateOnly(2019, 1, 1));
        Assert.Contains("message_2018_03", first);

        // Runs daily. The second pass must find nothing rather than fail on a table that is
        // already gone — otherwise every run after the first would log an error.
        IReadOnlyList<string> second = await SweepAsync(context, new DateOnly(2019, 1, 1));
        Assert.DoesNotContain("message_2018_03", second);
    }

    /// <summary>
    /// Builds a sweep over the test's own context.
    /// </summary>
    /// <remarks>
    /// The context is the test's rather than the API scope's, deliberately: these tests assert what
    /// the database looks like afterwards, and reading through a second context would show a stale
    /// change-tracker view of a table that has just been dropped.
    /// </remarks>
    private RetentionSweep CreateSweep(ChatDbContext context)
    {
        using IServiceScope scope = Api.Services.CreateScope();

        return new RetentionSweep(context, scope.ServiceProvider.GetRequiredService<IObjectStore>());
    }

    private async Task<IReadOnlyList<string>> SweepAsync(ChatDbContext context, DateOnly cutoff) =>
        await CreateSweep(context).DropExpiredMessagePartitionsAsync(cutoff);

    private static async Task<IReadOnlyList<string>> PartitionNamesAsync(ChatDbContext context) =>
        await context.Database
            .SqlQueryRaw<string>(
                """
                SELECT child.relname AS "Value"
                FROM pg_inherits
                JOIN pg_class parent ON parent.oid = pg_inherits.inhparent
                JOIN pg_class child ON child.oid = pg_inherits.inhrelid
                WHERE parent.relname = 'message'
                ORDER BY child.relname
                """)
            .ToListAsync();
}
