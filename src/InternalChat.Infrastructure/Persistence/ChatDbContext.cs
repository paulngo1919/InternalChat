using System.Reflection;
using InternalChat.Application.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace InternalChat.Infrastructure.Persistence;

/// <summary>
/// The single PostgreSQL context. PostgreSQL is the source of truth for everything
/// (Constitution Principle VII) — Redis and MinIO hold only derived or blob data.
/// </summary>
/// <remarks>
/// <para>
/// Lives in Infrastructure and is never referenced outside it. Constitution Principle I:
/// "No Entity Framework <c>DbContext</c> may be referenced outside the Infrastructure project",
/// enforced by <c>tests/Architecture/CompositionRootTests.cs</c>.
/// </para>
/// <para>
/// Entity configurations are discovered from this assembly rather than written inline, so a new
/// aggregate cannot be silently mismapped by an <c>OnModelCreating</c> block someone forgot to
/// extend.
/// </para>
/// </remarks>
public class ChatDbContext : DbContext, IUnitOfWork
{
    /// <summary>
    /// Name of the EF Core migrations history table.
    /// </summary>
    /// <remarks>
    /// Declared once and applied through <see cref="ConfigureNpgsql"/> everywhere a context is
    /// built. It is snake_case to match every other table (data-model.md), but the reason it is
    /// a shared constant is sharper than consistency: EF's default is <c>__EFMigrationsHistory</c>,
    /// so a context configured without this silently uses a *different* table. The schema then
    /// looks applied while the history reads empty, and anything keyed off applied-migrations —
    /// including a test-database reset — behaves as though the database were brand new.
    /// </remarks>
    public const string MigrationsHistoryTableName = "__ef_migrations_history";

    /// <summary>Creates the context.</summary>
    public ChatDbContext(DbContextOptions<ChatDbContext> options)
        : base(options)
    {
    }

    /// <summary>
    /// Domain events awaiting publication, written in the same transaction as the state change
    /// they describe (Principle VI).
    /// </summary>
    public DbSet<Outbox.OutboxMessage> OutboxMessages => Set<Outbox.OutboxMessage>();

    /// <summary>
    /// Consumer deduplication records. At-least-once delivery is safe only because of these.
    /// </summary>
    public DbSet<Outbox.ProcessedMessage> ProcessedMessages => Set<Outbox.ProcessedMessage>();

    /// <summary>
    /// Append-only audit log (FR-006). The database grants INSERT and SELECT only, so this
    /// <see cref="DbSet{TEntity}"/> cannot be used to alter history even by mistake.
    /// </summary>
    public DbSet<Audit.AuditEventRecord> AuditEvents => Set<Audit.AuditEventRecord>();

    /// <summary>
    /// Directory projection (FR-001). The platform never owns employee lifecycle.
    /// </summary>
    public DbSet<Domain.Employees.Employee> Employees => Set<Domain.Employees.Employee>();

    /// <summary>
    /// The authorization record. Every access decision in the platform resolves to a row here.
    /// </summary>
    public DbSet<Domain.Conversations.Membership> Memberships => Set<Domain.Conversations.Membership>();

    /// <summary>
    /// Applies the Npgsql configuration every context must share — production, design-time
    /// tooling, and tests alike.
    /// </summary>
    /// <remarks>
    /// Centralised so no caller can accidentally omit the history table name. See
    /// <see cref="MigrationsHistoryTableName"/> for what goes wrong when one does.
    /// </remarks>
    public static DbContextOptionsBuilder<ChatDbContext> ConfigureNpgsql(
        DbContextOptionsBuilder<ChatDbContext> builder,
        string connectionString)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseNpgsql(
            connectionString,
            npgsql =>
            {
                npgsql.MigrationsHistoryTable(MigrationsHistoryTableName);

                // PostgreSQL enum types rather than text plus a CHECK constraint (data-model.md).
                // The mapping must be declared on the connection as well as on the model — without
                // it Npgsql cannot read the value back and every query throws at materialisation,
                // not at startup, which is a confusing place to discover a missing line.
                npgsql.MapEnum<Domain.Employees.EmployeeStatus>("employee_status");
                npgsql.MapEnum<Domain.Conversations.MembershipRole>("membership_role");
            });

