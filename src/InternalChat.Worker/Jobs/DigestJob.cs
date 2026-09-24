using InternalChat.Application.Abstractions;
using InternalChat.Domain.Common;
using InternalChat.Domain.Notifications;
using InternalChat.Worker.Notifications;

namespace InternalChat.Worker.Jobs;

/// <summary>
/// T135 — batches a returning employee's backlog into one summary push (FR-038, US4 scenario 5).
/// </summary>
/// <remarks>
/// <para>
/// <b>What "returning" means here.</b> <c>NotificationFanoutConsumer</c> sends the first eligible
/// message immediately and then starts a <see cref="NotificationPreference.DigestAfterMinutes"/>
/// cooldown, suppressing individual pushes for the rest of it — see its remarks. This job is what
/// closes the loop: once that cooldown lapses for an employee who still has unread messages, it
/// sends exactly one summary and starts the cooldown again, rather than leaving them silent
/// forever or resuming one push per message.
/// </para>
/// <para>
/// Sweeps only employees who hold at least one push subscription — <em>Constitution Performance
/// Requirements</em> forbid an unbounded scan, and most of the platform's 10,000 employees will
/// never have installed push at all.
/// </para>
/// </remarks>
public sealed partial class DigestJob : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    private readonly IServiceScopeFactory _scopes;
    private readonly IClock _clock;
    private readonly ILogger<DigestJob> _logger;

    /// <summary>Creates the job.</summary>
    public DigestJob(IServiceScopeFactory scopes, IClock clock, ILogger<DigestJob> logger)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        _scopes = scopes;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc />
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
            catch (Exception exception)
            {
                // Swallowed, matching PartitionMaintenanceJob: one failed sweep must not take the
                // Worker — and with it the outbox dispatcher and every consumer — down with it.
                SweepFailed(_logger, exception);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    private async Task SweepAsync(CancellationToken cancellationToken)
    {
        using IServiceScope scope = _scopes.CreateScope();

        ICacheStore cache = scope.ServiceProvider.GetRequiredService<ICacheStore>();
        IPushSubscriptionStore subscriptions = scope.ServiceProvider.GetRequiredService<IPushSubscriptionStore>();
        IReadStateRepository readStates = scope.ServiceProvider.GetRequiredService<IReadStateRepository>();
        INotificationPreferenceRepository preferences =
            scope.ServiceProvider.GetRequiredService<INotificationPreferenceRepository>();
        IPushSender sender = scope.ServiceProvider.GetRequiredService<IPushSender>();

        IReadOnlyList<Guid> candidates = await subscriptions
            .ListEmployeeIdsWithSubscriptionsAsync(cancellationToken)
            .ConfigureAwait(false);

        int digested = 0;

        foreach (Guid employeeId in candidates)
        {
            if (await cache.GetAsync<string>(NotificationCooldown.KeyFor(employeeId), cancellationToken)
                    .ConfigureAwait(false) is not null)
            {
                // Still within a cooldown — either notified individually a moment ago, or already
                // digested this same backlog on an earlier sweep.
                continue;
            }

            UnreadSummary summary = await readStates.SummariseUnreadAsync(employeeId, cancellationToken)
                .ConfigureAwait(false);

            if (summary.TotalUnread == 0)
            {
                continue;
            }

            NotificationPreference preference = await preferences.FindAsync(employeeId, cancellationToken)
                .ConfigureAwait(false)
                ?? NotificationPreference.Default(employeeId);

            if (preference.DoNotDisturb.IsActiveAt(_clock.UtcNow))
            {
                continue;
            }

            await SendDigestAsync(employeeId, summary, subscriptions, sender, cancellationToken)
                .ConfigureAwait(false);

            await cache
                .SetAsync(
                    NotificationCooldown.KeyFor(employeeId),
                    "1",
                    TimeSpan.FromMinutes(preference.DigestAfterMinutes),
                    cancellationToken)
                .ConfigureAwait(false);

            digested++;
        }

        SweepCompleted(_logger, candidates.Count, digested);
    }

    private static async Task SendDigestAsync(
        Guid employeeId,
        UnreadSummary summary,
        IPushSubscriptionStore subscriptions,
        IPushSender sender,
        CancellationToken cancellationToken)
    {
        PushPayload payload = new(
            Title: "Unread messages",
            Body: summary.ConversationCount == 1
                ? $"You have {summary.TotalUnread} unread message(s) in one conversation."
                : $"You have {summary.TotalUnread} unread message(s) across {summary.ConversationCount} conversations.",
            DeepLink: "/");

        IReadOnlyList<PushSubscriptionDescriptor> descriptors = await subscriptions
            .ListForEmployeeAsync(employeeId, cancellationToken)
            .ConfigureAwait(false);

        foreach (PushSubscriptionDescriptor descriptor in descriptors)
        {
            PushDeliveryResult result = await sender.SendAsync(descriptor, payload, cancellationToken)
                .ConfigureAwait(false);

            switch (result)
            {
                case PushDeliveryResult.Delivered:
                    await subscriptions
                        .MarkDeliveredAsync(descriptor.SubscriptionId, DateTimeOffset.UtcNow, cancellationToken)
                        .ConfigureAwait(false);
                    break;

                case PushDeliveryResult.SubscriptionExpired:
                    await subscriptions.RemoveAsync(descriptor.SubscriptionId, cancellationToken)
                        .ConfigureAwait(false);
                    break;

                case PushDeliveryResult.TransientFailure:
                    break;
            }
        }
    }

    [LoggerMessage(
        EventId = 5200,
        Level = LogLevel.Debug,
        Message = "Digest sweep considered {CandidateCount} employee(s) with push subscriptions, digested {DigestedCount}")]
    private static partial void SweepCompleted(ILogger logger, int candidateCount, int digestedCount);

    [LoggerMessage(EventId = 5201, Level = LogLevel.Error, Message = "Digest sweep failed")]
    private static partial void SweepFailed(ILogger logger, Exception exception);
}
