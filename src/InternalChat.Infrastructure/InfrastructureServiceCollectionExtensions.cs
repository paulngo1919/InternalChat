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

        return services;
    }

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
        string connectionString = configuration.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException(
                "ConnectionStrings:Postgres is not configured. PostgreSQL is the source of truth "
                + "(Principle VII); the application cannot start without it.");

        services.AddDbContext<ChatDbContext>(options =>
        {
            ChatDbContext.ConfigureNpgsql(
                (DbContextOptionsBuilder<ChatDbContext>)options,
                connectionString);
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

        string connectionString = configuration.GetConnectionString("Redis")
            ?? configuration[$"{RedisCacheOptions.SectionName}:ConnectionString"]
            ?? throw new InvalidOperationException(
                "ConnectionStrings:Redis is not configured. Redis backs the SignalR backplane and "
                + "the membership cache; the application cannot start without it.");

        // One multiplexer per process. StackExchange.Redis multiplexes over a single connection
        // and is designed to be shared — creating one per operation exhausts sockets under load,
        // which at 7,000 concurrent connections arrives quickly.
        services.TryAddSingleton<IConnectionMultiplexer>(
            _ => ConnectionMultiplexer.Connect(connectionString));

        services.TryAddSingleton<ICacheStore, RedisCacheStore>();
    }
}
