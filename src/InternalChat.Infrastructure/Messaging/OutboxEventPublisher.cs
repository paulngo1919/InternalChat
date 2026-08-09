using System.Diagnostics;
using System.Text.Json;
using InternalChat.Application.Abstractions;
using InternalChat.Domain.Common;
using InternalChat.Infrastructure.Persistence;
using InternalChat.Infrastructure.Persistence.Outbox;

namespace InternalChat.Infrastructure.Messaging;

/// <summary>
/// Writes domain events to the transactional outbox.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately does not touch RabbitMQ. It adds a row to <see cref="OutboxMessage"/> through
/// the same <see cref="ChatDbContext"/> the use case is already using, so the event commits in
/// the same transaction as the state change (Constitution Principle VI). The dispatcher in the
/// Worker publishes from there.
/// </para>
/// <para>
/// It also does not call <c>SaveChanges</c>. The transaction behavior in the Application
/// pipeline owns the commit — saving here would create a second, independent commit boundary
/// and reintroduce exactly the dual write the outbox exists to eliminate.
/// </para>
/// </remarks>
public sealed class OutboxEventPublisher : IEventPublisher
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly ChatDbContext _context;

    /// <summary>Creates the publisher over the ambient context.</summary>
    public OutboxEventPublisher(ChatDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
    }

    /// <inheritdoc />
    public Task PublishAsync(IDomainEvent domainEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        return PublishAsync([domainEvent], cancellationToken);
    }

    /// <inheritdoc />
    public async Task PublishAsync(
        IReadOnlyCollection<IDomainEvent> domainEvents,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(domainEvents);

        if (domainEvents.Count == 0)
        {
            return;
        }

        List<OutboxMessage> rows = new(domainEvents.Count);

        foreach (IDomainEvent domainEvent in domainEvents)
        {
            rows.Add(new OutboxMessage
            {
                // The event's own id, not a fresh one. It becomes the AMQP message id and hence
                // the consumer idempotency key — generating a new one here would mean the same
                // logical event could be deduplicated as two different messages.
                Id = domainEvent.EventId,
                Type = domainEvent.EventType,
                RoutingKey = domainEvent.EventType,
                Payload = JsonSerializer.Serialize(domainEvent, domainEvent.GetType(), SerializerOptions),

                // Captured at write time, not at dispatch time. The dispatcher runs in a
                // different process minutes later, where the originating trace is long gone —
                // recording it here is what lets a trace span request → outbox → consumer.
                TraceParent = Activity.Current?.Id,

                OccurredAt = domainEvent.OccurredAt,
                DispatchedAt = null,
                Attempts = 0,
            });
        }

        await _context.OutboxMessages.AddRangeAsync(rows, cancellationToken).ConfigureAwait(false);
    }
}
