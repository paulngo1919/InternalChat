using InternalChat.Domain.Common;
using InternalChat.Domain.Meetings;

namespace InternalChat.UnitTests.Domain;

/// <summary>
/// T179 — the 25-participant cap, the platform ceiling's boundary, and refusal behaviour
/// (FR-042, FR-043, FR-044, FR-047).
/// </summary>
/// <remarks>
/// <para>
/// <b>Only one of the two ceilings is testable here, and the split is deliberate.</b> The
/// 25-participant cap is a property of a single room, so the entity owns it. The 1,250 platform-wide
/// ceiling is a property of every room at once and lives in Redis as a capacity guard
/// (data-model.md) — asserting it needs the real store, which is
/// <c>tests/Integration/Meetings/</c>. What is asserted here is that the entity refuses rather than
/// degrades, which is the half FR-044 makes a correctness claim about.
/// </para>
/// <para>
/// The most valuable test in this file is the idempotent-join one. LiveKit delivers webhooks at
/// least once and a reconnecting client produces a second <c>participant_joined</c> for someone who
/// never appeared to leave — counting that twice would walk the room towards its cap until it
/// refused people who were not there, and nothing about the meeting would look wrong until it did.
/// </para>
/// </remarks>
public sealed class MeetingCapacityTests : UnitTestBase
{
    private static readonly Guid ConversationId = Guid.CreateVersion7();
    private static readonly Guid Starter = Guid.CreateVersion7();

    private static Meeting Started(IClock clock) =>
        Meeting.Start(Guid.CreateVersion7(), ConversationId, Starter, clock);

    [Fact]
    public void A_new_meeting_is_active_and_empty()
    {
        TestClock clock = new();
        Meeting meeting = Started(clock);

        Assert.True(meeting.IsActive);
        Assert.Equal(0, meeting.ActiveParticipantCount);
        Assert.True(meeting.HasCapacity);

        MeetingStarted started = Assert.Single(meeting.DomainEvents.OfType<MeetingStarted>());
        Assert.Equal("chat.meeting.started.v1", started.EventType);
    }

