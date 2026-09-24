using System.Globalization;
using InternalChat.Application.Abstractions;
using InternalChat.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace InternalChat.Infrastructure.Search;

/// <summary>
/// PostgreSQL full-text search over message bodies and attachment names (T164, research.md D9).
/// </summary>
/// <remarks>
/// <para>
/// <b>The membership filter is applied before ranking, and that is both halves of the design.</b>
/// It is the access control FR-029 demands — a message from a conversation the caller does not
/// belong to is never a candidate, rather than being ranked and then dropped — and it is what makes
/// PostgreSQL viable at 125 million rows, since a typical employee belongs to tens of conversations.
/// Filtering afterwards would be wrong on both counts at once.
/// </para>
/// <para>
/// <b>Raw SQL rather than LINQ.</b> <c>websearch_to_tsquery</c>, <c>ts_rank_cd</c>,
/// <c>ts_headline</c>, and a <c>tsvector</c> column that EF's model does not carry have no LINQ
/// translation. Written as parameterised SQL — every value below is a parameter, including the
/// query text, so a search for <c>'; DROP TABLE</c> is a search for that string.
/// </para>
/// <para>
/// <b><see cref="IndexMessageAsync"/> and <see cref="RemoveMessageAsync"/> do nothing here, and
/// that is the correct implementation rather than a stub.</b> <c>body_tsv</c> is a
/// <c>GENERATED ALWAYS ... STORED</c> column, so PostgreSQL recomputes it inside the same statement
/// that changes the body. An edit is searchable the instant it commits; a delete clears
/// <c>body</c>, which makes the vector empty and stops it matching. FR-032 is therefore satisfied
/// transactionally rather than eventually — there is no window in which the index disagrees with
/// the row. The methods stay on the interface because they are the seam that keeps the OpenSearch
/// option open (research.md D9), where they would do real work.
/// </para>
/// </remarks>
public sealed class PostgresSearchIndex : ISearchIndex
{
    /// <summary>
    /// Statement timeout for a search, in milliseconds.
    /// </summary>
    /// <remarks>
    /// Below SC-007's 1-second budget so the caller is told results were truncated rather than
    /// waiting past it. FR-033: "either return results within the stated time or state explicitly
    /// that results were truncated" — this is what makes the second half reachable.
    /// </remarks>
    private const int StatementTimeoutMilliseconds = 800;

    private readonly ChatDbContext _context;

    /// <summary>Creates the index.</summary>
    public PostgresSearchIndex(ChatDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
    }

