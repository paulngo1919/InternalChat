using InternalChat.Domain.Common;
using InternalChat.Domain.Meetings;

namespace InternalChat.UnitTests.Domain;

/// <summary>
/// T199 — the single-publisher screen-share rule (FR-048, FR-050, FR-051).
/// </summary>
/// <remarks>
/// <para>
/// <b>FR-050 does not say which rule to apply — it says apply "one defined, visible rule".</b> That
/// makes these tests unusual: they are pinning down a choice rather than deriving one, and the
/// choice is <em>last writer wins, with the displaced sharer told why</em>. The tests below exist
/// so the choice cannot drift silently, because a screen-share rule that changes between releases
/// is exactly the unpredictability the requirement is written against.
/// </para>
/// <para>
/// <b>Why takeover rather than refusal.</b> Refusing the second sharer reads as more polite and
/// behaves worse in the case that actually happens: someone is presenting, the meeting moves on,
/// and the next person cannot take over until the first notices and stops. That produces a meeting
/// where people ask each other to stop sharing — which is the failure FR-050 exists to prevent.
/// </para>
/// <para>
/// <b>The stop <em>reason</em> is what makes the rule "visible".</b> Without it a client can only
/// say "sharing stopped", and the person cannot tell whether they stopped it, someone took over,
/// or the meeting ended.
/// </para>
/// </remarks>
public sealed class ShareSessionTests : UnitTestBase
{
    private static readonly Guid MeetingId = Guid.CreateVersion7();
    private static readonly Guid Presenter = Guid.CreateVersion7();
    private static readonly Guid Second = Guid.CreateVersion7();

    [Theory]
    [InlineData(ShareScope.Screen)]
    [InlineData(ShareScope.Window)]
    public void A_share_starts_active_with_the_scope_the_sharer_chose(ShareScope scope)
    {
        TestClock clock = new();

        ShareSession session = ShareSession.Start(MeetingId, Presenter, scope, clock);

        Assert.True(session.IsActive);
        Assert.Equal(scope, session.Scope);
        Assert.Equal(Presenter, session.EmployeeId);
        Assert.Null(session.StopReason);
    }

    [Fact]
    public void An_unknown_scope_is_refused_rather_than_defaulted()
    {
        TestClock clock = new();

        // Enum values are not closed in .NET and this one arrives from a client. Defaulting an
        // unrecognised scope to Screen would record a whole-screen share as though the person had
        // chosen it — which is precisely the claim FR-048 makes about the Window case.
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ShareSession.Start(MeetingId, Presenter, (ShareScope)99, clock));
    }

    [Fact]
    public void Stopping_records_when_and_why()
    {
        TestClock clock = new();
        ShareSession session = ShareSession.Start(MeetingId, Presenter, ShareScope.Window, clock);

        clock.Advance(TimeSpan.FromMinutes(3));
        session.Stop(ShareStopReason.Stopped, clock);

        Assert.False(session.IsActive);
        Assert.Equal(clock.UtcNow, session.StoppedAt);
        Assert.Equal(ShareStopReason.Stopped, session.StopReason);
    }

    [Fact]
    public void A_superseded_share_records_that_it_was_taken_over()
    {
        TestClock clock = new();
        ShareSession first = ShareSession.Start(MeetingId, Presenter, ShareScope.Screen, clock);

        clock.Advance(TimeSpan.FromMinutes(1));

        // What the application does when a second person starts sharing (T201).
        first.Stop(ShareStopReason.Superseded, clock);
        ShareSession second = ShareSession.Start(MeetingId, Second, ShareScope.Window, clock);

        Assert.False(first.IsActive);
        Assert.True(second.IsActive);

        // THE assertion for FR-050's "visible". "Sharing stopped" alone cannot tell the displaced
        // presenter whether they stopped it or somebody took over, and those call for different
        // reactions from them.
        Assert.Equal(ShareStopReason.Superseded, first.StopReason);
    }

    [Fact]
    public void Stopping_is_idempotent_and_neither_the_time_nor_the_reason_moves()
    {
        TestClock clock = new();
        ShareSession session = ShareSession.Start(MeetingId, Presenter, ShareScope.Screen, clock);

        clock.Advance(TimeSpan.FromMinutes(2));
        session.Stop(ShareStopReason.Superseded, clock);

        DateTimeOffset stoppedAt = session.StoppedAt!.Value;

        clock.Advance(TimeSpan.FromMinutes(5));
        session.Stop(ShareStopReason.MeetingEnded, clock);

        // The first reason is the true one. A later event — the meeting ending — must not rewrite
        // why this share actually stopped, and the duration feeds FR-051's audit total.
        Assert.Equal(stoppedAt, session.StoppedAt);
        Assert.Equal(ShareStopReason.Superseded, session.StopReason);
    }

    [Fact]
    public void Duration_is_zero_while_active_and_measured_once_stopped()
    {
        TestClock clock = new();
        ShareSession session = ShareSession.Start(MeetingId, Presenter, ShareScope.Screen, clock);

        // Zero rather than "time so far". A running share has no duration yet, and reporting a
        // growing number would make the FR-051 total depend on when it happened to be read.
        Assert.Equal(0, session.DurationSeconds);

        clock.Advance(TimeSpan.FromSeconds(95));
        session.Stop(ShareStopReason.Stopped, clock);

        Assert.Equal(95, session.DurationSeconds);
    }

    [Theory]
    [InlineData(ShareStopReason.Stopped)]
    [InlineData(ShareStopReason.Superseded)]
    [InlineData(ShareStopReason.ParticipantLeft)]
    [InlineData(ShareStopReason.MeetingEnded)]
    public void Every_stop_reason_produces_a_measurable_duration(ShareStopReason reason)
    {
        TestClock clock = new();
        ShareSession session = ShareSession.Start(MeetingId, Presenter, ShareScope.Window, clock);

        clock.Advance(TimeSpan.FromSeconds(30));
        session.Stop(reason, clock);

        // FR-051 counts shared-screen seconds regardless of how sharing ended. A path that left
        // the duration at zero would under-report the total for whichever reason it was.
        Assert.Equal(30, session.DurationSeconds);
        Assert.Equal(reason, session.StopReason);
    }

    [Fact]
    public void A_participant_can_share_again_after_being_superseded()
    {
        TestClock clock = new();

        ShareSession first = ShareSession.Start(MeetingId, Presenter, ShareScope.Screen, clock);
        first.Stop(ShareStopReason.Superseded, clock);

        clock.Advance(TimeSpan.FromMinutes(1));

        // Being taken over is not a penalty. A new session rather than reviving the old one, so
        // each span is measured separately and the audit total is their sum.
        ShareSession again = ShareSession.Start(MeetingId, Presenter, ShareScope.Window, clock);

        Assert.True(again.IsActive);
        Assert.NotEqual(first.StartedAt, again.StartedAt);
    }

    [Fact]
    public void The_scope_distinction_survives_into_the_record()
    {
        TestClock clock = new();

        ShareSession window = ShareSession.Start(MeetingId, Presenter, ShareScope.Window, clock);
        window.Stop(ShareStopReason.Stopped, clock);

        // FR-048 makes a promise about the Window case specifically — sharing one window reveals no
        // other application — and an audit record that could not tell the two apart could not
        // evidence it afterwards.
        Assert.Equal(ShareScope.Window, window.Scope);
    }
}
