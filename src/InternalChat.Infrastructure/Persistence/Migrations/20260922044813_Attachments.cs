using System;
using InternalChat.Domain.Attachments;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InternalChat.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Attachments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:attachment_kind", "image,video")
                .Annotation("Npgsql:Enum:conversation_kind", "direct,group")
                .Annotation("Npgsql:Enum:employee_status", "active,deactivated")
                .Annotation("Npgsql:Enum:history_visibility", "from_join,full")
                .Annotation("Npgsql:Enum:membership_role", "admin,member")
                .Annotation("Npgsql:Enum:scan_status", "clean,failed,infected,pending")
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

            migrationBuilder.CreateTable(
                name: "attachment",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    message_id = table.Column<Guid>(type: "uuid", nullable: true),
                    message_sent_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    uploaded_by = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<AttachmentKind>(type: "attachment_kind", nullable: false),
                    content_type = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    byte_size = table.Column<long>(type: "bigint", nullable: false),
                    duration_seconds = table.Column<int>(type: "integer", nullable: true),
                    file_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    poster_object_key = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    scan_status = table.Column<ScanVerdict>(type: "scan_status", nullable: false),
                    scanned_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_attachment", x => x.id);
                    table.CheckConstraint("ck_attachment_byte_size", "byte_size > 0 AND (\n    (kind = 'image' AND byte_size <= 26214400)\n OR (kind = 'video' AND byte_size <= 524288000))");
                    table.CheckConstraint("ck_attachment_duration", "(kind = 'video' AND duration_seconds IS NOT NULL\n     AND duration_seconds BETWEEN 1 AND 600)\nOR (kind <> 'video' AND duration_seconds IS NULL)");
                    table.CheckConstraint("ck_attachment_message_reference", "(message_id IS NULL) = (message_sent_at IS NULL)");
                    table.CheckConstraint("ck_attachment_poster_is_video", "poster_object_key IS NULL OR kind = 'video'");
                    table.CheckConstraint("ck_attachment_scanned_at", "(scan_status = 'pending') = (scanned_at IS NULL)");
                    table.ForeignKey(
                        name: "fk_attachment_conversation_conversation_id",
                        column: x => x.conversation_id,
                        principalTable: "conversation",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_attachment_employee_uploaded_by",
                        column: x => x.uploaded_by,
                        principalTable: "employee",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_attachment_conversation",
                table: "attachment",
                column: "conversation_id");

            migrationBuilder.CreateIndex(
                name: "ix_attachment_message",
                table: "attachment",
                column: "message_id",
                filter: "message_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_attachment_pending_scan",
                table: "attachment",
                column: "scan_status",
                filter: "scan_status = 'pending'");

            migrationBuilder.CreateIndex(
                name: "ix_attachment_uploaded_by",
                table: "attachment",
                column: "uploaded_by");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "attachment");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:conversation_kind", "direct,group")
                .Annotation("Npgsql:Enum:employee_status", "active,deactivated")
                .Annotation("Npgsql:Enum:history_visibility", "from_join,full")
                .Annotation("Npgsql:Enum:membership_role", "admin,member")
                .Annotation("Npgsql:PostgresExtension:citext", ",,")
                .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,")
                .Annotation("Npgsql:PostgresExtension:unaccent", ",,")
                .OldAnnotation("Npgsql:Enum:attachment_kind", "image,video")
                .OldAnnotation("Npgsql:Enum:conversation_kind", "direct,group")
                .OldAnnotation("Npgsql:Enum:employee_status", "active,deactivated")
                .OldAnnotation("Npgsql:Enum:history_visibility", "from_join,full")
                .OldAnnotation("Npgsql:Enum:membership_role", "admin,member")
                .OldAnnotation("Npgsql:Enum:scan_status", "clean,failed,infected,pending")
                .OldAnnotation("Npgsql:PostgresExtension:citext", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:pg_trgm", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:unaccent", ",,");
        }
    }
}
