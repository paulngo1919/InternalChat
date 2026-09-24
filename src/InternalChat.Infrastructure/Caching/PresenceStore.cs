using System.Globalization;
using InternalChat.Application.Abstractions;
using StackExchange.Redis;

namespace InternalChat.Infrastructure.Caching;

/// <summary>
/// T099 — presence and typing over Redis, expiring by TTL rather than by cleanup.
/// </summary>
/// <remarks>
/// <para>
/// Written against <see cref="IConnectionMultiplexer"/> rather than through
/// <see cref="ICacheStore"/>. The cache store serialises values as JSON and is the right tool for
/// cached projections; typing state is a set with a TTL, and expressing a set as a serialised list
/// would turn "add one member" into read-modify-write — which loses concurrent typers whenever two
/// people start at once.
/// </para>
/// <para>
/// <b>Every write sets a TTL, and no code path deletes on disconnect.</b> A crashed client cannot
/// send anything, so any design that depends on an explicit clear leaves a permanent "typing…" that
/// nobody can remove. Expiry is the mechanism (Principle VII, data-model.md), which also means
/// losing the whole keyspace costs only a few seconds of stale indicators.
/// </para>
/// </remarks>
public sealed class PresenceStore : IPresenceStore
{
    /// <summary>
    /// How long a presence assertion survives without a refresh.
    /// </summary>
    /// <remarks>
    /// 60 seconds, matching data-model.md. Long enough that a client refreshing on a comfortable
    /// interval never flickers offline, short enough that a closed laptop stops showing as online
    /// within a minute rather than for the rest of the afternoon.
    /// </remarks>
    private static readonly TimeSpan PresenceLifetime = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long a typing marker survives.
    /// </summary>
    /// <remarks>
    /// 10 seconds, matching data-model.md. This is what a stalled client's indicator costs everyone
    /// else, so it is deliberately near the shortest value that survives normal typing pauses.
    /// </remarks>
    private static readonly TimeSpan TypingLifetime = TimeSpan.FromSeconds(10);

    private readonly IConnectionMultiplexer _redis;
    private readonly RedisKeyspace _keyspace;

    /// <summary>Creates the store.</summary>
    public PresenceStore(IConnectionMultiplexer redis, RedisKeyspace keyspace)
    {
        ArgumentNullException.ThrowIfNull(redis);
        ArgumentNullException.ThrowIfNull(keyspace);

        _redis = redis;
        _keyspace = keyspace;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <see cref="PresenceState.Offline"/> deletes the key rather than storing the value. Storing it
    /// would make "offline" outlive the client that asserted it by the full TTL, so a colleague who
    /// signed out and back in would keep showing as offline for a minute.
    /// </remarks>
    public async Task SetPresenceAsync(
        Guid employeeId,
        PresenceState state,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IDatabase database = _redis.GetDatabase();
        RedisKey key = _keyspace.Qualify(PresenceKey(employeeId));

        if (state == PresenceState.Offline)
        {
            await database.KeyDeleteAsync(key).ConfigureAwait(false);
            return;
        }

        await database
            .StringSetAsync(key, state.ToString(), PresenceLifetime)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<Guid, PresenceState>> GetPresenceAsync(
        IReadOnlyCollection<Guid> employeeIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(employeeIds);
        cancellationToken.ThrowIfCancellationRequested();

        if (employeeIds.Count == 0)
        {
            return new Dictionary<Guid, PresenceState>();
        }

        Guid[] distinct = [.. employeeIds.Distinct()];

        IDatabase database = _redis.GetDatabase();

        // One MGET rather than one GET per employee. A member list of fifty would otherwise be fifty
        // sequential round trips on the path of a single screen render.
        RedisKey[] keys = [.. distinct.Select(id => (RedisKey)_keyspace.Qualify(PresenceKey(id)))];

        RedisValue[] values = await database.StringGetAsync(keys).ConfigureAwait(false);

        Dictionary<Guid, PresenceState> presence = new(distinct.Length);

        for (int i = 0; i < distinct.Length; i++)
        {
            // A missing or unparseable value is Offline. Absence IS the offline signal, and an
            // unreadable entry is treated the same way rather than thrown — a corrupt presence value
            // must not fail the screen that happened to read it.
            presence[distinct[i]] =
                values[i].HasValue && Enum.TryParse(values[i].ToString(), out PresenceState state)
                    ? state
                    : PresenceState.Offline;
        }

        return presence;
    }

    /// <inheritdoc />
    /// <remarks>
    /// The TTL is reset on the whole set on every assertion, which is the intended behaviour: any
    /// active typist keeps the set alive, and members who stopped are removed individually below or
    /// vanish with the set. The alternative — a TTL per member — is not something a Redis set
    /// supports, and the imprecision it causes lasts at most ten seconds.
    /// </remarks>
    public async Task StartTypingAsync(
        Guid conversationId,
        Guid employeeId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IDatabase database = _redis.GetDatabase();
        RedisKey key = _keyspace.Qualify(TypingKey(conversationId));

        await database.SetAddAsync(key, employeeId.ToString()).ConfigureAwait(false);
        await database.KeyExpireAsync(key, TypingLifetime).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task StopTypingAsync(
        Guid conversationId,
        Guid employeeId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await _redis.GetDatabase()
            .SetRemoveAsync(_keyspace.Qualify(TypingKey(conversationId)), employeeId.ToString())
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Guid>> GetTypingAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        RedisValue[] members = await _redis.GetDatabase()
            .SetMembersAsync(_keyspace.Qualify(TypingKey(conversationId)))
            .ConfigureAwait(false);

        List<Guid> typing = [];

        foreach (RedisValue member in members)
        {
            if (Guid.TryParse(member.ToString(), out Guid employeeId))
            {
                typing.Add(employeeId);
            }
        }

        return typing;
    }

    private static string PresenceKey(Guid employeeId) =>
        string.Create(CultureInfo.InvariantCulture, $"presence:{employeeId}");

    private static string TypingKey(Guid conversationId) =>
        string.Create(CultureInfo.InvariantCulture, $"typing:{conversationId}");
}
