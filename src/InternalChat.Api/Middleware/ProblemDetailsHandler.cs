using System.Diagnostics;
using InternalChat.Application.Abstractions;
using InternalChat.Application.Behaviors;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace InternalChat.Api.Middleware;

/// <summary>
/// Turns unhandled exceptions into RFC 9457 Problem Details responses.
/// </summary>
/// <remarks>
/// <para>
/// Constitution, Application Controls: "Errors returned to clients MUST NOT expose stack traces,
/// SQL, or internal hostnames." That is why this maps a small set of known exception types to
/// deliberate responses and collapses everything else into a bare 500 — an unexpected exception's
/// message is not safe to forward, because it is exactly where connection strings and internal
/// hostnames surface.
/// </para>
/// <para>
/// The full detail is logged with a trace id, and the same trace id is returned to the caller.
/// A user reporting "I got an error, the id was 00-4bf9…" gives support everything they need
/// without the response having leaked anything.
/// </para>
/// </remarks>
public sealed partial class ProblemDetailsHandler : IExceptionHandler
{
    private const string ProblemTypeBase = "https://internalchat.invalid/problems/";

    private readonly IProblemDetailsService _problemDetailsService;
    private readonly ILogger<ProblemDetailsHandler> _logger;

    /// <summary>Creates the handler.</summary>
    public ProblemDetailsHandler(
        IProblemDetailsService problemDetailsService,
        ILogger<ProblemDetailsHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(problemDetailsService);
        ArgumentNullException.ThrowIfNull(logger);

        _problemDetailsService = problemDetailsService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(exception);

        string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

        ProblemDetails problem = Map(exception, traceId);

        httpContext.Response.StatusCode = problem.Status ?? StatusCodes.Status500InternalServerError;

        // Logged at the level the outcome deserves: a client sending an invalid request is not
        // an incident, but an unexpected exception is. Logging both as errors trains people to
        // ignore the error log.
        // Path converted to a local first: PathString.ToString() allocates, and passing it as an
        // argument expression would run that work even when the level is disabled (CA1873).
        string path = httpContext.Request.Path.Value ?? string.Empty;

        if (problem.Status >= StatusCodes.Status500InternalServerError)
        {
            UnhandledException(_logger, path, traceId, exception);
        }
        else
        {
            RequestRejected(_logger, path, problem.Status ?? 0, traceId);
        }

        return await _problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = problem,
        }).ConfigureAwait(false);
    }

    private static ProblemDetails Map(Exception exception, string traceId) => exception switch
    {
        ValidationFailedException validation => new ProblemDetails
        {
            Type = ProblemTypeBase + "validation-failed",
            Title = "The request could not be processed.",
            Status = StatusCodes.Status400BadRequest,
            Detail = "One or more fields are invalid.",
            Extensions =
            {
                ["traceId"] = traceId,
                // Every field at once. Fixing four fields across four round trips is a worse
                // experience than being told all four problems now.
                ["errors"] = validation.Errors
                    .GroupBy(e => e.Field, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.Select(e => e.Message).ToArray(), StringComparer.Ordinal),
            },
        },

        // Deliberately identical in shape and wording to a not-found response. SC-017 requires a
        // refusal never to reveal that the resource exists — a distinct "forbidden" body would
        // let anyone enumerate conversations by reading which error came back.
        UnauthorizedAccessException => NotFoundShaped(traceId),

        // Not the author of a message in a conversation the caller can already read. 403 rather
        // than the 404 shape above, and that is not an inconsistency: the caller demonstrably knows
        // this message exists, because they were served it. There is nothing left to conceal, and
        // pretending the message is missing would make a legitimate client retry forever.
        Domain.Messages.NotTheAuthorException => new ProblemDetails
        {
            Type = ProblemTypeBase + "not-the-author",
            Title = "Only the author can change this message.",
            Status = StatusCodes.Status403Forbidden,
            Extensions = { ["traceId"] = traceId },
        },

        // Domain rules that the request was well-formed enough to reach. 422, not 400: the syntax
        // was fine and the state is what refused, so a client that re-sends the identical request
        // will be refused identically — which 400 does not communicate.
        //
        // Each carries its own title because these are the messages a person reads in the UI, and
        // "unprocessable entity" tells them nothing about what to do differently.
        Domain.Messages.EditWindowExpiredException => Unprocessable(
            traceId,
            "edit-window-expired",
            "This message is more than 24 hours old and can no longer be changed."),

        Domain.Messages.MessageDeletedException => Unprocessable(
            traceId,
            "message-deleted",
            "This message has been deleted."),

        Domain.Conversations.DirectConversationException => Unprocessable(
            traceId,
            "direct-conversation",
            "A direct conversation has exactly two participants and its membership cannot change."),

        Domain.Employees.DeactivatedEmployeeException => Unprocessable(
            traceId,
            "employee-deactivated",
            "That employee is no longer active and cannot be added to a conversation."),

        // Attachment limits (FR-023). 413 and 415 rather than 422, because these are the codes the
        // contract documents and the ones an upload client already knows how to act on. Both state
        // the limit in the title — FR-023 requires the refusal to name it, and a client that has to
        // guess how much smaller a file must be will guess wrong.
        Domain.Attachments.FileTooLargeException tooLarge => new ProblemDetails
        {
            Type = ProblemTypeBase + "attachment-too-large",
            Title = tooLarge.Message,
            Status = StatusCodes.Status413PayloadTooLarge,
            Extensions =
            {
                ["traceId"] = traceId,
                ["limitBytes"] = tooLarge.LimitBytes,
            },
        },

        Domain.Attachments.UnsupportedContentTypeException unsupported => new ProblemDetails
        {
            Type = ProblemTypeBase + "attachment-type-not-allowed",
            Title = unsupported.Message,
            Status = StatusCodes.Status415UnsupportedMediaType,
            Extensions =
            {
                ["traceId"] = traceId,
                ["allowed"] = unsupported.Allowed,
            },
        },

        // 422: a duration is not a media type and not a size, so neither 413 nor 415 fits, and the
        // request was well-formed enough to reach the rule that refused it.
        Domain.Attachments.VideoTooLongException tooLong => Unprocessable(
            traceId,
            "video-too-long",
            tooLong.Message),

        // 451. Reached only after membership has been confirmed, so naming the reason discloses
        // nothing — the caller is established as someone who may see this conversation's content.
        // Silence here would look like a broken upload to someone who was expecting the file.
        Application.Attachments.AttachmentInfectedException => new ProblemDetails
        {
            Type = ProblemTypeBase + "attachment-blocked",
            Title = "This file was blocked because a malware scan found it malicious.",
            Status = StatusCodes.Status451UnavailableForLegalReasons,
            Extensions = { ["traceId"] = traceId },
        },

        // 507 (FR-028). Its own code so a client can tell "storage is full, try later" from
        // "your file is wrong" — and so the message can say that text messaging still works.
        Application.Attachments.StorageCapacityExceededException => new ProblemDetails
        {
            Type = ProblemTypeBase + "storage-exhausted",
            Title = "Attachment storage is full. Messaging is unaffected; please try again later.",
            Status = StatusCodesExtra.InsufficientStorage,
            Extensions = { ["traceId"] = traceId },
        },

        // 422: the send was well-formed and the state refused it. The ids are echoed so a client
        // can drop them from its retry, but no reason is given per id — see the exception's remarks.
        Application.Messages.AttachmentNotAttachableException notAttachable => new ProblemDetails
        {
            Type = ProblemTypeBase + "attachment-not-attachable",
            Title = notAttachable.Message,
            Status = StatusCodes.Status422UnprocessableEntity,
            Extensions =
            {
                ["traceId"] = traceId,
                ["attachmentIds"] = notAttachable.AttachmentIds,
            },
        },

        // 409. The caller is established as a member — the handler checked before reaching this —
        // so naming the reason discloses nothing, and "the meeting is full" is something they can
        // act on where a bare 403 is not.
        Domain.Meetings.MeetingFullException full => new ProblemDetails
        {
            Type = ProblemTypeBase + "meeting-full",
            Title = full.Message,
            Status = StatusCodes.Status409Conflict,
            Extensions =
            {
                ["traceId"] = traceId,
                ["maxParticipants"] = full.Capacity,
            },
        },

        // 503, not 500. The media host is the one component on a second machine, and constitution
        // v1.2.0 requires the rest of the platform to keep working without it. The message says so
        // explicitly: a client that cannot tell "meetings are down" from "the platform is down"
        // will tell its user the wrong thing.
        Application.Meetings.MediaHostUnavailableException unavailable => new ProblemDetails
        {
            Type = ProblemTypeBase + "meetings-unavailable",
            Title = unavailable.Message,
            Status = StatusCodes.Status503ServiceUnavailable,
            Extensions =
            {
                ["traceId"] = traceId,
                ["retryAfterSeconds"] = Application.Meetings.MediaHostUnavailableException.RetryAfterSeconds,
            },
        },

        // 503 as well, and deliberately a different problem type: "try later, the platform is busy"
        // and "meetings are broken" call for different things from both a person and a dashboard.
        Application.Meetings.PlatformAtCapacityException atCapacity => new ProblemDetails
        {
            Type = ProblemTypeBase + "meetings-at-capacity",
            Title = atCapacity.Message,
            Status = StatusCodes.Status503ServiceUnavailable,
            Extensions =
            {
                ["traceId"] = traceId,
                ["retryAfterSeconds"] = Application.Meetings.PlatformAtCapacityException.RetryAfterSeconds,
            },
        },

        Domain.Meetings.MeetingEndedException => Unprocessable(
            traceId,
            "meeting-ended",
            "This meeting has ended."),

        Application.Attachments.UnsupportedAttachmentKindException kind => new ProblemDetails
        {
            Type = ProblemTypeBase + "attachment-kind-unknown",
            Title = kind.Message,
            Status = StatusCodes.Status400BadRequest,
            Extensions = { ["traceId"] = traceId },
        },

        // 409, not 422: the request conflicts with current state rather than a rule, and a client
        // that retries after reading the current member list will not be refused identically.
        Domain.Conversations.MemberAlreadyActiveException => new ProblemDetails
        {
            Type = ProblemTypeBase + "already-a-member",
            Title = "That employee is already a member of this conversation.",
            Status = StatusCodes.Status409Conflict,
            Extensions = { ["traceId"] = traceId },
        },

        OperationCanceledException => new ProblemDetails
        {
            Type = ProblemTypeBase + "request-cancelled",
            Title = "The request was cancelled.",
            Status = StatusCodesExtra.ClientClosedRequest,
            Extensions = { ["traceId"] = traceId },
        },

        TimeoutException => new ProblemDetails
        {
            Type = ProblemTypeBase + "upstream-timeout",
            Title = "A dependency did not respond in time.",
            Status = StatusCodes.Status504GatewayTimeout,
            Extensions = { ["traceId"] = traceId },
        },

        // Everything else. No message, no type name, no stack — an unexpected exception's text
        // is where connection strings and hostnames leak.
        _ => new ProblemDetails
        {
            Type = ProblemTypeBase + "internal-error",
            Title = "An unexpected error occurred.",
            Status = StatusCodes.Status500InternalServerError,
            Detail = "The error has been logged. Quote the trace id when reporting it.",
            Extensions = { ["traceId"] = traceId },
        },
    };

    /// <summary>
    /// A domain rule refused a syntactically valid request.
    /// </summary>
    /// <remarks>
    /// The <paramref name="detail"/> is shown to a person, so it must say what the rule is without
    /// naming anything the caller could not already see. None of these mention another employee, a
    /// conversation they are not in, or any identifier they did not supply.
    /// </remarks>
    private static ProblemDetails Unprocessable(string traceId, string type, string detail) => new()
    {
        Type = ProblemTypeBase + type,
        Title = "The request conflicts with a rule of the platform.",
        Status = StatusCodes.Status422UnprocessableEntity,
        Detail = detail,
        Extensions = { ["traceId"] = traceId },
    };

    private static ProblemDetails NotFoundShaped(string traceId) => new()
    {
        Type = ProblemTypeBase + "not-found",
        Title = "The requested resource was not found.",
        Status = StatusCodes.Status404NotFound,
        Extensions = { ["traceId"] = traceId },
    };

    [LoggerMessage(EventId = 3000, Level = LogLevel.Error, Message = "Unhandled exception on {Path} (trace {TraceId})")]
    private static partial void UnhandledException(ILogger logger, string path, string traceId, Exception exception);

    [LoggerMessage(EventId = 3001, Level = LogLevel.Information, Message = "Request to {Path} rejected with {StatusCode} (trace {TraceId})")]
    private static partial void RequestRejected(ILogger logger, string path, int statusCode, string traceId);
}

/// <summary>Status codes ASP.NET Core does not name.</summary>
internal static class StatusCodesExtra
{
    /// <summary>nginx's code for a client that disconnected before the response.</summary>
    public const int ClientClosedRequest = 499;

    /// <summary>
    /// RFC 4918 507 — the server cannot store what the request needs it to (FR-028).
    /// </summary>
    /// <remarks>
    /// Named here because <c>StatusCodes</c> does not: it is a WebDAV extension that ASP.NET Core
    /// omits, and <c>openapi.yaml</c> documents it for the upload reservation.
    /// </remarks>
    public const int InsufficientStorage = 507;
}
