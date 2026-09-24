namespace InternalChat.Domain.Notifications;

/// <summary>
/// One employee's notification settings: a do-not-disturb window (FR-037) and a digest threshold
/// (FR-038).
/// </summary>
/// <remarks>
/// <see cref="DoNotDisturb"/> is computed from the raw columns rather than stored as an owned
/// type. <see cref="DoNotDisturbWindow"/> validates its own time zone id at construction, and a raw
/// scalar mapping is what lets EF Core materialise a row with whatever was last saved without
/// re-running that validation on every read — validation happens once, in <see cref="Update"/>,
/// which is the only path a row can be written through.
/// </remarks>
public sealed class NotificationPreference
{
    /// <summary>Default digest threshold (data-model.md), and what a new employee starts with.</summary>
    public const int DefaultDigestAfterMinutes = 60;

    /// <summary>Floor. Zero would make every message its own digest, which is not a digest.</summary>
    public const int MinimumDigestAfterMinutes = 1;

    /// <summary>
    /// Ceiling. A day. FR-038 asks for a *reachable* backlog summary — a threshold longer than this
    /// is indistinguishable from never being notified.
    /// </summary>
    public const int MaximumDigestAfterMinutes = 24 * 60;

    private NotificationPreference(
        Guid employeeId, TimeOnly? dndStart, TimeOnly? dndEnd, string timeZoneId, int digestAfterMinutes)
    {
        EmployeeId = employeeId;
        DndStart = dndStart;
        DndEnd = dndEnd;
        TimeZoneId = timeZoneId;
        DigestAfterMinutes = digestAfterMinutes;
    }

    /// <summary>The employee this belongs to.</summary>
    public Guid EmployeeId { get; private set; }

    /// <summary>Raw column backing <see cref="DoNotDisturb"/>.</summary>
    public TimeOnly? DndStart { get; private set; }

    /// <summary>Raw column backing <see cref="DoNotDisturb"/>.</summary>
    public TimeOnly? DndEnd { get; private set; }

    /// <summary>IANA time zone id, meaningful even with no DND window configured (digest scheduling).</summary>
    public string TimeZoneId { get; private set; } = "UTC";

    /// <summary>Minutes of silence before backlog is batched into one summary (FR-038).</summary>
    public int DigestAfterMinutes { get; private set; } = DefaultDigestAfterMinutes;

    /// <summary>The evaluable do-not-disturb window.</summary>
    public DoNotDisturbWindow DoNotDisturb => DoNotDisturbWindow.Create(DndStart, DndEnd, TimeZoneId);

    /// <summary>The settings an employee who has never configured anything gets (data-model.md defaults).</summary>
    public static NotificationPreference Default(Guid employeeId) =>
        new(employeeId, dndStart: null, dndEnd: null, timeZoneId: "UTC", DefaultDigestAfterMinutes);

    /// <summary>
    /// Replaces the settings.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="digestAfterMinutes"/> is out of range.</exception>
    public void Update(DoNotDisturbWindow doNotDisturb, int digestAfterMinutes)
    {
        ArgumentNullException.ThrowIfNull(doNotDisturb);

        if (digestAfterMinutes is < MinimumDigestAfterMinutes or > MaximumDigestAfterMinutes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(digestAfterMinutes),
                digestAfterMinutes,
                $"A digest threshold is between {MinimumDigestAfterMinutes} and "
                + $"{MaximumDigestAfterMinutes} minutes.");
        }

        DndStart = doNotDisturb.Start;
        DndEnd = doNotDisturb.End;
        TimeZoneId = doNotDisturb.TimeZoneId;
        DigestAfterMinutes = digestAfterMinutes;
    }
}
