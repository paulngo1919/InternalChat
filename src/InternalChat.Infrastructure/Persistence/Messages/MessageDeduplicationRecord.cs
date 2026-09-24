namespace InternalChat.Infrastructure.Persistence.Messages;

/// <summary>
/// One <c>(conversation, client message key)</c> that has already been accepted.
/// </summary>
/// <remarks>
/// <para>
/// <b>This table is the exactly-once guarantee (FR-011), and it exists because the obvious place to
/// put that guarantee does not work.</b> data-model.md specifies a UNIQUE index on
/// <c>message (conversation_id, client_message_key)</c> and calls it "the entire exactly-once
/// guarantee". PostgreSQL refuses to create it:
/// </para>
/// <code>
/// ERROR: unique constraint on partitioned table must include all partitioning columns
/// DETAIL: UNIQUE constraint on table "message" lacks column "sent_at" which is part of the
///         partition key.
/// </code>
/// <para>
/// And widening it to <c>(conversation_id, client_message_key, sent_at)</c> would be worse than
/// useless: <c>sent_at</c> is assigned by the server at insert, so a retried send gets a *different*
/// timestamp, the composite differs, and the duplicate is admitted — an index that looks like a
/// guarantee and enforces nothing. The two requirements are in genuine tension: research.md D11
/// needs monthly RANGE partitioning so retention is a <c>DROP PARTITION</c>, and research.md D1
/// needs a uniqueness constraint that spans every partition.
/// </para>
/// <para>
/// So the constraint lives on its own un-partitioned table. The primary key
/// <c>(conversation_id, client_message_key)</c> is what a concurrent retry collides on, exactly as
/// D1 intends; the row also records which message won, so the handler can return it rather than
/// failing. A <c>SELECT</c>-then-<c>INSERT</c> would not do: two clicks racing would both see
/// nothing and both insert.
/// </para>
/// <para>
/// <see cref="SentAt"/> is carried so the retention sweep can delete these rows by the same month
/// range it drops a partition for. There is deliberately no foreign key to <c>message</c> — one
/// would have to include the partition key, and it would then block the partition drop that
/// retention depends on.
/// </para>
/// </remarks>
public sealed class MessageDeduplicationRecord
{
    /// <summary>The conversation the key is unique within.</summary>
    public Guid ConversationId { get; set; }

    /// <summary>The sender's idempotency key.</summary>
    public string ClientMessageKey { get; set; } = string.Empty;

    /// <summary>The message this key produced. Returned to a caller whose retry collided.</summary>
    public Guid MessageId { get; set; }

    /// <summary>
    /// The winning message's <c>sent_at</c>.
    /// </summary>
    /// <remarks>
    /// Needed twice over: the retention sweep deletes by it, and reading the message back requires
    /// it because the partitioned table's primary key is <c>(id, sent_at)</c> — without it, fetching
    /// the existing message would scan every partition.
    /// </remarks>
    public DateTimeOffset SentAt { get; set; }
}
