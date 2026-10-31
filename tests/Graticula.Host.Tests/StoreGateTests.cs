using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Host;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// ADR-046's queue bound at the platform store, where condition 1's re-runs found the wait (2026-10-31, 2026-11-01).
/// </summary>
/// <remarks>
/// <b>The two properties that make it the bound the ADR intended, and each fails without its half of the change.</b>
/// An arrival past the queue is refused at once rather than after a wait; and a request gives its permit back the
/// moment it asks for a data-source permit, so a request waiting for a slow database holds nothing every other service
/// needs (ADR-007's N4). Without <see cref="ConnectionBudget.EnterAsync"/> calling <see cref="StoreGate.Leave"/> the
/// second test fails; without the gate there is nothing to refuse.
/// </remarks>
public sealed class StoreGateTests
{
    private static readonly TimeSpan Brief = TimeSpan.FromMilliseconds(300);

    [Fact]
    public async Task An_arrival_past_the_queue_is_refused_at_once()
    {
        using StoreGate gate = new(permits: 2, waitersPerPermit: 1, wait: TimeSpan.FromSeconds(30));

        using IDisposable first = await gate.EnterAsync(CancellationToken.None);
        using IDisposable second = await gate.EnterAsync(CancellationToken.None);

        using CancellationTokenSource stop = new();
        Task<IDisposable> third = gate.EnterAsync(stop.Token).AsTask();
        Task<IDisposable> fourth = gate.EnterAsync(stop.Token).AsTask();
        await WaitUntilAsync(() => gate.Waiting == 2);

        // The queue is two deep (two permits, one waiter each) and full: the fifth is told now, not in 30 s.
        Stopwatch clock = Stopwatch.StartNew();
        ConnectionBudgetFullException refused = await Assert.ThrowsAsync<ConnectionBudgetFullException>(
            async () => await gate.EnterAsync(CancellationToken.None));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"refused after {clock.Elapsed}");
        Assert.Contains("platform store", refused.Message, StringComparison.Ordinal);
        Assert.Equal(1, gate.Refused);

        // And the count an operator reads never passes the depth it is compared with.
        Assert.Equal(2, gate.Waiting);
        Assert.True(gate.Waiting <= gate.QueueDepth);

        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => third);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fourth);
        Assert.Equal(0, gate.Waiting);
    }

    [Fact]
    public async Task A_request_gives_its_permit_back_when_it_asks_for_a_data_source()
    {
        using StoreGate gate = new(permits: 1, waitersPerPermit: 4, wait: Brief);
        using ConnectionBudget budget = new(worker: 8, perSource: 1, TimeSpan.FromSeconds(2));

        // A request holding the source's only permit, so the next one has to wait for it.
        using ConnectionBudget.Lease busy = await budget.EnterAsync("slow", CancellationToken.None);

        Task waitingForTheSource = Task.Run(async () =>
        {
            using IDisposable lease = await gate.EnterAsync(CancellationToken.None);
            StoreGate.Bind(lease);
            Assert.Equal(1, gate.Inside);

            // Refused by the source after its wait — and while it waited it held nothing at the store.
            await Assert.ThrowsAsync<ConnectionBudgetFullException>(
                async () => await budget.EnterAsync("slow", CancellationToken.None));
        });

        await WaitUntilAsync(() => budget.WaitingFor("slow") == 1);
        Assert.Equal(0, gate.Inside);

        // So another service's request gets the store's only permit at once.
        using (IDisposable other = await gate.EnterAsync(CancellationToken.None))
        {
            Assert.Equal(1, gate.Inside);
        }

        await waitingForTheSource;

        // The request's own end does not give the permit back a second time.
        Assert.Equal(0, gate.Inside);
    }

    [Fact]
    public async Task A_request_that_never_reaches_a_source_holds_its_permit_to_the_end()
    {
        using StoreGate gate = new(permits: 1, waitersPerPermit: 1, wait: Brief);

        IDisposable lease = await gate.EnterAsync(CancellationToken.None);
        StoreGate.Bind(lease);
        Assert.Equal(1, gate.Inside);
        Assert.True(StoreGate.Holding);

        lease.Dispose();
        lease.Dispose();

        Assert.Equal(0, gate.Inside);
        Assert.False(StoreGate.Holding);
    }

    /// <summary>
    /// Under a burst of arrivals the waiting count never reads above the depth — the flood of 2026-11-01 read 101
    /// against 96 while it was counted before the comparison.
    /// </summary>
    [Fact]
    public async Task The_waiting_count_never_passes_the_depth_under_a_burst()
    {
        using StoreGate gate = new(permits: 1, waitersPerPermit: 4, wait: TimeSpan.FromSeconds(30));
        using IDisposable held = await gate.EnterAsync(CancellationToken.None);
        using CancellationTokenSource stop = new();

        int most = 0;
        bool sampling = true;
        Task sampler = Task.Run(() =>
        {
            while (Volatile.Read(ref sampling))
            {
                int now = gate.Waiting;
                if (now > most)
                {
                    most = now;
                }
            }
        });

        Task[] arrivals = new Task[400];
        for (int i = 0; i < arrivals.Length; i++)
        {
            arrivals[i] = Task.Run(async () =>
            {
                try
                {
                    (await gate.EnterAsync(stop.Token)).Dispose();
                }
                catch (ConnectionBudgetFullException)
                {
                }
                catch (OperationCanceledException)
                {
                }
            });
        }

        await WaitUntilAsync(() => gate.Refused >= arrivals.Length - gate.QueueDepth);
        Volatile.Write(ref sampling, false);
        await sampler;
        await stop.CancelAsync();
        await Task.WhenAll(arrivals);

        Assert.Equal(arrivals.Length - gate.QueueDepth, gate.Refused);
        Assert.True(most <= gate.QueueDepth, $"read {most} waiting against a depth of {gate.QueueDepth}");
    }

    [Fact]
    public async Task Zero_permits_is_no_gate()
    {
        using StoreGate gate = new(permits: 0, waitersPerPermit: 4);

        for (int i = 0; i < 100; i++)
        {
            (await gate.EnterAsync(CancellationToken.None)).Dispose();
        }

        Assert.Equal(0, gate.Refused);
        Assert.Equal(0, gate.QueueDepth);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        Stopwatch clock = Stopwatch.StartNew();

        while (!condition())
        {
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), "the condition was never reached");
            await Task.Delay(10);
        }
    }
}
