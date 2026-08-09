using System.Text.Json;
using InternalChat.Application.Abstractions;
using InternalChat.Infrastructure.Caching;
using StackExchange.Redis;

namespace InternalChat.Infrastructure.Identity;

/// <summary>
/// Records the sessions the platform has seen, so FR-005 can list and end them.
/// </summary>
/// <remarks>
/// <para>
/// One Redis hash per employee: field is the session id, value is what the list needs to show. A
/// hash rather than one key per session because <c>GET /me/sessions</c> would otherwise have to
/// <c>SCAN</c> the keyspace, which is O(total keys) on a shared instance — an unbounded cost on a
/// user-facing endpoint to answer a question about at most a handful of rows.
/// </para>
/// <para>
/// <b>Expiry is logical as well as physical.</b> Redis expires the whole hash, and any touch
/// refreshes it — so one live session would otherwise keep a colleague's long-abandoned session
/// listed forever. Reads therefore also drop fields whose last-seen time is older than the TTL, and
/// delete them. Without that, the list would tell an employee checking for an unrecognised device
/// exactly the wrong thing.
/// </para>
/// </remarks>
public sealed class SessionRegistry : ISessionRegistry
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly IConnectionMultiplexer _redis;
    private readonly RedisKeyspace _keyspace;

    /// <summary>Creates the registry.</summary>
    public SessionRegistry(IConnectionMultiplexer redis, RedisKeyspace keyspace)
    {
        ArgumentNullException.ThrowIfNull(redis);
        ArgumentNullException.ThrowIfNull(keyspace);

        _redis = redis;
        _keyspace = keyspace;
    }

    /// <inheritdoc />
    public async Task<bool> TouchAsync(
        Guid employeeId,
        string sessionId,
        string userAgent,
        TimeSpan timeToLive,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeToLive, TimeSpan.Zero);
        cancellationToken.ThrowIfCancellationRequested();

        IDatabase database = _redis.GetDatabase();
        RedisKey key = _keyspace.Qualify(RedisKeyspace.SessionsKey(employeeId));

        RedisValue existing = await database.HashGetAsync(key, sessionId).ConfigureAwait(false);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        StoredSession session = Deserialize(existing) is { } previous
            ? previous with { LastSeenAt = now }

            // The user agent is captured once, at first sight, and never updated. It exists so an
            // employee can recognise a device; rewriting it on every request would mean a hijacked
            // session displayed the attacker's browser under the same entry the employee had
            // already decided looked familiar.
            : new StoredSession(userAgent, now, now);

        bool isNew = existing.IsNullOrEmpty;

        await database
            .HashSetAsync(key, sessionId, JsonSerializer.Serialize(session, SerializerOptions))
            .ConfigureAwait(false);

        await database.KeyExpireAsync(key, timeToLive).ConfigureAwait(false);

        return isNew;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SessionDescriptor>> ListAsync(
        Guid employeeId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IDatabase database = _redis.GetDatabase();
        RedisKey key = _keyspace.Qualify(RedisKeyspace.SessionsKey(employeeId));

        HashEntry[] entries = await database.HashGetAllAsync(key).ConfigureAwait(false);

        TimeSpan? remaining = await database.KeyTimeToLiveAsync(key).ConfigureAwait(false);
        DateTimeOffset cutoff = DateTimeOffset.UtcNow - (remaining ?? TimeSpan.FromHours(1));

        List<SessionDescriptor> live = [];
        List<RedisValue> stale = [];

        foreach (HashEntry entry in entries)
        {
            StoredSession? session = Deserialize(entry.Value);

            if (session is null || session.LastSeenAt < cutoff)
            {
                stale.Add(entry.Name);
                continue;
            }

            live.Add(new SessionDescriptor(
                entry.Name.ToString(),
                session.UserAgent,
                session.CreatedAt,
                session.LastSeenAt));
        }

        if (stale.Count > 0)
        {
            await database.HashDeleteAsync(key, [.. stale]).ConfigureAwait(false);
        }

        return [.. live.OrderByDescending(s => s.LastSeenAt)];
    }

    /// <inheritdoc />
    public async Task<bool> RemoveAsync(
        Guid employeeId,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        cancellationToken.ThrowIfCancellationRequested();

        return await _redis.GetDatabase()
            .HashDeleteAsync(_keyspace.Qualify(RedisKeyspace.SessionsKey(employeeId)), sessionId)
            .ConfigureAwait(false);
    }

    /// <summary>An entry that no longer deserializes is treated as stale rather than as an error.</summary>
    private static StoredSession? Deserialize(RedisValue value)
    {
        if (value.IsNullOrEmpty)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<StoredSession>(value.ToString(), SerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record StoredSession(string UserAgent, DateTimeOffset CreatedAt, DateTimeOffset LastSeenAt);
}
