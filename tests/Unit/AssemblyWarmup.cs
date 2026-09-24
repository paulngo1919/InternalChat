using System.Runtime.CompilerServices;
using InternalChat.Application.Abstractions;
using InternalChat.Application.Authorization;
using InternalChat.Domain.Common;
using InternalChat.Domain.Conversations;
using InternalChat.Domain.Employees;
using InternalChat.Domain.Messages;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
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
        WarmSubstitutes();
        WarmDomain();
        WarmSerialization();
        WarmAuthorizationPipeline();
    }

    /// <summary>
    /// Exercises NSubstitute end to end, not just the proxy generation.
    /// </summary>
    /// <remarks>
    /// Creating the first substitute generates a dynamic proxy assembly, and that was originally the
    /// whole of this warm-up. It was not enough: <c>Arg.Any</c>, <c>Returns</c>, and <c>Throws</c>
    /// each route through call-specification machinery that JITs on ITS first use, so the first test
    /// to <em>configure</em> a substitute paid roughly 130 ms and failed the budget — a different
    /// test each run, depending on ordering.
    /// </remarks>
    private static void WarmSubstitutes()
    {
        IMembershipReader reader = Substitute.For<IMembershipReader>();

        Guid conversationId = Guid.CreateVersion7();
        Guid employeeId = Guid.CreateVersion7();

        reader.FindGrantingAsync(conversationId, employeeId, Arg.Any<CancellationToken>())
            .Returns(new MembershipSnapshot(conversationId, employeeId, MembershipRole.Member, 0));

        // Awaiting it warms the async state machine and the returned-task path too. Blocking here is
        // safe and deliberate: this runs at module load, before any test exists to be starved.
        _ = reader.FindGrantingAsync(conversationId, employeeId, CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        // The throwing configuration is a separate code path, and the tests that matter most —
        // "a reader failure refuses rather than allowing" — all use it.
        IMembershipReader failing = Substitute.For<IMembershipReader>();
        failing.FindGrantingAsync(conversationId, employeeId, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("warmup"));

        try
        {
            _ = failing.FindGrantingAsync(conversationId, employeeId, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
        }
        catch (InvalidOperationException)
        {
            // Expected. The point is the JIT, not the outcome.
        }
    }

    /// <summary>Runs one real authorization decision, warming the evaluator and its logging path.</summary>
    private static void WarmAuthorizationPipeline()
    {
        IMembershipReader reader = Substitute.For<IMembershipReader>();

        ConversationMembershipEvaluator evaluator = new(
            reader,
            NullLogger<ConversationMembershipEvaluator>.Instance);

        _ = evaluator
            .EvaluateAsync(Guid.CreateVersion7(), new ConversationMembershipRequirement(Guid.CreateVersion7()))
            .GetAwaiter()
            .GetResult();
    }

    /// <summary>
    /// Touches the domain paths whose first call is measurably slower than the rest.
    /// </summary>
    /// <remarks>
    /// Added after the US2 tests were written, because the run immediately following a build failed
    /// two tests on the budget and the three runs after it passed — the signature of JIT cost landing
    /// inside a measured window. In CI every run is a cold run, so this would have been an
    /// intermittent red build blamed on whichever test happened to be first.
    /// </remarks>
    private static void WarmDomain()
    {
        StaticClock clock = new();

        Conversation conversation = Conversation.CreateGroup(
            Guid.CreateVersion7(),
            "warmup",
            Guid.CreateVersion7(),
            HistoryVisibility.FromJoin,
            clock);

        long seq = conversation.AllocateSequence();

        Message message = Message.Send(
            Guid.CreateVersion7(),
            conversation.Id,
            seq,
            Guid.CreateVersion7(),
            ClientMessageKey.New(),
            MessageBody.Create("warmup"),
            clock);

        message.Edit(message.AuthorId, MessageBody.Create("warmup edited"), clock);
        message.Delete(message.AuthorId, clock);

        _ = Employee.Project(Guid.CreateVersion7(), "warmup", "Warm Up", "warm@up.invalid", clock);
        _ = Membership.Join(conversation.Id, Guid.CreateVersion7(), MembershipRole.Member, 0, clock);

        // Reflection over a property's accessors is a first-touch cost of its own, and one test
        // asserts that HistoryVisibility has no setter.
        _ = typeof(Conversation).GetProperty(nameof(Conversation.HistoryVisibility))?.SetMethod;
    }

    /// <summary>
    /// Primes System.Text.Json's reflection-based metadata for a domain event.
    /// </summary>
    /// <remarks>
    /// The first <c>Serialize</c> call for a given type builds and caches a converter. One test
    /// serializes a domain event to prove it carries no message body (FR-056), and it would
    /// otherwise pay that cost inside its measured window.
    /// </remarks>
    private static void WarmSerialization()
    {
        _ = System.Text.Json.JsonSerializer.Serialize(
            new MessageSent(
                Guid.CreateVersion7(),
                DateTimeOffset.UnixEpoch,
                Guid.CreateVersion7(),
                Guid.CreateVersion7(),
                1,
                Guid.CreateVersion7(),
                DateTimeOffset.UnixEpoch,
                []));
    }

    /// <summary>A clock for warm-up only. Not shared with tests, which control their own time.</summary>
    private sealed class StaticClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch;
    }
}
