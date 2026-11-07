using System;
using System.Collections.Generic;

namespace Graticula.Scenes;

/// <summary>
/// Triangles covering a polygon with holes, in a plane — the roof and floor of an extruded footprint (ADR-269 §5.3).
/// </summary>
/// <remarks>
/// <para>
/// <b>Ear clipping, with each hole joined to the outer ring by a bridge.</b> A footprint has tens of vertices, so the
/// quadratic cost of clipping one ear at a time and testing every other vertex against it is nothing, and the method
/// needs no library: a hole is spliced into the outer ring along a segment that crosses no edge, which makes one
/// simple ring that touches itself only along that segment, and that ring is clipped.
/// </para>
/// <para>
/// <b>Robust where it matters, not everywhere.</b> Duplicate and collinear vertices are dropped first; a ring that
/// still yields no ear after a full pass (a self-intersecting footprint) is finished with a fan from its first vertex
/// rather than dropped, so a bad polygon is drawn wrongly rather than not at all.
/// </para>
/// </remarks>
public static class FootprintTriangulator
{
    /// <summary>Triangulates a polygon: the outer ring and its holes, each open or closed, in any winding.</summary>
    /// <param name="outer">The outer ring, as (x, y) in a plane.</param>
    /// <param name="holes">The holes.</param>
    /// <returns>Triangles as index triples into the returned vertex list, counter-clockwise.</returns>
    public static (List<(double X, double Y)> Vertices, List<int> Triangles) Triangulate(
        IReadOnlyList<(double X, double Y)> outer, IReadOnlyList<IReadOnlyList<(double X, double Y)>> holes)
    {
        ArgumentNullException.ThrowIfNull(outer);
        ArgumentNullException.ThrowIfNull(holes);

        List<(double X, double Y)> ring = Clean(outer);
        List<int> triangles = [];

        if (ring.Count < 3)
        {
            return (ring, triangles);
        }

        if (SignedArea(ring) < 0)
        {
            ring.Reverse();
        }

        // Holes clockwise, joined from the one reaching furthest right, as each bridge must see the ring it joins.
        List<List<(double X, double Y)>> cleanHoles = [];
        foreach (IReadOnlyList<(double X, double Y)> hole in holes)
        {
            List<(double X, double Y)> h = Clean(hole);
            if (h.Count < 3)
            {
                continue;
            }

            if (SignedArea(h) > 0)
            {
                h.Reverse();
            }

            cleanHoles.Add(h);
        }

        cleanHoles.Sort((a, b) => MaxX(b).CompareTo(MaxX(a)));

        foreach (List<(double X, double Y)> hole in cleanHoles)
        {
            ring = Bridge(ring, hole, cleanHoles);
        }

        Clip(ring, triangles);
        return (ring, triangles);
    }

    private static double MaxX(List<(double X, double Y)> ring)
    {
        double max = double.NegativeInfinity;
        foreach ((double x, _) in ring)
        {
            max = Math.Max(max, x);
        }

        return max;
    }

    /// <summary>The ring without its closing vertex, repeated vertices or vertices on a straight run.</summary>
    internal static List<(double X, double Y)> Clean(IReadOnlyList<(double X, double Y)> points)
    {
        List<(double X, double Y)> ring = [];

        foreach ((double X, double Y) p in points)
        {
            if (ring.Count == 0 || !Same(ring[^1], p))
            {
                ring.Add(p);
            }
        }

        if (ring.Count > 1 && Same(ring[0], ring[^1]))
        {
            ring.RemoveAt(ring.Count - 1);
        }

        bool removed = true;
        while (removed && ring.Count >= 3)
        {
            removed = false;
            for (int i = 0; i < ring.Count; i++)
            {
                (double X, double Y) a = ring[(i + ring.Count - 1) % ring.Count], b = ring[i], c = ring[(i + 1) % ring.Count];
                if (Math.Abs(Cross(a, b, c)) <= 1e-12 * Math.Max(1, Scale(a, b, c)))
                {
                    ring.RemoveAt(i);
                    removed = true;
                    break;
                }
            }
        }

        return ring;
    }

