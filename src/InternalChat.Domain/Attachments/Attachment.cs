using System.Globalization;
using InternalChat.Domain.Common;

namespace InternalChat.Domain.Attachments;

/// <summary>
/// Where an upload stands in the malware scan that gates its retrievability (FR-024).
/// </summary>
/// <remarks>
/// <para>
/// Transitions, per data-model.md: <c>pending → clean</c>, <c>pending → infected</c>,
/// <c>pending → failed</c>, and <c>failed → pending</c> on retry. <see cref="Clean"/> and
/// <see cref="Infected"/> are terminal.
/// </para>
/// <para>
/// <b><see cref="Failed"/> is not <see cref="Infected"/>, and the difference is the whole point of
/// having four states.</b> Failed means the scanner could not reach a verdict — ClamAV was down,
/// the object was unreadable, the definitions were stale. Collapsing it into <c>infected</c> would
/// tell an employee their holiday photo is malware because a container restarted; collapsing it
/// into <c>clean</c> would serve unscanned bytes, which is the one thing FR-024 forbids. So it is
/// its own state: not retrievable, and retryable.
/// </para>
/// </remarks>
public enum ScanVerdict
{
    /// <summary>Uploaded, awaiting a verdict. Not retrievable.</summary>
    Pending = 1,

    /// <summary>Scanned and clear. Retrievable, subject to membership and message state. Terminal.</summary>
    Clean = 2,

    /// <summary>Malicious. Bytes are purged; the row is retained for audit. Terminal.</summary>
    Infected = 3,

    /// <summary>The scan could not complete. Not retrievable, and retryable.</summary>
    Failed = 4,
}

/// <summary>
/// One file posted to a conversation.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="ConversationId"/> is denormalized on purpose</b> (data-model.md): the download
/// authorization check must not have to join through <c>message</c>, because at upload time there
/// is no message yet — the ticket is reserved before the send — and because the check runs on the
/// hot path for every image a conversation renders.
/// </para>
/// <para>
/// <b>There is no method here that returns a retrieval URL</b>, mirroring the same absence in
/// <c>IObjectStore</c>. FR-025 requires membership to be checked on every retrieval "regardless of
/// how the retrieval address was obtained", and any URL this type could hand back would be a bearer
/// capability. Retrievability is a question (<see cref="IsRetrievable"/>), answered per request,
/// never a token.
/// </para>
/// </remarks>
public sealed class Attachment : Entity<Guid>
{
    private Attachment(
        Guid id,
        Guid conversationId,
        Guid uploadedBy,
        AttachmentKind kind,
        string contentType,
        long byteSize,
        int? durationSeconds,
        string fileName,
        DateTimeOffset createdAt)
        : base(id)
    {
        ConversationId = conversationId;
        UploadedBy = uploadedBy;
        Kind = kind;
        ContentType = contentType;
        ByteSize = byteSize;
        DurationSeconds = durationSeconds;
        FileName = fileName;
        CreatedAt = createdAt;
    }

    /// <summary>Longest permitted file name, in characters.</summary>
    /// <remarks>
    /// The name is display metadata, never a path: <see cref="ObjectKey"/> is derived from
    /// identifiers alone, so a name containing <c>../</c> reaches no storage layer that would
    /// interpret it. The cap exists to bound the column, not to sanitize a path.
    /// </remarks>
    public const int MaximumFileNameLength = 255;

    /// <summary>The conversation this was posted to. The authorization subject (FR-025).</summary>
    public Guid ConversationId { get; private set; }

    /// <summary>
    /// The message carrying it, or <c>null</c> until the send completes.
    /// </summary>
    /// <remarks>
    /// Null is the normal early state, not an error: a ticket is reserved, bytes are uploaded, and
    /// only then does the client send the message that references it. An attachment that never gets
    /// a message is an abandoned upload, which the retention sweep collects.
    /// </remarks>
    public Guid? MessageId { get; private set; }

    /// <summary>
    /// The carrying message's <c>SentAt</c> — its partition key — or <c>null</c> while unattached.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Carried for the same reason <c>MessageSent</c> carries it.</b> <c>message</c> is
    /// <c>PARTITION BY RANGE (sent_at)</c>, so its primary key is <c>(id, sent_at)</c> and an id on
    /// its own does not locate a row — reading it means touching all twelve months of partitions.
    /// The download path has to load the message on every request to honour FR-027 ("stop serving
    /// when its message is deleted"), and that is the hot path for every image a conversation
    /// renders.
    /// </para>
    /// <para>
    /// It is also what makes the foreign key expressible at all: PostgreSQL requires a reference to
    /// match a unique constraint, and on a partitioned table the only one available is the
    /// composite. <c>data-model.md</c> lists <c>message_id</c> alone as the FK, which cannot be
    /// created against this schema.
    /// </para>
    /// </remarks>
    public DateTimeOffset? MessageSentAt { get; private set; }

