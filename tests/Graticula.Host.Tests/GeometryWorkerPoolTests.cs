using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Geometries;
using Graticula.Host;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// The bound that makes general overlay safe to offer at all.
/// </summary>
/// <remarks>
/// <para>
/// <b>Q-97, and these tests are the claim.</b>
/// <see href="../../benchmarks/geometry-overlay/RESULTS.md">Measurement</see>
/// invalidated A-042: a 6,408-vertex adversarial input cost 153 seconds and
/// 16.7 GB where a real 72,919-vertex national outline cost 312 ms and 17 MB.
/// The run that produced that figure took the machine into swap and killed the
/// Docker daemon. One unauthenticated request would have done it.
/// </para>
/// <para>
/// <b>They run the real worker process</b>, because the whole answer is that
/// the work happens somewhere killable. A fake would test the bookkeeping and
/// skip the bound.
/// </para>
/// </remarks>
public sealed class GeometryWorkerPoolTests
{
    private static string Executable => GeometryWorkerPool.ExecutableBesideThisOne();

    /// <summary>
    /// Fails loudly when the worker is missing, rather than skipping.
    /// </summary>
    /// <remarks>
    /// <b>The fifth time this project has needed this shape.</b> The worker is
    /// built by the host's own build and copied beside it, so its absence is a
    /// packaging regression — and a suite that goes green when the thing under
    /// test is not there reports that a safety bound holds when nothing is
    /// enforcing it.
    /// </remarks>
    private static void RequireWorker() =>
        Assert.True(
            File.Exists(Executable),
            $"The overlay worker is not at {Executable}. It is built by the host project and "
            + "copied into an 'overlay' directory beside it; if it is missing, that copy step is "
            + "broken and every overlay request will answer 503.");

    private static GeometryWorkerPool Pool(
        TimeSpan? deadline = null, long? maximumCandidatePairs = null) =>
        new(Executable, workers: 2, NullLoggerFactory.Instance, deadline, maximumCandidatePairs);

    /// <summary>
    /// A comb of <paramref name="teeth"/> teeth across a fixed span.
    /// </summary>
    /// <remarks>
    /// <b>The span is fixed and the teeth get narrower, which is the part that
    /// is easy to get wrong.</b> Widening the comb with its tooth count makes
    /// two combs at right angles stop overlapping, and the candidate count
    /// plateaus instead of growing quadratically — an adversarial input that
    /// quietly becomes a cheap one, and a test that passes for the wrong reason.
    /// </remarks>
    private static Polygon Comb(int teeth, bool horizontal, double span = 100)
    {
        double width = span / (2 * teeth);

        List<double> coordinates = [];

        void Add(double x, double y)
        {
            coordinates.Add(horizontal ? y : x);
            coordinates.Add(horizontal ? x : y);
        }

        for (int i = 0; i < teeth; i++)
        {
            double left = i * 2 * width;

            Add(left, 0);
            Add(left, span);
            Add(left + width, span);
            Add(left + width, 0);
        }

        Add(0, 0);

        return new Polygon(new LinearRing(XySequence.Wrap([.. coordinates])));
    }

    private static Polygon Square(double minX, double minY, double size) =>
        new(new LinearRing(XySequence.Wrap(
        [
            minX, minY,
            minX, minY + size,
            minX + size, minY + size,
            minX + size, minY,
            minX, minY,
        ])));

    [Fact]
    public async Task An_ordinary_overlay_is_computed()
    {
        RequireWorker();

        await using GeometryWorkerPool pool = Pool();

        EngineResult result = await pool.ComputeAsync(
            new EngineRequest(EngineOperation.Intersect,
            [Square(0, 0, 10)],
            [Square(5, 5, 10)],
            3857),
            CancellationToken.None);

        Assert.Equal(EngineRefusal.None, result.Refusal);
        Assert.Single(result.Geometries);
    }

    /// <summary>
    /// Every operation the worker declares answers with something.
    /// </summary>
    /// <remarks>
    /// <b>The theory is the list, so adding an operation without wiring it fails
    /// here.</b> It went from three cases to nine on 2026-08-15 when the owner
    /// removed the rule that kept six operations out — and the reason a test like
    /// this earns its place is the bug it caught while being written: the worker's
    /// operation switch ended in a discard pattern that computed a union, so an
    /// operation it did not recognise returned a plausible wrong shape instead of
    /// an error.
    /// </remarks>
    [Theory]
    [InlineData(EngineOperation.Intersect)]
    [InlineData(EngineOperation.Union)]
    [InlineData(EngineOperation.Difference)]
    [InlineData(EngineOperation.Cut)]
    [InlineData(EngineOperation.Buffer)]
    [InlineData(EngineOperation.Simplify)]
    public async Task Each_operation_produces_something(EngineOperation operation)
    {
        RequireWorker();

        await using GeometryWorkerPool pool = Pool();

        EngineResult result = await pool.ComputeAsync(
            new EngineRequest(operation, [Square(0, 0, 10)], [Square(5, 5, 10)], 3857)
            {
                Distance = 1,
            },
            CancellationToken.None);

        Assert.Equal(EngineRefusal.None, result.Refusal);
        Assert.NotEmpty(result.Geometries);
    }

