using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InternalChat.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// 002 T016 — rings <c>outbox_ready</c> whenever outbox rows are committed (research R1,
    /// data-model §1).
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what lets the dispatcher publish a message within a round trip of its commit rather
    /// than on its next poll. PostgreSQL delivers a <c>NOTIFY</c> only when the transaction that
    /// raised it commits, and discards it on rollback, so the doorbell can never ring for a row the
    /// dispatcher cannot yet see — or for a send that was refused.
    /// </para>
    /// <para>
    /// Statement-level, so a multi-row insert rings once; PostgreSQL also collapses identical
    /// notifications within one transaction. The payload is empty on purpose: the channel says
    /// "look", and the dispatcher always reads the table (FR-056 keeps identifiers off it anyway).
    /// </para>
    /// <para>
    /// <b>Live-safe.</b> Creating a function and a trigger takes a brief lock on
    /// <c>outbox_message</c> only. An application version that does not listen is unaffected — it
    /// keeps polling, and notifications nobody listens for cost nothing.
    /// </para>
    /// </remarks>
    public partial class OutboxNotifyTrigger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE OR REPLACE FUNCTION outbox_notify() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    PERFORM pg_notify('outbox_ready', '');
                    RETURN NULL;
                END;
                $$;
                """);

            migrationBuilder.Sql(
                """
                CREATE TRIGGER outbox_message_notify
                AFTER INSERT ON outbox_message
                FOR EACH STATEMENT
                EXECUTE FUNCTION outbox_notify();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS outbox_message_notify ON outbox_message;");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS outbox_notify();");
        }
    }
}
