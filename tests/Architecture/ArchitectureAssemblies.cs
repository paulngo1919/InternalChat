using System.Reflection;

namespace InternalChat.ArchitectureTests;

/// <summary>
/// Assembly handles for the architecture rules.
/// </summary>
/// <remarks>
/// Loaded by name rather than through a marker type, because the Domain assembly legitimately
/// contains no types early in the build and <c>typeof(T).Assembly</c> would not compile.
///
/// <para>
/// The load is guarded. A mistyped assembly name would otherwise produce a rule that matches
/// zero types and passes — the worst possible outcome for a guardrail, since it reports success
/// while enforcing nothing.
/// </para>
/// </remarks>
internal static class ArchitectureAssemblies
{
    internal const string DomainNamespace = "InternalChat.Domain";
    internal const string ApplicationNamespace = "InternalChat.Application";
    internal const string InfrastructureNamespace = "InternalChat.Infrastructure";
    internal const string ApiNamespace = "InternalChat.Api";
    internal const string WorkerNamespace = "InternalChat.Worker";

    internal static Assembly Domain { get; } = Load(DomainNamespace);

    internal static Assembly Application { get; } = Load(ApplicationNamespace);

    internal static Assembly Infrastructure { get; } = Load(InfrastructureNamespace);

    internal static Assembly Api { get; } = Load(ApiNamespace);

    internal static Assembly Worker { get; } = Load(WorkerNamespace);

    private static Assembly Load(string name)
    {
        try
        {
            return Assembly.Load(name);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Could not load '{name}'. Architecture rules cannot run against an assembly " +
                "that is not present, and silently skipping them would leave the Dependency " +
                "Rule unenforced. Check the project reference in " +
                "tests/Architecture/InternalChat.ArchitectureTests.csproj.",
                ex);
        }
    }
}
