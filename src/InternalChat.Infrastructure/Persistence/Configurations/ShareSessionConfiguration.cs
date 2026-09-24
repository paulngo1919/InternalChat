using InternalChat.Domain.Meetings;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InternalChat.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="ShareSession"/> (T201, FR-048, FR-050, FR-051).
/// </summary>
/// <remarks>
/// <para>
/// A table of its own rather than an owned collection on <c>Meeting</c>, unlike <c>participation</c>.
/// The difference is write rate: people start and stop sharing repeatedly within one call, and
/// making it owned would mean loading a meeting's entire share history to record one claim.
/// </para>
/// <para>
/// <b>A partial unique index is what actually enforces FR-050.</b> The application stops the
/// current share before starting the next, but two concurrent claims could interleave between the
/// read and the write — and the outcome would be two active shares, which is the one state the
/// requirement forbids. The index makes that unrepresentable in the database rather than merely
/// unlikely in the code.
/// </para>
/// </remarks>
internal sealed class ShareSessionConfiguration : IEntityTypeConfiguration<ShareSession>
{
    public void Configure(EntityTypeBuilder<ShareSession> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable(
            "share_session",
            table =>
            {
                table.HasCheckConstraint(
                    "ck_share_session_stops_after_start",
                    "stopped_at IS NULL OR stopped_at >= started_at");

                // A stopped share has a reason and a running one does not. Stated as a
                // biconditional so a half-applied stop cannot look settled — the reason is what
                // makes FR-050's rule visible, and a stop without one tells nobody anything.
                table.HasCheckConstraint(
                    "ck_share_session_stop_reason",
                    "(stopped_at IS NULL) = (stop_reason IS NULL)");
            });

        // The natural key. A share is identified by who started it, in which meeting, when —
        // there is no separate id because there is nothing else that would need one.
        builder.HasKey(s => new { s.MeetingId, s.EmployeeId, s.StartedAt });

        builder.Property(s => s.MeetingId).IsRequired();
        builder.Property(s => s.EmployeeId).IsRequired();
        builder.Property(s => s.StartedAt).IsRequired();
        builder.Property(s => s.Scope).IsRequired();
        builder.Property(s => s.StoppedAt);
        builder.Property(s => s.StopReason);

        builder.Ignore(s => s.IsActive);
        builder.Ignore(s => s.DurationSeconds);

        builder.HasOne<Meeting>()
            .WithMany()
            .HasForeignKey(s => s.MeetingId)
            .OnDelete(DeleteBehavior.Cascade);

        // THE constraint. One active share per meeting, enforced by PostgreSQL rather than by the
        // application winning a race it cannot see.
        builder.HasIndex(s => s.MeetingId)
            .IsUnique()
            .HasDatabaseName("ux_share_session_one_active_per_meeting")
            .HasFilter("stopped_at IS NULL");
    }
}
