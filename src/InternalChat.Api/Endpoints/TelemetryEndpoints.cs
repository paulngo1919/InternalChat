using InternalChat.Api.Authorization;
using InternalChat.Api.Contracts;
using InternalChat.Api.RateLimiting;
using InternalChat.Application.Behaviors;
using InternalChat.Application.Telemetry;

namespace InternalChat.Api.Endpoints;

/// <summary>
/// 002 T047 — where browsers report the delivery lag they observed (FR-011).
/// </summary>
/// <remarks>
/// <para>
/// The server's own histograms stop at the hub send; this is the only measurement that includes the
/// network and the browser, which is what an employee actually waits for. It is best-effort by
/// design: the client sends at most once a minute, drops a report on any error, and never retries.
/// </para>
/// <para>
/// Authenticated so it is not an open write into the metrics pipeline, and rate limited per
/// employee for the same reason — but nothing about the caller is recorded, and the body has no
/// field that could identify anyone (Principle IV).
/// </para>
/// </remarks>
public static class TelemetryEndpoints
{
    /// <summary>Maps the telemetry endpoint.</summary>
    public static IEndpointRouteBuilder MapTelemetryEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapPost("/telemetry/delivery", ReportDeliveryLag)
            .WithTags("Telemetry")
            .WithName("ReportDeliveryLag")
            .WithSummary("Report aggregated client-observed delivery lag (002 FR-011)")
            .RequireAuthorization(AuthorizationPolicies.Employee)
            .RequireRateLimiting(RateLimitPolicies.Telemetry);

        return routes;
    }

    private static async Task<IResult> ReportDeliveryLag(
        DeliveryLagReportRequest request,
        IUseCaseDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        await dispatcher
            .SendAsync<RecordDeliveryLag, bool>(
                new RecordDeliveryLag(request.Transport, request.WindowSeconds, request.Buckets ?? []),
                cancellationToken)
            .ConfigureAwait(false);

        return Results.Accepted();
    }
}
