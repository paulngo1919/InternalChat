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
}
