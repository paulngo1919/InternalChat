using System;
using InternalChat.Domain.Conversations;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InternalChat.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ConversationAndMessage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:conversation_kind", "direct,group")
                .Annotation("Npgsql:Enum:employee_status", "active,deactivated")
                .Annotation("Npgsql:Enum:history_visibility", "from_join,full")
                .Annotation("Npgsql:Enum:membership_role", "admin,member")
                .Annotation("Npgsql:PostgresExtension:citext", ",,")
                .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,")
                .Annotation("Npgsql:PostgresExtension:unaccent", ",,")
                .OldAnnotation("Npgsql:Enum:employee_status", "active,deactivated")
                .OldAnnotation("Npgsql:Enum:membership_role", "admin,member")
                .OldAnnotation("Npgsql:PostgresExtension:citext", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:pg_trgm", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:unaccent", ",,");

            migrationBuilder.CreateTable(
                name: "conversation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<ConversationKind>(type: "conversation_kind", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    history_visibility = table.Column<HistoryVisibility>(type: "history_visibility", nullable: false),
                    last_seq = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    direct_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_conversation", x => x.id);
                    table.CheckConstraint("ck_conversation_direct_shape", "(kind = 'direct' AND name IS NULL AND direct_key IS NOT NULL) OR (kind = 'group' AND direct_key IS NULL)");
                    table.CheckConstraint("ck_conversation_group_has_name", "kind <> 'group' OR (name IS NOT NULL AND length(btrim(name)) > 0)");
                    table.ForeignKey(
                        name: "fk_conversation_employee_created_by",
                        column: x => x.created_by,
                        principalTable: "employee",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "message_dedup",
                columns: table => new
                {
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_message_key = table.Column<string>(type: "character varying(26)", maxLength: 26, nullable: false),
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_message_dedup", x => new { x.conversation_id, x.client_message_key });
                });

            // Hand-written, because EF Core cannot express PARTITION BY RANGE (research.md D11).
            // The columns, types, constraints, and names are kept identical to what
            // MessageConfiguration describes — the model is what queries are built from, so a
            // divergence here surfaces as a column-not-found error at runtime, not at build time.
            //
            // Partitioning is not a performance nicety: FR-052 makes retention a DROP PARTITION,
            // and on a plain table it would be a DELETE across 125 million rows instead.
            migrationBuilder.Sql(
                """
                CREATE TABLE message (
                    id uuid NOT NULL,
                    sent_at timestamptz NOT NULL,
                    conversation_id uuid NOT NULL,
                    seq bigint NOT NULL,
                    author_id uuid NOT NULL,
                    client_message_key character varying(26) NOT NULL,
                    body character varying(8000),
                    edited_at timestamptz,
                    deleted_at timestamptz,
                    mentions uuid[] NOT NULL DEFAULT '{}'::uuid[],
                    CONSTRAINT pk_message PRIMARY KEY (id, sent_at),
                    CONSTRAINT ck_message_body_length
                        CHECK (body IS NULL OR (length(body) BETWEEN 1 AND 8000)),
                    CONSTRAINT ck_message_body_or_tombstone
                        CHECK ((deleted_at IS NULL AND body IS NOT NULL)
                            OR (deleted_at IS NOT NULL AND body IS NULL)),
                    CONSTRAINT ck_message_seq_positive CHECK (seq >= 1),
                    CONSTRAINT fk_message_conversation_conversation_id
                        FOREIGN KEY (conversation_id) REFERENCES conversation (id) ON DELETE RESTRICT,
                    CONSTRAINT fk_message_employee_author_id
                        FOREIGN KEY (author_id) REFERENCES employee (id) ON DELETE RESTRICT
                ) PARTITION BY RANGE (sent_at);
                """);

            migrationBuilder.CreateIndex(
                name: "ix_conversation_created_by",
                table: "conversation",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "ux_conversation_direct_key",
                table: "conversation",
                column: "direct_key",
                unique: true,
                filter: "kind = 'direct'");

            migrationBuilder.CreateIndex(
                name: "ix_message_author_id",
                table: "message",
                column: "author_id");

            migrationBuilder.CreateIndex(
                name: "ix_message_conversation_seq",
                table: "message",
                columns: new[] { "conversation_id", "seq" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_message_dedup_sent_at",
                table: "message_dedup",
                column: "sent_at");

            migrationBuilder.AddForeignKey(
                name: "fk_membership_conversation_conversation_id",
                table: "membership",
                column: "conversation_id",
                principalTable: "conversation",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            // Partitions for the full 12-month retention window plus three months ahead.
            //
            // Backwards as well as forwards: the seeder (T050) spreads load-test volume across the
            // whole retention window, and an INSERT into a month with no partition does not fall
            // back to the parent — it fails outright. Three ahead so a month boundary crossing at
            // 00:00 cannot reject every send on the platform while waiting for T089's job to run.
            migrationBuilder.Sql(
                """
                SELECT internalchat_ensure_month_partitions(
                    'message',
                    (date_trunc('month', CURRENT_DATE) - interval '12 months')::date,
                    15);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_membership_conversation_conversation_id",
                table: "membership");

            migrationBuilder.DropTable(
                name: "message");

            migrationBuilder.DropTable(
                name: "message_dedup");

            migrationBuilder.DropTable(
                name: "conversation");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:employee_status", "active,deactivated")
                .Annotation("Npgsql:Enum:membership_role", "admin,member")
                .Annotation("Npgsql:PostgresExtension:citext", ",,")
                .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,")
                .Annotation("Npgsql:PostgresExtension:unaccent", ",,")
                .OldAnnotation("Npgsql:Enum:conversation_kind", "direct,group")
                .OldAnnotation("Npgsql:Enum:employee_status", "active,deactivated")
                .OldAnnotation("Npgsql:Enum:history_visibility", "from_join,full")
                .OldAnnotation("Npgsql:Enum:membership_role", "admin,member")
                .OldAnnotation("Npgsql:PostgresExtension:citext", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:pg_trgm", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:unaccent", ",,");
        }
    }
}
