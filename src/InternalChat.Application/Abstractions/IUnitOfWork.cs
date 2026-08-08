namespace InternalChat.Application.Abstractions;

/// <summary>
/// Transaction boundary for a use case.
/// </summary>
/// <remarks>
/// Principle VI requires the state change and its outbox rows to commit together. Handlers
/// therefore never call a repository's save directly — the transaction behavior in the pipeline
/// wraps the whole use case so a partial write cannot escape.
/// </remarks>
public interface IUnitOfWork
{
    /// <summary>Commits pending changes, including any outbox rows written during this scope.</summary>
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs an operation inside an explicit transaction, committing on success and rolling back
    /// on any exception.
    /// </summary>
    Task<TResult> ExecuteInTransactionAsync<TResult>(
        Func<CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken = default);
}
