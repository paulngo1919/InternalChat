using InternalChat.Application.Abstractions;
using InternalChat.Domain.Meetings;
using Microsoft.EntityFrameworkCore;

namespace InternalChat.Infrastructure.Persistence.Repositories;

/// <summary>EF Core implementation of screen-share persistence (T201).</summary>
public sealed class ShareSessionStore : IShareSessionStore
{
    private readonly ChatDbContext _context;

    /// <summary>Creates the store.</summary>
    public ShareSessionStore(ChatDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
    }

    /// <inheritdoc />
    public async Task<ShareSession?> FindActiveAsync(
        Guid meetingId,
        CancellationToken cancellationToken = default) =>
        await _context.ShareSessions
            // Tracked: the caller's next move is almost always to stop it.
            .FirstOrDefaultAsync(s => s.MeetingId == meetingId && s.StoppedAt == null, cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task AddAsync(ShareSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        await _context.ShareSessions.AddAsync(session, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Effectively a no-op when the entity is already tracked, which it always is in practice — it
    /// came from <see cref="FindActiveAsync"/> moments earlier. Present so the interface does not
    /// require the caller to know that.
    /// </remarks>
    public Task UpdateAsync(ShareSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        _context.ShareSessions.Update(session);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<Guid, int>> SumSharedSecondsAsync(
        Guid meetingId,
        CancellationToken cancellationToken = default)
    {
        // Only stopped sessions count: a share still running has no duration yet, which is the same
        // rule ShareSession.DurationSeconds applies. Projected to the two timestamps and summed in
        // memory rather than in SQL — PostgreSQL has no DateDiff translation for timestamptz that
        // EF can use here, and a meeting holds tens of shares rather than thousands.
        List<ShareSpan> spans = await _context.ShareSessions
            .AsNoTracking()
            .Where(s => s.MeetingId == meetingId && s.StoppedAt != null)
            .Select(s => new ShareSpan(s.EmployeeId, s.StartedAt, s.StoppedAt!.Value))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return spans
            .GroupBy(span => span.EmployeeId)
            .ToDictionary(
                group => group.Key,
                group => group.Sum(span =>
                    Math.Max(0, (int)Math.Round((span.StoppedAt - span.StartedAt).TotalSeconds))));
    }

    private sealed record ShareSpan(Guid EmployeeId, DateTimeOffset StartedAt, DateTimeOffset StoppedAt);
}
