using InternalChat.Application.Abstractions;
using InternalChat.Infrastructure.Caching;
using InternalChat.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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

        return services;
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
