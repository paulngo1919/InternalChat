using InternalChat.Infrastructure.Persistence;
using InternalChat.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;

namespace InternalChat.IntegrationTests;

/// <summary>
/// Base class for integration tests. Gives each test a clean database and cache.
/// </summary>
/// <remarks>
/// <para>
/// State is reset before each test rather than after. A test that fails mid-way leaves its rows
/// behind either way, but resetting first means the failure can still be inspected in the
/// container afterwards, and the next test is unaffected regardless of how the previous one
/// ended.
/// </para>
/// <para>
/// Truncation is hand-rolled rather than using Respawn — it is about twenty lines against
/// PostgreSQL's catalog, and Principle VIII prefers one fewer dependency to re-verify at each
/// version bump.
/// </para>
/// </remarks>
[Collection(StackCollectionDefinition.Name)]
public abstract class IntegrationTestBase : IAsyncLifetime
{
    /// <summary>The shared containerised stack.</summary>
    protected StackFixture Stack { get; }

    /// <summary>Creates the base with the shared stack.</summary>
    protected IntegrationTestBase(StackFixture stack)
    {
        ArgumentNullException.ThrowIfNull(stack);
        Stack = stack;
    }

    /// <summary>A context bound to the containerised database.</summary>
    protected ChatDbContext CreateDbContext() => Stack.CreateDbContext();

    /// <inheritdoc />
    public virtual async Task InitializeAsync()
    {
        await ResetDatabaseAsync().ConfigureAwait(false);
        await ResetCacheAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public virtual Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// Empties every table while leaving the schema and migration history intact.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Dropping and re-migrating between tests would be correct but far too slow — the schema is
    /// rebuilt hundreds of times for no benefit. Truncating leaves the schema, which is also what
    /// production has.
    /// </para>
    /// <para>
    /// <c>__ef_migrations_history</c> is excluded: wiping it would make EF think the database is
    /// unmigrated and try to re-apply everything on the next context.
    /// </para>
    /// </remarks>
    protected async Task ResetDatabaseAsync()
    {
        await using ChatDbContext context = CreateDbContext();

        // Partitions are listed in pg_tables too, but truncating a partitioned parent cascades
        // to its partitions, so selecting only top-level tables is both correct and cheaper.
        // Excluded by PREFIX, case-insensitively, rather than by exact name. An earlier version
        // matched the exact string '__ef_migrations_history' and so failed to exclude EF's
        // default '__EFMigrationsHistory' — truncating it, which made the database look
        // unmigrated to every test that ran afterwards. Matching the family, not one spelling,
        // is what stops that recurring if the name is ever changed again.
        List<string> tables = await context.Database
            .SqlQueryRaw<string>(
                """
                SELECT c.relname AS "Value"
                FROM pg_class c
                JOIN pg_namespace n ON n.oid = c.relnamespace
                WHERE n.nspname = 'public'
                  AND c.relkind IN ('r', 'p')
                  AND lower(c.relname) NOT LIKE '\_\_ef%'
                  AND NOT EXISTS (SELECT 1 FROM pg_inherits i WHERE i.inhrelid = c.oid)
                """)
            .ToListAsync()
            .ConfigureAwait(false);

        if (tables.Count == 0)
        {
            return;
        }

        string quoted = string.Join(", ", tables.Select(t => $"\"{t}\""));

        // Table names come from pg_catalog, never from user input, so they cannot carry an
        // injection payload — and identifiers cannot be parameterised in SQL anyway. Built with
        // Concat rather than interpolation at the call site so EF1002 is satisfied honestly
        // instead of suppressed.
        //
        // RESTART IDENTITY so sequence-backed columns start from a known value each test, and
        // CASCADE so foreign keys do not dictate truncation order.
        string truncateSql = string.Concat("TRUNCATE TABLE ", quoted, " RESTART IDENTITY CASCADE;");

        await context.Database
            .ExecuteSqlRawAsync(truncateSql)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Flushes Redis so a cached entry cannot leak between tests.
    /// </summary>
    /// <remarks>
    /// Matters more than it looks. The membership cache has a 30-second TTL, which is longer than
    /// a test run — a stale authorization entry from a previous test would let a later test pass
    /// for entirely the wrong reason.
    /// </remarks>
    protected async Task ResetCacheAsync()
    {
        ConfigurationOptions options = ConfigurationOptions.Parse(Stack.RedisConnectionString);
        options.AllowAdmin = true;

        await using ConnectionMultiplexer redis =
            await ConnectionMultiplexer.ConnectAsync(options).ConfigureAwait(false);

        foreach (System.Net.EndPoint endpoint in redis.GetEndPoints())
        {
            await redis.GetServer(endpoint).FlushDatabaseAsync().ConfigureAwait(false);
        }
    }
}
