using InternalChat.Application.Abstractions;
using InternalChat.Domain.Conversations;
using InternalChat.Domain.Employees;
using InternalChat.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace InternalChat.Infrastructure.Caching;

/// <summary>Configuration for the authorization cache.</summary>
public sealed class MembershipCacheOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Authorization";

    /// <summary>
    /// How long an authorization decision may be reused, in seconds.
    /// </summary>
    /// <remarks>
    /// Constitution Principle VII caps authorization data at 60 seconds; research.md D6 chose 30 so
    /// FR-003's five-minute revocation budget keeps margin for directory-sync propagation. A value
    /// above the ceiling is refused at startup rather than warned about, because a cache TTL is
    /// exactly the kind of number that gets raised during an incident and never lowered again.
    /// </remarks>
    public int MembershipCacheTtlSeconds { get; set; } = 30;

    /// <summary>The constitutional ceiling for cached authorization data.</summary>
    public const int MaximumTtlSeconds = 60;

    /// <summary>Cache lifetime for one membership decision.</summary>
    public TimeSpan Ttl => TimeSpan.FromSeconds(MembershipCacheTtlSeconds);
}

/// <summary>
/// T068 — reads the membership row every access decision resolves to, through a 30-second cache.
/// </summary>
/// <remarks>
/// <para>
/// This is the hot path. Every message read, attachment retrieval, search, and meeting join passes
/// through it, so at 7,000 concurrent employees an uncached implementation would put a PostgreSQL
/// round trip on all of them. It is also the most dangerous thing to cache in the system, which is
/// why the TTL is short, validated against a ceiling at startup, and paired with explicit
/// invalidation rather than left to expire.
/// </para>
/// <para>
/// <b>A cache miss and a cache hit apply the same filter.</b> Both layers answer "is there a live
/// membership for a live employee", and neither returns a row for the caller to interpret. The
/// alternative — caching the row and judging it at the call site — means the judgement lives in as
/// many places as there are callers, and one of them eventually gets it wrong.
/// </para>
/// <para>
/// <b>A miss is cached too.</b> Without that, a caller probing conversations they do not belong to
/// reaches PostgreSQL on every attempt, which is both the cheapest denial-of-service available and
/// a measurable timing signal about which conversations exist (SC-017).
/// </para>
/// </remarks>
public sealed class MembershipCache : IMembershipReader, IMembershipCacheInvalidator
{
    private readonly ChatDbContext _context;
    private readonly ICacheStore _cache;
    private readonly MembershipCacheOptions _options;

    /// <summary>Creates the reader.</summary>
    public MembershipCache(
        ChatDbContext context,
        ICacheStore cache,
        IOptions<MembershipCacheOptions> options)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(options);

        _context = context;
        _cache = cache;
        _options = options.Value;
    }

    /// <inheritdoc />
    public async Task<MembershipSnapshot?> FindGrantingAsync(
        Guid conversationId,
        Guid employeeId,
        CancellationToken cancellationToken = default)
    {
        string key = RedisKeyspace.MembershipKey(conversationId, employeeId);

        CachedDecision? cached = await _cache
            .GetAsync<CachedDecision>(key, cancellationToken)
            .ConfigureAwait(false);

        if (cached is not null)
        {
            return cached.ToSnapshot(conversationId, employeeId);
        }

        CachedDecision decision = await ReadAsync(conversationId, employeeId, cancellationToken)
            .ConfigureAwait(false);

        await _cache.SetAsync(key, decision, _options.Ttl, cancellationToken).ConfigureAwait(false);

        return decision.ToSnapshot(conversationId, employeeId);
    }

    /// <summary>
    /// Drops every cached decision for a conversation.
    /// </summary>
    /// <remarks>
    /// Called from inside the use case that changed the membership, never from a background sweep
    /// (Principle VII). By prefix rather than by key because the mutating use case knows the
    /// conversation and the one employee it changed — but a role change alters what other members'
    /// cached decisions should say too, and enumerating them here would be a second source of truth
    /// about who is in the conversation.
    /// </remarks>
    public Task InvalidateConversationAsync(Guid conversationId, CancellationToken cancellationToken = default) =>
        _cache.RemoveByPrefixAsync(RedisKeyspace.MembershipPrefix(conversationId), cancellationToken);

    /// <summary>Drops one cached decision.</summary>
    public Task InvalidateAsync(Guid conversationId, Guid employeeId, CancellationToken cancellationToken = default) =>
        _cache.RemoveAsync(RedisKeyspace.MembershipKey(conversationId, employeeId), cancellationToken);

    private async Task<CachedDecision> ReadAsync(
        Guid conversationId,
        Guid employeeId,
        CancellationToken cancellationToken)
    {
        // The join to employee is what makes deactivation take effect on the authorization path
        // without anything having to remember to check it. A deactivated employee whose membership
        // rows still exist — and they do, deliberately, because SC-021 needs them a year later —
        // must stop being granted access the moment the row says so.
        var row = await _context.Memberships
            .AsNoTracking()
            .Where(m => m.ConversationId == conversationId
                && m.EmployeeId == employeeId
                && m.RemovedAt == null)
            .Join(
                _context.Employees.AsNoTracking().Where(e => e.Status == EmployeeStatus.Active),
                m => m.EmployeeId,
                e => e.Id,
                (m, _) => new { m.Role, m.VisibleFromSeq })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return row is null
            ? CachedDecision.Denied
            : new CachedDecision(true, row.Role, row.VisibleFromSeq);
    }

    /// <summary>
    /// What is stored in Redis: the decision, not the row.
    /// </summary>
    /// <remarks>
    /// Deliberately minimal. Caching the membership entity would put whatever fields it grows next
    /// into the cache too, and cached fields are stale fields — including ones a future reader
    /// might make an access decision from without realising how old they are.
    /// </remarks>
    /// <param name="Granted">Whether access is granted. <c>false</c> entries are cached as well.</param>
    /// <param name="Role">Role held, meaningful only when <paramref name="Granted"/>.</param>
    /// <param name="VisibleFromSeq">History floor, meaningful only when <paramref name="Granted"/>.</param>
    private sealed record CachedDecision(bool Granted, MembershipRole Role, long VisibleFromSeq)
    {
        public static readonly CachedDecision Denied = new(false, MembershipRole.Member, 0);

        /// <summary>
        /// Rebuilds the snapshot. The two ids come from the query rather than the cache entry —
        /// they are already known to the caller, and storing them would be storing the key inside
        /// the value.
        /// </summary>
        public MembershipSnapshot? ToSnapshot(Guid conversationId, Guid employeeId) =>
            Granted ? new MembershipSnapshot(conversationId, employeeId, Role, VisibleFromSeq) : null;
    }
}
