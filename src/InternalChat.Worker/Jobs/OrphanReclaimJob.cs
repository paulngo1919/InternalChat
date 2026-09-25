using System;
using System.Threading;
using System.Threading.Tasks;
using InternalChat.Application.Abstractions;
using InternalChat.Domain.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace InternalChat.Worker.Jobs;

/// <summary>
/// Sweeps orphaned objects and abandoned uploads from storage (T210).
/// </summary>
public sealed partial class OrphanReclaimJob : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);
    private static readonly TimeSpan AgeThreshold = TimeSpan.FromHours(24);
    private const int BatchSize = 100;

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<OrphanReclaimJob> _logger;
    private readonly IClock _clock;

    public OrphanReclaimJob(IServiceScopeFactory scopes, IClock clock, ILogger<OrphanReclaimJob> logger)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        _scopes = scopes;
        _clock = clock;
        _logger = logger;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Orphan reclaim sweep failed.")]
    private partial void LogSweepFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Reclaimed {Count} orphaned attachments.")]
    private partial void LogReclaimed(int count);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(Interval);

        do
        {
            try
            {
                await SweepAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                LogSweepFailed(ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    private async Task SweepAsync(CancellationToken cancellationToken)
    {
        int totalReclaimed = 0;
        int batchReclaimed;

        do
        {
            await using AsyncServiceScope scope = _scopes.CreateAsyncScope();
            var sweep = scope.ServiceProvider.GetRequiredService<IOrphanReclaimSweep>();

            batchReclaimed = await sweep.ReclaimOrphansAsync(AgeThreshold, BatchSize, cancellationToken).ConfigureAwait(false);
            totalReclaimed += batchReclaimed;
        }
        while (batchReclaimed == BatchSize && !cancellationToken.IsCancellationRequested);

        if (totalReclaimed > 0)
        {
            LogReclaimed(totalReclaimed);
        }
    }
}
