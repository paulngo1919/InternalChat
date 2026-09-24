namespace InternalChat.Infrastructure.Persistence.Migrations;

/// <summary>
/// The hand-written SQL behind full-text search (T163, research.md D9).
/// </summary>
/// <remarks>
/// Kept beside the migration that applies it rather than inline, because the immutability problem
/// it solves needs more explanation than a migration body should carry.
/// </remarks>
internal static class SearchIndexSql
{
    /// <summary>
    /// An <c>IMMUTABLE</c> wrapper over <c>unaccent</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This wrapper is not a workaround for a PostgreSQL quirk — it is an explicit acceptance of
    /// the risk PostgreSQL is warning about.</b> <c>unaccent()</c> is declared <c>STABLE</c>, not
    /// <c>IMMUTABLE</c>, because its result depends on a dictionary file that an administrator can
    /// change. A generated column and a functional index both require immutability, so PostgreSQL
    /// refuses to build either on <c>unaccent()</c> directly.
    /// </para>
    /// <para>
    /// Declaring the wrapper <c>IMMUTABLE</c> tells the planner the value can be cached and stored.
    /// The cost of that promise is real and worth stating: <b>if the unaccent dictionary is ever
    /// changed, every stored <c>body_tsv</c> becomes subtly wrong and the index silently disagrees
    /// with the text</b> — rows would have to be rewritten and the index rebuilt. We accept it
    /// because the dictionary is the stock one, it is pinned by the PostgreSQL image tag, and
    /// nothing in this deployment edits it.
    /// </para>
    /// <para>
    /// The alternative — a trigger maintaining an ordinary column, which is what research.md D9
    /// describes — avoids the promise but costs a row-level trigger on the hottest insert path in
    /// the system, at 100 messages/second sustained. A generated column is computed in the same
    /// tuple construction as the insert. That is why this went the other way.
    /// </para>
    /// </remarks>
    public const string CreateImmutableUnaccent =
        """
        CREATE OR REPLACE FUNCTION internalchat_immutable_unaccent(text)
        RETURNS text
        LANGUAGE sql
        IMMUTABLE
        PARALLEL SAFE
        STRICT
        AS $$ SELECT public.unaccent('public.unaccent'::regdictionary, $1) $$;
        """;

    /// <summary>
    /// The generated <c>tsvector</c> column and its GIN index.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><c>simple</c>, not <c>english</c>.</b> research.md D9: the deployment is a Vietnamese
    /// organization, and an English stemmer would mangle Vietnamese while helping nothing. The
    /// cost is no stemming at all — searching "meeting" does not match "meetings" — and that is the
    /// known, documented limitation of the choice rather than an oversight.
    /// </para>
    /// <para>
    /// <b>Added to the partitioned parent, so every partition inherits it</b>, including ones
    /// <c>internalchat_ensure_month_partitions</c> creates later. Adding it per-partition would
    /// leave next month's messages unsearchable until someone noticed.
    /// </para>
    /// <para>
    /// <c>CREATE INDEX</c> on a partitioned parent is not concurrent and takes an
    /// <c>ACCESS EXCLUSIVE</c> lock per partition while it builds. At the scale this migration runs
    /// (an empty or small table) that is instantaneous; on a populated 125-million-row table it
    /// would need the per-partition <c>CONCURRENTLY</c> dance, which is a deployment runbook
    /// concern rather than something a migration should attempt.
    /// </para>
    /// </remarks>
    public const string AddSearchColumn =
        """
        ALTER TABLE message
            ADD COLUMN body_tsv tsvector
            GENERATED ALWAYS AS (
                to_tsvector('simple', internalchat_immutable_unaccent(coalesce(body, '')))
            ) STORED;

        CREATE INDEX ix_message_body_tsv ON message USING gin (body_tsv);
        """;

    /// <summary>
    /// The attachment file-name index.
    /// </summary>
    /// <remarks>
    /// FR-029 says "message text <b>and attachment names</b>". The names live on <c>attachment</c>,
    /// not on <c>message</c>, so they need their own index — folding them into the message's vector
    /// would mean rewriting a message row whenever an attachment was added to it.
    /// </remarks>
    public const string AddAttachmentNameIndex =
        """
        CREATE INDEX ix_attachment_file_name_tsv ON attachment
            USING gin (to_tsvector('simple', internalchat_immutable_unaccent(file_name)));
        """;

    /// <summary>Reverses everything above, innermost first.</summary>
    public const string Drop =
        """
        DROP INDEX IF EXISTS ix_attachment_file_name_tsv;
        DROP INDEX IF EXISTS ix_message_body_tsv;
        ALTER TABLE message DROP COLUMN IF EXISTS body_tsv;
        DROP FUNCTION IF EXISTS internalchat_immutable_unaccent(text);
        """;
}
