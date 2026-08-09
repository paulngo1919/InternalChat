using Microsoft.Extensions.Options;

namespace InternalChat.Infrastructure.Caching;

/// <summary>
/// Builds the environment-namespaced Redis keys from data-model.md.
/// </summary>
/// <remarks>
/// <para>
/// Every key is <c>internalchat:{env}:{entity}:{id}</c>. The environment segment is not decoration:
/// it is what stops a staging instance pointed at the wrong Redis from reading production's
/// authorization decisions out of a shared keyspace.
/// </para>
/// <para>
/// <see cref="RedisCacheStore"/> applies the same prefix internally, so callers that go through
/// <c>ICacheStore</c> never see this. It exists for the stores that need Redis data structures the
/// cache interface deliberately does not expose — a hash for the session list, for one — and which
/// therefore hold a multiplexer directly.
/// </para>
/// </remarks>
public sealed class RedisKeyspace
{
    private readonly string _prefix;

    /// <summary>Creates the keyspace for the configured environment.</summary>
    public RedisKeyspace(IOptions<RedisCacheOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _prefix = $"internalchat:{options.Value.Environment}:";
    }

    /// <summary>Prefixes a logical key with the environment namespace.</summary>
    public string Qualify(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return _prefix + key;
    }

    /// <summary>
    /// Revocation entry for one session — sign-out and back-channel logout.
    /// </summary>
    public static string RevokedSessionKey(string sessionId) => $"revoked:sid:{sessionId}";

    /// <summary>
    /// Revocation entry for every session belonging to a subject — deactivation (FR-003).
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="RevokedSessionKey"/> because deactivation cannot enumerate the
    /// sessions it needs to end: they may live on devices this process has never served. Keying by
    /// subject makes the check a single lookup regardless of how many sessions exist.
    /// </remarks>
    public static string RevokedSubjectKey(string subject) => $"revoked:sub:{subject}";

    /// <summary>The hash holding one employee's known sessions (FR-005).</summary>
    public static string SessionsKey(Guid employeeId) => $"sessions:{employeeId}";

    /// <summary>Authorization cache entry for one membership (research.md D6).</summary>
    public static string MembershipKey(Guid conversationId, Guid employeeId) =>
        $"membership:{conversationId}:{employeeId}";

    /// <summary>Prefix covering every cached membership of one conversation.</summary>
    /// <remarks>
    /// Removing a member has to drop that conversation's cached decisions, and the removal use case
    /// knows the conversation, not the set of employees whose entries exist. Invalidating by prefix
    /// is what makes the invalidation complete rather than best-effort.
    /// </remarks>
    public static string MembershipPrefix(Guid conversationId) => $"membership:{conversationId}:";
}
