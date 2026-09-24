using InternalChat.Domain.Conversations;
using InternalChat.Domain.Employees;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InternalChat.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="Conversation"/> (data-model.md, <c>conversation</c>).
/// </summary>
internal sealed class ConversationConfiguration : IEntityTypeConfiguration<Conversation>
{
    public void Configure(EntityTypeBuilder<Conversation> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable(
            "conversation",
            table =>
            {
                // The database's own statement of what a group is. The domain refuses a nameless
                // group too (Conversation.CreateGroup), but a CHECK constraint also covers the
                // seeder, a migration, and anything that writes through raw SQL later.
                table.HasCheckConstraint(
                    "ck_conversation_group_has_name",
                    "kind <> 'group' OR (name IS NOT NULL AND length(btrim(name)) > 0)");

                // A direct conversation is named by who is in it, and a group has no direct key.
                // Stating both directions stops a row that is half one kind and half the other.
                table.HasCheckConstraint(
                    "ck_conversation_direct_shape",
                    "(kind = 'direct' AND name IS NULL AND direct_key IS NOT NULL) "
                    + "OR (kind = 'group' AND direct_key IS NULL)");
            });

        builder.Ignore(c => c.DomainEvents);

        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();

        builder.Property(c => c.Kind).IsRequired();
        builder.Property(c => c.Name).HasMaxLength(Conversation.MaximumNameLength);
        builder.Property(c => c.CreatedBy).IsRequired();
        builder.Property(c => c.HistoryVisibility).IsRequired();

        // Allocated by UPDATE ... RETURNING inside the send transaction (research.md D1). The
        // default matters: a NULL here would make the first allocation on any conversation fail.
        builder.Property(c => c.LastSeq).IsRequired().HasDefaultValue(0L);

        // Two employee ids joined by a colon, so 73 characters. Bounded rather than left to the
        // 512-character default, because the unique index below is read on every direct-conversation
        // creation.
        builder.Property(c => c.DirectKey).HasMaxLength(100);

        builder.Property(c => c.CreatedAt).IsRequired();
        builder.Property(c => c.UpdatedAt).IsRequired();

        builder.HasOne<Employee>()
            .WithMany()
            .HasForeignKey(c => c.CreatedBy)
            .OnDelete(DeleteBehavior.Restrict);

        // THE deduplication mechanism for direct conversations (data-model.md). Partial, because
        // every group has a NULL key and PostgreSQL would otherwise index all of them for nothing.
        // This index is what makes two people clicking "message" on each other at the same instant
        // produce one conversation rather than two halves of an exchange.
        builder.HasIndex(c => c.DirectKey)
            .HasDatabaseName("ux_conversation_direct_key")
            .IsUnique()
            .HasFilter("kind = 'direct'");
    }
}
