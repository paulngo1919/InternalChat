using InternalChat.Domain.Attachments;

namespace InternalChat.UnitTests.Domain;

/// <summary>
/// T171 — video duration, size, and the browser-playable codec allow-list (FR-023, research.md D8).
/// </summary>
/// <remarks>
/// <para>
/// <b>The codec check is the one constraint FR-023 cannot enforce before upload, and these tests
/// exist to make that boundary explicit.</b> Size and content type come from what a client
/// declares, so they are refused in a round trip. A codec lives in the bitstream: a file can be an
/// entirely honest <c>video/mp4</c> carrying H.265, and nothing short of probing the bytes reveals
/// it. That refusal necessarily happens after the transfer, in the scan consumer.
/// </para>
/// <para>
/// The failure it prevents is the quiet kind: without it the upload succeeds, the scan clears, and
/// the recipient sees a black rectangle with no error to report.
/// </para>
/// </remarks>
public sealed class VideoConstraintsTests : UnitTestBase
{
    [Theory]
    [InlineData("h264", "aac")]
    [InlineData("avc1", "mp4a")]
    [InlineData("vp9", "opus")]
    [InlineData("vp8", "vorbis")]
    [InlineData("av1", "opus")]
    public void A_browser_playable_combination_is_accepted(string video, string audio) =>
        FileConstraints.ValidateProbedVideo(video, audio, durationSeconds: 60);

    [Fact]
    public void A_silent_video_is_accepted()
    {
        // The single most common video anyone posts in a chat tool is a screen recording with no
        // audio. An allow-list that required an audio stream would reject the majority case.
        FileConstraints.ValidateProbedVideo("h264", audioCodec: null, durationSeconds: 60);
        FileConstraints.ValidateProbedVideo("h264", audioCodec: "", durationSeconds: 60);
    }

    [Theory]
    [InlineData("hevc")]
    [InlineData("h265")]
    [InlineData("mpeg4")]
    [InlineData("wmv3")]
    [InlineData("theora")]
    public void A_video_codec_browsers_cannot_play_is_refused(string codec)
    {
        UnsupportedCodecException refused = Assert.Throws<UnsupportedCodecException>(() =>
            FileConstraints.ValidateProbedVideo(codec, "aac", 60));

        Assert.Equal(codec, refused.Codec);

        // Names what would have worked. "Unsupported codec" on its own is unactionable — the person
        // cannot tell whether to re-export, re-encode, or give up.
        Assert.Contains("h264", refused.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("ac3")]
    [InlineData("dts")]
    [InlineData("pcm_s16le")]
    public void An_audio_codec_browsers_cannot_play_is_refused(string codec)
    {
        // Refused rather than stripped. A video that plays silently because its audio track was
        // unplayable is worse than a clear refusal: nobody can tell whether the recording had sound.
        Assert.Throws<UnsupportedCodecException>(() =>
            FileConstraints.ValidateProbedVideo("h264", codec, 60));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_file_with_no_detectable_video_stream_is_refused(string? codec)
    {
        // An audio file renamed to .mp4, or a corrupt upload. Either way it is not a video, whatever
        // the container and the declared type said.
        Assert.Throws<UnsupportedCodecException>(() =>
            FileConstraints.ValidateProbedVideo(codec, "aac", 60));
    }

    [Fact]
    public void A_codec_name_is_matched_case_insensitively()
    {
        // ffprobe reports lowercase, but the field is free text from a third-party tool and a
        // version bump changing its casing should not start refusing every upload.
        FileConstraints.ValidateProbedVideo("H264", "AAC", 60);
    }

    [Fact]
    public void The_measured_duration_is_rechecked_at_probe_time()
    {
        FileConstraints.ValidateProbedVideo("h264", "aac", FileConstraints.MaximumVideoDurationSeconds);

        // The declared duration got the upload past the gate; this is the measured one. A client
        // that declared 60 seconds and uploaded an hour is caught here and nowhere else.
        VideoTooLongException tooLong = Assert.Throws<VideoTooLongException>(() =>
            FileConstraints.ValidateProbedVideo(
                "h264", "aac", FileConstraints.MaximumVideoDurationSeconds + 1));

        Assert.Equal(FileConstraints.MaximumVideoDurationSeconds, tooLong.LimitSeconds);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_video_with_no_measurable_duration_is_refused(int? duration)
    {
        // A probe that cannot determine duration usually means a truncated file. Accepting it would
        // promote something no player can seek through.
        Assert.Throws<VideoTooLongException>(() =>
            FileConstraints.ValidateProbedVideo("h264", "aac", duration));
    }

    [Fact]
    public void The_accepted_codecs_are_retrievable_without_provoking_a_refusal()
    {
        Assert.Contains("h264", FileConstraints.AllowedVideoCodecs);
        Assert.Contains("vp9", FileConstraints.AllowedVideoCodecs);
        Assert.Contains("aac", FileConstraints.AllowedAudioCodecs);
        Assert.Contains("opus", FileConstraints.AllowedAudioCodecs);

        // The two lists are disjoint, so a mix-up at a call site — passing the audio codec as the
        // video one — is refused rather than silently accepted.
        Assert.Empty(FileConstraints.AllowedVideoCodecs
            .Intersect(FileConstraints.AllowedAudioCodecs, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_declared_size_and_duration_limits_still_apply_before_upload()
    {
        // The pre-upload gate is unchanged by any of the above — this is the half of FR-023 that
        // does happen before a byte transfers, and the codec check does not replace it.
        Assert.Throws<FileTooLargeException>(() =>
            FileConstraints.Validate(
                AttachmentKind.Video, "video/mp4", FileConstraints.MaximumVideoBytes + 1, 60));

        Assert.Throws<VideoTooLongException>(() =>
            FileConstraints.Validate(AttachmentKind.Video, "video/mp4", 1024, 601));

        Assert.Throws<UnsupportedContentTypeException>(() =>
            FileConstraints.Validate(AttachmentKind.Video, "video/quicktime", 1024, 60));
    }
}
