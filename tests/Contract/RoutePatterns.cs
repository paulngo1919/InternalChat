using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace InternalChat.ContractTests;

/// <summary>
/// Compares a mapped route against the path an OpenAPI document declares.
/// </summary>
/// <remarks>
/// <para>
/// The two spell parameters differently, and neither is wrong. ASP.NET Core writes
/// <c>{conversationId:guid}</c> — the constraint is what rejects a malformed id at routing, before
/// any handler or authorization filter runs. OpenAPI writes <c>{conversationId}</c> and expresses
/// the type separately, as <c>schema: {type: string, format: uuid}</c>.
/// </para>
/// <para>
/// The first version of these tests compared the raw strings and reported six documented operations
/// as unmapped when every one of them was mapped correctly. Normalising here keeps the assertion
/// pointed at what it is actually for — that the routing table offers what the contract promises —
/// rather than at a notation difference. Removing the constraints from the endpoints to satisfy the
/// test would have been the wrong repair: it would trade a real input check for a green comparison.
/// </para>
/// </remarks>
internal static partial class RoutePatterns
{
    /// <summary>Matches an inline route constraint, e.g. the <c>:guid</c> in <c>{id:guid}</c>.</summary>
    [GeneratedRegex(@"\{(?<name>[^:}?]+)(?::[^}]+)?(?<optional>\?)?\}", RegexOptions.CultureInvariant)]
    private static partial Regex ParameterWithConstraint();

    /// <summary>
    /// The route an endpoint serves, with constraints stripped so it can be compared to a contract
    /// path.
    /// </summary>
    public static string Of(Endpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        if (endpoint is not RouteEndpoint route)
        {
            return string.Empty;
        }

        string raw = $"/{route.RoutePattern.RawText?.TrimStart('/')}";

        return ParameterWithConstraint().Replace(raw, match => $"{{{match.Groups["name"].Value}}}");
    }

    /// <summary>The HTTP methods an endpoint answers.</summary>
    public static IReadOnlyCollection<string> MethodsOf(Endpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        return endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [];
    }
}
