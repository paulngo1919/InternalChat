using InternalChat.Domain.Notifications;

namespace InternalChat.Application.Abstractions;

/// <summary>Persistence for <see cref="NotificationPreference"/> (FR-037, FR-038).</summary>
public interface INotificationPreferenceRepository
{
    /// <summary>Loads one employee's preferences, tracked, or <c>null</c> when never set.</summary>
    Task<NotificationPreference?> FindAsync(Guid employeeId, CancellationToken cancellationToken = default);

    /// <summary>Stages a first preference row for the current transaction.</summary>
    Task AddAsync(NotificationPreference preference, CancellationToken cancellationToken = default);
}
