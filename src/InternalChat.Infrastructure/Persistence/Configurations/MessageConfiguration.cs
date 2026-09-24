using InternalChat.Domain.Conversations;
using InternalChat.Domain.Employees;
using InternalChat.Domain.Messages;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InternalChat.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="Message"/> (data-model.md, <c>message</c>).
/// </summary>
/// <remarks>
/// <para>
/// The table itself is created by hand-written SQL in the migration, because EF Core cannot express
/// <c>PARTITION BY RANGE</c>. This configuration still has to describe the same shape: it is what
/// queries are built from, and a mismatch between the two surfaces as a column-not-found error at
/// runtime rather than at build time.
/// </para>
/// <para>
/// <b>The primary key is composite</b> — <c>(id, sent_at)</c> — because PostgreSQL requires the
/// partition key to be part of every unique constraint on a partitioned table. That single rule is
/// also why the exactly-once index lives on its own table; see
/// <see cref="Messages.MessageDeduplicationRecord"/>.
/// </para>
/// </remarks>
internal sealed class MessageConfiguration : IEntityTypeConfiguration<Message>
{
    public void Configure(EntityTypeBuilder<Message> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable(
            "message",
            table =>
            {
                // A message is either readable or a tombstone, never both and never neither.
                // data-model.md declares body NOT NULL and also says deleting clears it; the two
                // cannot both hold, so the invariant is stated as the relationship it actually is.
                table.HasCheckConstraint(
                    "ck_message_body_or_tombstone",
                    "(deleted_at IS NULL AND body IS NOT NULL) OR (deleted_at IS NOT NULL AND body IS NULL)");

                table.HasCheckConstraint(
                    "ck_message_body_length",
                    "body IS NULL OR (length(body) BETWEEN 1 AND 8000)");

                // Sequences are allocated from 1 upward by UPDATE ... RETURNING. A zero or negative
                // value means the allocator was bypassed, which breaks keyset pagination silently.
                table.HasCheckConstraint("ck_message_seq_positive", "seq >= 1");
            });

        builder.Ignore(m => m.DomainEvents);

        // Composite, and sent_at must come second so the index also serves point lookups by id.
        builder.HasKey(m => new { m.Id, m.SentAt });
        builder.Property(m => m.Id).ValueGeneratedNever();

        builder.Property(m => m.ConversationId).IsRequired();
        builder.Property(m => m.Seq).IsRequired();
        builder.Property(m => m.AuthorId).IsRequired();

        // Stored as text rather than as an owned type: it is a single scalar, and a value converter
        // keeps the domain type at the boundary without an extra table or a shadow column.
        builder.Property(m => m.ClientMessageKey)
            .IsRequired()
            .HasMaxLength(ClientMessageKey.Length)
            .HasConversion(key => key.Value, value => ClientMessageKey.Parse(value));

        // Nullable, because a tombstone has no body. The CHECK above ties that to deleted_at so
        // the two can never disagree.
        builder.Property(m => m.Body)
            .HasMaxLength(MessageBody.MaximumLength)
            .HasConversion(
                body => body!.Value,
                value => MessageBody.Create(value));

        builder.Property(m => m.SentAt).IsRequired();
        builder.Property(m => m.EditedAt);
        builder.Property(m => m.DeletedAt);

        // A PostgreSQL array rather than a join table. Mentions are read with the message every
        // time and written once at send; a join table would add a round trip to the history query,
        // which has a 250 ms budget for 50 messages.
        builder.Property<IReadOnlyList<Guid>>("Mentions")
            .HasField("_mentions")
            .UsePropertyAccessMode(PropertyAccessMode.Field)
            .HasColumnName("mentions")
            .HasColumnType("uuid[]")
            .IsRequired()
            .HasDefaultValueSql("'{}'::uuid[]");

        builder.HasOne<Conversation>()
            .WithMany()
            .HasForeignKey(m => m.ConversationId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Employee>()
            .WithMany()
            .HasForeignKey(m => m.AuthorId)
            .OnDelete(DeleteBehavior.Restrict);

        // History paging, and the only index the read path needs. NOT unique: PostgreSQL would
        // require sent_at in it, and uniqueness is already guaranteed upstream by the row-locked
        // sequence allocator. Descending because history is read newest-first (FR-013).
        builder.HasIndex(m => new { m.ConversationId, m.Seq })
            .HasDatabaseName("ix_message_conversation_seq")
            .IsDescending(false, true);
    }
}

/// <summary>
/// Maps the deduplication table that carries FR-011.
/// </summary>
internal sealed class MessageDeduplicationConfiguration
    : IEntityTypeConfiguration<Messages.MessageDeduplicationRecord>
{
    public void Configure(EntityTypeBuilder<Messages.MessageDeduplicationRecord> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("message_dedup");

        // THE exactly-once guarantee. A concurrent retry collides here, and the handler returns the
        // message named by the winning row instead of inserting a second one.
        builder.HasKey(d => new { d.ConversationId, d.ClientMessageKey });

        builder.Property(d => d.ClientMessageKey).IsRequired().HasMaxLength(ClientMessageKey.Length);
        builder.Property(d => d.MessageId).IsRequired();
        builder.Property(d => d.SentAt).IsRequired();

        // The retention sweep deletes these alongside the partition it drops (FR-052). Without an
        // index on sent_at that sweep is a sequential scan of a table with 125 million rows.
        builder.HasIndex(d => d.SentAt).HasDatabaseName("ix_message_dedup_sent_at");
    }
}
