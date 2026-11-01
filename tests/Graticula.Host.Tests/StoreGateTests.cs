using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Host;
using Microsoft.Extensions.Time.Testing;
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
/// <para>
/// <b>And the depth is a standing queue's, not a burst's — 2026-11-01.</b> Several HTTP/2 map viewers panning at once
/// put more than 96 requests in front of the store for the few tens of milliseconds it takes to clear them, and were
/// refused a third of their tiles for it. The burst tests below fail on the gate that compared the count alone.
/// </para>
/// </remarks>
public sealed class StoreGateTests
{
    private static readonly TimeSpan Brief = TimeSpan.FromMilliseconds(300);

    [Fact]
    public async Task An_arrival_past_the_depth_of_a_standing_queue_is_refused_at_once()
    {
        FakeTimeProvider clock = new();
        using StoreGate gate = new(permits: 1, waitersPerPermit: 1, wait: TimeSpan.FromSeconds(30), time: clock);
        using IDisposable holding = await StandAsync(gate, clock);
        Assert.True(gate.Standing);

        // One waits, which is the depth; the next is told now, not in 30 s.
        using CancellationTokenSource stop = new();
        Task<IDisposable> queued = gate.EnterAsync(stop.Token).AsTask();
        await WaitUntilAsync(() => gate.Waiting == 1);

        Stopwatch elapsed = Stopwatch.StartNew();
        ConnectionBudgetFullException refused = await Assert.ThrowsAsync<ConnectionBudgetFullException>(
            async () => await gate.EnterAsync(CancellationToken.None));
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5), $"refused after {elapsed.Elapsed}");
        Assert.Contains("platform store", refused.Message, StringComparison.Ordinal);
        Assert.Contains("stopped draining", refused.Message, StringComparison.Ordinal);
        Assert.Equal(1, gate.Refused);
        Assert.Equal(1, gate.Waiting);

        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        Assert.Equal(0, gate.Waiting);
    }

    /// <summary>
    /// A burst into a queue that is draining is queued past the depth and served whole — the case that refused a third
    /// of three map viewers' tiles. Fails on the gate that refused at the depth whatever the queue was doing.
    /// </summary>
    [Fact]
    public async Task A_burst_into_a_draining_queue_is_queued_past_the_depth()
    {
        FakeTimeProvider clock = new();
        using StoreGate gate = new(permits: 2, waitersPerPermit: 1, wait: TimeSpan.FromSeconds(30), time: clock);
        Assert.Equal(2, gate.QueueDepth);
        Assert.Equal(32, gate.QueueCeiling);

        IDisposable a = await gate.EnterAsync(CancellationToken.None);
        IDisposable b = await gate.EnterAsync(CancellationToken.None);

        // Twenty at once, ten times the depth, into a gate that has never had a request wait.
        Task<IDisposable>[] burst = [.. Enumerable.Range(0, 20).Select(_ => gate.EnterAsync(CancellationToken.None).AsTask())];
        await WaitUntilAsync(() => gate.Waiting == 20);
        Assert.Equal(0, gate.Refused);
        Assert.False(gate.Standing);

        // And it drains: every one of them gets a permit.
        a.Dispose();
        b.Dispose();

        foreach (Task<IDisposable> next in burst)
        {
            (await next).Dispose();
        }

        Assert.Equal(0, gate.Refused);
        Assert.Equal(0, gate.Inside);
    }

    /// <summary>
    /// The ceiling holds whatever the queue is doing, so a flood's first interval cannot queue without limit.
    /// </summary>
    [Fact]
    public async Task An_arrival_past_the_ceiling_is_refused_even_when_the_queue_is_draining()
    {
        using StoreGate gate = new(permits: 1, waitersPerPermit: 1, wait: TimeSpan.FromSeconds(30), time: new FakeTimeProvider());
        using IDisposable held = await gate.EnterAsync(CancellationToken.None);
        using CancellationTokenSource stop = new();

        Task<IDisposable>[] queued = [.. Enumerable.Range(0, gate.QueueCeiling).Select(_ => gate.EnterAsync(stop.Token).AsTask())];
        await WaitUntilAsync(() => gate.Waiting == gate.QueueCeiling);
        Assert.False(gate.Standing);

        ConnectionBudgetFullException refused = await Assert.ThrowsAsync<ConnectionBudgetFullException>(
            async () => await gate.EnterAsync(CancellationToken.None));
        Assert.Contains($"{gate.QueueCeiling} it will queue at most", refused.Message, StringComparison.Ordinal);

        await stop.CancelAsync();

        foreach (Task<IDisposable> next in queued)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => next);
        }
    }

    /// <summary>
    /// A standing queue that drains stops being one: a grant with nobody left waiting clears the mark, and a burst is
    /// queued again.
    /// </summary>
    [Fact]
    public async Task A_queue_that_drains_stops_standing()
    {
        FakeTimeProvider clock = new();
        using StoreGate gate = new(permits: 1, waitersPerPermit: 1, wait: TimeSpan.FromSeconds(30), time: clock);
        IDisposable holding = await StandAsync(gate, clock);
        Assert.True(gate.Standing);

        // A request that finds a permit free, with nobody waiting, waited nothing: the queue drained.
        holding.Dispose();
        IDisposable quick = await gate.EnterAsync(CancellationToken.None);
        Assert.False(gate.Standing);

        await AssertABurstIsQueuedAsync(gate, quick);
    }

    /// <summary>
    /// A mark no long grant has renewed for an interval is a burst that ended, not a queue: the next burst is queued
    /// past the depth even though nothing cleared the mark. Fails without the recency half of the standing test.
    /// </summary>
    [Fact]
    public async Task A_mark_no_grant_renews_ages_out()
    {
        FakeTimeProvider clock = new();
        using StoreGate gate = new(permits: 1, waitersPerPermit: 1, wait: TimeSpan.FromSeconds(30), time: clock);
        IDisposable holding = await StandAsync(gate, clock);
        Assert.True(gate.Standing);

        clock.Advance(StoreGate.Interval);
        Assert.False(gate.Standing);

        await AssertABurstIsQueuedAsync(gate, holding);
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
    /// Under a burst of arrivals the waiting count never reads above the bound it is compared with — the flood of
    /// 2026-11-01 read 101 against 96 while it was counted before the comparison. A queue that never drains is
    /// not standing either (nothing was granted), so the bound here is the ceiling.
    /// </summary>
    [Fact]
    public async Task The_waiting_count_never_passes_the_ceiling_under_a_burst()
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

        await WaitUntilAsync(() => gate.Refused >= arrivals.Length - gate.QueueCeiling);
        Volatile.Write(ref sampling, false);
        await sampler;
        await stop.CancelAsync();
        await Task.WhenAll(arrivals);

        Assert.Equal(arrivals.Length - gate.QueueCeiling, gate.Refused);
        Assert.True(most <= gate.QueueCeiling, $"read {most} waiting against a ceiling of {gate.QueueCeiling}");
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

    /// <summary>
    /// Puts <paramref name="gate"/> (one permit) into a standing queue: three requests queued at once and granted one
    /// after another, every one after more than the target, over more than an interval. Returns the last one's permit.
    /// </summary>
    private static async Task<IDisposable> StandAsync(StoreGate gate, FakeTimeProvider clock)
    {
        TimeSpan step = StoreGate.Target + TimeSpan.FromMilliseconds(10);
        IDisposable held = await gate.EnterAsync(CancellationToken.None);
        Task<IDisposable>[] queued = [.. Enumerable.Range(0, 3).Select(_ => gate.EnterAsync(CancellationToken.None).AsTask())];
        await WaitUntilAsync(() => gate.Waiting == 3);

        for (int i = 0; i < queued.Length; i++)
        {
            clock.Advance(step);
            held.Dispose();
            // SemaphoreSlim grants its waiters in the order they came.
            held = await queued[i];
        }

        return held;
    }

    /// <summary>Five arrivals at once past a depth of one are queued, not refused, and all get a permit.</summary>
    private static async Task AssertABurstIsQueuedAsync(StoreGate gate, IDisposable holding)
    {
        long refusedBefore = gate.Refused;
        Task<IDisposable>[] burst = [.. Enumerable.Range(0, 5).Select(_ => gate.EnterAsync(CancellationToken.None).AsTask())];
        await WaitUntilAsync(() => gate.Waiting == 5);
        Assert.Equal(refusedBefore, gate.Refused);

        holding.Dispose();

        foreach (Task<IDisposable> next in burst)
        {
            (await next).Dispose();
        }

        Assert.Equal(refusedBefore, gate.Refused);
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
