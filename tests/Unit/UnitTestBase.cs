using System.Diagnostics;
using InternalChat.Domain.Common;

namespace InternalChat.UnitTests;

/// <summary>
/// Base class for unit tests. Fails any test that exceeds the time budget.
/// </summary>
/// <remarks>
/// <para>
/// Constitution Principle III: "Unit tests MUST NOT touch PostgreSQL, Redis, RabbitMQ, the
/// network, the clock, or the file system. [...] Tests taking longer than 100 ms each are
/// integration tests and belong in the integration suite."
/// </para>
/// <para>
/// Time is the only one of those that can be checked mechanically, and it happens to be a good
/// proxy: a unit test that quietly opened a socket or hit the file system will blow the budget.
/// It is a smoke alarm rather than a lock — but a smoke alarm that actually fails the build.
/// </para>
/// <para>
/// <b>Caveat worth knowing:</b> when a test both fails an assertion and exceeds the budget,
/// xUnit reports both. The assertion failure is the real one; the budget message is noise in
/// that case. It is not suppressed because knowing which test outcome came first is not possible
/// from <see cref="Dispose"/>, and losing a genuine budget breach would be worse than an extra
/// line of output.
/// </para>
/// </remarks>
public abstract class UnitTestBase : IDisposable
{
    /// <summary>
    /// Set once the first test in the process has been measured.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The first test in a run is exempt, and only the first.</b> Whichever test xUnit invokes
    /// first pays for JIT-compiling xUnit's own reflection-based invocation path and for tiered
    /// compilation of everything it touches — several hundred milliseconds that belong to the
    /// runtime, not to the test. <see cref="AssemblyWarmup"/> pre-pays what a module initializer
    /// can reach; the test framework's invoker is not reachable from there.
    /// </para>
    /// <para>
    /// Leaving it unexempt produced exactly the failure this guard is meant to prevent people from
    /// ignoring: a red build on the first run after any build, blaming a different trivial test each
    /// time. A guard that cries wolf is a guard someone deletes.
    /// </para>
    /// <para>
    /// The exemption is narrow on purpose. It covers one test per process, and a genuinely slow test
    /// is only ever first occasionally — on every other run it is measured in full and fails. What
    /// is given up is catching a slow test that is <em>always</em> ordered first, which xUnit does
    /// not guarantee for any test.
    /// </para>
    /// </remarks>
    private static int _firstTestMeasured;

    private readonly long _startTimestamp = Stopwatch.GetTimestamp();
    private bool _disposed;

    /// <summary>
    /// Maximum permitted duration. Override only with a comment justifying why the test cannot
    /// meet the constitution's budget — and consider whether it belongs in the integration suite.
    /// </summary>
    protected virtual TimeSpan Budget => TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Whether this test may claim the once-per-process warm-up exemption.
    /// </summary>
    /// <remarks>
    /// True for real tests. <c>UnitTestBudgetGuardTests</c>'s probes override it to <c>false</c>:
    /// they exist to prove the guard fires, and a probe that silently claimed the exemption would
    /// make that proof depend on execution order — the guard test failed exactly that way when the
    /// exemption was first added, on the runs where it happened to be scheduled first.
    /// </remarks>
    protected virtual bool MayClaimWarmupExemption => true;

    /// <summary>
    /// A deterministic clock fixed at a known instant.
    /// </summary>
    /// <remarks>
    /// Principle III forbids unit tests reading the ambient clock. Every entity and use case
    /// takes <see cref="IClock"/>, so tests assert on exact timestamps instead of tolerating a
    /// window — which is what makes the 24-hour edit window and the retention tolerance testable
    /// at all.
    /// </remarks>
    protected static IClock FixedClock(string iso8601 = "2026-08-01T09:00:00Z") =>
        new StubClock(DateTimeOffset.Parse(iso8601, System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>Ends the test and enforces the time budget.</summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Enforces the time budget.</summary>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed || !disposing)
        {
            return;
        }

        _disposed = true;

        TimeSpan elapsed = Stopwatch.GetElapsedTime(_startTimestamp);

        if (!MayClaimWarmupExemption)
        {
            if (elapsed <= Budget)
            {
                return;
            }
        }
        else
        {
            // Claim the exemption whether or not it is needed, so it is spent by the first real
            // test rather than lingering for an unrelated one later in the run. Exchange returns
            // the PREVIOUS value, so this is true exactly once per process.
            bool wasFirst = Interlocked.Exchange(ref _firstTestMeasured, 1) == 0;

            if (elapsed <= Budget || wasFirst)
            {
                return;
            }
        }

        throw new UnitTestBudgetExceededException(
            $"""
            {GetType().Name} took {elapsed.TotalMilliseconds:F0} ms, budget {Budget.TotalMilliseconds:F0} ms.

            Constitution Principle III: a unit test over 100 ms is an integration test in the
            wrong project. Either it is reaching real I/O — a database, the network, the file
            system, the clock — which unit tests may not do, or it belongs in tests/Integration
            where Testcontainers provides the real thing.
            """);
    }

    private sealed class StubClock : IClock
    {
        public StubClock(DateTimeOffset utcNow) => UtcNow = utcNow;

        public DateTimeOffset UtcNow { get; }
    }
}

/// <summary>Thrown when a unit test exceeds its time budget.</summary>
public sealed class UnitTestBudgetExceededException : Exception
{
    /// <summary>Creates the exception.</summary>
    public UnitTestBudgetExceededException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public UnitTestBudgetExceededException()
    {
    }

    /// <summary>Creates the exception.</summary>
    public UnitTestBudgetExceededException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
