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
    public async Task AddAsync(Employee employee, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(employee);

        await _context.Employees.AddAsync(employee, cancellationToken).ConfigureAwait(false);
    }
}
