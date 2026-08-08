namespace InternalChat.Application.Abstractions;

/// <summary>A browser push endpoint registered by one device.</summary>
/// <param name="SubscriptionId">Our identity for the subscription.</param>
/// <param name="Endpoint">The browser vendor's push endpoint.</param>
/// <param name="P256dh">Client public key.</param>
/// <param name="Auth">Client auth secret.</param>
public sealed record PushSubscriptionDescriptor(Guid SubscriptionId, Uri Endpoint, string P256dh, string Auth);

/// <summary>What the user sees, plus where tapping it lands them.</summary>
/// <param name="Title">Notification title.</param>
/// <param name="Body">
/// Short preview. Callers must respect that opening a notification MUST NOT reveal content the
/// user has since lost access to, or that has been deleted (FR-039).
/// </param>
/// <param name="DeepLink">Relative path to the triggering message.</param>
public sealed record PushPayload(string Title, string Body, string DeepLink);

/// <summary>Result of one delivery attempt.</summary>
public enum PushDeliveryResult
{
    /// <summary>Accepted by the push service.</summary>
    Delivered,

    /// <summary>
    /// The subscription is permanently gone. The caller MUST delete it rather than retry —
    /// a dead subscription retried forever is how a queue fills up quietly.
    /// </summary>
    SubscriptionExpired,

    /// <summary>A transient failure worth retrying.</summary>
    TransientFailure,
}

/// <summary>
/// Sends browser push notifications (VAPID).
/// </summary>
/// <remarks>
/// <para>
/// Browser and desktop only. There is no native mobile path: spec clarification Q3 chose
/// browser notifications precisely because native push would require a paid Apple Developer
/// Program, which Constitution Principle VIII forbids.
/// </para>
/// <para>
/// That choice has a visible consequence. iOS delivers web push only to home-screen-installed
/// web apps, so some employees genuinely cannot be reached. FR-040 requires the platform to say
/// so plainly rather than let them believe they are covered — the interface reports
/// <see cref="PushDeliveryResult.SubscriptionExpired"/> so callers can prune and surface it,
/// instead of silently dropping.
/// </para>
/// </remarks>
public interface IPushSender
{
    /// <summary>Attempts one delivery.</summary>
    Task<PushDeliveryResult> SendAsync(
        PushSubscriptionDescriptor subscription,
        PushPayload payload,
        CancellationToken cancellationToken = default);
}
