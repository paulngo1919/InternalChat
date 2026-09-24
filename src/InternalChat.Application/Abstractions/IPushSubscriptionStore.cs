namespace InternalChat.Application.Abstractions;

/// <summary>Persistence for browser push subscriptions (data-model.md, <c>push_subscription</c>).</summary>
/// <remarks>
/// Deliberately not folded into <see cref="IPushSender"/>, which sends and reports delivery outcome
/// and knows nothing about storage. FR-040 ("detect when a device cannot receive notifications")
/// resolves to "does this employee have any live row here", which is a persistence question, not a
/// delivery one.
/// </remarks>
public interface IPushSubscriptionStore
{
    /// <summary>
    /// Registers a subscription, replacing any existing row for the same endpoint.
    /// </summary>
    /// <remarks>
    /// A browser that already holds a subscription and registers again — a page reload, most
    /// plausibly — presents the same endpoint with possibly-rotated keys. Upserting on
    /// <c>endpoint</c> is what keeps that a refresh rather than a second, stale row alongside it.
    /// </remarks>
    Task RegisterAsync(
        Guid employeeId,
        Uri endpoint,
        string p256dh,
        string auth,
        string? userAgent,
        CancellationToken cancellationToken = default);

    /// <summary>Every live subscription for one employee.</summary>
    Task<IReadOnlyList<PushSubscriptionDescriptor>> ListForEmployeeAsync(
        Guid employeeId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether the employee holds at least one subscription — what FR-040 detects.
    /// </summary>
    Task<bool> HasAnyAsync(Guid employeeId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every employee id holding at least one subscription — the digest job's sweep set (FR-038).
    /// </summary>
    /// <remarks>
    /// Bounded by how many employees have push installed, not by the platform's whole roster —
    /// most of the 10,000 will never appear here, and the sweep has no reason to consider them.
    /// </remarks>
    Task<IReadOnlyList<Guid>> ListEmployeeIdsWithSubscriptionsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a subscription the push service reported as permanently gone.
    /// </summary>
    /// <remarks>
    /// data-model.md: "a subscription rejected by the push service is deleted, not retried
    /// indefinitely" — a dead endpoint kept around would fail again on every future message,
    /// forever, for no benefit.
    /// </remarks>
    Task RemoveAsync(Guid subscriptionId, CancellationToken cancellationToken = default);

    /// <summary>Records a successful delivery, for the device list a future endpoint might show.</summary>
    Task MarkDeliveredAsync(
        Guid subscriptionId,
        DateTimeOffset at,
        CancellationToken cancellationToken = default);
}