    private static double Scale((double X, double Y) a, (double X, double Y) b, (double X, double Y) c) =>
        Math.Max(Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y), Math.Abs(c.X - b.X) + Math.Abs(c.Y - b.Y));

    private static bool Same((double X, double Y) a, (double X, double Y) b) =>
        Math.Abs(a.X - b.X) <= 1e-9 && Math.Abs(a.Y - b.Y) <= 1e-9;

    /// <summary>Twice the signed area: positive for a counter-clockwise ring.</summary>
    internal static double SignedArea(IReadOnlyList<(double X, double Y)> ring)
    {
        double sum = 0;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
        {
            sum += (ring[j].X * ring[i].Y) - (ring[i].X * ring[j].Y);
        }

        return sum;
    }

    private static double Cross((double X, double Y) a, (double X, double Y) b, (double X, double Y) c) =>
        ((b.X - a.X) * (c.Y - a.Y)) - ((b.Y - a.Y) * (c.X - a.X));

    /// <summary>The ring with a hole spliced in along a segment from the hole's rightmost vertex that crosses no edge.</summary>
    private static List<(double X, double Y)> Bridge(
        List<(double X, double Y)> ring, List<(double X, double Y)> hole, List<List<(double X, double Y)>> allHoles)
    {
        int m = 0;
        for (int i = 1; i < hole.Count; i++)
        {
            if (hole[i].X > hole[m].X || (hole[i].X == hole[m].X && hole[i].Y < hole[m].Y))
            {
                m = i;
            }
        }

        (double X, double Y) from = hole[m];

        // Candidates nearest first; the first whose segment crosses no edge of the ring or any hole is the bridge.
        List<int> order = [];
        for (int i = 0; i < ring.Count; i++)
        {
            order.Add(i);
        }

        order.Sort((a, b) => Distance(ring[a], from).CompareTo(Distance(ring[b], from)));

        int to = order[0];
        foreach (int candidate in order)
        {
            if (!Crosses(from, ring[candidate], ring) && !CrossesAny(from, ring[candidate], allHoles))
            {
                to = candidate;
                break;
            }
        }

        List<(double X, double Y)> joined = new(ring.Count + hole.Count + 2);
        for (int i = 0; i <= to; i++)
        {
            joined.Add(ring[i]);
        }

        for (int k = 0; k <= hole.Count; k++)
        {
            joined.Add(hole[(m + k) % hole.Count]);
        }

        for (int i = to; i < ring.Count; i++)
        {
            joined.Add(ring[i]);
        }

        return joined;
    }

    private static double Distance((double X, double Y) a, (double X, double Y) b) =>
        ((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y));

    private static bool CrossesAny((double X, double Y) p, (double X, double Y) q, List<List<(double X, double Y)>> rings)
    {
        foreach (List<(double X, double Y)> ring in rings)
        {
            if (Crosses(p, q, ring))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether a segment properly crosses an edge of a ring; touching at an endpoint is not crossing.</summary>
    private static bool Crosses((double X, double Y) p, (double X, double Y) q, List<(double X, double Y)> ring)
    {
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
        {
            (double X, double Y) a = ring[j], b = ring[i];
            if (Same(a, p) || Same(a, q) || Same(b, p) || Same(b, q))
            {
                continue;
            }

            double d1 = Cross(p, q, a), d2 = Cross(p, q, b), d3 = Cross(a, b, p), d4 = Cross(a, b, q);
            if (((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0)))
            {
                return true;
            }
        }

        return false;
    }

    private static void Clip(List<(double X, double Y)> ring, List<int> triangles)
    {
        List<int> left = [];
        for (int i = 0; i < ring.Count; i++)
        {
            left.Add(i);
        }

        int guard = 0;
        while (left.Count > 3 && guard < ring.Count * ring.Count + 10)
        {
            guard++;
            bool clipped = false;

            for (int k = 0; k < left.Count; k++)
            {
                int a = left[(k + left.Count - 1) % left.Count], b = left[k], c = left[(k + 1) % left.Count];

                if (Cross(ring[a], ring[b], ring[c]) <= 0 || AnyInside(ring, left, a, b, c))
                {
                    continue;
                }

                triangles.Add(a);
                triangles.Add(b);
                triangles.Add(c);
                left.RemoveAt(k);
                clipped = true;
                break;
            }

            if (!clipped)
            {
                break;
            }
        }

        // What is left after the ears ran out (three vertices, or a ring that crosses itself) is finished with a fan.
        for (int k = 1; k + 1 < left.Count; k++)
        {
            triangles.Add(left[0]);
            triangles.Add(left[k]);
            triangles.Add(left[k + 1]);
        }
    }

    private static bool AnyInside(List<(double X, double Y)> ring, List<int> left, int a, int b, int c)
    {
        foreach (int i in left)
        {
            if (i == a || i == b || i == c)
            {
                continue;
            }

            (double X, double Y) p = ring[i];

            // A vertex at the same place as a corner — a bridge's two ends — does not block the ear.
            if (Same(p, ring[a]) || Same(p, ring[b]) || Same(p, ring[c]))
            {
                continue;
            }

            if (Cross(ring[a], ring[b], p) >= 0 && Cross(ring[b], ring[c], p) >= 0 && Cross(ring[c], ring[a], p) >= 0)
            {
                return true;
            }
        }

        return false;
    }
}