    [Fact]
    public async Task An_unknown_operation_is_an_error_rather_than_a_union()
    {
        RequireWorker();

        await using GeometryWorkerPool pool = Pool();

        // Reaching the worker with a name it does not know needs the enum
        // bypassed, which is what a protocol mismatch between server and worker
        // would look like in production.
        EngineResult result = await pool.ComputeAsync(
            new EngineRequest((EngineOperation)999, [Square(0, 0, 10)], [], 3857),
            CancellationToken.None);

        Assert.Equal(EngineRefusal.Invalid, result.Refusal);
        Assert.Empty(result.Geometries);
    }

    [Fact]
    public async Task Distance_answers_with_a_number_and_no_geometry()
    {
        RequireWorker();

        await using GeometryWorkerPool pool = Pool();

        EngineResult result = await pool.ComputeAsync(
            new EngineRequest(
                EngineOperation.Distance, [Square(0, 0, 10)], [Square(30, 0, 10)], 3857),
            CancellationToken.None);

        Assert.Equal(EngineRefusal.None, result.Refusal);
        Assert.Empty(result.Geometries);
        Assert.Equal(20, result.Scalar!.Value, 9);
    }

    /// <summary>
    /// relation compares every left against every right, in one round trip.
    /// </summary>
    /// <remarks>
    /// <b>Three by two is six comparisons and one process call.</b> Doing it a
    /// pair at a time would have been six, and for the sizes a real client sends
    /// — two sets of thirty — nine hundred.
    /// </remarks>
    [Fact]
    public async Task Relate_returns_the_pairs_that_match_and_only_those()
    {
        RequireWorker();

        await using GeometryWorkerPool pool = Pool();

        EngineResult result = await pool.ComputeAsync(
            new EngineRequest(
                EngineOperation.Relate,
                [Square(0, 0, 10), Square(100, 100, 10), Square(2, 2, 10)],
                [Square(1, 1, 2), Square(500, 500, 2)],
                3857)
            {
                // Intersects, as DE-9IM: the interiors meet.
                Pattern = "T********",
            },
            CancellationToken.None);

        Assert.Equal(EngineRefusal.None, result.Refusal);

        // Square(1,1,2) spans x and y in [1,3]. The first square covers it and
        // so does the third; the middle one is a hundred units away, and nothing
        // at all meets the far right-hand square.
        Assert.Equal(
            [[0, 0], [2, 0]],
            result.Pairs!.Select(pair => new[] { pair[0], pair[1] }).ToArray());
    }

    /// <summary>
    /// The pre-flight, when an operator asks for one.
    /// </summary>
    /// <remarks>
    /// <b>It is off by default since 2026-08-15</b> — the owner's rule is that
    /// the server bounds cost and does not decide on the caller's behalf what is
    /// worth attempting, and a filter measured under-predicting by fourteen times
    /// is a poor thing to refuse real work with. It remains a knob, so this test
    /// asks for it. What it proves is that the knob works, not that it is on.
    /// </remarks>
    [Fact]
    public async Task The_pre_flight_refuses_the_adversarial_comb_before_any_arithmetic()
    {
        RequireWorker();

        await using GeometryWorkerPool pool = Pool(
            maximumCandidatePairs: GeometryWorkerPool.PreflightAbove);

        Stopwatch clock = Stopwatch.StartNew();

        EngineResult result = await pool.ComputeAsync(
            new EngineRequest(EngineOperation.Intersect,
            [Comb(200, horizontal: false)],
            [Comb(200, horizontal: true)],
            3857),
            CancellationToken.None);

        Assert.Equal(EngineRefusal.TooLarge, result.Refusal);

        Assert.True(
            result.CandidatePairs > GeometryWorkerPool.PreflightAbove,
            $"The pre-flight counted {result.CandidatePairs:N0} pairs, which is not over the "
            + $"{GeometryWorkerPool.PreflightAbove:N0} limit — so this input is no longer "
            + "the adversarial case this test exists for.");

        // The point of a pre-flight is that it is cheap. An R-tree build and
        // query on 801 vertices should be milliseconds; if this ever takes
        // seconds, the estimate has become as expensive as the thing it avoids.
        Assert.True(
            clock.ElapsedMilliseconds < 5_000,
            $"The pre-flight took {clock.ElapsedMilliseconds} ms.");
    }

