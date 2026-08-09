using InternalChat.Application.Abstractions;
using InternalChat.Infrastructure.Caching;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace InternalChat.Infrastructure.Identity;

/// <summary>
/// T064 — the Redis revocation set (research.md D5).
/// </summary>
/// <remarks>
/// <para>
/// A JWT cannot be withdrawn once signed, so a platform that promises access ends within five
/// minutes (FR-003) has to either ask the identity provider on every request or keep its own list
/// of identities to refuse. Introspecting per request was rejected on the p95 budget — it puts a
/// Keycloak round trip in the messaging hot path — so this is the list.
/// </para>
/// <para>
/// <b>Entries outlive the tokens they revoke.</b> The TTL callers pass is the maximum access-token
/// lifetime, not something shorter. An entry that expired first would let a revoked token start
/// working again, which is worse than never having revoked it: the access comes back silently and
/// nothing in the logs marks the moment.
/// </para>
/// </remarks>
public sealed partial class RevocationStore : IRevocationStore
{
    /// <summary>Written as the value. Nothing reads it — presence of the key is the fact.</summary>
    private const string Marker = "1";

    private readonly IConnectionMultiplexer _redis;
    private readonly RedisKeyspace _keyspace;
    private readonly ILogger<RevocationStore> _logger;

    /// <summary>Creates the store.</summary>
    public RevocationStore(
        IConnectionMultiplexer redis,
        RedisKeyspace keyspace,
        ILogger<RevocationStore> logger)
    {
        ArgumentNullException.ThrowIfNull(redis);
        ArgumentNullException.ThrowIfNull(keyspace);
        ArgumentNullException.ThrowIfNull(logger);

        _redis = redis;
        _keyspace = keyspace;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task RevokeSessionAsync(
        string sessionId,
        TimeSpan timeToLive,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        cancellationToken.ThrowIfCancellationRequested();

        await WriteAsync(RedisKeyspace.RevokedSessionKey(sessionId), timeToLive).ConfigureAwait(false);
        SessionRevoked(_logger, sessionId);
    }

    /// <inheritdoc />
    public async Task RevokeSubjectAsync(
        string subject,
        TimeSpan timeToLive,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        cancellationToken.ThrowIfCancellationRequested();

        await WriteAsync(RedisKeyspace.RevokedSubjectKey(subject), timeToLive).ConfigureAwait(false);
        SubjectRevoked(_logger, subject);
    }

    /// <inheritdoc />
    public async Task ClearSubjectAsync(string subject, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        cancellationToken.ThrowIfCancellationRequested();

        await _redis.GetDatabase()
            .KeyDeleteAsync(_keyspace.Qualify(RedisKeyspace.RevokedSubjectKey(subject)))
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> IsRevokedAsync(
        string subject,
        string? sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            IDatabase database = _redis.GetDatabase();

            // Both in one round trip. Issued as a batch rather than awaited in sequence because
            // this runs on every authenticated request and on every sweep of every open
            // connection; two sequential round trips would double that cost for no benefit.
            Task<bool> subjectRevoked =
                database.KeyExistsAsync(_keyspace.Qualify(RedisKeyspace.RevokedSubjectKey(subject)));

            Task<bool> sessionRevoked = sessionId is null
                ? Task.FromResult(false)
                : database.KeyExistsAsync(_keyspace.Qualify(RedisKeyspace.RevokedSessionKey(sessionId)));

            bool[] results = await Task.WhenAll(subjectRevoked, sessionRevoked).ConfigureAwait(false);
            return results[0] || results[1];
        }
        catch (RedisException ex)
        {
            // FAILS OPEN, uniquely in this codebase, and the reasoning is written down in
            // IRevocationStore: the token has already been cryptographically validated and has at
            // most five minutes of life left, so the exposure is bounded and known. Failing closed
            // would sign all 10,000 employees out the moment Redis restarted, which SC-024
            // explicitly forbids — a cache-tier outage may cost latency, not availability.
            //
            // This is not the same judgement as the membership evaluator's, which refuses on
            // failure. There, nothing has been proven at all.
            RevocationCheckUnavailable(_logger, subject, ex);
            return false;
        }
    }

    private async Task WriteAsync(string key, TimeSpan timeToLive)
    {
        if (timeToLive <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeToLive),
                timeToLive,
                "A revocation entry needs a positive lifetime. Redis rejects a non-positive expiry, "
                + "so the write would silently do nothing and the token would keep working.");
        }

        await _redis.GetDatabase()
            .StringSetAsync(_keyspace.Qualify(key), Marker, timeToLive)
            .ConfigureAwait(false);
    }

    [LoggerMessage(
        EventId = 3100,
        Level = LogLevel.Information,
        Message = "Session {SessionId} revoked")]
    private static partial void SessionRevoked(ILogger logger, string sessionId);

    [LoggerMessage(
        EventId = 3101,
        Level = LogLevel.Information,
        Message = "All sessions for subject {Subject} revoked")]
    private static partial void SubjectRevoked(ILogger logger, string subject);

    [LoggerMessage(
        EventId = 3102,
        Level = LogLevel.Error,
        Message = "Revocation check for {Subject} failed; the token was allowed to stand for the "
            + "remainder of its lifetime. See IRevocationStore for why this does not fail closed.")]
    private static partial void RevocationCheckUnavailable(ILogger logger, string subject, Exception exception);
}
