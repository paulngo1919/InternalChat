namespace InternalChat.Domain.Employees;

/// <summary>
/// Availability an employee shows to colleagues (FR-017).
/// </summary>
/// <remarks>
/// <para>
/// Distinct from <see cref="EmployeeStatus"/> and easy to confuse with it. Status is a directory
/// fact that governs access; presence is a transient hint that governs nothing. An employee who is
/// <see cref="Offline"/> still has full access the moment they reconnect.
/// </para>
/// <para>
/// Presence lives in Redis with a TTL, never in PostgreSQL (data-model.md, Redis keyspace). It is
/// derived state that must expire on its own — a client that crashes must not leave a permanent
/// "online" marker, and there is no cleanup job to depend on.
/// </para>
/// </remarks>
public enum Presence
{
    /// <summary>
    /// Not connected. Inferred from the absence of a connection, never asserted by a client
    /// (contracts/signalr-hub.md) — a client claiming to be offline while still receiving would
    /// make the indicator a lie.
    /// </summary>
    Offline,

    /// <summary>Connected and active.</summary>
    Online,

    /// <summary>Connected but idle.</summary>
    Away,

    /// <summary>
    /// Connected and asking not to be interrupted. Suppresses notification delivery (FR-037) but
    /// never suppresses the message itself or the unread count.
    /// </summary>
    DoNotDisturb,
}
