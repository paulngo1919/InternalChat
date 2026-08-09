using InternalChat.Domain.Common;

namespace InternalChat.Domain.Conversations;

/// <summary>
/// The authorization record. Every access decision in the system resolves to a row of this type.
/// </summary>
/// <remarks>
/// <para>
/// data-model.md calls this "the authorization record", and that is not a figure of speech: reading
/// a message, retrieving an attachment, searching, and joining a meeting all reduce to "is there a
/// live membership row for this employee and this conversation". There is no second mechanism and
/// no bypass — Constitution Principle IV requires the check be resource-scoped, so a role, a token
/// scope, or a signed URL is never sufficient on its own.
/// </para>
/// <para>
/// Not an <see cref="Entity{TId}"/>: the primary key is the composite
/// <c>(conversation_id, employee_id)</c> and adding a surrogate id purely to satisfy a base class
/// would invite code to reference a membership by something other than the pair that defines it.
/// </para>
/// <para>
/// <b>Removal is soft.</b> <see cref="RemovedAt"/> is set rather than the row deleted, because
/// SC-021 requires answering "who had access on this date, and who changed it" a year later, and a
/// deleted row answers neither. A row with <see cref="RemovedAt"/> set grants nothing —
/// including for search (FR-032) and attachments (FR-025).
/// </para>
/// </remarks>
public sealed class Membership
{
    private Membership(
        Guid conversationId,
        Guid employeeId,
        MembershipRole role,
        DateTimeOffset joinedAt,
        long visibleFromSeq)
    {
        ConversationId = conversationId;
        EmployeeId = employeeId;
        Role = role;
        JoinedAt = joinedAt;
        VisibleFromSeq = visibleFromSeq;
    }

    /// <summary>The conversation this grants access to.</summary>
    public Guid ConversationId { get; private set; }

    /// <summary>The employee the access belongs to.</summary>
    public Guid EmployeeId { get; private set; }

    /// <summary>Role within this conversation.</summary>
    public MembershipRole Role { get; private set; }

    /// <summary>When the employee joined, or most recently rejoined.</summary>
    public DateTimeOffset JoinedAt { get; private set; }

    /// <summary>
    /// History floor. Messages at or below this sequence are invisible to this member.
    /// </summary>
    /// <remarks>
    /// Enforced in the query (<c>seq &gt; visible_from_seq</c>) rather than in the UI. A UI-level
    /// filter is a display preference; a query-level filter is an access rule, and this is an
    /// access rule.
    /// </remarks>
    public long VisibleFromSeq { get; private set; }

    /// <summary>Notifications suppressed until this instant (FR-037). Access is unaffected.</summary>
    public DateTimeOffset? MutedUntil { get; private set; }

    /// <summary>When access was withdrawn. <c>null</c> while the membership is live.</summary>
    public DateTimeOffset? RemovedAt { get; private set; }

    /// <summary>True when this row currently grants access.</summary>
    public bool IsActive => RemovedAt is null;

    /// <summary>
    /// Creates a membership.
    /// </summary>
    /// <param name="visibleFromSeq">
    /// The conversation's current <c>last_seq</c> when its history visibility is
    /// <c>from_join</c>, or <c>0</c> for <c>full</c>. Computed by the caller because only the
    /// conversation knows its own rule and its own sequence.
    /// </param>
    public static Membership Join(
        Guid conversationId,
        Guid employeeId,
        MembershipRole role,
        long visibleFromSeq,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentOutOfRangeException.ThrowIfNegative(visibleFromSeq);

        return new Membership(conversationId, employeeId, role, clock.UtcNow, visibleFromSeq);
    }

    /// <summary>
    /// Withdraws access. Idempotent — a repeated removal never moves <see cref="RemovedAt"/>.
    /// </summary>
    /// <remarks>
    /// Preserving the original timestamp matters for the same reason as
    /// <see cref="Employees.Employee.DeactivatedAt"/>: it is what an auditor reads to establish
    /// when access actually ended, and a duplicate message must not rewrite that answer.
    /// </remarks>
    public void Remove(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        RemovedAt ??= clock.UtcNow;
    }

    /// <summary>
    /// Restores a removed membership.
    /// </summary>
    /// <param name="visibleFromSeq">
    /// The floor implied by the conversation's rule at this moment.
    /// </param>
    /// <remarks>
    /// <b>The floor never moves down.</b> Someone removed at sequence 500 and re-added at 900 must
    /// not regain 500–900 — they were not a member for those messages, and lowering the floor would
    /// hand them history they were specifically excluded from. Taking the maximum is what makes
    /// that hold no matter which rule the conversation uses or how many times the cycle repeats.
    /// </remarks>
    public void Rejoin(long visibleFromSeq, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentOutOfRangeException.ThrowIfNegative(visibleFromSeq);

        RemovedAt = null;
        JoinedAt = clock.UtcNow;
        VisibleFromSeq = Math.Max(VisibleFromSeq, visibleFromSeq);
    }

    /// <summary>Changes the role held in this conversation.</summary>
    public void ChangeRole(MembershipRole role) => Role = role;

    /// <summary>Suppresses notifications until the given instant, or clears the setting.</summary>
    public void MuteUntil(DateTimeOffset? until) => MutedUntil = until;
}