    /// <summary>Who uploaded it.</summary>
    public Guid UploadedBy { get; private set; }

    /// <summary>Image or video. Decides every limit that applied at reservation.</summary>
    public AttachmentKind Kind { get; private set; }

    /// <summary>The normalized media type — lowercased, parameters stripped.</summary>
    public string ContentType { get; private set; }

    /// <summary>Size in bytes, as declared and then confirmed at promotion.</summary>
    public long ByteSize { get; private set; }

    /// <summary>Duration in seconds for a video; <c>null</c> for an image.</summary>
    public int? DurationSeconds { get; private set; }

    /// <summary>The uploader's file name, for display and for the download's filename hint.</summary>
    public string FileName { get; private set; }

    /// <summary>
    /// The storage key, <c>{conversationId}/{id}</c> (data-model.md).
    /// </summary>
    /// <remarks>
    /// Derived rather than stored so it cannot drift from the identifiers it is built from. It is
    /// not a security control — the key is unguessable only incidentally, and the membership check
    /// is what actually protects the bytes.
    /// </remarks>
    public string ObjectKey => string.Create(CultureInfo.InvariantCulture, $"{ConversationId}/{Id}");

    /// <summary>The poster frame's key for a video, once extracted (research.md D8); else <c>null</c>.</summary>
    public string? PosterObjectKey { get; private set; }

    /// <summary>Where the scan stands. Gates retrievability (FR-024).</summary>
    public ScanVerdict ScanStatus { get; private set; } = ScanVerdict.Pending;

    /// <summary>When the verdict was reached, or <c>null</c> while pending.</summary>
    public DateTimeOffset? ScannedAt { get; private set; }

    /// <summary>When the ticket was reserved.</summary>
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>
    /// Reserves an upload after checking every limit for the kind (FR-023).
    /// </summary>
    /// <remarks>
    /// Validation happens here rather than in the calling use case so that every path which ever
    /// creates an attachment — the endpoint, the seeder, a future import — goes through the same
    /// gate instead of each one having to remember.
    /// </remarks>
    /// <exception cref="UnsupportedContentTypeException">The type is not on the allow-list.</exception>
    /// <exception cref="FileTooLargeException">The declared size exceeds the limit for the kind.</exception>
    /// <exception cref="VideoTooLongException">A video declares no duration, or one over the limit.</exception>
    public static Attachment Reserve(
        Guid id,
        Guid conversationId,
        Guid uploadedBy,
        AttachmentKind kind,
        string contentType,
        long byteSize,
        int? durationSeconds,
        string fileName,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        FileConstraints.Validate(kind, contentType, byteSize, durationSeconds);

        // Validate already proved this parses; the null-forgiving read is the normalized essence.
        string normalizedType = FileConstraints.NormalizeContentType(contentType)!;

        return new Attachment(
            id,
            conversationId,
            uploadedBy,
            kind,
            normalizedType,
            byteSize,
            kind is AttachmentKind.Video ? durationSeconds : null,
            NormalizeFileName(fileName),
            clock.UtcNow);
    }

    /// <summary>
    /// Binds this to the message that carries it, once the send completes.
    /// </summary>
    /// <remarks>
    /// Write-once. Re-pointing an attachment at a different message would move a file between
    /// conversations without any membership check noticing, since
    /// <see cref="ConversationId"/> — the authorization subject — would not change with it.
    /// </remarks>
    /// <param name="messageId">The message that now carries this.</param>
    /// <param name="messageSentAt">
    /// That message's <c>SentAt</c> — its partition key, without which the row cannot be located.
    /// </param>
    /// <exception cref="InvalidOperationException">It is already attached to a different message.</exception>
    public void AttachTo(Guid messageId, DateTimeOffset messageSentAt)
    {
        if (MessageId == messageId)
        {
            // Idempotent: a retried send carrying the same attachment must not fail.
            return;
        }

        if (MessageId is { } existing)
        {
            throw new InvalidOperationException(
                $"Attachment {Id} already belongs to message {existing} and cannot be reassigned.");
        }

        MessageId = messageId;
        MessageSentAt = messageSentAt;

        // The scan is triggered from here, and this is the only place it can be: the client PUTs
        // bytes straight to MinIO, so the server never observes the transfer finishing. Naming the
        // attachment on a send is the first moment anything tells us the object exists — which is
        // also the first moment it matters, since an unsent attachment is retrievable by nobody.
        Raise(new AttachmentUploaded(
            Guid.CreateVersion7(),
            messageSentAt,
            Id,
            ConversationId,
            ObjectKey,
            Kind,
            ByteSize));
    }

