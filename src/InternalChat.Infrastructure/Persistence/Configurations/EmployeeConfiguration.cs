using InternalChat.Domain.Employees;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InternalChat.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="Employee"/> — the directory projection (data-model.md, <c>employee</c>).
/// </summary>
internal sealed class EmployeeConfiguration : IEntityTypeConfiguration<Employee>
{
    public void Configure(EntityTypeBuilder<Employee> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("employee");

        // Raised events are drained into outbox_message inside the same transaction (Principle VI)
        // and then cleared. Left mapped, EF's conventions read this navigation-shaped property as
        // a relationship and try to build a table of domain events.
        builder.Ignore(e => e.DomainEvents);

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();

        // The join between a Keycloak token and a row here. Unique because two rows claiming the
        // same subject would make "which employee is this token" ambiguous on every request.
        builder.Property(e => e.ExternalSubject).IsRequired().HasMaxLength(255);
        builder.HasIndex(e => e.ExternalSubject).IsUnique();

        builder.Property(e => e.DisplayName).IsRequired().HasMaxLength(200);

        // citext, so "An.Nguyen@..." and "an.nguyen@..." are one address rather than two accounts.
        // Case-folding in application code instead would work until one query forgot.
        builder.Property(e => e.Email).IsRequired().HasColumnType("citext");
        builder.HasIndex(e => e.Email).IsUnique();

        builder.Property(e => e.AvatarUrl).HasMaxLength(1024);

        builder.Property(e => e.Status).IsRequired();
        builder.Property(e => e.DeactivatedAt);
        builder.Property(e => e.CreatedAt).IsRequired();
        builder.Property(e => e.UpdatedAt).IsRequired();

        // Trigram index for directory search (FR-007). A plain B-tree cannot serve
        // "display_name ILIKE '%nguyen%'" — the leading wildcard makes it a sequential scan over
        // 10,000 rows on every keystroke of the people picker.
        builder.HasIndex(e => e.DisplayName)
            .HasDatabaseName("ix_employee_display_name_trgm")
            .HasMethod("gin")
            .HasOperators("gin_trgm_ops");
    }
}
