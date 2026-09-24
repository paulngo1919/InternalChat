using InternalChat.Application.Abstractions;
using InternalChat.Domain.Conversations;
using Microsoft.EntityFrameworkCore;

namespace InternalChat.Infrastructure.Persistence.Repositories;

/// <summary>
/// EF Core implementation of conversation writes and the sequence allocator (T090).
/// </summary>
public sealed class ConversationRepository : IConversationRepository
{
    private readonly ChatDbContext _context;

    /// <summary>Creates the repository.</summary>
    public ConversationRepository(ChatDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
    }

    /// <inheritdoc />
    public async Task<Conversation?> FindAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default) =>
        await _context.Conversations
            .FirstOrDefaultAsync(c => c.Id == conversationId, cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<Conversation?> FindByDirectKeyAsync(
        string directKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directKey);

        // Kind is in the predicate as well as the key, so the query can use the partial unique
        // index (`WHERE kind = 'direct'`). Without it PostgreSQL cannot prove the index applies and
        // falls back to a sequential scan of every conversation on the platform.
        return await _context.Conversations
            .FirstOrDefaultAsync(
                c => c.DirectKey == directKey && c.Kind == ConversationKind.Direct,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task AddAsync(Conversation conversation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversation);

        await _context.Conversations.AddAsync(conversation, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Guid> InsertDirectOrGetExistingAsync(
        Conversation conversation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversation);

        if (conversation.DirectKey is null)
        {
            throw new ArgumentException(
                "Only a direct conversation has a key to deduplicate on.",
                nameof(conversation));
        }

        // A data-modifying CTE, not a bare INSERT. EF's SqlQuery composes what it is given —
        // `SELECT t."Value" FROM (<sql>) AS t` — and PostgreSQL rejects a data-modifying statement
        // inside a subquery. Written as a bare INSERT ... RETURNING it produced a syntax error on
        // every call, which reached the caller as an HTTP 500 even for an uncontended creation.
        //
        // The enums are cast explicitly because the parameters arrive as text and the columns are
        // PostgreSQL enum types.
        List<Guid> inserted = await _context.Database
            .SqlQuery<Guid>(
                $"""
                WITH inserted AS (
                    INSERT INTO conversation (
                        id, kind, name, created_by, history_visibility, last_seq, direct_key,
                        created_at, updated_at)
                    VALUES (
                        {conversation.Id},
                        'direct'::conversation_kind,
                        NULL,
                        {conversation.CreatedBy},
                        'full'::history_visibility,
                        0,
                        {conversation.DirectKey},
                        {conversation.CreatedAt},
                        {conversation.UpdatedAt})
                    ON CONFLICT DO NOTHING
                    RETURNING id
                )
                SELECT id AS "Value" FROM inserted
                """)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (inserted.Count == 1)
        {
            return inserted[0];
        }

        // Reading the winner has to be a SEPARATE statement, and this is the subtle part.
        //
        // `ON CONFLICT DO NOTHING` waits for the conflicting transaction to finish before reporting
        // the conflict — but under READ COMMITTED a statement's snapshot is taken when the statement
        // begins, which is before that wait. So a `UNION ALL` arm in the same statement looks for the
        // winner's row using a snapshot from before the winner committed, finds nothing, and returns
        // zero rows. That is exactly what happened: merging the two into one round trip turned a
        // reliable failure into an intermittent one, with roughly one attempt in eight returning a
        // 500 under contention.
        //
        // A new statement takes a fresh snapshot, and by then the winner has committed.
        Guid? winner = await _context.Conversations
            .AsNoTracking()
            .Where(c => c.DirectKey == conversation.DirectKey && c.Kind == ConversationKind.Direct)
            .Select(c => (Guid?)c.Id)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return winner ?? throw new InvalidOperationException(
            $"The insert for direct key '{conversation.DirectKey}' conflicted, and yet no "
            + "conversation holds that key. That combination should be impossible; it suggests "
            + "ux_conversation_direct_key is missing or its filter no longer matches this query.");
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Raw SQL, because this is the one place where the exact statement matters more than the
    /// convenience of the model. <c>UPDATE ... RETURNING</c> is a single atomic round trip that both
    /// takes the row lock and reports the new value; read-then-write through the change tracker would
    /// be two statements with a window between them in which another sender allocates the same
    /// number.
    /// </para>
    /// <para>
    /// <c>updated_at</c> moves here too, so the conversation list orders by activity as a consequence
    /// of sending rather than as a second write that a future code path could omit.
    /// </para>
    /// <para>
    /// The change tracker does not observe this, which is why <see cref="Conversation.SynchroniseSequence"/>
    /// exists — the caller tells the loaded entity what the row now says.
    /// </para>
    /// </remarks>
    public async Task<long> AllocateSequenceAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        List<long> allocated = await _context.Database
            .SqlQuery<long>(
                $"""
                UPDATE conversation
                SET last_seq = last_seq + 1,
                    updated_at = now()
                WHERE id = {conversationId}
                RETURNING last_seq AS "Value"
                """)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return allocated.Count == 1
            ? allocated[0]
            : throw new InvalidOperationException(
                $"Could not allocate a sequence for conversation {conversationId}: the row does not "
                + "exist. The send path checks the conversation first, so this means it was deleted "
                + "mid-transaction.");
    }
}

/// <summary>
/// EF Core implementation of the conversation read projections (T090).
/// </summary>
/// <remarks>
/// <para>
/// Every query joins <c>membership</c> and filters on <c>removed_at IS NULL</c>, so scoping is a
/// property of the SQL rather than something a caller applies. It also joins <c>employee</c> and
/// requires <c>active</c>: a membership row outlives deactivation on purpose (SC-021 needs it a year
/// later), so a query that checked only the membership would keep serving conversations to someone
/// who has left.
/// </para>
/// <para>
/// <c>AsNoTracking</c> throughout. These are projections nobody modifies, and tracking them would put
/// entities into the change tracker that a later <c>SaveChanges</c> might try to write.
/// </para>
/// </remarks>
public sealed class ConversationReader : IConversationReader
{
    private readonly ChatDbContext _context;

    /// <summary>Creates the reader.</summary>
    public ConversationReader(ChatDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ConversationSummary>> ListForEmployeeAsync(
        Guid employeeId,
        int limit,
        DateTimeOffset? afterUpdatedAt,
        CancellationToken cancellationToken = default)
    {
        IQueryable<Conversation> reachable = Reachable(employeeId);

        if (afterUpdatedAt is not null)
        {
            // Strictly below the cursor, matching the descending order. Inclusive would return the
            // cursor row again on every page and the client would never advance.
            reachable = reachable.Where(c => c.UpdatedAt < afterUpdatedAt);
        }

        List<Conversation> conversations = await reachable
            .OrderByDescending(c => c.UpdatedAt)
            .ThenByDescending(c => c.Id)
            .Take(limit)
            .AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return await SummariseAsync(conversations, employeeId, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <inheritdoc />
    /// <remarks>
    /// Reuses <c>Reachable</c>, the same predicate every other read here is scoped by, so the set a
    /// search covers and the set the conversation list shows can never drift apart. A search that
    /// reached one conversation more than the list does would be an access-control bug wearing a
    /// filtering bug's clothes.
    /// </remarks>
    public async Task<IReadOnlyList<Guid>> ListAccessibleIdsAsync(
        Guid employeeId,
        CancellationToken cancellationToken = default) =>
        await Reachable(employeeId)
            .AsNoTracking()
            .Select(c => c.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<ConversationSummary?> FindForEmployeeAsync(
        Guid conversationId,
        Guid employeeId,
        CancellationToken cancellationToken = default)
    {
        Conversation? conversation = await Reachable(employeeId)
            .Where(c => c.Id == conversationId)
            .AsNoTracking()
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (conversation is null)
        {
            return null;
        }

        IReadOnlyList<ConversationSummary> summarised =
            await SummariseAsync([conversation], employeeId, cancellationToken).ConfigureAwait(false);

        return summarised[0];
    }

    /// <inheritdoc />
    /// <remarks>
    /// No <c>employee.Status == Active</c> filter, unlike <see cref="Reachable"/>. A deactivated
    /// employee's membership row still grants nothing (data-model.md), but a member list is what
    /// establishes who was in the conversation — hiding a departed colleague entirely would make a
    /// past conversation look like it always had one fewer person in it.
    /// </remarks>
    public async Task<IReadOnlyList<MemberProjection>> ListMembersAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        return await (
            from membership in _context.Memberships
            join employee in _context.Employees
                on membership.EmployeeId equals employee.Id
            where membership.ConversationId == conversationId && membership.RemovedAt == null
            orderby membership.JoinedAt
            select new MemberProjection(
                employee.Id,
                employee.DisplayName,
                employee.Email,
                employee.AvatarUrl,
                employee.Status == Domain.Employees.EmployeeStatus.Active,
                membership.Role,
                membership.JoinedAt))
            .AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Conversations this employee can currently reach.
    /// </summary>
    /// <remarks>
    /// The <c>employee</c> join is not redundant with the membership filter. Deactivation does not
    /// remove memberships, so without it a departed employee's list would still resolve — which is
    /// the exact gap T055 tests for on the authorization path.
    /// </remarks>
    private IQueryable<Conversation> Reachable(Guid employeeId) =>
        from conversation in _context.Conversations
        join membership in _context.Memberships
            on conversation.Id equals membership.ConversationId
        join employee in _context.Employees
            on membership.EmployeeId equals employee.Id
        where membership.EmployeeId == employeeId
            && membership.RemovedAt == null
            && employee.Status == Domain.Employees.EmployeeStatus.Active
        select conversation;

    /// <summary>
    /// Adds member counts and last-message pointers in two queries, not two per row.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The N+1 this avoids is the reason <c>QueryCountInterceptor</c> (T028) exists: a conversation
    /// list of fifty rows, each fetching its own count and preview, is a hundred and one queries
    /// against a 250 ms budget. Both aggregates are read for the whole page at once and joined in
    /// memory.
    /// </para>
    /// <para>
    /// <c>UnreadCount</c> is <c>last_seq - GREATEST(last_read_seq, visible_from_seq)</c>
    /// (data-model.md, T131), read from <c>read_state</c> and the same membership floor
    /// <see cref="Reachable"/> joins — computed here rather than stored, so it cannot drift from
    /// the messages actually present.
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyList<ConversationSummary>> SummariseAsync(
        List<Conversation> conversations,
        Guid employeeId,
        CancellationToken cancellationToken)
    {
        if (conversations.Count == 0)
        {
            return [];
        }

        Guid[] ids = [.. conversations.Select(c => c.Id)];

        Dictionary<Guid, int> memberCounts = await _context.Memberships
            .Where(m => ids.Contains(m.ConversationId) && m.RemovedAt == null)
            .GroupBy(m => m.ConversationId)
            .Select(g => new { ConversationId = g.Key, Count = g.Count() })
            .AsNoTracking()
            .ToDictionaryAsync(x => x.ConversationId, x => x.Count, cancellationToken)
            .ConfigureAwait(false);

        // The newest message per conversation, above this employee's own floor so the preview never
        // shows text from before they joined. Served by ix_message_conversation_seq.
        var previews = await (
            from message in _context.Messages
            join membership in _context.Memberships
                on message.ConversationId equals membership.ConversationId
            where ids.Contains(message.ConversationId)
                && membership.EmployeeId == employeeId
                && membership.RemovedAt == null
                && message.Seq > membership.VisibleFromSeq
            group new { message.Id, message.SentAt, message.Seq } by message.ConversationId into grouped
            select new
            {
                ConversationId = grouped.Key,
                MaxSeq = grouped.Max(m => m.Seq),
            })
            .AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        Dictionary<Guid, (Guid Id, DateTimeOffset SentAt)> lastMessages = [];

        if (previews.Count > 0)
        {
            long[] maxSeqs = [.. previews.Select(p => p.MaxSeq)];
            Guid[] previewIds = [.. previews.Select(p => p.ConversationId)];

            // Resolved in one further query rather than one per conversation. The pair
            // (conversation_id, seq) is unique by construction, so matching on the set of maxima
            // cannot pick up an extra row for a conversation whose maximum differs.
            var rows = await _context.Messages
                .Where(m => previewIds.Contains(m.ConversationId) && maxSeqs.Contains(m.Seq))
                .Select(m => new { m.ConversationId, m.Id, m.SentAt, m.Seq })
                .AsNoTracking()
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            foreach (var preview in previews)
            {
                var match = rows.FirstOrDefault(
                    r => r.ConversationId == preview.ConversationId && r.Seq == preview.MaxSeq);

                if (match is not null)
                {
                    lastMessages[preview.ConversationId] = (match.Id, match.SentAt);
                }
            }
        }

        // This employee's history floor per conversation — the same join Reachable() applies, read
        // again here because SummariseAsync works from the plain Conversation rows Reachable()
        // already filtered down to, not from the membership rows themselves.
        Dictionary<Guid, long> visibleFromSeq = await _context.Memberships
            .Where(m => ids.Contains(m.ConversationId) && m.EmployeeId == employeeId && m.RemovedAt == null)
            .Select(m => new { m.ConversationId, m.VisibleFromSeq })
            .AsNoTracking()
            .ToDictionaryAsync(x => x.ConversationId, x => x.VisibleFromSeq, cancellationToken)
            .ConfigureAwait(false);

        // Absent for a conversation this employee has never opened — floors to 0 below, same as
        // data-model.md's GREATEST(last_read_seq, visible_from_seq) with no read_state row yet.
        Dictionary<Guid, long> lastReadSeq = await _context.ReadStates
            .Where(r => ids.Contains(r.ConversationId) && r.EmployeeId == employeeId)
            .Select(r => new { r.ConversationId, r.LastReadSeq })
            .AsNoTracking()
            .ToDictionaryAsync(x => x.ConversationId, x => x.LastReadSeq, cancellationToken)
            .ConfigureAwait(false);

        Dictionary<Guid, DateTimeOffset?> mutedUntil = await _context.Memberships
            .Where(m => ids.Contains(m.ConversationId) && m.EmployeeId == employeeId && m.RemovedAt == null)
            .Select(m => new { m.ConversationId, m.MutedUntil })
            .AsNoTracking()
            .ToDictionaryAsync(x => x.ConversationId, x => x.MutedUntil, cancellationToken)
            .ConfigureAwait(false);

        return
        [
            .. conversations.Select(conversation =>
            {
                lastMessages.TryGetValue(conversation.Id, out (Guid Id, DateTimeOffset SentAt) last);

                long floor = Math.Max(
                    lastReadSeq.GetValueOrDefault(conversation.Id),
                    visibleFromSeq.GetValueOrDefault(conversation.Id));
                int unread = (int)Math.Max(0, conversation.LastSeq - floor);

                return new ConversationSummary(
                    conversation,
                    memberCounts.GetValueOrDefault(conversation.Id),
                    unread,
                    last.Id == Guid.Empty ? null : last.Id,
                    last.Id == Guid.Empty ? null : last.SentAt,
                    mutedUntil.GetValueOrDefault(conversation.Id));
            }),
        ];
    }
}
