using System;
using System.Collections.Generic;

namespace Graticula.Geometries;

/// <summary>
/// Geometry operations on geometry the caller brought with them.
/// </summary>
/// <remarks>
/// <para>
/// <b>The rule these exist under: push down when the data is already there,
/// compute in process when the caller brought it.</b>
/// <see href="../../../docs/adr/ADR-021-tile-encoding.md">ADR-021</see> pushes
/// tile encoding into PostGIS and is right to — the rows are already in the
/// database, and a z16 tile read 201,580 vertices to emit 2,080, so pushing down
/// is what avoids the traffic. A GeometryServer request is the opposite shape:
/// the geometry arrives in the request body, so sending it to the database
/// <em>creates</em> the traffic instead of avoiding it.
/// </para>
/// <para>
/// <b>The round trip is not a detail, it is the cost.</b> Four benchmark rounds
/// found that this system is bound by memory traffic, not CPU — ~139 bytes per
/// vertex, three to four copies of every coordinate, 80.9% GC pause at 18% CPU.
/// Routing <c>convexHull</c> through PostGIS would add two more copies (WKB out,
/// WKB back) and a network hop to avoid writing a monotone chain. That is the
/// measurement pointing the other way.
/// </para>
/// <para>
/// <b><c>project</c> stays the exception, and it is one.</b> It goes to the
/// datastore because the alternative is shipping PROJ and its datum grids
/// (ADR-022, Q-15), and because the accuracy is then the datastore's. Nothing
/// about that reasoning transfers to <c>ST_Distance</c>.
/// </para>
/// <para>
/// <b>PostGIS is still the oracle.</b> These are verified against
/// <c>ST_ConvexHull</c>, <c>ST_Segmentize</c> and
/// <c>ST_SimplifyPreserveTopology</c> on real geometry — the same method
/// <c>WkbReader</c> used against 6.5 million polygons. Using a database to check
/// an implementation is not the same as depending on one to run it.
/// </para>
/// <para>
/// Everything here works on <see cref="XySequence"/>, which is ADR-003 §6a
/// tier 2: ours, on flat arrays.
/// </para>
/// </remarks>
public static class GeometryOperations
{
    /// <summary>
    /// The smallest convex polygon containing every coordinate.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Andrew's monotone chain.</b> Sort once, then walk twice building the
    /// lower and upper hulls; O(n log n), dominated by the sort, and it allocates
    /// one index array rather than a node per coordinate.
    /// </para>
    /// <para>
    /// <b>The degenerate cases are answers, not failures.</b> A hull of one
    /// distinct point is that point; of two, a line; of collinear points, the
    /// line between the extremes. Returning an empty polygon for those would be
    /// wrong in a way a caller cannot detect.
    /// </para>
    /// </remarks>
    /// <param name="geometry">Any geometry; only its coordinates matter.</param>
    /// <returns>A point, a line or a polygon.</returns>
    public static Geometry ConvexHull(Geometry geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);

