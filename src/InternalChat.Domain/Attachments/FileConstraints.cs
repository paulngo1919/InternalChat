using System.Globalization;

namespace InternalChat.Domain.Attachments;

/// <summary>What an attachment is, which decides every limit that applies to it.</summary>
public enum AttachmentKind
{
    /// <summary>A still image, previewed inline (FR-021).</summary>
    Image = 1,

    /// <summary>A short recording, played in place without a full download (FR-022).</summary>
    Video = 2,
}

/// <summary>
/// The per-kind size, content-type, and duration limits, enforced before any bytes transfer (FR-023).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is checked against a declaration rather than against bytes.</b> FR-023 requires
/// rejection "before upload with the limit stated". At that point the server has a client's claim
/// about the file and nothing else. The claim is therefore not trusted — it is re-checked at
/// promotion time against what actually landed in quarantine (T152) — but checking it early is what
/// stops a 600 MB upload that was always going to be refused from consuming the link first.
/// </para>
/// <para>
/// <b>The allow-lists are deliberately narrower than "things browsers can render".</b>
/// <c>image/svg+xml</c> is the one worth naming: an SVG is a document with script, so a stored SVG
/// served from our origin is stored XSS, and no amount of ClamAV scanning changes that — ClamAV
/// looks for malware signatures, not for an <c>onload</c> attribute. <c>video/quicktime</c> is
/// excluded for a duller reason: a <c>.mov</c> is often H.264 and often is not, and research.md D8
/// accepts only containers that are reliably browser-playable because the alternative is
/// transcoding on the application host.
/// </para>
/// <para>
/// Not a value object: there is no per-attachment instance of "the constraints", there is one rule
/// set for the deployment. A static type is what that is.
/// </para>
/// </remarks>
public static class FileConstraints
{
    /// <summary>Largest permitted image, in bytes (25 MB).</summary>
    /// <remarks>Matches the CHECK constraint in <c>data-model.md</c>; both exist deliberately.</remarks>
    public const long MaximumImageBytes = 25L * 1024 * 1024;

    /// <summary>Largest permitted video, in bytes (500 MB).</summary>
    public const long MaximumVideoBytes = 500L * 1024 * 1024;

    /// <summary>Longest permitted video, in seconds (10 minutes).</summary>
    public const int MaximumVideoDurationSeconds = 600;

    private static readonly HashSet<string> ImageContentTypes =
        new(StringComparer.OrdinalIgnoreCase) { "image/png", "image/jpeg", "image/gif", "image/webp" };

    private static readonly HashSet<string> VideoContentTypes =
        new(StringComparer.OrdinalIgnoreCase) { "video/mp4", "video/webm" };

    /// <summary>
    /// Video codecs a browser can play without transcoding (research.md D8).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The container is not the codec, and that gap is the whole reason this list exists
    /// separately.</b> A file can be a perfectly valid <c>video/mp4</c> carrying H.265 or AV1, which
    /// several browsers will not play — the person uploads it, the scan clears it, and the recipient
    /// sees a black rectangle with no explanation. The content-type allow-list cannot catch that,
    /// because the content type is honest.
    /// </para>
    /// <para>
    /// Checked after upload rather than before it, unavoidably: the codec is in the bitstream, not
    /// in anything a client declares. That makes it the one constraint FR-023 cannot enforce
    /// "before upload" — the scan consumer probes the stored object and refuses it there
    /// (T175).
    /// </para>
    /// </remarks>
    private static readonly HashSet<string> VideoCodecs =
        new(StringComparer.OrdinalIgnoreCase) { "h264", "avc1", "vp9", "vp09", "vp8", "av1" };

    /// <summary>
    /// Audio codecs a browser can play, plus the absence of audio.
    /// </summary>
    /// <remarks>
    /// A silent screen recording is the single most common video anyone posts in a chat tool, so an
    /// allow-list that required an audio stream would reject the majority case.
    /// </remarks>
    private static readonly HashSet<string> AudioCodecs =
        new(StringComparer.OrdinalIgnoreCase) { "aac", "mp4a", "opus", "vorbis" };

