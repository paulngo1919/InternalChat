using System.Runtime.CompilerServices;
using InternalChat.Application.Abstractions;
using NSubstitute;
using Xunit;

// Timing is the assertion in this assembly, so contention between parallel collections is not
// background noise — it is measurement error that fails honest tests. A suite whose guard is a
// stopwatch has to be the one suite that does not race itself.
//
// The cost is wall-clock: these tests are pure computation and finish in tens of milliseconds
// total, so serialising them is cheap. The integration suite, where wall-clock actually matters,
// keeps its parallelism.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace InternalChat.UnitTests;

/// <summary>
/// Pays one-time runtime costs before any test is measured.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="UnitTestBase"/> fails any test over 100 ms, which is the constitution's way of
/// catching a "unit" test that quietly opened a socket. It measures from construction to disposal,
/// and that window unavoidably includes whatever the runtime happens to JIT on first touch.
/// </para>
/// <para>
/// NSubstitute is the expensive one: creating the first substitute generates a dynamic proxy
/// assembly, which costs a few hundred milliseconds once and microseconds thereafter. Whichever
/// test ran first absorbed that and failed — <b>a different test each run</b>, which is the worst
/// kind of failure because it looks like the test that failed is the broken one.
/// </para>
/// <para>
/// Warming up here moves the cost outside every measured window. The guard then measures the test
/// rather than the runtime, which is what it was always meant to do. This is not a way of dodging
/// the budget: the budget still applies in full to every test body.
/// </para>
/// </remarks>
internal static class AssemblyWarmup
{
    [ModuleInitializer]
    internal static void Warm()
    {
        // Forces Castle DynamicProxy to generate its assembly now. The result is deliberately
        // discarded — only the side effect on the runtime matters.
        _ = Substitute.For<IMembershipReader>();
    }
}
