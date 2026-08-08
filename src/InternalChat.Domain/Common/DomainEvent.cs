namespace InternalChat.Domain.Common;

/// <summary>
/// Base record for domain events. Carries the identity and timestamp every event needs; derived
/// records add only their own payload.
/// </summary>
/// <remarks>
/// <para>
/// A record, so events are immutable and structurally comparable — an event is a statement about
/// something that already happened and must never be edited after the fact.
/// </para>
/// <para>
/// Payloads deliberately carry identifiers rather than content. Message bodies must not appear
/// in queue payloads, because they would end up in broker logs, the management UI, and DLQ dumps,
/// which FR-056 forbids. Consumers that need the body read it from PostgreSQL.
/// </para>
/// </remarks>
/// <param name="EventId">Identity of this occurrence, and the consumer idempotency key.</param>
/// <param name="OccurredAt">Server time the event occurred.</param>
public abstract record DomainEvent(Guid EventId, DateTimeOffset OccurredAt) : IDomainEvent
{
    /// <summary>
    /// Convenience constructor stamping identity and time from the supplied clock.
    /// </summary>
    /// <remarks>
    /// Time is injected rather than read from <c>DateTimeOffset.UtcNow</c> so that unit tests
    /// stay deterministic and stay inside the 100 ms budget Principle III sets — a test that
    /// reads the ambient clock cannot assert on a timestamp without sleeping or fudging.
    /// </remarks>
    protected DomainEvent(IClock clock)
        : this(Guid.CreateVersion7(), ArgumentNullExceptionGuard(clock).UtcNow)
    {
    }

    /// <inheritdoc />
    public abstract string EventType { get; }

    private static IClock ArgumentNullExceptionGuard(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        return clock;
    }
}
