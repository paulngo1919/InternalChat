namespace InternalChat.Infrastructure.Caching;

/// <summary>Configuration for the Redis cache.</summary>
public sealed class RedisCacheOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Redis";

    /// <summary>Redis connection string.</summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Environment segment of the key namespace, per data-model.md:
    /// <c>internalchat:{env}:{entity}:{id}</c>.
    /// </summary>
    /// <remarks>
    /// Namespacing by environment is what stops a staging instance pointed at the wrong Redis
    /// from serving production authorization decisions out of a shared keyspace.
    /// </remarks>
    public string Environment { get; set; } = "local";
}
