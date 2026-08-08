namespace InternalChat.Application.Behaviors;

/// <summary>Invokes the next stage of the pipeline, ending at the use case handler.</summary>
public delegate Task<TResponse> UseCaseContinuation<TResponse>(CancellationToken cancellationToken);

/// <summary>
/// One use case. Constitution Principle II: one reason to change — a handler does one use case.
/// </summary>
/// <typeparam name="TRequest">The request this use case accepts.</typeparam>
/// <typeparam name="TResponse">What it returns.</typeparam>
public interface IUseCase<in TRequest, TResponse>
{
    /// <summary>Executes the use case.</summary>
    Task<TResponse> HandleAsync(TRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Cross-cutting concern wrapped around every use case — validation, logging, transactions,
/// audit.
/// </summary>
/// <remarks>
/// <para>
/// Hand-rolled rather than taken from MediatR, and not for fun: <b>MediatR v13 and later are
/// commercially licensed</b>. Adopting it would breach Constitution Principle VIII exactly the
/// way MassTransit v9 would (research.md D3), and it is just as easy to reach for by reflex.
/// The pipeline this project actually needs is small enough to own outright.
/// </para>
/// <para>
/// Owning it also keeps the ordering visible. Which behavior sits inside the transaction is a
/// correctness decision here, not a configuration detail buried in a framework's registration
/// order — see <c>UseCaseDispatcher</c> for the ordering and why.
/// </para>
/// </remarks>
public interface IPipelineBehavior<in TRequest, TResponse>
{
    /// <summary>Runs this behavior, calling <paramref name="continuation"/> to continue the pipeline.</summary>
    Task<TResponse> HandleAsync(
        TRequest request,
        UseCaseContinuation<TResponse> continuation,
        CancellationToken cancellationToken = default);
}

/// <summary>Dispatches a request through the pipeline to its use case.</summary>
public interface IUseCaseDispatcher
{
    /// <summary>Sends a request and returns the use case's response.</summary>
    Task<TResponse> SendAsync<TRequest, TResponse>(
        TRequest request,
        CancellationToken cancellationToken = default)
        where TRequest : notnull;
}
