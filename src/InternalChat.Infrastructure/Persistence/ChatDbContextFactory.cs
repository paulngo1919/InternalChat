using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace InternalChat.Infrastructure.Persistence;

/// <summary>
/// Builds a <see cref="ChatDbContext"/> for design-time tooling — <c>dotnet ef migrations add</c>
/// and <c>dotnet ef database update</c>.
/// </summary>
/// <remarks>
/// <para>
/// Used only by the EF Core CLI, never at runtime. Without it the tooling would try to boot the
/// API host to find a context, which would drag the whole composition root (Keycloak, Redis,
/// RabbitMQ) into a command that only needs a connection string.
/// </para>
/// <para>
/// The fallback connection string points at the local Compose stack and is deliberately the same
/// placeholder as <c>deploy/.env.example</c>. It is not a secret and grants nothing outside a
/// developer laptop; real values come from the environment (Principle IV).
/// </para>
/// </remarks>
public sealed class ChatDbContextFactory : IDesignTimeDbContextFactory<ChatDbContext>
{
    private const string ConnectionStringVariable = "INTERNALCHAT_DB_CONNECTION";

    private const string LocalDevelopmentFallback =
        "Host=localhost;Port=5432;Database=internalchat;Username=internalchat;Password=change-me-postgres";

    /// <inheritdoc />
    public ChatDbContext CreateDbContext(string[] args)
    {
        string connectionString =
            Environment.GetEnvironmentVariable(ConnectionStringVariable) ?? LocalDevelopmentFallback;

        DbContextOptionsBuilder<ChatDbContext> builder = new();
        ChatDbContext.ConfigureNpgsql(builder, connectionString);

        return new ChatDbContext(builder.Options);
    }
}