    /// <summary>The size ceiling for a kind, so a client can state it before an upload starts.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is not a declared value.</exception>
    public static long MaximumBytesFor(AttachmentKind kind) => kind switch
    {
        AttachmentKind.Image => MaximumImageBytes,
        AttachmentKind.Video => MaximumVideoBytes,
        _ => throw UnknownKind(kind),
    };

    /// <summary>The content types permitted for a kind.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is not a declared value.</exception>
    public static IReadOnlyCollection<string> AllowedContentTypesFor(AttachmentKind kind) => kind switch
    {
        AttachmentKind.Image => ImageContentTypes,
        AttachmentKind.Video => VideoContentTypes,
        _ => throw UnknownKind(kind),
    };

    /// <summary>
    /// Checks a declared upload against every limit for its kind, throwing on the first violation.
    /// </summary>
    /// <param name="kind">What the client says it is uploading.</param>
    /// <param name="contentType">The declared media type; parameters and casing are ignored.</param>
    /// <param name="byteSize">The declared size. Must be positive.</param>
    /// <param name="durationSeconds">
    /// Required and positive for <see cref="AttachmentKind.Video"/>; ignored otherwise.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="kind"/> is not a declared value, or <paramref name="byteSize"/> is not positive.
    /// </exception>
    /// <exception cref="UnsupportedContentTypeException">The type is not on the allow-list (415).</exception>
    /// <exception cref="FileTooLargeException">The declared size exceeds the limit (413).</exception>
    /// <exception cref="VideoTooLongException">The declared duration is missing or over the limit.</exception>
    public static void Validate(
        AttachmentKind kind,
        string contentType,
        long byteSize,
        int? durationSeconds)
    {
        // Kind first: every other limit is read from it, so validating anything before it would be
        // validating against whichever list a bad enum happened to land on.
        IReadOnlyCollection<string> allowed = AllowedContentTypesFor(kind);

        if (byteSize <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(byteSize), byteSize, "An attachment must declare a positive byte size.");
        }

        if (!IsAllowed(contentType, allowed))
        {
            throw new UnsupportedContentTypeException(kind, contentType, allowed);
        }

        long limit = MaximumBytesFor(kind);

        if (byteSize > limit)
        {
            throw new FileTooLargeException(kind, byteSize, limit);
        }

