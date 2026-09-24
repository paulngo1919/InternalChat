using InternalChat.Domain.Employees;
using InternalChat.Infrastructure.Persistence.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InternalChat.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="PushSubscriptionRecord"/> (data-model.md, <c>push_subscription</c>).
/// </summary>
internal sealed class PushSubscriptionConfiguration : IEntityTypeConfiguration<PushSubscriptionRecord>
{
    public void Configure(EntityTypeBuilder<PushSubscriptionRecord> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("push_subscription");

        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();

        // A browser vendor endpoint URL. Longer than the schema-wide 512 default (RedisKeyspace-style
        // FCM/Mozilla endpoints commonly carry long opaque tokens in the path).
        builder.Property(s => s.Endpoint).IsRequired().HasMaxLength(2048);
        builder.HasIndex(s => s.Endpoint).IsUnique();

        builder.Property(s => s.P256dh).IsRequired().HasMaxLength(255);
        builder.Property(s => s.Auth).IsRequired().HasMaxLength(255);
        builder.Property(s => s.UserAgent).HasMaxLength(512);
        builder.Property(s => s.CreatedAt).IsRequired();
        builder.Property(s => s.LastSuccessAt);

        builder.HasOne<Employee>()
            .WithMany()
            .HasForeignKey(s => s.EmployeeId)
            .OnDelete(DeleteBehavior.Restrict);

        // The notification fan-out's read path: every live subscription for one employee.
        builder.HasIndex(s => s.EmployeeId).HasDatabaseName("ix_push_subscription_employee");
    }
}
