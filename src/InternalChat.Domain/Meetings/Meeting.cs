using InternalChat.Domain.Common;

namespace InternalChat.Domain.Meetings;

/// <summary>
/// One video meeting, started from a conversation (FR-041).
/// </summary>
/// <remarks>
/// <para>
/// <b>The id is also the LiveKit room name</b> (data-model.md). One identifier rather than a
/// mapping table: a meeting and its room have exactly the same lifetime, and a second identifier
/// would be a thing that can disagree — a webhook naming a room this system cannot resolve is
/// indistinguishable from an attack.
/// </para>
/// <para>
/// <b>Ending is driven by LiveKit's webhooks, not by the starter leaving</b> (FR-047). That is why
/// <see cref="End"/> takes no actor: nobody ends a meeting, the room does when it empties. A
/// design in which the starter's departure ended it would make every meeting hostage to whoever
/// happened to click first.
/// </para>
/// <para>
/// <b>The 25-participant cap lives here; the 1,250 platform ceiling does not.</b> This entity knows
/// its own room, so it can refuse a 26th join (FR-042). The platform-wide count is a property of
/// every room at once and lives in Redis as a capacity guard (FR-043, FR-044) — putting it here
/// would require this entity to know about meetings it has no relationship to.
/// </para>
/// </remarks>
public sealed class Meeting : Entity<Guid>
{
    private readonly List<Participation> _participants = [];

    private Meeting(Guid id, Guid conversationId, Guid startedBy, DateTimeOffset startedAt)
        : base(id)
    {
        ConversationId = conversationId;
        StartedBy = startedBy;
        StartedAt = startedAt;
    }

    /// <summary>
    /// Most participants a single meeting may hold (FR-042).
    /// </summary>
    /// <remarks>
    /// 25, all with two-way audio and video. Not a soft target: FR-044 requires refusal rather than
    /// degradation, because a meeting that silently drops the 26th person's video is worse than one
    /// that tells them it is full.
    /// </remarks>
    public const int MaximumParticipants = 25;

    /// <summary>The conversation this was started from. The membership scope for joining (FR-041).</summary>
    public Guid ConversationId { get; private set; }

    /// <summary>Who started it. Carries no special rights afterwards — see FR-047.</summary>
    public Guid StartedBy { get; private set; }

    /// <summary>When it started.</summary>
    public DateTimeOffset StartedAt { get; private set; }

    /// <summary>When it ended, or <c>null</c> while active.</summary>
    public DateTimeOffset? EndedAt { get; private set; }

    /// <summary>Whether the room is still open.</summary>
    public bool IsActive => EndedAt is null;

    /// <summary>
    /// Highest simultaneous participant count observed, for capacity observability (FR-043).
    /// </summary>
    /// <remarks>
    /// A high-water mark rather than a current count, because the current count is derivable from
    /// <see cref="ActiveParticipantCount"/> and the peak is not — it is gone the moment someone
    /// leaves, and it is the number that tells an administrator whether 25 is the right ceiling.
    /// </remarks>
    public int PeakParticipants { get; private set; }

    /// <summary>Everyone who has ever joined, including those who have left (FR-051).</summary>
    public IReadOnlyList<Participation> Participants => _participants.AsReadOnly();

    /// <summary>How many are in the room right now.</summary>
    public int ActiveParticipantCount => _participants.Count(p => p.IsActive);

    /// <summary>Whether the room has space for one more (FR-042).</summary>
    public bool HasCapacity => ActiveParticipantCount < MaximumParticipants;

    /// <summary>Starts a meeting in a conversation.</summary>
    public static Meeting Start(Guid id, Guid conversationId, Guid startedBy, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        Meeting meeting = new(id, conversationId, startedBy, clock.UtcNow);

        meeting.Raise(new MeetingStarted(
            Guid.CreateVersion7(), clock.UtcNow, id, conversationId, startedBy));

        return meeting;
    }

