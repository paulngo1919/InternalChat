using InternalChat.Domain.Attachments;

namespace InternalChat.UnitTests.Domain;

/// <summary>
/// T142 — per-kind size and content-type constraints (FR-023).
/// </summary>
/// <remarks>
/// <para>
/// data-model.md's validation summary lists these as invariants that MUST be unit-tested: "Attachment
/// size and content type are within the per-kind allow-list before upload begins (FR-023)" and
/// "Video duration is at most 600 seconds".
/// </para>
/// <para>
/// <b>Why the constraints live in Domain rather than in the endpoint.</b> FR-023 requires rejection
/// <em>before</em> upload, which means the check runs against a declared size the client sends, not
/// against bytes the server has received. That declaration is also re-checked at promotion time
/// against what actually landed in quarantine — two call sites, one rule. A rule that lives in one
/// endpoint would have to be remembered at the second.
/// </para>
/// </remarks>
public sealed class FileConstraintsTests : UnitTestBase
{
    [Fact]
    public void An_image_at_the_size_limit_is_accepted_and_one_byte_over_is_not()
    {
        FileConstraints.Validate(
            AttachmentKind.Image, "image/png", FileConstraints.MaximumImageBytes, durationSeconds: null);

        FileTooLargeException tooLarge = Assert.Throws<FileTooLargeException>(() =>
            FileConstraints.Validate(
                AttachmentKind.Image, "image/png", FileConstraints.MaximumImageBytes + 1, null));

        // FR-023: "reject violations before upload with the limit stated". A refusal that does not
        // name the limit leaves the sender guessing how much to shrink the file by.
        Assert.Equal(FileConstraints.MaximumImageBytes, tooLarge.LimitBytes);
        Assert.Contains(FileConstraints.MaximumImageBytes.ToString(), tooLarge.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_video_at_the_size_limit_is_accepted_and_one_byte_over_is_not()
    {
        FileConstraints.Validate(
            AttachmentKind.Video, "video/mp4", FileConstraints.MaximumVideoBytes, durationSeconds: 10);

        FileTooLargeException tooLarge = Assert.Throws<FileTooLargeException>(() =>
            FileConstraints.Validate(
                AttachmentKind.Video, "video/mp4", FileConstraints.MaximumVideoBytes + 1, 10));

        Assert.Equal(FileConstraints.MaximumVideoBytes, tooLarge.LimitBytes);
    }

    [Fact]
    public void The_size_limit_is_per_kind_not_global()
    {
        // The whole point of "per-kind": a 100 MB payload is a legitimate video and an absurd image.
        // One global ceiling would have to be the video one, which would let a 100 MB PNG through.
        const long hundredMegabytes = 100L * 1024 * 1024;

        Assert.Throws<FileTooLargeException>(() =>
            FileConstraints.Validate(AttachmentKind.Image, "image/png", hundredMegabytes, null));

        FileConstraints.Validate(AttachmentKind.Video, "video/mp4", hundredMegabytes, durationSeconds: 60);
    }

    [Theory]
    [InlineData("image/png")]
    [InlineData("image/jpeg")]
    [InlineData("image/gif")]
    [InlineData("image/webp")]
    public void The_allowed_image_content_types_are_accepted(string contentType) =>
        FileConstraints.Validate(AttachmentKind.Image, contentType, byteSize: 1024, durationSeconds: null);

    [Theory]
    [InlineData("video/mp4")]
    [InlineData("video/webm")]
    public void The_allowed_video_content_types_are_accepted(string contentType) =>
        FileConstraints.Validate(AttachmentKind.Video, contentType, byteSize: 1024, durationSeconds: 60);

    [Theory]
    [InlineData("image/svg+xml")]       // Scriptable. An <svg> with an onload handler is stored XSS.
    [InlineData("text/html")]           // Same reason, less subtle.
    [InlineData("application/pdf")]     // Not an image, whatever the uploader called it.
    [InlineData("image/tiff")]          // Not browser-renderable; would download rather than preview.
    [InlineData("application/octet-stream")]
    public void A_content_type_off_the_image_allow_list_is_refused(string contentType)
    {
        UnsupportedContentTypeException refused = Assert.Throws<UnsupportedContentTypeException>(() =>
            FileConstraints.Validate(AttachmentKind.Image, contentType, 1024, null));

        Assert.Equal(contentType, refused.ContentType);
        Assert.Equal(AttachmentKind.Image, refused.Kind);
    }

    [Theory]
    [InlineData("video/quicktime")]     // .mov is frequently H.264 but frequently is not enough.
    [InlineData("video/x-msvideo")]
    [InlineData("video/x-matroska")]
    [InlineData("application/octet-stream")]
    public void A_content_type_off_the_video_allow_list_is_refused(string contentType) =>
        Assert.Throws<UnsupportedContentTypeException>(() =>
            FileConstraints.Validate(AttachmentKind.Video, contentType, 1024, 60));

    [Fact]
    public void An_image_content_type_is_refused_for_a_video_and_the_reverse()
    {
        // The allow-lists are disjoint by construction. Declaring kind=video with image/png is
        // either a confused client or someone probing for a path where the kind is trusted and the
        // content type is not (or the reverse); neither deserves a stored object.
        Assert.Throws<UnsupportedContentTypeException>(() =>
            FileConstraints.Validate(AttachmentKind.Video, "image/png", 1024, 60));

        Assert.Throws<UnsupportedContentTypeException>(() =>
            FileConstraints.Validate(AttachmentKind.Image, "video/mp4", 1024, null));
    }

    [Theory]
    [InlineData("IMAGE/PNG")]
    [InlineData("Image/Png")]
    [InlineData("image/png; charset=binary")]
    [InlineData("  image/png  ")]
    public void A_content_type_is_matched_case_insensitively_and_without_parameters(string contentType) =>
        // RFC 9110: the type and subtype are case-insensitive, and parameters are not part of the
        // media type's identity. A client that appends a charset is not attacking anything, and
        // refusing it would be a bug that only shows up against one particular HTTP library.
        FileConstraints.Validate(AttachmentKind.Image, contentType, 1024, null);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-media-type")]
    public void A_missing_or_malformed_content_type_is_refused(string? contentType) =>
        Assert.Throws<UnsupportedContentTypeException>(() =>
            FileConstraints.Validate(AttachmentKind.Image, contentType!, 1024, null));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_non_positive_byte_size_is_refused(long byteSize) =>
        // Zero is not "small", it is a client that has not worked out what it is uploading. Letting
        // it reserve a ticket costs a quarantine object and a scan for nothing.
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            FileConstraints.Validate(AttachmentKind.Image, "image/png", byteSize, null));

    [Fact]
    public void A_video_at_the_duration_limit_is_accepted_and_one_second_over_is_not()
    {
        FileConstraints.Validate(
            AttachmentKind.Video, "video/mp4", 1024, FileConstraints.MaximumVideoDurationSeconds);

        VideoTooLongException tooLong = Assert.Throws<VideoTooLongException>(() =>
            FileConstraints.Validate(
                AttachmentKind.Video, "video/mp4", 1024, FileConstraints.MaximumVideoDurationSeconds + 1));

        Assert.Equal(FileConstraints.MaximumVideoDurationSeconds, tooLong.LimitSeconds);
    }

    [Fact]
    public void A_video_without_a_declared_duration_is_refused()
    {
        // Accepting a null duration would make the 600-second rule opt-in: any client that omits
        // the field skips the check entirely, and the CHECK constraint in PostgreSQL permits NULL
        // too (it is there for images). The gate has to be here.
        Assert.Throws<VideoTooLongException>(() =>
            FileConstraints.Validate(AttachmentKind.Video, "video/mp4", 1024, durationSeconds: null));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void A_non_positive_video_duration_is_refused(int durationSeconds) =>
        Assert.Throws<VideoTooLongException>(() =>
            FileConstraints.Validate(AttachmentKind.Video, "video/mp4", 1024, durationSeconds));

    [Fact]
    public void A_duration_declared_on_an_image_is_ignored_rather_than_refused()
    {
        // A client that sends durationSeconds for every upload is sloppy, not hostile, and an
        // image has no duration to violate. Refusing would turn a harmless extra field into a
        // failed upload.
        FileConstraints.Validate(AttachmentKind.Image, "image/png", 1024, durationSeconds: 42);
    }

    [Fact]
    public void An_unknown_kind_is_refused_rather_than_defaulted()
    {
        // Enum values in .NET are not closed: (AttachmentKind)99 is representable and arrives from
        // any deserializer that does not validate. Defaulting it to Image would apply the image
        // allow-list to something the caller did not call an image.
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            FileConstraints.Validate((AttachmentKind)99, "image/png", 1024, null));
    }

    [Fact]
    public void The_stated_limit_is_retrievable_without_triggering_a_violation()
    {
        // The upload UI needs the limit to show it up front, which is the other half of FR-023 —
        // "reject before upload with the limit stated" is a poor experience if the only way to
        // learn the limit is to violate it.
        Assert.Equal(FileConstraints.MaximumImageBytes, FileConstraints.MaximumBytesFor(AttachmentKind.Image));
        Assert.Equal(FileConstraints.MaximumVideoBytes, FileConstraints.MaximumBytesFor(AttachmentKind.Video));

        Assert.Contains("image/png", FileConstraints.AllowedContentTypesFor(AttachmentKind.Image));
        Assert.Contains("video/mp4", FileConstraints.AllowedContentTypesFor(AttachmentKind.Video));
        Assert.DoesNotContain("image/svg+xml", FileConstraints.AllowedContentTypesFor(AttachmentKind.Image));
    }

    [Fact]
    public void The_image_and_video_allow_lists_do_not_overlap()
    {
        // Guards the disjointness the kind/content-type cross-check above relies on. If a future
        // edit adds a type to both lists, that test would keep passing for the wrong reason.
        Assert.Empty(FileConstraints.AllowedContentTypesFor(AttachmentKind.Image)
            .Intersect(FileConstraints.AllowedContentTypesFor(AttachmentKind.Video), StringComparer.Ordinal));
    }
}
