using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace InternalChat.Api.Authorization;

/// <summary>What a validated back-channel logout token asks this platform to revoke.</summary>
/// <param name="Subject">The employee's identity-provider subject, when the token names one.</param>
/// <param name="SessionId">The session that ended, when the token names one.</param>
public sealed record LogoutRequest(string? Subject, string? SessionId);

/// <summary>
/// Validates the logout token Keycloak posts to the back-channel logout endpoint.
/// </summary>
/// <remarks>
/// <para>
/// The endpoint receiving these is necessarily anonymous — Keycloak calls it server-to-server with
/// no user context — so this token is the <em>only</em> thing standing between an anonymous
/// HTTP request and the ability to sign employees out. Validating it loosely would hand anyone on
/// the network a denial-of-service against every session in the platform.
/// </para>
/// <para>
/// The checks follow the OpenID Connect Back-Channel Logout specification, and the last two are
/// what distinguish a logout token from an ordinary access token: a logout token MUST carry the
/// <c>events</c> claim naming the back-channel logout event, and MUST NOT carry <c>nonce</c>.
/// Without those, an attacker holding any valid access token could replay it here and sign the
/// bearer out — turning a stolen token into a logout weapon rather than merely a stolen token.
/// </para>
/// </remarks>
public sealed class LogoutTokenValidator
{
    /// <summary>The event URI a back-channel logout token must declare.</summary>
    public const string BackChannelLogoutEvent = "http://schemas.openid.net/event/backchannel-logout";

    private readonly IOptionsMonitor<JwtBearerOptions> _jwtOptions;
    private readonly ChatAuthenticationOptions _options;

    /// <summary>Creates the validator.</summary>
    public LogoutTokenValidator(
        IOptionsMonitor<JwtBearerOptions> jwtOptions,
        IOptions<ChatAuthenticationOptions> options)
    {
        ArgumentNullException.ThrowIfNull(jwtOptions);
        ArgumentNullException.ThrowIfNull(options);

        _jwtOptions = jwtOptions;
        _options = options.Value;
    }

    /// <summary>
    /// Validates a logout token, returning what to revoke, or <c>null</c> when it is not acceptable.
    /// </summary>
    public async Task<LogoutRequest?> ValidateAsync(string? logoutToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(logoutToken))
        {
            return null;
        }

        JwtBearerOptions bearer = _jwtOptions.Get(JwtBearerDefaults.AuthenticationScheme);

        if (bearer.ConfigurationManager is null)
        {
            return null;
        }

        // Reuses the same signing keys the request pipeline validates access tokens with, refreshed
        // on the same schedule. Fetching the JWKS separately here would create a second key cache
        // that expires at a different moment — so a key rotation would break logout and nothing
        // else, which is a hard failure to attribute.
        OpenIdConnectConfiguration configuration = await bearer.ConfigurationManager
            .GetConfigurationAsync(cancellationToken)
            .ConfigureAwait(false);

        TokenValidationParameters parameters = new()
        {
            ValidateIssuer = true,
            ValidIssuer = _options.Authority,

            ValidateAudience = true,
            ValidAudience = _options.Audience,

            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = configuration.SigningKeys,

            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero,
        };

        JsonWebTokenHandler handler = new();
        TokenValidationResult result = await handler
            .ValidateTokenAsync(logoutToken, parameters)
            .ConfigureAwait(false);

        if (!result.IsValid || result.SecurityToken is not JsonWebToken token)
        {
            return null;
        }

        if (!DeclaresLogoutEvent(token) || token.TryGetClaim("nonce", out _))
        {
            return null;
        }

        token.TryGetClaim(ChatClaims.Subject, out System.Security.Claims.Claim? subject);
        token.TryGetClaim(ChatClaims.SessionId, out System.Security.Claims.Claim? sessionId);

        // A logout token naming neither is meaningless — there is nothing to revoke — and treating
        // it as success would return 200 to a caller whose logout silently did nothing.
        if (subject is null && sessionId is null)
        {
            return null;
        }

        return new LogoutRequest(subject?.Value, sessionId?.Value);
    }

    /// <summary>
    /// Whether the token declares the back-channel logout event.
    /// </summary>
    /// <remarks>
    /// The <c>events</c> claim is a JSON object keyed by event URI, so its presence as a key is the
    /// assertion; the value is an empty object by specification and carries nothing.
    /// </remarks>
    private static bool DeclaresLogoutEvent(JsonWebToken token)
    {
        if (!token.TryGetClaim("events", out System.Security.Claims.Claim? events))
        {
            return false;
        }

        try
        {
            using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(events.Value);
            return document.RootElement.TryGetProperty(BackChannelLogoutEvent, out _);
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }
}
