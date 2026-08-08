namespace InternalChat.Application.Abstractions;

/// <summary>
/// Cache access. Redis is a cache and a transient coordination store only — never authoritative.
/// </summary>
/// <remarks>
/// <para>
/// Constitution Principle VII: "Every cached entry MUST have an explicit TTL. Unbounded cache
/// entries are prohibited." That rule is enforced by this interface's shape rather than by
/// review — <see cref="SetAsync"/> takes a required <see cref="TimeSpan"/> and there is no
/// overload without one, so a caller cannot write an immortal key even by accident.
/// </para>
/// <para>
/// Losing the entire keyspace must cost latency and transient state only, never data and never
/// a wrong authorization decision (SC-024). Nothing may be read from here that cannot be
/// rebuilt from PostgreSQL.
/// </para>
/// </remarks>
public interface ICacheStore
{
    /// <summary>Reads a cached value, or <c>null</c> when absent or expired.</summary>
    Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
        where T : class;

    /// <summary>
    /// Writes a value with a mandatory expiry.
    /// </summary>
    /// <param name="timeToLive">
    /// Required. For authorization data this MUST NOT exceed 60 seconds (Principle VII); the
    /// membership cache uses 30 seconds so the 5-minute revocation budget in FR-003 keeps margin
    /// for identity-provider propagation.
    /// </param>
    Task SetAsync<T>(string key, T value, TimeSpan timeToLive, CancellationToken cancellationToken = default)
        where T : class;

    /// <summary>
    /// Invalidates a single key. Must be called from inside the use case that mutated the
    /// underlying data, not from a background sweep (Principle VII).
    /// </summary>
    Task RemoveAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Invalidates every key under a prefix — used when a conversation's membership changes and
    /// all of its cached authorization entries must go at once.
    /// </summary>
    Task RemoveByPrefixAsync(string keyPrefix, CancellationToken cancellationToken = default);
}
