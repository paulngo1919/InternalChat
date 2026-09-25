using System;
using System.Threading;
using System.Threading.Tasks;

namespace InternalChat.Application.Abstractions;

/// <summary>
/// Sweeps abandoned uploads and orphaned attachments (T210).
/// </summary>
public interface IOrphanReclaimSweep
{
    /// <summary>
    /// Deletes one batch of orphaned attachments, purging their objects from storage.
    /// </summary>
    /// <param name="ageThreshold">Attachments created before this are eligible for reclaim.</param>
    /// <param name="batchSize">Maximum number of attachments to delete in this batch.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of attachments removed. Zero means none found.</returns>
    Task<int> ReclaimOrphansAsync(TimeSpan ageThreshold, int batchSize, CancellationToken cancellationToken = default);
}
