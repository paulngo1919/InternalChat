using System.Text.Json;
using InternalChat.Api.Hubs;
using InternalChat.Application.Abstractions;
using Microsoft.AspNetCore.SignalR;

namespace InternalChat.Api.Consumers;

/// <summary>
/// T191 — announces meeting start and end over SignalR (FR-041, FR-047).
/// </summary>
/// <remarks>
/// <para>
/// <b>Sent to the conversation's group, not to individuals.</b> A meeting belongs to a conversation,
/// so its membership is exactly the right audience — and the group is already maintained by
/// <c>MembershipChangeConsumer</c>, so someone removed from the conversation stops receiving join
/// prompts for its meetings without this consumer knowing anything about membership.
/// </para>
/// <para>
/// <b><c>MeetingEnded</c> exists because nothing a client can observe locally tells it the prompt
/// is stale.</b> A meeting ends when the room empties (FR-047), which may be long after the reader
/// stopped paying attention — without this event a conversation would show "join the meeting" for
/// a call that finished an hour ago, and clicking it would produce a refusal nobody could explain.
/// </para>
/// <para>
/// Hosted in the API rather than the Worker, like every other real-time fan-out here: only a
/// process holding a WebSocket can write to it.
/// </para>
/// </remarks>
public sealed partial class MeetingLifecycleConsumer : IMessageConsumer
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly IHubContext<ChatHub> _hub;
    private readonly ILogger<MeetingLifecycleConsumer> _logger;

    /// <summary>Creates the consumer.</summary>
    public MeetingLifecycleConsumer(IHubContext<ChatHub> hub, ILogger<MeetingLifecycleConsumer> logger)
    {
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(logger);

        _hub = hub;
        _logger = logger;
    }

    /// <inheritdoc />
    public string QueueName => "meetings.lifecycle";

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

                await _hub.Clients
                    .Group(ChatHub.GroupFor(payload.ConversationId))
                    .SendAsync(
                        ChatHubEvents.MeetingStarted,
                        new
                        {
                            meetingId = payload.MeetingId,
                            conversationId = payload.ConversationId,
                            startedBy = payload.StartedBy,
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

                MeetingAnnounced(_logger, payload.MeetingId, payload.ConversationId);
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

                await _hub.Clients
                    .Group(ChatHub.GroupFor(payload.ConversationId))
                    .SendAsync(
                        ChatHubEvents.MeetingEnded,
                        new { meetingId = payload.MeetingId, conversationId = payload.ConversationId },
                        cancellationToken)
                    .ConfigureAwait(false);

                break;
            }

            default:
                // The binding is chat.meeting.#, so anything else this system adds under that
                // prefix arrives here. Acknowledged and ignored rather than thrown — throwing would
                // put our own future events in the dead letters.
                break;
        }
    }

    [LoggerMessage(EventId = 4601, Level = LogLevel.Information, Message = "Announced meeting {MeetingId} to conversation {ConversationId}")]
    private static partial void MeetingAnnounced(ILogger logger, Guid meetingId, Guid conversationId);

    private sealed record MeetingStartedPayload(Guid MeetingId, Guid ConversationId, Guid StartedBy);

    private sealed record MeetingEndedPayload(
        Guid MeetingId,
        Guid ConversationId,
        DateTimeOffset StartedAt,
        DateTimeOffset EndedAt,
        int PeakParticipants);
}
