using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Geometries;
using Graticula.Host;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// A request's own deadline and pre-flight threshold beat the pool's.
/// </summary>
/// <remarks>
/// <para>
/// <b>These exist because the owner asked for a settable timeout and the first implementation
/// half-delivered it.</b> Their question, on being told the deadline was a configuration-file
/// value: *"iyi de neden yok. yani ben neden max timeout süresi tanımlayamıyorum?"* — why can I
/// not define a maximum timeout? The answer given was that changing it live would mean rebuilding
/// the worker pool. That was wrong: the pool applies both bounds per operation.
/// </para>
/// <para>
/// <b>And then the pre-flight was stored, reported, and ignored.</b> The first version put only
/// the deadline on the request and left the pre-flight reading the pool's field, so
/// <c>PUT /admin/services/Geometry/limits</c> saved a threshold of 100,000, the service document
/// advertised it, and a request measuring 130,324 candidate pairs was computed anyway — found by
/// measuring against a running server rather than by reading the diff. A setting that is stored
/// and reported and does nothing is worse than one that does not exist, because a deployment
/// believes it is protected. The pre-flight test below is the one that would have caught it, and
/// it is deliberately the cheap one: the pre-flight runs before any arithmetic, so it needs no
/// slow input.
/// </para>
/// <para>
/// <b>These do not need PostGIS</b> — they are about the pool's own bookkeeping, not about whether
/// an answer is right, which is <c>WorkerAgainstPostgisTests</c>'s job. They do need the worker
/// executable, and they fail rather than skip without it, following this repository's rule.
/// </para>
/// </remarks>
public sealed class RequestBoundsTests : IAsyncLifetime, IAsyncDisposable
{
    private GeometryWorkerPool? _pool;

    /// <summary>Builds a pool with bounds a request should be able to override.</summary>
    /// <returns>Nothing.</returns>
    /// <remarks>
    /// <b>A minute and no pre-flight, both deliberately unlike what the tests ask for.</b> If the
    /// pool's own values were the ones under test, a bug that ignored the request would pass.
    /// </remarks>
    public Task InitializeAsync()
    {
        _pool = new GeometryWorkerPool(
            GeometryWorkerPool.ExecutableBesideThisOne(),
            workers: 2,
            NullLoggerFactory.Instance,
            deadline: TimeSpan.FromMinutes(1),
            maximumCandidatePairs: 0);

        return Task.CompletedTask;
    }

    /// <summary>Disposes the pool.</summary>
    /// <returns>Nothing.</returns>
    public async Task DisposeAsync()
    {
        if (_pool is not null)
        {
            await _pool.DisposeAsync();
        }
    }

    /// <summary>The same teardown under the interface the analyzer looks for.</summary>
    /// <returns>Nothing.</returns>
    ValueTask IAsyncDisposable.DisposeAsync() => new(DisposeAsync());

    private GeometryWorkerPool Pool()
    {
        Assert.True(
            _pool is { Available: true },
            "The geometry worker is not built beside these tests, so these FAIL rather than skip. "
            + "A test that goes green with its subject absent is worse than no test.");

        return _pool!;
    }

    /// <summary>
    /// Two combs at right angles — the shape whose cost is set by crossings rather than size.
    /// </summary>
    /// <remarks>
    /// The same construction <c>benchmarks/geometry-overlay</c> uses, because the point of it is
    /// that a small input can be expensive: at 200 teeth this measures 130,324 candidate segment
    /// pairs and a few seconds of work from about 800 vertices.
    /// </remarks>
    private static Polygon Comb(int teeth, bool flip)
    {
        List<double> ring = [];
        double step = 1.0 / (2 * teeth);

        void Add(double x, double y)
        {
            ring.Add(flip ? y : x);
            ring.Add(flip ? x : y);
        }

        for (int i = 0; i < 2 * teeth; i++)
        {
            Add(i * step, i % 2 == 0 ? 0.0 : 0.9);
        }

        Add(1.0, 0.0);
        Add(1.0, -0.1);
        Add(0.0, -0.1);

        // Closed, because a ring that does not close is a different error from the one under test.
        ring.Add(ring[0]);
        ring.Add(ring[1]);

        return new Polygon(new LinearRing(XySequence.Wrap([.. ring])));
    }

