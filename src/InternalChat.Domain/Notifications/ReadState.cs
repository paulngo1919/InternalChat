using InternalChat.Domain.Common;

namespace InternalChat.Domain.Notifications;

/// <summary>
/// How far one employee has read one conversation (data-model.md, <c>read_state</c>).
/// </summary>
/// <remarks>
/// <para>
/// Not an <see cref="Entity{TId}"/>, for the same reason <see cref="Conversations.Membership"/> is
/// not one: the primary key is the composite <c>(employee_id, conversation_id)</c>, and a surrogate
/// id would invite code to reference a read state by something other than the pair that defines it.
/// </para>
/// <para>
/// <b><see cref="AdvanceTo"/> is the whole mechanism behind FR-036.</b> Two devices can report reads
/// in either order — a phone catching up five minutes after a laptop already read further — and
/// taking the greatest of the two, rather than assigning the latest report, is what stops the
/// laptop's read from being silently undone by the phone's stale one arriving later.
/// </para>
/// </remarks>
public sealed class ReadState
{
    private ReadState(Guid employeeId, Guid conversationId, long lastReadSeq, DateTimeOffset updatedAt)
    {
        EmployeeId = employeeId;
        ConversationId = conversationId;
        LastReadSeq = lastReadSeq;
        UpdatedAt = updatedAt;
    }

    /// <summary>Whose read position this is.</summary>
    public Guid EmployeeId { get; private set; }

    /// <summary>Which conversation.</summary>
    public Guid ConversationId { get; private set; }

    /// <summary>
    /// Highest sequence read. Unread count is <c>last_seq - GREATEST(last_read_seq, visible_from_seq)</c>,
    /// computed rather than stored here (data-model.md), so it cannot drift from the messages
    /// actually present.
    /// </summary>
    public long LastReadSeq { get; private set; }

    /// <summary>When this last advanced.</summary>
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Creates a first read position.</summary>
    public static ReadState Start(Guid employeeId, Guid conversationId, long lastReadSeq, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentOutOfRangeException.ThrowIfNegative(lastReadSeq);

        return new ReadState(employeeId, conversationId, lastReadSeq, clock.UtcNow);
    }

    /// <summary>
    /// Merges a reported read position, monotonically.
    /// </summary>
    /// <returns>
    /// <c>true</c> when the position actually advanced. <c>false</c> for a report at or below the
    /// current position — a client catching up on a stale device report, or a redelivered
    /// <c>PUT</c>. The caller uses this to decide whether there is anything worth publishing;
    /// re-publishing an unchanged read state would tell other devices "you have new information"
    /// about nothing.
    /// </returns>
    public bool AdvanceTo(long seq, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentOutOfRangeException.ThrowIfNegative(seq);

        if (seq <= LastReadSeq)
        {
            return false;
        }

        LastReadSeq = seq;
        UpdatedAt = clock.UtcNow;
        return true;
    }
}

/// <summary>
/// Raised when a read position advances (<c>chat.read_state.updated.v1</c>), so the employee's
/// other devices can clear the same badge (FR-036).
/// </summary>
/// <remarks>
/// Constructed directly by the use case that changes a <see cref="ReadState"/>, not raised through
/// <see cref="Entity{TId}.Raise"/> — <see cref="ReadState"/> is deliberately not an
/// <see cref="Entity{TId}"/>, the same reasoning <see cref="Conversations.MembershipChanged"/>
/// documents for <see cref="Conversations.Membership"/>.
/// </remarks>
public sealed record ReadStateUpdated(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid EmployeeId,
    Guid ConversationId,
    long LastReadSeq) : DomainEvent(EventId, OccurredAt)
{
    /// <inheritdoc />
    public override string EventType => "chat.read_state.updated.v1";
}
