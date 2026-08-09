using InternalChat.Infrastructure.Persistence.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InternalChat.Infrastructure.Persistence.Configurations;

/// <summary>EF mapping for <see cref="OutboxMessage"/>.</summary>
internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("outbox_message");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();

        builder.Property(x => x.Type).HasMaxLength(200).IsRequired();
        builder.Property(x => x.RoutingKey).HasMaxLength(200).IsRequired();

        // jsonb rather than text: the payload is queried by the operator during incident
        // triage, and jsonb makes that possible without parsing in the application.
        //
        // SetMaxLength(null) is load-bearing. ChatDbContext.ConfigureConventions applies a
        // default 512-character cap to every string so a forgotten HasMaxLength cannot create an
        // unbounded column — but that default silently applied here too, producing a jsonb
        // payload declared as max 512. PostgreSQL ignores the length on jsonb, so nothing would
        // have failed; the model would simply have carried a false constraint until someone
        // changed the column type and lost data.
        builder.Property(x => x.Payload).HasColumnType("jsonb").IsRequired();
        builder.Property(x => x.Payload).Metadata.SetMaxLength(null);

        builder.Property(x => x.TraceParent).HasMaxLength(128);
        builder.Property(x => x.OccurredAt).IsRequired();
        builder.Property(x => x.Attempts).HasDefaultValue(0);
        builder.Property(x => x.LastError).HasMaxLength(2000);

        // The dispatcher's only query, and it runs continuously — a partial index keeps it
        // proportional to the undispatched backlog rather than to the whole table, which at
        // 100 messages/second would otherwise grow past 8 million rows a day.
        builder.HasIndex(x => x.OccurredAt)
            .HasDatabaseName("ix_outbox_message_pending")
            .HasFilter("dispatched_at IS NULL");
    }
}

/// <summary>EF mapping for <see cref="ProcessedMessage"/>.</summary>
internal sealed class ProcessedMessageConfiguration : IEntityTypeConfiguration<ProcessedMessage>
{
    public void Configure(EntityTypeBuilder<ProcessedMessage> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("processed_message");

        // Composite key IS the deduplication mechanism. A redelivered message hits the primary
        // key and the insert fails, which is what the consumer treats as "already handled".
        builder.HasKey(x => new { x.ConsumerName, x.MessageId });

        builder.Property(x => x.ConsumerName).HasMaxLength(200).IsRequired();
        builder.Property(x => x.ProcessedAt).IsRequired();

        // Supports the 30-day pruning sweep without scanning the whole table.
        builder.HasIndex(x => x.ProcessedAt).HasDatabaseName("ix_processed_message_processed_at");
    }
}
