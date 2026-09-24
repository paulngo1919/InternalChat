using InternalChat.Domain.Common;

namespace InternalChat.Domain.Meetings;

/// <summary>What a participant chose to share (FR-048).</summary>
/// <remarks>
/// The distinction is recorded rather than inferred because FR-048 makes a promise about the
/// <see cref="Window"/> case specifically — sharing one window "MUST NOT reveal any other
/// application" — and an audit record that could not tell the two apart could not evidence it.
/// </remarks>
public enum ShareScope
{
    /// <summary>The whole screen, including anything else on it.</summary>
    Screen = 1,

    /// <summary>A single application window. Nothing outside it is captured.</summary>
    Window = 2,
}

/// <summary>
/// One participant's screen share within a meeting (FR-048, FR-050).
/// </summary>
/// <remarks>
/// <para>
/// <b>The rule for simultaneous sharing is last-writer-wins, and it is visible</b> (FR-050). When a
/// second participant starts sharing, the first is stopped and told. The requirement is that the
/// system "apply one defined, visible rule" — the choice matters less than that everyone can
/// predict it, and this one matches what people expect from having used other tools.
/// </para>
/// <para>
/// The alternative — refusing the second sharer — reads as more polite and behaves worse in the
/// situation that actually happens: someone is presenting, the meeting moves on, and the next
/// person cannot take over without the first noticing and stopping. That produces a meeting where
/// people ask each other to stop sharing, which is the failure FR-050 exists to prevent.
/// </para>
/// <para>
/// Not an <see cref="Entity{TId}"/>. A share session belongs entirely to its meeting and has no
/// identity outside it, the same reasoning as <see cref="Participation"/>.
/// </para>
/// </remarks>
public sealed class ShareSession
{
    private ShareSession(Guid meetingId, Guid employeeId, ShareScope scope, DateTimeOffset startedAt)
    {
        MeetingId = meetingId;
        EmployeeId = employeeId;
        Scope = scope;
        StartedAt = startedAt;
    }

    /// <summary>The meeting.</summary>
    public Guid MeetingId { get; }

    /// <summary>Who is sharing.</summary>
    public Guid EmployeeId { get; }

    /// <summary>Screen or single window (FR-048).</summary>
    public ShareScope Scope { get; }

    /// <summary>When sharing began.</summary>
    public DateTimeOffset StartedAt { get; }

    /// <summary>When it stopped, or <c>null</c> while sharing.</summary>
    public DateTimeOffset? StoppedAt { get; private set; }

    /// <summary>Why it stopped. <c>null</c> while active.</summary>
    public ShareStopReason? StopReason { get; private set; }

    /// <summary>Whether this share is still live.</summary>
    public bool IsActive => StoppedAt is null;

    /// <summary>How long it lasted, for the participation total (FR-051).</summary>
    public int DurationSeconds => StoppedAt is { } stopped
        ? (int)Math.Max(0, Math.Round((stopped - StartedAt).TotalSeconds))
        : 0;

    /// <summary>Begins a share.</summary>
    public static ShareSession Start(Guid meetingId, Guid employeeId, ShareScope scope, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        if (!Enum.IsDefined(scope))
        {
            // Enum values are not closed in .NET and this one arrives from a client. Defaulting an
            // unknown scope to Screen would record a whole-screen share as though the person had
            // chosen it, which is exactly the claim FR-048 makes about the Window case.
            throw new ArgumentOutOfRangeException(
                nameof(scope), scope, "A share scope is 'screen' or 'window'.");
        }

        return new ShareSession(meetingId, employeeId, scope, clock.UtcNow);
    }

    /// <summary>Stops the share.</summary>
    /// <remarks>Idempotent, and the timestamp never moves — it feeds the audit duration.</remarks>
    public void Stop(ShareStopReason reason, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        if (!IsActive)
        {
            return;
        }

        StoppedAt = clock.UtcNow;
        StopReason = reason;
    }
}

/// <summary>Why a screen share ended.</summary>
/// <remarks>
/// Recorded because FR-050 requires the rule to be <em>visible</em>: a participant whose share
/// stopped needs to know whether they stopped it, someone took over, or the meeting ended. Without
/// the reason the client can only say "sharing stopped", which is the ambiguity the requirement is
/// written against.
/// </remarks>
public enum ShareStopReason
{
    /// <summary>The sharer stopped it themselves.</summary>
    Stopped = 1,

    /// <summary>Another participant started sharing and took over (FR-050).</summary>
    Superseded = 2,

    /// <summary>The sharer left the meeting.</summary>
    ParticipantLeft = 3,

    /// <summary>The meeting ended.</summary>
    MeetingEnded = 4,
}
