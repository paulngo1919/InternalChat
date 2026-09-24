using InternalChat.Application.Abstractions;
using InternalChat.Application.Meetings;
using InternalChat.Domain.Common;
using InternalChat.Domain.Meetings;

namespace InternalChat.UnitTests.Application;

/// <summary>
/// The media-server webhook handler (T189, FR-047, FR-051).
/// </summary>
/// <remarks>
/// <para>
/// <b>Every path here has to survive being delivered twice, and most have to survive being delivered
/// for something that no longer exists.</b> LiveKit delivers at least once; a retry can arrive after
/// retention swept the meeting row, or for a room created directly against LiveKit that this system
/// never knew about. The handler's contract is that neither case throws — throwing would retry the
/// event until it reached the dead letters, where nobody can do anything about it because there is
/// nothing to fix.
/// </para>
/// <para>
/// These are unit tests with in-memory doubles rather than integration tests against a real LiveKit,
/// because what is being asserted is the decision logic — which events change state, which are
/// ignored, and what the capacity counter is set to — none of which needs a media server to be
/// wrong. The webhook's signature verification and payload parsing are tested separately, against
/// the real thing.
/// </para>
/// </remarks>
public sealed class ApplyMediaSignalTests : UnitTestBase
{
    private readonly TestClock _clock = new();
    private readonly FakeMeetingRepository _meetings = new();
    private readonly FakeCapacityGuard _capacity = new();
    private readonly FakeShareStore _shares = new();
    private readonly FakeEventPublisher _events = new();

    private readonly Guid _conversationId = Guid.CreateVersion7();
    private readonly Guid _starter = Guid.CreateVersion7();
    private readonly Guid _joiner = Guid.CreateVersion7();

    [Fact]
    public async Task An_event_for_an_unparseable_room_changes_nothing()
    {
        // MeetingId is null when the room name did not parse as one. Accepted rather than thrown,
        // so LiveKit does not retry a name that will never parse.
        bool changed = await Handle(new ApplyMediaSignal("participant_joined", null, _joiner, 3));

        Assert.False(changed);
        Assert.Equal(0, _capacity.ReconciledTo);
    }

    [Fact]
    public async Task An_event_for_a_meeting_this_system_has_never_heard_of_changes_nothing()
    {
        // Retention swept the row, or the room was created directly against LiveKit. Nothing to
        // fix, so nothing to retry.
        bool changed = await Handle(
            new ApplyMediaSignal("participant_joined", Guid.CreateVersion7(), _joiner, 3));

        Assert.False(changed);
    }

    [Fact]
    public async Task A_join_adds_the_participant_and_reconciles_the_platform_count()
    {
        Meeting meeting = StartMeeting();

        bool changed = await Handle(new ApplyMediaSignal("participant_joined", meeting.Id, _joiner, 7));

        Assert.True(changed);
        Assert.Equal(2, meeting.ActiveParticipantCount);

        // Reconciled from LiveKit's own figure rather than incremented. LiveKit knows who is
        // actually connected; our counter only estimates it, and taking the authoritative number
        // whenever one passes by is what stops a lost webhook leaving a phantom participant behind
        // forever.
        Assert.Equal(7, _capacity.ReconciledTo);
    }

    [Fact]
    public async Task A_repeated_join_does_not_consume_a_second_place()
    {
        Meeting meeting = StartMeeting();

        await Handle(new ApplyMediaSignal("participant_joined", meeting.Id, _joiner, 2));
        await Handle(new ApplyMediaSignal("participant_joined", meeting.Id, _joiner, 2));

        // At-least-once delivery. The entity enforces the idempotency, which is why the handler
        // deliberately has no deduplication of its own that could disagree with it.
        Assert.Equal(2, meeting.ActiveParticipantCount);
    }

