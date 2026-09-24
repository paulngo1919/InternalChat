using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;

namespace InternalChat.ArchitectureTests;

/// <summary>
/// T019 — Constitution v1.2.0, Principle IV (Security by Default):
/// "an endpoint without an explicit authorization policy MUST fail the build".
/// </summary>
/// <remarks>
/// This is the test the constitution is describing. A review checklist cannot fail a build, and
/// "we always remember to add the policy" is exactly the kind of claim that holds until the one
/// Friday it does not.
///
/// <para>
/// Endpoints are enumerated from <see cref="EndpointDataSource"/> rather than by reflection,
/// because Minimal API routes are registered by code, not by attribute — reflection cannot see
/// them at all.
/// </para>
///
/// <para>
/// The failure mode being prevented is silence: an endpoint carrying neither
/// <c>[Authorize]</c> nor <c>[AllowAnonymous]</c> is served without a decision ever being made.
/// It looks fine in review and is wide open at runtime.
/// </para>
/// </remarks>
public sealed class AuthorizationCoverageTests : IClassFixture<WebApplicationFactory<Program>>
{
    /// <summary>
    /// The complete set of endpoints permitted to be anonymous, with the reason each is
    /// justified. Anything not listed here MUST carry an authorization policy.
    ///
    /// <para>
    /// Adding a route to this list is a security decision. Per the constitution it needs a
    /// second reviewer with security sign-off, because it removes an endpoint from
    /// deny-by-default permanently and quietly.
    /// </para>
    /// </summary>
    private static readonly Dictionary<string, string> JustifiedAnonymousRoutes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["/health/live"] = "Liveness probe. A probe that needs a token cannot report an outage.",
        ["/health/ready"] = "Readiness probe. Same reasoning as liveness.",

        // T064. Keycloak posts here server-to-server when a session ends, with no user context, so
        // there is no bearer token it could present. The logout token in the body IS the
        // credential, and LogoutTokenValidator checks it in full — signature, issuer, audience,
        // lifetime, the back-channel logout `events` claim, and the absence of `nonce` — before
        // anything is revoked. The last two are what stop an ordinary access token being replayed
        // here to sign its bearer out. Rate limited per IP on the authentication policy.
        ["/api/v1/auth/backchannel-logout"] =
            "Keycloak back-channel logout (research.md D5). Called server-to-server with no user "
            + "context; the signed logout token is the credential and is fully validated.",

