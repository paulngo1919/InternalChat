namespace InternalChat.Domain.Employees;

/// <summary>
/// Directory lifecycle state. The platform never owns this — it is a projection of the corporate
/// directory (FR-001).
/// </summary>
/// <remarks>
/// Only two states, and only two transitions: <c>Active → Deactivated</c> on directory sync, and
/// <c>Deactivated → Active</c> on rehire (data-model.md). There is deliberately no "suspended" or
/// "pending" — every additional state is another branch every authorization decision has to get
/// right, and the directory is the authority on anything finer-grained.
/// </remarks>
public enum EmployeeStatus
{
    /// <summary>Employed and permitted to use the platform.</summary>
    Active,

    /// <summary>
    /// No longer employed. Retains authored history — deactivation is not deletion (FR-001) —
    /// but grants no access and cannot be added to a conversation.
    /// </summary>
    Deactivated,
}
