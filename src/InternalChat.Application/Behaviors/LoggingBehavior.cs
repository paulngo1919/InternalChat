using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace InternalChat.Application.Behaviors;

/// <summary>
/// Structured logging and timing around every use case.
/// </summary>
/// <remarks>
/// <para>
/// The outermost behavior, so the recorded duration is what the caller actually waited —
/// validation, transaction, and handler included. Anything narrower would report a number that
/// looks good while the endpoint misses its budget.
/// </para>
/// <para>
/// Constitution Principle IV and FR-056: logs MUST NOT contain message bodies, attachment
/// contents, credentials, tokens, or personal data beyond a stable identifier. This behavior
/// therefore logs the request <em>type name</em> and never the request itself. Logging
/// <c>{@Request}</c> here would put every message body into the log pipeline in one line of
/// well-meaning code.
/// </para>
/// </remarks>
public sealed partial class LoggingBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
{
    private readonly ILogger<LoggingBehavior<TRequest, TResponse>> _logger;

    /// <summary>Creates the behavior.</summary>
    public LoggingBehavior(ILogger<LoggingBehavior<TRequest, TResponse>> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<TResponse> HandleAsync(
        TRequest request,
        UseCaseContinuation<TResponse> continuation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(continuation);

        string useCase = typeof(TRequest).Name;
        long start = Stopwatch.GetTimestamp();

        UseCaseStarted(_logger, useCase);

        try
        {
            TResponse response = await continuation(cancellationToken).ConfigureAwait(false);

            // Elapsed time is computed into a local rather than passed as an expression, so the
            // work happens once and only where it is actually used (analyzer CA1873).
            double elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            UseCaseSucceeded(_logger, useCase, elapsedMs);
            return response;
        }
        catch (Exception ex)
        {
            double elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            UseCaseFailed(_logger, useCase, elapsedMs, ex);
            throw;
        }
    }

    // Source-generated logging: required by analyzer CA1848 and avoids boxing on the hot path.
    // At 100 messages/second the send path runs this on every request.
    [LoggerMessage(EventId = 1000, Level = LogLevel.Debug, Message = "Use case {UseCase} started")]
    private static partial void UseCaseStarted(ILogger logger, string useCase);

    [LoggerMessage(EventId = 1001, Level = LogLevel.Information, Message = "Use case {UseCase} succeeded in {ElapsedMs} ms")]
    private static partial void UseCaseSucceeded(ILogger logger, string useCase, double elapsedMs);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Warning, Message = "Use case {UseCase} failed after {ElapsedMs} ms")]
    private static partial void UseCaseFailed(ILogger logger, string useCase, double elapsedMs, Exception exception);
}
