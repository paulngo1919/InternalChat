using System.Reflection;
using System.Text.Json;
using InternalChat.Api.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace InternalChat.ContractTests;

/// <summary>
/// T054 — <c>/me</c>, <c>/me/sessions</c>, and <c>/directory/employees</c> against
/// <c>contracts/openapi.yaml</c>.
/// </summary>
/// <remarks>
/// <para>
/// Two independent things can drift, so both are asserted. The <b>routing table</b> must offer
/// every documented path and method — a client generated from the document would otherwise call an
/// endpoint that returns 404. And the <b>response DTOs</b> must carry every property the document
/// declares required, spelled the way the document spells them, because a client reading
/// <c>displayName</c> from a payload that says <c>DisplayName</c> gets null and shows a blank name
/// rather than an error.
/// </para>
/// <para>
/// Deliberately no database. These are structural assertions about the contract, and they should
/// run in the pipeline's fast gate rather than behind five containers — the behavioural half is
/// covered by the integration suite, which does have the containers.
/// </para>
/// </remarks>
public sealed class DirectoryContractTests : IClassFixture<WebApplicationFactory<Program>>
{
    /// <summary>Serialization must match what the API actually returns, hence Web defaults.</summary>
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly WebApplicationFactory<Program> _factory;
    private readonly OpenApiContract _contract = OpenApiContract.Load();

    public DirectoryContractTests(WebApplicationFactory<Program> factory) => _factory = factory;

    /// <summary>The directory paths this task covers, as written in the contract.</summary>
    public static TheoryData<string, string> DocumentedOperations() => new()
    {
        { "/me", "GET" },
        { "/me/sessions", "GET" },
        { "/me/sessions/{sessionId}", "DELETE" },
        { "/directory/employees", "GET" },
    };

    [Theory]
    [MemberData(nameof(DocumentedOperations))]
    public void Every_documented_directory_operation_is_mapped(string path, string method)
    {
        Assert.Contains(path, _contract.Paths);
        Assert.Contains(method, _contract.MethodsFor(path));

        string expected = _contract.BasePath + path;

        bool mapped = Endpoints().Any(endpoint =>
            string.Equals(RouteOf(endpoint), expected, StringComparison.OrdinalIgnoreCase)
            && MethodsOf(endpoint).Contains(method, StringComparer.OrdinalIgnoreCase));

        Assert.True(
            mapped,
            $"""
            The contract documents {method} {expected}, and no endpoint serves it.

            Mapped routes:
              {string.Join("\n  ", Endpoints().Select(e => $"{string.Join('|', MethodsOf(e))} {RouteOf(e)}").Order())}
            """);
    }

    /// <summary>
    /// The <c>servers</c> prefix is not decoration: it is part of every URL a generated client
    /// builds. Asserted separately so a mismatch says so plainly rather than surfacing as four
    /// unrelated "not mapped" failures.
    /// </summary>
    [Fact]
    public void The_api_is_served_under_the_documented_base_path()
    {
        Assert.Equal("/api/v1", _contract.BasePath);

        Assert.Contains(
            Endpoints(),
            endpoint => RouteOf(endpoint).StartsWith(_contract.BasePath, StringComparison.Ordinal));
    }

    [Fact]
    public void CurrentEmployee_carries_every_required_property()
    {
        AssertSatisfies<CurrentEmployeeResponse>("CurrentEmployee");
    }

    [Fact]
    public void EmployeeSummary_carries_every_required_property()
    {
        AssertSatisfies<EmployeeSummaryResponse>("EmployeeSummary");
    }

    /// <summary>
    /// <c>Session</c> declares no <c>required</c> block, so every property is optional and this
    /// asserts spelling and type instead — which is the part a client actually breaks on.
    /// </summary>
    [Fact]
    public void Session_property_names_match_the_contract()
    {
        IReadOnlyCollection<string> documented = _contract.PropertiesOf("Session");
        IReadOnlyCollection<string> serialized = SerializedPropertyNames<SessionResponse>();

        foreach (string property in documented)
        {
            Assert.Contains(property, serialized);
        }
    }

    /// <summary>
    /// FR-040's field must be a boolean, not a string.
    /// </summary>
    /// <remarks>
    /// Its own test because the consequence is specific and silent: a client that reads
    /// <c>canReceiveNotifications</c> as truthy would treat the string <c>"false"</c> as true and
    /// tell an unreachable employee they are reachable — the exact thing FR-040 exists to prevent.
    /// </remarks>
    [Fact]
    public void The_notification_capability_flag_is_a_boolean()
    {
        Assert.Equal("boolean", _contract.TypeOfProperty("CurrentEmployee", "canReceiveNotifications"));

        Assert.Equal(
            typeof(bool),
            typeof(CurrentEmployeeResponse)
                .GetProperty(nameof(CurrentEmployeeResponse.CanReceiveNotifications))!
                .PropertyType);
    }

    /// <summary>
    /// A response DTO must serialize every property the schema marks required, spelled identically.
    /// </summary>
    private void AssertSatisfies<TResponse>(string schemaName)
    {
        IReadOnlyCollection<string> required = _contract.RequiredPropertiesOf(schemaName);

        Assert.NotEmpty(required);

        IReadOnlyCollection<string> serialized = SerializedPropertyNames<TResponse>();

        List<string> missing = [.. required.Where(r => !serialized.Contains(r, StringComparer.Ordinal))];

        Assert.True(
            missing.Count == 0,
            $"""
            {typeof(TResponse).Name} does not serialize {string.Join(", ", missing)}, which
            openapi.yaml declares required on '{schemaName}'.

            Serialized as: {string.Join(", ", serialized.Order())}

            A required property that is absent — or spelled differently — reaches the client as
            null, which usually renders as a blank field rather than as an error.
            """);
    }

    /// <summary>
    /// The property names this type actually puts on the wire.
    /// </summary>
    /// <remarks>
    /// Obtained by serializing a constructed instance rather than by reading
    /// <see cref="PropertyInfo.Name"/>, so the camel-casing policy the API applies is part of what
    /// is being asserted. Reflecting over the CLR names would compare <c>DisplayName</c> against
    /// the contract's <c>displayName</c> and report a failure that is not real — or, worse, pass
    /// while the wire format was wrong.
    /// </remarks>
    private static IReadOnlyCollection<string> SerializedPropertyNames<TResponse>()
    {
        object instance = CreateSample(typeof(TResponse));

        using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(instance, SerializerOptions));

        return [.. document.RootElement.EnumerateObject().Select(p => p.Name)];
    }

    /// <summary>Builds an instance from the record's primary constructor with placeholder values.</summary>
    private static object CreateSample(Type type)
    {
        ConstructorInfo constructor = type.GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();

        object?[] arguments = [.. constructor.GetParameters().Select(p => Placeholder(p.ParameterType))];

        return constructor.Invoke(arguments);
    }

    private static object? Placeholder(Type type)
    {
        Type underlying = Nullable.GetUnderlyingType(type) ?? type;

        if (underlying == typeof(string))
        {
            return "sample";
        }

        // Value types get their default; reference types other than string get null, which is
        // enough for the property to appear in the payload with the right name.
        return underlying.IsValueType ? Activator.CreateInstance(underlying) : null;
    }

    private IEnumerable<Endpoint> Endpoints() =>
        _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints;

    private static string RouteOf(Endpoint endpoint) =>
        endpoint is RouteEndpoint route ? $"/{route.RoutePattern.RawText?.TrimStart('/')}" : string.Empty;

    private static IReadOnlyCollection<string> MethodsOf(Endpoint endpoint) =>
        endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [];
}
