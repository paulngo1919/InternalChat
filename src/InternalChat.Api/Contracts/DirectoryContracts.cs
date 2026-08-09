namespace InternalChat.Api.Contracts;

/// <summary>
/// Why this browser cannot be reached by a notification (FR-040).
/// </summary>
/// <remarks>
/// Serialized in the snake_case spellings the OpenAPI enum declares, because the client shows a
/// different sentence for each and a renamed value would silently fall through to the generic one.
/// </remarks>
public static class NotificationBlockReasons
{
    /// <summary>The employee declined the browser's permission prompt.</summary>
    public const string PermissionDenied = "permission_denied";

    /// <summary>The service worker is not installed.</summary>
    public const string NotInstalled = "not_installed";

    /// <summary>The browser does not implement push at all.</summary>
    public const string UnsupportedBrowser = "unsupported_browser";

    /// <summary>Permission exists but no live push subscription is registered.</summary>
    public const string NoSubscription = "no_subscription";
}

/// <summary>
/// <c>GET /me</c> — the signed-in employee (<c>CurrentEmployee</c> in openapi.yaml).
/// </summary>
/// <remarks>
/// A DTO, not the domain entity: <c>tests/Architecture/BoundaryTests.cs</c> fails the build if a
/// Domain type appears in an Api contract. That rule earns its keep here — <c>Employee</c> carries
/// <c>ExternalSubject</c>, and serializing it would publish every colleague's identity-provider
/// subject to any signed-in caller.
/// </remarks>
public sealed record CurrentEmployeeResponse(
    Guid Id,
    string DisplayName,
    string Email,
    string? AvatarUrl,
    bool IsAdmin,
    bool CanReceiveNotifications,
    string? NotificationBlockReason);

/// <summary>
/// <c>GET /me/sessions</c> — one active session (<c>Session</c> in openapi.yaml).
/// </summary>
/// <param name="Id">Identity-provider session id.</param>
/// <param name="UserAgent">Browser string as first seen, so a device is recognisable.</param>
/// <param name="CreatedAt">When the platform first saw this session.</param>
/// <param name="LastSeenAt">When it last presented a token.</param>
/// <param name="IsCurrent">
/// True for the session making this request, so the UI can avoid offering to end the session the
/// employee is reading the list on without warning them what it does.
/// </param>
public sealed record SessionResponse(
    string Id,
    string UserAgent,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastSeenAt,
    bool IsCurrent);

/// <summary>
/// <c>GET /directory/employees</c> — one search result (<c>EmployeeSummary</c> in openapi.yaml).
/// </summary>
/// <remarks>
/// <c>Presence</c> is declared by the contract and deliberately omitted until US2 brings the
/// presence store online. Returning a hardcoded "offline" would be worse than omitting it: the
/// field would look implemented and be wrong for everyone.
/// </remarks>
public sealed record EmployeeSummaryResponse(
    Guid Id,
    string DisplayName,
    string Email,
    string? AvatarUrl,
    string Status);
