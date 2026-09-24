using InternalChat.Domain.Conversations;

namespace InternalChat.UnitTests.Domain;

/// <summary>
/// T079 — direct-conversation invariants: exactly two members, neither removable.
/// </summary>
/// <remarks>
/// data-model.md, <c>conversation</c>: "A `direct` conversation MUST have exactly two memberships
/// and they MUST NOT be removable." Both halves live on the entity rather than in
/// <c>AddMember</c>/<c>RemoveMember</c>, so the membership endpoints, the seeder, and anything added
/// later all pass through the same gate.
/// </remarks>
public sealed class ConversationTests : UnitTestBase
{
    [Fact]
    public void A_direct_conversation_is_created_from_exactly_two_employees()
    {
        Guid first = Guid.CreateVersion7();
        Guid second = Guid.CreateVersion7();

        Conversation conversation = Conversation.CreateDirect(Guid.CreateVersion7(), first, second, new TestClock());

        Assert.Equal(ConversationKind.Direct, conversation.Kind);
        Assert.Null(conversation.Name);
        Assert.NotNull(conversation.DirectKey);

        // `full`, not `from_join`. A pair conversation has no join point worth hiding behind —
        // both participants were there from the first message by construction.
        Assert.Equal(HistoryVisibility.Full, conversation.HistoryVisibility);
    }

    [Fact]
    public void A_direct_conversation_with_one_employee_twice_is_refused()
    {
        Guid employeeId = Guid.CreateVersion7();

        // "Exactly two members" means two *distinct* members. A self-conversation would satisfy a
        // naive count of two and is not what FR-007 describes.
        Assert.Throws<ArgumentException>(
            () => Conversation.CreateDirect(Guid.CreateVersion7(), employeeId, employeeId, new TestClock()));
    }

    [Fact]
    public void The_direct_key_is_the_same_whichever_participant_asks_first()
    {
        Guid first = Guid.CreateVersion7();
        Guid second = Guid.CreateVersion7();

        Conversation forward = Conversation.CreateDirect(Guid.CreateVersion7(), first, second, new TestClock());
        Conversation reverse = Conversation.CreateDirect(Guid.CreateVersion7(), second, first, new TestClock());

        // This is the whole mechanism behind the UNIQUE index on direct_key. If the key depended on
        // argument order, two people clicking "message" on each other simultaneously would produce
        // two conversations and each would see half the exchange.
        Assert.Equal(forward.DirectKey, reverse.DirectKey);
    }

    [Fact]
    public void The_direct_key_matches_what_the_seeder_builds()
    {
        // The seeder writes direct_key itself (tools/Seeder/SeedIdentity.DirectKey). If the two
        // spellings drifted, the seeded conversations would be invisible to CreateConversation's
        // deduplication and it would try to insert a duplicate the database then refuses.
        Guid low = Guid.Parse("00000000-0000-4000-8000-000000000001");
        Guid high = Guid.Parse("00000000-0000-4000-8000-000000000002");

        Conversation conversation = Conversation.CreateDirect(Guid.CreateVersion7(), high, low, new TestClock());

        Assert.Equal($"{low}:{high}", conversation.DirectKey);
    }

    [Fact]
    public void A_member_cannot_be_added_to_a_direct_conversation()
    {
        Conversation conversation = Conversation.CreateDirect(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), new TestClock());

