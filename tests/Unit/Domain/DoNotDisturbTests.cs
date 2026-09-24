using InternalChat.Domain.Notifications;

namespace InternalChat.UnitTests.Domain;

/// <summary>
/// T123 — do-not-disturb evaluation across time zones (FR-037).
/// </summary>
/// <remarks>
/// The load-bearing case is the overnight window: "18:00 to 08:00" spans midnight, and a naive
/// <c>start &lt;= now &lt; end</c> comparison is backwards for it — it would report the window
/// active only in the ten minutes between 18:00 and 08:00 the *same* day, which never happens.
/// </remarks>
public sealed class DoNotDisturbTests
{
    [Fact]
    public void No_window_configured_is_never_active()
    {
        DoNotDisturbWindow window = DoNotDisturbWindow.None();

        Assert.False(window.IsActiveAt(DateTimeOffset.Parse("2026-01-01T03:00:00Z", System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void A_same_day_window_is_active_between_its_bounds()
    {
        // 12:00–14:00 UTC, a lunchtime DND window — the ordinary, non-overnight case.
        DoNotDisturbWindow window = DoNotDisturbWindow.Create(
            new TimeOnly(12, 0), new TimeOnly(14, 0), "UTC");

        Assert.True(window.IsActiveAt(DateTimeOffset.Parse("2026-01-01T13:00:00Z", System.Globalization.CultureInfo.InvariantCulture)));
        Assert.False(window.IsActiveAt(DateTimeOffset.Parse("2026-01-01T15:00:00Z", System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void The_end_bound_is_exclusive()
    {
        DoNotDisturbWindow window = DoNotDisturbWindow.Create(new TimeOnly(12, 0), new TimeOnly(14, 0), "UTC");

        Assert.False(window.IsActiveAt(DateTimeOffset.Parse("2026-01-01T14:00:00Z", System.Globalization.CultureInfo.InvariantCulture)));
    }

    /// <summary>The case this test file exists for: a window that crosses midnight.</summary>
    [Theory]
    [InlineData("2026-01-01T20:00:00Z", true)] // 20:00, same evening — inside
    [InlineData("2026-01-02T03:00:00Z", true)] // 03:00, after midnight — still inside
    [InlineData("2026-01-01T12:00:00Z", false)] // midday — outside
    [InlineData("2026-01-01T18:00:00Z", true)] // exactly the start — inside (inclusive)
    [InlineData("2026-01-02T08:00:00Z", false)] // exactly the end — outside (exclusive)
    public void An_overnight_window_spans_midnight_correctly(string instant, bool expectedActive)
    {
        DoNotDisturbWindow window = DoNotDisturbWindow.Create(new TimeOnly(18, 0), new TimeOnly(8, 0), "UTC");

        Assert.Equal(expectedActive, window.IsActiveAt(DateTimeOffset.Parse(instant, System.Globalization.CultureInfo.InvariantCulture)));
    }

    /// <summary>
    /// The same absolute instant is inside the window for one time zone and outside it for another.
    /// </summary>
    /// <remarks>
    /// This is the test that would pass even if the implementation silently ignored the time zone
    /// and compared UTC clock time directly — asserting on one zone alone could not catch that bug.
    /// </remarks>
    [Fact]
    public void The_same_instant_differs_by_time_zone()
    {
        // A synthetic fixed +7 zone rather than the real "Asia/Ho_Chi_Minh" IANA id: the id
        // resolves through the OS time zone database, which this repo's containers (Linux) carry
        // and a bare Windows dev machine without ICU data may not — the property under test is
        // "a non-UTC offset changes the answer", not "this OS recognises this specific IANA name".
        TimeZoneInfo plusSeven = TimeZoneInfo.CreateCustomTimeZone(
            "Test/UTC+7", TimeSpan.FromHours(7), "Test UTC+7", "Test UTC+7");

        // 2026-01-01T13:00:00Z is 20:00 at UTC+7 and 13:00 at UTC.
        DateTimeOffset instant = DateTimeOffset.Parse("2026-01-01T13:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

        DoNotDisturbWindow atPlusSeven = DoNotDisturbWindow.Create(new TimeOnly(18, 0), new TimeOnly(8, 0), plusSeven);
        DoNotDisturbWindow atUtc = DoNotDisturbWindow.Create(new TimeOnly(18, 0), new TimeOnly(8, 0), TimeZoneInfo.Utc);

        Assert.True(atPlusSeven.IsActiveAt(instant));
        Assert.False(atUtc.IsActiveAt(instant));
    }

    [Fact]
    public void Setting_only_a_start_without_an_end_is_refused()
    {
        Assert.Throws<ArgumentException>(() => DoNotDisturbWindow.Create(new TimeOnly(18, 0), null, "UTC"));
    }

    [Fact]
    public void Setting_only_an_end_without_a_start_is_refused()
    {
        Assert.Throws<ArgumentException>(() => DoNotDisturbWindow.Create(null, new TimeOnly(8, 0), "UTC"));
    }

    [Fact]
    public void An_unrecognised_time_zone_id_is_refused()
    {
        Assert.Throws<ArgumentException>(() => DoNotDisturbWindow.Create(new TimeOnly(18, 0), new TimeOnly(8, 0), "Not/AZone"));
    }

    [Fact]
    public void A_preference_with_no_window_configured_defaults_to_utc_and_the_stated_digest_threshold()
    {
        NotificationPreference preference = NotificationPreference.Default(Guid.CreateVersion7());

        Assert.Equal("UTC", preference.TimeZoneId);
        Assert.Equal(NotificationPreference.DefaultDigestAfterMinutes, preference.DigestAfterMinutes);
        Assert.False(preference.DoNotDisturb.IsActiveAt(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Updating_a_preference_replaces_the_window_and_the_digest_threshold()
    {
        NotificationPreference preference = NotificationPreference.Default(Guid.CreateVersion7());

        // "UTC" here rather than a named IANA zone: this test is about Update() copying the window's
        // fields onto the preference, not about time zone resolution, which DoNotDisturbTests
        // exercises separately.
        DoNotDisturbWindow window = DoNotDisturbWindow.Create(new TimeOnly(22, 0), new TimeOnly(7, 0), "UTC");
        preference.Update(window, digestAfterMinutes: 30);

        Assert.Equal(new TimeOnly(22, 0), preference.DndStart);
        Assert.Equal(new TimeOnly(7, 0), preference.DndEnd);
        Assert.Equal("UTC", preference.TimeZoneId);
        Assert.Equal(30, preference.DigestAfterMinutes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1441)]
    public void A_digest_threshold_outside_the_valid_range_is_refused(int minutes)
    {
        NotificationPreference preference = NotificationPreference.Default(Guid.CreateVersion7());

        Assert.Throws<ArgumentOutOfRangeException>(
            () => preference.Update(DoNotDisturbWindow.None(), minutes));
    }
}
