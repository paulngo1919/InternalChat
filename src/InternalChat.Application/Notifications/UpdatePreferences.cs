using InternalChat.Application.Abstractions;
using InternalChat.Application.Behaviors;
using InternalChat.Domain.Notifications;

namespace InternalChat.Application.Notifications;

/// <summary>Reads one employee's notification preferences.</summary>
public sealed record GetPreferences(Guid EmployeeId);

/// <summary>
/// Serves preferences, defaulting rather than failing for an employee who never set any.
/// </summary>
/// <remarks>
/// data-model.md's <c>notification_preference</c> row is created on first write, not on employee
/// creation — an employee who never opens the settings page has never needed one. Reading before
/// that point returns <see cref="NotificationPreference.Default"/> rather than a 404, so the
/// settings page always has something to render.
/// </remarks>
public sealed class GetPreferencesHandler : IUseCase<GetPreferences, NotificationPreference>
{
    private readonly INotificationPreferenceRepository _preferences;

    /// <summary>Creates the handler.</summary>
    public GetPreferencesHandler(INotificationPreferenceRepository preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        _preferences = preferences;
    }

    /// <inheritdoc />
    public async Task<NotificationPreference> HandleAsync(
        GetPreferences request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return await _preferences.FindAsync(request.EmployeeId, cancellationToken).ConfigureAwait(false)
            ?? NotificationPreference.Default(request.EmployeeId);
    }
}

/// <summary>Replaces an employee's do-not-disturb window and digest threshold (FR-037, FR-038).</summary>
public sealed record UpdatePreferences(
    Guid EmployeeId,
    TimeOnly? DndStart,
    TimeOnly? DndEnd,
    string TimeZoneId,
    int DigestAfterMinutes) : ITransactionalRequest;

/// <summary>Creates the preference row on first write, or updates the existing one.</summary>
public sealed class UpdatePreferencesHandler : IUseCase<UpdatePreferences, NotificationPreference>
{
    private readonly INotificationPreferenceRepository _preferences;

    /// <summary>Creates the handler.</summary>
    public UpdatePreferencesHandler(INotificationPreferenceRepository preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        _preferences = preferences;
    }

    /// <inheritdoc />
    public async Task<NotificationPreference> HandleAsync(
        UpdatePreferences request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Already validated by ValidationBehavior; constructed again here to get the value object.
        DoNotDisturbWindow window = DoNotDisturbWindow.Create(request.DndStart, request.DndEnd, request.TimeZoneId);

        NotificationPreference? preference = await _preferences
            .FindAsync(request.EmployeeId, cancellationToken)
            .ConfigureAwait(false);

        if (preference is null)
        {
            preference = NotificationPreference.Default(request.EmployeeId);
            preference.Update(window, request.DigestAfterMinutes);
            await _preferences.AddAsync(preference, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            preference.Update(window, request.DigestAfterMinutes);
        }

        return preference;
    }
}

/// <summary>Rejects an update before it can write anything.</summary>
/// <remarks>
/// <see cref="DoNotDisturbWindow.Create(TimeOnly?, TimeOnly?, string)"/> already refuses a
/// mismatched start/end pair and an unrecognised time zone id — this validator exists so those
/// surface as a 400 with a named field rather than as the 500 an uncaught
/// <see cref="ArgumentException"/> from inside the handler would produce.
/// </remarks>
public sealed class UpdatePreferencesValidator : IValidator<UpdatePreferences>
{
    /// <inheritdoc />
    public ValueTask<ValidationResult> ValidateAsync(
        UpdatePreferences request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            DoNotDisturbWindow.Create(request.DndStart, request.DndEnd, request.TimeZoneId);
        }
        catch (ArgumentException ex)
        {
            return ValueTask.FromResult(ValidationResult.Fail("dndStart", ex.Message));
        }

        if (request.DigestAfterMinutes is < NotificationPreference.MinimumDigestAfterMinutes
            or > NotificationPreference.MaximumDigestAfterMinutes)
        {
            return ValueTask.FromResult(ValidationResult.Fail(
                "digestAfterMinutes",
                $"A digest threshold is between {NotificationPreference.MinimumDigestAfterMinutes} and "
                + $"{NotificationPreference.MaximumDigestAfterMinutes} minutes."));
        }

        return ValueTask.FromResult(ValidationResult.Valid);
    }
}