    /// <summary>
    /// An overlay that outlives its deadline is killed, and the answer says so.
    /// </summary>
    /// <remarks>
    /// <b>This is the only bound.</b> The pre-flight is a filter with a measured
    /// leak — finding 16 caught it under-predicting fourteenfold — and the
    /// memory ceiling limits damage rather than duration. Killing the process is
    /// what stops the work.
    /// </remarks>
    [Fact]
    public async Task An_overlay_that_runs_too_long_is_killed()
    {
        RequireWorker();

        // A short deadline and a high pre-flight limit, so the input gets past
        // the filter and has to be stopped by the deadline. The alternative —
        // an input that genuinely outlives ten seconds — also allocates
        // gigabytes, and a unit test should not need to.
        await using GeometryWorkerPool pool = Pool(
            deadline: TimeSpan.FromMilliseconds(150),
            maximumCandidatePairs: long.MaxValue);

        EngineResult result = await pool.ComputeAsync(
            new EngineRequest(EngineOperation.Intersect,
            [Comb(300, horizontal: false)],
            [Comb(300, horizontal: true)],
            3857),
            CancellationToken.None);

        Assert.Equal(EngineRefusal.Deadline, result.Refusal);
        Assert.Contains("was stopped", result.Message!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The pool serves the next caller after one of its workers was killed.
    /// </summary>
    /// <remarks>
    /// <b>A whole second, and the reason is that one deadline governs two
    /// requests here.</b> The test needs the first request killed and the second
    /// to succeed, and the pool applies the same deadline to both — so a bound
    /// tight enough to make the comb fail quickly is also the bound the trivial
    /// two-square intersection has to beat. At 150 ms it lost, intermittently
    /// and only when the whole solution was running: eight test assemblies, a
    /// PostGIS container and a server, all on one machine. **Found by a failure
    /// that passed in isolation and twice more when its own assembly ran
    /// alone**, which is the signature of contention rather than of a defect.
    /// A second still kills the comb — the smallest adversarial input that
    /// matters was measured at seventeen seconds — and gives the recovery
    /// request two hundred times its warm cost.
    /// </remarks>
    [Fact]
    public async Task The_pool_still_works_after_a_worker_has_been_killed()
    {
        // A killed worker must not be handed to the next caller: there is no
        // knowing what it was in the middle of. If the pool returned it, this
        // second request would read the first one's abandoned response.
        RequireWorker();

        await using GeometryWorkerPool pool = Pool(
            deadline: TimeSpan.FromSeconds(1),
            maximumCandidatePairs: long.MaxValue);

        EngineResult killed = await pool.ComputeAsync(
            new EngineRequest(EngineOperation.Intersect,
            [Comb(300, horizontal: false)],
            [Comb(300, horizontal: true)],
            3857),
            CancellationToken.None);

        Assert.Equal(EngineRefusal.Deadline, killed.Refusal);

        EngineResult after = await pool.ComputeAsync(
            new EngineRequest(EngineOperation.Intersect,
            [Square(0, 0, 10)],
            [Square(5, 5, 10)],
            3857),
            CancellationToken.None);

        Assert.Equal(EngineRefusal.None, after.Refusal);
        Assert.Single(after.Geometries);
    }

    [Fact]
    public async Task A_worker_is_reused_rather_than_launched_per_request()
    {
        // A process launch is tens of milliseconds and these operations are
        // milliseconds, so spawning per request would make the common case
        // slower than the work it does.
        RequireWorker();

        await using GeometryWorkerPool pool = Pool();

        for (int i = 0; i < 5; i++)
        {
            EngineResult result = await pool.ComputeAsync(
                new EngineRequest(EngineOperation.Intersect,
                [Square(0, 0, 10)],
                [Square(5, 5, 10)],
                3857),
                CancellationToken.None);

            Assert.Equal(EngineRefusal.None, result.Refusal);
        }

        // <b>This pool's own count, not the machine's.</b> It used to count
        // processes named Graticula.Overlay.Worker across the whole machine,
        // which also counts the pools other test classes are running in
        // parallel and any server the developer has up. It failed
        // intermittently and was written off as flaky twice before anybody
        // noticed it was not measuring the claim in its own name.
        Assert.Equal(1, pool.WorkersStarted);
    }

    [Fact]
    public async Task A_missing_worker_is_reported_rather_than_thrown()
    {
        // A packaging mistake must not surface as an unhandled exception on the
        // first request somebody makes.
        await using GeometryWorkerPool pool = new(
            Path.Combine(Path.GetTempPath(), "no-such-overlay-worker.exe"),
            workers: 1,
            NullLoggerFactory.Instance);

        EngineResult result = await pool.ComputeAsync(
            new EngineRequest(EngineOperation.Intersect,
            [Square(0, 0, 10)],
            [Square(5, 5, 10)],
            3857),
            CancellationToken.None);

        Assert.Equal(EngineRefusal.Unavailable, result.Refusal);
        Assert.Contains("not installed", result.Message!, StringComparison.Ordinal);
    }
}
