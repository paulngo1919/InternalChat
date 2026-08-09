using System;
using System.Net;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace InternalChat.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AuditEvent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audit_event",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: true),
                    action = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    subject_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    subject_id = table.Column<Guid>(type: "uuid", nullable: true),
                    source_ip = table.Column<IPAddress>(type: "inet", nullable: true),
                    outcome = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    detail = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_event", x => x.id);
                    table.CheckConstraint("ck_audit_event_outcome", "outcome IN ('success', 'denied', 'error')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_audit_event_actor",
                table: "audit_event",
                columns: new[] { "actor_id", "occurred_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_audit_event_subject",
                table: "audit_event",
                columns: new[] { "subject_type", "subject_id", "occurred_at" },
                descending: new[] { false, false, true });

            ApplyAppendOnlyGrants(migrationBuilder);
        }

        /// <inheritdoc />
        /// <summary>
        /// Makes the audit log append-only at the database level.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Constitution Principle IV and FR-006: audit records "cannot be altered or deleted by
        /// application code". Enforcing that only in C# would leave it one careless
        /// <c>ExecuteSqlRaw</c> away from being untrue — and the code most likely to tamper with
        /// the log is exactly the code an investigator is examining.
        /// </para>
        /// <para>
        /// Privileges are held by a group role rather than granted to a login user directly, so
        /// rotating the application's database user does not silently drop the restriction. The
        /// deployment grants <c>internalchat_app</c> to whichever login user the API connects as.
        /// </para>
        /// <para>
        /// SELECT and INSERT only. No UPDATE, no DELETE, and no TRUNCATE — TRUNCATE matters
        /// because it is not covered by DELETE and would otherwise erase the entire log in one
        /// statement.
        /// </para>
        /// </remarks>
        private static void ApplyAppendOnlyGrants(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'internalchat_app') THEN
                        CREATE ROLE internalchat_app NOLOGIN;
                    END IF;
                END
                $$;
                """);

            // Revoke first: PUBLIC may hold privileges by default, and granting without
            // revoking would leave a wider door open beside the narrow one.
            migrationBuilder.Sql("REVOKE ALL ON TABLE audit_event FROM PUBLIC;");
            migrationBuilder.Sql("REVOKE ALL ON TABLE audit_event FROM internalchat_app;");
            migrationBuilder.Sql("GRANT SELECT, INSERT ON TABLE audit_event TO internalchat_app;");

            // The identity column's sequence needs its own grant for INSERT to succeed.
            migrationBuilder.Sql(
                """
                DO $$
                DECLARE
                    seq_name text;
                BEGIN
                    SELECT pg_get_serial_sequence('audit_event', 'id') INTO seq_name;
                    IF seq_name IS NOT NULL THEN
                        EXECUTE format('GRANT USAGE, SELECT ON SEQUENCE %s TO internalchat_app', seq_name);
                    END IF;
                END
                $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("REVOKE ALL ON TABLE audit_event FROM internalchat_app;");

            migrationBuilder.DropTable(
                name: "audit_event");

            // The role is intentionally left in place. It may hold grants on other tables from
            // later migrations, and dropping it here would break them.
        }
    }
}