        if (kind is AttachmentKind.Video)
        {
            // A null duration is a violation rather than a skip. PostgreSQL's CHECK permits NULL
            // because images legitimately have none, so if this returned early on null, any client
            // that omitted the field would bypass the 600-second rule end to end.
            if (durationSeconds is not { } duration || duration <= 0
                || duration > MaximumVideoDurationSeconds)
            {
                throw new VideoTooLongException(durationSeconds, MaximumVideoDurationSeconds);
            }
        }
    }

    /// <summary>
    /// Reduces a declared media type to its essence — lowercased type/subtype, parameters dropped.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Per RFC 9110 the type and subtype are case-insensitive and parameters are not part of the
    /// media type's identity, so <c>image/PNG; charset=binary</c> is <c>image/png</c>. Refusing it
    /// would be a defect visible only against one particular HTTP client library.
    /// </para>
    /// <para>
    /// Callers persist the essence rather than the raw header so one type has one spelling in the
    /// database. Two rows reading <c>image/png</c> and <c>IMAGE/PNG</c> are the same type, and any
    /// later query that groups or filters on it would disagree.
    /// </para>
    /// </remarks>
    /// <returns>The essence, or <c>null</c> when the input is not a well-formed media type.</returns>
    public static string? NormalizeContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return null;
        }

        ReadOnlySpan<char> essence = contentType.AsSpan();
        int separator = essence.IndexOf(';');

        if (separator >= 0)
        {
            essence = essence[..separator];
        }

        essence = essence.Trim();

        // A media type without a slash is not a media type. Checked explicitly so "png" cannot
        // match by some future allow-list entry being stored unqualified.
        return essence.IndexOf('/') <= 0 ? null : essence.ToString().ToLowerInvariant();
    }

    private static bool IsAllowed(string? contentType, IReadOnlyCollection<string> allowed) =>
        NormalizeContentType(contentType) is { } essence && allowed.Contains(essence);

    /// <summary>The video codecs accepted, for a client that wants to state them up front.</summary>
    public static IReadOnlyCollection<string> AllowedVideoCodecs => VideoCodecs;

    /// <summary>The audio codecs accepted. A video with no audio stream is also accepted.</summary>
    public static IReadOnlyCollection<string> AllowedAudioCodecs => AudioCodecs;

    /// <summary>
    /// Checks the codecs actually found in an uploaded video (research.md D8).
    /// </summary>
    /// <param name="videoCodec">The video stream's codec name, as reported by a probe.</param>
    /// <param name="audioCodec">The audio stream's codec name, or <c>null</c> when there is none.</param>
    /// <param name="durationSeconds">
    /// The measured duration. Re-checked here because the declared one was a client's claim, and
    /// this is the first point at which the real value is known.
    /// </param>
    /// <exception cref="UnsupportedCodecException">A codec is not browser-playable.</exception>
    /// <exception cref="VideoTooLongException">The measured duration exceeds the limit.</exception>
    public static void ValidateProbedVideo(
        string? videoCodec,
        string? audioCodec,
        int? durationSeconds)
    {
        // A file with no detectable video stream is not a video, whatever it was declared as.
        if (string.IsNullOrWhiteSpace(videoCodec) || !VideoCodecs.Contains(videoCodec.Trim()))
        {
            throw new UnsupportedCodecException(videoCodec, VideoCodecs);
        }

        // Null means no audio stream, which is fine — a silent screen recording is the common case.
        // A present-but-unplayable audio codec is not: the video would play with silence, or not at
        // all, depending on the browser.
        if (!string.IsNullOrWhiteSpace(audioCodec) && !AudioCodecs.Contains(audioCodec.Trim()))
        {
            throw new UnsupportedCodecException(audioCodec, AudioCodecs);
        }

        if (durationSeconds is not { } duration || duration <= 0
            || duration > MaximumVideoDurationSeconds)
        {
            throw new VideoTooLongException(durationSeconds, MaximumVideoDurationSeconds);
        }
    }

    private static ArgumentOutOfRangeException UnknownKind(AttachmentKind kind) =>
        new(nameof(kind), kind, $"'{kind}' is not a known attachment kind.");
}

/// <summary>
/// Thrown when a declared upload exceeds the size limit for its kind (FR-023). Maps to 413.
/// </summary>
public sealed class FileTooLargeException : InvalidOperationException
{
    /// <summary>Creates the exception, naming the limit so the sender need not guess it.</summary>
    public FileTooLargeException(AttachmentKind kind, long declaredBytes, long limitBytes)
        : base(string.Create(
            CultureInfo.InvariantCulture,
            $"A {kind.ToString().ToLowerInvariant()} attachment is at most {limitBytes} bytes; this one declares {declaredBytes}."))
    {
        Kind = kind;
        DeclaredBytes = declaredBytes;
        LimitBytes = limitBytes;
    }

    /// <summary>Creates the exception.</summary>
    public FileTooLargeException()
    {
    }

    /// <summary>Creates the exception.</summary>
    public FileTooLargeException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public FileTooLargeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>What the client said it was uploading.</summary>
    public AttachmentKind Kind { get; }

    /// <summary>The size the client declared.</summary>
    public long DeclaredBytes { get; }

    /// <summary>The ceiling for this kind. Surfaced so the refusal states the limit (FR-023).</summary>
    public long LimitBytes { get; }
}

