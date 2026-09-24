namespace InternalChat.Domain.Notifications;

/// <summary>
/// A local do-not-disturb window (FR-037): during it, unread state still updates but no
/// interrupting notification is delivered.
/// </summary>
/// <remarks>
/// <para>
/// <b>Time zone is not decoration.</b> <c>dnd_start</c>/<c>dnd_end</c> (data-model.md) are local
/// times — "18:00" means nothing without knowing whose 18:00 — so every evaluation converts the
/// instant being checked into this window's zone before comparing. An employee in
/// <c>Asia/Ho_Chi_Minh</c> and one in UTC with the same stored start/end are silenced at different
/// moments in absolute time, and that is correct, not a bug.
/// </para>
/// <para>
/// <b>Overnight windows are the case that is easy to get backwards.</b> "18:00 to 08:00" spans
/// midnight: the window is active from 18:00 to 23:59:59 <em>and</em> from 00:00 to 07:59:59. A
/// same-day comparison (<c>start &lt;= now &lt; end</c>) is wrong whenever <c>start &gt; end</c>,
/// which is exactly the shape most people actually set for sleeping hours.
/// </para>
/// </remarks>
public sealed class DoNotDisturbWindow
{
    private readonly TimeZoneInfo _zone;

    private DoNotDisturbWindow(TimeOnly? start, TimeOnly? end, TimeZoneInfo zone)
    {
        Start = start;
        End = end;
        _zone = zone;
    }

    /// <summary>Local start of the window, or <c>null</c> when no window is set.</summary>
    public TimeOnly? Start { get; }

    /// <summary>Local end of the window, or <c>null</c> when no window is set.</summary>
    public TimeOnly? End { get; }

    /// <summary>
    /// IANA time zone id the start and end are local to. Meaningful even with no window set — an
    /// employee who has not configured DND still has a time zone their digest schedule reads.
    /// </summary>
    public string TimeZoneId => _zone.Id;

    /// <summary>No do-not-disturb window configured.</summary>
    public static DoNotDisturbWindow None(string timeZoneId = "UTC") => new(null, null, RequireTimeZone(timeZoneId));

    /// <summary>
    /// Creates a window, or the absence of one when both bounds are <c>null</c>.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Exactly one of <paramref name="start"/>/<paramref name="end"/> is set, or the time zone id
    /// is not one the runtime recognises.
    /// </exception>
    public static DoNotDisturbWindow Create(TimeOnly? start, TimeOnly? end, string timeZoneId) =>
        Create(start, end, RequireTimeZone(timeZoneId));

    /// <summary>
    /// Creates a window from an already-resolved zone.
    /// </summary>
    /// <remarks>
    /// The string overload resolves a fresh <see cref="TimeZoneInfo"/> on every call; a caller
    /// evaluating many windows for the same employee (the notification fan-out, most plausibly) can
    /// resolve once and reuse it here instead.
    /// </remarks>
    public static DoNotDisturbWindow Create(TimeOnly? start, TimeOnly? end, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);

        if (start.HasValue != end.HasValue)
        {
            throw new ArgumentException(
                "A do-not-disturb window needs both a start and an end, or neither — one without "
                + "the other cannot be evaluated.",
                nameof(end));
        }

        return new DoNotDisturbWindow(start, end, zone);
    }

    /// <summary>
    /// Whether the window is suppressing notifications at the given instant.
    /// </summary>
    /// <remarks>
    /// <c>false</c> whenever no window is set — mute is a separate, per-conversation control
    /// (<c>Membership.MutedUntil</c>); a preference row with no DND configured must not silence
    /// anything.
    /// </remarks>
    public bool IsActiveAt(DateTimeOffset instant)
    {
        if (Start is not { } start || End is not { } end)
        {
            return false;
        }

        TimeOnly local = TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, _zone).DateTime);

        return start <= end
            ? local >= start && local < end
            : local >= start || local < end;
    }

    private static TimeZoneInfo RequireTimeZone(string timeZoneId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(timeZoneId);

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (TimeZoneNotFoundException ex)
        {
            throw new ArgumentException(
                $"'{timeZoneId}' is not a time zone id this platform recognises.", nameof(timeZoneId), ex);
        }
        catch (InvalidTimeZoneException ex)
        {
            throw new ArgumentException(
                $"'{timeZoneId}' is a corrupt time zone definition.", nameof(timeZoneId), ex);
        }
    }
}