    /// <summary>
    /// Records a clean verdict and makes the attachment retrievable (FR-024).
    /// </summary>
    /// <param name="confirmedByteSize">
    /// What actually landed in quarantine. Replaces the declared size, which was a client's claim.
    /// </param>
    /// <param name="posterObjectKey">The extracted poster frame for a video, if any.</param>
    /// <param name="clock">Source of the verdict timestamp.</param>
    /// <exception cref="ScanVerdictConflictException">The verdict is already terminal.</exception>
    public void MarkClean(long confirmedByteSize, string? posterObjectKey, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(confirmedByteSize, 0);

        RequireNotTerminal(ScanVerdict.Clean);

        // The declared size got the upload past the gate; this is the measured one. They differ
        // when a client lies or an upload is truncated, and everything downstream — the capacity
        // monitor, the Content-Length the proxy serves — should read the measured value.
        ByteSize = confirmedByteSize;
        PosterObjectKey = posterObjectKey;
        ScanStatus = ScanVerdict.Clean;
        ScannedAt = clock.UtcNow;

        Raise(new AttachmentScanned(
            Guid.CreateVersion7(), clock.UtcNow, Id, ConversationId, ScanVerdict.Clean, clock.UtcNow));
    }

    /// <summary>
    /// Records a malicious verdict. The bytes are purged by the caller; the row is retained for audit.
    /// </summary>
    /// <exception cref="ScanVerdictConflictException">The verdict is already terminal.</exception>
    public void MarkInfected(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        RequireNotTerminal(ScanVerdict.Infected);

        ScanStatus = ScanVerdict.Infected;
        ScannedAt = clock.UtcNow;

        Raise(new AttachmentScanned(
            Guid.CreateVersion7(), clock.UtcNow, Id, ConversationId, ScanVerdict.Infected, clock.UtcNow));
    }

    /// <summary>
    /// Records that the scan could not reach a verdict. Not retrievable, and retryable.
    /// </summary>
    /// <exception cref="ScanVerdictConflictException">The verdict is already terminal.</exception>
    public void MarkScanFailed(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        RequireNotTerminal(ScanVerdict.Failed);

        ScanStatus = ScanVerdict.Failed;
        ScannedAt = clock.UtcNow;

        Raise(new AttachmentScanned(
            Guid.CreateVersion7(), clock.UtcNow, Id, ConversationId, ScanVerdict.Failed, clock.UtcNow));
    }

    /// <summary>
    /// Returns a failed scan to <see cref="ScanVerdict.Pending"/> so it can be scanned again.
    /// </summary>
    /// <remarks>
    /// The only transition back into <c>pending</c>, and only from <c>failed</c>. Allowing it from
    /// <c>infected</c> would let a retry launder a known-malicious object into a clean verdict.
    /// </remarks>
    /// <exception cref="ScanVerdictConflictException">The current state is not <c>failed</c>.</exception>
    public void RetryScan()
    {
        if (ScanStatus is not ScanVerdict.Failed)
        {
            throw new ScanVerdictConflictException(Id, ScanStatus, ScanVerdict.Pending);
        }

        ScanStatus = ScanVerdict.Pending;
        ScannedAt = null;
    }

    /// <summary>
    /// Whether the bytes may be served, given what the caller knows about the carrying message.
    /// </summary>
    /// <param name="messageIsDeleted">
    /// Whether the message carrying this is a tombstone. Passed in rather than navigated to,
    /// because Domain holds no repository and the caller has already loaded the message.
    /// </param>
    /// <remarks>
    /// <b>Membership is deliberately not a parameter.</b> This answers only the part of FR-024 and
    /// FR-027 that the attachment itself knows. Membership is a database-backed check the
    /// Application layer performs per request (FR-025); folding it in here would invite a caller to
    /// believe a <c>true</c> from this method is sufficient authorization. It is necessary, never
    /// sufficient.
    /// </remarks>
    public bool IsRetrievable(bool messageIsDeleted) =>
        ScanStatus is ScanVerdict.Clean && !messageIsDeleted;

