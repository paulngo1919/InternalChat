using InternalChat.Application.Abstractions;
using InternalChat.Infrastructure.Caching;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace InternalChat.Infrastructure.Meetings;

/// <summary>The platform-wide meeting ceiling.</summary>
public sealed class MeetingCapacityOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Meetings";

    /// <summary>
    /// Most concurrent participants across every meeting on the platform (FR-043).
    /// </summary>
    /// <remarks>
    /// 1,250 — 50 simultaneous meetings at 25 each (plan.md, Scale/Scope). research.md D12 records
    /// that this is a <em>design</em> figure and that measuring real media-host capacity is an open
    /// item (T197); it is configurable so the measured number can replace it without a deployment
    /// of new code.
    /// </remarks>
    public int MaximumConcurrentParticipants { get; set; } = 1250;

    /// <summary>
    /// How long the counter survives without a write.
    /// </summary>
    /// <remarks>
    /// The drift backstop. Every lost leave webhook leaves a phantom participant in the counter, and
    /// without expiry those accumulate until the platform refuses meetings it has capacity for. An
    /// hour is far longer than any reconciliation interval, so this only ever fires when
    /// reconciliation has itself stopped — at which point forgetting the count is the safer error.
    /// </remarks>
    public TimeSpan CounterTtl { get; set; } = TimeSpan.FromHours(1);
}

/// <summary>
/// <see cref="IMeetingCapacityGuard"/> over Redis (T187, data-model.md).
/// </summary>
/// <remarks>
/// <para>
/// <b>Every operation refreshes the TTL, and that is deliberate.</b> A counter that expired while
/// meetings were in progress would report zero and admit the platform's worth of participants
/// again. Refreshing on write means the key survives exactly as long as there is activity, and
/// disappears when there is not — which is also the correct value at that point.
/// </para>
/// <para>
/// <b>Failures are open, not closed.</b> If Redis is unreachable this reports capacity available
/// rather than refusing every meeting. Constitution Principle VII: losing the cache "must cost
/// latency, never data and never a wrong authorization decision" — and this is explicitly not an
/// authorization decision, so the failure mode that keeps meetings working is the right one. The
/// per-room 25 cap still holds regardless, because the entity enforces it from PostgreSQL.
/// </para>
/// </remarks>
public sealed class RedisMeetingCapacityGuard : IMeetingCapacityGuard
{
    /// <summary>The single counter key. One number for the whole platform.</summary>
    private const string CounterKey = "meetings:participants:concurrent";

    private readonly IConnectionMultiplexer _redis;
    private readonly RedisKeyspace _keyspace;
    private readonly MeetingCapacityOptions _options;

    /// <summary>Creates the guard.</summary>
    public RedisMeetingCapacityGuard(
        IConnectionMultiplexer redis,
        RedisKeyspace keyspace,
        IOptions<MeetingCapacityOptions> options)
    {
        ArgumentNullException.ThrowIfNull(redis);
        ArgumentNullException.ThrowIfNull(keyspace);
        ArgumentNullException.ThrowIfNull(options);

        _redis = redis;
        _keyspace = keyspace;
        _options = options.Value;
    }

    /// <inheritdoc />
    public async Task<bool> HasCapacityAsync(
        int additionalParticipants = 1,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(additionalParticipants);

        int current = await GetCountAsync(cancellationToken).ConfigureAwait(false);

        return current + additionalParticipants <= _options.MaximumConcurrentParticipants;
    }

    /// <inheritdoc />
    public async Task<int> GetCountAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            RedisValue value = await Database().StringGetAsync(Key()).ConfigureAwait(false);

            return value.TryParse(out int count) ? Math.Max(0, count) : 0;
        }
        catch (RedisConnectionException)
        {
            // Fails open — see the class remarks. Reporting zero means capacity is available, which
            // keeps meetings working through a cache outage; the per-room cap is unaffected.
            return 0;
        }
    }

    /// <inheritdoc />
    public async Task IncrementAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            IDatabase database = Database();

            await database.StringIncrementAsync(Key()).ConfigureAwait(false);
            await database.KeyExpireAsync(Key(), _options.CounterTtl).ConfigureAwait(false);
        }
        catch (RedisConnectionException)
        {
            // Swallowed. A miscounted participant is a capacity inaccuracy that reconciliation
            // corrects; throwing would fail a join that the media server has already accepted.
        }
    }

    /// <inheritdoc />
    public async Task DecrementAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            IDatabase database = Database();

            long updated = await database.StringDecrementAsync(Key()).ConfigureAwait(false);

            if (updated < 0)
            {
                // A leave for a join this counter never saw — normal across a restart. Clamped,
                // because a negative counter hides a genuinely full platform behind a false surplus
                // and takes as many joins to work off as it went below zero.
                await database.StringSetAsync(Key(), 0).ConfigureAwait(false);
            }

            await database.KeyExpireAsync(Key(), _options.CounterTtl).ConfigureAwait(false);
        }
        catch (RedisConnectionException)
        {
            // Same reasoning as IncrementAsync.
        }
    }

    /// <inheritdoc />
    public async Task ReconcileAsync(int actualCount, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(actualCount);

        try
        {
            await Database()
                .StringSetAsync(Key(), actualCount, _options.CounterTtl)
                .ConfigureAwait(false);
        }
        catch (RedisConnectionException)
        {
            // The next reconciliation will do it.
        }
    }

    private IDatabase Database() => _redis.GetDatabase();

    private string Key() => _keyspace.Qualify(CounterKey);
}
