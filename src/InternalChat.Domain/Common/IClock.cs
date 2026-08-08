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
    public DateTimeOffset UtcNow => _timeProvider.GetUtcNow();
}
