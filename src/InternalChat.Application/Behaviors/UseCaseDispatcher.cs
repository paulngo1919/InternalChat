using Microsoft.Extensions.DependencyInjection;

namespace InternalChat.Application.Behaviors;

/// <summary>
/// Resolves a use case and wraps it in its pipeline behaviors.
/// </summary>
/// <remarks>
/// <para>
/// Behaviors are composed in registration order, outermost first. That order is load-bearing and
/// is fixed in <c>ApplicationServiceCollectionExtensions</c> rather than left to whatever order
/// registrations happen to occur in:
/// </para>
/// <list type="number">
///   <item><description>
///     <b>Logging</b> — outermost, so the recorded duration is what the caller actually waited.
///   </description></item>
///   <item><description>
///     <b>Validation</b> — before any transaction, so invalid input never opens one.
///   </description></item>
///   <item><description>
///     <b>Transaction</b> — so the state change and its outbox rows commit together
///     (Principle VI).
///   </description></item>
///   <item><description>
///     <b>Audit</b> — innermost, inside the transaction, so a successful action and its audit
///     record commit atomically.
///   </description></item>
/// </list>
/// <para>
/// Moving audit outside the transaction would leave a window where an action succeeded but was
/// never recorded. Moving validation inside it would open a transaction for input that was
/// never going to be accepted.
/// </para>
/// </remarks>
public sealed class UseCaseDispatcher : IUseCaseDispatcher
{
    private readonly IServiceProvider _services;

    /// <summary>Creates the dispatcher.</summary>
    public UseCaseDispatcher(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
    }

    /// <inheritdoc />
    public Task<TResponse> SendAsync<TRequest, TResponse>(
        TRequest request,
        CancellationToken cancellationToken = default)
        where TRequest : notnull
    {
        ArgumentNullException.ThrowIfNull(request);

        IUseCase<TRequest, TResponse> useCase =
            _services.GetRequiredService<IUseCase<TRequest, TResponse>>();

        UseCaseContinuation<TResponse> pipeline = ct => useCase.HandleAsync(request, ct);

        // Reversed so the first-registered behavior ends up outermost.
        IEnumerable<IPipelineBehavior<TRequest, TResponse>> behaviors =
            _services.GetServices<IPipelineBehavior<TRequest, TResponse>>().Reverse();

        foreach (IPipelineBehavior<TRequest, TResponse> behavior in behaviors)
        {
            UseCaseContinuation<TResponse> next = pipeline;
            pipeline = ct => behavior.HandleAsync(request, next, ct);
        }

        return pipeline(cancellationToken);
    }
}
