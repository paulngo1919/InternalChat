using System.Text.Json;
using InternalChat.Application.Abstractions;
using InternalChat.Domain.Attachments;
using InternalChat.Domain.Common;

namespace InternalChat.Worker.Consumers;

/// <summary>
/// T152 — scans an uploaded attachment, promotes it if clean, and records the verdict (FR-024).
/// </summary>
/// <remarks>
/// <para>
/// <b>Promotion is the last step, and the order is the requirement.</b> Scan, then re-measure,
/// then copy into the clean bucket, then record <c>clean</c>. Recording the verdict first would
/// leave a window in which the row says retrievable and the object is not yet there; promoting
/// first would leave a window in which unscanned bytes sit in the bucket the serving path reads
/// from. FR-024 only permits one of those orderings.
/// </para>
/// <para>
/// <b>The declared size is re-measured against what actually arrived.</b> Everything the upload
/// gate checked was a client's claim. This is the point where the claim meets the bytes, and a file
/// that turns out to exceed its kind's ceiling is refused here — an oversized object that got past
/// the gate by lying is not promoted just because it is clean.
/// </para>
/// <para>
/// <b>A failed scan is recorded and acknowledged, not retried through the broker.</b> The row goes
/// to <c>failed</c>, which is a retryable state a job revisits. Throwing instead would burn the
/// capped retries on a scanner outage and land the message in the DLQ, where nothing would ever
/// look at it again — and the attachment would sit at <c>pending</c> forever.
/// </para>
/// </remarks>
public sealed partial class AttachmentScanConsumer : IMessageConsumer
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly IAttachmentRepository _attachments;
    private readonly IObjectStore _objects;
    private readonly IMalwareScanner _scanner;
    private readonly IVideoProcessor _video;
    private readonly IEventPublisher _events;
    private readonly IAuditLog _audit;
    private readonly IClock _clock;
    private readonly ILogger<AttachmentScanConsumer> _logger;

    /// <summary>Creates the consumer.</summary>
    public AttachmentScanConsumer(
        IAttachmentRepository attachments,
        IObjectStore objects,
        IMalwareScanner scanner,
        IVideoProcessor video,
        IEventPublisher events,
        IAuditLog audit,
        IClock clock,
        ILogger<AttachmentScanConsumer> logger)
    {
        ArgumentNullException.ThrowIfNull(attachments);
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(scanner);
        ArgumentNullException.ThrowIfNull(video);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        _attachments = attachments;
        _objects = objects;
        _scanner = scanner;
        _video = video;
        _events = events;
        _audit = audit;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc />
    public string QueueName => "attachments.scan";

    /// <inheritdoc />
    public async Task HandleAsync(
        MessageEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (envelope.Type is not "chat.attachment.uploaded.v1")
        {
            // The binding is chat.attachment.#, so the scanned event this consumer itself produces
            // comes back here. Ignored rather than treated as an error: acknowledging it is
            // correct, and throwing would put our own output in the DLQ.
            return;
        }

        AttachmentUploadedPayload? payload =
            JsonSerializer.Deserialize<AttachmentUploadedPayload>(envelope.Payload, SerializerOptions);

        if (payload is null)
        {
            throw new InvalidOperationException(
                $"Outbox row {envelope.MessageId} carries an unreadable {envelope.Type} payload.");
        }

        Attachment? attachment = await _attachments
            .FindAsync(payload.AttachmentId, cancellationToken)
            .ConfigureAwait(false);

        if (attachment is null)
        {
            // The row is gone — retention swept it, or the send was rolled back after the outbox
            // row was written but the whole transaction failed. Nothing to scan, nothing to fix.
            AttachmentMissing(_logger, payload.AttachmentId);
            return;
        }

        if (attachment.ScanStatus is ScanVerdict.Clean or ScanVerdict.Infected)
        {
            // Terminal already. At-least-once delivery means this message can legitimately arrive
            // twice, and re-running the scan would either throw on the state machine or re-promote
            // an object that is already promoted.
            return;
        }

        await using Stream? content = await OpenQuarantineAsync(attachment, cancellationToken)
            .ConfigureAwait(false);

        if (content is null)
        {
            await RecordFailureAsync(attachment, cancellationToken).ConfigureAwait(false);
            return;
        }

        MalwareScanResult verdict = await _scanner.ScanAsync(content, cancellationToken).ConfigureAwait(false);

        switch (verdict.Outcome)
        {
            case MalwareScanOutcome.Infected:
                await QuarantineAsync(attachment, verdict, cancellationToken).ConfigureAwait(false);
                break;

            case MalwareScanOutcome.Clean:
                await PromoteAsync(attachment, content, cancellationToken).ConfigureAwait(false);
                break;

            default:
                await RecordFailureAsync(attachment, cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    /// <summary>Reads the quarantined object, or <c>null</c> when it is not there.</summary>
    /// <remarks>
    /// A missing object is the ordinary interrupted-upload case (FR-026): the ticket was issued and
    /// the send referenced it, but the bytes never finished arriving. Recorded as a failed scan
    /// rather than thrown, so the row stops saying <c>pending</c>.
    /// </remarks>
    private async Task<Stream?> OpenQuarantineAsync(Attachment attachment, CancellationToken cancellationToken)
    {
        try
        {
            return await _objects.OpenQuarantineReadAsync(attachment.ObjectKey, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (IOException exception)
        {
            QuarantineUnreadable(_logger, attachment.Id, exception);
            return null;
        }
    }

    private async Task PromoteAsync(
        Attachment attachment,
        Stream content,
        CancellationToken cancellationToken)
    {
        long measuredBytes = content.Length;

        // The claim meets the bytes. A file that lied its way past the upload gate is refused here,
        // clean or not — FileConstraints is the authority on what may be stored, and this is the
        // first moment the real size is known.
        try
        {
            FileConstraints.Validate(
                attachment.Kind, attachment.ContentType, measuredBytes, attachment.DurationSeconds);
        }
        catch (Exception exception) when (exception is FileTooLargeException or VideoTooLongException)
        {
            OversizedOnArrival(_logger, attachment.Id, measuredBytes, attachment.ByteSize);

            await _objects.DeleteAsync(attachment.ObjectKey, cancellationToken).ConfigureAwait(false);
            await RecordFailureAsync(attachment, cancellationToken).ConfigureAwait(false);
            return;
        }

        // Copy into the clean bucket BEFORE the row says clean. The other order would publish a
        // retrievable attachment whose object is still only in quarantine.
        await _objects.PromoteToCleanAsync(attachment.ObjectKey, cancellationToken).ConfigureAwait(false);

        string? posterKey = attachment.Kind is AttachmentKind.Video
            ? await PrepareVideoAsync(attachment, content, cancellationToken).ConfigureAwait(false)
            : null;

        attachment.MarkClean(measuredBytes, posterKey, _clock);

        await PublishAsync(attachment, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Probes a clean video, refuses unplayable codecs, relocates its index, and extracts a poster
    /// (T175, research.md D8).
    /// </summary>
    /// <returns>The poster's object key, or <c>null</c> when none could be produced.</returns>
    /// <remarks>
    /// <para>
    /// <b>The codec check is the one FR-023 constraint that cannot happen before upload.</b> A codec
    /// lives in the bitstream, not in anything a client declares, so an honest <c>video/mp4</c>
    /// carrying H.265 passes every pre-upload gate. Caught here, it becomes a refusal; missed, it
    /// becomes a black rectangle the recipient cannot explain.
    /// </para>
    /// <para>
    /// <b>Failures below the codec check are degradations, not refusals.</b> A missing poster or a
    /// failed remux makes a video start more slowly or show no thumbnail; withholding the file over
    /// either would be a worse outcome than the cosmetic loss.
    /// </para>
    /// </remarks>
    private async Task<string?> PrepareVideoAsync(
        Attachment attachment,
        Stream content,
        CancellationToken cancellationToken)
    {
        VideoProbe probe = await _video.ProbeAsync(content, cancellationToken).ConfigureAwait(false);

        // Throws out of PromoteAsync, which catches it alongside the size check and records a
        // failed scan — the file is never promoted.
        FileConstraints.ValidateProbedVideo(
            probe.VideoCodec,
            probe.AudioCodec,

            // The measured duration when there is one, falling back to the declared value only if
            // the probe could not determine it — in which case ValidateProbedVideo refuses anyway.
            probe.DurationSeconds ?? attachment.DurationSeconds);

        if (!probe.FastStart)
        {
            // Unconditional for MP4: ffprobe does not report atom order, and the remux is a no-op
            // when the index is already at the front. Cheaper than being wrong about it — without
            // a front-loaded moov, a browser must download the whole file before the first frame,
            // and range requests do not help because the player does not yet know what to ask for.
            Stream? remuxed = await _video.RelocateMoovAtomAsync(content, cancellationToken)
                .ConfigureAwait(false);

            if (remuxed is not null)
            {
                await using (remuxed.ConfigureAwait(false))
                {
                    // Overwrites the quarantined object, so the promotion that follows copies the
                    // fast-start version. Writing to the clean bucket here instead would put bytes
                    // there before the row says clean, which is the ordering FR-024 forbids.
                    await _objects
                        .ReplaceQuarantineObjectAsync(
                            attachment.ObjectKey, remuxed, attachment.ContentType, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }

        return await ExtractPosterAsync(attachment, content, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> ExtractPosterAsync(
        Attachment attachment,
        Stream content,
        CancellationToken cancellationToken)
    {
        Stream? poster = await _video.ExtractPosterAsync(content, cancellationToken)
            .ConfigureAwait(false);

        if (poster is null)
        {
            return null;
        }

        await using (poster.ConfigureAwait(false))
        {
            // Suffixed rather than given its own prefix, so the retention sweep that deletes an
            // attachment's object by key prefix collects the poster with it.
            string posterKey = $"{attachment.ObjectKey}.poster.jpg";

            await _objects
                .PutCleanObjectAsync(posterKey, poster, "image/jpeg", cancellationToken)
                .ConfigureAwait(false);

            return posterKey;
        }
    }

    private async Task QuarantineAsync(
        Attachment attachment,
        MalwareScanResult verdict,
        CancellationToken cancellationToken)
    {
        // Purged, never promoted. The row survives for audit (data-model.md).
        await _objects.DeleteAsync(attachment.ObjectKey, cancellationToken).ConfigureAwait(false);

        attachment.MarkInfected(_clock);

        // FR-024 requires the infected verdict to be auditable. The signature name goes here and
        // never to the uploader: it is attacker-chosen text, and naming the exact signature that
        // fired is a free oracle for tuning an evasion.
        await _audit.RecordAsync(
            new AuditEntry(
                Action: "attachment.infected",
                ActorId: attachment.UploadedBy,
                SubjectType: "attachment",
                SubjectId: attachment.Id,

                // No source IP: this runs in the Worker, off the back of a queue message, and there
                // is no request whose address could honestly be recorded here.
                SourceIp: null,
                Outcome: AuditOutcome.Denied,
                Detail: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["conversationId"] = attachment.ConversationId.ToString(),
                    ["signature"] = verdict.SignatureName ?? "unknown",
                }),
            cancellationToken)
            .ConfigureAwait(false);

        await PublishAsync(attachment, cancellationToken).ConfigureAwait(false);
    }

    private async Task RecordFailureAsync(Attachment attachment, CancellationToken cancellationToken)
    {
        attachment.MarkScanFailed(_clock);
        await PublishAsync(attachment, cancellationToken).ConfigureAwait(false);
    }

    private async Task PublishAsync(Attachment attachment, CancellationToken cancellationToken)
    {
        await _events.PublishAsync(attachment.DomainEvents, cancellationToken).ConfigureAwait(false);
        attachment.ClearDomainEvents();
    }

    /// <summary>The <c>chat.attachment.uploaded.v1</c> body, per contracts/messaging.md.</summary>
    private sealed record AttachmentUploadedPayload(
        Guid AttachmentId,
        Guid ConversationId,
        string ObjectKey,
        string Kind,
        long ByteSize);

    [LoggerMessage(EventId = 4101, Level = LogLevel.Information, Message = "Attachment {AttachmentId} no longer exists; nothing to scan")]
    private static partial void AttachmentMissing(ILogger logger, Guid attachmentId);

    [LoggerMessage(EventId = 4102, Level = LogLevel.Warning, Message = "Quarantined object for attachment {AttachmentId} could not be read; recording a failed scan")]
    private static partial void QuarantineUnreadable(ILogger logger, Guid attachmentId, Exception exception);

    [LoggerMessage(EventId = 4103, Level = LogLevel.Warning, Message = "Attachment {AttachmentId} arrived at {MeasuredBytes} bytes having declared {DeclaredBytes}; refusing to promote")]
    private static partial void OversizedOnArrival(ILogger logger, Guid attachmentId, long measuredBytes, long declaredBytes);
}
