using InternalChat.Application.Abstractions;
using InternalChat.Domain.Messages;
using InternalChat.Infrastructure.Persistence.Messages;
using Microsoft.EntityFrameworkCore;

namespace InternalChat.Infrastructure.Persistence.Repositories;

/// <summary>
/// EF Core implementation of message persistence (T090).
/// </summary>
public sealed class MessageRepository : IMessageRepository
{
    private readonly ChatDbContext _context;

    /// <summary>Creates the repository.</summary>
    public MessageRepository(ChatDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
    }

    /// <inheritdoc />
    public async Task AddAsync(Message message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        await _context.Messages.AddAsync(message, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Message?> FindAsync(
        Guid conversationId,
        Guid messageId,
        DateTimeOffset? sentAt = null,
        CancellationToken cancellationToken = default)
    {
        IQueryable<Message> query = _context.Messages
            .Where(m => m.ConversationId == conversationId && m.Id == messageId);

        if (sentAt is not null)
        {
            // Turns a scan of every monthly partition into a primary-key lookup in one of them.
            // Worth the extra parameter: without it this query touches all sixteen partitions on a
            // table holding 125 million rows, on the send path's retry branch.
            query = query.Where(m => m.SentAt == sentAt);
        }

        return await query.FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Ordering follows the direction being paged, and both are bounded and ordered in SQL. The
    /// <c>afterSeq</c> branch reads oldest-first because a client applying a catch-up batch in
    /// reverse would render the conversation backwards; the <c>beforeSeq</c> branch reads
    /// newest-first because that is how history is scrolled.
    /// </para>
    /// <para>
    /// The result of the forward branch is <em>not</em> re-sorted afterwards. Both branches leave
    /// the rows in the order the caller should apply them, so the last element is always the cursor
    /// for the next page.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<Message>> GetHistoryAsync(
        MessageHistoryQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        // The floor is part of the predicate, never a filter applied to fetched rows. Filtering
        // afterwards would make the page size depend on how much of it the caller may see.
        IQueryable<Message> messages = _context.Messages
            .Where(m => m.ConversationId == query.ConversationId && m.Seq > query.VisibleFromSeq);

        if (query.AfterSeq is not null)
        {
            return await messages
                .Where(m => m.Seq > query.AfterSeq)
                .OrderBy(m => m.Seq)
                .Take(query.Limit)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        if (query.BeforeSeq is not null)
        {
            messages = messages.Where(m => m.Seq < query.BeforeSeq);
        }

        return await messages
            .OrderByDescending(m => m.Seq)
            .Take(query.Limit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <c>INSERT ... ON CONFLICT DO NOTHING</c>, then a read of whoever holds the key. Raw SQL
    /// because the change tracker cannot express "insert unless it exists, and tell me who won"
    /// without a round trip in between — and that round trip is precisely the window two racing
    /// sends would both pass through.
    /// </para>
    /// <para>
    /// The row is <b>not</b> staged in the change tracker. It has to reach the database now, inside
    /// the ambient transaction, because its visibility to a concurrent transaction is what serialises
    /// the two senders. Deferring it to <c>SaveChanges</c> would let both proceed and both insert a
    /// message.
    /// </para>
    /// <para>
    /// Written inside the transaction the pipeline opened, so a handler that fails afterwards
    /// releases the key. A claim that outlived a failed send would permanently refuse the client's
    /// retry — the message would be lost and the client told it already exists.
    /// </para>
    /// </remarks>
    public async Task<ClaimedMessage?> TryClaimClientKeyAsync(
        Guid conversationId,
        ClientMessageKey clientMessageKey,
        Guid messageId,
        DateTimeOffset sentAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clientMessageKey);

        string key = clientMessageKey.Value;

        int inserted = await _context.Database
            .ExecuteSqlAsync(
                $"""
                INSERT INTO message_dedup (conversation_id, client_message_key, message_id, sent_at)
                VALUES ({conversationId}, {key}, {messageId}, {sentAt})
                ON CONFLICT (conversation_id, client_message_key) DO NOTHING
                """,
                cancellationToken)
            .ConfigureAwait(false);

        if (inserted == 1)
        {
            return null;
        }

        // Lost the race, or this is a retry of an earlier success. Either way the winner's row names
        // the message to return. Read rather than assumed: the caller needs the winner's id and
        // sent_at, not its own.
        List<MessageDeduplicationRecord> holder = await _context.MessageDeduplication
            .Where(d => d.ConversationId == conversationId && d.ClientMessageKey == key)
            .AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return holder.Count == 1
            ? new ClaimedMessage(holder[0].MessageId, holder[0].SentAt)
            : throw new InvalidOperationException(
                $"The insert of client message key '{key}' in conversation {conversationId} "
                + "conflicted, yet no row holds it. The primary key on message_dedup is what makes "
                + "that combination impossible, so it has been dropped or altered.");
    }
}

/// <summary>
/// EF Core implementation of membership persistence for the mutating paths (T090).
/// </summary>
public sealed class MembershipRepository : IMembershipRepository
{
    private readonly ChatDbContext _context;

    /// <summary>Creates the repository.</summary>
    public MembershipRepository(ChatDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
    }

    /// <inheritdoc />
    public async Task AddAsync(
        Domain.Conversations.Membership membership,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(membership);

        await _context.Memberships.AddAsync(membership, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Removed rows are returned, not filtered. Every caller decides what to do with one, and a
    /// re-add specifically needs the old row so <c>Rejoin</c> can take the higher of the two history
    /// floors.
    /// </remarks>
    public async Task<Domain.Conversations.Membership?> FindAsync(
        Guid conversationId,
        Guid employeeId,
        CancellationToken cancellationToken = default) =>
        await _context.Memberships
            .FirstOrDefaultAsync(
                m => m.ConversationId == conversationId && m.EmployeeId == employeeId,
                cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyList<Domain.Conversations.Membership>> ListForConversationAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default) =>
        await _context.Memberships
            .Where(m => m.ConversationId == conversationId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
}