    private static EngineRequest Intersect(int teeth) =>
        new(EngineOperation.Intersect, [Comb(teeth, false)], [Comb(teeth, true)], 4326);

    /// <summary>
    /// A request's pre-flight threshold is applied even when the pool has none.
    /// </summary>
    /// <remarks>
    /// <b>This is the regression, and it is cheap because the pre-flight runs first.</b> The pool
    /// is built with no pre-flight at all; the request asks for one; the refusal has to name the
    /// request's number, because naming the pool's would mean the request's was thrown away.
    /// </remarks>
    [Fact]
    public async Task A_requests_preflight_threshold_is_the_one_applied()
    {
        EngineResult refused = await Pool().ComputeAsync(
            Intersect(200) with { PreflightPairs = 100_000 },
            CancellationToken.None);

        Assert.Equal(EngineRefusal.TooLarge, refused.Refusal);

        Assert.Contains(
            "100,000",
            refused.Message,
            StringComparison.Ordinal);

        // And a threshold above the measured cost lets the same work through, so the refusal above
        // is the threshold acting rather than the input being rejected for some other reason.
        EngineResult allowed = await Pool().ComputeAsync(
            Intersect(200) with { PreflightPairs = 1_000_000 },
            CancellationToken.None);

        Assert.Equal(EngineRefusal.None, allowed.Refusal);
    }

