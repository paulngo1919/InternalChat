using InternalChat.Domain.Employees;

namespace InternalChat.Application.Abstractions;

/// <summary>
/// The write side of the employee projection. Used by directory sync alone.
/// </summary>
/// <remarks>
/// <para>
/// Separate from <see cref="IEmployeeDirectory"/> on purpose. That one is a read model returning
/// <see cref="EmployeeProfile"/> projections and is injected into request paths all over the
/// platform; this one hands back the tracked <see cref="Employee"/> aggregate so its invariants can
/// be exercised, and exists to serve exactly one caller.
/// </para>
/// <para>
/// Splitting them means an endpoint cannot accidentally acquire the ability to write an employee
/// row. FR-001 says the corporate directory owns employee lifecycle; a single interface with both
/// halves would make that a convention rather than something the type system enforces.
/// </para>
/// <para>
/// There is no <c>SaveChangesAsync</c> here. The pipeline's transaction behavior commits, so the
/// state change and the outbox rows it produced go together (Principle VI).
/// </para>
/// </remarks>
public interface IEmployeeStore
{
    /// <summary>Finds the tracked employee for a subject, or <c>null</c>.</summary>
    Task<Employee?> FindBySubjectAsync(string externalSubject, CancellationToken cancellationToken = default);

    /// <summary>Adds a newly projected employee.</summary>
    Task AddAsync(Employee employee, CancellationToken cancellationToken = default);
}
