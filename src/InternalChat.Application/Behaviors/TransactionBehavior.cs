using InternalChat.Application.Abstractions;

namespace InternalChat.Application.Behaviors;

/// <summary>
/// Marks a request as changing state, so the pipeline wraps it in a transaction.
/// </summary>
/// <remarks>
/// Queries deliberately do not get a transaction — opening one per history page at 100 requests
/// per second buys nothing and costs connection-pool headroom the send path needs.
/// </remarks>
public interface ITransactionalRequest;

/// <summary>
/// Wraps state-changing use cases in a database transaction.
/// </summary>
/// <remarks>
/// <para>
/// This is what makes the transactional outbox work (Constitution Principle VI). The message
/// row and its outbox row are written inside one transaction, so a crash between them is
/// impossible. Without this, <c>SendMessage</c> would be a dual write: commit the message, then
/// publish — and a failure in between silently loses the notification, permanently and
/// invisibly.
/// </para>
/// <para>
/// Sits inside validation (so invalid input never opens a transaction) and outside audit (so a
/// successful action and its audit record commit together).
/// </para>
/// </remarks>
public sealed class TransactionBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
{
    private readonly IUnitOfWork _unitOfWork;

    /// <summary>Creates the behavior.</summary>
    public TransactionBehavior(IUnitOfWork unitOfWork)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);
        _unitOfWork = unitOfWork;
    }

    /// <inheritdoc />
    public async Task<TResponse> HandleAsync(
        TRequest request,
        UseCaseContinuation<TResponse> continuation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(continuation);

        if (request is not ITransactionalRequest)
        {
            return await continuation(cancellationToken).ConfigureAwait(false);
        }

        return await _unitOfWork.ExecuteInTransactionAsync(
            async ct =>
            {
                TResponse response = await continuation(ct).ConfigureAwait(false);
                await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
                return response;
            },
            cancellationToken).ConfigureAwait(false);
    }
}
