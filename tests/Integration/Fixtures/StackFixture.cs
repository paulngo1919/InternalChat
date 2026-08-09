using DotNet.Testcontainers.Builders;
using InternalChat.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Testcontainers.Keycloak;
using Testcontainers.Minio;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Testcontainers.Redis;

namespace InternalChat.IntegrationTests.Fixtures;

/// <summary>
/// Starts the real backing services once for the whole integration test run.
/// </summary>
/// <remarks>
/// <para>
/// Constitution Principle III: integration tests run against real PostgreSQL, Redis, RabbitMQ,
/// MinIO, and Keycloak — "mocking these in integration tests is prohibited". The in-memory EF
/// provider is specifically excluded because it does not implement PostgreSQL semantics:
/// unique constraints, <c>FOR UPDATE SKIP LOCKED</c>, full-text search, and range partitioning
/// all behave differently, so a test that passes against it proves nothing about production.
/// </para>
/// <para>
/// Shared across the whole assembly via a collection fixture. Five containers take tens of
/// seconds to start; paying that per test class would make the suite slow enough that people
/// stop running it, and a suite nobody runs is worse than no suite.
/// </para>
/// <para>
/// Image tags are pinned to the same digests as <c>deploy/docker-compose.yml</c>. Testing
/// against a different version of PostgreSQL than production runs is a way to be surprised
/// later.
/// </para>
/// </remarks>
public sealed class StackFixture : IAsyncLifetime
{
    private const string PostgresImage = "postgres:17.10-alpine";
    private const string RedisImage = "redis:7.4.10-alpine";
    private const string RabbitMqImage = "rabbitmq:4.3.4-management-alpine";
    private const string MinioImage = "minio/minio:RELEASE.2025-09-07T16-13-09Z";
    private const string KeycloakImage = "quay.io/keycloak/keycloak:26.7.1";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder(PostgresImage)
        .WithDatabase("internalchat")
        .WithUsername("internalchat")
        .WithPassword("internalchat")
        .WithCleanUp(true)
        .Build();

    private readonly RedisContainer _redis = new RedisBuilder(RedisImage)
        .WithCleanUp(true)
        .Build();

    private readonly RabbitMqContainer _rabbitMq = new RabbitMqBuilder(RabbitMqImage)
        .WithUsername("internalchat")
        .WithPassword("internalchat")
        .WithCleanUp(true)
        .Build();

    private readonly MinioContainer _minio = new MinioBuilder(MinioImage)
        .WithCleanUp(true)
        .Build();

    private readonly KeycloakContainer _keycloak = new KeycloakBuilder(KeycloakImage)
        .WithCleanUp(true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPath("/").ForPort(8080)))
        .Build();

    /// <summary>PostgreSQL connection string, schema already migrated.</summary>
    public string PostgresConnectionString => _postgres.GetConnectionString();

    /// <summary>Redis connection string.</summary>
    public string RedisConnectionString => _redis.GetConnectionString();

    /// <summary>RabbitMQ AMQP connection string.</summary>
    public string RabbitMqConnectionString => _rabbitMq.GetConnectionString();

    /// <summary>MinIO S3 endpoint, host:port with no scheme.</summary>
    public string MinioEndpoint => $"{_minio.Hostname}:{_minio.GetMappedPublicPort(9000)}";

    /// <summary>MinIO access key.</summary>
    public static string MinioAccessKey => MinioBuilder.DefaultUsername;

    /// <summary>MinIO secret key.</summary>
    public static string MinioSecretKey => MinioBuilder.DefaultPassword;

    /// <summary>Keycloak base address.</summary>
    public Uri KeycloakBaseAddress => new(_keycloak.GetBaseAddress());

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        // Started in parallel so total wall-clock is the slowest container rather than the sum.
        // Keycloak dominates — it is an identity server booting a JVM, not a key-value store.
        await Task.WhenAll(
            _postgres.StartAsync(),
            _redis.StartAsync(),
            _rabbitMq.StartAsync(),
            _minio.StartAsync(),
            _keycloak.StartAsync()).ConfigureAwait(false);

        await ApplyMigrationsAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        await Task.WhenAll(
            _postgres.DisposeAsync().AsTask(),
            _redis.DisposeAsync().AsTask(),
            _rabbitMq.DisposeAsync().AsTask(),
            _minio.DisposeAsync().AsTask(),
            _keycloak.DisposeAsync().AsTask()).ConfigureAwait(false);
    }

    /// <summary>Creates a context bound to the containerised database.</summary>
    public ChatDbContext CreateDbContext()
    {
        DbContextOptionsBuilder<ChatDbContext> builder = new();
        ChatDbContext.ConfigureNpgsql(builder, PostgresConnectionString);
        return new ChatDbContext(builder.Options);
    }

    /// <summary>
    /// Applies migrations exactly as the deployment does.
    /// </summary>
    /// <remarks>
    /// Uses <c>Migrate()</c> rather than <c>EnsureCreated()</c> deliberately. <c>EnsureCreated</c>
    /// builds the schema from the model and skips migrations entirely, so a broken migration
    /// would pass every test and fail on deploy — which is the one thing this suite exists to
    /// catch.
    /// </remarks>
    private async Task ApplyMigrationsAsync()
    {
        await using ChatDbContext context = CreateDbContext();
        await context.Database.MigrateAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// Collection definition binding every integration test to the one shared stack.
/// </summary>
[CollectionDefinition(Name)]
public sealed class StackCollectionDefinition : ICollectionFixture<StackFixture>
{
    /// <summary>Collection name applied via <c>[Collection(StackCollectionDefinition.Name)]</c>.</summary>
    public const string Name = "integration-stack";
}
