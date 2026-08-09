using InternalChat.Domain.Common;

namespace InternalChat.Domain.Employees;

/// <summary>
/// An employee, projected from the corporate directory.
/// </summary>
/// <remarks>
/// <para>
/// FR-001: the platform never owns employee lifecycle and never stores a password. Everything here
/// arrives from Keycloak via directory sync, and the only mutations this type permits are the ones
/// the directory can drive. There is no <c>Delete</c>: SC-021 requires an administrator to answer
/// "who had access on this date" a year later, and a deleted row cannot answer anything.
/// </para>
/// <para>
/// <b>The invariant that matters</b> is <see cref="EnsureCanJoinConversation"/>. A deactivated
/// employee must not be addable to a conversation — the natural place to check that is the use
/// case, but a use case is one code path among several and each new one has to remember. Putting
/// it on the entity means every path that adds a member goes through the same gate.
/// </para>
/// </remarks>
public sealed class Employee : Entity<Guid>
{
    private Employee(
        Guid id,
        string externalSubject,
        string displayName,
        string email,
        string? avatarUrl,
        EmployeeStatus status,
        DateTimeOffset createdAt)
        : base(id)
    {
        ExternalSubject = externalSubject;
        DisplayName = displayName;
        Email = email;
        AvatarUrl = avatarUrl;
        Status = status;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
    }

    /// <summary>
    /// Keycloak <c>sub</c> claim. The join between a token and a row in this table.
    /// </summary>
    /// <remarks>
    /// Deliberately not the primary key. The internal id stays stable if the identity provider is
    /// ever replaced or a subject is reissued, which matters because message authorship and
    /// membership rows reference it and must survive that.
    /// </remarks>
    public string ExternalSubject { get; private set; }

    /// <summary>Name shown to colleagues.</summary>
    public string DisplayName { get; private set; }

    /// <summary>Corporate address. Unique, case-insensitive (<c>citext</c>).</summary>
    public string Email { get; private set; }

    /// <summary>Avatar location, when the directory supplies one.</summary>
    public string? AvatarUrl { get; private set; }

    /// <summary>Directory lifecycle state.</summary>
    public EmployeeStatus Status { get; private set; }

    /// <summary>When deactivation happened, for audit reconstruction (SC-021).</summary>
    public DateTimeOffset? DeactivatedAt { get; private set; }

    /// <summary>When the row was first projected.</summary>
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>When the row last changed.</summary>
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>True when the employee may sign in and be added to conversations.</summary>
    public bool IsActive => Status == EmployeeStatus.Active;

    /// <summary>Projects a new employee from the directory.</summary>
    /// <exception cref="ArgumentException">A required directory attribute is missing.</exception>
    public static Employee Project(
        Guid id,
        string externalSubject,
        string displayName,
        string email,
        IClock clock,
        string? avatarUrl = null)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentException.ThrowIfNullOrWhiteSpace(externalSubject);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);

        return new Employee(
            id,
            externalSubject.Trim(),
            displayName.Trim(),
            email.Trim(),
            avatarUrl,
            EmployeeStatus.Active,
            clock.UtcNow);
    }

    /// <summary>
    /// Applies changed directory attributes. Never changes <see cref="Status"/>.
    /// </summary>
    /// <remarks>
    /// Status is moved only by <see cref="Deactivate"/> and <see cref="Reactivate"/>, which record
    /// <see cref="DeactivatedAt"/> and raise events. Letting an attribute sync also flip status
    /// would make a rename and a revocation the same operation, and revocation is the one that has
    /// a five-minute budget attached to it (FR-003).
    /// </remarks>
    public void UpdateDirectoryAttributes(string displayName, string email, string? avatarUrl, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);

        DisplayName = displayName.Trim();
        Email = email.Trim();
        AvatarUrl = avatarUrl;
        UpdatedAt = clock.UtcNow;
    }

    /// <summary>
    /// Marks the employee as no longer employed.
    /// </summary>
    /// <remarks>
    /// Idempotent. Directory sync is an at-least-once consumer (Principle VI), so the same
    /// deactivation can arrive twice; the second must not move <see cref="DeactivatedAt"/> forward,
    /// because that timestamp is what an auditor reads to establish when access actually ended.
    /// </remarks>
    public void Deactivate(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        if (Status == EmployeeStatus.Deactivated)
        {
            return;
        }

        Status = EmployeeStatus.Deactivated;
        DeactivatedAt = clock.UtcNow;
        UpdatedAt = clock.UtcNow;

        // Consumed by the revocation path — this is what closes an already-open session inside
        // the five minutes FR-003 allows.
        Raise(new EmployeeDeactivated(Guid.CreateVersion7(), clock.UtcNow, Id, ExternalSubject));
    }

    /// <summary>Restores access on rehire.</summary>
    public void Reactivate(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        if (Status == EmployeeStatus.Active)
        {
            return;
        }

        Status = EmployeeStatus.Active;
        DeactivatedAt = null;
        UpdatedAt = clock.UtcNow;

        Raise(new EmployeeReactivated(Guid.CreateVersion7(), clock.UtcNow, Id, ExternalSubject));
    }

    /// <summary>
    /// Throws unless this employee may be added to a conversation.
    /// </summary>
    /// <exception cref="DeactivatedEmployeeException">The employee is deactivated.</exception>
    public void EnsureCanJoinConversation()
    {
        if (Status == EmployeeStatus.Deactivated)
        {
            throw new DeactivatedEmployeeException(Id);
        }
    }
}

/// <summary>Raised when the directory reports an employee has left.</summary>
/// <remarks>
/// Carries <see cref="ExternalSubject"/> as well as the internal id because the revocation set is
/// keyed by the token's <c>sub</c> claim — the consumer would otherwise need a database round trip
/// to revoke, on the path with the tightest deadline in the system.
/// </remarks>
public sealed record EmployeeDeactivated(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid EmployeeId,
    string ExternalSubject) : DomainEvent(EventId, OccurredAt)
{
    /// <inheritdoc />
    public override string EventType => "chat.employee.deactivated.v1";
}

/// <summary>Raised when a previously deactivated employee is rehired.</summary>
public sealed record EmployeeReactivated(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid EmployeeId,
    string ExternalSubject) : DomainEvent(EventId, OccurredAt)
{
    /// <inheritdoc />
    public override string EventType => "chat.employee.reactivated.v1";
}

/// <summary>Thrown when a deactivated employee is offered a conversation membership.</summary>
public sealed class DeactivatedEmployeeException : InvalidOperationException
{
    /// <summary>Creates the exception.</summary>
    public DeactivatedEmployeeException(Guid employeeId)
        : base($"Employee {employeeId} is deactivated and cannot be added to a conversation.") =>
        EmployeeId = employeeId;

    /// <summary>Creates the exception.</summary>
    public DeactivatedEmployeeException()
    {
    }

    /// <summary>Creates the exception.</summary>
    public DeactivatedEmployeeException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public DeactivatedEmployeeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The employee that was refused.</summary>
    public Guid EmployeeId { get; }
}