    [Fact]
    public async Task A_join_for_a_room_the_entity_considers_full_is_absorbed()
    {
        Meeting meeting = StartMeeting();

        for (int index = 0; index < Meeting.MaximumParticipants - 1; index++)
        {
            meeting.Join(Guid.CreateVersion7(), _clock);
        }

        Assert.Equal(Meeting.MaximumParticipants, meeting.ActiveParticipantCount);

        // LiveKit admitted them before we heard about it. Recording the refusal is pointless — the
        // person is already in the room — and throwing would retry the webhook forever.
        bool changed = await Handle(
            new ApplyMediaSignal("participant_joined", meeting.Id, _joiner, Meeting.MaximumParticipants));

        Assert.False(changed);
        Assert.Equal(Meeting.MaximumParticipants, meeting.ActiveParticipantCount);
    }

    [Fact]
    public async Task A_join_for_a_meeting_that_has_already_ended_is_absorbed()
    {
        Meeting meeting = StartMeeting();
        meeting.End(_clock);
        meeting.ClearDomainEvents();

        bool changed = await Handle(new ApplyMediaSignal("participant_joined", meeting.Id, _joiner, 1));

        Assert.False(changed);
    }

    [Fact]
    public async Task A_leave_releases_the_share_slot_the_departing_participant_held()
    {
        Meeting meeting = StartMeeting();
        meeting.Join(_joiner, _clock);

        ShareSession share = ShareSession.Start(meeting.Id, _joiner, ShareScope.Screen, _clock);
        _shares.Active = share;

        _clock.Advance(TimeSpan.FromMinutes(5));

        bool changed = await Handle(new ApplyMediaSignal("participant_left", meeting.Id, _joiner, 1));

        Assert.True(changed);

        // Someone who leaves cannot still be sharing. Without this the one slot stays claimed by an
        // absent participant and nobody else can take it — FR-050's rule applied to a share that no
        // longer exists.
        Assert.NotNull(share.StoppedAt);
        Assert.Equal(ShareStopReason.ParticipantLeft, share.StopReason);
        Assert.Same(share, _shares.Updated);
    }

    [Fact]
    public async Task A_leave_does_not_stop_somebody_elses_share()
    {
        Meeting meeting = StartMeeting();
        meeting.Join(_joiner, _clock);

        // The starter is sharing; the joiner leaves. Stopping the share here would cut off a
        // presenter because an unrelated person hung up.
        ShareSession share = ShareSession.Start(meeting.Id, _starter, ShareScope.Screen, _clock);
        _shares.Active = share;

        await Handle(new ApplyMediaSignal("participant_left", meeting.Id, _joiner, 1));

        Assert.Null(share.StoppedAt);
        Assert.Null(_shares.Updated);
    }

    [Fact]
    public async Task A_leave_with_nobody_sharing_is_fine()
    {
        Meeting meeting = StartMeeting();
        meeting.Join(_joiner, _clock);

        bool changed = await Handle(new ApplyMediaSignal("participant_left", meeting.Id, _joiner, 1));

        Assert.True(changed);
        Assert.Null(_shares.Updated);
    }

    [Fact]
    public async Task The_room_finishing_ends_the_meeting()
    {
        Meeting meeting = StartMeeting();
        meeting.Join(_joiner, _clock);

        _clock.Advance(TimeSpan.FromMinutes(30));

        bool changed = await Handle(new ApplyMediaSignal("room_finished", meeting.Id, null, 0));

        Assert.True(changed);

        // FR-047: the room ending is what ends a meeting, never the starter leaving.
        Assert.False(meeting.IsActive);
        Assert.Equal(_clock.UtcNow, meeting.EndedAt);

        // The MeetingEnded event carries the duration an auditor reads (FR-051), so it has to reach
        // the outbox before the entity's events are cleared.
        Assert.Contains(_events.Published, published => published is MeetingEnded);
    }

    [Fact]
    public async Task An_open_share_is_stopped_before_the_totals_are_summed()
    {
        Meeting meeting = StartMeeting();

        ShareSession share = ShareSession.Start(meeting.Id, _starter, ShareScope.Screen, _clock);
        _shares.Active = share;

        _clock.Advance(TimeSpan.FromMinutes(10));

        await Handle(new ApplyMediaSignal("room_finished", meeting.Id, null, 0));

        // Ordering is the assertion. A session with no stop time has no duration, so summing first
        // would drop the final share from the FR-051 figure entirely — silently, and only for the
        // person who was presenting when the call ended.
        Assert.NotNull(share.StoppedAt);
        Assert.Equal(ShareStopReason.MeetingEnded, share.StopReason);
        Assert.True(_shares.StoppedBeforeSummed);
    }

