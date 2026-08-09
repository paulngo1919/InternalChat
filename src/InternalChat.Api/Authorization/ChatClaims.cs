using System.Globalization;
using System.Security.Claims;

namespace InternalChat.Api.Authorization;

/// <summary>
/// Reads the three token facts this platform's access control depends on.
/// </summary>
/// <remarks>
/// Centralised because the claim names are not obvious and are read from several places — the HTTP
/// gate, the hub filter, and the sweep. ASP.NET Core's default claim mapping rewrites <c>sub</c> to
/// a long WS-Federation URI, so code reaching for <c>"sub"</c> directly finds nothing and silently
/// treats every request as unidentified. Turning the mapping off (which
/// <see cref="AuthenticationExtensions"/> does) and reading the raw names here means one place
/// knows about that, instead of every caller rediscovering it.
/// </remarks>
public static class ChatClaims
{
    /// <summary>Keycloak subject. Equals <c>employee.external_subject</c>.</summary>
    public const string Subject = "sub";

    /// <summary>Keycloak session id. The unit sign-out and back-channel logout revoke.</summary>
    public const string SessionId = "sid";

    /// <summary>Expiry, as seconds since the Unix epoch.</summary>
    public const string ExpiresAt = "exp";

    /// <summary>The subject, or <c>null</c> when the principal carries none.</summary>
    public static string? SubjectOf(ClaimsPrincipal? principal)
    {
        string? subject = principal?.FindFirstValue(Subject);
        return string.IsNullOrWhiteSpace(subject) ? null : subject;
    }

    /// <summary>
    /// The session id, or <c>null</c>.
    /// </summary>
    /// <remarks>
    /// Genuinely optional: a token minted by a flow without a browser session carries no <c>sid</c>.
    /// Callers must treat its absence as "cannot revoke this individually", never as a reason to
    /// skip the subject-level check.
    /// </remarks>
    public static string? SessionIdOf(ClaimsPrincipal? principal)
    {
        string? sessionId = principal?.FindFirstValue(SessionId);
        return string.IsNullOrWhiteSpace(sessionId) ? null : sessionId;
    }

    /// <summary>
    /// The moment this token stops being valid, or <c>null</c> when it declares no expiry.
    /// </summary>
    /// <remarks>
    /// A token with no <c>exp</c> never expires, which is why callers treat <c>null</c> here as
    /// already-expired rather than as permission to continue.
    /// </remarks>
    public static DateTimeOffset? ExpiresAtOf(ClaimsPrincipal? principal)
    {
        string? raw = principal?.FindFirstValue(ExpiresAt);

        return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;
    }
}
