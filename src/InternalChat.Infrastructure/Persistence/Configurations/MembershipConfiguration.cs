using InternalChat.Domain.Conversations;
using InternalChat.Domain.Employees;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InternalChat.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="Membership"/> — the authorization record (data-model.md, <c>membership</c>).
/// </summary>
/// <remarks>
/// The indexes here are not tuning. Every access decision in the platform reads this table, so its
/// lookup path is on the critical path of every request that touches a conversation.
/// </remarks>
internal sealed class MembershipConfiguration : IEntityTypeConfiguration<Membership>
{
    public void Configure(EntityTypeBuilder<Membership> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("membership");

        builder.HasKey(m => new { m.ConversationId, m.EmployeeId });

        builder.Property(m => m.Role).IsRequired();
        builder.Property(m => m.JoinedAt).IsRequired();
        builder.Property(m => m.VisibleFromSeq).IsRequired();
        builder.Property(m => m.MutedUntil);
        builder.Property(m => m.RemovedAt);

        builder.HasOne<Employee>()
            .WithMany()
            .HasForeignKey(m => m.EmployeeId)
            .OnDelete(DeleteBehavior.Restrict);

        // No foreign key to `conversation` yet — that table arrives with T088 (US2). Adding it
        // here would make this migration unapplyable. T088 adds the constraint in its own
        // migration, which is forward-only and therefore fine (Principle VII).

        // Conversation-list queries: "every conversation this employee is still in".
        builder.HasIndex(m => new { m.EmployeeId, m.RemovedAt })
            .HasDatabaseName("ix_membership_employee_removed");

        // The authorization lookup. Partial, so the index holds only rows that can grant
        // anything — removed rows are dead weight in an index read on every single request, and
        // over a year of membership churn they would come to dominate it.
        builder.HasIndex(m => new { m.ConversationId, m.EmployeeId })
            .HasDatabaseName("ix_membership_active_lookup")
            .HasFilter("removed_at IS NULL");
    }
}
