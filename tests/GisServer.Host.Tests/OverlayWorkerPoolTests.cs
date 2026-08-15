using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using GisServer.Geometries;
using GisServer.Host;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GisServer.Host.Tests;

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
public sealed class OverlayWorkerPoolTests
{
    private static string Executable => OverlayWorkerPool.ExecutableBesideThisOne();

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

    private static OverlayWorkerPool Pool(
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

        await using OverlayWorkerPool pool = Pool();

        OverlayResult result = await pool.ComputeAsync(
            OverlayOperation.Intersect,
            [Square(0, 0, 10)],
            [Square(5, 5, 10)],
            3857,
            CancellationToken.None);

        Assert.Equal(OverlayRefusal.None, result.Refusal);
        Assert.Single(result.Geometries);
    }

    [Theory]
    [InlineData(OverlayOperation.Intersect)]
    [InlineData(OverlayOperation.Union)]
    [InlineData(OverlayOperation.Difference)]
    public async Task Each_operation_produces_something(OverlayOperation operation)
    {
        RequireWorker();

        await using OverlayWorkerPool pool = Pool();

        OverlayResult result = await pool.ComputeAsync(
            operation, [Square(0, 0, 10)], [Square(5, 5, 10)], 3857, CancellationToken.None);

        Assert.Equal(OverlayRefusal.None, result.Refusal);
        Assert.Single(result.Geometries);
    }

    [Fact]
    public async Task The_pre_flight_refuses_the_adversarial_comb_before_any_arithmetic()
    {
        RequireWorker();

        await using OverlayWorkerPool pool = Pool();

        Stopwatch clock = Stopwatch.StartNew();

        OverlayResult result = await pool.ComputeAsync(
            OverlayOperation.Intersect,
            [Comb(200, horizontal: false)],
            [Comb(200, horizontal: true)],
            3857,
            CancellationToken.None);

        Assert.Equal(OverlayRefusal.TooLarge, result.Refusal);

        Assert.True(
            result.CandidatePairs > OverlayWorkerPool.MaximumCandidatePairs,
            $"The pre-flight counted {result.CandidatePairs:N0} pairs, which is not over the "
            + $"{OverlayWorkerPool.MaximumCandidatePairs:N0} limit — so this input is no longer "
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
        await using OverlayWorkerPool pool = Pool(
            deadline: TimeSpan.FromMilliseconds(150),
            maximumCandidatePairs: long.MaxValue);

        OverlayResult result = await pool.ComputeAsync(
            OverlayOperation.Intersect,
            [Comb(300, horizontal: false)],
            [Comb(300, horizontal: true)],
            3857,
            CancellationToken.None);

        Assert.Equal(OverlayRefusal.Deadline, result.Refusal);
        Assert.Contains("was stopped", result.Message!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_pool_still_works_after_a_worker_has_been_killed()
    {
        // A killed worker must not be handed to the next caller: there is no
        // knowing what it was in the middle of. If the pool returned it, this
        // second request would read the first one's abandoned response.
        RequireWorker();

        await using OverlayWorkerPool pool = Pool(
            deadline: TimeSpan.FromMilliseconds(150),
            maximumCandidatePairs: long.MaxValue);

        OverlayResult killed = await pool.ComputeAsync(
            OverlayOperation.Intersect,
            [Comb(300, horizontal: false)],
            [Comb(300, horizontal: true)],
            3857,
            CancellationToken.None);

        Assert.Equal(OverlayRefusal.Deadline, killed.Refusal);

        OverlayResult after = await pool.ComputeAsync(
            OverlayOperation.Intersect,
            [Square(0, 0, 10)],
            [Square(5, 5, 10)],
            3857,
            CancellationToken.None);

        Assert.Equal(OverlayRefusal.None, after.Refusal);
        Assert.Single(after.Geometries);
    }

    [Fact]
    public async Task A_worker_is_reused_rather_than_launched_per_request()
    {
        // A process launch is tens of milliseconds and these operations are
        // milliseconds, so spawning per request would make the common case
        // slower than the work it does.
        RequireWorker();

        await using OverlayWorkerPool pool = Pool();

        int before = Process.GetProcessesByName("GisServer.Overlay.Worker").Length;

        for (int i = 0; i < 5; i++)
        {
            OverlayResult result = await pool.ComputeAsync(
                OverlayOperation.Intersect,
                [Square(0, 0, 10)],
                [Square(5, 5, 10)],
                3857,
                CancellationToken.None);

            Assert.Equal(OverlayRefusal.None, result.Refusal);
        }

        int after = Process.GetProcessesByName("GisServer.Overlay.Worker").Length;

        Assert.True(
            after - before <= 1,
            $"Five sequential overlays left {after - before} extra worker processes, so each "
            + "request started its own.");
    }

    [Fact]
    public async Task A_missing_worker_is_reported_rather_than_thrown()
    {
        // A packaging mistake must not surface as an unhandled exception on the
        // first request somebody makes.
        await using OverlayWorkerPool pool = new(
            Path.Combine(Path.GetTempPath(), "no-such-overlay-worker.exe"),
            workers: 1,
            NullLoggerFactory.Instance);

        OverlayResult result = await pool.ComputeAsync(
            OverlayOperation.Intersect,
            [Square(0, 0, 10)],
            [Square(5, 5, 10)],
            3857,
            CancellationToken.None);

        Assert.Equal(OverlayRefusal.Unavailable, result.Refusal);
        Assert.Contains("not installed", result.Message!, StringComparison.Ordinal);
    }
}
