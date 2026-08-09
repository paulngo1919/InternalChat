using InternalChat.Domain.Employees;

namespace InternalChat.Application.Abstractions;

/// <summary>
/// The directory facts a use case needs about one employee.
/// </summary>
/// <remarks>
/// A projection, not the entity. Constitution Principle I forbids a Domain entity appearing in an
/// Api contract DTO (<c>tests/Architecture/BoundaryTests.cs</c> fails the build), and returning
/// <see cref="Employee"/> from a read path would put it one mapping mistake away from being
/// serialized straight out of an endpoint.
/// </remarks>
/// <param name="Id">Internal employee id — the value every other table references.</param>
/// <param name="ExternalSubject">Keycloak <c>sub</c>. The join between a token and this row.</param>
/// <param name="DisplayName">Name shown to colleagues.</param>
/// <param name="Email">Corporate address.</param>
/// <param name="AvatarUrl">Avatar location, when the directory supplies one.</param>
/// <param name="Status">Directory lifecycle state.</param>
public sealed record EmployeeProfile(
    Guid Id,
    string ExternalSubject,
    string DisplayName,
    string Email,
    string? AvatarUrl,
    EmployeeStatus Status)
{
    /// <summary>True when this employee may sign in and be added to conversations.</summary>
    public bool IsActive => Status == EmployeeStatus.Active;
}

/// <summary>
/// Reads the employee directory projection.
/// </summary>
/// <remarks>
/// <para>
/// Two questions, both on user-facing paths. <see cref="FindBySubjectAsync"/> turns a validated
/// token into the employee id everything else is keyed by; <see cref="SearchAsync"/> backs FR-007's
/// "find a colleague to message".
/// </para>
/// <para>
/// Read-only by construction. Employee rows are written by directory sync alone (FR-001: the
/// platform never owns employee lifecycle), so an interface that could write here would be an
/// invitation to create an employee from a request path and quietly become a second source of
/// truth alongside Keycloak.
/// </para>
/// </remarks>
public interface IEmployeeDirectory
{
    /// <summary>
    /// Resolves a token subject to an employee, or <c>null</c> when no row matches.
    /// </summary>
    /// <remarks>
    /// Returns deactivated employees too, rather than filtering them out. The caller must refuse
    /// them — but a caller that cannot tell "no such employee" from "deactivated employee" cannot
    /// write a useful audit record for the refusal, and those two cases mean very different things
    /// to whoever reads it later.
    /// </remarks>
    Task<EmployeeProfile?> FindBySubjectAsync(string externalSubject, CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches active employees by display name or email (FR-007).
    /// </summary>
    /// <param name="query">At least two characters; shorter queries match too much to be useful.</param>
    /// <param name="limit">Hard-capped by the caller. Never unbounded (Principle V).</param>
    Task<IReadOnlyList<EmployeeProfile>> SearchAsync(
        string query,
        int limit,
        CancellationToken cancellationToken = default);
}
