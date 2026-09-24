using InternalChat.Application.Abstractions;
using InternalChat.Application.Behaviors;
using InternalChat.Domain.Common;
using InternalChat.Domain.Meetings;

namespace InternalChat.Application.Meetings;

/// <summary>Starts a meeting in a conversation, or returns the one already running (FR-041).</summary>
public sealed record StartMeeting(Guid ConversationId, Guid StartedBy) : ITransactionalRequest;

/// <summary>The meeting and whether this call created it.</summary>
/// <param name="WasCreated">
/// <c>false</c> when a meeting was already running. The endpoint turns it into 201 or 200, so a
/// client can tell "you started this" from "you are joining what is already happening".
/// </param>
public sealed record StartMeetingResult(Meeting Meeting, bool WasCreated);

/// <summary>
/// Starts a meeting after checking membership, media-host availability, and platform capacity.
/// </summary>
/// <remarks>
/// <para>
/// <b>One live meeting per conversation, and a second attempt joins rather than competing.</b>
/// Nothing in the requirements asks for concurrent meetings in one conversation, and allowing them
/// produces the failure everyone has seen: two people click "start", and the conversation now holds
/// two rooms with half the participants in each and no way to tell which is the real one.
/// </para>
/// <para>
/// <b>The three checks are ordered by what they cost and what they mean.</b> Membership first,
/// because it is the authorization decision and nothing else should run for someone who may not be
/// here. Availability second, because it is a cached probe and refusing early keeps a down media
/// host from consuming anything further. Capacity last, because it is the only one that can change
/// between the check and the join — and putting it last narrows that window to as little as this
/// code can make it.
/// </para>
/// <para>
/// <b>Capacity is checked but never reserved.</b> FR-044 requires refusal at the ceiling; it does
/// not require the count to be exact, and a reservation would need a compensating release on every
/// path that fails afterwards — including the client simply never connecting. The counter is
/// advanced by the join webhook, which is the point at which someone is genuinely in a room.
/// </para>
/// </remarks>
public sealed class StartMeetingHandler : IUseCase<StartMeeting, StartMeetingResult>
{
    private readonly IMeetingRepository _meetings;
    private readonly IMembershipReader _memberships;
    private readonly IMeetingTokenIssuer _media;
    private readonly IMeetingCapacityGuard _capacity;
    private readonly IEventPublisher _events;
    private readonly IClock _clock;

    /// <summary>Creates the handler.</summary>
    public StartMeetingHandler(
        IMeetingRepository meetings,
        IMembershipReader memberships,
        IMeetingTokenIssuer media,
        IMeetingCapacityGuard capacity,
        IEventPublisher events,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(meetings);
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(media);
        ArgumentNullException.ThrowIfNull(capacity);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(clock);

        _meetings = meetings;
        _memberships = memberships;
        _media = media;
        _capacity = capacity;
        _events = events;
        _clock = clock;
    }

    /// <inheritdoc />
    public async Task<StartMeetingResult> HandleAsync(
        StartMeeting request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // FR-041: only members of the conversation. The same refusal every other resource uses, so
        // a non-member cannot tell a conversation that exists from one that does not (SC-017).
        MembershipSnapshot? membership = await _memberships
            .FindGrantingAsync(request.ConversationId, request.StartedBy, cancellationToken)
            .ConfigureAwait(false);

        if (membership is null)
        {
            throw new UnauthorizedAccessException(
                $"Conversation {request.ConversationId} is not accessible.");
        }

        Meeting? existing = await _meetings
            .FindActiveForConversationAsync(request.ConversationId, cancellationToken)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            // Already running. Returned rather than refused — the caller wanted to be in a meeting
            // in this conversation, and there is one.
            return new StartMeetingResult(existing, WasCreated: false);
        }

        if (!await _media.IsAvailableAsync(cancellationToken).ConfigureAwait(false))
        {
            // The media host is the only component on a second machine. Constitution v1.2.0: the
            // application stays fully functional without it — meetings report unavailable while
            // messaging, attachments, search, and notifications keep working.
            throw new MediaHostUnavailableException();
        }

        if (!await _capacity.HasCapacityAsync(1, cancellationToken).ConfigureAwait(false))
        {
            throw new PlatformAtCapacityException(
                await _capacity.GetCountAsync(cancellationToken).ConfigureAwait(false));
        }

        Meeting meeting = Meeting.Start(
            Guid.CreateVersion7(), request.ConversationId, request.StartedBy, _clock);

        await _meetings.AddAsync(meeting, cancellationToken).ConfigureAwait(false);

        await _events.PublishAsync(meeting.DomainEvents, cancellationToken).ConfigureAwait(false);
        meeting.ClearDomainEvents();

        return new StartMeetingResult(meeting, WasCreated: true);
    }
}

/// <summary>
/// Thrown when the media host is unreachable (constitution v1.2.0, research.md D12). Maps to 503.
/// </summary>
/// <remarks>
/// <b>Deliberately specific to meetings.</b> The whole point of the two-host split is that this
/// condition degrades one feature rather than the platform: a caller seeing this must be able to
/// tell that messaging is fine, and a generic 503 would say the opposite.
/// </remarks>
public sealed class MediaHostUnavailableException : InvalidOperationException
{
    /// <summary>Creates the exception.</summary>
    public MediaHostUnavailableException()
        : base("Meetings are unavailable right now. Messaging, files, and search are unaffected.")
    {
    }

    /// <summary>Creates the exception.</summary>
    public MediaHostUnavailableException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public MediaHostUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>How long a client should wait before retrying.</summary>
    /// <remarks>
    /// Short, because a media host comes back in seconds rather than minutes and the client is a
    /// person waiting to start a call.
    /// </remarks>
    public static int RetryAfterSeconds => 15;
}

/// <summary>
/// Thrown when the platform-wide participant ceiling is reached (FR-043, FR-044). Maps to 503.
/// </summary>
/// <remarks>
/// FR-044 requires refusal rather than degradation. The alternative — admitting everyone and
/// letting quality collapse — produces a platform where every meeting is bad instead of a few
/// meetings being refused, and nobody can tell what is wrong.
/// </remarks>
public sealed class PlatformAtCapacityException : InvalidOperationException
{
    /// <summary>Creates the exception.</summary>
    public PlatformAtCapacityException(int currentParticipants)
        : base("The platform is at its meeting capacity. Please try again shortly.") =>
        CurrentParticipants = currentParticipants;

    /// <summary>Creates the exception.</summary>
    public PlatformAtCapacityException()
    {
    }

    /// <summary>Creates the exception.</summary>
    public PlatformAtCapacityException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public PlatformAtCapacityException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Concurrent participants at the moment of refusal.</summary>
    public int CurrentParticipants { get; }

    /// <summary>How long a client should wait before retrying.</summary>
    /// <remarks>
    /// Longer than the media-host case: capacity frees when a meeting ends, which is minutes rather
    /// than seconds, and a client retrying every few seconds against a full platform adds load to
    /// the thing it is waiting for.
    /// </remarks>
    public static int RetryAfterSeconds => 60;
}
