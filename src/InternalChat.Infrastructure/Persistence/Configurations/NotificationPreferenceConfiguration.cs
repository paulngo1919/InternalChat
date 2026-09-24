using InternalChat.Domain.Employees;
using InternalChat.Domain.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InternalChat.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="NotificationPreference"/> (data-model.md, <c>notification_preference</c>).
/// </summary>
internal sealed class NotificationPreferenceConfiguration : IEntityTypeConfiguration<NotificationPreference>
{
    public void Configure(EntityTypeBuilder<NotificationPreference> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("notification_preference");

        builder.HasKey(p => p.EmployeeId);
        builder.Property(p => p.EmployeeId).ValueGeneratedNever();

        builder.Property(p => p.DndStart);
        builder.Property(p => p.DndEnd);
        builder.Property(p => p.TimeZoneId).IsRequired().HasMaxLength(64);
        builder.Property(p => p.DigestAfterMinutes).IsRequired();

        // Ignored: DoNotDisturb is computed from the raw columns above, not a column of its own
        // (see NotificationPreference's remarks on why it is not mapped as an owned type).
        builder.Ignore(p => p.DoNotDisturb);

        builder.HasOne<Employee>()
            .WithMany()
            .HasForeignKey(p => p.EmployeeId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
