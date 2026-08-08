using InternalChat.Domain.Common;

namespace InternalChat.Application.Abstractions;

/// <summary>
/// Publishes domain events for cross-boundary side effects — notifications, search indexing,
/// attachment scanning, audit, analytics.
/// </summary>
/// <remarks>
/// <para>
/// This does NOT send to RabbitMQ. It writes to the transactional outbox in the ambient
/// database transaction; a dispatcher in the Worker publishes from there with publisher
/// confirms (Constitution Principle VI, research.md D2).
/// </para>
/// <para>
/// The distinction is the whole point. A direct publish creates a dual write: a crash between
/// commit and publish silently loses a notification, and a publish before commit can announce a
/// message that never existed. Neither failure is visible in testing, and both are permanent.
/// </para>
/// </remarks>
public interface IEventPublisher
{
    /// <summary>Enqueues one event into the outbox within the ambient transaction.</summary>
    Task PublishAsync(IDomainEvent domainEvent, CancellationToken cancellationToken = default);

    /// <summary>Enqueues several events into the outbox within the ambient transaction.</summary>
    Task PublishAsync(
        IReadOnlyCollection<IDomainEvent> domainEvents,
        CancellationToken cancellationToken = default);
}
