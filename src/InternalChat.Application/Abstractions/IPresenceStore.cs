namespace InternalChat.Application.Abstractions;

/// <summary>What an employee is currently doing (FR-017).</summary>
/// <remarks>
/// <c>Offline</c> is inferred from the absence of a presence key, never asserted by a client. A
/// client that could declare itself offline would also be able to declare a colleague's browser
/// crash indistinguishable from a deliberate sign-out — and, more practically, a crashed client
/// never gets to send anything at all, so absence has to be the signal either way.
/// </remarks>
public enum PresenceState
{
    /// <summary>No live key. The default, and what a crashed or closed client decays to.</summary>
    Offline = 0,

    /// <summary>Connected and interacting.</summary>
    Online = 1,

    /// <summary>Connected but idle.</summary>
    Away = 2,

    /// <summary>Connected and asking not to be interrupted.</summary>
    DoNotDisturb = 3,
}

/// <summary>
/// Presence and typing state, held only in the cache tier.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing here is authoritative and nothing here is cleaned up explicitly.</b> Every key carries
/// a TTL and expiry is the only removal mechanism (data-model.md, Redis keyspace). That is the
/// design, not a shortcut: a client that crashes, loses its network, or has its laptop lid closed
/// never sends a "stopped typing" or "went offline" message, so any scheme relying on an explicit
/// clear leaves a permanent "Chi is typing…" that nobody can remove.
/// </para>
/// <para>
/// Losing the entire keyspace therefore costs transient state and nothing else (SC-024) — presence
/// reverts to offline and typing indicators disappear, both of which self-correct within seconds.
/// </para>
/// </remarks>
public interface IPresenceStore
{
    /// <summary>Records an employee's presence, refreshing its TTL.</summary>
    Task SetPresenceAsync(
        Guid employeeId,
        PresenceState state,
        CancellationToken cancellationToken = default);

    /// <summary>Reads presence for several employees at once.</summary>
    /// <remarks>
    /// A batch, because the caller is rendering a conversation list or a member list. One round trip
    /// per employee would put fifty of them on the path of a single screen.
    /// </remarks>
    Task<IReadOnlyDictionary<Guid, PresenceState>> GetPresenceAsync(
        IReadOnlyCollection<Guid> employeeIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks an employee as typing in a conversation, for the next few seconds.
    /// </summary>
    /// <remarks>
    /// Deliberately fire-and-forget and deliberately short-lived. The client re-asserts it while the
    /// person keeps typing; stopping is the absence of a re-assertion rather than an event, so a
    /// dropped connection resolves itself.
    /// </remarks>
    Task StartTypingAsync(
        Guid conversationId,
        Guid employeeId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears a typing marker early.
    /// </summary>
    /// <remarks>
    /// An optimisation over waiting for the TTL, for the case where someone deliberately stops — a
    /// sent message, a cleared composer. Correctness does not depend on it ever being called.
    /// </remarks>
    Task StopTypingAsync(
        Guid conversationId,
        Guid employeeId,
        CancellationToken cancellationToken = default);

    /// <summary>Who is currently typing in a conversation.</summary>
    Task<IReadOnlyList<Guid>> GetTypingAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default);
}
