namespace InternalChat.Api.Contracts;

/// <summary>
/// <c>POST /conversations/{conversationId}/attachments</c> body
/// (<c>RequestUploadRequest</c> in openapi.yaml).
/// </summary>
/// <remarks>
/// Every field here is a <em>claim</em>, not a measurement — the server has no bytes yet. That is
/// the point: FR-023 requires rejection "before upload with the limit stated", which can only be
/// done against what the client declares. The claim is re-checked against reality at promotion
/// time (T152), so a client that lies gains a quarantine object and a refusal, never a stored file.
/// </remarks>
/// <param name="Kind">
/// <c>image</c> or <c>video</c>. Decides which size ceiling and which content-type allow-list
/// applies, so it is validated before anything else.
/// </param>
/// <param name="ContentType">The media type. Compared case-insensitively, parameters ignored.</param>
/// <param name="ByteSize">The declared size, in bytes.</param>
/// <param name="DurationSeconds">
/// Required for a video and ignored for an image. Nullable in the contract because images send
/// nothing; a null on a video is a refusal, not a skipped check.
/// </param>
/// <param name="FileName">The uploader's file name, for display and for the download's filename hint.</param>
public sealed record RequestUploadRequest(
    string Kind,
    string ContentType,
    long ByteSize,
    int? DurationSeconds,
    string FileName);

/// <summary>
/// <c>201</c> body of the upload reservation (<c>UploadTicket</c> in openapi.yaml).
/// </summary>
/// <param name="AttachmentId">
/// The id to quote back in <c>SendMessageRequest.attachmentIds</c> once the bytes are uploaded.
/// </param>
/// <param name="UploadUrl">
/// A one-time quarantine upload location. <b>Not a retrieval address</b>, and deliberately so: it
/// addresses the quarantine bucket, from which nothing is ever served. Holding it grants the right
/// to write bytes nobody can read.
/// </param>
/// <param name="ExpiresAt">When the ticket stops being redeemable.</param>
public sealed record UploadTicketResponse(
    Guid AttachmentId,
    string UploadUrl,
    DateTimeOffset ExpiresAt);

/// <summary>
/// Attachment metadata (<c>Attachment</c> in openapi.yaml).
/// </summary>
/// <remarks>
/// <b><see cref="ContentUrl"/> is a route, never a capability.</b> It is present only when the scan
/// verdict is clean, and following it re-checks membership on every request (FR-025). It is not a
/// signed or time-limited URL, because a URL that carries its own authorization is exactly what
/// FR-025 rules out — "regardless of how the retrieval address was obtained".
/// </remarks>
/// <param name="ScanStatus">
/// <c>pending</c>, <c>clean</c>, <c>infected</c>, or <c>failed</c>. Sent so a client can render
/// "scanning…" rather than a broken image, which is the difference between a considered wait and an
/// apparent bug.
/// </param>
/// <param name="ContentUrl"><c>null</c> unless <paramref name="ScanStatus"/> is <c>clean</c>.</param>
/// <param name="PosterUrl">The video poster frame, when one has been extracted (research.md D8).</param>
public sealed record AttachmentResponse(
    Guid Id,
    string Kind,
    string ContentType,
    long ByteSize,
    int? DurationSeconds,
    string FileName,
    string ScanStatus,
    string? ContentUrl,
    string? PosterUrl);

/// <summary>The <c>kind</c> spellings the contract declares. Lowercase, not the C# member names.</summary>
/// <remarks>
/// Constants rather than a serializer convention, for the same reason <c>ConversationKinds</c>
/// exists: a client comparing against <c>Image</c> when the API emits <c>image</c> fails in a
/// branch nobody tests, and the failure looks like a rendering bug rather than a contract break.
/// </remarks>
public static class AttachmentKinds
{
    /// <summary>A still image.</summary>
    public const string Image = "image";

    /// <summary>A short recording.</summary>
    public const string Video = "video";
}

/// <summary>The <c>scanStatus</c> spellings the contract declares.</summary>
public static class ScanStatuses
{
    /// <summary>Uploaded, awaiting a verdict. Not retrievable.</summary>
    public const string Pending = "pending";

    /// <summary>Scanned and clear. Retrievable, subject to membership and message state.</summary>
    public const string Clean = "clean";

    /// <summary>Malicious. Bytes purged; the row retained for audit.</summary>
    public const string Infected = "infected";

    /// <summary>The scan could not complete. Not retrievable, and retryable.</summary>
    public const string Failed = "failed";
}
