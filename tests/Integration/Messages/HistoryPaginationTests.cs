using InternalChat.Api.Contracts;
using InternalChat.Domain.Conversations;
using InternalChat.Infrastructure.Persistence;
using InternalChat.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;

namespace InternalChat.IntegrationTests.Messages;

/// <summary>
/// T083 — FR-013: keyset pagination stays correct across a monthly partition boundary.
/// </summary>
/// <remarks>
/// <para>
/// The partition boundary is the point of this file. <c>message</c> is partitioned by month on
/// <c>sent_at</c> (research.md D11) while pagination is ordered by <c>seq</c>, so a page can span
/// two physical tables. PostgreSQL will happily return rows from a partitioned parent in whatever
/// order the plan produces unless <c>ORDER BY</c> says otherwise — and per-partition ordering looks
/// correct in every test where all the rows sit in one month.
/// </para>
/// <para>
/// So every conversation here deliberately straddles at least one boundary, and the assertions are
/// about the <em>whole sequence</em> reassembled from pages rather than about any single page. A
/// test that checked one page would pass against an implementation that silently dropped the rows
/// on the far side of the boundary.
/// </para>
/// </remarks>
public sealed class HistoryPaginationTests : MessagingTestBase
{
    public HistoryPaginationTests(StackFixture stack)
        : base(stack)
    {
    }

    /// <summary>
    /// Paging backwards through history returns every message exactly once, newest first.
    /// </summary>
    [Fact]
    public async Task Paging_backwards_across_a_month_boundary_returns_every_message_once()
    {
        const string Reader = "an.nguyen";
        const int Total = 40;
        const int PageSize = 10;

        (Guid conversationId, _) = await SeedStraddlingConversationAsync(Reader, "binh.tran", Total);

        using HttpClient client = await AuthenticatedClientAsync(Reader);

        List<long> collected = [];
        long? beforeSeq = null;
        int pages = 0;

        while (true)
        {
            string query = beforeSeq is null
                ? $"limit={PageSize}"
                : $"limit={PageSize}&beforeSeq={beforeSeq}";

            MessagePageResponse page = await HistoryAsync(client, conversationId, query);

            collected.AddRange(page.Items.Select(m => m.Seq));

            // Newest first within the page, as the contract documents.
            Assert.Equal(page.Items.Select(m => m.Seq).OrderByDescending(s => s), page.Items.Select(m => m.Seq));

            pages++;

            Assert.True(pages <= (Total / PageSize) + 2, "Pagination did not terminate.");

            if (!page.HasMore)
            {
                break;
            }

            Assert.NotEmpty(page.Items);
            beforeSeq = page.Items[^1].Seq;
        }

        // Every sequence, once. Distinct() would hide a duplicate, so the count is asserted first.
        Assert.Equal(Total, collected.Count);
        Assert.Equal(Total, collected.Distinct().Count());
        Assert.Equal(Enumerable.Range(1, Total).Select(i => (long)i).OrderByDescending(s => s), collected);
    }

    /// <summary>
    /// <c>afterSeq</c> returns the catch-up window oldest-first, across the boundary.
    /// </summary>
    /// <remarks>
    /// The reconnect direction (FR-018). Oldest-first matters: a client applying a catch-up batch
    /// newest-first would render the conversation backwards until the next full reload.
    /// </remarks>
    [Fact]
    public async Task Paging_forwards_from_a_sequence_returns_the_remainder_oldest_first()
    {
        const string Reader = "chi.le";
        const int Total = 30;
        const long From = 12;

        (Guid conversationId, _) = await SeedStraddlingConversationAsync(Reader, "dung.pham", Total);

        using HttpClient client = await AuthenticatedClientAsync(Reader);

        MessagePageResponse page = await HistoryAsync(client, conversationId, $"afterSeq={From}&limit=100");

        Assert.Equal(
            Enumerable.Range((int)From + 1, Total - (int)From).Select(i => (long)i),
            page.Items.Select(m => m.Seq));
    }

