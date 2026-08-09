using InternalChat.Application.Abstractions;

namespace InternalChat.Api.Authorization;

/// <summary>
/// The employee behind the current request, resolved once and reused.
/// </summary>
/// <remarks>
/// <para>
/// A token identifies a subject; almost everything else in this platform is keyed by the internal
/// employee id (research.md, <c>employee.external_subject</c>). Something has to bridge the two on
/// every authenticated request, and doing it ad hoc in each endpoint would mean the same lookup
/// several times per request — visible immediately in the query-count budget the EF interceptor
/// enforces.
/// </para>
/// <para>
/// Scoped, and populated by <see cref="AccessGateMiddleware"/> before any endpoint runs. Endpoints
/// therefore read a value rather than performing a lookup, and an endpoint that somehow ran without
/// the gate finds <see cref="Profile"/> null rather than silently resolving a second identity.
/// </para>
/// </remarks>
public sealed class CurrentEmployee
{
    /// <summary>The resolved employee, or <c>null</c> on an anonymous or unresolved request.</summary>
    public EmployeeProfile? Profile { get; private set; }

    /// <summary>The token's session id, when it carried one.</summary>
    public string? SessionId { get; private set; }

    /// <summary>The resolved employee id.</summary>
    /// <exception cref="InvalidOperationException">The request was not authenticated.</exception>
    public Guid Id => Profile?.Id
        ?? throw new InvalidOperationException(
            "No employee is resolved for this request. An endpoint reached CurrentEmployee.Id "
            + "without AccessGateMiddleware having run, which means it is served without the "
            + "identity checks every authenticated endpoint depends on.");

    /// <summary>True when an active employee is resolved.</summary>
    public bool IsResolved => Profile is not null;

    /// <summary>Called by the access gate once the token has been resolved to a row.</summary>
    public void Set(EmployeeProfile profile, string? sessionId)
    {
        ArgumentNullException.ThrowIfNull(profile);

        Profile = profile;
        SessionId = sessionId;
    }
}