    [Fact]
    public async Task The_shared_screen_totals_are_recorded_on_the_meeting()
    {
        Meeting meeting = StartMeeting();
        meeting.Join(_joiner, _clock);

        _shares.Totals = new Dictionary<Guid, int> { [_starter] = 300, [_joiner] = 120 };

        await Handle(new ApplyMediaSignal("room_finished", meeting.Id, null, 0));

        Participation starter = meeting.Participants.Single(p => p.EmployeeId == _starter);

        // Summed from the share sessions, which are the authoritative record, rather than
        // accumulated per event — at-least-once delivery makes an accumulated figure depend on how
        // many times each event happened to be redelivered.
        Assert.Equal(300, starter.SharedScreenSeconds);
    }

    [Fact]
    public async Task The_platform_count_is_not_replaced_by_a_rooms_count_when_that_room_finishes()
    {
        Meeting meeting = StartMeeting();

        _capacity.ReconciledTo = 42;

        await Handle(new ApplyMediaSignal("room_finished", meeting.Id, null, 0));

        // room_finished reports zero *for the room*, which is correct for it and wrong for the
        // platform. Reconciling from it would zero the platform-wide counter every time any meeting
        // ended, making a full platform look empty.
        Assert.Equal(42, _capacity.ReconciledTo);
    }

    [Fact]
    public async Task A_negative_participant_count_is_clamped_rather_than_trusted()
    {
        Meeting meeting = StartMeeting();

        await Handle(new ApplyMediaSignal("participant_joined", meeting.Id, _joiner, -5));

        // A negative count would hide a genuinely full platform behind a false surplus.
        Assert.Equal(0, _capacity.ReconciledTo);
    }

    [Theory]
    [InlineData("room_started")]
    [InlineData("track_published")]
    [InlineData("egress_ended")]
    public async Task Events_nothing_depends_on_are_accepted_and_ignored(string eventType)
    {
        Meeting meeting = StartMeeting();

        // Accepted so LiveKit does not retry them; ignored because nothing here reacts to them. The
        // count is still reconciled, since the figure is good whatever the event was.
        bool changed = await Handle(new ApplyMediaSignal(eventType, meeting.Id, _joiner, 4));

        Assert.False(changed);
        Assert.True(meeting.IsActive);
        Assert.Equal(4, _capacity.ReconciledTo);
    }

    [Fact]
    public async Task A_join_with_no_participant_identity_is_ignored()
    {
        Meeting meeting = StartMeeting();

        // Room-level events carry no identity. Falling through to the default arm is correct —
        // there is nobody to add.
        bool changed = await Handle(new ApplyMediaSignal("participant_joined", meeting.Id, null, 1));

        Assert.False(changed);
        Assert.Equal(1, meeting.ActiveParticipantCount);
    }

    [Fact]
    public async Task The_entitys_events_are_cleared_once_they_reach_the_outbox()
    {
        Meeting meeting = StartMeeting();

        await Handle(new ApplyMediaSignal("room_finished", meeting.Id, null, 0));

        // Without the clear, the next save would re-publish the same events (Principle VI).
        Assert.Empty(meeting.DomainEvents);
        Assert.NotEmpty(_events.Published);
    }

    [Fact]
    public void The_handler_refuses_to_be_built_without_its_collaborators()
    {
        Assert.Throws<ArgumentNullException>(
            () => new ApplyMediaSignalHandler(null!, _capacity, _shares, _events, _clock));
        Assert.Throws<ArgumentNullException>(
            () => new ApplyMediaSignalHandler(_meetings, null!, _shares, _events, _clock));
        Assert.Throws<ArgumentNullException>(
            () => new ApplyMediaSignalHandler(_meetings, _capacity, null!, _events, _clock));
        Assert.Throws<ArgumentNullException>(
            () => new ApplyMediaSignalHandler(_meetings, _capacity, _shares, null!, _clock));
        Assert.Throws<ArgumentNullException>(
            () => new ApplyMediaSignalHandler(_meetings, _capacity, _shares, _events, null!));
    }

