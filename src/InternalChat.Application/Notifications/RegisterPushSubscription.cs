using InternalChat.Application.Abstractions;
using InternalChat.Application.Behaviors;

namespace InternalChat.Application.Notifications;

/// <summary>Registers this browser for push (FR-034).</summary>
public sealed record RegisterPushSubscription(
    Guid EmployeeId,
    Uri Endpoint,
    string P256dh,
    string Auth,
    string? UserAgent) : ITransactionalRequest;

/// <summary>
/// Stores a subscription. Registering the same endpoint again refreshes it rather than duplicating
/// it — see <see cref="IPushSubscriptionStore.RegisterAsync"/>'s remarks.
/// </summary>
public sealed class RegisterPushSubscriptionHandler : IUseCase<RegisterPushSubscription, bool>
{
    private readonly IPushSubscriptionStore _subscriptions;

    /// <summary>Creates the handler.</summary>
    public RegisterPushSubscriptionHandler(IPushSubscriptionStore subscriptions)
    {
        ArgumentNullException.ThrowIfNull(subscriptions);
        _subscriptions = subscriptions;
    }

    /// <inheritdoc />
    public async Task<bool> HandleAsync(
        RegisterPushSubscription request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        await _subscriptions
            .RegisterAsync(
                request.EmployeeId, request.Endpoint, request.P256dh, request.Auth, request.UserAgent,
                cancellationToken)
            .ConfigureAwait(false);

        return true;
    }
}

/// <summary>Rejects a registration before it can write anything.</summary>
public sealed class RegisterPushSubscriptionValidator : IValidator<RegisterPushSubscription>
{
    /// <inheritdoc />
    public ValueTask<ValidationResult> ValidateAsync(
        RegisterPushSubscription request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        List<ValidationError> errors = [];

        if (string.IsNullOrWhiteSpace(request.P256dh))
        {
            errors.Add(new ValidationError("p256dh", "A push subscription requires the client's public key."));
        }

        if (string.IsNullOrWhiteSpace(request.Auth))
        {
            errors.Add(new ValidationError("auth", "A push subscription requires the client's auth secret."));
        }

        return ValueTask.FromResult(errors.Count == 0 ? ValidationResult.Valid : new ValidationResult(errors));
    }
}
