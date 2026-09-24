using InternalChat.Application.Abstractions;
using InternalChat.Infrastructure.Persistence.Notifications;
using Microsoft.EntityFrameworkCore;

namespace InternalChat.Infrastructure.Persistence.Repositories;

/// <summary>EF Core implementation of <see cref="IPushSubscriptionStore"/> (T130).</summary>
public sealed class PushSubscriptionStore : IPushSubscriptionStore
{
    private readonly ChatDbContext _context;
    private readonly Domain.Common.IClock _clock;

    /// <summary>Creates the store.</summary>
    public PushSubscriptionStore(ChatDbContext context, Domain.Common.IClock clock)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(clock);

        _context = context;
        _clock = clock;
    }

    /// <inheritdoc />
    public async Task RegisterAsync(
        Guid employeeId,
        Uri endpoint,
        string p256dh,
        string auth,
        string? userAgent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        string endpointText = endpoint.ToString();

        PushSubscriptionRecord? existing = await _context.PushSubscriptions
            .FirstOrDefaultAsync(s => s.Endpoint == endpointText, cancellationToken)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            // A page reload re-registering the same endpoint, most plausibly. Refreshed in place
            // rather than inserted again — the unique index on endpoint would refuse a second row,
            // and a second row would be the same physical device notified twice regardless.
            existing.P256dh = p256dh;
            existing.Auth = auth;
            existing.UserAgent = userAgent;
            return;
        }

        await _context.PushSubscriptions.AddAsync(
            new PushSubscriptionRecord
            {
                Id = Guid.CreateVersion7(),
                EmployeeId = employeeId,
                Endpoint = endpointText,
                P256dh = p256dh,
                Auth = auth,
                UserAgent = userAgent,
                CreatedAt = _clock.UtcNow,
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PushSubscriptionDescriptor>> ListForEmployeeAsync(
        Guid employeeId,
        CancellationToken cancellationToken = default) =>
        await _context.PushSubscriptions
            .Where(s => s.EmployeeId == employeeId)
            .Select(s => new PushSubscriptionDescriptor(s.Id, new Uri(s.Endpoint), s.P256dh, s.Auth))
            .AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public Task<bool> HasAnyAsync(Guid employeeId, CancellationToken cancellationToken = default) =>
        _context.PushSubscriptions.AnyAsync(s => s.EmployeeId == employeeId, cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<Guid>> ListEmployeeIdsWithSubscriptionsAsync(
        CancellationToken cancellationToken = default) =>
        await _context.PushSubscriptions
            .AsNoTracking()
            .Select(s => s.EmployeeId)
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task RemoveAsync(Guid subscriptionId, CancellationToken cancellationToken = default)
    {
        PushSubscriptionRecord? existing = await _context.PushSubscriptions
            .FirstOrDefaultAsync(s => s.Id == subscriptionId, cancellationToken)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            _context.PushSubscriptions.Remove(existing);
        }
    }

    /// <inheritdoc />
    public async Task MarkDeliveredAsync(
        Guid subscriptionId,
        DateTimeOffset at,
        CancellationToken cancellationToken = default)
    {
        PushSubscriptionRecord? existing = await _context.PushSubscriptions
            .FirstOrDefaultAsync(s => s.Id == subscriptionId, cancellationToken)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            existing.LastSuccessAt = at;
        }
    }
}
