using InternalChat.Application.Abstractions;

namespace InternalChat.Application.Behaviors;

/// <summary>
/// Runs every registered validator for a request before the use case sees it.
/// </summary>
/// <remarks>
/// <para>
/// Sits outside the transaction behavior deliberately: an invalid request must be rejected
/// before a database transaction is opened, so bad input costs nothing but a round trip.
/// </para>
/// <para>
/// Constitution Principle IV: untrusted input is validated at the boundary AND re-validated as
/// domain invariants. This is the first half. It exists to produce a good error message; the
/// invariant inside the entity is what actually makes the bad state unreachable.
/// </para>
/// </remarks>
public sealed class ValidationBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
{
    private readonly IEnumerable<IValidator<TRequest>> _validators;

    /// <summary>Creates the behavior over every validator registered for the request type.</summary>
    public ValidationBehavior(IEnumerable<IValidator<TRequest>> validators)
    {
        ArgumentNullException.ThrowIfNull(validators);
        _validators = validators;
    }

    /// <inheritdoc />
    public async Task<TResponse> HandleAsync(
        TRequest request,
        UseCaseContinuation<TResponse> continuation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(continuation);

        List<ValidationError> errors = [];

        foreach (IValidator<TRequest> validator in _validators)
        {
            ValidationResult result = await validator
                .ValidateAsync(request, cancellationToken)
                .ConfigureAwait(false);

            // Collect from every validator rather than stopping at the first failure. A user
            // fixing one field at a time across four round trips is a worse experience than
            // being told all four problems at once.
            if (!result.IsValid)
            {
                errors.AddRange(result.Errors);
            }
        }

        if (errors.Count > 0)
        {
            throw new ValidationFailedException(errors);
        }

        return await continuation(cancellationToken).ConfigureAwait(false);
    }
}
