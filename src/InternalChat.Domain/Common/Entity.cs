namespace InternalChat.Domain.Common;

/// <summary>
/// Base class for entities — objects whose identity, not their attribute values, determines
/// equality. Two <see cref="Message"/> rows with identical text are different messages.
/// </summary>
/// <typeparam name="TId">The identity type. Immutable and never reassigned after construction.</typeparam>
/// <remarks>
/// Constitution Principle I: this type lives in Domain and references nothing but the BCL.
/// </remarks>
public abstract class Entity<TId> : IEquatable<Entity<TId>>
    where TId : notnull
{
    private readonly List<IDomainEvent> _domainEvents = [];

    protected Entity(TId id)
    {
        ArgumentNullException.ThrowIfNull(id);
        Id = id;
    }

    /// <summary>Stable identity. Assigned once at construction and never changed.</summary>
    public TId Id { get; }

    /// <summary>
    /// Events raised by this entity that have not yet been dispatched.
    /// </summary>
    /// <remarks>
    /// Collected here and written to the transactional outbox by the persistence layer inside
    /// the same transaction as the state change (Constitution Principle VI). Raising an event
    /// is therefore not a publish — it is a request to publish atomically, which is what stops
    /// a crash between commit and publish from silently losing a notification.
    /// </remarks>
    public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    protected void Raise(IDomainEvent domainEvent)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        _domainEvents.Add(domainEvent);
    }

    /// <summary>Called by the persistence layer once events have been written to the outbox.</summary>
    public void ClearDomainEvents() => _domainEvents.Clear();

    public bool Equals(Entity<TId>? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        // Entities of different concrete types are never equal even when their ids collide,
        // which matters because ids here are UUIDs generated independently per table.
        return GetType() == other.GetType() && EqualityComparer<TId>.Default.Equals(Id, other.Id);
    }

    public override bool Equals(object? obj) => Equals(obj as Entity<TId>);

    public override int GetHashCode() => HashCode.Combine(GetType(), Id);

    public static bool operator ==(Entity<TId>? left, Entity<TId>? right) =>
        left?.Equals(right) ?? right is null;

    public static bool operator !=(Entity<TId>? left, Entity<TId>? right) => !(left == right);
}
