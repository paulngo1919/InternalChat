using System.Globalization;
using InternalChat.Domain.Common;

namespace InternalChat.UnitTests;

/// <summary>
/// A clock the test controls completely.
/// </summary>
/// <remarks>
/// <para>
/// Constitution Principle III forbids unit tests reading the ambient clock. That is usually stated
/// as a speed rule, but the sharper reason is that several invariants in this system are *about*
/// time — the 24-hour edit window, the history floor, the retention tolerance — and none of them
/// can be asserted exactly against a clock that moves on its own.
/// </para>
/// <para>
/// <see cref="Advance"/> is what makes "23 hours later" a line of code rather than a sleep. A test
/// that slept for the real duration would take a day and would be deleted by whoever ran the suite
/// next.
/// </para>
/// </remarks>
public sealed class TestClock : IClock
{
    /// <summary>Creates a clock frozen at the given instant.</summary>
    public TestClock(DateTimeOffset utcNow) => UtcNow = utcNow;

    /// <summary>Creates a clock frozen at an ISO 8601 instant.</summary>
    public TestClock(string iso8601 = "2026-08-01T09:00:00Z")
        : this(DateTimeOffset.Parse(iso8601, CultureInfo.InvariantCulture))
    {
    }

    /// <inheritdoc />
    public DateTimeOffset UtcNow { get; private set; }

    /// <summary>Moves time forward. Never backwards — a clock that goes back is not a clock.</summary>
    public TestClock Advance(TimeSpan amount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(amount, TimeSpan.Zero);

        UtcNow += amount;
        return this;
    }
}
