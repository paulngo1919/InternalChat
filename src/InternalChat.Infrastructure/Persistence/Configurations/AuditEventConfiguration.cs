using InternalChat.Infrastructure.Persistence.Audit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InternalChat.Infrastructure.Persistence.Configurations;

/// <summary>EF mapping for <see cref="AuditEventRecord"/>.</summary>
internal sealed class AuditEventConfiguration : IEntityTypeConfiguration<AuditEventRecord>
{
    public void Configure(EntityTypeBuilder<AuditEventRecord> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("audit_event", t => t.HasCheckConstraint(
            "ck_audit_event_outcome",
            "outcome IN ('success', 'denied', 'error')"));

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).UseIdentityByDefaultColumn();

        builder.Property(x => x.OccurredAt).IsRequired();
        builder.Property(x => x.Action).HasMaxLength(200).IsRequired();
        builder.Property(x => x.SubjectType).HasMaxLength(100).IsRequired();
        builder.Property(x => x.Outcome).HasMaxLength(20).IsRequired();

        // inet rather than text: it lets an investigator filter by subnet, which is the
        // difference between "was this one workstation" and reading ten thousand rows by eye.
        builder.Property(x => x.SourceIp).HasColumnType("inet");

        builder.Property(x => x.Detail).HasColumnType("jsonb").IsRequired();

        // Same trap as the outbox payload: the context-wide 512-character string convention
        // would otherwise declare this jsonb column as length-capped.
        builder.Property(x => x.Detail).Metadata.SetMaxLength(null);

        // The index behind SC-021 — "who had access to this conversation on this date, and who
        // changed it, in under 10 minutes". Without it that question is a sequential scan of a
        // year of audit history.
        builder.HasIndex(x => new { x.SubjectType, x.SubjectId, x.OccurredAt })
            .HasDatabaseName("ix_audit_event_subject")
            .IsDescending(false, false, true);

        builder.HasIndex(x => new { x.ActorId, x.OccurredAt })
            .HasDatabaseName("ix_audit_event_actor")
            .IsDescending(false, true);
    }
}
