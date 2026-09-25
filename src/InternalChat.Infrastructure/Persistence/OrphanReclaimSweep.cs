using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using InternalChat.Application.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace InternalChat.Infrastructure.Persistence;

/// <summary>
/// Implements the orphaned object reclamation sweep (T210).
/// </summary>
public sealed class OrphanReclaimSweep : IOrphanReclaimSweep
{
    private readonly ChatDbContext _dbContext;
    private readonly IObjectStore _objectStore;
    private readonly InternalChat.Domain.Common.IClock _clock;

    public OrphanReclaimSweep(ChatDbContext dbContext, IObjectStore objectStore, InternalChat.Domain.Common.IClock clock)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(objectStore);
        ArgumentNullException.ThrowIfNull(clock);

        _dbContext = dbContext;
        _objectStore = objectStore;
        _clock = clock;
    }

    public async Task<int> ReclaimOrphansAsync(TimeSpan ageThreshold, int batchSize, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);

        DateTimeOffset cutoff = _clock.UtcNow - ageThreshold;

        // Orphans are attachments that either:
        // 1. Have no MessageId (abandoned uploads)
        // 2. Have a MessageId but the message row no longer exists (e.g. partition dropped or message deleted)
        
        // Find orphans: we query the attachments table where CreatedAt < cutoff
        // and either MessageId is null OR there is no matching message.
        // We use a LEFT JOIN to the message table. Since we don't have a navigation property for Message
        // (to avoid deep coupling in the domain), we can write the query using GroupJoin/SelectMany or 
        // raw SQL, or just use the DbContext's Messages set.
        
        var orphans = await _dbContext.Attachments
            .Where(a => a.CreatedAt < cutoff)
            .Where(a => a.MessageId == null || !_dbContext.Messages.Any(m => m.Id == a.MessageId && m.SentAt == a.MessageSentAt))
            .OrderBy(a => a.CreatedAt)
            .Take(batchSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (orphans.Count == 0)
        {
            return 0;
        }

        // Object first, row second.
        foreach (var attachment in orphans)
        {
            await _objectStore.DeleteAsync(attachment.ObjectKey, cancellationToken).ConfigureAwait(false);
            await _objectStore.DeleteAsync($"{attachment.ObjectKey}.poster.jpg", cancellationToken).ConfigureAwait(false);
        }

        _dbContext.Attachments.RemoveRange(orphans);
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return orphans.Count;
    }
}
