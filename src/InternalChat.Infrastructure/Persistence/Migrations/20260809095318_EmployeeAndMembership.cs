using System;
using InternalChat.Domain.Conversations;
using InternalChat.Domain.Employees;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InternalChat.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// T060 — <c>employee</c> and <c>membership</c>, the directory projection and the
    /// authorization record (data-model.md).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Forward-only and additive, so it applies safely while the previous version is still running
    /// (Principle VII).
    /// </para>
    /// <para>
    /// <b>No foreign key from <c>membership.conversation_id</c> to <c>conversation</c>.</b> That
    /// table arrives with T088 (US2); declaring the constraint here would make this migration
    /// unapplyable. T088 adds it, which is itself an additive change.
    /// </para>
    /// <para>
    /// <b>Do not order by <c>membership_role</c> in SQL.</b> The provider emits enum labels
    /// alphabetically, so PostgreSQL created the type as <c>admin</c> then <c>member</c> — the
    /// reverse of the C# ordering the evaluator relies on. Values are stored and read by label, so
    /// nothing is wrong today, but <c>ORDER BY role</c> or <c>role &gt; 'member'</c> in a future
    /// query would mean the opposite of what it reads like. Compare roles in C#, where
    /// <c>ConversationMembershipEvaluatorTests</c> pins the ordering.
    /// </para>
    /// </remarks>
    public partial class EmployeeAndMembership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:employee_status", "active,deactivated")
                .Annotation("Npgsql:Enum:membership_role", "admin,member")
                .Annotation("Npgsql:PostgresExtension:citext", ",,")
                .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,")
                .Annotation("Npgsql:PostgresExtension:unaccent", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:pg_trgm", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:unaccent", ",,");

            migrationBuilder.CreateTable(
                name: "employee",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    external_subject = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    email = table.Column<string>(type: "citext", maxLength: 512, nullable: false),
                    avatar_url = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    status = table.Column<EmployeeStatus>(type: "employee_status", nullable: false),
                    deactivated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_employee", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "membership",
                columns: table => new
                {
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<MembershipRole>(type: "membership_role", nullable: false),
                    joined_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    visible_from_seq = table.Column<long>(type: "bigint", nullable: false),
                    muted_until = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    removed_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_membership", x => new { x.conversation_id, x.employee_id });
                    table.ForeignKey(
                        name: "fk_membership_employee_employee_id",
                        column: x => x.employee_id,
                        principalTable: "employee",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_employee_display_name_trgm",
                table: "employee",
                column: "display_name")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "ix_employee_email",
                table: "employee",
                column: "email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_employee_external_subject",
                table: "employee",
                column: "external_subject",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_membership_active_lookup",
                table: "membership",
                columns: new[] { "conversation_id", "employee_id" },
                filter: "removed_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_membership_employee_removed",
                table: "membership",
                columns: new[] { "employee_id", "removed_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "membership");

            migrationBuilder.DropTable(
                name: "employee");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,")
                .Annotation("Npgsql:PostgresExtension:unaccent", ",,")
                .OldAnnotation("Npgsql:Enum:employee_status", "active,deactivated")
                .OldAnnotation("Npgsql:Enum:membership_role", "admin,member")
                .OldAnnotation("Npgsql:PostgresExtension:citext", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:pg_trgm", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:unaccent", ",,");
        }
    }
}
