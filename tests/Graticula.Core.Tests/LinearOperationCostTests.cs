using System;
using System.Collections.Generic;
using System.Diagnostics;
using Graticula.Geometries;

// The type and the namespace `Graticula.Geometry` differ by one letter, so `Geometry`
// alone is ambiguous inside a test namespace. Qualified rather than aliased, because an
// alias reads as a type this project does not have.
using Xunit;
using Xunit.Abstractions;

namespace Graticula.Core.Tests;

/// <summary>
/// What the 500,000-vertex cap bounds, in seconds, on adversarial input.
/// </summary>
/// <remarks>
/// <para>
/// <b>[Q-115](../../docs/open-questions.md), and the point is to make a claim
/// falsifiable rather than to add a timeout.</b>
/// [ADR-022](../../docs/adr/ADR-022-geometry-server.md) §3 argues that each of these
/// operations is one pass over the coordinates, so the vertex cap bounds the work
/// exactly and no clock is needed. The argument is sound and had never been costed —
/// and an uncosted argument about a whole class of operations is the thing Q-115 said
/// should not be settled by whoever noticed.
/// </para>
/// <para>
/// <b>Adversarial input, not representative input.</b> A real 500,000-vertex outline
/// says nothing about what a caller can construct on purpose, so each operation gets
/// the shape that is worst for it: a spiral for the hull, segments long enough that
/// densify must split every one, a zigzag with no removable vertex for generalize.
/// Each also gets its easy case, because an operation that is fast only when it does
/// nothing has not been measured.
/// </para>
/// <para>
/// <b>The ceiling is deliberately generous.</b> These figures are hundreds of
/// milliseconds on the machine they were written on; the assertion is seconds. A tight
/// bound would be a flaky test, and the claim under test is *this is bounded at all*,
/// not *this is fast*. A regression that turned one pass into two would still show.
/// </para>
/// <para>
/// <b>Needs a quiet machine — [ADR-048](../../docs/adr/ADR-048-ci-does-not-run-the-real-data-suites.md)
/// §5b.</b> It is a timing assertion, and a contended runner measures the neighbours.
/// </para>
/// </remarks>
[Trait("Needs", "QuietMachine")]
public sealed class LinearOperationCostTests(ITestOutputHelper output)
{
    /// <summary>The cap GeometryServer applies, from its own constant's value.</summary>
    private const int Cap = 500_000;

    /// <summary>
    /// What no linear operation may exceed at the cap.
    /// </summary>
    /// <remarks>
    /// <b>Five seconds, against measurements in the hundreds of milliseconds.</b> The
    /// number is not a performance target — it is the line past which *the cap is the
    /// bound* stops being true, and something quadratic would cross it by orders of
    /// magnitude rather than by a margin.
    /// </remarks>
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(5);

