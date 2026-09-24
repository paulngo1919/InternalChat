using InternalChat.Domain.Conversations;

namespace InternalChat.UnitTests.Domain;

/// <summary>
/// T109 — membership invariants: how access starts, ends, and is restored (US3).
/// </summary>
/// <remarks>
/// <c>Membership</c> itself was built at T059; these tests pin the behaviour formally, the same way
/// T052 pinned <c>Employee</c> invariants that already existed on the entity. The load-bearing one is
/// the re-add floor: data-model.md requires it never move down, and <see cref="Membership.Rejoin"/>
/// is exactly the operation US3's "add a fourth member" scenario exercises when the fourth member is
/// someone who left and came back.
/// </remarks>
public sealed class MembershipTests : UnitTestBase
{
    [Fact]
    public void Joining_grants_an_active_membership_at_the_given_role_and_floor()
    {
        Guid conversationId = Guid.CreateVersion7();
        Guid employeeId = Guid.CreateVersion7();

        Membership membership = Membership.Join(
            conversationId, employeeId, MembershipRole.Admin, visibleFromSeq: 42, new TestClock());

        Assert.Equal(conversationId, membership.ConversationId);
        Assert.Equal(employeeId, membership.EmployeeId);
        Assert.Equal(MembershipRole.Admin, membership.Role);
        Assert.Equal(42, membership.VisibleFromSeq);
        Assert.True(membership.IsActive);
        Assert.Null(membership.RemovedAt);
    }

