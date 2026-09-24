using InternalChat.Api.Authorization;
using InternalChat.Api.Contracts;
using InternalChat.Api.Mapping;
using InternalChat.Api.RateLimiting;
using InternalChat.Application.Attachments;
using InternalChat.Application.Behaviors;
using Microsoft.Extensions.Options;

namespace InternalChat.Api.Endpoints;

/// <summary>
/// T153 — reserve an upload, read attachment metadata, and retrieve bytes (FR-021 – FR-025).
/// </summary>
/// <remarks>
/// <para>
/// <b>The two retrieval routes are authorized differently, and the difference is the point.</b>
/// The upload route names a conversation, so it carries the membership filter like every other
/// conversation-scoped endpoint. The retrieval routes name only an attachment id — the conversation
/// is a property of the row, not of the request — so the filter cannot apply and the check happens
/// inside <see cref="AuthorizeDownloadHandler"/> against the conversation the attachment actually
/// belongs to. Routing them through the filter would mean trusting a caller-supplied conversation
/// id, which is the one input that must not be trusted here.
/// </para>
/// <para>
/// <b>Bytes never pass through .NET</b> (research.md D7). The content route authorizes and then
/// emits <c>X-Accel-Redirect</c>; nginx streams from an internal-only location with range support.
/// Streaming through Kestrel would put a 500 MB transfer on a thread pool whose budget belongs to
/// messaging, on a host that is already limited to one machine.
/// </para>
/// </remarks>
public static class AttachmentEndpoints
{
    /// <summary>Maps the attachment endpoints.</summary>
    public static IEndpointRouteBuilder MapAttachmentEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapPost("/conversations/{conversationId:guid}/attachments", RequestUpload)
            .WithTags("Attachments")
            .WithName("RequestAttachmentUpload")
            .WithSummary("Reserve an upload, validating kind, type, size, and duration first (FR-023)")
            .RequireAuthorization(AuthorizationPolicies.ConversationMember)
            .RequireConversationMembership()

            // Concurrency rather than a rate: the cost of an upload is a transfer held open, not
            // the request that starts it.
            .RequireRateLimiting(RateLimitPolicies.Upload);

        routes.MapGet("/attachments/{attachmentId:guid}", GetAttachment)
            .WithTags("Attachments")
            .WithName("GetAttachment")
            .WithSummary("Attachment metadata and scan status")

            // No membership filter: see the class remarks. The conversation is on the row.
            .RequireAuthorization(AuthorizationPolicies.Employee);

        routes.MapGet("/attachments/{attachmentId:guid}/content", GetAttachmentContent)
            .WithTags("Attachments")
            .WithName("GetAttachmentContent")
            .WithSummary("Retrieve bytes; membership is checked on every request (FR-025)")
            .RequireAuthorization(AuthorizationPolicies.Employee);

        return routes;
    }

    /// <summary>Reserves an upload and returns a one-time quarantine ticket.</summary>
    private static async Task<IResult> RequestUpload(
        Guid conversationId,
        RequestUploadRequest request,
        CurrentEmployee currentEmployee,
        IUseCaseDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        UploadTicket ticket = await dispatcher
            .SendAsync<Application.Attachments.RequestUpload, UploadTicket>(
                new Application.Attachments.RequestUpload(
                    conversationId,
                    currentEmployee.Id,
                    request.Kind,
                    request.ContentType,
                    request.ByteSize,
                    request.DurationSeconds,
                    request.FileName),
                cancellationToken)
            .ConfigureAwait(false);

        UploadTicketResponse response = new(
            ticket.Attachment.Id,
            ticket.UploadUrl.ToString(),
            ticket.ExpiresAt);

        return Results.Created($"/attachments/{ticket.Attachment.Id}", response);
    }

    /// <summary>Attachment metadata, including where it stands in its scan.</summary>
    private static async Task<IResult> GetAttachment(
        Guid attachmentId,
        CurrentEmployee currentEmployee,
        IUseCaseDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        Domain.Attachments.Attachment attachment = await dispatcher
            .SendAsync<GetAttachmentMetadata, Domain.Attachments.Attachment>(
                new GetAttachmentMetadata(attachmentId, currentEmployee.Id),
                cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(attachment.ToResponse());
    }

    /// <summary>
    /// Authorizes a retrieval and hands the byte transfer to the reverse proxy.
    /// </summary>
    /// <remarks>
    /// Returns 204 with headers rather than a body. nginx sees <c>X-Accel-Redirect</c>, discards
    /// this response, and serves the named internal location itself — including <c>Range</c>
    /// handling, which is what makes a video start playing before it has downloaded (FR-022).
    /// </remarks>
    private static async Task<IResult> GetAttachmentContent(
        Guid attachmentId,
        CurrentEmployee currentEmployee,
        IUseCaseDispatcher dispatcher,
        IOptions<AttachmentDeliveryOptions> delivery,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        ArgumentNullException.ThrowIfNull(context);

        AuthorizedContent content = await dispatcher
            .SendAsync<AuthorizeDownload, AuthorizedContent>(
                new AuthorizeDownload(attachmentId, currentEmployee.Id),
                cancellationToken)
            .ConfigureAwait(false);

        AttachmentDeliveryOptions options = delivery.Value;

        // private, no-store: a shared cache holding these would serve one member's authorized
        // response to whoever asked next, which is FR-025 broken by an intermediary rather than by
        // this code. no-store rather than no-cache because the bytes must not be written down at
        // all, not merely revalidated.
        context.Response.Headers.CacheControl = "private, no-store";
        context.Response.Headers.ContentType = content.ContentType;

        // The uploader's file name, quoted and percent-encoded. Never interpolated raw: it is
        // attacker-influenced text going into a response header.
        context.Response.Headers.ContentDisposition =
            $"inline; filename*=UTF-8''{Uri.EscapeDataString(content.FileName)}";

        if (!options.UseInternalRedirect)
        {
            // No reverse proxy in front (integration tests, and a deployment without nginx).
            // Streams through Kestrel instead — correct, just not what production does.
            Stream stream = await dispatcher
                .SendAsync<OpenAttachmentContent, Stream>(
                    new OpenAttachmentContent(content.ObjectKey),
                    cancellationToken)
                .ConfigureAwait(false);

            return Results.Stream(stream, content.ContentType, enableRangeProcessing: true);
        }

        context.Response.Headers["X-Accel-Redirect"] =
            $"{options.InternalLocation.TrimEnd('/')}/{content.ObjectKey}";

        return Results.NoContent();
    }
}

/// <summary>How authorized attachment bytes reach the client.</summary>
public sealed class AttachmentDeliveryOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "AttachmentDelivery";

    /// <summary>
    /// Whether to hand the transfer to the reverse proxy via <c>X-Accel-Redirect</c>.
    /// </summary>
    /// <remarks>
    /// True in every deployment. False only where no proxy is in front — the integration suite
    /// talks to Kestrel directly, and a 204 with a header it cannot act on would make every
    /// retrieval test assert against an empty body.
    /// </remarks>
    public bool UseInternalRedirect { get; set; } = true;

    /// <summary>
    /// The internal-only nginx location that fronts the clean bucket.
    /// </summary>
    /// <remarks>
    /// Marked <c>internal</c> in nginx, so it is unreachable from outside no matter what a client
    /// requests — the only way in is this header, emitted after authorization.
    /// </remarks>
    public string InternalLocation { get; set; } = "/internal-attachments";
}
