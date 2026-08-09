using Npgsql;

namespace InternalChat.Seeder;

/// <summary>
/// Checks that the tables a seeding run writes to actually exist before it writes anything.
/// </summary>
/// <remarks>
/// <para>
/// The seeder is a Phase 2 tool that writes Phase 3 and Phase 4 tables: <c>employee</c> and
/// <c>membership</c> arrive with T060, <c>conversation</c> and the partitioned <c>message</c> with
/// T088. Run before those land, a bare INSERT fails with <c>relation "employee" does not exist</c>,
/// which reads like a broken tool rather than an unapplied migration.
/// </para>
/// <para>
/// So the check happens up front and names the task that creates each missing table. The cost is
/// one query; the alternative is somebody spending an afternoon on a connection string that was
/// never the problem.
/// </para>
/// </remarks>
internal static class SchemaPreflight
{
    /// <summary>Which task introduces each table, for the failure message.</summary>
    private static readonly Dictionary<string, string> OwningTask = new(StringComparer.Ordinal)
    {
        ["employee"] = "T060 (User Story 1)",
        ["membership"] = "T060 (User Story 1)",
        ["conversation"] = "T088 (User Story 2)",
        ["message"] = "T088 (User Story 2)",
    };

    /// <summary>
    /// Throws when any of <paramref name="tables"/> is missing.
    /// </summary>
    public static async Task EnsureAsync(
        NpgsqlDataSource dataSource,
        IReadOnlyList<string> tables,
        CancellationToken cancellationToken = default)
    {
        await using NpgsqlConnection connection =
            await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        List<string> missing = [];

        foreach (string table in tables)
        {
            await using NpgsqlCommand command = connection.CreateCommand();
            command.CommandText = "SELECT to_regclass($1) IS NOT NULL";
            command.Parameters.AddWithValue(table);

            object? present = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

            if (present is not true)
            {
                missing.Add(table);
            }
        }

        if (missing.Count == 0)
        {
            return;
        }

        IEnumerable<string> lines = missing.Select(t =>
            $"  - {t}  (created by {(OwningTask.TryGetValue(t, out string? task) ? task : "a later migration")})");

        throw new InvalidOperationException(
            $"""
            The database is missing {missing.Count} table(s) this seeder writes to:

            {string.Join(Environment.NewLine, lines)}

            Apply the migrations first:
              dotnet ef database update --project src/InternalChat.Infrastructure

            If the migration itself does not exist yet, the task that adds it has not been
            implemented — the seeder is ahead of the schema, which is expected during Phase 2.
            """);
    }
}
