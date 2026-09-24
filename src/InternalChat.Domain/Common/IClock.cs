namespace InternalChat.Domain.Common;

/// <summary>
/// The only permitted source of the current time.
/// </summary>
/// <remarks>
/// <para>
/// Constitution Principle III: unit tests MUST NOT touch the clock — time is injected. Reading
/// <c>DateTimeOffset.UtcNow</c> anywhere in Domain or Application makes the code untestable
/// without sleeping, and a test that sleeps cannot meet the 100 ms budget.
/// </para>
/// <para>
/// It also matters for correctness, not just testability. FR-012 requires message ordering to be
/// independent of any client device's clock, and SC-025 requires retention deletion to land
/// inside a stated tolerance. Both need a single, substitutable definition of "now" so the
/// behaviour can be asserted rather than hoped for.
/// </para>
/// </remarks>
public interface IClock
{
    /// <summary>Current UTC time.</summary>
    DateTimeOffset UtcNow { get; }
}

/// <summary>
/// The resolution every timestamp in this platform is held at.
/// </summary>
/// <remarks>
/// <para>
/// PostgreSQL's <c>timestamptz</c> stores microseconds. .NET's <see cref="DateTimeOffset"/> carries
/// 100-nanosecond ticks. So a timestamp that has been through the database is not equal to the one
/// the application created, and the difference is invisible in every debugger and every log line —
/// they render identically at microsecond precision.
/// </para>
/// <para>
/// <b>Why that is a correctness problem and not a cosmetic one.</b> <c>message</c> is partitioned by
/// <c>sent_at</c>, so its primary key is <c>(id, sent_at)</c>, and every event carries
/// <c>SentAt</c> precisely so a consumer can find the row without scanning twelve months of
/// partitions. Those lookups are equality filters. An in-memory value one tick off the stored one
/// matches <em>nothing</em>, and the real-time fan-out consumer would silently deliver no message
/// while reporting success. It surfaced as a one-tick assertion failure in
/// <c>IdempotentSendTests</c>, which compares the timestamp returned by a send against the one
/// returned by its replay — the first from memory, the second read back from PostgreSQL.
/// </para>
/// <para>
/// Truncating at the source is what makes the two identical by construction. Rounding in each
/// comparison instead would mean every future equality check had to remember to do it.
/// </para>
/// </remarks>
public static class ClockResolution
{
    /// <summary>Ticks per microsecond — the store's resolution.</summary>
    public const long TicksPerMicrosecond = TimeSpan.TicksPerMillisecond / 1000;

    /// <summary>
    /// Truncates to the resolution PostgreSQL will store.
    /// </summary>
    /// <remarks>
    /// Truncation rather than rounding, so a timestamp never moves forward past an instant that has
    /// already been observed — a rounded-up <c>sent_at</c> could sort after a message that was
    /// genuinely sent later.
    /// </remarks>
    public static DateTimeOffset Truncate(DateTimeOffset value) =>
        new(value.Ticks - (value.Ticks % TicksPerMicrosecond), value.Offset);
}

/// <summary>
/// Adapts the BCL <see cref="TimeProvider"/> to <see cref="IClock"/>.
/// </summary>
/// <remarks>
/// <para>
/// Lives in Domain rather than Infrastructure because <see cref="TimeProvider"/> is part of the
/// base class library, so this introduces no dependency Principle I forbids. Keeping it here
/// means Domain, Application, and their unit tests share one definition of time instead of each
/// layer growing its own.
/// </para>
/// <para>
/// Tests substitute <see cref="TimeProvider"/> directly — <c>FakeTimeProvider</c> from
/// <c>Microsoft.Extensions.TimeProvider.Testing</c>, or any stub implementing
/// <see cref="IClock"/>.
/// </para>
/// </remarks>
public sealed class SystemClock : IClock
{
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates a clock over the supplied time provider.</summary>
    public SystemClock(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
    }

    /// <summary>Creates a clock over the system time provider.</summary>
    public SystemClock()
        : this(TimeProvider.System)
    {
    }

    /// <inheritdoc />
    /// <remarks>
    /// Truncated to the store's resolution. See <see cref="ClockResolution"/> for why a timestamp
    /// the database cannot represent exactly is a correctness problem rather than a rounding detail.
    /// </remarks>
    public DateTimeOffset UtcNow => ClockResolution.Truncate(_timeProvider.GetUtcNow());
}