    [Fact]
    public void Joining_with_a_negative_floor_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Membership.Join(
            Guid.CreateVersion7(), Guid.CreateVersion7(), MembershipRole.Member, visibleFromSeq: -1, new TestClock()));
    }

    [Fact]
    public void Removing_sets_removed_at_and_clears_active_status()
    {
        Membership membership = Membership.Join(
            Guid.CreateVersion7(), Guid.CreateVersion7(), MembershipRole.Member, 0, new TestClock());

        membership.Remove(new TestClock());

        Assert.False(membership.IsActive);
        Assert.NotNull(membership.RemovedAt);
    }

    /// <summary>
    /// A repeated removal never moves <c>RemovedAt</c>.
    /// </summary>
    /// <remarks>
    /// Same idempotency reasoning as <see cref="Employees.EmployeeTests"/>'s deactivation test:
    /// <c>RemovedAt</c> is what an auditor reads to establish when access ended (SC-021), and a
    /// redelivered removal must not rewrite that answer.
    /// </remarks>
    [Fact]
    public void A_second_removal_does_not_move_the_removed_at_timestamp()
    {
        TestClock clock = new();
        Membership membership = Membership.Join(Guid.CreateVersion7(), Guid.CreateVersion7(), MembershipRole.Member, 0, clock);

        membership.Remove(clock);
        DateTimeOffset firstRemoval = membership.RemovedAt!.Value;

        clock.Advance(TimeSpan.FromHours(1));
        membership.Remove(clock);

        Assert.Equal(firstRemoval, membership.RemovedAt);
    }

    [Fact]
    public void Rejoining_clears_removed_at_and_reactivates_the_membership()
    {
        Membership membership = Membership.Join(
            Guid.CreateVersion7(), Guid.CreateVersion7(), MembershipRole.Member, 0, new TestClock());
        membership.Remove(new TestClock());

        membership.Rejoin(visibleFromSeq: 100, new TestClock());

        Assert.True(membership.IsActive);
        Assert.Null(membership.RemovedAt);
    }

    /// <summary>
    /// The load-bearing rule: a re-add never lowers the history floor.
    /// </summary>
    /// <remarks>
    /// Someone removed at seq 900 and re-added into a conversation whose current floor computes to
    /// 500 (for instance, a from-join group that has not posted since) must not regain 500-900 — they
    /// were specifically excluded from it. <see cref="Membership.Rejoin"/> takes the higher of the
    /// two floors for exactly this reason.
    /// </remarks>
    [Fact]
    public void Rejoining_with_a_lower_floor_than_before_keeps_the_higher_one()
    {
        Membership membership = Membership.Join(
            Guid.CreateVersion7(), Guid.CreateVersion7(), MembershipRole.Member, visibleFromSeq: 900, new TestClock());
        membership.Remove(new TestClock());

        membership.Rejoin(visibleFromSeq: 500, new TestClock());

        Assert.Equal(900, membership.VisibleFromSeq);
    }

    [Fact]
    public void Rejoining_with_a_higher_floor_than_before_adopts_the_new_one()
    {
        Membership membership = Membership.Join(
            Guid.CreateVersion7(), Guid.CreateVersion7(), MembershipRole.Member, visibleFromSeq: 200, new TestClock());
        membership.Remove(new TestClock());

        membership.Rejoin(visibleFromSeq: 900, new TestClock());

        Assert.Equal(900, membership.VisibleFromSeq);
    }

    [Fact]
    public void Rejoining_updates_joined_at_to_the_current_instant()
    {
        TestClock clock = new();
        Membership membership = Membership.Join(Guid.CreateVersion7(), Guid.CreateVersion7(), MembershipRole.Member, 0, clock);
        DateTimeOffset originalJoinedAt = membership.JoinedAt;

        membership.Remove(clock);
        clock.Advance(TimeSpan.FromDays(1));
        membership.Rejoin(0, clock);

        Assert.NotEqual(originalJoinedAt, membership.JoinedAt);
        Assert.Equal(clock.UtcNow, membership.JoinedAt);
    }

    [Fact]
    public void Changing_role_replaces_it()
    {
        Membership membership = Membership.Join(
            Guid.CreateVersion7(), Guid.CreateVersion7(), MembershipRole.Member, 0, new TestClock());

        membership.ChangeRole(MembershipRole.Admin);

        Assert.Equal(MembershipRole.Admin, membership.Role);
    }

    [Fact]
    public void Muting_sets_and_clears_the_suppression_window()
    {
        Membership membership = Membership.Join(
            Guid.CreateVersion7(), Guid.CreateVersion7(), MembershipRole.Member, 0, new TestClock());

        DateTimeOffset until = DateTimeOffset.UtcNow.AddHours(2);
        membership.MuteUntil(until);
        Assert.Equal(until, membership.MutedUntil);

        // Muting is notification-only. Neither call changes whether the membership grants access.
        Assert.True(membership.IsActive);

        membership.MuteUntil(null);
        Assert.Null(membership.MutedUntil);
    }

    /// <summary>
    /// <c>Member &lt; Admin</c> so <c>ConversationMembershipEvaluator</c>'s ordinal comparison holds.
    /// </summary>
    /// <remarks>
    /// The evaluator compares roles with <c>&lt;</c> (T066) rather than a switch, so the numeric
    /// ordering of the enum members <em>is</em> the privilege ordering. A role inserted in the middle
    /// of the enum would silently change what <c>RequireConversationMembership(Admin)</c> permits.
    /// </remarks>
    [Fact]
    public void Member_is_ordered_below_admin()
    {
        Assert.True(MembershipRole.Member < MembershipRole.Admin);
    }

    [Fact]
    public void The_wire_spelling_of_each_change_kind_matches_contracts_messaging_md()
    {
        MembershipChanged added = MembershipChanged.Create(
            Guid.CreateVersion7(), Guid.CreateVersion7(), MembershipChangeKind.Added, Guid.CreateVersion7(), 0, new TestClock());
        MembershipChanged removed = MembershipChanged.Create(
            Guid.CreateVersion7(), Guid.CreateVersion7(), MembershipChangeKind.Removed, Guid.CreateVersion7(), 0, new TestClock());
        MembershipChanged roleChanged = MembershipChanged.Create(
            Guid.CreateVersion7(), Guid.CreateVersion7(), MembershipChangeKind.RoleChanged, Guid.CreateVersion7(), 0, new TestClock());

        Assert.Equal("added", added.Change);
        Assert.Equal("removed", removed.Change);
        Assert.Equal("role_changed", roleChanged.Change);
        Assert.Equal("chat.membership.changed.v1", added.EventType);
    }
}