    /// <summary>
    /// Records a participant joining.
    /// </summary>
    /// <remarks>
    /// <b>Idempotent on a participant who is already in the room.</b> LiveKit delivers webhooks at
    /// least once, and a reconnecting client produces a second <c>participant_joined</c> for
    /// someone who never appeared to leave. Treating that as a new participant would inflate the
    /// count towards the cap until the room refused people who were not there.
    /// </remarks>
    /// <exception cref="MeetingFullException">The room already holds 25 (FR-042).</exception>
    /// <exception cref="MeetingEndedException">The meeting is over.</exception>
    public Participation Join(Guid employeeId, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        if (!IsActive)
        {
            throw new MeetingEndedException(Id);
        }

        Participation? existing = _participants.FirstOrDefault(p => p.EmployeeId == employeeId);

        if (existing is { IsActive: true })
        {
            return existing;
        }

        if (!HasCapacity)
        {
            throw new MeetingFullException(Id, MaximumParticipants);
        }

        if (existing is not null)
        {
            // Rejoining after leaving. The same row resumes rather than a second one being created,
            // so FR-051's duration figure stays one span per person per meeting.
            existing.Rejoin(clock);
        }
        else
        {
            _participants.Add(Participation.Join(Id, employeeId, clock));
        }

        PeakParticipants = Math.Max(PeakParticipants, ActiveParticipantCount);

        return _participants.First(p => p.EmployeeId == employeeId);
    }

    /// <summary>
    /// Records a participant leaving. Idempotent, and never ends the meeting (FR-047).
    /// </summary>
    public void Leave(Guid employeeId, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        _participants.FirstOrDefault(p => p.EmployeeId == employeeId && p.IsActive)?.Leave(clock);
    }

    /// <summary>
    /// Records how long each participant spent sharing their screen (FR-051, T202).
    /// </summary>
    /// <param name="secondsByEmployee">Totals, as the share-session store summed them.</param>
    /// <remarks>
    /// <para>
    /// <b>Set from an authoritative total rather than accumulated incrementally.</b> The share
    /// sessions are the record of what happened; this is a denormalised copy kept on the
    /// participation so the audit answer needs no join. Adding to it per event would make the value
    /// depend on how many times a webhook was redelivered, which for an at-least-once transport is
    /// not a number anyone should record.
    /// </para>
    /// <para>
    /// Someone with no shares is simply absent from the map and keeps zero. An id that matches no
    /// participation is ignored rather than throwing — a share by someone whose participation row
    /// was never created is a data oddity, not a reason to fail the meeting's audit record.
    /// </para>
    /// </remarks>
    public void RecordSharedScreenSeconds(IReadOnlyDictionary<Guid, int> secondsByEmployee)
    {
        ArgumentNullException.ThrowIfNull(secondsByEmployee);

        foreach ((Guid employeeId, int seconds) in secondsByEmployee)
        {
            _participants.FirstOrDefault(p => p.EmployeeId == employeeId)?.SetSharedScreenSeconds(seconds);
        }
    }

    /// <summary>
    /// Ends the meeting, driven by LiveKit's room-finished webhook (FR-047).
    /// </summary>
    /// <remarks>
    /// Idempotent, and the timestamp never moves — the same reasoning as <c>Message.Delete</c>.
    /// <see cref="EndedAt"/> is what an auditor reads to establish the meeting's duration (FR-051),
    /// and a redelivered webhook must not rewrite that answer.
    /// </remarks>
    public void End(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        if (!IsActive)
        {
            return;
        }

        EndedAt = clock.UtcNow;

        // Anyone still marked present is marked gone at the same instant. Without this a
        // participant whose leave webhook was lost would have an open-ended participation and an
        // infinite duration in the audit record.
        foreach (Participation participant in _participants.Where(p => p.IsActive))
        {
            participant.Leave(clock);
        }

        Raise(new MeetingEnded(
            Guid.CreateVersion7(),
            clock.UtcNow,
            Id,
            ConversationId,
            StartedAt,
            EndedAt.Value,
            PeakParticipants));
    }
}

/// <summary>
/// One person's presence in one meeting (FR-051).
/// </summary>
/// <remarks>
/// Not an <see cref="Entity{TId}"/>: its identity is the pair <c>(meeting, employee)</c>, which is
/// also its primary key, and it has no life outside its meeting.
/// </remarks>
public sealed class Participation
{
    private Participation(Guid meetingId, Guid employeeId, DateTimeOffset joinedAt)
    {
        MeetingId = meetingId;
        EmployeeId = employeeId;
        JoinedAt = joinedAt;
    }