    /// <summary>
    /// The <c>visible_from_seq</c> floor is applied, and pagination never reaches below it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// US3 scenario 4 enforced in the query rather than the UI. The floor is checked here as well as
    /// in the US3 suite because pagination is where it is most likely to be lost: a keyset query
    /// that carries the cursor but forgets the floor returns the hidden messages as soon as the
    /// caller pages past the visible ones.
    /// </para>
    /// <para>
    /// The floor is set below a partition boundary and the visible window above it, so an
    /// implementation that filtered per-partition rather than in the query would show its seams.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task History_never_returns_messages_below_the_callers_floor()
    {
        const string Reader = "hai.vo";
        const int Total = 30;
        const long Floor = 18;

        (Guid conversationId, Guid readerId) = await SeedStraddlingConversationAsync(Reader, "khanh.dang", Total);

        await using (ChatDbContext context = CreateDbContext())
        {
            await context.Memberships
                .Where(m => m.ConversationId == conversationId && m.EmployeeId == readerId)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.VisibleFromSeq, Floor));
        }

        using HttpClient client = await AuthenticatedClientAsync(Reader);

        List<long> collected = [];
        long? beforeSeq = null;

        while (true)
        {
            string query = beforeSeq is null ? "limit=5" : $"limit=5&beforeSeq={beforeSeq}";

            MessagePageResponse page = await HistoryAsync(client, conversationId, query);

            collected.AddRange(page.Items.Select(m => m.Seq));

            if (!page.HasMore || page.Items.Count == 0)
            {
                break;
            }

            beforeSeq = page.Items[^1].Seq;
        }

        Assert.Equal(Total - (int)Floor, collected.Count);
        Assert.DoesNotContain(collected, seq => seq <= Floor);
        Assert.Equal(Floor + 1, collected.Min());
    }

    /// <summary>
    /// The documented maximum page size is enforced.
    /// </summary>
    /// <remarks>
    /// Principle V caps a page at 100. A caller asking for more is clamped rather than refused,
    /// because refusing turns a client's optimistic guess into a broken screen — but it must be
    /// clamped somewhere, or the 250 ms history budget is a function of what a caller types.
    /// </remarks>
    [Fact]
    public async Task A_page_larger_than_the_maximum_is_clamped()
    {
        const string Reader = "giang.hoang";
        const int Total = 130;

        (Guid conversationId, _) = await SeedStraddlingConversationAsync(Reader, "minh.do", Total);

        using HttpClient client = await AuthenticatedClientAsync(Reader);

        MessagePageResponse page = await HistoryAsync(client, conversationId, "limit=5000");

        Assert.Equal(100, page.Items.Count);
        Assert.True(page.HasMore);
    }

    /// <summary>
    /// A tombstone keeps its place in the sequence.
    /// </summary>
    /// <remarks>
    /// Deletion is soft precisely so pagination stays gapless (data-model.md). If a deleted message
    /// vanished from history, "everything above seq N" would return fewer rows than the sequence
    /// implies and a client counting them would conclude it had missed something.
    /// </remarks>
    [Fact]
    public async Task A_deleted_message_still_occupies_its_sequence_in_history()
    {
        const string Reader = "nga.ngo";
        const int Total = 20;

        (Guid conversationId, Guid readerId) = await SeedStraddlingConversationAsync(Reader, "phuc.duong", Total);

        await using (ChatDbContext context = CreateDbContext())
        {
            Domain.Messages.Message message = await context.Messages
                .SingleAsync(m => m.ConversationId == conversationId && m.Seq == 10);

            // Deleted through the domain, so the body-or-tombstone invariant holds exactly as the
            // application would leave it.
            message.Delete(readerId, new FixedClock(message.SentAt.AddMinutes(1)));
            message.ClearDomainEvents();

            await context.SaveChangesAsync();
        }

        using HttpClient client = await AuthenticatedClientAsync(Reader);

        MessagePageResponse page = await HistoryAsync(client, conversationId, "limit=100");

        Assert.Equal(Total, page.Items.Count);

        MessageResponse tombstone = page.Items.Single(m => m.Seq == 10);

        Assert.Null(tombstone.Body);
        Assert.NotNull(tombstone.DeletedAt);
    }

    /// <summary>
    /// Seeds a conversation whose messages straddle three monthly partitions.
    /// </summary>
    /// <remarks>
    /// Timestamps are spread backwards from the start of the current month, so the run always
    /// crosses at least two boundaries and always lands inside the partitions the migration
    /// created. Anchoring to "now" rather than to a fixed date keeps the test from expiring the
    /// month it was written in.
    /// </remarks>
    private async Task<(Guid ConversationId, Guid ReaderId)> SeedStraddlingConversationAsync(
        string reader,
        string other,
        int messageCount)
    {
        Guid readerId;
        Guid otherId;

        await using (ChatDbContext context = CreateDbContext())
        {
            readerId = await TestData.SeedEmployeeAsync(context, reader);
            otherId = await TestData.SeedEmployeeAsync(context, other);
        }

        Guid conversationId = await SeedDirectConversationAsync(readerId, otherId);

        DateTimeOffset currentMonth = new(
            DateTimeOffset.UtcNow.Year,
            DateTimeOffset.UtcNow.Month,
            1,
            0,
            0,
            0,
            TimeSpan.Zero);

        // Two months back, then forward in even steps so the run spans that month, the next, and
        // into the current one.
        DateTimeOffset start = currentMonth.AddMonths(-2).AddDays(1);
        TimeSpan step = TimeSpan.FromMinutes((currentMonth.AddDays(5) - start).TotalMinutes / messageCount);

        List<DateTimeOffset> instants = [.. Enumerable.Range(0, messageCount).Select(i => start + (step * i))];

        await SeedMessagesAsync(conversationId, readerId, instants);

        await using (ChatDbContext verification = CreateDbContext())
        {
            // The premise of the whole file. If the seeded run happened to land in one partition,
            // every assertion below would still pass and prove nothing about the boundary.
            int partitionsTouched = await verification.Database
                .SqlQueryRaw<int>(
                    """
                    SELECT count(DISTINCT tableoid)::int AS "Value"
                    FROM message
                    WHERE conversation_id = {0}
                    """,
                    conversationId)
                .SingleAsync();

            Assert.True(
                partitionsTouched >= 2,
                $"The seeded messages landed in {partitionsTouched} partition(s). This suite exists "
                + "to test pagination across a boundary, so a single-partition run would pass "
                + "while asserting nothing.");
        }

        return (conversationId, readerId);
    }
}
