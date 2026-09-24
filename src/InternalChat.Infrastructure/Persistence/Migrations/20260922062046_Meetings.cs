using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InternalChat.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Meetings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "meeting",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    started_by = table.Column<Guid>(type: "uuid", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    peak_participants = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_meeting", x => x.id);
                    table.CheckConstraint("ck_meeting_ends_after_start", "ended_at IS NULL OR ended_at >= started_at");
                    table.CheckConstraint("ck_meeting_peak_participants", "peak_participants >= 0 AND peak_participants <= 25");
                    table.ForeignKey(
                        name: "fk_meeting_conversation_conversation_id",
                        column: x => x.conversation_id,
                        principalTable: "conversation",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_meeting_employee_started_by",
                        column: x => x.started_by,
                        principalTable: "employee",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "participation",
                columns: table => new
                {
                    meeting_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<Guid>(type: "uuid", nullable: false),
                    joined_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    left_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    shared_screen_seconds = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_participation", x => new { x.meeting_id, x.employee_id });
                    table.CheckConstraint("ck_participation_leaves_after_join", "left_at IS NULL OR left_at >= joined_at");
                    table.CheckConstraint("ck_participation_share_seconds", "shared_screen_seconds >= 0");
                    table.ForeignKey(
                        name: "fk_participation_employee_employee_id",
                        column: x => x.employee_id,
                        principalTable: "employee",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_participation_meeting_meeting_id",
                        column: x => x.meeting_id,
                        principalTable: "meeting",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_meeting_active_by_conversation",
                table: "meeting",
                column: "conversation_id",
                filter: "ended_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_meeting_started_at",
                table: "meeting",
                column: "started_at");

            migrationBuilder.CreateIndex(
                name: "ix_meeting_started_by",
                table: "meeting",
                column: "started_by");

            migrationBuilder.CreateIndex(
                name: "ix_participation_employee",
                table: "participation",
                column: "employee_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "participation");

            migrationBuilder.DropTable(
                name: "meeting");
        }
    }
}