    private void RequireNotTerminal(ScanVerdict target)
    {
        if (ScanStatus is ScanVerdict.Clean or ScanVerdict.Infected)
        {
            throw new ScanVerdictConflictException(Id, ScanStatus, target);
        }
    }

    private static string NormalizeFileName(string? fileName)
    {
        string trimmed = fileName?.Trim() ?? string.Empty;

        if (trimmed.Length == 0)
        {
            throw new ArgumentException("An attachment must declare a file name.", nameof(fileName));
        }

        // Keep the leaf only. The name never becomes a path — ObjectKey is built from identifiers —
        // but a name echoed into a Content-Disposition header should not carry directory
        // separators, and truncating to the leaf is both the safe and the expected rendering.
        int lastSeparator = trimmed.LastIndexOfAny(['/', '\\']);

        if (lastSeparator >= 0)
        {
            trimmed = trimmed[(lastSeparator + 1)..].Trim();
        }

        if (trimmed.Length == 0)
        {
            throw new ArgumentException("An attachment must declare a file name.", nameof(fileName));
        }

        return trimmed.Length > MaximumFileNameLength
            ? trimmed[..MaximumFileNameLength]
            : trimmed;
    }
}

/// <summary>
/// Raised when an attachment's bytes are known to exist (<c>chat.attachment.uploaded.v1</c>).
/// </summary>
/// <remarks>
/// <para>
/// Consumed by <c>attachments.scan</c>, which scans, extracts a poster frame for video, promotes
/// from quarantine, and then emits <see cref="AttachmentScanned"/> (contracts/messaging.md).
/// </para>
/// <para>
/// <b>Carries the object key, which is the one exception to "payloads carry identifiers only".</b>
/// It is not content: it is two identifiers with a slash between them, derived from values already
/// in the payload, and the scan consumer needs it to address the object. It reveals nothing a
/// consumer could not reconstruct from <see cref="AttachmentId"/> and
/// <see cref="ConversationId"/> — which is exactly why it is safe, and why the file name and the
/// bytes are not here.
/// </para>
/// </remarks>
public sealed record AttachmentUploaded(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid AttachmentId,
    Guid ConversationId,
    string ObjectKey,
    AttachmentKind Kind,
    long ByteSize) : DomainEvent(EventId, OccurredAt)
{
    /// <inheritdoc />
    public override string EventType => "chat.attachment.uploaded.v1";
}

/// <summary>
/// Raised when a scan reaches a verdict (<c>chat.attachment.scanned.v1</c>).
/// </summary>
/// <remarks>
/// <para>
/// One event for all three outcomes, with the verdict as data, because that is what
/// contracts/messaging.md declares. Per the contract: <c>clean</c> makes the attachment retrievable
/// and emits <c>AttachmentReady</c> over SignalR; <c>infected</c> purges the object, notifies only
/// the uploader, and writes an audit event (FR-024).
/// </para>
/// <para>
/// <b>Carries no file name and no object key.</b> A rejected upload's name is shown to its uploader
/// through the notification path, which reads the row — putting it on the wire would place
/// user-supplied text in broker logs and DLQ dumps, which FR-056 forbids.
/// </para>
/// </remarks>
public sealed record AttachmentScanned(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid AttachmentId,
    Guid ConversationId,
    ScanVerdict Verdict,
    DateTimeOffset ScannedAt) : DomainEvent(EventId, OccurredAt)
{
    /// <inheritdoc />
    public override string EventType => "chat.attachment.scanned.v1";
}

/// <summary>Thrown when a scan verdict would move out of a terminal state, or retry a non-failure.</summary>
public sealed class ScanVerdictConflictException : InvalidOperationException
{
    /// <summary>Creates the exception.</summary>
    public ScanVerdictConflictException(Guid attachmentId, ScanVerdict current, ScanVerdict attempted)
        : base($"Attachment {attachmentId} is '{current}', which cannot become '{attempted}'.")
    {
        AttachmentId = attachmentId;
        Current = current;
        Attempted = attempted;
    }

    /// <summary>Creates the exception.</summary>
    public ScanVerdictConflictException()
    {
    }

    /// <summary>Creates the exception.</summary>
    public ScanVerdictConflictException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public ScanVerdictConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The attachment whose verdict was already settled.</summary>
    public Guid AttachmentId { get; }

    /// <summary>The state it is in.</summary>
    public ScanVerdict Current { get; }

    /// <summary>The state that was attempted.</summary>
    public ScanVerdict Attempted { get; }
}
