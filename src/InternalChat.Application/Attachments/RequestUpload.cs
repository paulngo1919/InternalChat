using InternalChat.Application.Abstractions;
using InternalChat.Application.Behaviors;
using InternalChat.Domain.Attachments;
using InternalChat.Domain.Common;

namespace InternalChat.Application.Attachments;

/// <summary>Reserves an upload after validating it, before any bytes transfer (FR-023).</summary>
/// <param name="Kind">The wire spelling — <c>image</c> or <c>video</c>.</param>
public sealed record RequestUpload(
    Guid ConversationId,
    Guid UploadedBy,
    string Kind,
    string ContentType,
    long ByteSize,
    int? DurationSeconds,
    string FileName) : ITransactionalRequest;

/// <summary>The reserved upload.</summary>
/// <param name="UploadUrl">
/// A quarantine write location. Not a retrieval address — nothing is ever served from quarantine.
/// </param>
public sealed record UploadTicket(Attachment Attachment, Uri UploadUrl, DateTimeOffset ExpiresAt);

/// <summary>
/// Validates a declared upload, reserves a row, and issues a quarantine ticket.
/// </summary>
/// <remarks>
/// <para>
/// <b>The order here is the requirement.</b> FR-023 says violations are rejected "before upload
/// with the limit stated", so validation happens before the row is written and long before a
/// ticket exists. A client that declares a 600 MB video learns the limit without transferring a
/// byte.
/// </para>
/// <para>
/// <b>The capacity check refuses rather than degrades (FR-028).</b> When storage is full this
/// returns 507 and text messaging keeps working — which is the second half of FR-028 and the
/// reason the check lives here, on the attachment path, rather than in a global health gate that
/// would take the whole platform down with it.
/// </para>
/// <para>
/// <b>Nothing is published.</b> A reserved attachment is not an event anyone needs: it carries no
/// bytes yet, belongs to no message, and may never be redeemed. The first event is
/// <c>AttachmentReady</c>, after a clean scan.
/// </para>
/// </remarks>
public sealed class RequestUploadHandler : IUseCase<RequestUpload, UploadTicket>
{
    /// <summary>
    /// Fraction of capacity at which uploads start being refused.
    /// </summary>
    /// <remarks>
    /// Below 1.0 on purpose. A declared size is a claim, and several uploads can be in flight
    /// against the same free space, so accepting right up to the brim would let the bucket pass
    /// full between the check and the write. The headroom is what makes "refuse" a decision rather
    /// than a race.
    /// </remarks>
    public const double CapacityRefusalThreshold = 0.95;

    private readonly IAttachmentRepository _attachments;
    private readonly IObjectStore _objects;
    private readonly IClock _clock;

    /// <summary>Creates the handler.</summary>
    public RequestUploadHandler(
        IAttachmentRepository attachments,
        IObjectStore objects,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(attachments);
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(clock);

        _attachments = attachments;
        _objects = objects;
        _clock = clock;
    }

    /// <summary>How long a ticket stays redeemable.</summary>
    /// <remarks>
    /// Generous enough for a 500 MB video on a poor connection. It grants no read access, so a long
    /// life costs little; it is bounded at all so an abandoned ticket does not stay a live write
    /// capability indefinitely.
    /// </remarks>
    public static TimeSpan TicketLifetime => TimeSpan.FromMinutes(30);

    /// <inheritdoc />
    public async Task<UploadTicket> HandleAsync(
        RequestUpload request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        AttachmentKind kind = ParseKind(request.Kind);

        // Before the capacity check, because a malformed request should be told what is wrong with
        // it rather than that the disk is full — and because refusing it costs nothing either way.
        FileConstraints.Validate(kind, request.ContentType, request.ByteSize, request.DurationSeconds);

        StorageCapacity capacity = await _objects.GetCapacityAsync(cancellationToken).ConfigureAwait(false);

        if (capacity.UsedFraction >= CapacityRefusalThreshold)
        {
            throw new StorageCapacityExceededException(capacity.UsedBytes, capacity.CapacityBytes);
        }

        Attachment attachment = Attachment.Reserve(
            Guid.CreateVersion7(),
            request.ConversationId,
            request.UploadedBy,
            kind,
            request.ContentType,
            request.ByteSize,
            request.DurationSeconds,
            request.FileName,
            _clock);

        await _attachments.AddAsync(attachment, cancellationToken).ConfigureAwait(false);

        // After the row is staged. A ticket for an attachment that failed to persist would let a
        // client upload bytes nothing will ever scan, promote, or collect.
        Uri uploadUrl = await _objects
            .CreateUploadTicketAsync(
                attachment.ObjectKey,
                attachment.ContentType,
                attachment.ByteSize,
                TicketLifetime,
                cancellationToken)
            .ConfigureAwait(false);

        return new UploadTicket(attachment, uploadUrl, _clock.UtcNow.Add(TicketLifetime));
    }

