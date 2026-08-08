using InternalChat.Application.Abstractions;

namespace InternalChat.Application.Behaviors;

/// <summary>
/// Thrown when a request fails boundary validation. Mapped to a 400 Problem Details response by
/// the API, which reports every field at once rather than one per round trip.
/// </summary>
public sealed class ValidationFailedException : Exception
{
    /// <summary>Creates an exception carrying the collected errors.</summary>
    public ValidationFailedException(IReadOnlyList<ValidationError> errors)
        : base(BuildMessage(errors))
    {
        Errors = errors;
    }

    /// <summary>Creates an exception with no specific errors.</summary>
    public ValidationFailedException()
        : this([])
    {
    }

    /// <summary>Creates an exception with a message.</summary>
    public ValidationFailedException(string message)
        : base(message)
    {
        Errors = [];
    }

    /// <summary>Creates an exception with a message and inner exception.</summary>
    public ValidationFailedException(string message, Exception innerException)
        : base(message, innerException)
    {
        Errors = [];
    }

    /// <summary>Every problem found, not just the first.</summary>
    public IReadOnlyList<ValidationError> Errors { get; }

    private static string BuildMessage(IReadOnlyList<ValidationError> errors) =>
        errors.Count == 0
            ? "Validation failed."
            : $"Validation failed: {string.Join("; ", errors.Select(e => $"{e.Field}: {e.Message}"))}";
}