    public static TheoryData<string, string> Cases() => new()
    {
        { "ConvexHull", "spiral" },
        { "ConvexHull", "circle" },
        { "Densify", "every segment split" },
        { "Densify", "nothing to split" },
        { "Generalize", "everything removable" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void A_linear_operation_at_the_cap_is_bounded_by_the_cap(string operation, string shape)
    {
        Graticula.Geometries.Geometry input = Input(operation, shape);
        Func<Graticula.Geometries.Geometry, Graticula.Geometries.Geometry> run = Run(operation, shape);

        // The first call pays JIT and first touch, and measuring it would measure the
        // runtime rather than the algorithm.
        run(input);

        List<double> times = [];
        long allocated = 0;

        for (int attempt = 0; attempt < 3; attempt++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            Stopwatch clock = Stopwatch.StartNew();

            run(input);

            clock.Stop();
            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            times.Add(clock.Elapsed.TotalMilliseconds);
        }

        times.Sort();
        double median = times[1];

        output.WriteLine(
            $"{operation} ({shape}): {Cap:N0} vertices, {median:F0} ms, "
            + $"{allocated / (1024.0 * 1024.0):F0} MB allocated");

        Assert.True(
            median < Ceiling.TotalMilliseconds,
            $"{operation} on {Cap:N0} vertices ({shape}) took {median:F0} ms. ADR-022 §3's "
            + "argument is that each of these is one pass over the coordinates, so the vertex "
            + "cap bounds the work — a figure this size means it does not, and the cap is not "
            + "the time bound this server implies it is.");
    }

    private static LineString Input(string operation, string shape) => (operation, shape) switch
    {
        ("ConvexHull", "spiral") => Spiral(Cap),
        ("ConvexHull", "circle") => Circle(Cap),

        // <b>Sized so the *output* is at the cap, which is the only sizing that means
        // anything for this operation.</b> Densify's cost is its output and its output
        // is a function of a caller-supplied number, so an input at the cap says
        // nothing: the first version of this test asked for 250,000 segments split a
        // hundred and forty ways each, ran for ten minutes, and was measuring an
        // unbounded request rather than a bounded one. That is Q-115's finding, and
        // `DensifiedVertexCount` is now what the endpoint refuses on.
        ("Densify", _) => Comb(Cap / 2, shape == "every segment split" ? 3 : 1),
        _ => Straight(Cap),
    };

    private static Func<Graticula.Geometries.Geometry, Graticula.Geometries.Geometry> Run(string operation, string shape) =>
        (operation, shape) switch
        {
            ("ConvexHull", _) => GeometryOperations.ConvexHull,
            ("Densify", "every segment split") => g => GeometryOperations.Densify(g, 1),
            ("Densify", _) => g => GeometryOperations.Densify(g, 10_000),
            _ => g => GeometryOperations.Generalize(g, 1_000),
        };

    [Fact]
    public void Densify_counts_what_it_would_produce_without_producing_it()
    {
        // <b>The finding, in one assertion.</b> Two vertices is far under any input cap
        // and produces a million: densify's cost is its output, and its output is a
        // number the caller chooses. Measured before the count existed: 1,000,001
        // vertices and 47 MB from a two-vertex line, with nothing stopping the same
        // call three orders of magnitude smaller.
        double[] xy = [0, 0, 1000, 0];
        LineString line = new(XySequence.Wrap(xy));

        Assert.Equal(1_000_001, GeometryOperations.DensifiedVertexCount(line, 0.001));

        // The count is exact, which is what lets it be a refusal rather than a guess.
        Assert.Equal(
            GeometryOperations.DensifiedVertexCount(line, 0.01),
            ((LineString)GeometryOperations.Densify(line, 0.01)).Coordinates.Count);
    }

    [Fact]
    public void A_segment_length_small_enough_to_overflow_saturates_instead()
    {
        // A caller who wraps the count into a small number passes the very check it
        // exists to fail, so the arithmetic saturates rather than overflowing.
        double[] xy = [0, 0, 1_000_000_000, 0];
        LineString line = new(XySequence.Wrap(xy));

        Assert.Equal(long.MaxValue, GeometryOperations.DensifiedVertexCount(line, 1e-12));
    }

    [Fact]
    public void Generalize_refuses_a_shape_built_to_make_it_quadratic()
    {
        // <b>Douglas-Peucker on a run where nothing may be dropped.</b> Measured before
        // the budget existed: 237 ms at 2,000 vertices, 46,241 ms at 32,000 — a clean
        // quadratic that extrapolates to about three hours of CPU at the 500,000-vertex
        // cap, for one request, in process, with nothing to cancel it.
        //
        // 20,000 rather than the cap, because this test must fail fast when the budget
        // is removed rather than hang the suite for an hour.
        GeometryWorkException refused = Assert.Throws<GeometryWorkException>(
            () => GeometryOperations.Generalize(Zigzag(20_000), 0.0001));

        Assert.Contains("tolerance", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Ordinary_data_is_nowhere_near_the_budget()
    {
        // <b>The control, and it is the reason the budget is 64 and not 4.</b> A refusal
        // that fired on real coordinates would be worse than the quadratic case: the
        // pathological input is constructed on purpose and a customer's coastline is
        // not. A circle at the full cap is the smooth case, and it passes.
        GeometryOperations.Generalize(Circle(Cap), 0.0001);

        // <b>And a jagged line, which is where the first version of this control went
        // wrong and the correction is worth keeping.</b> It used a comb of amplitude 1
        // at a tolerance of 0.5 — at which *nothing* is removable, so the "ordinary"
        // control was a second pathological case and was refused. A tolerance above the
        // noise is what makes a line ordinary, and that is the case real callers send:
        // survey noise dropped, shape kept.
        GeometryOperations.Generalize(Noisy(Cap, 1), 5);
    }

    /// <summary>A spiral: no point is interior, so the hull cannot discard early.</summary>
    private static LineString Spiral(int points)
    {
        double[] xy = new double[points * 2];

        for (int i = 0; i < points; i++)
        {
            double angle = i * 0.01;
            double radius = 1 + (i * 0.001);

            xy[i * 2] = radius * Math.Cos(angle);
            xy[(i * 2) + 1] = radius * Math.Sin(angle);
        }

        return new LineString(XySequence.Wrap(xy));
    }

    /// <summary>A circle: every point is on the hull.</summary>
    private static LineString Circle(int points)
    {
        double[] xy = new double[points * 2];

        for (int i = 0; i < points; i++)
        {
            double angle = i * 2 * Math.PI / points;

            xy[i * 2] = 1000 * Math.Cos(angle);
            xy[(i * 2) + 1] = 1000 * Math.Sin(angle);
        }

        return new LineString(XySequence.Wrap(xy));
    }

    /// <summary>Segments of a chosen length, so densify's work is chosen too.</summary>
    private static LineString Comb(int points, double segment)
    {
        double[] xy = new double[points * 2];

        for (int i = 0; i < points; i++)
        {
            xy[i * 2] = i * segment;
            xy[(i * 2) + 1] = i % 2 == 0 ? 0 : segment;
        }

        return new LineString(XySequence.Wrap(xy));
    }

    /// <summary>A zigzag whose every vertex is further out than any usable tolerance.</summary>
    private static LineString Zigzag(int points)
    {
        double[] xy = new double[points * 2];

        for (int i = 0; i < points; i++)
        {
            xy[i * 2] = i;
            xy[(i * 2) + 1] = i % 2 == 0 ? 0 : 100;
        }

        return new LineString(XySequence.Wrap(xy));
    }

    /// <summary>A line with deterministic noise below the tolerance it is simplified at.</summary>
    /// <remarks>
    /// <b>Deterministic, because a control that fails one run in fifty is worse than no
    /// control.</b> The values come from a fixed recurrence rather than a random source,
    /// so the same shape is measured on every machine.
    /// </remarks>
    private static LineString Noisy(int points, double amplitude)
    {
        double[] xy = new double[points * 2];
        uint state = 0x9E3779B9;

        for (int i = 0; i < points; i++)
        {
            state = (state * 1664525) + 1013904223;

            xy[i * 2] = i;
            xy[(i * 2) + 1] = (i * 0.01) + (((state >> 16) & 0xFF) / 255.0 * amplitude);
        }

        return new LineString(XySequence.Wrap(xy));
    }

    /// <summary>A straight line: every interior vertex is removable.</summary>
    private static LineString Straight(int points)
    {
        double[] xy = new double[points * 2];

        for (int i = 0; i < points; i++)
        {
            xy[i * 2] = i;
            xy[(i * 2) + 1] = 0;
        }

        return new LineString(XySequence.Wrap(xy));
    }
}
