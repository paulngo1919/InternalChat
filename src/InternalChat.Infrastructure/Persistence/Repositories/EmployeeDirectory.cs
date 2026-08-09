using InternalChat.Application.Abstractions;
using InternalChat.Domain.Employees;
using Microsoft.EntityFrameworkCore;

namespace InternalChat.Infrastructure.Persistence.Repositories;

/// <summary>
/// Reads the employee directory projection from PostgreSQL (T071).
/// </summary>
/// <remarks>
/// Read-only, matching <see cref="IEmployeeDirectory"/>: employee rows are written by directory
/// sync alone (FR-001), and a write path here would make the platform a second source of truth
/// alongside Keycloak.
/// </remarks>
public sealed class EmployeeDirectory : IEmployeeDirectory
{
    /// <summary>Hard ceiling on a directory page, regardless of what was asked for.</summary>
    /// <remarks>
    /// Principle V forbids an unbounded query. The OpenAPI contract caps <c>limit</c> at 50; this
    /// enforces the same number below the endpoint, so a second caller added later cannot ask for
    /// ten thousand rows by not knowing about the contract.
    /// </remarks>
    public const int MaximumSearchResults = 50;

    private readonly ChatDbContext _context;

    /// <summary>Creates the directory reader.</summary>
    public EmployeeDirectory(ChatDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
    }

    /// <inheritdoc />
    public async Task<EmployeeProfile?> FindBySubjectAsync(
        string externalSubject,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(externalSubject);

        return await _context.Employees
            .AsNoTracking()
            .Where(e => e.ExternalSubject == externalSubject)
            .Select(e => new EmployeeProfile(
                e.Id,
                e.ExternalSubject,
                e.DisplayName,
                e.Email,
                e.AvatarUrl,
                e.Status))
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<EmployeeProfile>> SearchAsync(
        string query,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        int take = Math.Clamp(limit, 1, MaximumSearchResults);
        string trimmed = query.Trim();

        // ILIKE with a leading wildcard, served by the GIN trigram index on display_name
        // (EmployeeConfiguration). A B-tree cannot answer this — every keystroke of the people
        // picker would be a sequential scan over 10,000 rows.
        string pattern = $"%{Escape(trimmed)}%";

        return await _context.Employees
            .AsNoTracking()
            .Where(e => e.Status == EmployeeStatus.Active)
            .Where(e => EF.Functions.ILike(e.DisplayName, pattern, @"\")
                || EF.Functions.ILike(e.Email, pattern, @"\"))

            // Deterministic order. Without the tiebreak, two employees sharing a display name
            // would swap places between calls and a paging client would show one twice.
            .OrderBy(e => e.DisplayName)
            .ThenBy(e => e.Id)
            .Take(take)
            .Select(e => new EmployeeProfile(
                e.Id,
                e.ExternalSubject,
                e.DisplayName,
                e.Email,
                e.AvatarUrl,
                e.Status))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Neutralises LIKE metacharacters in user input.
    /// </summary>
    /// <remarks>
    /// Not an injection defence — the value is parameterised — but a correctness one. A colleague
    /// searching for "100%" would otherwise match every employee, and someone searching "_" would
    /// match all of them too.
    /// </remarks>
    private static string Escape(string value) => value
        .Replace(@"\", @"\\", StringComparison.Ordinal)
        .Replace("%", @"\%", StringComparison.Ordinal)
        .Replace("_", @"\_", StringComparison.Ordinal);
}
