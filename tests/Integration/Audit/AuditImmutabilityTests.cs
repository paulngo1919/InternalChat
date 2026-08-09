using InternalChat.Application.Abstractions;
using InternalChat.Infrastructure.Persistence;
using InternalChat.Infrastructure.Persistence.Audit;
using InternalChat.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace InternalChat.IntegrationTests.Audit;

/// <summary>
/// Proves the audit log is append-only as enforced by PostgreSQL, not merely by convention.
/// </summary>
/// <remarks>
/// <para>
/// T049. FR-006 requires audit records that "application code cannot alter or delete", and
/// SC-021 requires an administrator to reconstruct access history from this log alone. Both
/// claims collapse if the application's database user can rewrite rows.
/// </para>
/// <para>
/// These tests connect as the RESTRICTED role, not as the Testcontainers superuser. Asserting
/// against the superuser would prove nothing — it can do anything by definition, and a test that
/// passed as superuser would give false confidence about production.
/// </para>
/// </remarks>
public sealed class AuditImmutabilityTests : IntegrationTestBase, IAsyncLifetime
{
    private const string RestrictedUser = "audit_probe_user";
    private const string RestrictedPassword = "audit_probe_password";

    private string _restrictedConnectionString = string.Empty;

    public AuditImmutabilityTests(StackFixture stack)
        : base(stack)
    {
    }

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();

        await using ChatDbContext context = CreateDbContext();

        // A login user granted the same group role the deployment grants the API. This is what
        // production privileges look like, reproduced in the test.
        await context.Database.ExecuteSqlRawAsync(
            $"""
            DO $$
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{RestrictedUser}') THEN
                    CREATE ROLE {RestrictedUser} LOGIN PASSWORD '{RestrictedPassword}';
                END IF;
            END
            $$;
            """);

        await context.Database.ExecuteSqlRawAsync($"GRANT CONNECT ON DATABASE internalchat TO {RestrictedUser};");
        await context.Database.ExecuteSqlRawAsync($"GRANT USAGE ON SCHEMA public TO {RestrictedUser};");
        await context.Database.ExecuteSqlRawAsync($"GRANT internalchat_app TO {RestrictedUser};");

        NpgsqlConnectionStringBuilder builder = new(Stack.PostgresConnectionString)
        {
            Username = RestrictedUser,
            Password = RestrictedPassword,
        };

