using System.Reflection;
using NetArchTest.Rules;

namespace InternalChat.ArchitectureTests;

/// <summary>
/// T018 — Constitution v1.2.0, Principle I: Infrastructure is wired into the process
/// exclusively through dependency injection at the composition root. No other file may
/// construct or name an Infrastructure type directly.
/// </summary>
/// <remarks>
/// The Api and Worker projects DO carry a project reference to Infrastructure — without one,
/// <c>Program.cs</c> could not register it in the container. The reference is therefore not the
/// thing to police; <em>usage</em> is. These tests allow Infrastructure types in the composition
/// root and nowhere else, which is what the constitution actually requires.
/// </remarks>
public sealed class CompositionRootTests
{
    /// <summary>
    /// Types permitted to name Infrastructure. Top-level statements compile to a class called
    /// <c>Program</c>, so that is the composition root's runtime name.
    ///
    /// Adding an entry here widens the blast radius of an infrastructure change across the
    /// whole host, so it needs the same scrutiny as any other architecture deviation.
    /// </summary>
    private static readonly string[] CompositionRootTypeNames = ["Program"];

    [Fact]
    public void Api_names_Infrastructure_only_in_the_composition_root()
    {
        AssertInfrastructureConfinedToCompositionRoot(ArchitectureAssemblies.Api);
    }

    [Fact]
    public void Worker_names_Infrastructure_only_in_the_composition_root()
    {
        AssertInfrastructureConfinedToCompositionRoot(ArchitectureAssemblies.Worker);
    }

    /// <summary>
    /// A presentation host must not reach past Application into a persistence technology.
    /// Seeing EF Core in an endpoint means a query was written where a use case belongs.
    /// </summary>
    [Theory]
    [InlineData("Microsoft.EntityFrameworkCore")]
    [InlineData("Npgsql")]
    [InlineData("StackExchange.Redis")]
    [InlineData("RabbitMQ")]
    [InlineData("Minio")]
    public void Api_does_not_use_persistence_or_transport_technologies_directly(string technology)
    {
        TestResult result = NonCompositionRootTypes(ArchitectureAssemblies.Api)
            .ShouldNot()
            .HaveDependencyOn(technology)
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            $"""
            InternalChat.Api must not use {technology} directly.

            Business rules and data access belong in Application and Infrastructure
            (Constitution Principle I). An endpoint that queries the database directly has
            skipped the use-case layer.

            Offending types:
              {string.Join("\n  ", result.FailingTypeNames ?? [])}
            """);
    }

    private static void AssertInfrastructureConfinedToCompositionRoot(Assembly assembly)
    {
        TestResult result = NonCompositionRootTypes(assembly)
            .ShouldNot()
            .HaveDependencyOn(ArchitectureAssemblies.InfrastructureNamespace)
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            $"""
            {assembly.GetName().Name} may name Infrastructure types only in its composition
            root ({string.Join(", ", CompositionRootTypeNames)}).

            Constitution Principle I: Infrastructure is wired in exclusively through dependency
            injection at the composition root. Depend on the Application-layer interface instead
            and let Program.cs choose the implementation.

            Offending types:
              {string.Join("\n  ", result.FailingTypeNames ?? [])}
            """);
    }

    private static PredicateList NonCompositionRootTypes(Assembly assembly)
    {
        // Compiler-generated closures and iterator state machines are nested inside the types
        // that own them and carry '<' or '>' in their names. They are not separately authored
        // code, so judging them would report failures nobody can act on.
        return Types.InAssembly(assembly)
            .That()
            .DoNotHaveNameMatching(@".*[<>].*")
            .And()
            .DoNotHaveName(CompositionRootTypeNames);
    }
}
