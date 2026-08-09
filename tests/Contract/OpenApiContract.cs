using InternalChat.TestSupport;
using YamlDotNet.Serialization;

namespace InternalChat.ContractTests;

/// <summary>
/// Reads <c>contracts/openapi.yaml</c> so tests can assert against the committed document.
/// </summary>
/// <remarks>
/// <para>
/// The point of a contract test is that the contract is a separate artefact. Restating the schemas
/// in C# and comparing the code to itself would pass forever — including the day someone changes a
/// response and forgets the document, which is the failure Principle VI's "contract changes MUST be
/// additive" rule exists to catch.
/// </para>
/// <para>
/// Parsed as loosely typed maps rather than deserialized into an OpenAPI object model. The document
/// is small, only a handful of facts are asserted, and an object model would need to keep pace with
/// every OpenAPI construct the document later grows.
/// </para>
/// </remarks>
public sealed class OpenApiContract
{
    private readonly Dictionary<object, object> _root;

    private OpenApiContract(Dictionary<object, object> root) => _root = root;

    /// <summary>Loads and parses the committed contract.</summary>
    public static OpenApiContract Load()
    {
        string path = RepositoryPaths.OpenApiContract;

        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"The OpenAPI contract was not found at '{path}'. The contract suite asserts the "
                + "API against the committed document; without it these tests would silently "
                + "assert nothing.",
                path);
        }

        IDeserializer deserializer = new DeserializerBuilder().Build();

        object? parsed = deserializer.Deserialize(File.ReadAllText(path));

        return parsed is Dictionary<object, object> root
            ? new OpenApiContract(root)
            : throw new InvalidOperationException($"'{path}' did not parse as a YAML mapping.");
    }

    /// <summary>
    /// The path prefix every documented endpoint sits behind, from the document's <c>servers</c>.
    /// </summary>
    /// <remarks>
    /// Read from the document rather than hardcoded, so moving to <c>/api/v2</c> is a change to one
    /// artefact and the tests follow.
    /// </remarks>
    public string BasePath
    {
        get
        {
            if (_root.TryGetValue("servers", out object? servers)
                && servers is List<object> { Count: > 0 } list
                && list[0] is Dictionary<object, object> first
                && first.TryGetValue("url", out object? url))
            {
                return url.ToString() ?? string.Empty;
            }

            return string.Empty;
        }
    }

    /// <summary>Every documented path, as written in the document.</summary>
    public IReadOnlyCollection<string> Paths =>
        [.. Section("paths").Keys.Select(k => k.ToString() ?? string.Empty)];

    /// <summary>The HTTP methods documented for a path, upper-cased.</summary>
    public IReadOnlyCollection<string> MethodsFor(string path)
    {
        if (!Section("paths").TryGetValue(path, out object? item)
            || item is not Dictionary<object, object> operations)
        {
            return [];
        }

        // `parameters` is a sibling of the operations and is not one, so it is filtered out rather
        // than reported as an HTTP method nobody implements.
        return
        [
            .. operations.Keys
                .Select(k => k.ToString() ?? string.Empty)
                .Where(k => !string.Equals(k, "parameters", StringComparison.Ordinal))
                .Select(k => k.ToUpperInvariant()),
        ];
    }

    /// <summary>Property names a schema declares as required, or empty when it declares none.</summary>
    public IReadOnlyCollection<string> RequiredPropertiesOf(string schemaName)
    {
        if (!Schema(schemaName).TryGetValue("required", out object? required)
            || required is not List<object> names)
        {
            return [];
        }

        return [.. names.Select(n => n.ToString() ?? string.Empty)];
    }

    /// <summary>Every property a schema declares.</summary>
    public IReadOnlyCollection<string> PropertiesOf(string schemaName)
    {
        if (!Schema(schemaName).TryGetValue("properties", out object? properties)
            || properties is not Dictionary<object, object> map)
        {
            return [];
        }

        return [.. map.Keys.Select(k => k.ToString() ?? string.Empty)];
    }

    /// <summary>The declared type of one property, for example <c>string</c> or <c>boolean</c>.</summary>
    public string? TypeOfProperty(string schemaName, string propertyName)
    {
        if (!Schema(schemaName).TryGetValue("properties", out object? properties)
            || properties is not Dictionary<object, object> map
            || !map.TryGetValue(propertyName, out object? property)
            || property is not Dictionary<object, object> definition)
        {
            return null;
        }

        return definition.TryGetValue("type", out object? type) ? type.ToString() : null;
    }

    private Dictionary<object, object> Schema(string name)
    {
        Dictionary<object, object> components = Section("components");

        return components.TryGetValue("schemas", out object? schemas)
            && schemas is Dictionary<object, object> map
            && map.TryGetValue(name, out object? schema)
            && schema is Dictionary<object, object> definition
                ? definition
                : throw new InvalidOperationException(
                    $"The contract declares no schema named '{name}'. Either the schema was renamed "
                    + "in openapi.yaml without the test following, or the test is asserting against "
                    + "a name that never existed.");
    }

    private Dictionary<object, object> Section(string name) =>
        _root.TryGetValue(name, out object? section) && section is Dictionary<object, object> map
            ? map
            : [];
}
