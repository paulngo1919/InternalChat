namespace InternalChat.Domain.Common;

/// <summary>
/// Base class for value objects — objects with no identity, equal when their components are
/// equal. A <c>MessageBody</c> holding the same text as another IS that value.
/// </summary>
/// <remarks>
/// Value objects are where invariants live most naturally: a <c>MessageBody</c> that cannot be
/// constructed empty or over 8,000 characters makes FR-019 unbreakable rather than merely
/// checked at one entry point.
/// </remarks>
public abstract class ValueObject : IEquatable<ValueObject>
{
    /// <summary>
    /// The components that define equality, in a stable order.
    /// </summary>
    protected abstract IEnumerable<object?> GetEqualityComponents();

    public bool Equals(ValueObject? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        return GetType() == other.GetType()
            && GetEqualityComponents().SequenceEqual(other.GetEqualityComponents());
    }

    public override bool Equals(object? obj) => Equals(obj as ValueObject);

    public override int GetHashCode()
    {
        HashCode hash = default;
        hash.Add(GetType());

        foreach (object? component in GetEqualityComponents())
        {
            hash.Add(component);
        }

        return hash.ToHashCode();
    }

    public static bool operator ==(ValueObject? left, ValueObject? right) =>
        left?.Equals(right) ?? right is null;

    public static bool operator !=(ValueObject? left, ValueObject? right) => !(left == right);
}
