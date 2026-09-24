using InternalChat.Domain.Attachments;
using InternalChat.Domain.Conversations;
using InternalChat.Domain.Employees;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InternalChat.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="Attachment"/> (data-model.md, <c>attachment</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Three deliberate departures from the table as data-model.md draws it.</b>
/// </para>
/// <list type="number">
/// <item>
/// <b>There is no foreign key on <c>message_id</c>, though the document lists one.</b> It is not
/// merely awkward to create, it is incompatible with how history is retained. <c>message</c> is
/// partitioned by month and FR-052 expires history by dropping whole partitions; an inbound
/// foreign key to a partitioned table makes PostgreSQL refuse that drop, turning a metadata
/// operation into a dependency error every month. The referential rule is therefore enforced where
/// it is created — <c>AttachTo</c> is write-once — and the retention sweep clears attachments in
/// the same pass that drops the partition.
/// </item>
/// <item>
/// <b><c>message_sent_at</c> exists.</b> Independent of the foreign key: <c>message</c>'s primary
/// key is <c>(id, sent_at)</c> because the partition key must be part of it, so an id on its own
/// does not locate a row. The download path reads the message on every request to honour FR-027
/// ("stop serving when its message is deleted"), and that is the hot path for every image a
/// conversation renders — without the partition key it is a scan of twelve months.
/// </item>
/// <item>
/// <b><c>file_name</c> exists.</b> The document omits it; <c>openapi.yaml</c> requires it on
/// <c>RequestUploadRequest</c> and returns it on <c>Attachment</c>. The contract wins, because a
/// client cannot satisfy a required request field the database has nowhere to put.
/// </item>
/// </list>
/// <para>
/// The size and duration CHECK constraints restate limits that <c>FileConstraints</c> already
/// enforces, for the same reason <c>message</c> has one: the domain type gives a usable error, and
/// the constraint is what holds if the domain type is ever bypassed — by the seeder, by a migration,
/// by a future bulk import.
/// </para>
/// </remarks>
internal sealed class AttachmentConfiguration : IEntityTypeConfiguration<Attachment>
{
    public void Configure(EntityTypeBuilder<Attachment> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable(
            "attachment",
            table =>
            {
                // Per-kind ceilings, expressed as one constraint rather than two so there is no
                // gap for a kind that is neither.
                table.HasCheckConstraint(
                    "ck_attachment_byte_size",
                    """
                    byte_size > 0 AND (
                        (kind = 'image' AND byte_size <= 26214400)
                     OR (kind = 'video' AND byte_size <= 524288000))
                    """);

                // Videos declare a duration within the limit; images declare none. Stated as a
                // biconditional so neither a duration-less video nor a timed image can be stored.
                table.HasCheckConstraint(
                    "ck_attachment_duration",
                    """
                    (kind = 'video' AND duration_seconds IS NOT NULL
                         AND duration_seconds BETWEEN 1 AND 600)
                    OR (kind <> 'video' AND duration_seconds IS NULL)
                    """);

                // The id and the partition key travel together or not at all. A half-set pair
                // would be a row whose message can never be located.
                table.HasCheckConstraint(
                    "ck_attachment_message_reference",
                    "(message_id IS NULL) = (message_sent_at IS NULL)");

                // pending is the only state with no verdict time, and the three settled states
                // always have one. Keeps a partially applied verdict from looking settled.
                table.HasCheckConstraint(
                    "ck_attachment_scanned_at",
                    "(scan_status = 'pending') = (scanned_at IS NULL)");

                // A poster frame belongs to a video (research.md D8) and to nothing else.
                table.HasCheckConstraint(
                    "ck_attachment_poster_is_video",
                    "poster_object_key IS NULL OR kind = 'video'");
            });

        builder.Ignore(a => a.DomainEvents);

        // Derived from ConversationId and Id; there is no column to drift from them.
        builder.Ignore(a => a.ObjectKey);

        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).ValueGeneratedNever();

        builder.Property(a => a.ConversationId).IsRequired();
        builder.Property(a => a.UploadedBy).IsRequired();
        builder.Property(a => a.MessageId);
        builder.Property(a => a.MessageSentAt);

        builder.Property(a => a.Kind).IsRequired();
        builder.Property(a => a.ScanStatus).IsRequired();

        // Bounded because every text column is (ChatDbContext conventions). 255 is generous for a
        // media type; the allow-list makes the real limit far shorter.
        builder.Property(a => a.ContentType).IsRequired().HasMaxLength(255);
        builder.Property(a => a.FileName).IsRequired().HasMaxLength(Attachment.MaximumFileNameLength);

        builder.Property(a => a.ByteSize).IsRequired();
        builder.Property(a => a.DurationSeconds);

        // "{conversationId}/{attachmentId}" — two UUIDs and a separator, with room to spare for a
        // poster key's suffix.
        builder.Property(a => a.PosterObjectKey).HasMaxLength(512);

        builder.Property(a => a.CreatedAt).IsRequired();
        builder.Property(a => a.ScannedAt);

        builder.HasOne<Conversation>()
            .WithMany()
            .HasForeignKey(a => a.ConversationId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Employee>()
            .WithMany()
            .HasForeignKey(a => a.UploadedBy)
            .OnDelete(DeleteBehavior.Restrict);

        // Listing a conversation's attachments, and the scope the retention sweep deletes by.
        builder.HasIndex(a => a.ConversationId).HasDatabaseName("ix_attachment_conversation");

        // Loading the attachments of a page of messages. Partial: an unattached row is an
        // abandoned upload, never something this index is used to find.
        builder.HasIndex(a => a.MessageId)
            .HasDatabaseName("ix_attachment_message")
            .HasFilter("message_id IS NOT NULL");

        // The scan queue. Partial, because it is only ever read for work still to do — and once
        // the table holds a year of clean rows, a full index on scan_status would be almost
        // entirely dead weight kept hot for nothing.
        builder.HasIndex(a => a.ScanStatus)
            .HasDatabaseName("ix_attachment_pending_scan")
            .HasFilter("scan_status = 'pending'");
    }
}
