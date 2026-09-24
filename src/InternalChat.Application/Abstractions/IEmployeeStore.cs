using InternalChat.Domain.Employees;

namespace InternalChat.Application.Abstractions;

/// <summary>
/// The side of the employee projection that hands back tracked aggregates.
/// </summary>
/// <remarks>
/// <para>
/// Separate from <see cref="IEmployeeDirectory"/> on purpose. That one is a read model returning
/// <see cref="EmployeeProfile"/> projections and is injected into request paths all over the
/// platform; this one hands back the tracked <see cref="Employee"/> aggregate so its invariants can
/// be exercised.
/// </para>
/// <para>
/// Two callers now: directory sync, which writes, and conversation creation, which needs the
/// aggregate rather than a projection so it can call
/// <see cref="Employee.EnsureCanJoinConversation"/>. Reading <c>IsActive</c> off a projection would
/// have been the same check written a second time, and T058 put that rule on the entity precisely so
/// every path that adds a member passes through one gate instead of each remembering.
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

    /// <summary>
    /// Loads several employees by internal id.
    /// </summary>
    /// <remarks>
    /// One query rather than one per id — a group creation naming fifty colleagues would otherwise
    /// be fifty round trips. The result may be shorter than the input when an id does not exist; the
    /// caller compares the counts, because a silently missing employee is how a conversation ends up
    /// with fewer members than the creator asked for.
    /// </remarks>
    Task<IReadOnlyList<Employee>> FindManyAsync(
        IReadOnlyCollection<Guid> employeeIds,
        CancellationToken cancellationToken = default);

    /// <summary>Adds a newly projected employee.</summary>
    Task AddAsync(Employee employee, CancellationToken cancellationToken = default);
}
