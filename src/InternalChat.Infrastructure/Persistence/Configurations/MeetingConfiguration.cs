using InternalChat.Domain.Conversations;
using InternalChat.Domain.Employees;
using InternalChat.Domain.Meetings;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InternalChat.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="Meeting"/> and its participations (data-model.md, <c>meeting</c> /
/// <c>participation</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Participations are an owned collection, not a separate aggregate.</b> A participation has no
/// life outside its meeting and is never queried without one — FR-051's audit question is always
/// "who was in this meeting", never "which meetings was this person in" on its own. Mapping it as
/// owned means it cannot be loaded or written independently, which is the same guarantee the domain
/// makes by keeping <c>Participation</c>'s constructor internal.
/// </para>
/// <para>
/// <b>The row survives the meeting for a year</b> (FR-051, and the audit retention question in
/// spec.md). Nothing here cascades on delete: an employee who leaves the company keeps their
/// participation rows, because an audit record with the participants removed is not an audit record.
/// </para>
/// </remarks>
internal sealed class MeetingConfiguration : IEntityTypeConfiguration<Meeting>
{
    public void Configure(EntityTypeBuilder<Meeting> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable(
            "meeting",
            table =>
            {
                // A meeting cannot end before it started. Cheap, and it is the constraint that
                // catches a clock going backwards across a restart before the bad duration reaches
                // an audit report.
                table.HasCheckConstraint(
                    "ck_meeting_ends_after_start",
                    "ended_at IS NULL OR ended_at >= started_at");

                // The peak cannot exceed the cap the domain enforces. Restating it here is what
                // holds if a row is ever written by something other than the domain — the seeder,
                // a migration, a repair script.
                table.HasCheckConstraint(
                    "ck_meeting_peak_participants",
                    "peak_participants >= 0 AND peak_participants <= 25");
            });

        builder.Ignore(m => m.DomainEvents);
        builder.Ignore(m => m.IsActive);
        builder.Ignore(m => m.ActiveParticipantCount);
        builder.Ignore(m => m.HasCapacity);

        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).ValueGeneratedNever();

        builder.Property(m => m.ConversationId).IsRequired();
        builder.Property(m => m.StartedBy).IsRequired();
        builder.Property(m => m.StartedAt).IsRequired();
        builder.Property(m => m.EndedAt);
        builder.Property(m => m.PeakParticipants).IsRequired();

        builder.HasOne<Conversation>()
            .WithMany()
            .HasForeignKey(m => m.ConversationId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Employee>()
            .WithMany()
            .HasForeignKey(m => m.StartedBy)
            .OnDelete(DeleteBehavior.Restrict);

        // Finding the live meeting for a conversation, which the join prompt asks on every render
        // of a conversation. Partial: an ended meeting is never what this lookup wants, and once the
        // table holds a year of them a full index would be almost entirely dead weight kept hot.
        builder.HasIndex(m => m.ConversationId)
            .HasDatabaseName("ix_meeting_active_by_conversation")
            .HasFilter("ended_at IS NULL");

        // The audit and retention scan, both of which work by age.
        builder.HasIndex(m => m.StartedAt).HasDatabaseName("ix_meeting_started_at");

        builder.OwnsMany(m => m.Participants, participation =>
        {
            participation.ToTable(
                "participation",
                table =>
                {
                    table.HasCheckConstraint(
                        "ck_participation_leaves_after_join",
                        "left_at IS NULL OR left_at >= joined_at");

                    table.HasCheckConstraint(
                        "ck_participation_share_seconds",
                        "shared_screen_seconds >= 0");
                });

            participation.WithOwner().HasForeignKey(p => p.MeetingId);

            // The composite key data-model.md declares. One row per person per meeting is what makes
            // FR-051's duration a single span rather than a set of fragments to reassemble.
            participation.HasKey(p => new { p.MeetingId, p.EmployeeId });

            participation.Property(p => p.EmployeeId).IsRequired();
            participation.Property(p => p.JoinedAt).IsRequired();
            participation.Property(p => p.LeftAt);
            participation.Property(p => p.SharedScreenSeconds).IsRequired();

            participation.Ignore(p => p.IsActive);

            participation.HasOne<Employee>()
                .WithMany()
                .HasForeignKey(p => p.EmployeeId)
                .OnDelete(DeleteBehavior.Restrict);

            // "Which meetings was this person in" — the question an audit request actually asks.
            participation.HasIndex(p => p.EmployeeId).HasDatabaseName("ix_participation_employee");
        });
    }
}
