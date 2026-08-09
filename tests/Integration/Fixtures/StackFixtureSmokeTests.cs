using InternalChat.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using RabbitMQ.Client;
using StackExchange.Redis;

namespace InternalChat.IntegrationTests.Fixtures;

/// <summary>
/// Proves the containerised stack actually works before any real test depends on it.
/// </summary>
/// <remarks>
/// <para>
/// Without these, a broken fixture surfaces as dozens of confusing failures in unrelated
/// suites. Here it surfaces as one test named after the service that is broken.
/// </para>
/// <para>
/// Each check does real work against the real service rather than asserting that a container
/// object is non-null — a started container proves Docker ran something, not that the service
/// inside it accepts connections.
/// </para>
/// </remarks>
public sealed class StackFixtureSmokeTests : IntegrationTestBase
{
    public StackFixtureSmokeTests(StackFixture stack)
        : base(stack)
    {
    }

    [Fact]
    public async Task Postgres_is_reachable_and_migrations_are_applied()
    {
        await using ChatDbContext context = CreateDbContext();

        Assert.True(await context.Database.CanConnectAsync());

        string[] applied = (await context.Database.GetAppliedMigrationsAsync()).ToArray();
        Assert.Contains(applied, m => m.EndsWith("InitialSchema", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Postgres_has_the_extensions_search_and_directory_lookup_depend_on()
    {
        await using ChatDbContext context = CreateDbContext();

        List<string> extensions = await context.Database
            .SqlQueryRaw<string>(
                "SELECT extname AS \"Value\" FROM pg_extension WHERE extname IN ('unaccent', 'pg_trgm')")
            .ToListAsync();

        // unaccent backs diacritic-insensitive search (FR-029, and Vietnamese in particular);
        // pg_trgm backs directory name lookup (FR-007).
        Assert.Contains("unaccent", extensions);
        Assert.Contains("pg_trgm", extensions);
    }

    [Fact]
    public async Task Postgres_partition_helpers_create_route_and_drop()
    {
        await using ChatDbContext context = CreateDbContext();

        await context.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE smoke_partitioned (
                id uuid NOT NULL,
                sent_at timestamptz NOT NULL,
                PRIMARY KEY (id, sent_at)
            ) PARTITION BY RANGE (sent_at);
            """);

        try
        {
            await context.Database.ExecuteSqlRawAsync(
                "SELECT internalchat_ensure_month_partitions('smoke_partitioned', DATE '2026-08-01', 1);");

            // An insert must route into the correct monthly partition. If partitioning were
            // misconfigured this would fail outright — there is no default partition, by design:
            // a row landing in a catch-all would silently escape the retention sweep.
            await context.Database.ExecuteSqlRawAsync(
                "INSERT INTO smoke_partitioned VALUES (gen_random_uuid(), '2026-09-15T10:00:00Z');");

            List<string> landedIn = await context.Database
                .SqlQueryRaw<string>(
                    "SELECT tableoid::regclass::text AS \"Value\" FROM smoke_partitioned")
                .ToListAsync();

            Assert.Equal(["smoke_partitioned_2026_09"], landedIn);

            List<bool> dropped = await context.Database
                .SqlQueryRaw<bool>(
                    "SELECT internalchat_drop_month_partition('smoke_partitioned', DATE '2026-08-01') AS \"Value\"")
                .ToListAsync();

            Assert.Equal([true], dropped);
        }
        finally
        {
            await context.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS smoke_partitioned CASCADE;");
        }
    }

    [Fact]
    public async Task Redis_accepts_a_write_and_expires_it()
    {
        await using ConnectionMultiplexer redis =
            await ConnectionMultiplexer.ConnectAsync(Stack.RedisConnectionString);

        IDatabase db = redis.GetDatabase();
        await db.StringSetAsync("smoke:key", "value", TimeSpan.FromSeconds(30));

        Assert.Equal("value", await db.StringGetAsync("smoke:key"));

        // Principle VII requires every cached entry to carry a TTL. Assert the server honours
        // one, so ICacheStore's mandatory-TTL contract is enforceable rather than aspirational.
        TimeSpan? ttl = await db.KeyTimeToLiveAsync("smoke:key");
        Assert.NotNull(ttl);
        Assert.InRange(ttl.Value.TotalSeconds, 1, 30);
    }

    [Fact]
    public async Task RabbitMq_accepts_a_connection_and_a_durable_queue()
    {
        ConnectionFactory factory = new() { Uri = new Uri(Stack.RabbitMqConnectionString) };

        await using IConnection connection = await factory.CreateConnectionAsync();
        await using IChannel channel = await connection.CreateChannelAsync();

        // Durable, because Principle VI requires messages to survive a broker restart.
        QueueDeclareOk declared = await channel.QueueDeclareAsync(
            queue: "smoke.queue",
            durable: true,
            exclusive: false,
            autoDelete: false);

        Assert.Equal("smoke.queue", declared.QueueName);

        await channel.QueueDeleteAsync("smoke.queue");
    }

    [Fact]
    public async Task Minio_and_Keycloak_answer_over_http()
    {
        using HttpClient http = new() { Timeout = TimeSpan.FromSeconds(30) };

        using HttpResponseMessage minio =
            await http.GetAsync(new Uri($"http://{Stack.MinioEndpoint}/minio/health/live"));
        Assert.True(minio.IsSuccessStatusCode, $"MinIO health returned {minio.StatusCode}");

        using HttpResponseMessage keycloak = await http.GetAsync(Stack.KeycloakBaseAddress);
        Assert.True(
            keycloak.IsSuccessStatusCode || keycloak.StatusCode == System.Net.HttpStatusCode.Found,
            $"Keycloak returned {keycloak.StatusCode}");
    }

    [Fact]
    public async Task Database_reset_leaves_the_schema_and_migration_history_intact()
    {
        await ResetDatabaseAsync();

        await using ChatDbContext context = CreateDbContext();

        // Reset must empty data without wiping migration history — clearing it would make EF
        // believe the database is unmigrated and re-apply everything on the next context.
        string[] applied = (await context.Database.GetAppliedMigrationsAsync()).ToArray();
        Assert.NotEmpty(applied);
    }
}
