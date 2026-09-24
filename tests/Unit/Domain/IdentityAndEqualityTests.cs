using InternalChat.Domain.Common;

namespace InternalChat.UnitTests.Domain;

/// <summary>
/// The equality contracts <see cref="Entity{TId}"/> and <see cref="ValueObject"/> impose on every
/// type in the domain.
/// </summary>
/// <remarks>
/// <para>
/// These two base classes decide, for every entity and value object in the system, what "the same"
/// means. Almost nothing calls them directly — they are reached through <c>MessageBody</c>,
/// <c>Membership</c>, <c>Conversation</c> and the rest — which is exactly why they are worth
/// asserting here rather than only through their subclasses. A defect in <c>GetHashCode</c> does
/// not surface as a failing assertion anywhere; it surfaces as a dictionary lookup that misses, a
/// <c>Distinct()</c> that keeps duplicates, or a change-tracker that treats one row as two.
/// </para>
/// <para>
/// <b>The type check in both <c>Equals</c> implementations is the assertion that matters.</b> Ids
/// here are UUIDs generated independently per table, so two different kinds of entity can hold the
/// same <see cref="Guid"/> — a meeting's id is deliberately derived from the conversation it
/// belongs to. Without the type check those two would compare equal, and a collection holding both
/// would silently drop one.
/// </para>
/// </remarks>
public sealed class IdentityAndEqualityTests : UnitTestBase
{
    [Fact]
    public void Two_entities_of_the_same_type_are_equal_when_their_ids_are()
    {
        Guid id = Guid.CreateVersion7();

        TestEntity first = new(id);
        TestEntity second = new(id);

        // Attributes differ; identity does not. That is the whole point of an entity.
        second.Rename("a different name");

        Assert.Equal(first, second);
        Assert.True(first == second);
        Assert.False(first != second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
    }

    [Fact]
    public void Entities_of_different_types_are_never_equal_even_with_the_same_id()
    {
        // Not hypothetical: a meeting's id is the conversation-derived room name, so a meeting and
        // a conversation genuinely share a Guid.
        Guid shared = Guid.CreateVersion7();

        TestEntity entity = new(shared);
        OtherTestEntity other = new(shared);

        Assert.False(entity.Equals(other));
        Assert.False(other.Equals(entity));

        // And they hash apart, so a set holding both keeps both.
        Assert.Equal(2, new HashSet<object> { entity, other }.Count);
    }

    [Fact]
    public void An_entity_is_never_equal_to_null_or_to_an_unrelated_object()
    {
        TestEntity entity = new(Guid.CreateVersion7());

        Assert.False(entity.Equals(null));
        Assert.False(entity.Equals("not an entity"));
        Assert.False(entity == null);
        Assert.True(entity != null);

        // Both null is equal. The operator has to special-case it, because the null-conditional in
        // `left?.Equals(right)` yields null rather than false when left is null.
        TestEntity? left = null;
        TestEntity? right = null;

        Assert.True(left == right);
        Assert.False(left != right);
    }

    [Fact]
    public void An_entity_cannot_be_constructed_without_an_id()
    {
        // Reachable only through a reference-typed id, which is what the guard is for. An entity
        // with no identity is an entity nothing can find again.
        Assert.Throws<ArgumentNullException>(() => new StringKeyedEntity(null!));
    }

    [Fact]
    public void Domain_events_accumulate_until_they_are_cleared()
    {
        TestEntity entity = new(Guid.CreateVersion7());

        Assert.Empty(entity.DomainEvents);

        entity.Rename("first");
        entity.Rename("second");

        Assert.Equal(2, entity.DomainEvents.Count);

        // The persistence layer clears them once they are in the outbox. Without the clear, every
        // subsequent save would re-publish the same events (Principle VI).
        entity.ClearDomainEvents();

        Assert.Empty(entity.DomainEvents);
    }

    [Fact]
    public void Raising_a_null_event_is_refused()
    {
        TestEntity entity = new(Guid.CreateVersion7());

        // A null in the outbox is a null reference at dispatch time, far from here.
        Assert.Throws<ArgumentNullException>(entity.RaiseNothing);
    }

    [Fact]
    public void Value_objects_are_equal_when_every_component_is()
    {
        TestValue first = new("alpha", 1);
        TestValue second = new("alpha", 1);

        Assert.Equal(first, second);
        Assert.True(first == second);
        Assert.False(first != second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());

        Assert.NotEqual(first, new TestValue("alpha", 2));
        Assert.NotEqual(first, new TestValue("beta", 1));
    }

    [Fact]
    public void Value_objects_of_different_types_with_identical_components_are_not_equal()
    {
        TestValue value = new("alpha", 1);
        OtherTestValue other = new("alpha", 1);

        // Without the type check, two different value objects holding the same string would
        // compare equal, which would make any dictionary keyed by either of them wrong.
        Assert.False(value.Equals(other));
        Assert.NotEqual(value.GetHashCode(), other.GetHashCode());
    }

    [Fact]
    public void A_value_object_handles_null_components_and_null_comparands()
    {
        TestValue withNull = new(null, 1);

        Assert.Equal(withNull, new TestValue(null, 1));
        Assert.NotEqual(withNull, new TestValue("alpha", 1));

        // A null component must not throw from GetHashCode. HashCode.Add handles it — this asserts
        // we actually rely on that, rather than on components never being null.
        _ = withNull.GetHashCode();

        Assert.False(withNull.Equals(null));
        Assert.False(withNull.Equals((object?)null));

        TestValue? left = null;

        Assert.False(left == withNull);
        Assert.True(left != withNull);
    }

    [Fact]
    public void A_value_object_equals_itself()
    {
        TestValue value = new("alpha", 1);

        // The ReferenceEquals shortcut, which is what keeps equality cheap for the self-comparison
        // that SequenceEqual-heavy code does constantly.
#pragma warning disable CS1718 // Comparison made to same variable — that is the point.
        Assert.True(value == value);
#pragma warning restore CS1718
        Assert.True(value.Equals(value));
    }

    private sealed class TestEntity : Entity<Guid>
    {
        public TestEntity(Guid id)
            : base(id)
        {
        }

        public string Name { get; private set; } = "initial";

        public void Rename(string name)
        {
            Name = name;
            Raise(new TestEvent(Id));
        }

        public void RaiseNothing() => Raise(null!);
    }

    private sealed class OtherTestEntity : Entity<Guid>
    {
        public OtherTestEntity(Guid id)
            : base(id)
        {
        }
    }

    private sealed class StringKeyedEntity : Entity<string>
    {
        public StringKeyedEntity(string id)
            : base(id)
        {
        }
    }

    private sealed record TestEvent(Guid EntityId) : IDomainEvent
    {
        public Guid EventId { get; } = Guid.CreateVersion7();

        public DateTimeOffset OccurredAt { get; } = DateTimeOffset.UnixEpoch;

        public string EventType => "test.entity.renamed.v1";
    }

    private sealed class TestValue : ValueObject
    {
        public TestValue(string? text, int number)
        {
            Text = text;
            Number = number;
        }

        public string? Text { get; }

        public int Number { get; }

        protected override IEnumerable<object?> GetEqualityComponents()
        {
            yield return Text;
            yield return Number;
        }
    }

    private sealed class OtherTestValue : ValueObject
    {
        public OtherTestValue(string? text, int number)
        {
            Text = text;
            Number = number;
        }

        public string? Text { get; }

        public int Number { get; }

        protected override IEnumerable<object?> GetEqualityComponents()
        {
            yield return Text;
            yield return Number;
        }
    }
}
