using InternalChat.Application.Abstractions;
using InternalChat.Infrastructure.Caching;
using InternalChat.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Minio;
using StackExchange.Redis;

namespace InternalChat.Infrastructure;

/// <summary>
/// Registers Infrastructure implementations of the Application layer's outbound interfaces.
/// </summary>
/// <remarks>
/// Constitution Principle I: this is the only seam through which Infrastructure enters the
/// process. Composition roots (<c>InternalChat.Api</c> and <c>InternalChat.Worker</c>) call this
/// from <c>Program.cs</c> and never name an Infrastructure type anywhere else —
/// <c>tests/Architecture/CompositionRootTests.cs</c> fails the build if they do.
/// </remarks>
public static class InfrastructureServiceCollectionExtensions
{
    /// <summary>Adds persistence, caching, messaging, storage, and identity implementations.</summary>
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        AddPersistence(services, configuration);
        AddCaching(services, configuration);
        AddMessaging(services, configuration);
        AddIdentity(services, configuration);
        AddNotifications(services, configuration);
        AddStorage(services, configuration);
        AddMeetings(services, configuration);

        return services;
    }

    /// <summary>Registers meeting persistence, the LiveKit issuer, and the capacity guard (T184–T187).</summary>
    /// <remarks>
    /// The issuer goes through <c>AddHttpClient</c> with an explicit timeout: it calls the media
    /// host, which is the one component on a second machine, and the default 100-second timeout
    /// would let an unreachable host hold a request thread for a minute and a half.
    /// </remarks>
    private static void AddMeetings(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<Meetings.LiveKitOptions>(
            configuration.GetSection(Meetings.LiveKitOptions.SectionName));
        services.Configure<Meetings.MeetingCapacityOptions>(
            configuration.GetSection(Meetings.MeetingCapacityOptions.SectionName));

        services.TryAddScoped<IMeetingRepository, Persistence.Repositories.MeetingRepository>();
        services.TryAddScoped<IMeetingCapacityGuard, Meetings.RedisMeetingCapacityGuard>();
        services.TryAddScoped<IShareSessionStore, Persistence.Repositories.ShareSessionStore>();

        // T206 — the retention sweep. Scoped: it uses the request's DbContext and object store,
        // and the job creates a fresh scope per run rather than holding one for the process's life.
        services.TryAddScoped<IRetentionSweep, Persistence.RetentionSweep>();

        services.AddHttpClient<IMeetingTokenIssuer, Meetings.LiveKitTokenIssuer>(
            client => client.Timeout = TimeSpan.FromSeconds(5));
    }

    /// <summary>Registers MinIO attachment storage (T150).</summary>
    /// <remarks>
    /// The client is a singleton: it owns an HTTP connection pool, and building one per request
    /// would exhaust sockets under the upload rate FR-026 implies.
    /// </remarks>
    private static void AddStorage(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<Storage.MinioOptions>(configuration.GetSection(Storage.MinioOptions.SectionName));

        services.TryAddSingleton<Minio.IMinioClient>(provider =>
        {
            Storage.MinioOptions options = provider
                .GetRequiredService<Microsoft.Extensions.Options.IOptions<Storage.MinioOptions>>().Value;

            // Validated here rather than with .ValidateOnStart(), for the same reason as
            // VapidOptions: both hosts share AddInfrastructure(), and a host that never touches
            // storage should not fail to boot over configuration it does not use. This factory
            // runs only when something actually resolves the client.
            if (string.IsNullOrWhiteSpace(options.Endpoint))
            {
                throw new InvalidOperationException(
                    $"{Storage.MinioOptions.SectionName}:Endpoint is required to use attachment storage.");
            }

            return new Minio.MinioClient()
                .WithEndpoint(options.Endpoint)
                .WithCredentials(options.AccessKey, options.SecretKey)
                .WithSSL(options.UseTls)
                .Build();
        });

        services.TryAddScoped<IObjectStore, Storage.MinioObjectStore>();

        services.TryAddScoped<
            IAttachmentRepository,
            Persistence.Repositories.AttachmentRepository>();

        // T152. Registered for both hosts because AddInfrastructure is shared, but only the Worker
        // resolves it — the scan consumer is the single caller. Validation is lazy for the same
        // reason as the MinIO client: the API carries no ClamAv__* configuration and must not fail
        // to boot over a dependency it never uses.
        services.Configure<Storage.ClamAvOptions>(configuration.GetSection(Storage.ClamAvOptions.SectionName));
        services.TryAddScoped<IMalwareScanner, Storage.ClamAvScanner>();

        // T175 — poster frames and faststart remuxing (research.md D8). Worker-only in practice:
        // the API image carries no ffmpeg. The implementation degrades rather than throwing when
        // the binary is absent, so an API that somehow resolved it would still function.
        services.Configure<Storage.FfmpegOptions>(configuration.GetSection(Storage.FfmpegOptions.SectionName));
        services.TryAddScoped<IVideoProcessor, Storage.FfmpegVideoProcessor>();

        // T164 — PostgreSQL full-text search (research.md D9). The interface is the seam that keeps
        // the OpenSearch option open if the load test finds the budget breached.
        services.TryAddScoped<ISearchIndex, Search.PostgresSearchIndex>();
        
        services.TryAddScoped<IExportProvider, Exports.ExportProvider>();
    }

    /// <summary>
    /// Registers read state, notification preference, and push subscription persistence (T130),
    /// plus the VAPID push sender (T134).
    /// </summary>
    private static void AddNotifications(IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddScoped<
            Application.Abstractions.IReadStateRepository,
            Persistence.Repositories.ReadStateRepository>();
        services.TryAddScoped<
            Application.Abstractions.INotificationPreferenceRepository,
            Persistence.Repositories.NotificationPreferenceRepository>();
        services.TryAddScoped<
            Application.Abstractions.IPushSubscriptionStore,
            Persistence.Repositories.PushSubscriptionStore>();

        // No .ValidateOnStart() here, deliberately: only the Worker host ever resolves
        // IPushSender (NotificationFanoutConsumer), but AddInfrastructure() is shared by both
        // hosts, and the API's Compose environment carries no WebPush__* variables at all — it
        // never sends a push. Eager validation would fail the API host at startup over
        // configuration it does not need. WebPushSender validates lazily instead, in its own
        // constructor, which runs only when something actually resolves it.
        services.Configure<Push.VapidOptions>(configuration.GetSection(Push.VapidOptions.SectionName));

        // Just the public half, and safe to register unconditionally: the API host only ever
        // hands this to a browser subscribing (never sends a push itself), so it needs no eager
        // validation the way WebPushSender does.
        services.TryAddScoped<Application.Abstractions.IVapidPublicKeyProvider, Push.VapidPublicKeyProvider>();

        // Every outbound call needs an explicit timeout (Constitution Performance Requirements).
        // A push service unreachable for the default 100 s would hold the notification fan-out
        // consumer's prefetched batch hostage far longer than any message is worth waiting for.
        services.AddHttpClient<Application.Abstractions.IPushSender, Push.WebPushSender>(
            client => client.Timeout = TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// Registers the token revocation set and the session registry (T064, research.md D5).
    /// </summary>
    private static void AddIdentity(IServiceCollection services, IConfiguration configuration)
    {
        // Singletons: both are stateless wrappers over the shared multiplexer, and the revocation
        // check runs on every authenticated request and on every sweep of every open connection —
        // a per-request instance would allocate for nothing on the hottest path in the system.
        services.TryAddSingleton<Application.Abstractions.IRevocationStore, Identity.RevocationStore>();
        services.TryAddSingleton<Application.Abstractions.ISessionRegistry, Identity.SessionRegistry>();

        services
            .AddOptions<Caching.MembershipCacheOptions>()
            .Bind(configuration.GetSection(Caching.MembershipCacheOptions.SectionName))
            .Validate(
                options => options.MembershipCacheTtlSeconds is > 0
                    and <= Caching.MembershipCacheOptions.MaximumTtlSeconds,
                $"Authorization:MembershipCacheTtlSeconds must be between 1 and "
                + $"{Caching.MembershipCacheOptions.MaximumTtlSeconds}. Constitution Principle VII "
                + "caps cached authorization data at 60 seconds, and research.md D6 chose 30 so "
                + "FR-003's five-minute revocation budget keeps margin. Refused at startup rather "
                + "than warned about, because a cache TTL is exactly the number that gets raised "
                + "during an incident and never lowered again.")
            .ValidateOnStart();

        // Scoped: it reads through the request's ChatDbContext on a cache miss.
        services.TryAddScoped<Application.Abstractions.IMembershipReader, Caching.MembershipCache>();
        services.TryAddScoped<Application.Abstractions.IMembershipCacheInvalidator, Caching.MembershipCache>();
        services.TryAddScoped<Caching.MembershipCache>();

        services.TryAddScoped<
            Application.Abstractions.IEmployeeDirectory,
            Persistence.Repositories.EmployeeDirectory>();

        // The write side, used by directory sync alone. Registered separately from the read side
        // so an endpoint cannot acquire the ability to write an employee row by asking for the
        // reader (FR-001: the corporate directory owns employee lifecycle).
        services.TryAddScoped<
            Application.Abstractions.IEmployeeStore,
            Persistence.Repositories.EmployeeStore>();

        // US2 — conversations, messages, memberships (T090). Split into write and read sides for
        // the same reason as employees above: an endpoint that renders a list must not acquire the
        // ability to allocate a sequence number.
        services.TryAddScoped<
            Application.Abstractions.IConversationRepository,
            Persistence.Repositories.ConversationRepository>();
        services.TryAddScoped<
            Application.Abstractions.IConversationReader,
            Persistence.Repositories.ConversationReader>();
        services.TryAddScoped<
            Application.Abstractions.IMessageRepository,
            Persistence.Repositories.MessageRepository>();
        services.TryAddScoped<
            Application.Abstractions.IMembershipRepository,
            Persistence.Repositories.MembershipRepository>();

        // Monthly partition management (T089). Scoped, because it runs through a DbContext.
        services.TryAddScoped<
            Application.Abstractions.IPartitionMaintenance,
            Persistence.PartitionMaintenance>();

        // Presence and typing (T099). Singleton: it holds no per-request state and talks to the
        // multiplexer, which is itself a singleton by design.
        services.TryAddSingleton<Application.Abstractions.IPresenceStore, Caching.PresenceStore>();
    }

    /// <summary>Reads a required connection string, or explains what cannot start without it.</summary>
    private static string RequireConnectionString(IConfiguration configuration, string name, string why) =>
        configuration.GetConnectionString(name)
        ?? throw new InvalidOperationException($"ConnectionStrings:{name} is not configured. {why}");

    private static void AddMessaging(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<Messaging.RabbitMqOptions>(
            configuration.GetSection(Messaging.RabbitMqOptions.SectionName));

        // One connection per process, shared; channels are created per operation because they
        // are not thread-safe.
        services.TryAddSingleton<Messaging.IRabbitMqConnectionProvider, Messaging.RabbitMqConnectionProvider>();

        // Scoped: it writes through the request's ChatDbContext so the outbox row joins the
        // ambient transaction (Principle VI). A singleton here would silently publish outside it.
        services.TryAddScoped<IEventPublisher, Messaging.OutboxEventPublisher>();
        services.TryAddScoped<Messaging.OutboxDispatcher>();

        // 002 R1 — the doorbell between a commit and the dispatcher. The signal is per process and
        // advisory; the listener that raises it is hosted only where the dispatcher runs (Worker).
        services.TryAddSingleton<Messaging.IOutboxWakeSignal, Messaging.OutboxWakeSignal>();
        services.AddOptions<Messaging.OutboxListenerOptions>()
            .Configure<IConfiguration>((options, config) =>
                options.ConnectionString = config.GetConnectionString("Postgres") ?? string.Empty);

        // Singleton: it owns the AMQP channels for the process's lifetime and creates a scope per
        // delivered message internally, so it must not be scoped itself.
        //
        // Registered here rather than in either composition root because BOTH hosts consume now —
        // the Worker for directory sync, the API for real-time fan-out — and it was previously
        // registered in neither. Every test that starts the API host failed on
        // "Unable to resolve service for type ConsumerHost", and the Worker host was broken the
        // same way with no test to notice.
        services.TryAddSingleton<Messaging.ConsumerHost>();
    }

    private static void AddPersistence(IServiceCollection services, IConfiguration configuration)
    {
        // Resolved from the service provider, NOT from the `configuration` argument, and therefore
        // read when the context is first created rather than while services are being registered.
        // The distinction is not academic: under minimal hosting the configuration passed here is
        // the builder's, and sources added afterwards — which is how a test host or a late
        // environment provider supplies its values — are not visible to an eager read. The symptom
        // is an application that silently connects to whatever the default in appsettings.json
        // says, which in a test run means the deployment's hostnames.
        services.AddDbContext<ChatDbContext>((serviceProvider, options) =>
        {
            ChatDbContext.ConfigureNpgsql(
                (DbContextOptionsBuilder<ChatDbContext>)options,
                RequireConnectionString(
                    serviceProvider.GetRequiredService<IConfiguration>(),
                    "Postgres",
                    "PostgreSQL is the source of truth (Principle VII); the application cannot "
                    + "start without it."));
        });

        // The Application layer depends on IUnitOfWork, never on ChatDbContext — the same
        // instance, reached through the abstraction it owns.
        services.TryAddScoped<IUnitOfWork>(sp => sp.GetRequiredService<ChatDbContext>());

        // Scoped so an audit record written by the pipeline's audit behavior joins the same
        // transaction as the action it describes (Principle IV).
        services.TryAddScoped<IAuditLog, Persistence.Audit.AuditLog>();

        // T206 - Sweeps
        services.TryAddScoped<IRetentionSweep, Persistence.RetentionSweep>();

        // T210 - Orphaned attachments
        services.TryAddScoped<IOrphanReclaimSweep, Persistence.OrphanReclaimSweep>();
    }

    private static void AddCaching(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<RedisCacheOptions>(configuration.GetSection(RedisCacheOptions.SectionName));

        // One multiplexer per process. StackExchange.Redis multiplexes over a single connection
        // and is designed to be shared — creating one per operation exhausts sockets under load,
        // which at 7,000 concurrent connections arrives quickly.
        //
        // Read lazily, for the reason spelled out in AddPersistence.
        services.TryAddSingleton<IConnectionMultiplexer>(serviceProvider =>
        {
            IConfiguration live = serviceProvider.GetRequiredService<IConfiguration>();

            string connectionString = live.GetConnectionString("Redis")
                ?? live[$"{RedisCacheOptions.SectionName}:ConnectionString"]
                ?? throw new InvalidOperationException(
                    "ConnectionStrings:Redis is not configured. Redis backs the SignalR backplane "
                    + "and the membership cache; the application cannot start without it.");

            return ConnectionMultiplexer.Connect(connectionString);
        });

        services.TryAddSingleton<ICacheStore, RedisCacheStore>();

        // The environment-namespaced key builder, shared by the stores that hold a multiplexer
        // directly because they need Redis data structures ICacheStore does not expose.
        services.TryAddSingleton<RedisKeyspace>();
    }
}
