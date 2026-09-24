using System.Net;
using InternalChat.Domain.Attachments;
using InternalChat.Domain.Common;
using InternalChat.Domain.Messages;
using InternalChat.Infrastructure.Persistence;
using InternalChat.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Minio;
using Minio.DataModel.Args;

namespace InternalChat.IntegrationTests.Attachments;

/// <summary>
/// T146 — deleting a message stops its attachment being served (FR-027).
/// </summary>
/// <remarks>
/// <para>
/// <b>The attachment row and the object both survive the delete.</b> That is not an oversight: the
/// message is a tombstone rather than a removed row precisely so ordering and sequence continuity
/// hold, and the attachment is retained for the same reason plus audit. What changes is
/// reachability, and that is what these tests assert — the bytes stop being served while everything
/// about them stays on disk.
/// </para>
/// <para>
/// Worth stating because the obvious implementation is the wrong one: purging the object on delete
/// would satisfy "stop serving" and quietly break the retention and audit story, and it would make
/// the operation irreversible in a way FR-027 never asked for.
/// </para>
/// </remarks>
public sealed class DeletionTests : MessagingTestBase
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x01];

    public DeletionTests(StackFixture stack)
        : base(stack)
    {
    }

    [Fact]
    public async Task An_attachment_on_a_live_message_is_served()
    {
        Scenario scenario = await ArrangeAsync();

        HttpClient author = await AuthenticatedClientAsync("an.nguyen");
        HttpResponseMessage response = await author.GetAsync(scenario.ContentUrl);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Deleting_the_message_stops_the_attachment_being_served()
    {
        Scenario scenario = await ArrangeAsync();

        HttpClient author = await AuthenticatedClientAsync("an.nguyen");
        Assert.Equal(HttpStatusCode.OK, (await author.GetAsync(scenario.ContentUrl)).StatusCode);

        HttpResponseMessage deleted = await author.DeleteAsync(
            $"/api/v1/conversations/{scenario.ConversationId}/messages/{scenario.MessageId}");

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        // FR-027. Same URL, same member, same token — the only thing that changed is that the
        // message carrying it became a tombstone.
        Assert.Equal(HttpStatusCode.NotFound, (await author.GetAsync(scenario.ContentUrl)).StatusCode);
    }

    [Fact]
    public async Task Deletion_stops_service_for_every_member_not_only_the_author()
    {
        Scenario scenario = await ArrangeAsync();

        HttpClient author = await AuthenticatedClientAsync("an.nguyen");
        HttpClient other = await AuthenticatedClientAsync("binh.tran");

        Assert.Equal(HttpStatusCode.OK, (await other.GetAsync(scenario.ContentUrl)).StatusCode);

        await author.DeleteAsync(
            $"/api/v1/conversations/{scenario.ConversationId}/messages/{scenario.MessageId}");

        // The check is on the message's state, not on who is asking. A reader who had already
        // loaded the conversation must not keep a working URL after the sender withdrew the file.
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync(scenario.ContentUrl)).StatusCode);
    }

    [Fact]
    public async Task The_row_and_the_object_survive_the_delete()
    {
        Scenario scenario = await ArrangeAsync();

        HttpClient author = await AuthenticatedClientAsync("an.nguyen");
        await author.DeleteAsync(
            $"/api/v1/conversations/{scenario.ConversationId}/messages/{scenario.MessageId}");

        await using ChatDbContext context = CreateDbContext();

        Attachment row = await context.Attachments.FirstAsync(a => a.Id == scenario.AttachmentId);

        // Still clean, still present. FR-027 is about reachability; the retention sweep (T206) is
        // what eventually removes bytes, and it works on age rather than on deletion.
        Assert.Equal(ScanVerdict.Clean, row.ScanStatus);

        StatObjectArgs stat = new StatObjectArgs()
            .WithBucket(StackFixture.AttachmentsBucket)
            .WithObject(row.ObjectKey);

        Assert.NotNull(await Stack.CreateMinioClient().StatObjectAsync(stat));
    }

    private async Task<Scenario> ArrangeAsync()
    {
        Guid conversationId = Guid.CreateVersion7();
        Guid attachmentId;
        Guid messageId;

        await using (ChatDbContext context = CreateDbContext())
        {
            Guid author = await TestData.SeedEmployeeAsync(context, "an.nguyen");
            Guid other = await TestData.SeedEmployeeAsync(context, "binh.tran");

            await TestData.SeedMembershipAsync(context, conversationId, author);
            await TestData.SeedMembershipAsync(context, conversationId, other);

            FixedClock clock = new();

            Message message = Message.Send(
                Guid.CreateVersion7(),
                conversationId,
                seq: 1,
                author,
                ClientMessageKey.Parse("01JBXQ7ZPT4M9WYFN2VKC3H6RD"),
                MessageBody.Create("here is the screenshot"),
                clock);

            message.ClearDomainEvents();
            context.Messages.Add(message);

            Attachment attachment = Attachment.Reserve(
                Guid.CreateVersion7(),
                conversationId,
                author,
                AttachmentKind.Image,
                "image/png",
                Png.Length,
                durationSeconds: null,
                "screenshot.png",
                clock);

            attachment.AttachTo(message.Id, message.SentAt);
            attachment.MarkClean(Png.Length, posterObjectKey: null, clock);
            attachment.ClearDomainEvents();

            context.Attachments.Add(attachment);
            await context.SaveChangesAsync();

            attachmentId = attachment.Id;
            messageId = message.Id;

            using MemoryStream content = new(Png);

            await Stack.CreateMinioClient().PutObjectAsync(new PutObjectArgs()
                .WithBucket(StackFixture.AttachmentsBucket)
                .WithObject(attachment.ObjectKey)
                .WithStreamData(content)
                .WithObjectSize(content.Length)
                .WithContentType("image/png"));
        }

        return new Scenario(
            conversationId,
            messageId,
            attachmentId,
            $"/api/v1/attachments/{attachmentId}/content");
    }

    private sealed record Scenario(
        Guid ConversationId,
        Guid MessageId,
        Guid AttachmentId,
        string ContentUrl);
}
