using InternalChat.Application.Abstractions;

namespace InternalChat.Worker.Jobs;

/// <summary>
/// T089 — keeps monthly <c>message</c> partitions ahead of the clock (research.md D11).
/// </summary>
/// <remarks>
/// <para>
/// <b>What happens without this job is a total outage of sending.</b> PostgreSQL does not route a row
/// to the parent when no partition covers its range; the insert fails. At 00:00 on the first of a
/// month with no partition ready, every send on the platform starts returning an error, and it stays
/// that way until a person notices and runs DDL by hand.
/// </para>
/// <para>
/// It therefore runs <b>once at startup and then daily</b>, rather than monthly near the boundary. A
/// monthly schedule has exactly one chance to work, and if the Worker happens to be restarting or
/// the database is briefly unreachable at that moment, the window is missed silently. Daily with a
/// three-month runway means roughly ninety consecutive failures are needed before anything breaks.
/// </para>
/// <para>
/// This host runs at a single replica (Principle V), so there is no coordination to do. Two replicas
/// would both call the same idempotent function and the second would create nothing — but the job
/// would also stop being a reliable signal of anything, because the log would show two runs whether
/// or not either succeeded.
/// </para>
/// </remarks>
public sealed partial class PartitionMaintenanceJob : BackgroundService
{
    /// <summary>
    /// How many months beyond the current one to keep ready.
    /// </summary>
    /// <remarks>
    /// Three. One would be enough for correctness if the job never failed; three means the runway
    /// survives a Worker that has been down for two months, which has happened to every platform
    /// that has ever had a long incident.
    /// </remarks>
    private const int MonthsAhead = 3;

    /// <summary>
    /// Runway below which the job complains rather than reporting success.
    /// </summary>
    /// <remarks>
    /// The distinction matters: "the job ran" and "there is a partition for next month" are different
    /// facts, and only the second one keeps the platform sending. A run that leaves less than a month
    /// of runway has not done its job even if every statement succeeded.
    /// </remarks>
    private const int MinimumHealthyRunway = 1;

    private static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<PartitionMaintenanceJob> _logger;

    /// <summary>Creates the job.</summary>
    public PartitionMaintenanceJob(IServiceScopeFactory scopes, ILogger<PartitionMaintenanceJob> logger)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(logger);

        _scopes = scopes;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Immediately, before the first delay. Waiting a day after startup would mean a freshly
        // deployed platform has whatever runway its migration happened to create and no more.
        using PeriodicTimer timer = new(Interval);

        do
        {
            try
            {
                await EnsureAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Shutdown, not a failure.
                return;
            }
            catch (Exception exception)
            {
                // Swallowed deliberately: an unhandled exception in a BackgroundService stops the
                // host, and a database hiccup must not take the Worker — and with it the outbox
                // dispatcher and every consumer — down with it. The next tick retries.
                MaintenanceFailed(_logger, exception);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    private async Task EnsureAsync(CancellationToken cancellationToken)
    {
        // A fresh scope per run. The maintenance helper depends on a scoped DbContext, and holding
        // one for the lifetime of the process would keep a connection and a change tracker alive for
        // months.
        using IServiceScope scope = _scopes.CreateScope();

        IPartitionMaintenance maintenance =
            scope.ServiceProvider.GetRequiredService<IPartitionMaintenance>();

        await maintenance.EnsureMonthPartitionsAsync(MonthsAhead, cancellationToken).ConfigureAwait(false);

        int runway = await maintenance.CountMonthsAheadAsync(cancellationToken).ConfigureAwait(false);

        if (runway < MinimumHealthyRunway)
        {
            // Warning rather than an exception. Throwing would retry in a day, which is no sooner
            // than the next tick anyway, and would lose the one line that tells an operator what is
            // about to happen.
            RunwayTooShort(_logger, runway);
            return;
        }

        RunwayHealthy(_logger, runway);
    }

    [LoggerMessage(
        EventId = 5000,
        Level = LogLevel.Information,
        Message = "Message partitions are ready for {MonthsAhead} month(s) beyond the current one")]
    private static partial void RunwayHealthy(ILogger logger, int monthsAhead);

    [LoggerMessage(
        EventId = 5001,
        Level = LogLevel.Warning,
        Message = "Only {MonthsAhead} month(s) of message partitions exist beyond the current one. "
            + "Sending fails outright once the clock passes the last partition.")]
    private static partial void RunwayTooShort(ILogger logger, int monthsAhead);

    [LoggerMessage(
        EventId = 5002,
        Level = LogLevel.Error,
        Message = "Partition maintenance failed. Sending stops when the clock passes the last partition.")]
    private static partial void MaintenanceFailed(ILogger logger, Exception exception);
}