    /// <summary>Maps the wire spelling to the domain enum.</summary>
    /// <remarks>
    /// Explicit rather than <c>Enum.TryParse</c>, which is case-insensitive by request and would
    /// also accept <c>"1"</c> and <c>"2"</c> — so a client sending the ordinal would silently get a
    /// kind it never named.
    /// </remarks>
    internal static AttachmentKind ParseKind(string? kind) => kind switch
    {
        "image" => AttachmentKind.Image,
        "video" => AttachmentKind.Video,
        _ => throw new UnsupportedAttachmentKindException(kind),
    };
}

/// <summary>Validates the parts of a request that are malformed rather than merely over a limit.</summary>
/// <remarks>
/// The size, type, and duration rules are deliberately <em>not</em> here. They live on
/// <c>FileConstraints</c>, which the domain enforces at construction, so they hold for every caller
/// rather than only for requests that came through this use case. What is left for a validator is
/// the shape of the request itself.
/// </remarks>
public sealed class RequestUploadValidator : IValidator<RequestUpload>
{
    /// <inheritdoc />
    public ValueTask<ValidationResult> ValidateAsync(
        RequestUpload request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        List<ValidationError> errors = [];

        if (request.Kind is not ("image" or "video"))
        {
            errors.Add(new ValidationError(
                "kind", "An attachment kind is 'image' or 'video'."));
        }

        if (string.IsNullOrWhiteSpace(request.FileName))
        {
            errors.Add(new ValidationError(
                "fileName", "An attachment needs a file name."));
        }

        if (request.ByteSize <= 0)
        {
            errors.Add(new ValidationError(
                "byteSize",
                "An upload must declare a positive size. The size is what the limit is checked "
                + "against before any bytes transfer (FR-023)."));
        }

        return ValueTask.FromResult(
            errors.Count == 0 ? ValidationResult.Valid : new ValidationResult(errors));
    }
}

/// <summary>Thrown when a client names an attachment kind that does not exist. Maps to 400.</summary>
public sealed class UnsupportedAttachmentKindException : ArgumentException
{
    /// <summary>Creates the exception.</summary>
    public UnsupportedAttachmentKindException(string? kind)
        : base($"'{kind}' is not an attachment kind. Use 'image' or 'video'.") => Kind = kind;

    /// <summary>Creates the exception.</summary>
    public UnsupportedAttachmentKindException()
    {
    }

    /// <summary>Creates the exception.</summary>
    public UnsupportedAttachmentKindException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>What the client sent.</summary>
    public string? Kind { get; }
}

/// <summary>
/// Thrown when attachment storage is too full to accept another upload (FR-028). Maps to 507.
/// </summary>
/// <remarks>
/// Its own exception rather than a generic failure because the response code matters: 507 tells a
/// client this is a server-side capacity condition that retrying later may fix, not a problem with
/// the file it just tried to send.
/// </remarks>
public sealed class StorageCapacityExceededException : InvalidOperationException
{
    /// <summary>Creates the exception.</summary>
    public StorageCapacityExceededException(long usedBytes, long capacityBytes)
        : base("Attachment storage is at capacity. Text messaging is unaffected (FR-028).")
    {
        UsedBytes = usedBytes;
        CapacityBytes = capacityBytes;
    }

    /// <summary>Creates the exception.</summary>
    public StorageCapacityExceededException()
    {
    }

    /// <summary>Creates the exception.</summary>
    public StorageCapacityExceededException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public StorageCapacityExceededException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Bytes currently consumed.</summary>
    public long UsedBytes { get; }

    /// <summary>Provisioned capacity.</summary>
    public long CapacityBytes { get; }
}
