using InternalChat.Application.Abstractions;
using InternalChat.Application.Behaviors;
using InternalChat.Domain.Common;
using InternalChat.Domain.Meetings;

namespace InternalChat.Application.Meetings;

/// <summary>A lifecycle event reported by the media server (T189).</summary>
/// <param name="EventType">LiveKit's event name, for example <c>participant_joined</c>.</param>
/// <param name="MeetingId">The room name parsed as a meeting id, or <c>null</c> when unparseable.</param>
/// <param name="EmployeeId">The participant identity, or <c>null</c> for room-level events.</param>
/// <param name="RoomParticipantCount">LiveKit's own count, used to reconcile the capacity guard.</param>
public sealed record ApplyMediaSignal(
    string EventType,
    Guid? MeetingId,
    Guid? EmployeeId,
    int RoomParticipantCount) : ITransactionalRequest;

/// <summary>
/// Applies a media-server lifecycle event to the meeting record (FR-047, FR-051).
/// </summary>
/// <remarks>
/// <para>
/// <b>Every handler here is idempotent, because LiveKit delivers at least once.</b> The entity
/// enforces that — a repeated join does not consume a second place, a repeated leave does not move
/// the departure time, a repeated end does not raise a second event — so this class does not need
/// its own deduplication and deliberately does not have one that could disagree.
/// </para>
/// <para>
/// <b>An unknown room is accepted and ignored.</b> A webhook can arrive for a room this system has
/// no meeting for: a retry after retention swept the row, or a room created directly against
/// LiveKit. Throwing would retry it until it reached the dead letters; there is nothing to fix.
/// </para>
/// <para>
/// <b>The capacity counter is reconciled from the room count on every event</b>, rather than being
/// incremented and decremented independently. LiveKit knows who is actually connected; our counter
/// only estimates it, and taking the authoritative figure whenever it passes by is what stops a lost
/// webhook leaving a phantom participant behind forever.
/// </para>
/// </remarks>
public sealed class ApplyMediaSignalHandler : IUseCase<ApplyMediaSignal, bool>
{
    private readonly IMeetingRepository _meetings;
    private readonly IMeetingCapacityGuard _capacity;
    private readonly IShareSessionStore _shares;
    private readonly IEventPublisher _events;
    private readonly IClock _clock;

    /// <summary>Creates the handler.</summary>
    public ApplyMediaSignalHandler(
        IMeetingRepository meetings,
        IMeetingCapacityGuard capacity,
        IShareSessionStore shares,
        IEventPublisher events,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(meetings);
        ArgumentNullException.ThrowIfNull(capacity);
        ArgumentNullException.ThrowIfNull(shares);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(clock);

        _meetings = meetings;
        _capacity = capacity;
        _shares = shares;
        _events = events;
        _clock = clock;
    }

    /// <inheritdoc />
    /// <returns><c>true</c> when the event changed something.</returns>
    public async Task<bool> HandleAsync(
        ApplyMediaSignal request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.MeetingId is not { } meetingId)
        {
            return false;
        }

        Meeting? meeting = await _meetings.FindAsync(meetingId, cancellationToken).ConfigureAwait(false);

        if (meeting is null)
        {
            // Retention swept it, or the room was created directly against LiveKit. Nothing to fix,
            // so nothing to retry.
            return false;
        }

        bool changed = false;

        switch (request.EventType)
        {
            case "participant_joined" when request.EmployeeId is { } joiner:
                // Wrapped, because a join webhook can legitimately arrive for a room the entity
                // considers full — LiveKit admitted them before we heard about it. Recording the
                // refusal is pointless; the person is already in the room, and the count will be
                // reconciled below from LiveKit's own figure.
                try
                {
                    meeting.Join(joiner, _clock);
                    changed = true;
                }
                catch (Exception exception) when (exception is MeetingFullException or MeetingEndedException)
                {
                    changed = false;
                }

                break;

            case "participant_left" when request.EmployeeId is { } leaver:
            {
                meeting.Leave(leaver, _clock);

                // Someone who leaves cannot still be sharing. Without this the slot stays claimed
                // by an absent participant and nobody else can take it — FR-050's rule applied to
                // a share that no longer exists.
                ShareSession? theirs = await _shares
                    .FindActiveAsync(meeting.Id, cancellationToken)
                    .ConfigureAwait(false);

                if (theirs is not null && theirs.EmployeeId == leaver)
                {
                    theirs.Stop(ShareStopReason.ParticipantLeft, _clock);
                    await _shares.UpdateAsync(theirs, cancellationToken).ConfigureAwait(false);
                }

                changed = true;
                break;
            }

            case "room_finished":
            {
                // Any share still open ends with the meeting. Ordered BEFORE the totals are summed,
                // because a session with no stop time has no duration — leaving it open would drop
                // the final share from the FR-051 figure entirely.
                ShareSession? openShare = await _shares
                    .FindActiveAsync(meeting.Id, cancellationToken)
                    .ConfigureAwait(false);

                if (openShare is not null)
                {
                    openShare.Stop(ShareStopReason.MeetingEnded, _clock);
                    await _shares.UpdateAsync(openShare, cancellationToken).ConfigureAwait(false);
                }

                // T202 — the denormalised per-participant total the audit record reads (FR-051).
                // Summed from the share sessions, which are the authoritative record, rather than
                // accumulated per event: at-least-once delivery makes an accumulated figure depend
                // on redelivery count.
                IReadOnlyDictionary<Guid, int> sharedSeconds = await _shares
                    .SumSharedSecondsAsync(meeting.Id, cancellationToken)
                    .ConfigureAwait(false);

                meeting.RecordSharedScreenSeconds(sharedSeconds);

                // FR-047: the room ending is what ends a meeting, never the starter leaving.
                meeting.End(_clock);
                changed = true;
                break;
            }

            default:
                // room_started, track_published, and everything else LiveKit sends. Accepted so it
                // is not retried; ignored because nothing here depends on it.
                break;
        }

        await _events.PublishAsync(meeting.DomainEvents, cancellationToken).ConfigureAwait(false);
        meeting.ClearDomainEvents();

        // Reconciled from the authoritative figure whenever one passes by. room_finished reports
        // zero for the room, which is correct for it and wrong for the platform — so the platform
        // count is only ever replaced by a figure that describes the platform, never a room.
        if (request.EventType is not "room_finished")
        {
            await _capacity
                .ReconcileAsync(Math.Max(0, request.RoomParticipantCount), cancellationToken)
                .ConfigureAwait(false);
        }

        return changed;
    }
}
