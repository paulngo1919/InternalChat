using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InternalChat.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Database-level scaffolding: required PostgreSQL extensions and the monthly partition
    /// management functions.
    /// </summary>
    /// <remarks>
    /// <para>
    /// No tables yet — entity tables arrive with the user stories that own them. What is here has
    /// no entity dependency and must exist before any of them:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>
    ///     <c>unaccent</c> — diacritic-insensitive full-text search (research.md D9). Vietnamese
    ///     is space-delimited, so <c>simple</c> plus <c>unaccent</c> works adequately without
    ///     stemming.
    ///   </description></item>
    ///   <item><description>
    ///     <c>pg_trgm</c> — trigram index for directory name search (FR-007).
    ///   </description></item>
    ///   <item><description>
    ///     Partition helpers — the <c>messages</c> table is partitioned by month (research.md
    ///     D11). At roughly 125 million rows a year, retention has to be
    ///     <c>DROP TABLE partition</c>; deleting ~10 million rows a month row-by-row would
    ///     dominate the database's I/O budget and leave bloat behind.
    ///   </description></item>
    /// </list>
    /// <para>
    /// Forward-only and safe to apply while the previous application version is still running
    /// (Constitution Principle VII): this migration only adds objects.
    /// </para>
    /// </remarks>
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,")
                .Annotation("Npgsql:PostgresExtension:unaccent", ",,");

            // Creates one monthly partition. Idempotent — returns the existing partition rather
            // than failing, so the maintenance job can run on any schedule without coordination.
            migrationBuilder.Sql(
                """
                CREATE OR REPLACE FUNCTION internalchat_create_month_partition(
                    parent_table text,
                    month_start date)
                RETURNS text
                LANGUAGE plpgsql
                AS $function$
                DECLARE
                    partition_name text;
                    range_start timestamptz;
                    range_end timestamptz;
                BEGIN
                    range_start := date_trunc('month', month_start)::timestamptz;
                    range_end := (date_trunc('month', month_start) + interval '1 month')::timestamptz;
                    partition_name := format('%s_%s', parent_table, to_char(range_start, 'YYYY_MM'));

                    IF to_regclass(quote_ident(partition_name)) IS NOT NULL THEN
                        RETURN partition_name;
                    END IF;

                    EXECUTE format(
                        'CREATE TABLE %I PARTITION OF %I FOR VALUES FROM (%L) TO (%L)',
                        partition_name, parent_table, range_start, range_end);

                    RETURN partition_name;
                END;
                $function$;
                """);

            // Ensures the current month plus N ahead exist. Called by the maintenance job.
            //
            // Running ahead is not optional: an INSERT landing in a month with no partition
            // fails outright. A month boundary crossing at 00:00 with no partition ready would
            // reject every message being sent, so the job keeps a buffer.
            migrationBuilder.Sql(
                """
                CREATE OR REPLACE FUNCTION internalchat_ensure_month_partitions(
                    parent_table text,
                    from_month date,
                    months_ahead integer)
                RETURNS integer
                LANGUAGE plpgsql
                AS $function$
                DECLARE
                    created integer := 0;
                    offset_index integer;
                    target_month date;
                BEGIN
                    IF months_ahead < 0 THEN
                        RAISE EXCEPTION 'months_ahead must not be negative, got %', months_ahead;
                    END IF;

                    FOR offset_index IN 0..months_ahead LOOP
                        target_month := (date_trunc('month', from_month) + (offset_index || ' month')::interval)::date;
                        PERFORM internalchat_create_month_partition(parent_table, target_month);
                        created := created + 1;
                    END LOOP;

                    RETURN created;
                END;
                $function$;
                """);

            // Detaches and drops one monthly partition, for the retention sweep (FR-052).
            //
            // DETACH before DROP so the parent table is not exclusively locked for the whole
            // drop. On a live chat platform an exclusive lock on `messages` stops every send.
            migrationBuilder.Sql(
                """
                CREATE OR REPLACE FUNCTION internalchat_drop_month_partition(
                    parent_table text,
                    month_start date)
                RETURNS boolean
                LANGUAGE plpgsql
                AS $function$
                DECLARE
                    partition_name text;
                    range_start timestamptz;
                BEGIN
                    range_start := date_trunc('month', month_start)::timestamptz;
                    partition_name := format('%s_%s', parent_table, to_char(range_start, 'YYYY_MM'));

                    IF to_regclass(quote_ident(partition_name)) IS NULL THEN
                        RETURN false;
                    END IF;

                    EXECUTE format('ALTER TABLE %I DETACH PARTITION %I', parent_table, partition_name);
                    EXECUTE format('DROP TABLE %I', partition_name);

                    RETURN true;
                END;
                $function$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS internalchat_drop_month_partition(text, date);");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS internalchat_ensure_month_partitions(text, date, integer);");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS internalchat_create_month_partition(text, date);");
        }
    }
}
