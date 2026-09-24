using InternalChat.Application.Abstractions;
using InternalChat.Domain.Notifications;
using Microsoft.EntityFrameworkCore;

namespace InternalChat.Infrastructure.Persistence.Repositories;

/// <summary>EF Core implementation of <see cref="INotificationPreferenceRepository"/> (T130).</summary>
public sealed class NotificationPreferenceRepository : INotificationPreferenceRepository
{
    private readonly ChatDbContext _context;

    /// <summary>Creates the repository.</summary>
    public NotificationPreferenceRepository(ChatDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
    }

    /// <inheritdoc />
    public async Task<NotificationPreference?> FindAsync(
        Guid employeeId,
        CancellationToken cancellationToken = default) =>
        await _context.NotificationPreferences
            .FirstOrDefaultAsync(p => p.EmployeeId == employeeId, cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task AddAsync(NotificationPreference preference, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preference);
        await _context.NotificationPreferences.AddAsync(preference, cancellationToken).ConfigureAwait(false);
    }
}
