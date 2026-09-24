using System.Globalization;
using System.Text.Json;
using InternalChat.Application.Abstractions;

namespace InternalChat.Worker.Consumers;

/// <summary>
/// T192 — writes the audit record FR-051 requires for every meeting.
/// </summary>
/// <remarks>
/// <para>
/// <b>FR-051 asks for "the occurrence, participants, and duration of every meeting".</b> Start and
/// end are written here; who was in it comes from the <c>participation</c> rows the webhook handler
/// maintains, which is why the end record carries the peak count and the duration rather than a
/// participant list — the list is a query against a table that already holds it, and duplicating it
/// into an audit payload would create a second copy that can disagree.
/// </para>
/// <para>
/// <b>Duration is recorded as a number, not left to be derived.</b> An auditor reading the log
/// should not have to join two records and subtract; and more practically, the start record can be
/// swept by retention before the end record is, which would make the subtraction impossible
/// exactly when someone needs it.
/// </para>
/// <para>
/// <b>Separate from <c>MeetingLifecycleConsumer</c>, on a different host.</b> That one tells people
/// a meeting is happening and must be in the API where the WebSockets are; this one writes durable
/// history and belongs with the other Worker consumers. Folding them together would put an audit
/// write on the real-time path, where a slow database would delay the join prompt.
/// </para>
/// </remarks>
public sealed partial class MeetingAuditConsumer : IMessageConsumer
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly IAuditLog _audit;
    private readonly ILogger<MeetingAuditConsumer> _logger;

    /// <summary>Creates the consumer.</summary>
    public MeetingAuditConsumer(IAuditLog audit, ILogger<MeetingAuditConsumer> logger)
    {
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(logger);

        _audit = audit;
        _logger = logger;
    }

    /// <inheritdoc />
    /// <remarks>
    /// A different queue from <c>meetings.lifecycle</c> even though both bind <c>chat.meeting.#</c>.
    /// One queue consumed by two hosts would give each event to whichever host got there first, so
    /// half the meetings would be announced and the other half audited.
    /// </remarks>
    public string QueueName => "meetings.audit";

    /// <inheritdoc />
    public async Task HandleAsync(
        MessageEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        switch (envelope.Type)
        {
            case "chat.meeting.started.v1":
            {
                MeetingStartedPayload? payload =
                    JsonSerializer.Deserialize<MeetingStartedPayload>(envelope.Payload, SerializerOptions);

                if (payload is null)
                {
                    throw new InvalidOperationException(
                        $"Outbox row {envelope.MessageId} carries an unreadable {envelope.Type} payload.");
                }

                await _audit.RecordAsync(
                    new AuditEntry(
                        Action: "meeting.started",
                        ActorId: payload.StartedBy,
                        SubjectType: "meeting",
                        SubjectId: payload.MeetingId,

                        // No source IP: this runs in the Worker off a queue message. Recording the
                        // Worker's own address would be worse than recording nothing — it looks
                        // like provenance and is not.
                        SourceIp: null,
                        Outcome: AuditOutcome.Success,
                        Detail: new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["conversationId"] = payload.ConversationId.ToString(),
                        }),
                    cancellationToken)
                    .ConfigureAwait(false);

                break;
            }

            case "chat.meeting.ended.v1":
            {
                MeetingEndedPayload? payload =
                    JsonSerializer.Deserialize<MeetingEndedPayload>(envelope.Payload, SerializerOptions);

                if (payload is null)
                {
                    throw new InvalidOperationException(
                        $"Outbox row {envelope.MessageId} carries an unreadable {envelope.Type} payload.");
                }

                int durationSeconds =
                    (int)Math.Max(0, Math.Round((payload.EndedAt - payload.StartedAt).TotalSeconds));

                await _audit.RecordAsync(
                    new AuditEntry(
                        Action: "meeting.ended",

                        // No actor. FR-047: nobody ends a meeting — the room does when it empties,
                        // and naming the last person to leave would attribute a system event to
                        // someone who merely closed a tab.
                        ActorId: null,
                        SubjectType: "meeting",
                        SubjectId: payload.MeetingId,
                        SourceIp: null,
                        Outcome: AuditOutcome.Success,
                        Detail: new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["conversationId"] = payload.ConversationId.ToString(),
                            ["durationSeconds"] = durationSeconds.ToString(CultureInfo.InvariantCulture),
                            ["peakParticipants"] = payload.PeakParticipants.ToString(CultureInfo.InvariantCulture),
                        }),
                    cancellationToken)
                    .ConfigureAwait(false);

                MeetingAudited(_logger, payload.MeetingId, durationSeconds, payload.PeakParticipants);
                break;
            }

            default:
                break;
        }
    }

    [LoggerMessage(EventId = 4701, Level = LogLevel.Information, Message = "Audited meeting {MeetingId}: {DurationSeconds}s, peak {PeakParticipants}")]
    private static partial void MeetingAudited(ILogger logger, Guid meetingId, int durationSeconds, int peakParticipants);

    private sealed record MeetingStartedPayload(Guid MeetingId, Guid ConversationId, Guid StartedBy);

    private sealed record MeetingEndedPayload(
        Guid MeetingId,
        Guid ConversationId,
        DateTimeOffset StartedAt,
        DateTimeOffset EndedAt,
        int PeakParticipants);
}
