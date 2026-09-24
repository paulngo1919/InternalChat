using System;
using InternalChat.Domain.Meetings;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InternalChat.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ScreenShare : Migration
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
                .Annotation("Npgsql:Enum:share_scope", "screen,window")
                .Annotation("Npgsql:Enum:share_stop_reason", "meeting_ended,participant_left,stopped,superseded")
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

            migrationBuilder.CreateTable(
                name: "share_session",
                columns: table => new
                {
                    meeting_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<Guid>(type: "uuid", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    scope = table.Column<ShareScope>(type: "share_scope", nullable: false),
                    stopped_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    stop_reason = table.Column<ShareStopReason>(type: "share_stop_reason", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_share_session", x => new { x.meeting_id, x.employee_id, x.started_at });
                    table.CheckConstraint("ck_share_session_stop_reason", "(stopped_at IS NULL) = (stop_reason IS NULL)");
                    table.CheckConstraint("ck_share_session_stops_after_start", "stopped_at IS NULL OR stopped_at >= started_at");
                    table.ForeignKey(
                        name: "fk_share_session_meeting_meeting_id",
                        column: x => x.meeting_id,
                        principalTable: "meeting",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ux_share_session_one_active_per_meeting",
                table: "share_session",
                column: "meeting_id",
                unique: true,
                filter: "stopped_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "share_session");

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
                .OldAnnotation("Npgsql:Enum:attachment_kind", "image,video")
                .OldAnnotation("Npgsql:Enum:conversation_kind", "direct,group")
                .OldAnnotation("Npgsql:Enum:employee_status", "active,deactivated")
                .OldAnnotation("Npgsql:Enum:history_visibility", "from_join,full")
                .OldAnnotation("Npgsql:Enum:membership_role", "admin,member")
                .OldAnnotation("Npgsql:Enum:scan_status", "clean,failed,infected,pending")
                .OldAnnotation("Npgsql:Enum:share_scope", "screen,window")
                .OldAnnotation("Npgsql:Enum:share_stop_reason", "meeting_ended,participant_left,stopped,superseded")
                .OldAnnotation("Npgsql:PostgresExtension:citext", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:pg_trgm", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:unaccent", ",,");
        }
    }
}
