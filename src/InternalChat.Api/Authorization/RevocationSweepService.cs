using InternalChat.Application.Abstractions;
using Microsoft.Extensions.Options;

namespace InternalChat.Api.Authorization;

/// <summary>
/// Closes open hub connections whose access has ended (FR-003, SC-018).
/// </summary>
/// <remarks>
/// <para>
/// This is the piece quickstart.md warns about: "An open connection that outlives its token is the
/// default behaviour, not the exception." A hub filter catches a revoked employee the moment they
/// invoke something — but an employee whose client is simply receiving messages invokes nothing,
/// and a filter never fires for them. Without a timer, deactivating that employee would end their
/// HTTP access immediately and leave the socket delivering messages indefinitely.
/// </para>
/// <para>
/// <b>Cost.</b> One Redis existence check per open connection per sweep. At 7,000 connections and a
/// thirty-second interval that is roughly 230 lookups a second against an instance already serving
/// the membership cache — small, and it is why the interval can be an order of magnitude tighter
/// than the five minutes FR-003 allows.
/// </para>
/// <para>
/// <b>A sweep failure must not end the sweeper.</b> An unhandled exception in a
/// <see cref="BackgroundService"/> stops it silently for the life of the process, and the symptom
/// would be revocation quietly ceasing to work while every other part of the platform looked
/// healthy. Each pass is therefore isolated.
/// </para>
/// </remarks>
public sealed partial class RevocationSweepService : BackgroundService
{
    private readonly HubConnectionRegistry _registry;
    private readonly IRevocationStore _revocations;
    private readonly ChatAuthenticationOptions _options;
    private readonly ILogger<RevocationSweepService> _logger;

    /// <summary>Creates the sweeper.</summary>
    public RevocationSweepService(
        HubConnectionRegistry registry,
        IRevocationStore revocations,
        IOptions<ChatAuthenticationOptions> options,
        ILogger<RevocationSweepService> logger)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(revocations);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _registry = registry;
        _revocations = revocations;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(_options.RevocationSweep);

        SweeperStarted(_logger, _options.RevocationSweepSeconds);

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await SweepAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // See the class remarks: one failed pass must not end the sweeper.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                SweepFailed(_logger, ex);
            }
        }
    }

    /// <summary>Runs one pass. Internal so a test can drive it without waiting for a tick.</summary>
    internal async Task<int> SweepAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<OpenConnection> connections = _registry.Snapshot();

        if (connections.Count == 0)
        {
            return 0;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        int closed = 0;

        foreach (OpenConnection connection in connections)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Expiry first: it is a local comparison, so an expired connection costs no Redis
            // round trip at all.
            bool ended = connection.ExpiresAt <= now
                || await _revocations
                    .IsRevokedAsync(connection.Subject, connection.SessionId, cancellationToken)
                    .ConfigureAwait(false);

            if (!ended)
            {
                continue;
            }

            ConnectionClosed(_logger, connection.Subject, connection.ConnectionId);

            // Abort triggers OnDisconnectedAsync, which deregisters. Removing here as well would
            // be harmless but would hide a filter that had stopped being invoked.
            connection.Context.Abort();
            closed++;
        }

        return closed;
    }

    [LoggerMessage(
        EventId = 3300,
        Level = LogLevel.Information,
        Message = "Revocation sweep started; open connections are re-checked every {IntervalSeconds}s")]
    private static partial void SweeperStarted(ILogger logger, int intervalSeconds);

    [LoggerMessage(
        EventId = 3301,
        Level = LogLevel.Information,
        Message = "Closed hub connection {ConnectionId} for {Subject}: access has ended")]
    private static partial void ConnectionClosed(ILogger logger, string subject, string connectionId);

    [LoggerMessage(
        EventId = 3302,
        Level = LogLevel.Error,
        Message = "A revocation sweep failed. Open connections were NOT re-checked this pass; "
            + "the next tick will retry.")]
    private static partial void SweepFailed(ILogger logger, Exception exception);
}
