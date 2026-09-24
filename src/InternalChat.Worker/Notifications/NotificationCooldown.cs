namespace InternalChat.Worker.Notifications;

/// <summary>
/// The Redis key both <see cref="Consumers.NotificationFanoutConsumer"/> and
/// <see cref="Jobs.DigestJob"/> read and write to agree on when an employee was last notified
/// (FR-038).
/// </summary>
/// <remarks>
/// Shared rather than duplicated in each file: both sides only work when they mean exactly the
/// same key for exactly the same employee, and a drift between two independently-formatted
/// strings would fail silently — the fan-out consumer would never see the digest job's cooldown,
/// or the reverse, and nobody would notice until an employee reported being paged twice.
/// </remarks>
internal static class NotificationCooldown
{
    /// <summary>The key for one employee's cooldown marker.</summary>
    public static string KeyFor(Guid employeeId) => $"notify:cooldown:{employeeId}";
}
