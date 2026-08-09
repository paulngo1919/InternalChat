using InternalChat.Application.Telemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace InternalChat.Api.Observability;

/// <summary>
/// Configures OpenTelemetry traces, metrics, and logs.
/// </summary>
/// <remarks>
/// <para>
/// Constitution stack table: "OpenTelemetry (traces, metrics, logs) — W3C trace context
/// propagated across HTTP, SignalR, and RabbitMQ." Everything exports over OTLP to a
/// self-hosted collector; no hosted APM, which Principle VIII forbids and CI gate 10 greps for.
/// </para>
/// <para>
/// The RabbitMQ half is the part that usually gets missed. A trace that stops at the HTTP
/// response tells you a message was accepted but nothing about whether the notification was ever
/// delivered — and with a transactional outbox that work happens in a different process minutes
/// later. The <c>traceparent</c> captured at outbox-write time is what stitches the two ends
/// together.
/// </para>
/// <para>
/// SignalR needs no separate instrumentation package: a hub invocation arrives over the negotiated
/// HTTP connection, so ASP.NET Core instrumentation already opens the server span and reads the
/// incoming <c>traceparent</c>. What it does not do is name the hub method — that span is added by
/// the hub itself in T097, on <see cref="ChatTelemetry.ActivitySource"/>, which is already
/// subscribed to below.
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
                    .AddAspNetCoreInstrumentation(o =>
                    {
                        // Health probes run every few seconds forever and would otherwise
                        // dominate the trace volume while carrying no information.
                        o.Filter = context =>
                            !context.Request.Path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase);

                        o.RecordException = true;
                    })
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
                    .AddAspNetCoreInstrumentation()
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

                // Formatted message only. Structured state can carry whatever the caller passed,
                // and FR-056 forbids message bodies, attachment contents, and credentials
                // reaching the log pipeline — LoggingBehavior logs request type names for the
                // same reason.
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
