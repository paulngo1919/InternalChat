using System.Net;
using System.Net.Http.Json;
using InternalChat.Api.Contracts;
using InternalChat.Domain.Attachments;
using InternalChat.Infrastructure.Persistence;
using InternalChat.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;

namespace InternalChat.IntegrationTests.Attachments;

/// <summary>
/// T147 — an interrupted upload leaves no partial and no duplicate attachment (FR-026).
/// </summary>
/// <remarks>
/// <para>
/// <b>"No partial" is the more interesting half.</b> A partial attachment is not a half-written
/// row — the row is one INSERT — it is a row that says <c>clean</c> while the object behind it is
/// truncated or absent. The upload flow makes that unreachable by construction: the row starts at
/// <c>pending</c>, nothing is served from <c>pending</c>, and only the scan consumer can move it
/// on, after reading the bytes that actually arrived.
/// </para>
/// <para>
/// <b>"No duplicate" is about the retry.</b> A client whose upload dies mid-transfer reserves
/// again. That yields a second ticket and a second row, which is correct — but only one can ever be
/// bound to a message, and the abandoned one must never become retrievable on its own.
/// </para>
/// </remarks>
public sealed class InterruptedUploadTests : MessagingTestBase
{
    public InterruptedUploadTests(StackFixture stack)
        : base(stack)
    {
    }

    [Fact]
    public async Task A_reserved_upload_is_not_retrievable_before_its_bytes_are_scanned()
    {
        (HttpClient client, Guid conversationId, _) = await ArrangeAsync();

        UploadTicketResponse ticket = await ReserveAsync(client, conversationId);

        // The whole of FR-024 in one assertion. The ticket exists, the row exists, and nothing has
        // been uploaded or scanned — so there is nothing to serve.
        HttpResponseMessage content = await client.GetAsync(
            $"/api/v1/attachments/{ticket.AttachmentId}/content");

        Assert.Equal(HttpStatusCode.NotFound, content.StatusCode);
    }

    [Fact]
    public async Task A_reserved_upload_reports_pending_rather_than_looking_broken()
    {
        (HttpClient client, Guid conversationId, _) = await ArrangeAsync();

        UploadTicketResponse ticket = await ReserveAsync(client, conversationId);

        AttachmentResponse? metadata = await client
            .GetFromJsonAsync<AttachmentResponse>($"/api/v1/attachments/{ticket.AttachmentId}");

        Assert.NotNull(metadata);
        Assert.Equal(ScanStatuses.Pending, metadata.ScanStatus);

        // No content URL while pending. A client that received one would render a broken image
        // instead of "scanning…", and would keep retrying an address that is correctly refusing.
        Assert.Null(metadata.ContentUrl);
    }

    [Fact]
    public async Task Reserving_twice_after_an_interruption_leaves_two_independent_pending_rows()
    {
        (HttpClient client, Guid conversationId, _) = await ArrangeAsync();

        UploadTicketResponse abandoned = await ReserveAsync(client, conversationId);
        UploadTicketResponse retried = await ReserveAsync(client, conversationId);

        // Distinct ids and distinct object keys. Reusing the key would let the retry's bytes land
        // on top of the abandoned upload's — which is how a "duplicate" becomes a corrupted single
        // object rather than two harmless rows.
        Assert.NotEqual(abandoned.AttachmentId, retried.AttachmentId);

        await using ChatDbContext context = CreateDbContext();

        List<Attachment> rows = await context.Attachments
            .Where(a => a.ConversationId == conversationId)
            .ToListAsync();

        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Equal(ScanVerdict.Pending, row.ScanStatus));
        Assert.All(rows, row => Assert.Null(row.MessageId));
        Assert.Equal(2, rows.Select(r => r.ObjectKey).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task An_abandoned_upload_cannot_be_attached_by_someone_else()
    {
        (HttpClient client, Guid conversationId, _) = await ArrangeAsync();

        UploadTicketResponse ticket = await ReserveAsync(client, conversationId);

        HttpClient other = await AuthenticatedClientAsync("binh.tran");

        HttpResponseMessage response = await other.PostAsJsonAsync(
            $"/api/v1/conversations/{conversationId}/messages",
            new SendMessageRequest(
                "01JBXQ7ZPT4M9WYFN2VKC3H6RE",
                "not mine",
                [ticket.AttachmentId],
                null));

        // The uploader is part of the query that finds attachable rows, so someone else's reserved
        // upload is simply absent — indistinguishable from an id that never existed.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task An_attachment_from_another_conversation_cannot_be_attached()
    {
        (HttpClient client, Guid conversationId, Guid otherConversationId) = await ArrangeAsync();

        UploadTicketResponse ticket = await ReserveAsync(client, otherConversationId);

        HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/api/v1/conversations/{conversationId}/messages",
            new SendMessageRequest(
                "01JBXQ7ZPT4M9WYFN2VKC3H6RF",
                "wrong conversation",
                [ticket.AttachmentId],
                null));

        // Binding it would move a file across an authorization boundary: ConversationId is what
        // every membership check reads, and it would not have changed with the message.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task A_send_naming_an_unknown_attachment_is_refused_without_creating_the_message()
    {
        (HttpClient client, Guid conversationId, _) = await ArrangeAsync();

        HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/api/v1/conversations/{conversationId}/messages",
            new SendMessageRequest(
                "01JBXQ7ZPT4M9WYFN2VKC3H6RG",
                "with a ghost attachment",
                [Guid.CreateVersion7()],
                null));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        await using ChatDbContext context = CreateDbContext();

        // The whole send rolls back. A message that persisted without the image the sender attached
        // is the failure FR-026 is written against — the person believes they shared something and
        // will not find out otherwise until someone asks.
        Assert.Empty(await context.Messages.Where(m => m.ConversationId == conversationId).ToListAsync());
    }

    private static async Task<UploadTicketResponse> ReserveAsync(HttpClient client, Guid conversationId)
    {
        HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/api/v1/conversations/{conversationId}/attachments",
            new RequestUploadRequest(AttachmentKinds.Image, "image/png", 4096, null, "shot.png"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        UploadTicketResponse? ticket = await response.Content.ReadFromJsonAsync<UploadTicketResponse>();
        Assert.NotNull(ticket);

        // A quarantine address, never a retrieval one.
        Assert.Contains(StackFixture.QuarantineBucket, ticket.UploadUrl, StringComparison.Ordinal);

        return ticket;
    }

    private async Task<(HttpClient Client, Guid ConversationId, Guid OtherConversationId)> ArrangeAsync()
    {
        Guid conversationId = Guid.CreateVersion7();
        Guid otherConversationId = Guid.CreateVersion7();

        await using (ChatDbContext context = CreateDbContext())
        {
            Guid author = await TestData.SeedEmployeeAsync(context, "an.nguyen");
            Guid other = await TestData.SeedEmployeeAsync(context, "binh.tran");

            await TestData.SeedMembershipAsync(context, conversationId, author);
            await TestData.SeedMembershipAsync(context, conversationId, other);
            await TestData.SeedMembershipAsync(context, otherConversationId, author);
        }

        return (await AuthenticatedClientAsync("an.nguyen"), conversationId, otherConversationId);
    }
}
