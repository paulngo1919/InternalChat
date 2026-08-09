using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace InternalChat.Api.RateLimiting;

/// <summary>
/// Rate limit policy names, referenced by endpoints via <c>.RequireRateLimiting(...)</c>.
/// </summary>
public static class RateLimitPolicies
{
    /// <summary>Sign-in and token exchange — the credential-stuffing surface.</summary>
    public const string Authentication = "auth";

    /// <summary>Message send.</summary>
    public const string MessageSend = "send";

    /// <summary>Full-text search.</summary>
    public const string Search = "search";

    /// <summary>Attachment upload reservation.</summary>
    public const string Upload = "upload";
}

/// <summary>
/// Configures per-user and per-IP rate limiting.
/// </summary>
/// <remarks>
/// <para>
/// Constitution, Application Controls: "Rate limiting MUST be applied per user and per IP on
/// authentication, message send, search, and file upload endpoints."
/// </para>
/// <para>
/// This is the second of two layers — nginx limits per IP at the edge (T044). Both exist because
/// they fail differently: the edge limiter still works when the application is saturated, and
/// this one sees the authenticated identity, so one employee cannot exhaust a shared office IP's
/// budget for everyone sitting behind it.
/// </para>
/// </remarks>
public static class RateLimitingExtensions
{
    /// <summary>Adds the rate limiter and its policies.</summary>
    public static IServiceCollection AddChatRateLimiting(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            options.OnRejected = async (context, cancellationToken) =>
            {
                // Retry-After is not decoration. Without it a client backs off by guessing,
                // which in practice means retrying immediately and making the overload worse.
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter =
                        ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                }

                context.HttpContext.Response.ContentType = "application/problem+json";

                await context.HttpContext.Response.WriteAsJsonAsync(
                    new
                    {
                        type = "https://internalchat.invalid/problems/rate-limited",
                        title = "Too many requests.",
                        status = StatusCodes.Status429TooManyRequests,
                        detail = "Slow down and retry after the interval in the Retry-After header.",
                    },
                    cancellationToken).ConfigureAwait(false);
            };

            // Authentication is limited hardest and always by IP: the caller is by definition
            // not yet authenticated, so there is no user to attribute the attempt to.
            options.AddPolicy(RateLimitPolicies.Authentication, httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: ClientIp(httpContext),
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 10,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0, // Queueing failed sign-ins would just delay the rejection.
                    }));

            // Send uses a token bucket rather than a fixed window: bursts are normal and
            // legitimate — someone types three short messages in a row — while the sustained
            // rate still has to stay bounded. A fixed window would reject that natural burst.
            options.AddPolicy(RateLimitPolicies.MessageSend, httpContext =>
                RateLimitPartition.GetTokenBucketLimiter(
                    partitionKey: UserOrIp(httpContext),
                    factory: _ => new TokenBucketRateLimiterOptions
                    {
                        TokenLimit = 30,
                        TokensPerPeriod = 10,
                        ReplenishmentPeriod = TimeSpan.FromSeconds(1),
                        QueueLimit = 5,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        AutoReplenishment = true,
                    }));

            // Search is the most expensive read on the platform (research D9: PostgreSQL
            // full-text over 125 million rows), so it is limited well below ordinary reads.
            options.AddPolicy(RateLimitPolicies.Search, httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: UserOrIp(httpContext),
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 30,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                    }));

            // Upload is limited by concurrency, not by rate. The cost is a 500 MB transfer held
            // open, so what matters is how many are in flight at once, not how often they start.
            options.AddPolicy(RateLimitPolicies.Upload, httpContext =>
                RateLimitPartition.GetConcurrencyLimiter(
                    partitionKey: UserOrIp(httpContext),
                    factory: _ => new ConcurrencyLimiterOptions
                    {
                        PermitLimit = 3,
                        QueueLimit = 2,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    }));

            // Backstop for everything with no explicit policy, so a new endpoint is never
            // completely unlimited by omission.
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: UserOrIp(httpContext),
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 600,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                    }));
        });

        return services;
    }

    /// <summary>
    /// Partitions by authenticated employee, falling back to client IP.
    /// </summary>
    /// <remarks>
    /// Preferring the user id matters in an office: dozens of employees share one egress address,
    /// so an IP-only partition lets one heavy user exhaust the budget for the whole floor.
    /// </remarks>
    private static string UserOrIp(HttpContext httpContext)
    {
        string? subject = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? httpContext.User.FindFirstValue("sub");

        return string.IsNullOrEmpty(subject) ? $"ip:{ClientIp(httpContext)}" : $"user:{subject}";
    }

    private static string ClientIp(HttpContext httpContext) =>
        httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}
