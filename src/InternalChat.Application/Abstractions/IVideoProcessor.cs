namespace InternalChat.Application.Abstractions;

/// <summary>What a probe found in an uploaded video.</summary>
/// <param name="VideoCodec">The video stream's codec, or <c>null</c> when there is no video stream.</param>
/// <param name="AudioCodec">The audio stream's codec, or <c>null</c> when the video is silent.</param>
/// <param name="DurationSeconds">Measured duration, rounded up. <c>null</c> when undeterminable.</param>
/// <param name="FastStart">
/// Whether the <c>moov</c> atom is already at the front of the file.
/// </param>
/// <remarks>
/// <para>
/// <b><see cref="FastStart"/> is what "plays without downloading the whole file" actually means for
/// MP4</b> (research.md D8, FR-022). The <c>moov</c> atom holds the index a player needs before it
/// can render a single frame; most encoders write it last, so a browser fetching a 400 MB file
/// must receive all 400 MB before playback begins — range requests do not help, because the player
/// does not know what to ask for yet.
/// </para>
/// <para>
/// Irrelevant for WebM, whose structure is seekable by design. Reported as <c>true</c> for it so
/// callers need no format branch.
/// </para>
/// </remarks>
public sealed record VideoProbe(
    string? VideoCodec,
    string? AudioCodec,
    int? DurationSeconds,
    bool FastStart);

/// <summary>
/// The one thing this platform uses ffmpeg for (research.md D8).
/// </summary>
/// <remarks>
/// <para>
/// <b>Deliberately not a transcoder.</b> D8: transcoding 500 MB videos would be the single largest
/// CPU consumer on the application host, competing directly with the messaging budget on a machine
/// the constitution limits to one. The format allow-list moves that cost to the uploader's machine,
/// where it is free. This interface is scoped to the two operations that cannot be moved there:
/// reading what was actually uploaded, and relocating an index without re-encoding anything.
/// </para>
/// <para>
/// Both operations are stream-in, stream-out and neither touches the object store — the caller
/// decides which bucket the bytes came from and where they go. That keeps the scan consumer's
/// ordering (scan, probe, promote) visible in one place rather than split across two components.
/// </para>
/// </remarks>
public interface IVideoProcessor
{
    /// <summary>
    /// Reads codecs, duration, and <c>moov</c> placement without decoding the video.
    /// </summary>
    /// <remarks>
    /// Returns a probe with null fields rather than throwing when the file is unreadable. A corrupt
    /// upload is an ordinary outcome the caller turns into a refusal, not an exceptional one.
    /// </remarks>
    Task<VideoProbe> ProbeAsync(Stream content, CancellationToken cancellationToken = default);

    /// <summary>
    /// Extracts a single frame as a poster image.
    /// </summary>
    /// <returns>A JPEG stream, or <c>null</c> when no frame could be extracted.</returns>
    /// <remarks>
    /// Null rather than an exception, for the same reason: a video with no decodable frame still
    /// gets promoted if it is clean, and the player simply falls back to its own first frame. A
    /// missing poster is a cosmetic loss, never a reason to withhold a file someone shared.
    /// </remarks>
    Task<Stream?> ExtractPosterAsync(Stream content, CancellationToken cancellationToken = default);

    /// <summary>
    /// Rewrites an MP4 with its <c>moov</c> atom at the front, copying streams rather than re-encoding.
    /// </summary>
    /// <returns>The remuxed stream, or <c>null</c> when the remux failed.</returns>
    /// <remarks>
    /// <c>-c copy -movflags +faststart</c>: the bitstream is untouched, so this is an I/O cost
    /// rather than a CPU one, and the result is bit-identical in every way that matters to a
    /// player. Null on failure means the caller promotes the original — a video that starts slowly
    /// is better than one nobody can see.
    /// </remarks>
    Task<Stream?> RelocateMoovAtomAsync(Stream content, CancellationToken cancellationToken = default);
}
