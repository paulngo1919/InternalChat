namespace InternalChat.Api.Contracts;

/// <summary>
/// <c>DeliveryLagReport</c> in openapi.yaml — a browser's aggregated delivery lag (002 FR-011).
/// </summary>
/// <param name="Transport"><c>webSockets</c> or <c>longPolling</c>.</param>
/// <param name="WindowSeconds">How long the counts cover.</param>
/// <param name="ClockOffsetMs">The client-minus-server offset the browser already corrected for. Informational.</param>
/// <param name="Buckets">Non-cumulative counts per upper bound in milliseconds, or <c>+Inf</c>.</param>
public sealed record DeliveryLagReportRequest(
    string Transport,
    int WindowSeconds,
    int? ClockOffsetMs,
    Dictionary<string, long> Buckets);