        _restrictedConnectionString = builder.ConnectionString;
    }

    private async Task<long> SeedRecordAsync()
    {
        await using ChatDbContext context = CreateDbContext();

        AuditLog log = new(context, NullLogger<AuditLog>.Instance);
        await log.RecordAsync(new AuditEntry(
            Action: "membership.removed",
            ActorId: Guid.CreateVersion7(),
            SubjectType: "conversation",
            SubjectId: Guid.CreateVersion7(),
            SourceIp: "10.0.0.5",
            Outcome: AuditOutcome.Success));

        await context.SaveChangesAsync();

        return await context.AuditEvents.OrderByDescending(a => a.Id).Select(a => a.Id).FirstAsync();
    }

    private async Task<NpgsqlConnection> OpenRestrictedAsync()
    {
        NpgsqlConnection connection = new(_restrictedConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    [Fact]
    public async Task Restricted_role_can_insert_an_audit_record()
    {
        await using NpgsqlConnection connection = await OpenRestrictedAsync();

        await using NpgsqlCommand insert = connection.CreateCommand();
        insert.CommandText =
            """
            INSERT INTO audit_event (occurred_at, action, subject_type, outcome, detail)
            VALUES (now(), 'auth.signed_in', 'employee', 'success', '{}'::jsonb)
            """;

        // Insert must work, or the application cannot audit anything. Restricting the log is
        // only useful if writing to it still succeeds.
        Assert.Equal(1, await insert.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task Restricted_role_cannot_update_an_audit_record()
    {
        long id = await SeedRecordAsync();

        await using NpgsqlConnection connection = await OpenRestrictedAsync();

        await using NpgsqlCommand update = connection.CreateCommand();
        update.CommandText = "UPDATE audit_event SET outcome = 'success' WHERE id = @id";
        update.Parameters.AddWithValue("id", id);

        // Rewriting a denial as a success is the single most valuable thing an attacker could do
        // to this table, and the reason UPDATE is withheld rather than merely unused.
        PostgresException error = await Assert.ThrowsAsync<PostgresException>(
            () => update.ExecuteNonQueryAsync());

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
    }

    [Fact]
    public async Task Restricted_role_cannot_delete_an_audit_record()
    {
        long id = await SeedRecordAsync();

        await using NpgsqlConnection connection = await OpenRestrictedAsync();

        await using NpgsqlCommand delete = connection.CreateCommand();
        delete.CommandText = "DELETE FROM audit_event WHERE id = @id";
        delete.Parameters.AddWithValue("id", id);

        PostgresException error = await Assert.ThrowsAsync<PostgresException>(
            () => delete.ExecuteNonQueryAsync());

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
    }

    [Fact]
    public async Task Restricted_role_cannot_truncate_the_audit_log()
    {
        await SeedRecordAsync();

        await using NpgsqlConnection connection = await OpenRestrictedAsync();

        await using NpgsqlCommand truncate = connection.CreateCommand();
        truncate.CommandText = "TRUNCATE TABLE audit_event";

        // TRUNCATE is a separate privilege from DELETE. Withholding DELETE while leaving
        // TRUNCATE available would let one statement erase the entire year of history.
        PostgresException error = await Assert.ThrowsAsync<PostgresException>(
            () => truncate.ExecuteNonQueryAsync());

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
    }

    [Fact]
    public async Task Restricted_role_can_read_the_audit_log()
    {
        await SeedRecordAsync();

        await using NpgsqlConnection connection = await OpenRestrictedAsync();

        await using NpgsqlCommand read = connection.CreateCommand();
        read.CommandText = "SELECT count(*) FROM audit_event";

        // SC-021 requires the log to be answerable. Append-only means immutable, not unreadable.
        Assert.True(Convert.ToInt64(await read.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture) > 0);
    }

    [Fact]
    public async Task Recorded_entry_keeps_actor_subject_time_and_outcome()
    {
        Guid actorId = Guid.CreateVersion7();
        Guid subjectId = Guid.CreateVersion7();

        await using (ChatDbContext write = CreateDbContext())
        {
            AuditLog log = new(write, NullLogger<AuditLog>.Instance);
            await log.RecordAsync(new AuditEntry(
                Action: "membership.removed",
                ActorId: actorId,
                SubjectType: "conversation",
                SubjectId: subjectId,
                SourceIp: "10.0.0.5",
                Outcome: AuditOutcome.Denied,
                Detail: new Dictionary<string, string> { ["reason"] = "not_a_member" }));

            await write.SaveChangesAsync();
        }

        await using ChatDbContext read = CreateDbContext();
        AuditEventRecord record = await read.AuditEvents.SingleAsync(a => a.ActorId == actorId);

        // FR-006 lists exactly these fields. A log missing any one of them cannot answer
        // "who did what to which thing, from where, and did it succeed".
        Assert.Equal("membership.removed", record.Action);
        Assert.Equal("conversation", record.SubjectType);
        Assert.Equal(subjectId, record.SubjectId);
        Assert.Equal("denied", record.Outcome);
        Assert.Equal("10.0.0.5", record.SourceIp?.ToString());
        Assert.NotEqual(default, record.OccurredAt);
        Assert.Contains("not_a_member", record.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unparseable_source_address_does_not_cost_the_record()
    {
        Guid actorId = Guid.CreateVersion7();

        await using (ChatDbContext write = CreateDbContext())
        {
            AuditLog log = new(write, NullLogger<AuditLog>.Instance);
            await log.RecordAsync(new AuditEntry(
                Action: "auth.denied",
                ActorId: actorId,
                SubjectType: "employee",
                SubjectId: null,
                SourceIp: "not-an-ip-address",
                Outcome: AuditOutcome.Denied));

            await write.SaveChangesAsync();
        }

        await using ChatDbContext read = CreateDbContext();
        AuditEventRecord record = await read.AuditEvents.SingleAsync(a => a.ActorId == actorId);

        // A malformed X-Forwarded-For must not drop the row. The log would otherwise be least
        // complete exactly when something unusual is happening.
        Assert.Null(record.SourceIp);
        Assert.Equal("auth.denied", record.Action);
    }
}
