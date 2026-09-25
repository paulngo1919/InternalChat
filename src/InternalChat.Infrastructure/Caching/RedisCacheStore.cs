using System.Text.Json;
using InternalChat.Application.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace InternalChat.Infrastructure.Caching;

/// <summary>
/// Redis implementation of <see cref="ICacheStore"/>.
/// </summary>
/// <remarks>
/// <para>
/// Constitution Principle VII: Redis is a cache and a transient coordination store only.
/// PostgreSQL is authoritative. Losing this entire keyspace must cost latency, never data and
/// never a wrong authorization decision (SC-024).
/// </para>
/// <para>
/// The interface already forces callers to pass a TTL — there is no overload without one. What
/// it cannot express is a <em>degenerate</em> TTL, so this class rejects those: non-positive
/// values (which Redis would either refuse or expire instantly, silently disabling the cache)
/// and values above <see cref="MaxTimeToLive"/> (which produce a key that outlives any change
/// that should have invalidated it — an immortal cache entry wearing a TTL).
/// </para>
/// </remarks>
public sealed class RedisCacheStore : ICacheStore
{
    /// <summary>
    /// Longest permitted lifetime for any cached entry.
    /// </summary>
    /// <remarks>
    /// A day is far beyond anything this system legitimately caches — the longest real TTL is the
    /// 30-second membership cache. The ceiling exists to catch a typo like
    /// <c>TimeSpan.FromDays(30)</c> where <c>FromSeconds(30)</c> was meant, which would otherwise
    /// serve stale authorization for a month.
    /// </remarks>
    public static readonly TimeSpan MaxTimeToLive = TimeSpan.FromHours(24);

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly IConnectionMultiplexer _redis;
    private readonly string _keyPrefix;

    /// <summary>Creates the store.</summary>
    public RedisCacheStore(IConnectionMultiplexer redis, IOptions<RedisCacheOptions> options)
    {
        ArgumentNullException.ThrowIfNull(redis);
        ArgumentNullException.ThrowIfNull(options);

        _redis = redis;
        _keyPrefix = $"internalchat:{options.Value.Environment}:";
    }

    /// <inheritdoc />
    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
        where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            RedisValue value = await _redis.GetDatabase()
                .StringGetAsync(Qualify(key))
                .ConfigureAwait(false);

            if (value.IsNullOrEmpty)
            {
                return null;
            }

            try
            {
                return JsonSerializer.Deserialize<T>(value.ToString(), SerializerOptions);
            }
            catch (JsonException)
            {
                await RemoveAsync(key, cancellationToken).ConfigureAwait(false);
                return null;
            }
        }
        catch (RedisConnectionException)
        {
            return null;
        }
        catch (RedisTimeoutException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task SetAsync<T>(
        string key,
        T value,
        TimeSpan timeToLive,
        CancellationToken cancellationToken = default)
        where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        cancellationToken.ThrowIfCancellationRequested();

        ValidateTimeToLive(key, timeToLive);

        string payload = JsonSerializer.Serialize(value, SerializerOptions);

        try
        {
            await _redis.GetDatabase()
                .StringSetAsync(Qualify(key), payload, timeToLive)
                .ConfigureAwait(false);
        }
        catch (RedisConnectionException)
        {
            // Transient outage; swallow and degrade gracefully.
        }
        catch (RedisTimeoutException)
        {
            // Transient outage; swallow and degrade gracefully.
        }
    }

    /// <inheritdoc />
    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            await _redis.GetDatabase().KeyDeleteAsync(Qualify(key)).ConfigureAwait(false);
        }
        catch (RedisConnectionException)
        {
        }
        catch (RedisTimeoutException)
        {
        }
    }

    /// <inheritdoc />
    public async Task RemoveByPrefixAsync(string keyPrefix, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyPrefix);
        cancellationToken.ThrowIfCancellationRequested();

        IDatabase database = _redis.GetDatabase();
        RedisValue pattern = $"{_keyPrefix}{keyPrefix}*";

        try
        {
            foreach (System.Net.EndPoint endpoint in _redis.GetEndPoints())
            {
                IServer server = _redis.GetServer(endpoint);

                if (server.IsReplica)
                {
                    continue;
                }

                // KeysAsync uses SCAN under the hood, not KEYS. KEYS blocks the single-threaded
                // server for the whole sweep, which on a busy instance stalls every other request —
                // including the membership lookups on the message-delivery path.
                await foreach (RedisKey redisKey in server.KeysAsync(pattern: pattern, pageSize: 250)
                                   .WithCancellation(cancellationToken)
                                   .ConfigureAwait(false))
                {
                    await database.KeyDeleteAsync(redisKey).ConfigureAwait(false);
                }
            }
        }
        catch (RedisConnectionException)
        {
        }
        catch (RedisTimeoutException)
        {
        }
    }

    private static void ValidateTimeToLive(string key, TimeSpan timeToLive)
    {
        if (timeToLive <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeToLive),
                timeToLive,
                $"""
                Cache key '{key}' was written with a non-positive TTL.

                Constitution Principle VII: "Every cached entry MUST have an explicit TTL.
                Unbounded cache entries are prohibited." A zero or negative TTL is not "cache
                forever" — Redis rejects it, so the write silently fails and the cache stops
                working while looking healthy.
                """);
        }

        if (timeToLive > MaxTimeToLive)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeToLive),
                timeToLive,
                $"""
                Cache key '{key}' was written with a TTL of {timeToLive}, above the {MaxTimeToLive} ceiling.

                Nothing in this system legitimately caches for that long — the longest real TTL is
                the 30-second membership cache, capped at 60 seconds by Principle VII. A TTL this
                large is almost always a units mistake (FromDays where FromSeconds was meant), and
                it would serve stale authorization long after the change that should have
                invalidated it.
                """);
        }
    }

    private RedisKey Qualify(string key) => _keyPrefix + key;
}