        return ConvexHull([geometry]);
    }

    /// <summary>
    /// The smallest convex polygon containing every coordinate of every input.
    /// </summary>
    /// <remarks>
    /// <b>One hull for the set, which is what ArcGIS returns.</b> Hulling each
    /// geometry separately is a different operation, and a caller who wants it
    /// can send one at a time. Taking a list rather than a synthetic collection
    /// type keeps that choice at the boundary where it belongs.
    /// </remarks>
    /// <param name="geometries">Any geometries; only their coordinates matter.</param>
    /// <returns>A point, a line or a polygon.</returns>
    public static Geometry ConvexHull(IReadOnlyList<Geometry> geometries)
    {
        ArgumentNullException.ThrowIfNull(geometries);

        double[] points = DistinctCoordinates(geometries);
        int n = points.Length / 2;

        if (n == 0)
        {
            return Polygon.Empty;
        }

        if (n == 1)
        {
            return new Point(points[0], points[1]);
        }

        Sort(points, n);

        double[] hull = new double[4 * n];
        int size = 0;

        // Lower hull, then upper. The `size - 1 - start` guard is what keeps the
        // two halves from eating each other on collinear input.
        for (int pass = 0; pass < 2; pass++)
        {
            int start = size;

            for (int step = 0; step < n; step++)
            {
                int i = pass == 0 ? step : n - 1 - step;

                while (size - start >= 2
                       && Cross(hull[2 * size - 4], hull[2 * size - 3],
                                hull[2 * size - 2], hull[2 * size - 1],
                                points[2 * i], points[2 * i + 1]) <= 0)
                {
                    size--;
                }

                hull[2 * size] = points[2 * i];
                hull[2 * size + 1] = points[2 * i + 1];
                size++;
            }

            // The last point of each pass is the first of the next.
            size--;
        }

        if (size <= 1)
        {
            return new Point(points[0], points[1]);
        }

        if (size == 2)
        {
            return new LineString(XySequence.Wrap([hull[0], hull[1], hull[2], hull[3]]));
        }

        // A ring is closed, so the first coordinate is repeated at the end.
        double[] ring = new double[2 * (size + 1)];
        Array.Copy(hull, ring, 2 * size);
        ring[2 * size] = hull[0];
        ring[2 * size + 1] = hull[1];

        return new Polygon(new LinearRing(XySequence.Wrap(ring)));
    }

    /// <summary>
    /// Adds vertices so that no segment is longer than the given length.
    /// </summary>
    /// <remarks>
    /// <b>Interpolated, never moved.</b> Every original coordinate survives at
    /// its original value; densifying is additive. A version that resampled at a
    /// fixed interval would be cheaper and would move survey coordinates, which
    /// is the one thing a GIS server must not do quietly.
    /// </remarks>
    /// <param name="geometry">The geometry.</param>
    /// <param name="maxSegmentLength">
    /// The longest segment to leave alone, in the coordinate unit. Must be
    /// positive: zero would ask for infinitely many vertices.
    /// </param>
    /// <returns>The same geometry with more vertices.</returns>
    public static Geometry Densify(Geometry geometry, double maxSegmentLength)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxSegmentLength);

        return Rebuild(geometry, s => DensifyRun(s, maxSegmentLength));
    }

    /// <summary>
    /// Removes vertices that are within the given distance of the line they
    /// sit on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Douglas–Peucker.</b> This is ArcGIS's <c>generalize</c>, which is a
    /// shape simplification. It is <em>not</em> ArcGIS's <c>simplify</c>, which
    /// repairs topology — a different and much harder operation that is not
    /// offered here rather than being offered as this one wearing its name.
    /// </para>
    /// <para>
    /// <b>Rings keep four coordinates.</b> Simplifying a ring to two distinct
    /// points produces something that is closed and encloses nothing, and every
    /// consumer downstream then has to decide what that means. The floor is
    /// enforced here instead.
    /// </para>
    /// </remarks>
    /// <param name="geometry">The geometry.</param>
    /// <param name="maxDeviation">
    /// How far a dropped vertex may be from the line replacing it, in the
    /// coordinate unit. Zero removes only exactly-collinear vertices.
    /// </param>
    /// <returns>The same geometry with fewer vertices.</returns>
    public static Geometry Generalize(Geometry geometry, double maxDeviation)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentOutOfRangeException.ThrowIfNegative(maxDeviation);

        return Rebuild(geometry, s => GeneralizeRun(s, maxDeviation));
    }

    // ---------- walking a geometry ----------

    /// <summary>Applies a coordinate transform to every ring and line.</summary>
    private static Geometry Rebuild(Geometry geometry, Func<XySequence, XySequence> run)
    {
        switch (geometry)
        {
            case Point:
                return geometry;

            case LineString line:
                return line.IsEmpty ? line : new LineString(run(line.Coordinates));

            case Polygon polygon:
                return polygon.IsEmpty ? polygon : RebuildPolygon(polygon, run);

            case MultiPoint:
                return geometry;

            case MultiLineString lines:
            {
                List<LineString> parts = new(lines.Parts.Count);

                foreach (LineString part in lines.Parts)
                {
                    parts.Add(part.IsEmpty ? part : new LineString(run(part.Coordinates)));
                }

                return new MultiLineString(parts);
            }

            case MultiPolygon polygons:
            {
                List<Polygon> parts = new(polygons.Parts.Count);

                foreach (Polygon part in polygons.Parts)
                {
                    parts.Add(part.IsEmpty ? part : RebuildPolygon(part, run));
                }

                return new MultiPolygon(parts);
            }

            default:
                return geometry;
        }
    }

    private static Polygon RebuildPolygon(Polygon polygon, Func<XySequence, XySequence> run)
    {
        LinearRing shell = new(Closed(run(polygon.Shell.Coordinates)));

        if (polygon.Holes.Count == 0)
        {
            return new Polygon(shell);
        }

        List<LinearRing> holes = new(polygon.Holes.Count);

        foreach (LinearRing hole in polygon.Holes)
        {
            holes.Add(new LinearRing(Closed(run(hole.Coordinates))));
        }

        return new Polygon(shell, holes);
    }

    /// <summary>Re-closes a ring whose last coordinate a transform may have moved.</summary>
    private static XySequence Closed(XySequence ring)
    {
        int n = ring.Count;

        if (n == 0)
        {
            return ring;
        }

        if (Math.Abs(ring.X(0) - ring.X(n - 1)) < double.Epsilon
            && Math.Abs(ring.Y(0) - ring.Y(n - 1)) < double.Epsilon)
        {
            return ring;
        }

        double[] closed = new double[2 * (n + 1)];
        ring.AsSpan().CopyTo(closed);
        closed[2 * n] = ring.X(0);
        closed[2 * n + 1] = ring.Y(0);

        return XySequence.Wrap(closed);
    }

    // ---------- the two runs ----------

    private static XySequence DensifyRun(XySequence run, double maxSegmentLength)
    {
        if (run.Count < 2)
        {
            return run;
        }

        List<double> outp = new(run.Count * 2);

        for (int i = 0; i < run.Count - 1; i++)
        {
            double x0 = run.X(i), y0 = run.Y(i);
            double x1 = run.X(i + 1), y1 = run.Y(i + 1);

            outp.Add(x0);
            outp.Add(y0);

            double length = Math.Sqrt(((x1 - x0) * (x1 - x0)) + ((y1 - y0) * (y1 - y0)));

            if (length <= maxSegmentLength)
            {
                continue;
            }

            // <b>Ceiling, then equal steps.</b> Walking forward by exactly
            // maxSegmentLength leaves a short remainder at the end and puts a
            // visible kink there; dividing the segment evenly does not.
            int pieces = (int)Math.Ceiling(length / maxSegmentLength);

            for (int step = 1; step < pieces; step++)
            {
                double fraction = (double)step / pieces;
                outp.Add(x0 + ((x1 - x0) * fraction));
                outp.Add(y0 + ((y1 - y0) * fraction));
            }
        }

        outp.Add(run.X(run.Count - 1));
        outp.Add(run.Y(run.Count - 1));

        return XySequence.Wrap([.. outp]);
    }

    private static XySequence GeneralizeRun(XySequence run, double maxDeviation)
    {
        int n = run.Count;

        if (n <= 2)
        {
            return run;
        }

        bool[] keep = new bool[n];
        keep[0] = true;
        keep[n - 1] = true;

        bool closed = Math.Abs(run.X(0) - run.X(n - 1)) < double.Epsilon
                      && Math.Abs(run.Y(0) - run.Y(n - 1)) < double.Epsilon;

        int split = 0;

        if (closed && n > 3)
        {
            // <b>A ring must be split before it is simplified, and this was
            // wrong until 2026-08-15.</b> On a closed run the first and last
            // coordinates are the same point, so the opening Douglas-Peucker
            // segment is degenerate — every deviation is then measured from that
            // one point rather than from a chord, and the recursion starts in
            // the wrong place. On a square with a notch it dropped a genuine
            // corner and kept the notch's neighbours; PostGIS kept the corner.
            //
            // Splitting at the vertex farthest from the start gives two open
            // chains with real endpoints, which is what the algorithm assumes.
            //
            // The test that should have caught it asserted a ceiling — ours no
            // coarser than twice theirs — rather than comparing the shapes, and
            // a lenient assertion is how a subtle wrongness ships.
            int far = 0;
            double worst = -1;

            for (int i = 1; i < n - 1; i++)
            {
                double dx = run.X(i) - run.X(0);
                double dy = run.Y(i) - run.Y(0);
                double d = (dx * dx) + (dy * dy);

                if (d > worst)
                {
                    worst = d;
                    far = i;
                }
            }

            split = far;
            keep[far] = true;
            Simplify(run, 0, far, maxDeviation, keep);
            Simplify(run, far, n - 1, maxDeviation, keep);
        }
        else
        {
            Simplify(run, 0, n - 1, maxDeviation, keep);
        }

        int kept = 0;

        foreach (bool k in keep)
        {
            if (k)
            {
                kept++;
            }
        }

        if (closed && kept < 4)
        {
            // <b>The floor keeps the shape, not the bounding box.</b> A ring
            // whose vertices all fall inside the tolerance collapses to a sliver
            // — correct Douglas-Peucker, useless output — so something has to be
            // put back. The first version took the extreme x and y vertices,
            // which on a square resolves ties to the same corner twice and
            // produced a triangle with half the area. What goes back instead is
            // the vertex each half of the ring deviated most by: the shape's own
            // most significant corners, which is what PostGIS keeps too.
            foreach (int candidate in (int[])[Worst(run, 0, split), Worst(run, split, n - 1)])
            {
                if (candidate > 0 && !keep[candidate])
                {
                    keep[candidate] = true;
                    kept++;
                }
            }
        }

        if (closed && kept < 4)
        {
            // Still short: the ring genuinely has fewer than three distinct
            // corners. Returning it untouched is better than inventing one.
            return run;
        }

        double[] outp = new double[2 * kept];
        int at = 0;

        for (int i = 0; i < n; i++)
        {
            if (!keep[i])
            {
                continue;
            }

            outp[at++] = run.X(i);
            outp[at++] = run.Y(i);
        }

        return XySequence.Wrap(outp);
    }

    /// <summary>Douglas–Peucker, iterative so a long run cannot overflow the stack.</summary>
    private static void Simplify(XySequence run, int first, int last, double tolerance, bool[] keep)
    {
        Stack<(int First, int Last)> pending = new();
        pending.Push((first, last));

        while (pending.Count > 0)
        {
            (int a, int b) = pending.Pop();

            if (b <= a + 1)
            {
                continue;
            }

            double worst = -1;
            int at = -1;

            for (int i = a + 1; i < b; i++)
            {
                double d = PerpendicularDistance(
                    run.X(i), run.Y(i), run.X(a), run.Y(a), run.X(b), run.Y(b));

                if (d > worst)
                {
                    worst = d;
                    at = i;
                }
            }

            if (worst <= tolerance || at < 0)
            {
                continue;
            }

            keep[at] = true;
            pending.Push((a, at));
            pending.Push((at, b));
        }
    }

    private static double PerpendicularDistance(
        double px, double py, double ax, double ay, double bx, double by)
    {
        double dx = bx - ax;
        double dy = by - ay;

        if (Math.Abs(dx) < double.Epsilon && Math.Abs(dy) < double.Epsilon)
        {
            return Math.Sqrt(((px - ax) * (px - ax)) + ((py - ay) * (py - ay)));
        }

        // Twice the triangle area over the base length, which is the height and
        // needs no square root on the projection.
        double area = Math.Abs(((px - ax) * dy) - ((py - ay) * dx));

        return area / Math.Sqrt((dx * dx) + (dy * dy));
    }

    /// <summary>The vertex between two ends that deviates most from their chord.</summary>
    private static int Worst(XySequence run, int a, int b)
    {
        double worst = -1;
        int at = -1;

        for (int i = a + 1; i < b; i++)
        {
            double d = PerpendicularDistance(
                run.X(i), run.Y(i), run.X(a), run.Y(a), run.X(b), run.Y(b));

            if (d > worst)
            {
                worst = d;
                at = i;
            }
        }

        return at;
    }

    private static XySequence Extremes(XySequence run)
    {
        int minX = 0, maxX = 0, minY = 0, maxY = 0;

        for (int i = 1; i < run.Count; i++)
        {
            if (run.X(i) < run.X(minX)) { minX = i; }
            if (run.X(i) > run.X(maxX)) { maxX = i; }
            if (run.Y(i) < run.Y(minY)) { minY = i; }
            if (run.Y(i) > run.Y(maxY)) { maxY = i; }
        }

        SortedSet<int> chosen = [minX, maxX, minY, maxY];
        List<double> outp = [];

        foreach (int i in chosen)
        {
            outp.Add(run.X(i));
            outp.Add(run.Y(i));
        }

        outp.Add(outp[0]);
        outp.Add(outp[1]);

        return XySequence.Wrap([.. outp]);
    }

    // ---------- coordinates ----------

    private static double[] DistinctCoordinates(IReadOnlyList<Geometry> geometries)
    {
        List<double> all = [];

        foreach (Geometry geometry in geometries)
        {
            Collect(geometry, all);
        }

        if (all.Count == 0)
        {
            return [];
        }

        HashSet<(double, double)> seen = [];
        List<double> distinct = new(all.Count);

        for (int i = 0; i < all.Count; i += 2)
        {
            if (seen.Add((all[i], all[i + 1])))
            {
                distinct.Add(all[i]);
                distinct.Add(all[i + 1]);
            }
        }

        return [.. distinct];
    }

    private static void Collect(Geometry geometry, List<double> into)
    {
        switch (geometry)
        {
            case Point p when !p.IsEmpty:
                into.Add(p.X);
                into.Add(p.Y);
                break;

            case LineString line:
                Append(line.Coordinates, into);
                break;

            case Polygon polygon:
                Append(polygon.Shell.Coordinates, into);

                foreach (LinearRing hole in polygon.Holes)
                {
                    Append(hole.Coordinates, into);
                }

                break;

            case MultiPoint points:
                foreach (Point part in points.Parts)
                {
                    Collect(part, into);
                }

                break;

            case MultiLineString lines:
                foreach (LineString part in lines.Parts)
                {
                    Collect(part, into);
                }

                break;

            case MultiPolygon polygons:
                foreach (Polygon part in polygons.Parts)
                {
                    Collect(part, into);
                }

                break;

            default:
                break;
        }
    }

    private static void Append(XySequence run, List<double> into)
    {
        for (int i = 0; i < run.Count; i++)
        {
            into.Add(run.X(i));
            into.Add(run.Y(i));
        }
    }

    /// <summary>Sorts interleaved coordinates by x then y, in place.</summary>
    private static void Sort(double[] xy, int n)
    {
        int[] order = new int[n];

        for (int i = 0; i < n; i++)
        {
            order[i] = i;
        }

        Array.Sort(order, (a, b) =>
        {
            int byX = xy[2 * a].CompareTo(xy[2 * b]);
            return byX != 0 ? byX : xy[(2 * a) + 1].CompareTo(xy[(2 * b) + 1]);
        });

        double[] sorted = new double[2 * n];

        for (int i = 0; i < n; i++)
        {
            sorted[2 * i] = xy[2 * order[i]];
            sorted[(2 * i) + 1] = xy[(2 * order[i]) + 1];
        }

        Array.Copy(sorted, xy, 2 * n);
    }

    /// <summary>The z of the cross product: &gt;0 is a left turn.</summary>
    private static double Cross(double ox, double oy, double ax, double ay, double bx, double by) =>
        ((ax - ox) * (by - oy)) - ((ay - oy) * (bx - ox));
}
