namespace InternalChat.UnitTests;

/// <summary>
/// Verifies the time-budget guard itself.
/// </summary>
/// <remarks>
/// A guardrail that has never been observed failing is unproven. <see cref="UnitTestBase"/>
/// enforces Constitution Principle III's 100 ms budget, so these assert that it actually fires,
/// and equally that it stays quiet for a fast test — a guard that fired on everything would be
/// disabled within a week.
///
/// <para>
/// These do not derive from <see cref="UnitTestBase"/>: the guard under test would otherwise
/// also apply to the test exercising it.
/// </para>
/// </remarks>
public sealed class UnitTestBudgetGuardTests
{
    [Fact]
    public void Guard_fails_a_test_that_exceeds_its_budget()
    {
        ExceedsBudgetProbe probe = new();

        // A real over-budget test blocks on I/O; one millisecond against a zero budget is the
        // same condition without spending a hundred milliseconds proving it.
        Thread.Sleep(2);

        UnitTestBudgetExceededException error =
            Assert.Throws<UnitTestBudgetExceededException>(probe.TriggerDispose);

        Assert.Contains("Constitution Principle III", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Guard_stays_quiet_for_a_test_within_its_budget()
    {
        WithinBudgetProbe probe = new();

        probe.TriggerDispose();
    }

    [Fact]
    public void Guard_is_idempotent_so_a_double_dispose_does_not_throw_twice()
    {
        ExceedsBudgetProbe probe = new();
        Thread.Sleep(2);

        Assert.Throws<UnitTestBudgetExceededException>(probe.TriggerDispose);

        // xUnit may dispose more than once; the second call must be silent rather than
        // reporting a second, confusing failure for the same test.
        probe.TriggerDispose();
    }

    [Fact]
    public void Fixed_clock_is_deterministic()
    {
        global::InternalChat.Domain.Common.IClock clock = ProbeClock.Fixed();

        Assert.Equal(clock.UtcNow, clock.UtcNow);
        Assert.Equal(
            DateTimeOffset.Parse("2026-08-01T09:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            clock.UtcNow);
    }

    // The probes waive the once-per-process warm-up exemption. They are not real tests, and one
    // claiming it would make "the guard fires" depend on whether this class happened to run first —
    // which is precisely how these tests failed when the exemption was introduced.
    private sealed class ExceedsBudgetProbe : UnitTestBase
    {
        protected override TimeSpan Budget => TimeSpan.Zero;

        protected override bool MayClaimWarmupExemption => false;

        public void TriggerDispose() => Dispose();
    }

    private sealed class WithinBudgetProbe : UnitTestBase
    {
        protected override TimeSpan Budget => TimeSpan.FromHours(1);

        protected override bool MayClaimWarmupExemption => false;

        public void TriggerDispose() => Dispose();
    }

    private sealed class ProbeClock : UnitTestBase
    {
        protected override bool MayClaimWarmupExemption => false;

        // Globally qualified: this project now has its own InternalChat.UnitTests.Domain
        // namespace for domain test classes, which otherwise shadows InternalChat.Domain here.
        public static global::InternalChat.Domain.Common.IClock Fixed() => FixedClock();
    }
}