    [Fact]
    public async Task The_handler_refuses_a_null_request()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await Handle(null!));
    }

    /// <summary>
    /// A meeting with its starter already in the room.
    /// </summary>
    /// <remarks>
    /// <see cref="Meeting.Start"/> deliberately does not add the starter — starting a room and being
    /// in it are separate events, and the starter joins by presenting a token like anyone else,
    /// which arrives here as its own <c>participant_joined</c> webhook. The join is done explicitly
    /// so these tests begin from the state a live meeting is actually in, rather than from one that
    /// exists for a few hundred milliseconds.
    /// </remarks>
    private Meeting StartMeeting()
    {
        Meeting meeting = Meeting.Start(Guid.CreateVersion7(), _conversationId, _starter, _clock);
        meeting.Join(_starter, _clock);
        meeting.ClearDomainEvents();

        _meetings.Meeting = meeting;

        return meeting;
    }

    private async Task<bool> Handle(ApplyMediaSignal request) =>
        await new ApplyMediaSignalHandler(_meetings, _capacity, _shares, _events, _clock)
            .HandleAsync(request);

    private sealed class FakeMeetingRepository : IMeetingRepository
    {
        public Meeting? Meeting { get; set; }

        public Task AddAsync(Meeting meeting, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<Meeting?> FindActiveForConversationAsync(
            Guid conversationId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Meeting?.IsActive == true ? Meeting : null);

        // Matched on id, so the "meeting this system has never heard of" case is genuinely a miss
        // rather than the fake handing back whatever it happens to hold.
        public Task<Meeting?> FindAsync(Guid meetingId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Meeting?.Id == meetingId ? Meeting : null);
    }

    private sealed class FakeCapacityGuard : IMeetingCapacityGuard
    {
        public int ReconciledTo { get; set; }

        public Task<bool> HasCapacityAsync(
            int additionalParticipants = 1,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<int> GetCountAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(ReconciledTo);

        public Task IncrementAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task DecrementAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ReconcileAsync(int actualCount, CancellationToken cancellationToken = default)
        {
            ReconciledTo = actualCount;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeShareStore : IShareSessionStore
    {
        public ShareSession? Active { get; set; }

        public ShareSession? Updated { get; private set; }

        public Dictionary<Guid, int> Totals { get; set; } = [];

        /// <summary>
        /// Whether the open share had been stopped by the time the totals were read.
        /// </summary>
        /// <remarks>
        /// The ordering FR-051 depends on. Recorded here rather than inferred from call order,
        /// because the sum is what the meeting's figures come from and a session still open when it
        /// runs contributes nothing.
        /// </remarks>
        public bool StoppedBeforeSummed { get; private set; }

        public Task<ShareSession?> FindActiveAsync(
            Guid meetingId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Active?.StoppedAt is null ? Active : null);

        public Task AddAsync(ShareSession session, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task UpdateAsync(ShareSession session, CancellationToken cancellationToken = default)
        {
            Updated = session;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyDictionary<Guid, int>> SumSharedSecondsAsync(
            Guid meetingId,
            CancellationToken cancellationToken = default)
        {
            StoppedBeforeSummed = Active is null || Active.StoppedAt is not null;

            return Task.FromResult<IReadOnlyDictionary<Guid, int>>(Totals);
        }
    }

    private sealed class FakeEventPublisher : IEventPublisher
    {
        public List<IDomainEvent> Published { get; } = [];

        public Task PublishAsync(IDomainEvent domainEvent, CancellationToken cancellationToken = default)
        {
            Published.Add(domainEvent);
            return Task.CompletedTask;
        }

        public Task PublishAsync(
            IReadOnlyCollection<IDomainEvent> domainEvents,
            CancellationToken cancellationToken = default)
        {
            Published.AddRange(domainEvents);
            return Task.CompletedTask;
        }
    }
}
