using InternalChat.Api.Contracts;
using InternalChat.Domain.Attachments;

namespace InternalChat.Api.Mapping;

/// <summary>
/// Turns attachments into the wire contract.
/// </summary>
/// <remarks>
/// <para>
/// <b>The content URL is built here and nowhere else.</b> It is a route on this API — never a
/// presigned storage address — so that following it re-enters the authorization check on every
/// request (FR-025). Centralising it means there is exactly one line in the codebase that decides
/// what a client is told to fetch, which is the line worth guarding.
/// </para>
/// <para>
/// <b>It is omitted unless the verdict is clean.</b> Emitting an address for a pending or infected
/// attachment would be harmless in itself — the endpoint refuses — but it would make every client
/// render a broken image instead of "scanning…", and it would put a retrievable-looking URL in a
/// response for bytes FR-024 forbids serving.
/// </para>
/// </remarks>
public static class AttachmentMapper
{
    /// <summary>The route prefix attachment content is served from.</summary>
    /// <remarks>
    /// Relative, so it works unchanged behind the reverse proxy, in the integration suite, and from
    /// a frontend on a different origin — an absolute URL would need the public host name injected
    /// here, which is configuration this layer should not need.
    /// </remarks>
    public const string ContentRoutePrefix = "/api/v1/attachments";

    /// <summary>Maps one attachment.</summary>
    public static AttachmentResponse ToResponse(this Attachment attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);

        bool isClean = attachment.ScanStatus is ScanVerdict.Clean;

        return new AttachmentResponse(
            attachment.Id,
            ToWire(attachment.Kind),
            attachment.ContentType,
            attachment.ByteSize,
            attachment.DurationSeconds,
            attachment.FileName,
            ToWire(attachment.ScanStatus),
            isClean ? $"{ContentRoutePrefix}/{attachment.Id}/content" : null,

            // A poster exists only for a video, and only once the scan consumer has extracted one.
            isClean && attachment.PosterObjectKey is not null
                ? $"{ContentRoutePrefix}/{attachment.Id}/poster"
                : null);
    }

    /// <summary>Maps a set of attachments, preserving upload order.</summary>
    public static IReadOnlyList<AttachmentResponse> ToResponses(this IEnumerable<Attachment> attachments)
    {
        ArgumentNullException.ThrowIfNull(attachments);

        return [.. attachments.Select(ToResponse)];
    }

    /// <summary>
    /// The documented spelling of a kind.
    /// </summary>
    /// <remarks>
    /// Explicit rather than <c>ToString()</c>, for the same reason <c>ConversationKinds</c> is: a
    /// default enum serialization emits <c>Image</c>, and a client switching on the documented
    /// <c>image</c> falls through to its unknown-type branch and renders a download link where an
    /// inline preview belongs.
    /// </remarks>
    private static string ToWire(AttachmentKind kind) => kind switch
    {
        AttachmentKind.Image => AttachmentKinds.Image,
        AttachmentKind.Video => AttachmentKinds.Video,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown attachment kind."),
    };

    /// <summary>The documented spelling of a scan verdict.</summary>
    private static string ToWire(ScanVerdict verdict) => verdict switch
    {
        ScanVerdict.Pending => ScanStatuses.Pending,
        ScanVerdict.Clean => ScanStatuses.Clean,
        ScanVerdict.Infected => ScanStatuses.Infected,
        ScanVerdict.Failed => ScanStatuses.Failed,
        _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, "Unknown scan verdict."),
    };
}
