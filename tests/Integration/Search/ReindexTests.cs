using System.Net.Http.Json;
using InternalChat.Api.Contracts;
using InternalChat.Domain.Common;
using InternalChat.Domain.Conversations;
using InternalChat.Domain.Messages;
using InternalChat.Infrastructure.Persistence;
using InternalChat.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;

namespace InternalChat.IntegrationTests.Search;

/// <summary>
/// T162 — edits, deletions, and membership removal are reflected in results (FR-032).
/// </summary>
/// <remarks>
/// <para>
/// <b>These tests assert an immediacy the design gives for free, and that is the point of running
/// them.</b> <c>body_tsv</c> is a <c>GENERATED ALWAYS ... STORED</c> column, so an edit's new text
/// is searchable in the same transaction that wrote it and a delete's old text stops matching in
/// the same transaction that cleared it. There is no re-indexing step and therefore no window in
/// which the index disagrees with the row.
/// </para>
/// <para>
/// Membership removal is immediate for a different reason: the scope is recomputed from the
/// database on every search, so a removed member's next search simply no longer includes that
/// conversation. Nothing is rebuilt because nothing about the content changed — only who may see it.
/// </para>
/// <para>
/// Written against the real database precisely because all three claims are claims about
/// PostgreSQL's behaviour rather than about this code's branching. A test with a fake index would
/// pass while the generated column was misdeclared.
/// </para>
/// </remarks>
public sealed class ReindexTests : MessagingTestBase
{
    private const string OriginalPhrase = "aubergine provisioning schedule";
    private const string ReplacementPhrase = "courgette provisioning schedule";

    public ReindexTests(StackFixture stack)
        : base(stack)
    {
    }

    [Fact]
    public async Task An_edit_makes_the_new_text_findable_and_the_old_text_not()
    {
        Arrangement arrangement = await ArrangeAsync();

        Assert.Single((await SearchAsync(OriginalPhrase)).Items);

        HttpClient client = await AuthenticatedClientAsync("an.nguyen");

        HttpResponseMessage edited = await client.PatchAsJsonAsync(
            $"/api/v1/conversations/{arrangement.ConversationId}/messages/{arrangement.MessageId}",
            new EditMessageRequest(ReplacementPhrase));

        edited.EnsureSuccessStatusCode();

        // Both halves. Finding the new text proves the vector was recomputed; not finding the old
        // proves it was replaced rather than appended to — a trigger that concatenated would pass
        // the first assertion and fail the second.
        Assert.Single((await SearchAsync(ReplacementPhrase)).Items);
        Assert.Empty((await SearchAsync(OriginalPhrase)).Items);
    }

    [Fact]
    public async Task A_deleted_message_stops_matching_its_own_text()
    {
        Arrangement arrangement = await ArrangeAsync();

        Assert.Single((await SearchAsync(OriginalPhrase)).Items);

        HttpClient client = await AuthenticatedClientAsync("an.nguyen");

        HttpResponseMessage deleted = await client.DeleteAsync(
            $"/api/v1/conversations/{arrangement.ConversationId}/messages/{arrangement.MessageId}");

        deleted.EnsureSuccessStatusCode();

        // FR-032. The row survives as a tombstone — ordering depends on it — but its text is gone,
        // so the vector is empty and matches nothing.
        Assert.Empty((await SearchAsync(OriginalPhrase)).Items);
    }

    [Fact]
    public async Task A_tombstone_survives_the_delete_that_made_it_unsearchable()
    {
        Arrangement arrangement = await ArrangeAsync();

        HttpClient client = await AuthenticatedClientAsync("an.nguyen");
        await client.DeleteAsync(
            $"/api/v1/conversations/{arrangement.ConversationId}/messages/{arrangement.MessageId}");

        await using ChatDbContext context = CreateDbContext();

        Message tombstone = await context.Messages
            .FirstAsync(m => m.Id == arrangement.MessageId);

        // Unsearchable is not the same as gone. A search index that achieved FR-032 by deleting the
        // row would break sequence continuity, which every reconnect depends on.
        Assert.True(tombstone.IsDeleted);
        Assert.Null(tombstone.Body);
        Assert.Equal(1, tombstone.Seq);
    }

    [Fact]
    public async Task Losing_membership_removes_the_conversation_from_future_results()
    {
        Arrangement arrangement = await ArrangeAsync();

        Assert.Single((await SearchAsync(OriginalPhrase, "binh.tran")).Items);

        await using (ChatDbContext context = CreateDbContext())
        {
            Membership membership = await context.Memberships
                .FirstAsync(m => m.ConversationId == arrangement.ConversationId
                    && m.EmployeeId == arrangement.SecondMemberId);

            membership.Remove(new FixedClock());
            await context.SaveChangesAsync();
        }

        // The scope is recomputed per search, so this needs no re-indexing — only the membership
        // cache to expire, which the test flushes rather than waiting out.
        await ResetCacheAsync();

        Assert.Empty((await SearchAsync(OriginalPhrase, "binh.tran")).Items);

        // And the author, who is still a member, is unaffected. Without this the previous
        // assertion would also pass if the message had simply been destroyed.
        Assert.Single((await SearchAsync(OriginalPhrase)).Items);
    }

    [Fact]
    public async Task An_edit_by_the_author_is_findable_by_every_member()
    {
        Arrangement arrangement = await ArrangeAsync();

        HttpClient author = await AuthenticatedClientAsync("an.nguyen");

        await author.PatchAsJsonAsync(
            $"/api/v1/conversations/{arrangement.ConversationId}/messages/{arrangement.MessageId}",
            new EditMessageRequest(ReplacementPhrase));

        // The index is shared, not per-reader — only the scope is per-reader. A second member sees
        // the edit immediately, with no action of their own.
        Assert.Single((await SearchAsync(ReplacementPhrase, "binh.tran")).Items);
    }

    private async Task<SearchResultPageResponse> SearchAsync(
        string phrase,
        string username = "an.nguyen")
    {
        HttpClient client = await AuthenticatedClientAsync(username);

        SearchResultPageResponse? page = await client.GetFromJsonAsync<SearchResultPageResponse>(
            $"/api/v1/search/messages?q={Uri.EscapeDataString(phrase)}");

        Assert.NotNull(page);
        return page;
    }

    private async Task<Arrangement> ArrangeAsync()
    {
        Guid conversationId = Guid.CreateVersion7();

        await using ChatDbContext context = CreateDbContext();

        Guid author = await TestData.SeedEmployeeAsync(context, "an.nguyen");
        Guid second = await TestData.SeedEmployeeAsync(context, "binh.tran");

        await TestData.SeedMembershipAsync(context, conversationId, author);
        await TestData.SeedMembershipAsync(context, conversationId, second);

        // Allocated through the conversation so last_seq and the message agree — an edit endpoint
        // that could not find the message would fail for the wrong reason.
        Conversation conversation = await context.Conversations.FirstAsync(c => c.Id == conversationId);

        FixedClock clock = new();

        Message message = Message.Send(
            Guid.CreateVersion7(),
            conversationId,
            seq: 1,
            author,
            ClientMessageKey.Parse("01JBXQ7ZPT4M9WYFN2VKC3H6RD"),
            MessageBody.Create(OriginalPhrase),
            clock);

        message.ClearDomainEvents();
        conversation.SynchroniseSequence(1);
        conversation.ClearDomainEvents();

        context.Messages.Add(message);
        await context.SaveChangesAsync();

        return new Arrangement(conversationId, message.Id, second);
    }

    private sealed record Arrangement(Guid ConversationId, Guid MessageId, Guid SecondMemberId);
}
