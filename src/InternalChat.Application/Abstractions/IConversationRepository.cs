using InternalChat.Domain.Conversations;

namespace InternalChat.Application.Abstractions;

/// <summary>
/// One conversation as a list or detail view needs it, with the counts computed in the query.
/// </summary>
/// <param name="UnreadCount">
/// Computed as <c>last_seq - GREATEST(last_read_seq, visible_from_seq)</c> rather than stored
/// (data-model.md). A stored counter drifts the first time a message is deleted or a member is
/// re-added, and the drift is invisible until someone compares the badge with the messages actually
/// there.
/// </param>
/// <param name="LastMessageId">
/// The newest message, so a list renders a preview without one query per row. Carried as an id plus
/// its <c>sent_at</c> because <c>message</c>'s primary key needs both — the timestamp is the
/// partition key.
/// </param>
/// <param name="MutedUntil">
/// This employee's own mute for this conversation (<c>Membership.MutedUntil</c>, FR-037) — never
/// null for lack of the feature; null means genuinely not muted.
/// </param>
public sealed record ConversationSummary(
    Conversation Conversation,
    int MemberCount,
    int UnreadCount,
    Guid? LastMessageId,
    DateTimeOffset? LastMessageSentAt,
    DateTimeOffset? MutedUntil);

/// <summary>
/// Conversation writes and the sequence allocator.
/// </summary>
/// <remarks>
/// Split from <see cref="IConversationReader"/> for the same reason
/// <see cref="IEmployeeStore"/> is split from <see cref="IEmployeeDirectory"/>: this one hands back
/// tracked aggregates so invariants can be exercised, and an endpoint that only needs to render a
/// list should not acquire the ability to allocate a sequence number.
/// </remarks>
public interface IConversationRepository
{
    /// <summary>Loads a conversation, tracked so it can be modified.</summary>
    Task<Conversation?> FindAsync(Guid conversationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds the direct conversation for a canonical pair key, if one exists.
    /// </summary>
    /// <remarks>
    /// The read half of deduplication. Not sufficient on its own — two callers can both miss and
    /// both insert — which is why the unique partial index exists behind it. This is here so the
    /// common case, where the conversation already exists, costs one SELECT rather than a failed
    /// INSERT and a rollback.
    /// </remarks>
    Task<Conversation?> FindByDirectKeyAsync(string directKey, CancellationToken cancellationToken = default);

    /// <summary>Stages a new conversation for the current transaction.</summary>
    Task AddAsync(Conversation conversation, CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts a direct conversation, or reports the one that already claimed its key.
    /// </summary>
    /// <returns>
    /// The id now holding <see cref="Conversation.DirectKey"/>. Equal to
    /// <paramref name="conversation"/>'s id when this call created the row, and a different id when
    /// a concurrent caller won.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>Why this exists rather than <see cref="FindByDirectKeyAsync"/> followed by
    /// <see cref="AddAsync"/>.</b> That pair is a check-then-insert, and under concurrency every
    /// caller misses the check and every caller inserts. Eight simultaneous attempts produced one
    /// conversation and seven HTTP 500s — the unique index did its job and the handler had no way to
    /// recover, because the violation surfaces from <c>SaveChanges</c> inside the transaction
    /// behavior, after the handler has already returned.
    /// </para>
    /// <para>
    /// A single <c>INSERT ... ON CONFLICT DO NOTHING</c> has no such window. A concurrent
    /// transaction blocks on the conflicting row until the winner commits, then reports the
    /// conflict, and the loser can return the winner's conversation with no exception raised
    /// anywhere. It is the same shape as the message deduplication insert, for the same reason.
    /// </para>
    /// </remarks>
    Task<Guid> InsertDirectOrGetExistingAsync(
        Conversation conversation,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Allocates the next sequence for a conversation and returns it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>UPDATE conversation SET last_seq = last_seq + 1, updated_at = ... WHERE id = @id
    /// RETURNING last_seq</c> (research.md D1). The row lock the <c>UPDATE</c> takes is the entire
    /// concurrency control: two simultaneous sends serialize on it and receive different numbers. An
    /// in-memory increment on a loaded entity would hand the same number to both, and the index
    /// would then reject one — turning an ordinary concurrent send into an error the sender sees.
    /// </para>
    /// <para>
    /// <c>updated_at</c> moves in the same statement so the conversation list orders by real
    /// activity without a second write to remember.
    /// </para>
    /// <para>
    /// Must be called inside the send transaction, and only after the client message key has been
    /// claimed. Allocating before the idempotency check would let a duplicate send consume a
    /// sequence and leave a permanent gap.
    /// </para>
    /// </remarks>
    Task<long> AllocateSequenceAsync(Guid conversationId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Conversation read projections, always scoped to the employee asking.
/// </summary>
/// <remarks>
/// Every method takes an employee id and filters by active membership inside the query. There is no
/// "get any conversation" method by design: the scoping cannot then be forgotten at a call site, and
/// a caller has no id to substitute in order to read someone else's conversation.
/// </remarks>
public interface IConversationReader
{
    /// <summary>
    /// The conversations an employee is still a member of, most recent activity first.
    /// </summary>
    /// <param name="afterUpdatedAt">Keyset cursor, exclusive. <c>null</c> starts at the newest.</param>
    Task<IReadOnlyList<ConversationSummary>> ListForEmployeeAsync(
        Guid employeeId,
        int limit,
        DateTimeOffset? afterUpdatedAt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// One conversation, or <c>null</c> when the employee has no active membership in it.
    /// </summary>
    /// <remarks>
    /// A dedicated lookup rather than a filter over the list. This serves
    /// <c>GET /conversations/{id}</c>, which is on the hot path and is the endpoint T055 measures
    /// for timing — scanning an employee's whole conversation list to find one would make its cost a
    /// function of how many conversations the caller has, and that difference is exactly the kind of
    /// signal SC-017 forbids.
    /// </remarks>
    Task<ConversationSummary?> FindForEmployeeAsync(
        Guid conversationId,
        Guid employeeId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Just the ids of the conversations an employee may currently read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately separate from <see cref="ListForEmployeeAsync"/> rather than a projection over
    /// it. That method computes member counts, unread counts, and last-message previews — work a
    /// search scope has no use for, on a query that runs before every single search.
    /// </para>
    /// <para>
    /// Unpaged, and that is a considered limit rather than an oversight: this is the set a search is
    /// scoped to, so paging it would silently narrow the scope and make results depend on a page
    /// size. An employee in tens of conversations costs a few hundred bytes; one in thousands would
    /// make the <c>= ANY(...)</c> filter the wrong shape, and that is a scaling problem to solve
    /// when it exists rather than to half-solve now.
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<Guid>> ListAccessibleIdsAsync(
        Guid employeeId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Active members of a conversation, with the employee attributes a member list displays.
    /// </summary>
    /// <remarks>
    /// A joined projection rather than <see cref="IMembershipRepository.ListForConversationAsync"/>
    /// followed by a lookup per row — the same N+1 <see cref="ConversationSummary"/> avoids for
    /// counts and previews. Removed memberships are excluded here: a member list is a display
    /// concern, and data-model.md's rule that a removed row "grants nothing" applies to what is shown
    /// as much as to what is authorized.
    /// </remarks>
    Task<IReadOnlyList<MemberProjection>> ListMembersAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default);
}

/// <summary>One row of a member list — an active membership joined to its employee.</summary>
public sealed record MemberProjection(
    Guid EmployeeId,
    string DisplayName,
    string? Email,
    string? AvatarUrl,
    bool IsActive,
    MembershipRole Role,
    DateTimeOffset JoinedAt);
