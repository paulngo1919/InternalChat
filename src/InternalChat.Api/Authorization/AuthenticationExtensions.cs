using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace InternalChat.Api.Authorization;

/// <summary>
/// T062 — validates Keycloak-issued bearer tokens: signature, issuer, audience, and expiry.
/// </summary>
/// <remarks>
/// FR-001: the platform never stores a password and never issues a credential. Everything it knows
/// about who is calling comes from a token this class validated.
/// </remarks>
public static class AuthenticationExtensions
{
    /// <summary>Path the SignalR hub is mapped at.</summary>
    /// <remarks>
    /// Named here because the token-from-query-string exception below is scoped to it, and an
    /// exception scoped by a string literal repeated in two files is an exception that eventually
    /// applies somewhere else.
    /// </remarks>
    public const string HubPath = "/hubs/chat";

    /// <summary>Adds JWT bearer authentication against the configured realm.</summary>
    public static IServiceCollection AddChatAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services
            .AddOptions<ChatAuthenticationOptions>()
            .Bind(configuration.GetSection(ChatAuthenticationOptions.SectionName))
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.Authority),
                "Keycloak:Authority is not configured. The API cannot validate a token without "
                + "knowing which realm issued it, and starting without it would mean starting with "
                + "authentication switched off.")
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.Audience),
                "Keycloak:Audience is not configured. Without audience validation, a token minted "
                + "for any other client in the same realm is accepted here.")
            .Validate(
                options => options.MaxAccessTokenLifetimeSeconds is > 0 and <= 900,
                "Keycloak:MaxAccessTokenLifetimeSeconds must be between 1 and 900. The constitution "
                + "caps access tokens at 15 minutes and research.md D5 sets 5, because FR-003's "
                + "five-minute revocation deadline is bounded by this number.")
            .ValidateOnStart();

        // The default mapping rewrites `sub` to a WS-Federation URI, so ChatClaims.SubjectOf would
        // find nothing and every authenticated request would look unidentified. Turning it off is
        // what makes the claim names in ChatClaims the ones actually present.
        JwtSecurityTokenHandler.DefaultInboundClaimTypeMap.Clear();

        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer();

        services
            .AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<ChatAuthenticationOptions>>((jwt, chat) =>
            {
                ChatAuthenticationOptions options = chat.Value;

                jwt.Authority = options.Authority;
                jwt.Audience = options.Audience;
                jwt.RequireHttpsMetadata = options.RequireHttpsMetadata;
                jwt.MapInboundClaims = false;

                jwt.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = options.Authority,

                    ValidateAudience = true,
                    ValidAudience = options.Audience,

                    ValidateIssuerSigningKey = true,
                    ValidateLifetime = true,

                    // ZERO, deliberately. The default is five minutes, which would let a
                    // five-minute token be accepted for ten — silently doubling the window FR-003
                    // gives to end access, and making the realm's 300-second lifespan meaningless.
                    // Keycloak and the API run on the same host clock; there is nothing to forgive.
                    ClockSkew = TimeSpan.Zero,

                    NameClaimType = "preferred_username",
                    RoleClaimType = "roles",
                };

                jwt.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        // WebSockets cannot carry an Authorization header — the browser API does
                        // not expose one — so SignalR passes the token as a query parameter. This
                        // is accepted for the hub path ONLY: allowing it everywhere would put
                        // access tokens into every proxy access log and browser history entry for
                        // the whole API.
                        if (string.IsNullOrEmpty(context.Token)
                            && context.HttpContext.Request.Path.StartsWithSegments(HubPath, StringComparison.Ordinal))
                        {
                            string? queryToken = context.Request.Query["access_token"];
                            if (!string.IsNullOrEmpty(queryToken))
                            {
                                context.Token = queryToken;
                            }
                        }

                        return Task.CompletedTask;
                    },

                    OnTokenValidated = context =>
                    {
                        FlattenRealmRoles(context.Principal);
                        return Task.CompletedTask;
                    },
                };
            });

        return services;
    }

    /// <summary>
    /// Turns Keycloak's nested <c>realm_access.roles</c> into flat role claims.
    /// </summary>
    /// <remarks>
    /// Keycloak emits realm roles as a JSON object — <c>{"realm_access":{"roles":["employee"]}}</c> —
    /// which arrives as a single claim whose value is that JSON text. <c>RequireRole</c> compares
    /// claim values as strings, so without this it compares against the whole JSON blob and matches
    /// nothing. The failure is silent in the worst way: every role check simply denies, and the
    /// obvious conclusion is that the role was never assigned.
    /// </remarks>
    private static void FlattenRealmRoles(System.Security.Claims.ClaimsPrincipal? principal)
    {
        if (principal?.Identity is not System.Security.Claims.ClaimsIdentity identity)
        {
            return;
        }

        string? realmAccess = principal.FindFirst("realm_access")?.Value;
        if (string.IsNullOrWhiteSpace(realmAccess))
        {
            return;
        }

        try
        {
            using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(realmAccess);

            if (!document.RootElement.TryGetProperty("roles", out System.Text.Json.JsonElement roles)
                || roles.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                return;
            }

            foreach (System.Text.Json.JsonElement role in roles.EnumerateArray())
            {
                if (role.GetString() is { Length: > 0 } value)
                {
                    identity.AddClaim(new System.Security.Claims.Claim("roles", value));
                }
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // A malformed realm_access claim means no roles, not a failed request. The token has
            // already been validated; refusing it here would turn an identity-provider quirk into
            // an outage, and every role-gated endpoint denies anyway.
        }
    }
}
