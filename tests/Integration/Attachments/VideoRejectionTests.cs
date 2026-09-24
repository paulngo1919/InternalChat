using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using InternalChat.Api.Contracts;
using InternalChat.Domain.Attachments;
using InternalChat.Infrastructure.Persistence;
using InternalChat.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;

namespace InternalChat.IntegrationTests.Attachments;

/// <summary>
/// T173 — an oversized or unsupported video is rejected before upload, with the limit stated
/// (FR-023).
/// </summary>
/// <remarks>
/// <para>
/// <b>"Before upload" is asserted as the absence of a stored row, not merely as an error code.</b>
/// An endpoint that reserved the attachment and then failed would also return 413 — and would leave
/// a row and a live upload ticket behind, so the client could still transfer 600 MB to a location
/// that was already refused. Checking that nothing was written is what makes the ordering claim
/// real rather than incidental.
/// </para>
/// <para>
/// The refusal must also <em>state the limit</em>. FR-023 says so explicitly, and the reason is
/// practical: "too large" without a number leaves someone guessing how much to trim, and they
/// usually guess wrong twice before giving up.
/// </para>
/// </remarks>
public sealed class VideoRejectionTests : MessagingTestBase
{
    private Guid _conversationId;

    public VideoRejectionTests(StackFixture stack)
        : base(stack)
    {
    }

    [Fact]
    public async Task A_video_over_the_size_limit_is_refused_with_the_limit_stated()
    {
        HttpClient client = await ArrangeAsync();

        HttpResponseMessage response = await ReserveAsync(
            client,
            new RequestUploadRequest(
                AttachmentKinds.Video,
                "video/mp4",
                FileConstraints.MaximumVideoBytes + 1,
                DurationSeconds: 60,
                "long-recording.mp4"));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);

        string body = await response.Content.ReadAsStringAsync();

        using JsonDocument problem = JsonDocument.Parse(body);

        // The limit is machine-readable as well as in the title, so a client can render "500 MB"
        // rather than parsing prose.
        Assert.Equal(
            FileConstraints.MaximumVideoBytes,
            problem.RootElement.GetProperty("limitBytes").GetInt64());

        Assert.Contains(
            FileConstraints.MaximumVideoBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            problem.RootElement.GetProperty("title").GetString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refused_video_leaves_no_row_and_no_upload_ticket()
    {
        HttpClient client = await ArrangeAsync();

        await ReserveAsync(
            client,
            new RequestUploadRequest(
                AttachmentKinds.Video,
                "video/mp4",
                FileConstraints.MaximumVideoBytes + 1,
                60,
                "long-recording.mp4"));

        await using ChatDbContext context = CreateDbContext();

        // The assertion that makes "before upload" mean something. A row here would mean a ticket
        // was issued for a file the server had already refused.
        Assert.Empty(await context.Attachments
            .Where(a => a.ConversationId == _conversationId)
            .ToListAsync());
    }

    [Fact]
    public async Task A_video_at_the_size_limit_is_accepted()
    {
        HttpClient client = await ArrangeAsync();

        // The boundary in the accepting direction. Without it, a rejection test passes just as well
        // against an implementation that refuses every video.
        HttpResponseMessage response = await ReserveAsync(
            client,
            new RequestUploadRequest(
                AttachmentKinds.Video,
                "video/mp4",
                FileConstraints.MaximumVideoBytes,
                60,
                "big-but-allowed.mp4"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Theory]
    [InlineData("video/quicktime")]
    [InlineData("video/x-matroska")]
    [InlineData("video/x-msvideo")]
    [InlineData("application/octet-stream")]
    public async Task A_container_that_is_not_browser_playable_is_refused(string contentType)
    {
        HttpClient client = await ArrangeAsync();

        HttpResponseMessage response = await ReserveAsync(
            client,
            new RequestUploadRequest(AttachmentKinds.Video, contentType, 1024, 60, "clip.mov"));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);

        // Names what would have worked, so the person knows to re-export rather than retry.
        string body = await response.Content.ReadAsStringAsync();
        Assert.Contains("video/mp4", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_video_over_the_duration_limit_is_refused_before_upload()
    {
        HttpClient client = await ArrangeAsync();

        HttpResponseMessage response = await ReserveAsync(
            client,
            new RequestUploadRequest(
                AttachmentKinds.Video,
                "video/mp4",
                1024,
                FileConstraints.MaximumVideoDurationSeconds + 1,
                "too-long.mp4"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        Assert.Contains(
            FileConstraints.MaximumVideoDurationSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_video_declaring_no_duration_is_refused()
    {
        HttpClient client = await ArrangeAsync();

        HttpResponseMessage response = await ReserveAsync(
            client,
            new RequestUploadRequest(AttachmentKinds.Video, "video/mp4", 1024, null, "clip.mp4"));

        // Accepting a null duration would make the 600-second rule opt-in: any client that omitted
        // the field would skip it, and PostgreSQL's CHECK permits NULL because images need it to.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task An_image_sized_like_a_video_is_still_refused()
    {
        HttpClient client = await ArrangeAsync();

        // 100 MB: comfortably inside the video ceiling, absurd for an image. Proves the limit is
        // selected by kind rather than applied globally at the larger of the two.
        HttpResponseMessage response = await ReserveAsync(
            client,
            new RequestUploadRequest(
                AttachmentKinds.Image, "image/png", 100L * 1024 * 1024, null, "huge.png"));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    private Task<HttpResponseMessage> ReserveAsync(HttpClient client, RequestUploadRequest request) =>
        client.PostAsJsonAsync($"/api/v1/conversations/{_conversationId}/attachments", request);

    private async Task<HttpClient> ArrangeAsync()
    {
        _conversationId = Guid.CreateVersion7();

        await using (ChatDbContext context = CreateDbContext())
        {
            Guid author = await TestData.SeedEmployeeAsync(context, "an.nguyen");
            await TestData.SeedMembershipAsync(context, _conversationId, author);
        }

        return await AuthenticatedClientAsync("an.nguyen");
    }
}