    /// <inheritdoc />
    public async Task<SearchResults> SearchAsync(
        SearchQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (query.ConversationIds.Count == 0 || string.IsNullOrWhiteSpace(query.Text))
        {
            // No accessible conversations, or nothing to look for. Returned without touching the
            // database — and, importantly, in the same shape and roughly the same time as a search
            // that found nothing, which is what keeps FR-029's non-disclosure honest.
            return new SearchResults([], null, Truncated: false);
        }

        // The context's OWN connection, not a new one built from its connection string.
        // Database.GetConnectionString() redacts the password — Npgsql strips it when reporting the
        // string back — so a connection opened from it fails SCRAM authentication at runtime while
        // looking perfectly correct in code. Reusing the connection also avoids a second pool and
        // keeps this read on the same session as the rest of the request.
        NpgsqlConnection connection = (NpgsqlConnection)_context.Database.GetDbConnection();

        if (connection.State is not System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        // SET LOCAL needs a transaction, and the read needs no more than that. An ambient one is
        // reused rather than nested — a search invoked inside another use case must not commit or
        // roll back on its behalf.
        NpgsqlTransaction? owned = _context.Database.CurrentTransaction is null
            ? await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)
            : null;

        try
        {
            await using (NpgsqlCommand timeout = new(
                $"SET LOCAL statement_timeout = {StatementTimeoutMilliseconds}", connection))
            {
                timeout.Transaction = owned;
                await timeout.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            return await ExecuteAsync(connection, owned, query, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (PostgresException exception) when (exception.SqlState == "57014")
        {
            // query_canceled — the statement timeout fired. Reported as a truncated empty page
            // rather than as an error: FR-033 wants the caller told the answer is incomplete, and
            // a 500 would tell them nothing at all.
            return new SearchResults([], null, Truncated: true);
        }
        finally
        {
            if (owned is not null)
            {
                // Rolled back, never committed: this read wrote nothing, and rolling back is what
                // discards the SET LOCAL so the pooled connection does not carry an 800 ms
                // statement timeout into whatever borrows it next.
                await owned.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                await owned.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Intentionally empty. See the class remarks: the <c>tsvector</c> is a generated column, so
    /// indexing happens inside the write that changed the row.
    /// </remarks>
    public Task IndexMessageAsync(Guid messageId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <inheritdoc />
    /// <remarks>
    /// Intentionally empty, for the same reason. A delete clears <c>body</c>, and an empty
    /// <c>tsvector</c> matches nothing — verified against real PostgreSQL in the T163 migration
    /// check and asserted in <c>tests/Integration/Search/ReindexTests.cs</c>.
    /// </remarks>
    public Task RemoveMessageAsync(Guid messageId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    private static async Task<SearchResults> ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        SearchQuery query,
        CancellationToken cancellationToken)
    {
        // One extra row, discarded below, so "is there another page" is a fact rather than a guess.
        int limit = Math.Clamp(query.Limit, 1, 100);

        await using NpgsqlCommand command = new(Sql, connection) { Transaction = transaction };

        // websearch_to_tsquery, not to_tsquery: it accepts what a person actually types — quoted
        // phrases, OR, leading minus — and never throws on punctuation. to_tsquery raises a syntax
        // error on a bare apostrophe, which would turn "it's" into a 500.
        command.Parameters.AddWithValue("q", query.Text);
        command.Parameters.AddWithValue("conversations", query.ConversationIds.ToArray());
        command.Parameters.AddWithValue("author", (object?)query.AuthorId ?? DBNull.Value);
        command.Parameters.AddWithValue("from_at", (object?)query.From ?? DBNull.Value);
        command.Parameters.AddWithValue("to_at", (object?)query.To ?? DBNull.Value);
        command.Parameters.AddWithValue("after_rank", (object?)CursorRank(query.Cursor) ?? DBNull.Value);

        // 'any' becomes a null kind with the EXISTS still required, so it filters on "has one"
        // rather than on a particular kind. Anything unrecognised was already refused by the
        // validator; treated as no filter here rather than as an error, so this method has no
        // failure mode a caller could reach.
        command.Parameters.AddWithValue(
            "attachment_required",
            query.HasAttachment is "any" or "image" or "video");
        command.Parameters.AddWithValue(
            "attachment_kind",
            query.HasAttachment is "image" or "video" ? query.HasAttachment : (object)DBNull.Value);
        command.Parameters.AddWithValue("lim", limit + 1);

        List<SearchHit> hits = [];

        await using (NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                hits.Add(new SearchHit(
                    reader.GetGuid(0),
                    reader.GetGuid(1),
                    reader.GetInt64(2),
                    reader.GetString(3),
                    reader.GetFloat(4)));
            }
        }

        bool hasMore = hits.Count > limit;

        if (hasMore)
        {
            hits.RemoveAt(hits.Count - 1);
        }

        string? nextCursor = hasMore && hits.Count > 0
            ? hits[^1].Rank.ToString("R", CultureInfo.InvariantCulture)
            : null;

        return new SearchResults(hits, nextCursor, Truncated: false);
    }

    /// <summary>The cursor is the last page's lowest rank. Malformed input pages from the start.</summary>
    /// <remarks>
    /// Not an error: a cursor is an opaque token a client echoes back, and a garbled one should
    /// restart the search rather than fail it. Returning the first page is the benign reading.
    /// </remarks>
    private static float? CursorRank(string? cursor) =>
        float.TryParse(cursor, NumberStyles.Float, CultureInfo.InvariantCulture, out float rank)
            ? rank
            : null;

    /// <summary>
    /// The search query.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <c>conversation_id = ANY(@conversations)</c> predicate is first in the WHERE clause for
    /// readability only — the planner reorders freely — but it is the one that must exist, because
    /// it is the access control. Every other predicate is a user convenience.
    /// </para>
    /// <para>
    /// <c>deleted_at IS NULL</c> is belt and braces: a tombstone has a NULL body and therefore an
    /// empty vector, so it cannot match anyway. Stated explicitly so the intent survives someone
    /// later deciding tombstones should keep their text.
    /// </para>
    /// <para>
    /// Attachment names are matched through an EXISTS rather than a JOIN, so a message with three
    /// matching attachments is one result rather than three.
    /// </para>
    /// </remarks>
    private const string Sql =
        """
        WITH q AS (
            SELECT websearch_to_tsquery('simple', internalchat_immutable_unaccent(@q)) AS tsq
        )
        SELECT
            m.id,
            m.conversation_id,
            m.seq,
            ts_headline(
                'simple',
                coalesce(m.body, ''),
                q.tsq,
                'StartSel=<mark>,StopSel=</mark>,MaxFragments=2,MaxWords=18,MinWords=5') AS highlight,
            ts_rank_cd(m.body_tsv, q.tsq)::real AS rank
        FROM message m
        CROSS JOIN q
        WHERE m.conversation_id = ANY(@conversations)
          AND m.deleted_at IS NULL
          AND (
                m.body_tsv @@ q.tsq
             OR EXISTS (
                    SELECT 1 FROM attachment a
                    WHERE a.message_id = m.id
                      AND to_tsvector('simple', internalchat_immutable_unaccent(a.file_name)) @@ q.tsq)
              )
          AND (@author::uuid IS NULL OR m.author_id = @author::uuid)
          AND (@from_at::timestamptz IS NULL OR m.sent_at >= @from_at::timestamptz)
          AND (@to_at::timestamptz IS NULL OR m.sent_at <= @to_at::timestamptz)
          AND (@after_rank::real IS NULL OR ts_rank_cd(m.body_tsv, q.tsq)::real < @after_rank::real)
          AND (
                NOT @attachment_required
             OR EXISTS (
                    SELECT 1 FROM attachment a2
                    WHERE a2.message_id = m.id
                      -- Only what the searcher could actually open. A pending or infected upload
                      -- would otherwise produce a result that leads to a refusal.
                      AND a2.scan_status = 'clean'
                      AND (@attachment_kind::text IS NULL
                           OR a2.kind::text = @attachment_kind::text))
              )
        ORDER BY rank DESC, m.sent_at DESC, m.id
        LIMIT @lim
        """;
}
