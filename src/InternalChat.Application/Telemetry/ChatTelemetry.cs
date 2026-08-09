using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace InternalChat.Application.Telemetry;

/// <summary>
/// The activity source and meter this platform emits under, plus the names of the spans and
/// attributes that cross a process boundary.
/// </summary>
/// <remarks>
/// <para>
/// This lives in Application rather than in a host because three layers need the same names and
/// none of them may reference each other. Infrastructure creates the messaging spans, the Api and
/// Worker composition roots subscribe to the source, and Principle I forbids either host from
/// naming an Infrastructure type outside <c>Program.cs</c>. Application is the one place all three
/// already depend on.
/// </para>
/// <para>
/// It carries no behaviour and no dependency on OpenTelemetry — <see cref="ActivitySource"/> and
/// <see cref="Meter"/> are BCL types. The choice of exporter stays entirely in the hosts, so the
/// Application layer records that something happened without knowing where it is sent.
/// </para>
/// </remarks>
public static class ChatTelemetry
{
    /// <summary>Activity source for spans the application creates itself.</summary>
    public const string ActivitySourceName = "InternalChat";

    /// <summary>Meter for application metrics.</summary>
    public const string MeterName = "InternalChat";

    /// <summary>Shared activity source. Subscribed to by each host's OpenTelemetry setup.</summary>
    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);

    /// <summary>Shared meter.</summary>
    public static readonly Meter Meter = new(MeterName);

    /// <summary>
    /// Span names for the asynchronous hop. Following the OpenTelemetry messaging convention of
    /// <c>{destination} {operation}</c> so a trace viewer groups them without extra configuration.
    /// </summary>
    public static class Spans
    {
        /// <summary>Publishing one outbox row to the broker.</summary>
        public const string OutboxPublish = "outbox publish";

        /// <summary>Handling one delivery, as a child of the request that produced it.</summary>
        public const string ConsumeMessage = "consume";
    }

    /// <summary>Attribute keys attached to messaging spans.</summary>
    /// <remarks>
    /// Deliberately identifiers and counts only. FR-056 forbids message bodies reaching the
    /// telemetry pipeline, and a span attribute is exactly as permanent as a log line — the
    /// collector strips <c>message.body</c> again on the way out as defence in depth, but nothing
    /// here should ever rely on that.
    /// </remarks>
    public static class Attributes
    {
        /// <summary>Messaging system, per OpenTelemetry semantic conventions.</summary>
        public const string MessagingSystem = "messaging.system";

        /// <summary>Event type name, e.g. <c>chat.message.sent.v1</c>.</summary>
        public const string MessageType = "messaging.message.type";

        /// <summary>Message identity — also the consumer's idempotency key.</summary>
        public const string MessageId = "messaging.message.id";

        /// <summary>Queue or routing key.</summary>
        public const string Destination = "messaging.destination.name";

        /// <summary>Delivery attempt, starting at 1. Above 1 means a retry is in progress.</summary>
        public const string Attempt = "messaging.delivery.attempt";

        /// <summary>True when the delivery was recognised as already processed and skipped.</summary>
        public const string Duplicate = "messaging.delivery.duplicate";
    }
}
