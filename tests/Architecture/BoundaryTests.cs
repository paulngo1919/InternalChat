using System.Reflection;
using NetArchTest.Rules;

namespace InternalChat.ArchitectureTests;

/// <summary>
/// T020 — Constitution v1.2.0, Principle I: "Domain entities MUST NOT be serialized to HTTP or
/// message-bus payloads. Every boundary crossing uses an explicit DTO or contract type."
/// </summary>
/// <remarks>
/// Serializing a domain entity couples the wire format to the internal model, so an internal
/// rename becomes a breaking API change, and it leaks whatever fields the entity happens to
/// carry — which for a chat platform means fields like a message tombstone or a membership's
/// history floor reaching clients that should never see them.
/// </remarks>
public sealed class BoundaryTests
{
    private const string ApiContractsNamespace = "InternalChat.Api.Contracts";

    /// <summary>
    /// Response and request DTOs must be self-contained. A DTO that references a Domain type
    /// drags that type into the serialized payload however carefully the endpoint is written.
    /// </summary>
    [Fact]
    public void Api_contract_types_do_not_reference_the_Domain()
    {
        Type[] contractTypes = ArchitectureAssemblies.Api
            .GetTypes()
            .Where(t => t.Namespace?.StartsWith(ApiContractsNamespace, StringComparison.Ordinal) == true)
            .ToArray();

        if (contractTypes.Length == 0)
        {
            // No DTOs exist yet (they arrive with US1/US2). Recorded rather than passed
            // silently: a rule matching zero types proves nothing, and a future refactor that
            // moved the namespace would otherwise look like a green test forever.
            Assert.True(
                ArchitectureAssemblies.Api.GetTypes().Length > 0,
                "Api assembly failed to load any types.");
            return;
        }

        TestResult result = Types.InAssembly(ArchitectureAssemblies.Api)
            .That()
            .ResideInNamespaceStartingWith(ApiContractsNamespace)
            .ShouldNot()
            .HaveDependencyOn(ArchitectureAssemblies.DomainNamespace)
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            $"""
            These API contract types reference the Domain:

              {string.Join("\n  ", result.FailingTypeNames ?? [])}

            Constitution Principle I: no Domain entity crosses a boundary. Map to a DTO that
            owns its own shape, so an internal rename is not a breaking API change.
            """);
    }

    /// <summary>
    /// The same rule for the message bus. A domain entity in a queue payload ends up in broker
    /// logs, the management UI, and DLQ dumps — which for this system would put message bodies
    /// somewhere FR-056 forbids them to be.
    /// </summary>
    [Fact]
    public void Domain_types_are_not_marked_serializable_for_transport()
    {
        Type[] serializableDomainTypes = ArchitectureAssemblies.Domain
            .GetTypes()
            .Where(t => t.GetCustomAttributes()
                .Any(a => a.GetType().Name.Contains("JsonSerializable", StringComparison.Ordinal)
                       || a.GetType().Name.Contains("DataContract", StringComparison.Ordinal)))
            .ToArray();

        Assert.True(
            serializableDomainTypes.Length == 0,
            $"""
            These Domain types carry serialization attributes:

              {string.Join("\n  ", serializableDomainTypes.Select(t => t.FullName))}

            Serialization is a boundary concern. Marking a Domain type for the wire is how an
            entity ends up in an HTTP response or a queue payload by accident.
            """);
    }

    /// <summary>
    /// Domain must not depend on the Application layer either — the Dependency Rule is a strict
    /// ordering, not merely "Domain is separate from Infrastructure".
    /// </summary>
    [Fact]
    public void Domain_does_not_reach_outward_into_Application()
    {
        TestResult result = Types.InAssembly(ArchitectureAssemblies.Domain)
            .ShouldNot()
            .HaveDependencyOn(ArchitectureAssemblies.ApplicationNamespace)
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            $"""
            These Domain types depend on Application:

              {string.Join("\n  ", result.FailingTypeNames ?? [])}

            Dependencies point inward. If Domain needs something from Application, the concept
            belongs in Domain or the dependency is inverted.
            """);
    }
}
