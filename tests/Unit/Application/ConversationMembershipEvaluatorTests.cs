using InternalChat.Application.Abstractions;
using InternalChat.Application.Authorization;
using InternalChat.Domain.Conversations;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace InternalChat.UnitTests.Application;

/// <summary>
/// T053 — the authorization evaluator: deny by default, active membership required.
/// </summary>
/// <remarks>
/// <para>
/// This is the most security-sensitive class in the platform, so the suite is written around the
/// failure modes rather than the happy path. The happy path is one test; the rest assert that
/// every other route through the method refuses.
/// </para>
/// <para>
/// The reader is substituted here deliberately, and it is not a violation of Principle III's ban on
/// mocking infrastructure — the real reader is exercised against real PostgreSQL and Redis in the
/// integration suite. What is under test here is the <em>decision</em>, which is a pure function of
/// what the reader returns, and the cases that matter most (the reader throwing, the reader
/// returning nothing) are ones a live database will not produce on demand.
/// </para>
/// </remarks>
public sealed class ConversationMembershipEvaluatorTests : UnitTestBase
{
    private static readonly Guid Conversation = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid Employee = Guid.Parse("22222222-2222-4222-8222-222222222222");

    private readonly IMembershipReader _reader = Substitute.For<IMembershipReader>();

    private ConversationMembershipEvaluator CreateEvaluator() =>
        new(_reader, NullLogger<ConversationMembershipEvaluator>.Instance);

    private static MembershipSnapshot Snapshot(MembershipRole role = MembershipRole.Member, long floor = 0) =>
        new(Conversation, Employee, role, floor);

    // -------------------------------------------------------------------------------------------
    // Allow — the single permitted path
    // -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Live_membership_is_allowed()
    {
        _reader.FindGrantingAsync(Conversation, Employee, Arg.Any<CancellationToken>())
            .Returns(Snapshot(floor: 42));

        MembershipDecision decision = await CreateEvaluator()
            .EvaluateAsync(Employee, new ConversationMembershipRequirement(Conversation));

        Assert.True(decision.IsAllowed);
        Assert.Equal(MembershipDenialReason.None, decision.Reason);

        // The membership travels with the decision so a history query does not read the same row
        // again — the authorization check has already paid for it.
        Assert.Equal(42, decision.Membership!.VisibleFromSeq);
    }

    // -------------------------------------------------------------------------------------------
    // Deny — everything else
    // -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Absent_membership_is_refused()
    {
        _reader.FindGrantingAsync(Conversation, Employee, Arg.Any<CancellationToken>())
            .Returns((MembershipSnapshot?)null);

        MembershipDecision decision = await CreateEvaluator()
            .EvaluateAsync(Employee, new ConversationMembershipRequirement(Conversation));

        Assert.False(decision.IsAllowed);
        Assert.Equal(MembershipDenialReason.NotAMember, decision.Reason);
        Assert.Null(decision.Membership);
    }

    [Fact]
    public async Task Unauthenticated_caller_is_refused_without_a_lookup()
    {
        MembershipDecision decision = await CreateEvaluator()
            .EvaluateAsync(Guid.Empty, new ConversationMembershipRequirement(Conversation));

        Assert.False(decision.IsAllowed);
        Assert.Equal(MembershipDenialReason.NotAuthenticated, decision.Reason);

        // Not merely refused — never looked up. Querying for the all-zero id is a real query that
        // an unlucky seed row could satisfy.
        await _reader.DidNotReceiveWithAnyArgs()
            .FindGrantingAsync(default, default, default);
    }

    [Fact]
    public async Task Missing_conversation_id_is_refused_without_a_lookup()
    {
        MembershipDecision decision = await CreateEvaluator()
            .EvaluateAsync(Employee, new ConversationMembershipRequirement(Guid.Empty));

        Assert.False(decision.IsAllowed);
        Assert.Equal(MembershipDenialReason.NoResource, decision.Reason);

        await _reader.DidNotReceiveWithAnyArgs()
            .FindGrantingAsync(default, default, default);
    }

    [Fact]
    public async Task Member_is_refused_an_operation_requiring_admin()
    {
        _reader.FindGrantingAsync(Conversation, Employee, Arg.Any<CancellationToken>())
            .Returns(Snapshot(MembershipRole.Member));

        MembershipDecision decision = await CreateEvaluator().EvaluateAsync(
            Employee,
            new ConversationMembershipRequirement(Conversation, MembershipRole.Admin));

        Assert.False(decision.IsAllowed);
        Assert.Equal(MembershipDenialReason.InsufficientRole, decision.Reason);
    }

    [Fact]
    public async Task Admin_satisfies_an_operation_requiring_admin()
    {
        _reader.FindGrantingAsync(Conversation, Employee, Arg.Any<CancellationToken>())
            .Returns(Snapshot(MembershipRole.Admin));

        MembershipDecision decision = await CreateEvaluator().EvaluateAsync(
            Employee,
            new ConversationMembershipRequirement(Conversation, MembershipRole.Admin));

        Assert.True(decision.IsAllowed);
    }

    [Fact]
    public async Task Admin_also_satisfies_an_operation_requiring_only_membership()
    {
        _reader.FindGrantingAsync(Conversation, Employee, Arg.Any<CancellationToken>())
            .Returns(Snapshot(MembershipRole.Admin));

        MembershipDecision decision = await CreateEvaluator()
            .EvaluateAsync(Employee, new ConversationMembershipRequirement(Conversation));

        // Roles are cumulative. An admin locked out of ordinary member operations would be an
        // absurd but entirely possible consequence of comparing roles for equality.
        Assert.True(decision.IsAllowed);
    }

    // -------------------------------------------------------------------------------------------
    // Fail closed — SC-024
    // -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Reader_failure_refuses_rather_than_allowing()
    {
        _reader.FindGrantingAsync(Conversation, Employee, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("redis and postgres both unreachable"));

        MembershipDecision decision = await CreateEvaluator()
            .EvaluateAsync(Employee, new ConversationMembershipRequirement(Conversation));

        // SC-024: an outage may cost latency, never a wrong access decision. The tempting
        // alternative — log and continue — opens every conversation at the exact moment nobody is
        // reading dashboards.
        Assert.False(decision.IsAllowed);
        Assert.Equal(MembershipDenialReason.Undetermined, decision.Reason);
    }

    [Fact]
    public async Task Cancellation_is_not_swallowed_as_a_denial()
    {
        _reader.FindGrantingAsync(Conversation, Employee, Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException());

        // A cancelled request is the caller going away, not a refusal. Converting it into a denial
        // would write an access-denied audit event every time somebody closed a browser tab, and
        // SC-020's audit trail would fill with noise that looks like attempted intrusion.
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            CreateEvaluator().EvaluateAsync(Employee, new ConversationMembershipRequirement(Conversation)));
    }

    // -------------------------------------------------------------------------------------------
    // The ordering the role comparison depends on
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void Membership_roles_are_ordered_least_to_most_privileged()
    {
        // The evaluator compares roles with <. That is only correct while the enum is ordered by
        // privilege — inserting a role in the middle would silently promote everyone below it.
        // This test is what turns that from a comment into a build failure.
        Assert.True(MembershipRole.Member < MembershipRole.Admin);
        Assert.Equal(0, (int)MembershipRole.Member);
        Assert.Equal(1, (int)MembershipRole.Admin);
    }
}
