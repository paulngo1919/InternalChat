using System.Text.Json;
using InternalChat.Api.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace InternalChat.ContractTests;

/// <summary>
/// T110 — the membership endpoints against <c>contracts/openapi.yaml</c> (US3).
/// </summary>
/// <remarks>
/// Same shape as <see cref="MessagingContractTests"/>: the routing table is checked so a generated
/// client does not call a 404, and the <c>Member</c> DTO is checked against every property the
/// contract documents on it — that schema declares no <c>required</c> list, so this uses the
/// "every documented property is served" style rather than <c>MessagingContractTests</c>'s
/// required-property style.
/// </remarks>
public sealed class MembershipContractTests : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly WebApplicationFactory<Program> _factory;
    private readonly OpenApiContract _contract = OpenApiContract.Load();

    public MembershipContractTests(WebApplicationFactory<Program> factory) => _factory = factory;

    /// <summary>The membership operations this task covers.</summary>
    public static TheoryData<string, string> DocumentedOperations() => new()
    {
        { "/conversations/{conversationId}/members", "GET" },
        { "/conversations/{conversationId}/members", "POST" },
        { "/conversations/{conversationId}/members/{employeeId}", "DELETE" },
    };

    [Theory]
    [MemberData(nameof(DocumentedOperations))]
    public void Every_documented_membership_operation_is_mapped(string path, string method)
    {
        Assert.Contains(path, _contract.Paths);
        Assert.Contains(method, _contract.MethodsFor(path));

        string expected = _contract.BasePath + path;

        bool mapped = Endpoints().Any(endpoint =>
            string.Equals(RoutePatterns.Of(endpoint), expected, StringComparison.OrdinalIgnoreCase)
            && RoutePatterns.MethodsOf(endpoint).Contains(method, StringComparer.OrdinalIgnoreCase));

        Assert.True(
            mapped,
            $"""
            The contract documents {method} {expected}, and no endpoint serves it.

            Mapped routes:
              {string.Join("\n  ", Endpoints().Select(e => $"{string.Join('|', RoutePatterns.MethodsOf(e))} {RoutePatterns.Of(e)}").Order())}
            """);
    }

    /// <summary>
    /// Every property the contract documents on <c>Member</c> is actually served.
    /// </summary>
    [Fact]
    public void Member_serves_every_documented_property()
    {
        IReadOnlyCollection<string> documented = _contract.PropertiesOf("Member");
        IReadOnlyCollection<string> serialized = SerializedPropertyNames();

        Assert.NotEmpty(documented);

        List<string> missing = [.. documented.Where(d => !serialized.Contains(d, StringComparer.Ordinal))];

        Assert.True(
            missing.Count == 0,
            $"Member does not serialize {string.Join(", ", missing)}. Serialized: {string.Join(", ", serialized.Order())}");
    }

    /// <summary>
    /// <c>employee</c> is a nested <c>EmployeeSummary</c>, not a bare id.
    /// </summary>
    /// <remarks>
    /// A member list exists so a group's members can be recognised by name, not by guid — a flat
    /// employee id would make every caller round-trip to the directory to render a list this
    /// endpoint could have returned complete.
    /// </remarks>
    [Fact]
    public void Member_nests_the_employee_summary()
    {
        JsonElement employee = SerializedSample().GetProperty("employee");

        Assert.Equal(JsonValueKind.Object, employee.ValueKind);
        Assert.True(employee.TryGetProperty("displayName", out _));
        Assert.True(employee.TryGetProperty("status", out _));
    }

    private static IReadOnlyCollection<string> SerializedPropertyNames() =>
        [.. SerializedSample().EnumerateObject().Select(p => p.Name)];

    private static JsonElement SerializedSample()
    {
        MemberResponse sample = new(
            new EmployeeSummaryResponse(
                Guid.CreateVersion7(),
                "Sample Person",
                "sample@internalchat.local",
                AvatarUrl: null,
                "active"),
            MembershipRoles.Member,
            DateTimeOffset.UtcNow);

        using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(sample, SerializerOptions));

        return document.RootElement.Clone();
    }

    private IEnumerable<Endpoint> Endpoints() =>
        _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints;
}
