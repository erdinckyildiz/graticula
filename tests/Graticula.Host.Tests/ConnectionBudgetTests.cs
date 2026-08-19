using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Host;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// The bound ADR-007 §4.8 has required since 2026-08-12.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every claim here is about a refusal or a queue, which is the half that is never exercised by
/// ordinary use.</b> A budget that admits everything passes any test that only checks the happy path,
/// and that is exactly the state the server was in before this existed — `(data sources + 1) × 100`
/// potential connections per worker with nothing enforcing it
/// ([Q-04](../../docs/open-questions.md)).
/// </para>
/// <para>
/// <b>The per-source gate is entered before the global one, and one test is about that order.</b>
/// Taking the global slot first would let one saturated data source fill the worker's whole budget
/// while requests for other sources wait behind it for slots they are not competing for — which is the
/// blast-radius problem §4.8's N4 describes, and it would pass a test that only counted totals.
/// </para>
/// </remarks>
public sealed class ConnectionBudgetTests
{
    private static readonly TimeSpan Brief = TimeSpan.FromMilliseconds(150);

    [Fact]
    public async Task Requests_up_to_the_bound_are_admitted()
    {
        using ConnectionBudget budget = new(worker: 4, perSource: 4, Brief);

        List<ConnectionBudget.Lease> held = [];

        try
        {
            for (int i = 0; i < 4; i++)
            {
                held.Add(await budget.EnterAsync("one", CancellationToken.None));
            }
        }
        finally
        {
            foreach (ConnectionBudget.Lease lease in held)
            {
                lease.Dispose();
            }
        }

        Assert.Equal(4, held.Count);
    }

