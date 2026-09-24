using System.Net;
using System.Net.Http.Headers;
using InternalChat.Domain.Attachments;
using InternalChat.Domain.Common;
using InternalChat.Infrastructure.Persistence;
using InternalChat.IntegrationTests.Fixtures;
using Minio;
using Minio.DataModel.Args;

namespace InternalChat.IntegrationTests.Attachments;

/// <summary>
/// T172 — a range request returns 206 with the correct bytes (FR-022).
/// </summary>
/// <remarks>
/// <para>
/// <b>What this test can and cannot prove, stated plainly.</b> In production the bytes never pass
/// through .NET: the endpoint authorizes and emits <c>X-Accel-Redirect</c>, and nginx serves the
/// range itself (research.md D7). The integration stack has no nginx, so these tests exercise the
/// fallback streaming path instead. That verifies the half that is this codebase's — authorization
/// runs, the correct object is selected, and range handling is enabled — and it does not verify the
/// proxy's behaviour, which is T176's <c>nginx -t</c> and the manual quickstart run.
/// </para>
/// <para>
/// Worth having anyway, because the failure it catches is a real one: an endpoint that returns the
/// whole file for a ranged request makes a 400 MB video download entirely before playback starts,
/// which is FR-022 broken in a way that looks like slowness rather than a bug.
/// </para>
/// </remarks>
public sealed class RangeRequestTests : MessagingTestBase
{
    /// <summary>Distinctive, so a wrong offset produces a visibly wrong slice rather than zeroes.</summary>
    private static readonly byte[] Payload =
        [.. Enumerable.Range(0, 4096).Select(index => (byte)(index % 251))];

    public RangeRequestTests(StackFixture stack)
        : base(stack)
    {
    }

    [Fact]
    public async Task A_request_without_a_range_returns_the_whole_object()
    {
        string url = await ArrangeAsync();

        HttpClient client = await AuthenticatedClientAsync("an.nguyen");
        HttpResponseMessage response = await client.GetAsync(url);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Payload, await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task A_ranged_request_returns_206_with_exactly_the_requested_bytes()
    {
        string url = await ArrangeAsync();

        HttpClient client = await AuthenticatedClientAsync("an.nguyen");

        using HttpRequestMessage request = new(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(100, 199);

        HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);

        byte[] received = await response.Content.ReadAsByteArrayAsync();

        // Inclusive on both ends, per RFC 9110 — 100..199 is 100 bytes, not 99. An off-by-one here
        // corrupts every seek in a video player rather than failing outright.
        Assert.Equal(100, received.Length);
        Assert.Equal(Payload[100..200], received);

        Assert.NotNull(response.Content.Headers.ContentRange);
        Assert.Equal(100, response.Content.Headers.ContentRange!.From);
        Assert.Equal(199, response.Content.Headers.ContentRange.To);
        Assert.Equal(Payload.Length, response.Content.Headers.ContentRange.Length);
    }

    [Fact]
    public async Task An_open_ended_range_returns_the_remainder()
    {
        string url = await ArrangeAsync();

        HttpClient client = await AuthenticatedClientAsync("an.nguyen");

        using HttpRequestMessage request = new(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(4000, null);

        HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);

        // "bytes=4000-" is what a player sends when it seeks and then keeps playing to the end.
        Assert.Equal(Payload[4000..], await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task A_suffix_range_returns_the_last_bytes()
    {
        string url = await ArrangeAsync();

        HttpClient client = await AuthenticatedClientAsync("an.nguyen");

        using HttpRequestMessage request = new(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(null, 128);

        HttpResponseMessage response = await client.SendAsync(request);

        // "bytes=-128" is exactly what a player does to find a trailing MP4 index — the case
        // faststart remuxing exists to make unnecessary, and which must still work when it happens.
        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal(Payload[^128..], await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task The_response_advertises_that_ranges_are_supported()
    {
        string url = await ArrangeAsync();

        HttpClient client = await AuthenticatedClientAsync("an.nguyen");
        HttpResponseMessage response = await client.GetAsync(url);

        // Without Accept-Ranges a browser will not attempt a seek at all — it downloads linearly
        // and the scrub bar does nothing, which is FR-022 failing silently.
        Assert.Contains("bytes", response.Headers.AcceptRanges);
    }

    [Fact]
    public async Task A_non_member_is_refused_even_for_a_range()
    {
        string url = await ArrangeAsync();

        HttpClient outsider = await AuthenticatedClientAsync("chi.le");

        using HttpRequestMessage request = new(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(0, 99);

        HttpResponseMessage response = await outsider.SendAsync(request);

        // Authorization comes before range handling. A ranged request must not be a path that
        // reaches the bytes without the membership check FR-025 requires on every retrieval.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>Seeds a clean video attachment and returns its content URL.</summary>
    private async Task<string> ArrangeAsync()
    {
        Guid conversationId = Guid.CreateVersion7();
        Guid attachmentId;

        await using (ChatDbContext context = CreateDbContext())
        {
            Guid author = await TestData.SeedEmployeeAsync(context, "an.nguyen");
            await TestData.SeedEmployeeAsync(context, "chi.le");
            await TestData.SeedMembershipAsync(context, conversationId, author);

            FixedClock clock = new();

            Attachment attachment = Attachment.Reserve(
                Guid.CreateVersion7(),
                conversationId,
                author,
                AttachmentKind.Video,
                "video/mp4",
                Payload.Length,
                durationSeconds: 30,
                "clip.mp4",
                clock);

            attachment.MarkClean(Payload.Length, posterObjectKey: null, clock);
            attachment.ClearDomainEvents();

            context.Attachments.Add(attachment);
            await context.SaveChangesAsync();

            attachmentId = attachment.Id;

            using MemoryStream content = new(Payload);

            await Stack.CreateMinioClient().PutObjectAsync(new PutObjectArgs()
                .WithBucket(StackFixture.AttachmentsBucket)
                .WithObject(attachment.ObjectKey)
                .WithStreamData(content)
                .WithObjectSize(content.Length)
                .WithContentType("video/mp4"));
        }

        return $"/api/v1/attachments/{attachmentId}/content";
    }
}
