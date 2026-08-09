using InternalChat.Application.Abstractions;
using InternalChat.Infrastructure.Caching;
using InternalChat.IntegrationTests.Fixtures;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace InternalChat.IntegrationTests.Caching;

/// <summary>
/// Verifies the Redis cache store against a real Redis.
/// </summary>
/// <remarks>
/// Constitution Principle III prohibits mocking Redis here. A mock would happily accept a
/// zero TTL and report success — the exact behaviour these tests exist to rule out.
/// </remarks>
public sealed class RedisCacheStoreTests : IntegrationTestBase, IAsyncLifetime
{
    private const string Environment = "test";

    private ConnectionMultiplexer _redis = null!;
    private RedisCacheStore _store = null!;

    public RedisCacheStoreTests(StackFixture stack)
        : base(stack)
    {
    }

    private sealed record CachedMembership(Guid ConversationId, Guid EmployeeId, string Role);

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();

        _redis = await ConnectionMultiplexer.ConnectAsync(Stack.RedisConnectionString);
        _store = new RedisCacheStore(
            _redis,
            Options.Create(new RedisCacheOptions { Environment = Environment }));
    }

    public override async Task DisposeAsync()
    {
        if (_redis is not null)
        {
            await _redis.DisposeAsync();
        }

        await base.DisposeAsync();
    }

    [Fact]
    public async Task Set_then_get_round_trips_the_value()
    {
        CachedMembership expected = new(Guid.CreateVersion7(), Guid.CreateVersion7(), "member");

        await _store.SetAsync("membership:round-trip", expected, TimeSpan.FromSeconds(30));

        CachedMembership? actual = await _store.GetAsync<CachedMembership>("membership:round-trip");

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task Get_returns_null_for_a_key_that_was_never_written()
    {
        Assert.Null(await _store.GetAsync<CachedMembership>("membership:absent"));
    }

    /// <summary>
    /// The core of T033. Principle VII forbids unbounded cache entries; the interface makes the
    /// TTL a required argument, and the implementation refuses the degenerate values that would
    /// otherwise slip through it.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-3600)]
    public async Task Write_with_a_non_positive_ttl_is_refused(int seconds)
    {
        CachedMembership value = new(Guid.CreateVersion7(), Guid.CreateVersion7(), "member");

        ArgumentOutOfRangeException error = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => _store.SetAsync("membership:no-ttl", value, TimeSpan.FromSeconds(seconds)));

        Assert.Contains("Principle VII", error.Message, StringComparison.Ordinal);

        // The refusal must leave nothing behind. A partially written key with no expiry would be
        // precisely the unbounded entry the rule prohibits.
        Assert.Null(await _store.GetAsync<CachedMembership>("membership:no-ttl"));
    }

    [Fact]
    public async Task Write_with_a_ttl_above_the_ceiling_is_refused()
    {
        CachedMembership value = new(Guid.CreateVersion7(), Guid.CreateVersion7(), "member");

        // The realistic mistake: FromDays where FromSeconds was meant. Caught rather than
        // serving stale authorization for a month.
        ArgumentOutOfRangeException error = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => _store.SetAsync("membership:immortal", value, TimeSpan.FromDays(30)));

        Assert.Contains("ceiling", error.Message, StringComparison.Ordinal);
        Assert.Null(await _store.GetAsync<CachedMembership>("membership:immortal"));
    }

    [Fact]
    public async Task Written_key_carries_a_real_expiry_in_redis()
    {
        CachedMembership value = new(Guid.CreateVersion7(), Guid.CreateVersion7(), "member");

        await _store.SetAsync("membership:expiring", value, TimeSpan.FromSeconds(30));

        // Asserted against Redis itself rather than against our own abstraction. The rule is
        // about what is stored, so checking our wrapper would be checking the wrong thing.
        TimeSpan? ttl = await _redis.GetDatabase()
            .KeyTimeToLiveAsync($"internalchat:{Environment}:membership:expiring");

        Assert.NotNull(ttl);
        Assert.InRange(ttl.Value.TotalSeconds, 1, 30);
    }

    [Fact]
    public async Task Authorization_ttl_stays_within_the_sixty_second_constitutional_ceiling()
    {
        CachedMembership value = new(Guid.CreateVersion7(), Guid.CreateVersion7(), "member");

        // The membership cache runs at 30 s, half the 60 s ceiling, so the 5-minute revocation
        // budget in FR-003 keeps margin for identity-provider propagation.
        await _store.SetAsync("membership:authz", value, TimeSpan.FromSeconds(30));

        TimeSpan? ttl = await _redis.GetDatabase()
            .KeyTimeToLiveAsync($"internalchat:{Environment}:membership:authz");

        Assert.NotNull(ttl);
        Assert.True(
            ttl.Value <= TimeSpan.FromSeconds(60),
            $"Authorization cache TTL was {ttl}, above the 60-second ceiling in Principle VII.");
    }

    [Fact]
    public async Task Remove_invalidates_a_single_key()
    {
        CachedMembership value = new(Guid.CreateVersion7(), Guid.CreateVersion7(), "member");
        await _store.SetAsync("membership:removable", value, TimeSpan.FromSeconds(30));

        await _store.RemoveAsync("membership:removable");

        Assert.Null(await _store.GetAsync<CachedMembership>("membership:removable"));
    }

    [Fact]
    public async Task Remove_by_prefix_invalidates_a_whole_conversation_and_nothing_else()
    {
        Guid conversationId = Guid.CreateVersion7();
        CachedMembership value = new(conversationId, Guid.CreateVersion7(), "member");

        await _store.SetAsync($"membership:{conversationId}:alice", value, TimeSpan.FromSeconds(30));
        await _store.SetAsync($"membership:{conversationId}:bob", value, TimeSpan.FromSeconds(30));
        await _store.SetAsync("membership:other-conversation:carol", value, TimeSpan.FromSeconds(30));

        // This is what RemoveMember calls: a membership change must drop every cached
        // authorization entry for that conversation at once (Principle VII).
        await _store.RemoveByPrefixAsync($"membership:{conversationId}");

        Assert.Null(await _store.GetAsync<CachedMembership>($"membership:{conversationId}:alice"));
        Assert.Null(await _store.GetAsync<CachedMembership>($"membership:{conversationId}:bob"));
        Assert.NotNull(await _store.GetAsync<CachedMembership>("membership:other-conversation:carol"));
    }

    [Fact]
    public async Task Keys_are_namespaced_so_environments_cannot_collide()
    {
        CachedMembership value = new(Guid.CreateVersion7(), Guid.CreateVersion7(), "member");
        await _store.SetAsync("membership:namespaced", value, TimeSpan.FromSeconds(30));

        Assert.True(
            await _redis.GetDatabase().KeyExistsAsync($"internalchat:{Environment}:membership:namespaced"));

        // Without the namespace, a staging instance pointed at the wrong Redis would serve
        // production authorization decisions out of a shared keyspace.
        Assert.False(await _redis.GetDatabase().KeyExistsAsync("membership:namespaced"));
    }

    [Fact]
    public async Task Unreadable_entry_is_treated_as_a_miss_rather_than_an_error()
    {
        // Simulates a type whose shape changed between deploys. PostgreSQL is authoritative, so
        // a miss is always safe; throwing here would turn a rolling deploy into an outage.
        await _redis.GetDatabase().StringSetAsync(
            $"internalchat:{Environment}:membership:corrupt",
            "{not valid json",
            TimeSpan.FromSeconds(30));

        Assert.Null(await _store.GetAsync<CachedMembership>("membership:corrupt"));
    }
}
