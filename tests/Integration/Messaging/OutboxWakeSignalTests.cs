using System.Diagnostics;
using InternalChat.Infrastructure.Messaging;

namespace InternalChat.IntegrationTests.Messaging;

/// <summary>
/// 002 T015 — the in-process wake signal between the listener and the dispatcher loop (data-model §3).
/// </summary>
/// <remarks>
/// <para>
/// Pure logic with no container, but kept in this project because the unit suite deliberately does
/// not reference Infrastructure (its coverage gate would start measuring a layer it cannot reach).
/// No stack fixture, so these run without Docker.
/// </para>
/// <para>
/// The property that matters is coalescing. A burst of commits rings the doorbell many times while
/// the dispatcher is mid-pass; each ring must not queue its own pass, or a burst of 1,000 sends
/// would schedule 1,000 empty passes after the table was already drained.
/// </para>
/// </remarks>
public sealed class OutboxWakeSignalTests
{
    [Fact]
    public async Task A_signal_releases_a_waiter()
    {
        OutboxWakeSignal signal = new();

        Task<bool> waiting = signal.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        signal.Signal();

        Assert.True(await waiting);
    }

    [Fact]
    public async Task A_signal_raised_before_the_wait_is_not_lost()
    {
        // The notification can arrive while the dispatcher is still finishing a pass. Losing it
        // would leave that commit waiting for the backstop poll.
        OutboxWakeSignal signal = new();

        signal.Signal();

        Assert.True(await signal.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None));
    }

    [Fact]
    public async Task Many_signals_between_waits_coalesce_into_one_wake()
    {
        OutboxWakeSignal signal = new();

        for (int i = 0; i < 1000; i++)
        {
            signal.Signal();
        }

        Assert.True(await signal.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None));
        Assert.False(await signal.WaitAsync(TimeSpan.FromMilliseconds(20), CancellationToken.None));
    }

    [Fact]
    public async Task With_no_signal_the_wait_times_out_and_reports_it()
    {
        OutboxWakeSignal signal = new();
        Stopwatch elapsed = Stopwatch.StartNew();

        bool woken = await signal.WaitAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None);

        Assert.False(woken);
        Assert.True(elapsed.Elapsed >= TimeSpan.FromMilliseconds(40));
    }

    [Fact]
    public async Task Cancellation_ends_the_wait()
    {
        OutboxWakeSignal signal = new();
        using CancellationTokenSource cancel = new();

        Task<bool> waiting = signal.WaitAsync(TimeSpan.FromMinutes(1), cancel.Token);
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
    }
}
