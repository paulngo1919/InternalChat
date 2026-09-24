using InternalChat.Application.Abstractions;
using InternalChat.Domain.Notifications;
using Microsoft.EntityFrameworkCore;

namespace InternalChat.Infrastructure.Persistence.Repositories;

/// <summary>EF Core implementation of <see cref="IReadStateRepository"/> (T130).</summary>
public sealed class ReadStateRepository : IReadStateRepository
{
    private readonly ChatDbContext _context;

    /// <summary>Creates the repository.</summary>
    public ReadStateRepository(ChatDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
    }

    /// <inheritdoc />
    public async Task<ReadState?> FindAsync(
        Guid employeeId,
        Guid conversationId,
        CancellationToken cancellationToken = default) =>
        await _context.ReadStates
            .FirstOrDefaultAsync(
                r => r.EmployeeId == employeeId && r.ConversationId == conversationId,
                cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task AddAsync(ReadState readState, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(readState);
        await _context.ReadStates.AddAsync(readState, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// One query across every conversation the employee belongs to, with the read position resolved
    /// per row by a correlated subquery rather than a second round trip per conversation — the same
    /// N+1 concern <see cref="ConversationRepository.SummariseAsync"/> already avoids for member
    /// counts and previews.
    /// </remarks>
    public async Task<UnreadSummary> SummariseUnreadAsync(
        Guid employeeId,
        CancellationToken cancellationToken = default)
    {
        var rows = await (
            from membership in _context.Memberships
            join conversation in _context.Conversations
                on membership.ConversationId equals conversation.Id
            where membership.EmployeeId == employeeId && membership.RemovedAt == null
            select new
            {
                conversation.LastSeq,
                membership.VisibleFromSeq,
                LastReadSeq = _context.ReadStates
                    .Where(r => r.EmployeeId == employeeId && r.ConversationId == conversation.Id)
                    .Select(r => (long?)r.LastReadSeq)
                    .FirstOrDefault() ?? 0,
            })
            .AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        long total = 0;
        int withUnread = 0;

        foreach (var row in rows)
        {
            long floor = Math.Max(row.LastReadSeq, row.VisibleFromSeq);
            long unread = Math.Max(0, row.LastSeq - floor);

            if (unread > 0)
            {
                total += unread;
                withUnread++;
            }
        }

        return new UnreadSummary(total, withUnread);
    }
}
