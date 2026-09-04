using System;
using System.Collections.Generic;
using Graticula.Geometries;

namespace Graticula.Cartography;

/// <summary>
/// Places a polygon's dots, in the polygon's own coordinates and always in the same places.
/// </summary>
/// <remarks>
/// <para>
/// <b>[ADR-052](../../../docs/adr/ADR-052-the-canonical-symbology-document-is-cim.md) §3.15.</b>
/// A dot-density map draws <c>value / dotValue</c> dots inside each polygon, one colour per
/// field, and the reader judges the mixture. What makes it hard is not the drawing.
/// </para>
/// <para>
/// <b>The scatter is computed in world coordinates and clipped afterwards, not computed in the
/// tile.</b> A district that straddles two tiles has to scatter over its whole area and draw
/// only the dots that land in the picture; scattering into the visible part instead puts the
/// district's whole dot count into each half, and the density doubles along every seam. This is
/// the same fault the heat map has at its edges and it is more visible here, because a dot is a
/// mark somebody can count.
/// </para>
/// <para>
/// <b>And it is deterministic, which is what stops the dots from crawling.</b> `randomSeed` is on
/// the CIM renderer precisely so that the same polygon scatters the same way every time; a
/// scatter reseeded per request moves every dot when the reader pans, which is unreadable. The
/// generator is SplitMix64 — published, exactly specified, and the same on every runtime, unlike
/// `System.Random`, whose sequence carries no such promise across versions.
/// </para>
/// </remarks>
public static class DotScatter
{
    /// <summary>How many tries per dot before a sliver is accepted as under-filled.</summary>
    /// <remarks>
    /// <b>Rejection sampling costs the inverse of the fill ratio.</b> A polygon filling half its
    /// bounding box takes two tries a dot; a diagonal river valley filling a fiftieth takes
    /// fifty. Beyond that the shape is thin enough that its dots would be a line rather than a
    /// scatter, and spending unbounded time to place them is worse than placing fewer.
    /// </remarks>
    public const int TriesPerDot = 60;

    /// <summary>The most dots one feature may be given.</summary>
    /// <remarks>
    /// <b>A ceiling on the drawing, not on the data.</b> A `dotValue` of one over a population
    /// of nine million is nine million dots, which is not a map and not a request anybody meant
    /// to make. The renderer reports it rather than spending the afternoon on it.
    /// </remarks>
    public const int MostDots = 20_000;

    /// <summary>Scatters one polygon's dots.</summary>
    /// <param name="polygon">The area to fill, in its own coordinates.</param>
    /// <param name="count">How many dots to place.</param>
    /// <param name="seed">
    /// The document's <c>randomSeed</c> mixed with something identifying this feature, so the
    /// same feature scatters the same way and two different ones do not overlay each other.
    /// </param>
    /// <returns>The points, in the polygon's coordinates. Fewer than asked for on a sliver.</returns>
    public static IReadOnlyList<(double X, double Y)> Inside(
        Geometry polygon, int count, ulong seed)
    {
        ArgumentNullException.ThrowIfNull(polygon);

        if (count <= 0 || polygon.IsEmpty)
        {
            return [];
        }

        int wanted = Math.Min(count, MostDots);

        List<Polygon> rings = [];

        Collect(polygon, rings);

        if (rings.Count == 0)
        {
            return [];
        }

        Envelope box = Around(rings);

        if (box.MaxX <= box.MinX || box.MaxY <= box.MinY)
        {
            return [];
        }

        List<(double X, double Y)> placed = new(wanted);
        ulong state = seed;
        int tries = (wanted * TriesPerDot) + 100;

        while (placed.Count < wanted && tries-- > 0)
        {
            double x = box.MinX + (Next(ref state) * (box.MaxX - box.MinX));
            double y = box.MinY + (Next(ref state) * (box.MaxY - box.MinY));

            foreach (Polygon ring in rings)
            {
                if (Holds(ring, x, y))
                {
                    placed.Add((x, y));
                    break;
                }
            }
        }

        return placed;
    }