    /// <summary>The meeting.</summary>
    public Guid MeetingId { get; private set; }

    /// <summary>The participant.</summary>
    public Guid EmployeeId { get; private set; }

    /// <summary>When they first joined.</summary>
    public DateTimeOffset JoinedAt { get; private set; }

    /// <summary>When they left, or <c>null</c> while present.</summary>
    public DateTimeOffset? LeftAt { get; private set; }

    /// <summary>Whether they are in the room now.</summary>
    public bool IsActive => LeftAt is null;

    /// <summary>Seconds spent sharing a screen, accumulated across sessions (FR-051).</summary>
    public int SharedScreenSeconds { get; private set; }

    internal static Participation Join(Guid meetingId, Guid employeeId, IClock clock) =>
        new(meetingId, employeeId, clock.UtcNow);

    internal void Leave(IClock clock) => LeftAt ??= clock.UtcNow;

    /// <summary>
    /// Resumes an ended participation rather than creating a second one.
    /// </summary>
    /// <remarks>
    /// <b><see cref="JoinedAt"/> deliberately does not move.</b> FR-051 records "the occurrence,
    /// participants, and duration": for someone who dropped and reconnected, the honest answer to
    /// "how long were they in the meeting" spans the whole time, not just the last segment. Resetting
    /// it would shorten every reported duration by however long the reconnect took.
    /// </remarks>
    internal void Rejoin(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        LeftAt = null;
    }

    /// <summary>
    /// Sets the screen-sharing total from the authoritative sum of this person's share sessions.
    /// </summary>
    /// <remarks>
    /// Assignment rather than addition, deliberately. The share sessions are the record; this is a
    /// denormalised copy. Accumulating would make the stored value depend on how many times the
    /// meeting-ended webhook was redelivered — and at-least-once delivery means that is not a
    /// number worth recording in an audit trail.
    /// </remarks>
    internal void SetSharedScreenSeconds(int seconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(seconds);
        SharedScreenSeconds = seconds;
    }
}

/// <summary>Raised when a meeting starts (<c>chat.meeting.started.v1</c>).</summary>
public sealed record MeetingStarted(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid MeetingId,
    Guid ConversationId,
    Guid StartedBy) : DomainEvent(EventId, OccurredAt)
{
    /// <inheritdoc />
    public override string EventType => "chat.meeting.started.v1";
}

/// <summary>Raised when a meeting ends (<c>chat.meeting.ended.v1</c>).</summary>
/// <param name="PeakParticipants">The high-water mark, for capacity observability (FR-043).</param>
public sealed record MeetingEnded(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid MeetingId,
    Guid ConversationId,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    int PeakParticipants) : DomainEvent(EventId, OccurredAt)
{
    /// <inheritdoc />
    public override string EventType => "chat.meeting.ended.v1";
}

/// <summary>Thrown when a 26th participant tries to join (FR-042).</summary>
/// <remarks>
/// Its own exception because the response code matters: this is a capacity refusal the caller can
/// act on by waiting, not a permission problem and not a malformed request.
/// </remarks>
public sealed class MeetingFullException : InvalidOperationException
{
    /// <summary>Creates the exception.</summary>
    public MeetingFullException(Guid meetingId, int capacity)
        : base($"This meeting is full ({capacity} participants).")
    {
        MeetingId = meetingId;
        Capacity = capacity;
    }

    /// <summary>Creates the exception.</summary>
    public MeetingFullException()
    {
    }

    /// <summary>Creates the exception.</summary>
    public MeetingFullException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public MeetingFullException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The full meeting.</summary>
    public Guid MeetingId { get; }

    /// <summary>The per-meeting ceiling.</summary>
    public int Capacity { get; }
}

/// <summary>Thrown when someone tries to join or change a meeting that is over.</summary>
public sealed class MeetingEndedException : InvalidOperationException
{
    /// <summary>Creates the exception.</summary>
    public MeetingEndedException(Guid meetingId)
        : base("This meeting has ended.") => MeetingId = meetingId;

    /// <summary>Creates the exception.</summary>
    public MeetingEndedException()
    {
    }

    /// <summary>Creates the exception.</summary>
    public MeetingEndedException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public MeetingEndedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The meeting.</summary>
    public Guid MeetingId { get; }
}