/// <summary>
/// Thrown when a declared content type is not on the allow-list for its kind (FR-023). Maps to 415.
/// </summary>
public sealed class UnsupportedContentTypeException : InvalidOperationException
{
    /// <summary>Creates the exception, naming what is permitted instead.</summary>
    public UnsupportedContentTypeException(
        AttachmentKind kind,
        string? contentType,
        IEnumerable<string> allowed)
        : base($"'{contentType}' is not an accepted {kind.ToString().ToLowerInvariant()} type. Accepted: {string.Join(", ", allowed ?? [])}.")
    {
        Kind = kind;
        ContentType = contentType;
        Allowed = [.. allowed ?? []];
    }

    /// <summary>Creates the exception.</summary>
    public UnsupportedContentTypeException()
    {
    }

    /// <summary>Creates the exception.</summary>
    public UnsupportedContentTypeException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public UnsupportedContentTypeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>What the client said it was uploading.</summary>
    public AttachmentKind Kind { get; }

    /// <summary>The type that was refused. <c>null</c> or blank when none was declared.</summary>
    public string? ContentType { get; }

    /// <summary>The types that would have been accepted.</summary>
    public IReadOnlyList<string> Allowed { get; } = [];
}

/// <summary>
/// Thrown when a video declares no duration, or one over the limit (FR-023).
/// </summary>
/// <remarks>
/// A missing duration throws this rather than a separate "duration required" exception, because
/// from the caller's side both are the same failure: the upload cannot be shown to be within the
/// 600-second limit, so it is refused with that limit stated.
/// </remarks>
public sealed class VideoTooLongException : InvalidOperationException
{
    /// <summary>Creates the exception.</summary>
    public VideoTooLongException(int? declaredSeconds, int limitSeconds)
        : base(declaredSeconds is { } declared
            ? string.Create(CultureInfo.InvariantCulture, $"A video is at most {limitSeconds} seconds; this one declares {declared}.")
            : string.Create(CultureInfo.InvariantCulture, $"A video must declare its duration, which is at most {limitSeconds} seconds."))
    {
        DeclaredSeconds = declaredSeconds;
        LimitSeconds = limitSeconds;
    }

    /// <summary>Creates the exception.</summary>
    public VideoTooLongException()
    {
    }

    /// <summary>Creates the exception.</summary>
    public VideoTooLongException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public VideoTooLongException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>What the client declared, or <c>null</c> when it declared nothing.</summary>
    public int? DeclaredSeconds { get; }

    /// <summary>The ceiling. Surfaced so the refusal states the limit (FR-023).</summary>
    public int LimitSeconds { get; }
}

/// <summary>
/// Thrown when an uploaded video uses a codec browsers cannot play (research.md D8).
/// </summary>
/// <remarks>
/// Separate from <see cref="UnsupportedContentTypeException"/> because it is a genuinely different
/// refusal, reached at a different time. The content type was acceptable — the file really is an
/// MP4 — and only probing the bitstream revealed that nothing can play it. Merging the two would
/// produce a 415 telling someone their MP4 is not an accepted type, which is both wrong and
/// impossible to act on.
/// </remarks>
public sealed class UnsupportedCodecException : InvalidOperationException
{
    /// <summary>Creates the exception, naming what would have been accepted.</summary>
    public UnsupportedCodecException(string? codec, IEnumerable<string> allowed)
        : base($"'{codec}' is not a codec browsers can play without transcoding. Accepted: {string.Join(", ", allowed ?? [])}.")
    {
        Codec = codec;
        Allowed = [.. allowed ?? []];
    }

    /// <summary>Creates the exception.</summary>
    public UnsupportedCodecException()
    {
    }

    /// <summary>Creates the exception.</summary>
    public UnsupportedCodecException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public UnsupportedCodecException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>What the probe found. <c>null</c> when no stream was detected at all.</summary>
    public string? Codec { get; }

    /// <summary>What would have been accepted.</summary>
    public IReadOnlyList<string> Allowed { get; } = [];
}
