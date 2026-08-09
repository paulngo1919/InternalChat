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
    private readonly long _startTimestamp = Stopwatch.GetTimestamp();
    private bool _disposed;

    /// <summary>
    /// Maximum permitted duration. Override only with a comment justifying why the test cannot
    /// meet the constitution's budget — and consider whether it belongs in the integration suite.
    /// </summary>
    protected virtual TimeSpan Budget => TimeSpan.FromMilliseconds(100);

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
        if (elapsed <= Budget)
        {
            return;
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
