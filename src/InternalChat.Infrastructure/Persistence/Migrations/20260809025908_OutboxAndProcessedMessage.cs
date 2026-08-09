using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InternalChat.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// The transactional outbox and consumer deduplication tables.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Constitution Principle VI. <c>outbox_message</c> makes a state change and its event commit
    /// atomically; <c>processed_message</c> makes at-least-once delivery safe by letting each
    /// consumer recognise a message it has already handled.
    /// </para>
    /// <para>
    /// Forward-only and additive, so it is safe to apply while the previous application version
    /// is still running (Principle VII).
    /// </para>
    /// </remarks>
    public partial class OutboxAndProcessedMessage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "outbox_message",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    routing_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),

                    // Deliberately NO maxLength. The context-wide 512-character string convention
                    // applied here on first generation, declaring a jsonb column as max 512.
                    // PostgreSQL ignores length on jsonb so nothing would have failed — the model
                    // would simply have carried a false constraint until someone changed the
                    // column type and silently truncated event payloads.
                    payload = table.Column<string>(type: "jsonb", nullable: false),

                    trace_parent = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    dispatched_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    last_error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbox_message", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "processed_message",
                columns: table => new
                {
                    consumer_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    // This composite key IS the deduplication mechanism: a redelivered message
                    // violates it, and that violation is what the consumer reads as
                    // "already handled".
                    table.PrimaryKey("pk_processed_message", x => new { x.consumer_name, x.message_id });
                });

            // The dispatcher's only query, running continuously. Partial, so it stays
            // proportional to the undispatched backlog rather than to the whole table — which at
            // 100 messages/second grows past 8 million rows a day.
            migrationBuilder.CreateIndex(
                name: "ix_outbox_message_pending",
                table: "outbox_message",
                column: "occurred_at",
                filter: "dispatched_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_processed_message_processed_at",
                table: "processed_message",
                column: "processed_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "processed_message");
            migrationBuilder.DropTable(name: "outbox_message");
        }
    }
}