        // T189. LiveKit posts room lifecycle events server-to-server and holds no user token, so
        // there is nothing for the authorization pipeline to check. The HMAC signature IS the
        // credential: MeetingWebhookEndpoints.VerifySignature checks the JWT's own HMAC against
        // the API secret AND that its sha256 claim matches the raw request body, both in fixed
        // time. Verifying only the token would accept a valid one replayed over a different body,
        // which is what would let someone fabricate meeting attendance in the FR-051 audit record.
        //
        // Note this endpoint is outside /api/v1 and excluded from the OpenAPI document: it is not
        // part of the client contract and nothing generated should call it.
        ["/webhooks/livekit"] =
            "LiveKit room lifecycle webhook (T189, research.md D12). Server-to-server with no user "
            + "context; the HMAC signature over the raw body is the credential and is verified in "
            + "full before the body is read.",
    };

    private readonly WebApplicationFactory<Program> _factory;

    public AuthorizationCoverageTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public void Every_endpoint_declares_an_explicit_authorization_decision()
    {
        List<string> undeclared = [];

        foreach (Endpoint endpoint in Endpoints())
        {
            bool hasAuthorize = endpoint.Metadata.GetMetadata<IAuthorizeData>() is not null;
            bool hasAllowAnonymous = endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null;

            if (!hasAuthorize && !hasAllowAnonymous)
            {
                undeclared.Add(Describe(endpoint));
            }
        }

        Assert.True(
            undeclared.Count == 0,
            $"""
            These endpoints are served without any authorization decision:

              {string.Join("\n  ", undeclared)}

            Constitution Principle IV: every endpoint, hub method, and consumer is
            deny-by-default. Add .RequireAuthorization(...) with a resource-scoped policy, or
            .AllowAnonymous() plus an entry in JustifiedAnonymousRoutes explaining why.
            """);
    }

    [Fact]
    public void Anonymous_endpoints_are_limited_to_the_justified_allow_list()
    {
        List<string> unjustified = [];

        foreach (Endpoint endpoint in Endpoints())
        {
            if (endpoint.Metadata.GetMetadata<IAllowAnonymous>() is null)
            {
                continue;
            }

            string route = RoutePatternOf(endpoint);
            if (!JustifiedAnonymousRoutes.ContainsKey(route))
            {
                unjustified.Add(Describe(endpoint));
            }
        }

        Assert.True(
            unjustified.Count == 0,
            $"""
            These endpoints are anonymous but are not on the justified allow list:

              {string.Join("\n  ", unjustified)}

            Anonymous access is a security decision, not a default. Add the route to
            JustifiedAnonymousRoutes with the reason it must be reachable without a token, and
            get the security sign-off the constitution requires for authorization changes.
            """);
    }

    /// <summary>
    /// Resource-scoped authorization: per FR-002, access is checked against membership of the
    /// specific conversation, not merely against a role. An endpoint carrying a conversation id
    /// must therefore require a policy, not just any authenticated user.
    /// </summary>
    [Fact]
    public void Conversation_scoped_endpoints_require_a_named_policy()
    {
        List<string> roleOnly = [];

        foreach (Endpoint endpoint in Endpoints())
        {
            string route = RoutePatternOf(endpoint);
            if (!route.Contains("{conversationId", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            IAuthorizeData? authorize = endpoint.Metadata.GetMetadata<IAuthorizeData>();
            if (authorize is null || string.IsNullOrWhiteSpace(authorize.Policy))
            {
                roleOnly.Add(Describe(endpoint));
            }
        }

        Assert.True(
            roleOnly.Count == 0,
            $"""
            These conversation-scoped endpoints do not name an authorization policy:

              {string.Join("\n  ", roleOnly)}

            Constitution Principle IV requires resource-scoped authorization: roles say nothing
            about WHICH conversation. Require the membership policy so access is checked against
            the specific resource on every request.
            """);
    }

    /// <summary>
    /// SignalR hubs are enumerated by reflection because hub methods are invoked through the
    /// hub protocol, not through the endpoint routing table, so EndpointDataSource cannot see
    /// them individually.
    /// </summary>
    [Fact]
    public void Every_hub_carries_an_authorization_attribute()
    {
        Type[] hubs = ArchitectureAssemblies.Api
            .GetTypes()
            .Where(t => !t.IsAbstract && typeof(Hub).IsAssignableFrom(t))
            .ToArray();

        string[] unprotected = hubs
            .Where(h => h.GetCustomAttribute<AuthorizeAttribute>(inherit: true) is null)
            .Select(h => h.FullName ?? h.Name)
            .ToArray();

        Assert.True(
            unprotected.Length == 0,
            $"""
            These SignalR hubs are not marked [Authorize]:

              {string.Join("\n  ", unprotected)}

            A hub without [Authorize] accepts any connection. Per contracts/signalr-hub.md the
            token is validated on connect AND on reconnect — a long-lived connection must not
            outlive its token.
            """);
    }

    private IEnumerable<Endpoint> Endpoints()
    {
        // Touching Services builds the host, which is what registers the endpoints.
        EndpointDataSource source = _factory.Services.GetRequiredService<EndpointDataSource>();
        return source.Endpoints;
    }

    private static string RoutePatternOf(Endpoint endpoint) =>
        endpoint is RouteEndpoint route ? route.RoutePattern.RawText ?? string.Empty : string.Empty;

    private static string Describe(Endpoint endpoint)
    {
        string methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>() is { } m
            ? string.Join("|", m.HttpMethods)
            : "ANY";

        return $"{methods} {RoutePatternOf(endpoint)}  ({endpoint.DisplayName})";
    }
}
