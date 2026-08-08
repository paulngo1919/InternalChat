using System.Reflection;
using NetArchTest.Rules;

namespace InternalChat.ArchitectureTests;

/// <summary>
/// Constitution v1.2.0, Principle I — Clean Architecture &amp; The Dependency Rule.
/// Source-code dependencies point inward only.
/// </summary>
/// <remarks>
/// T016 and T017. These exist so that a violation fails the build rather than depending on a
/// reviewer noticing it. The constitution calls a leaked dependency a build-blocking defect;
/// only a test can actually block a build.
/// </remarks>
public sealed class DependencyRuleTests
{
    /// <summary>
    /// T016 — Domain MUST NOT reference any other project, ASP.NET Core, EF Core, Redis,
    /// RabbitMQ, or any package outside the BCL.
    /// </summary>
    [Fact]
    public void Domain_depends_on_nothing_but_the_base_class_library()
    {
        string[] forbidden =
        [
            ArchitectureAssemblies.ApplicationNamespace,
            ArchitectureAssemblies.InfrastructureNamespace,
            ArchitectureAssemblies.ApiNamespace,
            ArchitectureAssemblies.WorkerNamespace,
            "Microsoft.AspNetCore",
            "Microsoft.EntityFrameworkCore",
            "Microsoft.Extensions.DependencyInjection",
            "Npgsql",
            "StackExchange.Redis",
            "RabbitMQ",
            "Minio",
        ];

        AssertNoDependency(ArchitectureAssemblies.Domain, forbidden);
    }

    /// <summary>
    /// T016 (assembly level) — the strongest form of the rule. Domain's compiled references
    /// must contain nothing but the framework itself, which catches a package added directly
    /// to the csproj even when no code uses it yet.
    /// </summary>
    [Fact]
    public void Domain_assembly_references_only_framework_assemblies()
    {
        string[] allowedPrefixes = ["System", "netstandard", "mscorlib", "Microsoft.CSharp"];

        string[] offenders = ArchitectureAssemblies.Domain
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(name => !allowedPrefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal)))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "Domain must reference only the BCL (Constitution Principle I). " +
            $"Offending assembly references: {string.Join(", ", offenders)}");
    }

    /// <summary>
    /// T017 — Application depends on Domain only. It owns the interfaces for outbound concerns;
    /// Infrastructure implements them and is never referenced from here.
    /// </summary>
    [Fact]
    public void Application_never_references_Infrastructure_or_Presentation()
    {
        string[] forbidden =
        [
            ArchitectureAssemblies.InfrastructureNamespace,
            ArchitectureAssemblies.ApiNamespace,
            ArchitectureAssemblies.WorkerNamespace,
        ];

        AssertNoDependency(ArchitectureAssemblies.Application, forbidden);
    }

    /// <summary>
    /// T017 (assembly level) — catches an Infrastructure project reference added to
    /// Application's csproj before any code uses it.
    /// </summary>
    [Fact]
    public void Application_assembly_does_not_reference_Infrastructure()
    {
        bool referencesInfrastructure = ArchitectureAssemblies.Application
            .GetReferencedAssemblies()
            .Any(a => string.Equals(
                a.Name,
                ArchitectureAssemblies.InfrastructureNamespace,
                StringComparison.Ordinal));

        Assert.False(
            referencesInfrastructure,
            "Application must not reference Infrastructure (Constitution Principle I). " +
            "Outbound concerns belong behind an interface in Application/Abstractions, " +
            "implemented by Infrastructure and wired at the composition root.");
    }

    /// <summary>
    /// T017 — Application must not take a dependency on a persistence, transport, or hosting
    /// technology. An interface that leaks EF Core or ASP.NET Core types is not an abstraction.
    /// </summary>
    [Fact]
    public void Application_is_free_of_infrastructure_technologies()
    {
        string[] forbidden =
        [
            "Microsoft.AspNetCore",
            "Microsoft.EntityFrameworkCore",
            "Npgsql",
            "StackExchange.Redis",
            "RabbitMQ",
            "Minio",
        ];

        AssertNoDependency(ArchitectureAssemblies.Application, forbidden);
    }

    /// <summary>
    /// Infrastructure implements Application's interfaces; it must never reach back into a
    /// presentation host.
    /// </summary>
    [Fact]
    public void Infrastructure_never_references_a_presentation_host()
    {
        string[] forbidden =
        [
            ArchitectureAssemblies.ApiNamespace,
            ArchitectureAssemblies.WorkerNamespace,
        ];

        AssertNoDependency(ArchitectureAssemblies.Infrastructure, forbidden);
    }

    private static void AssertNoDependency(Assembly assembly, string[] forbiddenNamespaces)
    {
        // NetArchTest returns a vacuous success when an assembly contains no types, which is a
        // legitimate state early in the build. The assembly-level tests above cover that gap,
        // so this stays a type-level check without a non-empty precondition.
        TestResult result = Types.InAssembly(assembly)
            .ShouldNot()
            .HaveDependencyOnAny(forbiddenNamespaces)
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(assembly, forbiddenNamespaces, result));
    }

    private static string Describe(Assembly assembly, string[] forbidden, TestResult result)
    {
        IEnumerable<string> failing = result.FailingTypeNames ?? [];

        return $"""
            {assembly.GetName().Name} must not depend on: {string.Join(", ", forbidden)}

            Constitution Principle I — source-code dependencies point inward only.

            Offending types:
              {string.Join("\n  ", failing)}
            """;
    }
}