    /// <summary>Whether a polygon covers a point, by the even-odd rule.</summary>
    /// <remarks>
    /// <b>Even-odd, because that is the rule the canvas fills by.</b>
    /// <see cref="IMapCanvas.FillArea"/> says so in as many words: an inner ring inside an outer
    /// one is a hole whichever direction it winds. A dot placed by a different rule would land
    /// in a hole the fill leaves empty, which is a mark on the map that the map says is not
    /// there.
    /// <br/>
    /// The test is the crossing number — a ray east from the point, counting the edges it
    /// crosses — which is the standard algorithm and is exact for every point not exactly on an
    /// edge. A point on an edge falls one way or the other and it does not matter which: it is
    /// one dot on a boundary, and the alternative is a tolerance that has to be chosen.
    /// </remarks>
    /// <param name="polygon">The polygon, shell and holes.</param>
    /// <param name="x">The point's x.</param>
    /// <param name="y">Its y.</param>
    /// <returns>Whether the point is inside.</returns>
    public static bool Holds(Polygon polygon, double x, double y)
    {
        ArgumentNullException.ThrowIfNull(polygon);

        bool inside = Crosses(polygon.Shell.Coordinates, x, y);

        foreach (LinearRing hole in polygon.Holes)
        {
            if (Crosses(hole.Coordinates, x, y))
            {
                inside = !inside;
            }
        }

        return inside;
    }

    private static bool Crosses(XySequence ring, double x, double y)
    {
        bool odd = false;
        int count = ring.Count;

        for (int i = 0, j = count - 1; i < count; j = i++)
        {
            double yi = ring.Y(i);
            double yj = ring.Y(j);

            if (yi > y == yj > y)
            {
                continue;
            }

            double xi = ring.X(i);
            double xj = ring.X(j);

            if (x < xi + ((y - yi) / (yj - yi) * (xj - xi)))
            {
                odd = !odd;
            }
        }

        return odd;
    }

    private static void Collect(Geometry geometry, List<Polygon> into)
    {
        switch (geometry)
        {
            case Polygon polygon when !polygon.IsEmpty:
                into.Add(polygon);
                break;

            case MultiPolygon many:
                foreach (Polygon part in many.Parts)
                {
                    Collect(part, into);
                }

                break;

            default:
                break;
        }
    }

    private static Envelope Around(List<Polygon> rings)
    {
        Envelope box = Envelope.Of(rings[0].Shell.Coordinates);

        for (int i = 1; i < rings.Count; i++)
        {
            Envelope other = Envelope.Of(rings[i].Shell.Coordinates);

            box = new Envelope(
                Math.Min(box.MinX, other.MinX),
                Math.Min(box.MinY, other.MinY),
                Math.Max(box.MaxX, other.MaxX),
                Math.Max(box.MaxY, other.MaxY));
        }

        return box;
    }

    /// <summary>The next number in 0..1, and advances the state.</summary>
    /// <remarks>
    /// <b>SplitMix64, written out rather than taken from the framework.</b> `System.Random` does
    /// not promise the same sequence across .NET versions, and a dot-density map whose dots move
    /// when the runtime is upgraded is one nobody can compare to last year's. This one is
    /// specified to the constant: Steele, Lea and Flood, <i>Fast splittable pseudorandom number
    /// generators</i>, OOPSLA 2014.
    /// </remarks>
    /// <param name="state">The generator's state, advanced in place.</param>
    /// <returns>A number in 0..1.</returns>
    public static double Next(ref ulong state)
    {
        state += 0x9E3779B97F4A7C15UL;

        ulong z = state;

        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        z ^= z >> 31;

        // 53 bits is every value a double can hold between 0 and 1 without repeating one.
        return (z >> 11) * (1.0 / 9007199254740992.0);
    }

    /// <summary>Mixes a document's seed with a feature's identity.</summary>
    /// <remarks>
    /// <b>Both halves are needed.</b> The document's seed alone makes every polygon scatter
    /// identically, so neighbouring districts show the same pattern and the map looks tiled; the
    /// feature's alone ignores an author who changed the seed to get a different arrangement.
    /// </remarks>
    /// <param name="seed">The renderer's <c>randomSeed</c>.</param>
    /// <param name="identity">Something that identifies the feature.</param>
    /// <returns>The seed for this feature.</returns>
    public static ulong SeedFor(long seed, string? identity)
    {
        ulong mixed = (ulong)seed * 0x9E3779B97F4A7C15UL;

        foreach (char c in identity ?? string.Empty)
        {
            mixed = ((mixed << 5) + mixed) ^ c;
        }

        // Zero is a legal state for SplitMix64, but a feature with no identity and a seed of
        // zero would then share one sequence with every other such feature.
        return mixed == 0 ? 0x2545F4914F6CDD1DUL : mixed;
    }
}
