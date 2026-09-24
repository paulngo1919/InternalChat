using InternalChat.Application.Abstractions;
using InternalChat.Domain.Meetings;
using Microsoft.EntityFrameworkCore;

namespace InternalChat.Infrastructure.Persistence.Repositories;

/// <summary>EF Core implementation of meeting persistence (T184).</summary>
public sealed class MeetingRepository : IMeetingRepository
{
    private readonly ChatDbContext _context;

    /// <summary>Creates the repository.</summary>
    public MeetingRepository(ChatDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
    }

    /// <inheritdoc />
    public async Task AddAsync(Meeting meeting, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(meeting);

        await _context.Meetings.AddAsync(meeting, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Uses the partial index on active meetings. Tracked, because the caller may join or end it —
    /// a meeting loaded for a webhook is loaded in order to change it.
    /// </remarks>
    public async Task<Meeting?> FindActiveForConversationAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default) =>
        await _context.Meetings
            .Include(m => m.Participants)
            .FirstOrDefaultAsync(
                m => m.ConversationId == conversationId && m.EndedAt == null,
                cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<Meeting?> FindAsync(
        Guid meetingId,
        CancellationToken cancellationToken = default) =>
        await _context.Meetings
            // Participations come with it: every caller needs the count, and the 25-participant cap
            // is computed from the collection rather than from a stored number, so a meeting loaded
            // without them would report itself empty and admit a 26th person.
            .Include(m => m.Participants)
            .FirstOrDefaultAsync(m => m.Id == meetingId, cancellationToken)
            .ConfigureAwait(false);
}
