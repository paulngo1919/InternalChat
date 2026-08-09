using InternalChat.Application.Abstractions;
using InternalChat.Application.Behaviors;
using InternalChat.Domain.Common;
using InternalChat.Domain.Employees;

namespace InternalChat.Application.Directory;

/// <summary>What the corporate directory reported.</summary>
public enum DirectoryChange
{
    /// <summary>The employee exists, with these attributes. Creates or updates.</summary>
    Upserted,

    /// <summary>The employee has left. Access must end within five minutes (FR-003).</summary>
    Deactivated,

    /// <summary>A previously deactivated employee is back.</summary>
    Reactivated,
}

/// <summary>Applies one directory change to the employee projection.</summary>
/// <param name="ExternalSubject">Keycloak <c>sub</c>. The key — internal ids mean nothing upstream.</param>
/// <param name="Change">What happened.</param>
/// <param name="DisplayName">Used only by <see cref="DirectoryChange.Upserted"/>.</param>
/// <param name="Email">Used only by <see cref="DirectoryChange.Upserted"/>.</param>
/// <param name="AvatarUrl">Used only by <see cref="DirectoryChange.Upserted"/>.</param>
public sealed record SyncEmployee(
    string ExternalSubject,
    DirectoryChange Change,
    string? DisplayName,
    string? Email,
    string? AvatarUrl) : IAuditableRequest
{
    /// <inheritdoc />
    /// <remarks>
    /// Audited because FR-006 lists permission changes, and a deactivation is the largest one this
    /// platform ever applies: it ends every session an employee has. The detail carries the
    /// subject and the change, never the attributes — a name and an email address in an audit log
    /// retained for a year is personal data nobody asked to keep there.
    /// </remarks>
    public AuditEntry ToAuditEntry(AuditOutcome outcome) => new(
        "directory.employee.synced",

        // No actor. The corporate directory made this change, not a person using this platform,
        // and naming one would be a fabrication in a record meant to answer "who did this".
        ActorId: null,
        "employee",
        SubjectId: null,
        SourceIp: null,
        outcome,
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["externalSubject"] = ExternalSubject,
            ["change"] = Change.ToString(),
        });
}

/// <summary>Outcome of a directory sync.</summary>
/// <param name="EmployeeId">The internal id the subject resolved to.</param>
/// <param name="WasCreated">True when this call created the employee row.</param>
public sealed record SyncEmployeeResult(Guid EmployeeId, bool WasCreated);

/// <summary>
/// Projects a directory change onto the <c>employee</c> table and ends access when required.
/// </summary>
/// <remarks>
/// <para>
/// <b>Idempotent, because it has to be.</b> Delivery is at-least-once (Principle VI) and the
/// consumer host's deduplication covers a redelivery of the same message — but the directory can
/// legitimately send the same deactivation twice as two different messages. The domain handles
/// that: <see cref="Employee.Deactivate"/> refuses to move <c>DeactivatedAt</c> once set, because
/// that timestamp is what an auditor reads to establish when access ended (SC-021).
/// </para>
/// <para>
/// <b>The revocation write happens after the transaction, deliberately.</b> Redis is not
/// transactional with PostgreSQL, so one of the two has to go second. Revoking first would mean a
/// failed commit left an employee locked out of a platform that still considers them active —
/// recoverable only by waiting out the entry. Committing first means a crash in between leaves an
/// employee marked deactivated with a live token, which the access gate refuses anyway on its next
/// request because it re-reads the employee row. The database is the source of truth; the
/// revocation set is an accelerator (Principle VII).
/// </para>
/// </remarks>
public sealed class SyncEmployeeHandler : IUseCase<SyncEmployee, SyncEmployeeResult>
{
    private readonly IEmployeeStore _employees;
    private readonly IRevocationStore _revocations;
    private readonly IClock _clock;

    /// <summary>Creates the handler.</summary>
    public SyncEmployeeHandler(IEmployeeStore employees, IRevocationStore revocations, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(employees);
        ArgumentNullException.ThrowIfNull(revocations);
        ArgumentNullException.ThrowIfNull(clock);

        _employees = employees;
        _revocations = revocations;
        _clock = clock;
    }

    /// <summary>
    /// How long a revocation entry must survive.
    /// </summary>
    /// <remarks>
    /// The realm's access-token lifetime is 300 seconds; this is deliberately longer. An entry that
    /// expired before the token it revokes would let a departed employee's token start working
    /// again — silently, and with nothing in the logs to mark the moment.
    /// </remarks>
    public static readonly TimeSpan RevocationLifetime = TimeSpan.FromMinutes(15);

    /// <inheritdoc />
    public async Task<SyncEmployeeResult> HandleAsync(
        SyncEmployee request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        Employee? employee = await _employees
            .FindBySubjectAsync(request.ExternalSubject, cancellationToken)
            .ConfigureAwait(false);

        bool created = false;

        if (employee is null)
        {
            if (request.Change != DirectoryChange.Upserted)
            {
                // A deactivation for somebody we have never seen. Nothing to deactivate, and
                // creating a row so it can immediately be deactivated would invent an employee who
                // never had access. Still revoked below, because a token for that subject may well
                // be in circulation even though no row exists.
                await RevokeAsync(request, cancellationToken).ConfigureAwait(false);
                return new SyncEmployeeResult(Guid.Empty, WasCreated: false);
            }

            employee = Employee.Project(
                Guid.CreateVersion7(),
                request.ExternalSubject,
                Require(request.DisplayName, nameof(request.DisplayName)),
                Require(request.Email, nameof(request.Email)),
                _clock,
                request.AvatarUrl);

            await _employees.AddAsync(employee, cancellationToken).ConfigureAwait(false);
            created = true;
        }

        switch (request.Change)
        {
            case DirectoryChange.Upserted when !created:
                // Attributes only. UpdateDirectoryAttributes cannot change Status by construction,
                // which is what keeps a rename from ever being able to restore access.
                employee.UpdateDirectoryAttributes(
                    Require(request.DisplayName, nameof(request.DisplayName)),
                    Require(request.Email, nameof(request.Email)),
                    request.AvatarUrl,
                    _clock);
                break;

            case DirectoryChange.Deactivated:
                employee.Deactivate(_clock);
                break;

            case DirectoryChange.Reactivated:
                employee.Reactivate(_clock);
                break;

            case DirectoryChange.Upserted:
            default:
                break;
        }

        await RevokeAsync(request, cancellationToken).ConfigureAwait(false);

        return new SyncEmployeeResult(employee.Id, created);
    }

    /// <summary>Ends or restores token acceptance for the subject.</summary>
    private async Task RevokeAsync(SyncEmployee request, CancellationToken cancellationToken)
    {
        switch (request.Change)
        {
            case DirectoryChange.Deactivated:
                await _revocations
                    .RevokeSubjectAsync(request.ExternalSubject, RevocationLifetime, cancellationToken)
                    .ConfigureAwait(false);
                break;

            case DirectoryChange.Reactivated:
                // Cleared, or a rehired employee would be refused until the entry expired — and
                // would see a working sign-in followed by 401s, which reads as a broken platform.
                await _revocations
                    .ClearSubjectAsync(request.ExternalSubject, cancellationToken)
                    .ConfigureAwait(false);
                break;

            case DirectoryChange.Upserted:
            default:
                break;
        }
    }

    private static string Require(string? value, string field) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException(
                $"A directory upsert carried no {field}. An employee row cannot be projected "
                + "without it, and inventing a placeholder would put a fabricated name in front of "
                + "colleagues.")
            : value;
}
