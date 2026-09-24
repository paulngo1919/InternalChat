using InternalChat.Domain.Common;

namespace InternalChat.Domain.Messages;

/// <summary>
/// How long after sending a message may still be edited or deleted (FR-014).
/// </summary>
/// <remarks>
/// <para>
/// Its own type rather than a bare <c>TimeSpan</c> constant, because the boundary condition is the
/// part that goes wrong. "Within 24 hours" and "less than 24 hours" differ by an instant, and the
/// difference is invisible in a line that writes <c>&lt;</c> where it meant <c>&lt;=</c>. Naming the
/// comparison once means there is one place to be right.
/// </para>
/// <para>
/// The window is measured from <c>sent_at</c> and never from <c>edited_at</c>: otherwise an author
/// could edit at 23 hours, again at 46, and keep a message editable indefinitely — which is not a
/// 24-hour window at all.
/// </para>
/// </remarks>
public static class EditWindow
{
    /// <summary>
    /// The window, from the spec's Assumptions section.
    /// </summary>
    public static readonly TimeSpan Duration = TimeSpan.FromHours(24);

    /// <summary>
    /// Whether a message sent at <paramref name="sentAt"/> may still be changed.
    /// </summary>
    /// <remarks>
    /// <b>Inclusive at the boundary.</b> Exactly 24 hours is inside the window: an author told
    /// "within 24 hours" and refused at 24:00:00 has been told something untrue.
    /// </remarks>
    public static bool IsOpen(DateTimeOffset sentAt, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        return clock.UtcNow - sentAt <= Duration;
    }

    /// <summary>When the window closes for a message sent at the given instant.</summary>
    public static DateTimeOffset ClosesAt(DateTimeOffset sentAt) => sentAt + Duration;
}