        // openapi.yaml documents 422 for this. Adding a third participant would turn a private
        // exchange into a group without either participant agreeing to it.
        Assert.Throws<DirectConversationException>(conversation.EnsureMembersMayChange);
    }

    [Fact]
    public void A_participant_cannot_be_removed_from_a_direct_conversation()
    {
        Conversation conversation = Conversation.CreateDirect(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), new TestClock());

        // Same gate as adding, deliberately: "exactly two, neither removable" is one invariant, and
        // splitting it across two methods invites one of them to be forgotten.
        Assert.Throws<DirectConversationException>(conversation.EnsureMembersMayChange);
    }

    [Fact]
    public void A_group_conversation_requires_a_name()
    {
        // data-model.md: `name` is NOT NULL when kind = 'group', with a CHECK constraint. The
        // constraint is the backstop; this is the error message a person can act on.
        Assert.Throws<ArgumentException>(() => Conversation.CreateGroup(
            Guid.CreateVersion7(), "   ", Guid.CreateVersion7(), HistoryVisibility.FromJoin, new TestClock()));
    }

    [Fact]
    public void A_group_conversation_has_no_direct_key()
    {
        Conversation conversation = Conversation.CreateGroup(
            Guid.CreateVersion7(), "Engineering", Guid.CreateVersion7(), HistoryVisibility.FromJoin, new TestClock());

        // NULL rather than a sentinel. The unique index is partial (`WHERE kind = 'direct'`), and a
        // sentinel value would collide across every group in the platform.
        Assert.Null(conversation.DirectKey);
        Assert.Equal(ConversationKind.Group, conversation.Kind);
    }

    [Fact]
    public void Members_may_change_in_a_group()
    {
        Conversation conversation = Conversation.CreateGroup(
            Guid.CreateVersion7(), "Product Launch", Guid.CreateVersion7(), HistoryVisibility.FromJoin, new TestClock());

        // The control. Without it, an implementation that refused every membership change would
        // pass both direct-conversation tests above.
        conversation.EnsureMembersMayChange();
    }

    [Fact]
    public void The_history_floor_for_a_new_member_depends_on_the_visibility_rule()
    {
        Conversation fromJoin = Conversation.CreateGroup(
            Guid.CreateVersion7(), "From join", Guid.CreateVersion7(), HistoryVisibility.FromJoin, new TestClock());

        Conversation full = Conversation.CreateGroup(
            Guid.CreateVersion7(), "Full", Guid.CreateVersion7(), HistoryVisibility.Full, new TestClock());

        for (int i = 0; i < 12; i++)
        {
            fromJoin.AllocateSequence();
            full.AllocateSequence();
        }

        // Computed by the conversation because only it knows its own rule and its own sequence
        // (Membership.Join's parameter documentation says exactly this). A caller deriving it would
        // eventually derive it differently in one of the several places members are added.
        Assert.Equal(12, fromJoin.HistoryFloorForNewMember());
        Assert.Equal(0, full.HistoryFloorForNewMember());
    }

    [Fact]
    public void The_visibility_rule_is_fixed_at_creation()
    {
        Conversation conversation = Conversation.CreateGroup(
            Guid.CreateVersion7(), "Engineering", Guid.CreateVersion7(), HistoryVisibility.FromJoin, new TestClock());

        // US3 scenario 4 requires the rule to be displayed to members. A rule that could change
        // later would retroactively grant or withdraw history from people who already joined, and
        // there is no mutator for it — this test is what keeps one from being added casually.
        Assert.Null(
            typeof(Conversation).GetProperty(nameof(Conversation.HistoryVisibility))!.SetMethod);

        Assert.Equal(HistoryVisibility.FromJoin, conversation.HistoryVisibility);
    }

    [Fact]
    public void Renaming_a_group_records_when_it_changed()
    {
        TestClock clock = new("2026-08-01T09:00:00Z");
        Conversation conversation = Conversation.CreateGroup(
            Guid.CreateVersion7(), "Old name", Guid.CreateVersion7(), HistoryVisibility.FromJoin, clock);

        clock.Advance(TimeSpan.FromHours(1));
        conversation.Rename("New name", clock);

        Assert.Equal("New name", conversation.Name);
        Assert.Equal(clock.UtcNow, conversation.UpdatedAt);
    }

    [Fact]
    public void A_direct_conversation_cannot_be_renamed()
    {
        Conversation conversation = Conversation.CreateDirect(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), new TestClock());

        // A direct conversation is named by who is in it. Allowing a name would make the two
        // participants disagree about what the conversation is called.
        Assert.Throws<DirectConversationException>(() => conversation.Rename("Nickname", new TestClock()));
    }
}
