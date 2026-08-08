namespace InternalChat.Application.Abstractions;

/// <summary>One thing wrong with a request.</summary>
/// <param name="Field">Which field, in camelCase to match the wire contract.</param>
/// <param name="Message">What is wrong, phrased for the person who sent it.</param>
public sealed record ValidationError(string Field, string Message);

/// <summary>The outcome of validating a request.</summary>
/// <param name="Errors">Empty when the request is valid.</param>
public sealed record ValidationResult(IReadOnlyList<ValidationError> Errors)
{
    /// <summary>A passing result.</summary>
    public static ValidationResult Valid { get; } = new([]);

    /// <summary>Whether the request may proceed.</summary>
    public bool IsValid => Errors.Count == 0;

    /// <summary>Builds a failing result from one field error.</summary>
    public static ValidationResult Fail(string field, string message) =>
        new([new ValidationError(field, message)]);
}

/// <summary>
/// Validates a request at the Application boundary.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately hand-rolled rather than taken from a validation framework. Two reasons, and the
/// architectural one is the stronger: Application must not depend on a third-party framework's
/// attributes or fluent DSL, because that framework then appears in the signature of every use
/// case and becomes very hard to remove. The second reason is Principle VIII — it removes a
/// dependency whose licence would need re-verifying at every version bump.
/// </para>
/// <para>
/// This is boundary validation only. Domain invariants are enforced separately inside entities
/// and value objects, and Principle IV requires both: validation here produces a good error
/// message, while the invariant is what actually makes the bad state unreachable. Validation in
/// React is a usability feature and never a security control.
/// </para>
/// </remarks>
/// <typeparam name="TRequest">The request being validated.</typeparam>
public interface IValidator<in TRequest>
{
    /// <summary>Validates a request, returning every problem rather than only the first.</summary>
    ValueTask<ValidationResult> ValidateAsync(TRequest request, CancellationToken cancellationToken = default);
}
