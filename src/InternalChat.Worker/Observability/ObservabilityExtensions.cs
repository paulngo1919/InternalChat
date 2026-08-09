using InternalChat.Application.Telemetry;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace InternalChat.Worker.Observability;

/// <summary>
/// Configures OpenTelemetry traces, metrics, and logs for the Worker host.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately separate from the Api's equivalent rather than shared. The two hosts do not
/// instrument the same things — the Worker serves no HTTP and would gain nothing from ASP.NET Core
/// instrumentation — and the only place that could hold shared setup is Infrastructure, which
/// Principle I forbids either host from naming outside <c>Program.cs</c>. What genuinely must
/// match is the activity source and meter names, and those come from
/// <see cref="ChatTelemetry"/> in Application, so a trace cannot be split by a typo.
/// </para>
/// <para>
/// This host is where the second half of every trace lives. A request that sends a message ends at
/// the HTTP response; the outbox dispatch, notification fan-out, and attachment scan that follow
/// happen here, seconds or minutes later, as children of the original span via the
/// <c>traceparent</c> carried on the message.
/// </para>
/// </remarks>
public static class ObservabilityExtensions
{
    /// <summary>Adds tracing, metrics, and log export.</summary>
    public static IServiceCollection AddChatObservability(
        this IServiceCollection services,
        IConfiguration configuration,
        string serviceName)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Absent endpoint means telemetry is collected in-process and dropped. That is the right
        // default for a unit test or a bare `dotnet run`: the observability stack is a separate
        // Compose file precisely because the platform must run without it.
        string? otlpEndpoint = configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];

        ResourceBuilder resource = ResourceBuilder.CreateDefault()
            .AddService(
                serviceName: serviceName,
                serviceNamespace: configuration["OTEL_SERVICE_NAMESPACE"] ?? "internalchat",
                serviceVersion: typeof(ObservabilityExtensions).Assembly.GetName().Version?.ToString())
            .AddEnvironmentVariableDetector();

        services.AddOpenTelemetry()
            .WithTracing(tracing =>
            {
                tracing
                    .SetResourceBuilder(resource)
                    .AddSource(ChatTelemetry.ActivitySourceName)
                    .AddHttpClientInstrumentation();

                if (!string.IsNullOrWhiteSpace(otlpEndpoint))
                {
                    tracing.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint));
                }
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .SetResourceBuilder(resource)
                    .AddMeter(ChatTelemetry.MeterName)
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation();

                if (!string.IsNullOrWhiteSpace(otlpEndpoint))
                {
                    metrics.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint));
                }
            });

        services.AddLogging(logging =>
        {
            logging.AddOpenTelemetry(o =>
            {
                o.SetResourceBuilder(resource);
                o.IncludeScopes = true;

                // Formatted message only — see the Api's equivalent. FR-056 keeps message bodies,
                // attachment contents, and credentials out of the log pipeline, and this host
                // handles all three.
                o.IncludeFormattedMessage = true;
                o.ParseStateValues = false;

                if (!string.IsNullOrWhiteSpace(otlpEndpoint))
                {
                    o.AddOtlpExporter(exporter => exporter.Endpoint = new Uri(otlpEndpoint));
                }
            });
        });

        return services;
    }
}
