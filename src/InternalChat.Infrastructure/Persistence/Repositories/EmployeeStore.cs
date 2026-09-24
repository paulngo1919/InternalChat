using InternalChat.Application.Abstractions;
using InternalChat.Domain.Employees;
using Microsoft.EntityFrameworkCore;

namespace InternalChat.Infrastructure.Persistence.Repositories;

/// <summary>
/// EF Core implementation of the employee write side (T065).
/// </summary>
/// <remarks>
/// Tracked queries, unlike <see cref="EmployeeDirectory"/>'s: the point here is to load the
/// aggregate, call a method on it, and let the unit of work notice. Reading with
/// <c>AsNoTracking</c> and then saving would silently do nothing, which for a deactivation means an
/// employee who has left keeps their access and no error is raised anywhere.
/// </remarks>
public sealed class EmployeeStore : IEmployeeStore
{
    private readonly ChatDbContext _context;

    /// <summary>Creates the store.</summary>
    public EmployeeStore(ChatDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
    }

    /// <inheritdoc />
    public async Task<Employee?> FindBySubjectAsync(
        string externalSubject,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(externalSubject);

        return await _context.Employees
            .FirstOrDefaultAsync(e => e.ExternalSubject == externalSubject, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Employee>> FindManyAsync(
        IReadOnlyCollection<Guid> employeeIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(employeeIds);

        if (employeeIds.Count == 0)
        {
            // Short-circuited rather than sent as `WHERE id = ANY('{}')`. The query is harmless but
            // the round trip is not free, and every caller treats an empty request as an empty
            // answer anyway.
            return [];
        }

        // Distinct first: a caller that named the same employee twice would otherwise get two rows,
        // and CreateConversation compares the returned count against the requested count to detect
        // an unknown id — duplicates would make that comparison pass for the wrong reason.
        Guid[] distinct = [.. employeeIds.Distinct()];

        return await _context.Employees
            .Where(e => distinct.Contains(e.Id))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task AddAsync(Employee employee, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(employee);

        await _context.Employees.AddAsync(employee, cancellationToken).ConfigureAwait(false);
    }
}