    /// <summary>
    /// The one past the bound waits, and is then refused with a sentence naming the setting.
    /// </summary>
    /// <remarks>
    /// <b>Refused rather than queued for ever</b> — ADR-007 §4.9: admission control rejects with a
    /// retry signal rather than accepting work it cannot do. An unbounded queue is how a slow database
    /// becomes a server that answers nothing at all, ten minutes later, having accumulated every
    /// request that arrived in between.
    /// </remarks>
    [Fact]
    public async Task The_request_past_the_bound_is_refused_with_a_reason()
    {
        using ConnectionBudget budget = new(worker: 8, perSource: 2, Brief);

        using ConnectionBudget.Lease first = await budget.EnterAsync("one", CancellationToken.None);
        using ConnectionBudget.Lease second = await budget.EnterAsync("one", CancellationToken.None);

        ConnectionBudgetFullException refused =
            await Assert.ThrowsAsync<ConnectionBudgetFullException>(
                async () => await budget.EnterAsync("one", CancellationToken.None));

        // It says whose limit it is and which setting raises it, because a 503 that says only *at
        // capacity* sends an operator to look at the database.
        Assert.Contains("data source", refused.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PerSourceConcurrency", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A slot given back is a slot the next request gets.
    /// </summary>
    /// <remarks>
    /// <b>The failure this guards is a leak, and it would look like a working server for an hour.</b>
    /// A lease that is not returned — a `finally` missing around a streaming read — reduces the bound
    /// by one per request until the server refuses everything, and nothing about the symptom points at
    /// the budget.
    /// </remarks>
    [Fact]
    public async Task Releasing_a_slot_admits_the_next_request()
    {
        using ConnectionBudget budget = new(worker: 8, perSource: 1, Brief);

        using (await budget.EnterAsync("one", CancellationToken.None))
        {
            await Assert.ThrowsAsync<ConnectionBudgetFullException>(
                async () => await budget.EnterAsync("one", CancellationToken.None));
        }

        using ConnectionBudget.Lease after = await budget.EnterAsync("one", CancellationToken.None);
    }

    /// <summary>
    /// One saturated data source does not block another.
    /// </summary>
    /// <remarks>
    /// <b>This is what the per-source limit is for</b>, and the only test here that fails if the two
    /// gates are entered in the wrong order: with the global gate first, filling source `one` to the
    /// worker's bound leaves nothing for source `two` even though `two` has its own quota.
    /// </remarks>
    [Fact]
    public async Task A_saturated_source_does_not_block_another()
    {
        using ConnectionBudget budget = new(worker: 8, perSource: 2, Brief);

        using ConnectionBudget.Lease a = await budget.EnterAsync("one", CancellationToken.None);
        using ConnectionBudget.Lease b = await budget.EnterAsync("one", CancellationToken.None);

        await Assert.ThrowsAsync<ConnectionBudgetFullException>(
            async () => await budget.EnterAsync("one", CancellationToken.None));

        // The other source has its own quota and is unaffected.
        using ConnectionBudget.Lease elsewhere =
            await budget.EnterAsync("two", CancellationToken.None);
    }

    /// <summary>
    /// The worker's own bound holds across sources.
    /// </summary>
    /// <remarks>
    /// <b>Which is the half a per-source limit cannot do.</b> Twenty data sources each within their own
    /// quota is what produced Q-04's 700 potential connections; the worker bound is what makes many
    /// databases degrade by queueing instead of exhausting one of them.
    /// </remarks>
    [Fact]
    public async Task The_worker_bound_holds_across_sources()
    {
        using ConnectionBudget budget = new(worker: 2, perSource: 4, Brief);

        using ConnectionBudget.Lease a = await budget.EnterAsync("one", CancellationToken.None);
        using ConnectionBudget.Lease b = await budget.EnterAsync("two", CancellationToken.None);

        ConnectionBudgetFullException refused =
            await Assert.ThrowsAsync<ConnectionBudgetFullException>(
                async () => await budget.EnterAsync("three", CancellationToken.None));

        Assert.Contains("worker", refused.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ConnectionBudget", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Refusing on the global gate gives back the per-source slot it already took.
    /// </summary>
    /// <remarks>
    /// <b>The leak that only happens under load, which is when it matters.</b> The gates are entered in
    /// order, so a request can hold its source's slot and then fail to get the worker's. Without the
    /// unwind, every such refusal would permanently cost that source one slot — and the source most
    /// likely to hit the worker bound is the busiest one, so it would strangle itself first.
    /// </remarks>
    [Fact]
    public async Task A_refusal_on_the_worker_bound_returns_the_source_slot()
    {
        // <b>Both bounds are two, and the first version of this test got that wrong.</b> It gave the
        // worker one slot and the source four, then asserted four could be held afterwards — which the
        // worker bound forbids however many slots the source has. The bounds have to be equal for the
        // source's full quota to be observable at all.
        using ConnectionBudget budget = new(worker: 2, perSource: 2, Brief);

        using (await budget.EnterAsync("one", CancellationToken.None))
        using (await budget.EnterAsync("one", CancellationToken.None))
        {
            // The worker is full. This takes a slot on "two" and then cannot get the worker's.
            await Assert.ThrowsAsync<ConnectionBudgetFullException>(
                async () => await budget.EnterAsync("two", CancellationToken.None));
        }

        // If the unwind is missing, "two" is short one slot for ever and the second of these throws.
        List<ConnectionBudget.Lease> held = [];

        try
        {
            for (int i = 0; i < 2; i++)
            {
                held.Add(await budget.EnterAsync("two", CancellationToken.None));
            }
        }
        finally
        {
            foreach (ConnectionBudget.Lease lease in held)
            {
                lease.Dispose();
            }
        }

        Assert.Equal(2, held.Count);
    }

    /// <summary>
    /// Zero means unbounded, which is what a deployment that has measured its own database asks for.
    /// </summary>
    [Fact]
    public async Task Zero_is_no_bound_at_all()
    {
        using ConnectionBudget budget = new(worker: 0, perSource: 0, Brief);

        List<ConnectionBudget.Lease> held = [];

        try
        {
            for (int i = 0; i < 200; i++)
            {
                held.Add(await budget.EnterAsync("one", CancellationToken.None));
            }
        }
        finally
        {
            foreach (ConnectionBudget.Lease lease in held)
            {
                lease.Dispose();
            }
        }

        Assert.Equal(200, held.Count);
        Assert.Equal(0, budget.Worker);
        Assert.Equal(0, budget.PerSource);
    }

    /// <summary>
    /// The caller's own cancellation wins over the wait.
    /// </summary>
    /// <remarks>
    /// <b>Because a client that has gone away must not hold a slot for the whole wait.</b> A disconnect
    /// cancels the request token; if the budget ignored it, a burst of abandoned requests would each
    /// occupy the queue for its full five seconds after nobody was listening.
    /// </remarks>
    [Fact]
    public async Task A_cancelled_caller_stops_waiting_immediately()
    {
        using ConnectionBudget budget = new(worker: 8, perSource: 1, TimeSpan.FromSeconds(30));
        using CancellationTokenSource gone = new();

        using ConnectionBudget.Lease held = await budget.EnterAsync("one", CancellationToken.None);

        ValueTask<ConnectionBudget.Lease> waiting = budget.EnterAsync("one", gone.Token);

        await gone.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await waiting);
    }
}