    [Fact]
    public void The_room_accepts_exactly_twenty_five_and_refuses_the_twenty_sixth()
    {
        TestClock clock = new();
        Meeting meeting = Started(clock);

        for (int index = 0; index < Meeting.MaximumParticipants; index++)
        {
            meeting.Join(Guid.CreateVersion7(), clock);
        }

        Assert.Equal(Meeting.MaximumParticipants, meeting.ActiveParticipantCount);
        Assert.False(meeting.HasCapacity);

        // FR-044: refusal, not degradation. A meeting that silently dropped the 26th person's video
        // is worse than one that tells them it is full.
        MeetingFullException full = Assert.Throws<MeetingFullException>(() =>
            meeting.Join(Guid.CreateVersion7(), clock));

        Assert.Equal(Meeting.MaximumParticipants, full.Capacity);
        Assert.Contains("25", full.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_rejoining_participant_does_not_consume_a_second_place()
    {
        TestClock clock = new();
        Meeting meeting = Started(clock);
        Guid employee = Guid.CreateVersion7();

        meeting.Join(employee, clock);
        meeting.Join(employee, clock);
        meeting.Join(employee, clock);

        // At-least-once webhook delivery makes this the normal case, not an edge one. Counting it
        // three times would walk the room towards its cap invisibly.
        Assert.Equal(1, meeting.ActiveParticipantCount);
        Assert.Single(meeting.Participants);
    }

    [Fact]
    public void Leaving_frees_a_place()
    {
        TestClock clock = new();
        Meeting meeting = Started(clock);

        List<Guid> employees = [];

        for (int index = 0; index < Meeting.MaximumParticipants; index++)
        {
            Guid employee = Guid.CreateVersion7();
            employees.Add(employee);
            meeting.Join(employee, clock);
        }

        Assert.False(meeting.HasCapacity);

        meeting.Leave(employees[0], clock);

        Assert.True(meeting.HasCapacity);
        meeting.Join(Guid.CreateVersion7(), clock);

        Assert.Equal(Meeting.MaximumParticipants, meeting.ActiveParticipantCount);
    }

    [Fact]
    public void Leaving_is_idempotent_and_the_departure_time_never_moves()
    {
        TestClock clock = new();
        Meeting meeting = Started(clock);
        Guid employee = Guid.CreateVersion7();

        meeting.Join(employee, clock);
        clock.Advance(TimeSpan.FromMinutes(5));
        meeting.Leave(employee, clock);

        DateTimeOffset recorded = meeting.Participants.Single().LeftAt!.Value;

        clock.Advance(TimeSpan.FromMinutes(5));
        meeting.Leave(employee, clock);

        // FR-051 reads this to compute duration. A redelivered webhook must not extend it.
        Assert.Equal(recorded, meeting.Participants.Single().LeftAt);
    }

    [Fact]
    public void Rejoining_after_leaving_resumes_the_same_participation()
    {
        TestClock clock = new();
        Meeting meeting = Started(clock);
        Guid employee = Guid.CreateVersion7();

        meeting.Join(employee, clock);
        DateTimeOffset originalJoin = meeting.Participants.Single().JoinedAt;

        clock.Advance(TimeSpan.FromMinutes(2));
        meeting.Leave(employee, clock);

        clock.Advance(TimeSpan.FromSeconds(30));
        meeting.Join(employee, clock);

        Participation participation = Assert.Single(meeting.Participants);

        Assert.True(participation.IsActive);

        // JoinedAt does NOT move. For someone who dropped and reconnected, the honest answer to
        // "how long were they in the meeting" spans the whole time (FR-051); resetting it would
        // shorten every reported duration by however long the reconnect took.
        Assert.Equal(originalJoin, participation.JoinedAt);
    }

    [Fact]
    public void The_peak_is_a_high_water_mark_rather_than_a_current_count()
    {
        TestClock clock = new();
        Meeting meeting = Started(clock);

        List<Guid> employees = [];

        for (int index = 0; index < 10; index++)
        {
            Guid employee = Guid.CreateVersion7();
            employees.Add(employee);
            meeting.Join(employee, clock);
        }

        foreach (Guid employee in employees.Take(7))
        {
            meeting.Leave(employee, clock);
        }

        Assert.Equal(3, meeting.ActiveParticipantCount);

        // The current count is derivable; the peak is not — it is gone the moment someone leaves,
        // and it is the number that tells an administrator whether 25 is the right ceiling (FR-043).
        Assert.Equal(10, meeting.PeakParticipants);
    }

    [Fact]
    public void A_meeting_continues_when_the_starter_leaves()
    {
        TestClock clock = new();
        Meeting meeting = Started(clock);
        Guid other = Guid.CreateVersion7();

        meeting.Join(Starter, clock);
        meeting.Join(other, clock);

        meeting.Leave(Starter, clock);

        // FR-047. Nobody ends a meeting by leaving it — the room does, when LiveKit says it is
        // empty. A design where the starter's departure ended it makes every meeting hostage to
        // whoever happened to click first.
        Assert.True(meeting.IsActive);
        Assert.Equal(1, meeting.ActiveParticipantCount);
    }

    [Fact]
    public void Ending_closes_every_open_participation_at_the_same_instant()
    {
        TestClock clock = new();
        Meeting meeting = Started(clock);

        meeting.Join(Guid.CreateVersion7(), clock);
        meeting.Join(Guid.CreateVersion7(), clock);

        clock.Advance(TimeSpan.FromMinutes(30));
        meeting.End(clock);

        Assert.False(meeting.IsActive);
        Assert.Equal(0, meeting.ActiveParticipantCount);

        // Without this, a participant whose leave webhook was lost would have an open-ended
        // participation and an infinite duration in the audit record (FR-051).
        Assert.All(meeting.Participants, p => Assert.Equal(clock.UtcNow, p.LeftAt));
    }

    [Fact]
    public void Ending_is_idempotent_and_the_end_time_never_moves()
    {
        TestClock clock = new();
        Meeting meeting = Started(clock);

        clock.Advance(TimeSpan.FromMinutes(10));
        meeting.End(clock);

        DateTimeOffset endedAt = meeting.EndedAt!.Value;
        MeetingEnded ended = Assert.Single(meeting.DomainEvents.OfType<MeetingEnded>());

        clock.Advance(TimeSpan.FromMinutes(10));
        meeting.End(clock);

        Assert.Equal(endedAt, meeting.EndedAt);

        // And no second event. A redelivered room-finished webhook must not produce a second
        // audit record for one meeting.
        Assert.Single(meeting.DomainEvents.OfType<MeetingEnded>());
        Assert.Equal(ended.EndedAt, endedAt);
    }

    [Fact]
    public void Joining_an_ended_meeting_is_refused()
    {
        TestClock clock = new();
        Meeting meeting = Started(clock);
        meeting.End(clock);

        Assert.Throws<MeetingEndedException>(() => meeting.Join(Guid.CreateVersion7(), clock));
    }

    [Fact]
    public void The_ended_event_carries_what_the_audit_record_needs()
    {
        TestClock clock = new();
        Meeting meeting = Started(clock);

        meeting.Join(Guid.CreateVersion7(), clock);
        clock.Advance(TimeSpan.FromMinutes(45));
        meeting.End(clock);

        MeetingEnded ended = Assert.Single(meeting.DomainEvents.OfType<MeetingEnded>());

        // FR-051: occurrence, participants, and duration. The duration is derivable from the two
        // timestamps, which is why both are on the event rather than an elapsed figure — an elapsed
        // number cannot be re-checked against anything.
        Assert.Equal(meeting.StartedAt, ended.StartedAt);
        Assert.Equal(meeting.EndedAt, ended.EndedAt);
        Assert.Equal(1, ended.PeakParticipants);
        Assert.Equal("chat.meeting.ended.v1", ended.EventType);
    }
}
