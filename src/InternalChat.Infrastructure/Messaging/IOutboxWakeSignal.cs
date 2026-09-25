namespace InternalChat.Infrastructure.Messaging;

/// <summary>
/// Tells the outbox dispatcher that there may be rows to publish (002 research R1).
/// </summary>
/// <remarks>
/// The narrow seam between <see cref="OutboxNotificationListener"/>, which knows about PostgreSQL,
/// and <see cref="OutboxDispatcherService"/>, which should not. Advisory only: the dispatcher always
/// reads the table, and a wake with nothing to do costs one empty query.
/// </remarks>
public interface IOutboxWakeSignal
{
    /// <summary>Wakes the dispatcher, or arranges for its next wait to return at once.</summary>
    void Signal();

    /// <summary>
    /// Waits for a signal or until <paramref name="timeout"/> passes.
    /// </summary>
    /// <returns><c>true</c> if signalled; <c>false</c> on timeout.</returns>
    Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>
/// A single-slot, coalescing <see cref="IOutboxWakeSignal"/> (data-model §3).
/// </summary>
/// <remarks>
/// <para>
/// <b>At most one pending wake.</b> A burst of commits rings the doorbell once per transaction, and
/// most of those rings arrive while the dispatcher is already mid-pass publishing the rows they
/// announce. Queuing one wake per ring would follow the burst with as many empty passes; one slot
/// means "at least one ring since you last looked", which is all the dispatcher needs to know.
/// </para>
/// <para>
/// A signal raised while nobody is waiting is kept, not lost — otherwise a commit that lands between
/// a pass ending and the next wait starting would sit until the backstop poll.
/// </para>
/// </remarks>
public sealed class OutboxWakeSignal : IOutboxWakeSignal, IDisposable
{
    private readonly SemaphoreSlim _slot = new(0, 1);

    /// <inheritdoc />
    public void Signal()
    {
        try
        {
            _slot.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already signalled and not yet consumed. That is the coalescing, not an error.
        }
    }

    /// <inheritdoc />
    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        _slot.WaitAsync(timeout, cancellationToken);

    /// <inheritdoc />
    public void Dispose() => _slot.Dispose();
}
