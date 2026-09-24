using InternalChat.Domain.Notifications;

namespace InternalChat.UnitTests.Domain;

/// <summary>
/// T124 — read state merges monotonically and never moves backwards (FR-036).
/// </summary>
/// <remarks>
/// FR-036 requires unread state to stay consistent across every device an employee owns. Two
/// devices report reads independently and can arrive out of order — a phone's stale "read up to 40"
/// landing after a laptop already reported "read up to 90" — and <c>AdvanceTo</c> taking the greater
/// of the two is what stops the phone's report from silently undoing the laptop's.
/// </remarks>
public sealed class ReadStateTests : UnitTestBase
{
    [Fact]
    public void Starting_sets_the_initial_position()
    {
        Guid employeeId = Guid.CreateVersion7();
        Guid conversationId = Guid.CreateVersion7();

        ReadState state = ReadState.Start(employeeId, conversationId, lastReadSeq: 10, new TestClock());

        Assert.Equal(employeeId, state.EmployeeId);
        Assert.Equal(conversationId, state.ConversationId);
        Assert.Equal(10, state.LastReadSeq);
    }

    [Fact]
    public void Starting_with_a_negative_sequence_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ReadState.Start(Guid.CreateVersion7(), Guid.CreateVersion7(), -1, new TestClock()));
    }

    [Fact]
    public void Advancing_to_a_higher_sequence_moves_the_position_and_reports_a_change()
    {
        TestClock clock = new();
        ReadState state = ReadState.Start(Guid.CreateVersion7(), Guid.CreateVersion7(), 10, clock);

        bool advanced = state.AdvanceTo(50, clock);

        Assert.True(advanced);
        Assert.Equal(50, state.LastReadSeq);
    }

    /// <summary>The rule the whole type exists to enforce.</summary>
    [Fact]
    public void Advancing_to_a_lower_sequence_is_ignored_and_reports_no_change()
    {
        TestClock clock = new();
        ReadState state = ReadState.Start(Guid.CreateVersion7(), Guid.CreateVersion7(), 90, clock);

        bool advanced = state.AdvanceTo(40, clock);

        Assert.False(advanced);
        Assert.Equal(90, state.LastReadSeq);
    }

    [Fact]
    public void Advancing_to_the_same_sequence_is_ignored_and_reports_no_change()
    {
        TestClock clock = new();
        ReadState state = ReadState.Start(Guid.CreateVersion7(), Guid.CreateVersion7(), 90, clock);

        bool advanced = state.AdvanceTo(90, clock);

        Assert.False(advanced);
        Assert.Equal(90, state.LastReadSeq);
    }

    /// <summary>
    /// Two devices reporting out of order still end up at the higher of the two — the property
    /// FR-036 actually depends on.
    /// </summary>
    [Fact]
    public void Two_devices_reporting_out_of_order_settle_on_the_higher_position()
    {
        TestClock clock = new();
        ReadState state = ReadState.Start(Guid.CreateVersion7(), Guid.CreateVersion7(), 0, clock);

        // Laptop reads up to 90 first.
        Assert.True(state.AdvanceTo(90, clock));

        // Phone's report of 40 — generated before the laptop's, delivered after — must not undo it.
        Assert.False(state.AdvanceTo(40, clock));

        Assert.Equal(90, state.LastReadSeq);
    }

    [Fact]
    public void Advancing_updates_the_timestamp_only_when_the_position_actually_moves()
    {
        TestClock clock = new();
        ReadState state = ReadState.Start(Guid.CreateVersion7(), Guid.CreateVersion7(), 10, clock);
        DateTimeOffset afterStart = state.UpdatedAt;

        clock.Advance(TimeSpan.FromMinutes(5));
        state.AdvanceTo(5, clock); // ignored — lower

        Assert.Equal(afterStart, state.UpdatedAt);

        clock.Advance(TimeSpan.FromMinutes(5));
        state.AdvanceTo(20, clock); // applied

        Assert.Equal(clock.UtcNow, state.UpdatedAt);
    }

    [Fact]
    public void Advancing_to_a_negative_sequence_is_refused()
    {
        ReadState state = ReadState.Start(Guid.CreateVersion7(), Guid.CreateVersion7(), 0, new TestClock());

        Assert.Throws<ArgumentOutOfRangeException>(() => state.AdvanceTo(-1, new TestClock()));
    }

    [Fact]
    public void The_event_type_matches_the_documented_contract()
    {
        Guid employeeId = Guid.CreateVersion7();
        Guid conversationId = Guid.CreateVersion7();

        ReadStateUpdated updated = new(Guid.CreateVersion7(), DateTimeOffset.UtcNow, employeeId, conversationId, 42);

        Assert.Equal("chat.read_state.updated.v1", updated.EventType);
        Assert.Equal(employeeId, updated.EmployeeId);
        Assert.Equal(conversationId, updated.ConversationId);
        Assert.Equal(42, updated.LastReadSeq);
    }
}
