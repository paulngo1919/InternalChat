using InternalChat.Domain.Conversations;
using InternalChat.Domain.Employees;
using InternalChat.Domain.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InternalChat.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="ReadState"/> (data-model.md, <c>read_state</c>).
/// </summary>
internal sealed class ReadStateConfiguration : IEntityTypeConfiguration<ReadState>
{
    public void Configure(EntityTypeBuilder<ReadState> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("read_state");

        builder.HasKey(r => new { r.EmployeeId, r.ConversationId });

        builder.Property(r => r.LastReadSeq).IsRequired();
        builder.Property(r => r.UpdatedAt).IsRequired();

        builder.HasOne<Employee>()
            .WithMany()
            .HasForeignKey(r => r.EmployeeId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Conversation>()
            .WithMany()
            .HasForeignKey(r => r.ConversationId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