        return builder;
    }

    /// <inheritdoc />
    public async Task<TResult> ExecuteInTransactionAsync<TResult>(
        Func<CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        // Reuse an ambient transaction rather than nesting. A use case invoked from inside
        // another must not commit independently — a partial write is exactly what the
        // transactional outbox exists to prevent (Principle VI).
        if (Database.CurrentTransaction is not null)
        {
            return await operation(cancellationToken).ConfigureAwait(false);
        }

        IExecutionStrategy strategy = Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            await using IDbContextTransaction transaction =
                await Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                TResult result = await operation(cancellationToken).ConfigureAwait(false);
                await SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return result;
            }
            catch
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                throw;
            }
        }).ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        base.OnModelCreating(modelBuilder);

        modelBuilder.HasPostgresExtension("unaccent");
        modelBuilder.HasPostgresExtension("pg_trgm");

        // Case-insensitive text, for employee.email. Declared here rather than in the entity
        // configuration because an extension is a database-level object, not a column property.
        modelBuilder.HasPostgresExtension("citext");

        // Enum types are declared ONCE, by MapEnum in ConfigureNpgsql. Declaring them here as well
        // with HasPostgresEnum is the obvious-looking thing to do and is wrong: the provider then
        // emits two annotations per type under different keys, and it sorts the labels for one of
        // them — the first attempt produced membership_role as both "member,admin" and
        // "admin,member" in the same migration. Enum label order is the type's ordinal order in
        // PostgreSQL, so that is a coin flip over what "ORDER BY role" means.
        modelBuilder.ApplyConfigurationsFromAssembly(Assembly.GetExecutingAssembly());

        ApplySnakeCaseNames(modelBuilder);
    }

    /// <inheritdoc />
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);

        base.ConfigureConventions(configurationBuilder);

        // Every timestamp is timestamptz. Message ordering must not depend on a client clock
        // (FR-012), and retention must land inside a stated tolerance (SC-025); neither is
        // possible if some columns silently store local time.
        configurationBuilder.Properties<DateTimeOffset>().HaveColumnType("timestamptz");
        configurationBuilder.Properties<DateTimeOffset?>().HaveColumnType("timestamptz");

        // Bound every string by default so a missing HasMaxLength cannot produce an unbounded
        // text column that a 125-million-row table then has to live with.
        configurationBuilder.Properties<string>().HaveMaxLength(512);
    }

    /// <summary>
    /// Rewrites table, column, key, and index names to snake_case.
    /// </summary>
    /// <remarks>
    /// Done by hand rather than with the EFCore.NamingConventions package. It is roughly twenty
    /// lines, and Principle VIII prefers one fewer dependency whose licence has to be
    /// re-verified at every version bump. data-model.md specifies snake_case in the database and
    /// PascalCase in the domain, so this is the only place the two meet.
    /// </remarks>
    private static void ApplySnakeCaseNames(ModelBuilder modelBuilder)
    {
        foreach (Microsoft.EntityFrameworkCore.Metadata.IMutableEntityType entity in modelBuilder.Model.GetEntityTypes())
        {
            string? tableName = entity.GetTableName();
            if (tableName is not null)
            {
                entity.SetTableName(ToSnakeCase(tableName));
            }

            foreach (Microsoft.EntityFrameworkCore.Metadata.IMutableProperty property in entity.GetProperties())
            {
                property.SetColumnName(ToSnakeCase(property.GetColumnName()));
            }

            foreach (Microsoft.EntityFrameworkCore.Metadata.IMutableKey key in entity.GetKeys())
            {
                string? name = key.GetName();
                if (name is not null)
                {
                    key.SetName(ToSnakeCase(name));
                }
            }

            foreach (Microsoft.EntityFrameworkCore.Metadata.IMutableForeignKey foreignKey in entity.GetForeignKeys())
            {
                string? name = foreignKey.GetConstraintName();
                if (name is not null)
                {
                    foreignKey.SetConstraintName(ToSnakeCase(name));
                }
            }

            foreach (Microsoft.EntityFrameworkCore.Metadata.IMutableIndex index in entity.GetIndexes())
            {
                string? name = index.GetDatabaseName();
                if (name is not null)
                {
                    index.SetDatabaseName(ToSnakeCase(name));
                }
            }
        }
    }

    internal static string ToSnakeCase(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return name;
        }

        System.Text.StringBuilder builder = new(name.Length + 8);

        for (int i = 0; i < name.Length; i++)
        {
            char current = name[i];

            if (char.IsUpper(current))
            {
                bool previousIsLower = i > 0 && char.IsLower(name[i - 1]);
                bool nextIsLower = i + 1 < name.Length && char.IsLower(name[i + 1]);

                // Insert a separator at a lower-to-upper boundary, and at the end of an acronym
                // run, so "ConversationID" becomes conversation_id rather than conversation_i_d.
                if (i > 0 && (previousIsLower || nextIsLower) && builder[^1] != '_')
                {
                    builder.Append('_');
                }

                builder.Append(char.ToLowerInvariant(current));
            }
            else
            {
                builder.Append(current);
            }
        }

        return builder.ToString();
    }
}
