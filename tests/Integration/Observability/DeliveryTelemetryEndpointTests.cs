using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using InternalChat.Application.Telemetry;
using InternalChat.Infrastructure.Persistence;
using InternalChat.IntegrationTests.Fixtures;

namespace InternalChat.IntegrationTests.Observability;

/// <summary>
/// 002 T048 — <c>POST /api/v1/telemetry/delivery</c> against contracts/delivery-telemetry.openapi.yaml.
/// </summary>
/// <remarks>
/// Through the real API: the policy, the validator, the rate limiter, and the histogram it feeds.
/// The status codes are the contract — the browser's reporter drops a report on anything but 202
/// and never retries, so a 500 where a 400 belongs would be invisible to it.
/// </remarks>
public sealed class DeliveryTelemetryEndpointTests : MessagingTestBase
{
    private static readonly Uri Endpoint = new("/api/v1/telemetry/delivery", UriKind.Relative);

    public DeliveryTelemetryEndpointTests(StackFixture stack)
        : base(stack)
    {
    }

    [Fact]
    public async Task A_valid_report_is_accepted_and_reaches_the_client_lag_histogram()
    {
        ConcurrentQueue<(double Value, string? Transport)> recorded = new();

        using MeterListener listener = new();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == ChatTelemetry.MeterName && instrument.Name == ChatTelemetry.Metrics.ClientLag)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((_, value, tags, _) =>
        {
            string? transport = null;
            foreach (KeyValuePair<string, object?> tag in tags)
            {
                if (tag.Key == ChatTelemetry.Metrics.Transport)
                {
                    transport = tag.Value as string;
                }
            }

            recorded.Enqueue((value, transport));
        });
        listener.Start();

        using HttpClient client = await SignedInAsync("an.nguyen");

        using HttpResponseMessage response = await client.PostAsJsonAsync(Endpoint, new
        {
            transport = "longPolling",
            windowSeconds = 60,
            clockOffsetMs = -12,
            buckets = new Dictionary<string, long> { ["100"] = 2, ["+Inf"] = 1 },
        });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(3, recorded.Count);
        Assert.All(recorded, r => Assert.Equal("longPolling", r.Transport));
        Assert.Equal(2, recorded.Count(r => r.Value == 100));
    }

    [Fact]
    public async Task An_invalid_report_is_a_400_with_a_problem_body()
    {
        using HttpClient client = await SignedInAsync("binh.tran");

        using HttpResponseMessage response = await client.PostAsJsonAsync(Endpoint, new
        {
            transport = "carrierPigeon",
            windowSeconds = 60,
            buckets = new Dictionary<string, long> { ["150"] = 1 },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task An_unauthenticated_report_is_refused()
    {
        using HttpClient anonymous = Api.CreateClient();

        using HttpResponseMessage response = await anonymous.PostAsJsonAsync(Endpoint, new
        {
            transport = "webSockets",
            windowSeconds = 60,
            buckets = new Dictionary<string, long> { ["50"] = 1 },
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_third_report_within_a_minute_is_rate_limited()
    {
        using HttpClient client = await SignedInAsync("chi.le");

        var report = new
        {
            transport = "webSockets",
            windowSeconds = 60,
            buckets = new Dictionary<string, long> { ["50"] = 1 },
        };

        List<HttpStatusCode> statuses = [];

        for (int i = 0; i < 3; i++)
        {
            using HttpResponseMessage response = await client.PostAsJsonAsync(Endpoint, report);
            statuses.Add(response.StatusCode);
        }

        Assert.Equal([HttpStatusCode.Accepted, HttpStatusCode.Accepted, HttpStatusCode.TooManyRequests], statuses);
    }

    private async Task<HttpClient> SignedInAsync(string username)
    {
        await using (ChatDbContext context = CreateDbContext())
        {
            await TestData.SeedEmployeeAsync(context, username);
        }

        return await AuthenticatedClientAsync(username);
    }
}
