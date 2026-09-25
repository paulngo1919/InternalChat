using DotNet.Testcontainers.Builders;
using InternalChat.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Minio;
using Minio.DataModel.Args;
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

    // Imports the SAME realm the deployment imports (T061), not a test-only realm. A realm
    // invented here would let the authentication path pass against a configuration nobody runs —
    // wrong audience mapper, wrong token lifetime, wrong subject format, all invisible until
    // production. The wait strategy targets the realm's discovery document rather than the root
    // page for the same reason: the root answers before the import has finished.
    private readonly KeycloakContainer _keycloak = new KeycloakBuilder(KeycloakImage)
        .WithCleanUp(true)
        .WithResourceMapping(
            new FileInfo(TestSupport.RepositoryPaths.KeycloakRealmExport),
            "/opt/keycloak/data/import/")
        .WithCommand("--import-realm")
        .WithWaitStrategy(
            Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(
                r => r.ForPath($"/realms/{Realm}/.well-known/openid-configuration").ForPort(8080)))
        .Build();

    /// <summary>The realm imported from <c>deploy/keycloak/realm-export.json</c>.</summary>
    public const string Realm = "internalchat";

    /// <summary>
    /// The direct-access-grant client the realm carries for automated tests.
    /// </summary>
    /// <remarks>
    /// Tests need a token without driving a browser. This is why it exists, and why
    /// <c>deploy/keycloak/README.md</c> records that a production realm must not import it.
    /// </remarks>
    public const string TestClientId = "internalchat-test";

    /// <summary>The audience the API validates — the SPA client id, per the realm's audience mapper.</summary>
    public const string Audience = "internalchat-web";

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

    /// <summary>Bucket holding scanned-clean objects. Matches <c>deploy/docker-compose.yml</c>.</summary>
    public const string AttachmentsBucket = "attachments";

    /// <summary>Bucket holding uploads awaiting a verdict.</summary>
    public const string QuarantineBucket = "attachments-quarantine";

    /// <summary>Keycloak base address.</summary>
    public Uri KeycloakBaseAddress => new(_keycloak.GetBaseAddress());

    /// <summary>
    /// The OIDC authority the API must be configured with, and the exact value tokens carry as
    /// <c>iss</c>.
    /// </summary>
    /// <remarks>
    /// Keycloak derives the issuer from the request it received, so this is the container's mapped
    /// address — a different spelling of the same host (<c>127.0.0.1</c> for <c>localhost</c>)
    /// produces a token the API rejects for issuer mismatch, which reads as a signature problem
    /// and is not one.
    /// </remarks>
    public string RealmAuthority => $"{KeycloakBaseAddress.ToString().TrimEnd('/')}/realms/{Realm}";

    /// <summary>
    /// Obtains a real access token for a realm user, via the direct access grant.
    /// </summary>
    /// <remarks>
    /// Signed by the real Keycloak with the real realm settings, so a test exercises the same
    /// validation production does: issuer, audience, signature, and the 300-second lifetime that
    /// research.md D5 makes load-bearing. Hand-minting a token with a symmetric test key would
    /// verify the test's own assumptions instead.
    /// </remarks>
    /// <param name="username">Realm username. Passwords equal usernames in the development realm.</param>
    public async Task<string> IssueAccessTokenAsync(
        string username,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);

        using HttpClient client = new();
        using FormUrlEncodedContent form = new(
        [
            new KeyValuePair<string, string>("grant_type", "password"),
            new KeyValuePair<string, string>("client_id", TestClientId),
            new KeyValuePair<string, string>("username", username),
            new KeyValuePair<string, string>("password", username),
            new KeyValuePair<string, string>("scope", "openid"),
        ]);

        using HttpResponseMessage response = await client
            .PostAsync(new Uri($"{RealmAuthority}/protocol/openid-connect/token"), form, cancellationToken)
            .ConfigureAwait(false);

        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Keycloak refused a token for '{username}' ({(int)response.StatusCode}): {body}. "
                + "The realm export and the roster in tools/Seeder/DevelopmentSeeder must agree.");
        }

        using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(body);
        return document.RootElement.GetProperty("access_token").GetString()
            ?? throw new InvalidOperationException("Keycloak returned a token response with no access_token.");
    }

    /// <summary>
    /// Issues a token that is already expired by the time it is returned.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The realm's access tokens live 300 seconds, and a test cannot wait that out. Rather than mint
    /// a token by hand — which would test the test's idea of a token, not Keycloak's — the realm's
    /// lifespan is briefly narrowed through the admin API, a real token is issued, and the setting
    /// is restored. What comes back is genuinely signed, genuinely audience-mapped, and genuinely
    /// stale.
    /// </para>
    /// <para>
    /// The wait afterwards must exceed the JWT handler's permitted clock skew, which the API sets to
    /// zero (a five-minute default would silently double the effective token life and break the
    /// revocation budget in FR-003). Two seconds is therefore ample.
    /// </para>
    /// </remarks>
    public async Task<string> IssueExpiredAccessTokenAsync(
        string username,
        CancellationToken cancellationToken = default)
    {
        const int OriginalLifespanSeconds = 300;

        await SetAccessTokenLifespanAsync(1, cancellationToken).ConfigureAwait(false);

        try
        {
            string token = await IssueAccessTokenAsync(username, cancellationToken).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            return token;
        }
        finally
        {
            // Restored even on failure. Leaving the realm at a one-second lifespan would make every
            // later test in the collection fail with an expiry error and no hint of why.
            await SetAccessTokenLifespanAsync(OriginalLifespanSeconds, CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    /// <summary>Stops the Redis container to simulate a cache outage (T213).</summary>
    public Task StopRedisAsync(CancellationToken cancellationToken = default) => _redis.StopAsync(cancellationToken);

    /// <summary>Starts the Redis container after an outage simulation.</summary>
    public Task StartRedisAsync(CancellationToken cancellationToken = default) => _redis.StartAsync(cancellationToken);

    private async Task SetAccessTokenLifespanAsync(int seconds, CancellationToken cancellationToken)
    {
        using HttpClient client = new();

        string adminToken = await GetAdminTokenAsync(client, cancellationToken).ConfigureAwait(false);

        using HttpRequestMessage request = new(
            HttpMethod.Put,
            new Uri($"{KeycloakBaseAddress.ToString().TrimEnd('/')}/admin/realms/{Realm}"));

        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", adminToken);
        request.Content = new StringContent(
            $$"""{"realm":"{{Realm}}","accessTokenLifespan":{{seconds}}}""",
            System.Text.Encoding.UTF8,
            "application/json");

        using HttpResponseMessage response = await client
            .SendAsync(request, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"Could not set accessTokenLifespan on realm '{Realm}' ({(int)response.StatusCode}): {body}");
        }
    }

    private async Task<string> GetAdminTokenAsync(HttpClient client, CancellationToken cancellationToken)
    {
        // Testcontainers' Keycloak module bootstraps this administrator. Only the master realm is
        // reachable with it, which is the point — no test can accidentally authenticate as an
        // application user with administrative rights.
        using FormUrlEncodedContent form = new(
        [
            new KeyValuePair<string, string>("grant_type", "password"),
            new KeyValuePair<string, string>("client_id", "admin-cli"),
            new KeyValuePair<string, string>("username", "admin"),
            new KeyValuePair<string, string>("password", "admin"),
        ]);

        using HttpResponseMessage response = await client
            .PostAsync(
                new Uri($"{KeycloakBaseAddress.ToString().TrimEnd('/')}/realms/master/protocol/openid-connect/token"),
                form,
                cancellationToken)
            .ConfigureAwait(false);

        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Could not obtain a Keycloak administrator token ({(int)response.StatusCode}): {body}");
        }

        using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(body);
        return document.RootElement.GetProperty("access_token").GetString()
            ?? throw new InvalidOperationException("Keycloak returned an admin token response with no access_token.");
    }

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
        await CreateBucketsAsync().ConfigureAwait(false);
    }

    /// <summary>A MinIO client bound to the containerised server.</summary>
    /// <remarks>
    /// Exposed so a test can put an object in quarantine directly — standing in for the browser
    /// PUT that the API deliberately never performs, since bytes go from the client to MinIO and
    /// never through .NET.
    /// </remarks>
    public IMinioClient CreateMinioClient() =>
        new MinioClient()
            .WithEndpoint(MinioEndpoint)
            .WithCredentials(MinioAccessKey, MinioSecretKey)
            .WithSSL(false)
            .Build();

    /// <summary>
    /// Creates both attachment buckets, standing in for the <c>minio-init</c> Compose service.
    /// </summary>
    /// <remarks>
    /// Done here rather than lazily in <c>MinioObjectStore</c> for the same reason production does
    /// it in a separate service: an application that creates its own buckets holds bucket-creation
    /// rights it never needs, and a misconfigured bucket name silently becomes a new empty bucket
    /// instead of an error.
    /// </remarks>
    private async Task CreateBucketsAsync()
    {
        IMinioClient client = CreateMinioClient();

        foreach (string bucket in new[] { AttachmentsBucket, QuarantineBucket })
        {
            bool exists = await client
                .BucketExistsAsync(new BucketExistsArgs().WithBucket(bucket))
                .ConfigureAwait(false);

            if (!exists)
            {
                await client
                    .MakeBucketAsync(new MakeBucketArgs().WithBucket(bucket))
                    .ConfigureAwait(false);
            }
        }
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
