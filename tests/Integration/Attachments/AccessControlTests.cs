using System.Net;
using System.Net.Http.Json;
using InternalChat.Api.Contracts;
using InternalChat.Domain.Attachments;
using InternalChat.Infrastructure.Persistence;
using InternalChat.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Minio;
using Minio.DataModel.Args;

namespace InternalChat.IntegrationTests.Attachments;

/// <summary>
/// T144 — a non-member is refused the content URL, however they obtained it (FR-025).
/// </summary>
/// <remarks>
/// <para>
/// <b>The URL is handed to the non-member deliberately.</b> These tests do not check that an
/// outsider cannot guess the address; they check that knowing it exactly buys nothing. FR-025 says
/// retrieval is restricted to members "regardless of how the retrieval address was obtained", and
/// the realistic leak is mundane — a URL pasted into a ticket, an email, another chat.
/// </para>
/// <para>
/// Against the real stack, because the thing under test is a database-backed membership check on
/// every request. A stubbed membership reader would assert that the code calls a method, which is
/// not the same claim.
/// </para>
/// </remarks>
public sealed class AccessControlTests : MessagingTestBase
{
    public AccessControlTests(StackFixture stack)
        : base(stack)
    {
    }

    [Fact]
    public async Task A_member_can_retrieve_an_attachment_they_posted()
    {
        AttachmentScenario scenario = await ArrangeAsync();

        HttpClient member = await AuthenticatedClientAsync("an.nguyen");
        HttpResponseMessage response = await member.GetAsync(scenario.ContentUrl);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(AttachmentScenario.Bytes, await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task A_non_member_holding_the_exact_content_url_is_refused()
    {
        AttachmentScenario scenario = await ArrangeAsync();

        // chi.le is a real, active employee with a valid token. She is simply not in the
        // conversation — which is the only thing standing between her and the bytes.
        HttpClient outsider = await AuthenticatedClientAsync("chi.le");
        HttpResponseMessage response = await outsider.GetAsync(scenario.ContentUrl);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_refusal_is_indistinguishable_from_an_attachment_that_never_existed()
    {
        AttachmentScenario scenario = await ArrangeAsync();

        HttpClient outsider = await AuthenticatedClientAsync("chi.le");

        HttpResponseMessage real = await outsider.GetAsync(scenario.ContentUrl);
        HttpResponseMessage invented = await outsider.GetAsync(
            $"/api/v1/attachments/{Guid.CreateVersion7()}/content");

        // SC-017. If these differed, anyone could enumerate which attachment ids are real by
        // reading the status code — and a 403 on a real id versus a 404 on a fake one is exactly
        // that answer.
        Assert.Equal(invented.StatusCode, real.StatusCode);

        // The trace id is excluded, and only the trace id. It is unique per request by design — two
        // responses sharing one would be the defect — so comparing raw bodies would fail on the one
        // field that is supposed to differ while saying nothing about the ones that must not.
        Assert.Equal(
            WithoutTraceId(await invented.Content.ReadAsStringAsync()),
            WithoutTraceId(await real.Content.ReadAsStringAsync()));
    }

    private static string WithoutTraceId(string problemJson) =>
        System.Text.RegularExpressions.Regex.Replace(
            problemJson,
            "\"traceId\"\\s*:\\s*\"[^\"]*\"",
            "\"traceId\":\"<redacted>\"",
            System.Text.RegularExpressions.RegexOptions.None,
            TimeSpan.FromSeconds(1));

    [Fact]
    public async Task Metadata_is_refused_to_a_non_member_too()
    {
        AttachmentScenario scenario = await ArrangeAsync();

        HttpClient outsider = await AuthenticatedClientAsync("chi.le");
        HttpResponseMessage response = await outsider.GetAsync(
            $"/api/v1/attachments/{scenario.AttachmentId}");

        // A file name and a size describe what was posted in a conversation. Gating the bytes but
        // serving the metadata would leak the shape of a conversation to someone refused its
        // content.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Access_ends_when_membership_does()
    {
        AttachmentScenario scenario = await ArrangeAsync();

        HttpClient second = await AuthenticatedClientAsync("binh.tran");
        Assert.Equal(HttpStatusCode.OK, (await second.GetAsync(scenario.ContentUrl)).StatusCode);

        await using (ChatDbContext context = CreateDbContext())
        {
            Domain.Conversations.Membership membership = await context.Memberships
                .FirstAsync(m => m.ConversationId == scenario.ConversationId
                    && m.EmployeeId == scenario.SecondMemberId);

            membership.Remove(new FixedClock());

            await context.SaveChangesAsync();
        }

        // FR-027: an attachment stops being served when the requester's conversation access ends.
        // The membership cache has a 30-second TTL, so this is flushed rather than waited out —
        // the behaviour under test is the authorization decision, not the cache's expiry clock.
        await ResetCacheAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await second.GetAsync(scenario.ContentUrl)).StatusCode);
    }

    /// <summary>
    /// Seeds a conversation with two members, a clean attachment, and the message carrying it.
    /// </summary>
    /// <remarks>
    /// The object is put into the clean bucket directly and the row marked clean, rather than
    /// driving the upload and scan. That machinery is T145's and T147's subject; here it is
    /// scaffolding, and going through it would make these tests fail for reasons about ClamAV.
    /// </remarks>
    private async Task<AttachmentScenario> ArrangeAsync()
    {
        Guid author;
        Guid second;
        Guid conversationId = Guid.CreateVersion7();
        Guid attachmentId;

        await using (ChatDbContext context = CreateDbContext())
        {
            author = await TestData.SeedEmployeeAsync(context, "an.nguyen");
            second = await TestData.SeedEmployeeAsync(context, "binh.tran");
            await TestData.SeedEmployeeAsync(context, "chi.le");

            await TestData.SeedMembershipAsync(context, conversationId, author);
            await TestData.SeedMembershipAsync(context, conversationId, second);

            FixedClock clock = new();

            Attachment attachment = Attachment.Reserve(
                Guid.CreateVersion7(),
                conversationId,
                author,
                AttachmentKind.Image,
                "image/png",
                AttachmentScenario.Bytes.Length,
                durationSeconds: null,
                "screenshot.png",
                clock);

            attachment.MarkClean(AttachmentScenario.Bytes.Length, posterObjectKey: null, clock);
            attachment.ClearDomainEvents();

            context.Attachments.Add(attachment);
            await context.SaveChangesAsync();

            attachmentId = attachment.Id;

            await PutCleanObjectAsync(attachment.ObjectKey);
        }

        return new AttachmentScenario(
            conversationId,
            attachmentId,
            second,
            $"/api/v1/attachments/{attachmentId}/content");
    }

    private async Task PutCleanObjectAsync(string objectKey)
    {
        IMinioClient minio = Stack.CreateMinioClient();

        using MemoryStream content = new(AttachmentScenario.Bytes);

        await minio.PutObjectAsync(new PutObjectArgs()
            .WithBucket(StackFixture.AttachmentsBucket)
            .WithObject(objectKey)
            .WithStreamData(content)
            .WithObjectSize(content.Length)
            .WithContentType("image/png"));
    }

    private sealed record AttachmentScenario(
        Guid ConversationId,
        Guid AttachmentId,
        Guid SecondMemberId,
        string ContentUrl)
    {
        /// <summary>A minimal but genuine PNG, so the content type is not a lie.</summary>
        public static byte[] Bytes { get; } =
        [
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
            0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
            0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
            0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4,
            0x89, 0x00, 0x00, 0x00, 0x0A, 0x49, 0x44, 0x41,
            0x54, 0x78, 0x9C, 0x63, 0x00, 0x01, 0x00, 0x00,
            0x05, 0x00, 0x01, 0x0D, 0x0A, 0x2D, 0xB4, 0x00,
            0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE,
            0x42, 0x60, 0x82,
        ];
    }
}