    /// <summary>
    /// A request's deadline is applied even when the pool's is far longer.
    /// </summary>
    /// <remarks>
    /// <b>The pool's deadline is a minute and this work takes seconds</b>, so the only way this
    /// can be stopped is by the request's own bound. The message has to name the request's number
    /// too: an operator who set two seconds and is told *ran longer than 60 seconds* has been told
    /// something false about their own configuration.
    /// </remarks>
    [Fact]
    public async Task A_requests_deadline_is_the_one_enforced()
    {
        EngineResult result = await Pool().ComputeAsync(
            Intersect(200) with { Deadline = TimeSpan.FromSeconds(1) },
            CancellationToken.None);

        Assert.Equal(EngineRefusal.Deadline, result.Refusal);

        Assert.Contains(
            "1 second",
            result.Message,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A request that names neither bound gets the pool's, which is how every caller behaved
    /// before this existed.
    /// </summary>
    /// <remarks>
    /// Asserted so that adding the two fields cannot have changed the default path. The pool here
    /// has a minute and no pre-flight, so the work completes — under the old constants it would
    /// have been refused at ten seconds.
    /// </remarks>
    [Fact]
    public async Task Naming_no_bound_leaves_the_pools_own_in_force()
    {
        EngineResult result = await Pool().ComputeAsync(Intersect(200), CancellationToken.None);

        Assert.Equal(EngineRefusal.None, result.Refusal);

        Assert.True(
            result.Milliseconds > 0,
            "The pool reported no elapsed time, so this did not measure the work it claims to.");
    }

    /// <summary>
    /// A request's wait budget is applied, and it is not the work's deadline.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The split the reference's Pooling page makes and this pool did not.</b> The owner sent
    /// ArcGIS Server Manager's page for its own geometry service — *the maximum time a client can
    /// use a service* at 600 seconds beside *the maximum time a client will wait to get a service*
    /// at 60 — and asked whether these were good examples. This one was: the wait was bounded by
    /// the work's deadline, so a request queued behind somebody else's long overlay held a
    /// connection for as long as the work was allowed to take.
    /// </para>
    /// <para>
    /// <b>One worker and two requests, which is the only arrangement that tests a queue.</b> The
    /// second cannot get a slot until the first finishes, so it must come back on its own budget
    /// rather than on the deadline — asserted by the clock, because the two numbers are far enough
    /// apart that timing distinguishes them without being flaky.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_requests_wait_budget_is_not_the_work_deadline()
    {
        await using GeometryWorkerPool pool = new(
            GeometryWorkerPool.ExecutableBesideThisOne(),
            workers: 1,
            NullLoggerFactory.Instance,
            deadline: TimeSpan.FromMinutes(1),
            maximumCandidatePairs: 0,
            wait: TimeSpan.FromMinutes(1));

        Assert.True(pool.Available, "The geometry worker is not built beside these tests.");

        // Long enough that the queued request is certainly still waiting: the 200-tooth pair
        // measures a few seconds.
        Task<EngineResult> busy = pool.ComputeAsync(Intersect(200), CancellationToken.None);

        Stopwatch clock = Stopwatch.StartNew();

        EngineResult queued = await pool.ComputeAsync(
            Intersect(200) with { Wait = TimeSpan.FromMilliseconds(200) },
            CancellationToken.None);

        clock.Stop();

        Assert.Equal(EngineRefusal.Unavailable, queued.Refusal);

        Assert.True(
            clock.Elapsed < TimeSpan.FromSeconds(3),
            $"The queued request came back after {clock.Elapsed.TotalSeconds:0.0} s. Its wait "
            + "budget was 200 ms and the pool's deadline is a minute, so anything near the work's "
            + "duration means the wait is still bounded by the deadline.");

        await busy;
    }

    /// <summary>
    /// An idle worker is reclaimed, and the pool still answers afterwards.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Added because the pool kept every worker it had ever started.</b> A returned worker went
    /// into a bag and came out again for ever, so a deployment that ran one overlay in the morning
    /// held its worker processes — each able to have grown to a 1 GB heap — until it was restarted.
    /// ArcGIS Server Manager's *maximum time an idle instance can be kept running* is the control
    /// that was missing.
    /// </para>
    /// <para>
    /// <b>The second half is the half worth asserting.</b> Reclaiming is easy; reclaiming without
    /// breaking the next request is the property, and a pool that disposed a worker it had left in
    /// the bag would pass a test that only counted reclaims.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task An_idle_worker_is_reclaimed_and_the_next_request_still_works()
    {
        await using GeometryWorkerPool pool = new(
            GeometryWorkerPool.ExecutableBesideThisOne(),
            workers: 1,
            NullLoggerFactory.Instance,
            idle: TimeSpan.FromMilliseconds(1));

        Assert.True(pool.Available, "The geometry worker is not built beside these tests.");

        Assert.Equal(
            EngineRefusal.None,
            (await pool.ComputeAsync(Intersect(4), CancellationToken.None)).Refusal);

        // The reaper's own period is thirty seconds, which is too long for a test, so this drives
        // the sweep directly — the same method the timer calls.
        pool.ReapForTest();

        Assert.Equal(
            EngineRefusal.None,
            (await pool.ComputeAsync(Intersect(4), CancellationToken.None)).Refusal);
    }

    /// <summary>An idle budget of zero keeps workers, which is what this pool did before.</summary>
    /// <remarks>
    /// Zero is a value here rather than an absence, and it is the one an operator picks who would
    /// rather pay memory than a 650 ms cold start. Asserted so that *never reclaim* cannot quietly
    /// become *reclaim immediately*.
    /// </remarks>
    [Fact]
    public async Task An_idle_budget_of_zero_keeps_the_worker()
    {
        await using GeometryWorkerPool pool = new(
            GeometryWorkerPool.ExecutableBesideThisOne(),
            workers: 1,
            NullLoggerFactory.Instance,
            idle: TimeSpan.Zero);

        Assert.True(pool.Available, "The geometry worker is not built beside these tests.");

        await pool.ComputeAsync(Intersect(4), CancellationToken.None);

        Assert.Equal(0, pool.ReapForTest());
    }
}
