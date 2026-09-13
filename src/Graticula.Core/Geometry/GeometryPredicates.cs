using System;
using System.Collections.Generic;

namespace Graticula.Geometries;

/// <summary>
/// Spatial predicates computed in this process, on flat coordinates — ADR-066.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists when PostGIS answers <c>ST_Intersects</c>.</b> A layer served from a
/// GeoParquet file is read by DuckDB, and DuckDB's core has no exact spatial predicate — only
/// <c>st_intersects_extent</c>, which compares bounding boxes. The spatial extension that has one
/// is downloaded at run time and links GEOS, which this server keeps out of its own address space
/// (ADR-066 §2). So the box test runs in DuckDB, where the rows are, and the exact test runs here on
/// the candidates it returns — the rule <see cref="GeometryOperations"/> already follows: compute in
/// process when the data is not in a database that can.
/// </para>
/// <para>
/// <b>PostGIS is the oracle.</b> This is checked against <c>ST_Intersects</c> on real polygons, the
/// same method <see cref="GeometryOperations"/> uses, and where the two disagree the disagreement is
/// a finding to record rather than a tolerance to widen (Q-20).
/// </para>
/// <para>
/// <b>Plain double arithmetic, no epsilon.</b> GEOS evaluates orientation with extended precision,
/// so two geometries that meet at a single coordinate computed by different routes can be touching
/// to one and a hair apart to the other. A tolerance here would not remove that difference; it would
/// move it and make it harder to state.
/// </para>
/// </remarks>
public static class GeometryPredicates
{
    /// <summary>
    /// Whether two geometries share at least one point — the OGC <c>intersects</c>, boundaries
    /// included.
    /// </summary>
    /// <param name="a">A geometry.</param>
    /// <param name="b">Another, in the same coordinate reference.</param>
    /// <returns>Whether they intersect. An empty geometry intersects nothing.</returns>
    public static bool Intersects(Geometry a, Geometry b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        if (a.IsEmpty || b.IsEmpty || !Overlap(a.Envelope, b.Envelope))
        {
            return false;
        }

        List<Geometry> left = [];
        List<Geometry> right = [];
        Components(a, left);
        Components(b, right);

        foreach (Geometry x in left)
        {
            foreach (Geometry y in right)
            {
                if (Overlap(x.Envelope, y.Envelope) && ComponentsIntersect(x, y))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Whether two boxes share at least one point, edges included.</summary>
    private static bool Overlap(Envelope a, Envelope b) =>
        a.MinX <= b.MaxX && b.MinX <= a.MaxX && a.MinY <= b.MaxY && b.MinY <= a.MaxY;

    /// <summary>The non-empty points, lines and polygons a geometry is made of.</summary>
    private static void Components(Geometry geometry, List<Geometry> into)
    {
        switch (geometry)
        {
            case MultiPoint multi:
                foreach (Point part in multi.Parts)
                {
                    if (!part.IsEmpty)
                    {
                        into.Add(part);
                    }
                }

                break;

            case MultiLineString multi:
                foreach (LineString part in multi.Parts)
                {
                    if (!part.IsEmpty)
                    {
                        into.Add(part);
                    }
                }

                break;

            case MultiPolygon multi:
                foreach (Polygon part in multi.Parts)
                {
                    if (!part.IsEmpty)
                    {
                        into.Add(part);
                    }
                }

                break;

            default:
                if (!geometry.IsEmpty)
                {
                    into.Add(geometry);
                }

                break;
        }
    }

    private static bool ComponentsIntersect(Geometry x, Geometry y)
    {
        // Ordered so each pairing is written once: point before line before polygon.
        if (Rank(x) > Rank(y))
        {
            (x, y) = (y, x);
        }

        return (x, y) switch
        {
            (Point p, Point q) => p.X == q.X && p.Y == q.Y,
            (Point p, LineString line) => OnPath(p.X, p.Y, line.Coordinates),
            (Point p, Polygon polygon) => InOrOn(p.X, p.Y, polygon),
            (LineString l, LineString m) => PathsCross(l.Coordinates, m.Coordinates),
            (LineString line, Polygon polygon) => LineMeetsPolygon(line, polygon),
            (Polygon p, Polygon q) => PolygonsMeet(p, q),
            _ => throw new ArgumentException(
                $"'{x.GetType().Name}' and '{y.GetType().Name}' is a pairing this predicate does not know."),
        };
    }

    private static int Rank(Geometry g) => g switch
    {
        Point => 0,
        LineString => 1,
        _ => 2,
    };

    private static bool LineMeetsPolygon(LineString line, Polygon polygon)
    {
        foreach (XySequence ring in Rings(polygon))
        {
            if (PathsCross(line.Coordinates, ring))
            {
                return true;
            }
        }

        // No boundary crossing: the line is wholly inside or wholly outside, so one vertex decides.
        return InOrOn(line.Coordinates.X(0), line.Coordinates.Y(0), polygon);
    }

    private static bool PolygonsMeet(Polygon p, Polygon q)
    {
        foreach (XySequence a in Rings(p))
        {
            foreach (XySequence b in Rings(q))
            {
                if (PathsCross(a, b))
                {
                    return true;
                }
            }
        }

        // No boundaries meet: one contains the other, or they are apart.
        return InOrOn(p.Shell.Coordinates.X(0), p.Shell.Coordinates.Y(0), q)
            || InOrOn(q.Shell.Coordinates.X(0), q.Shell.Coordinates.Y(0), p);
    }

    private static IEnumerable<XySequence> Rings(Polygon polygon)
    {
        yield return polygon.Shell.Coordinates;

        foreach (LinearRing hole in polygon.Holes)
        {
            if (!hole.IsEmpty)
            {
                yield return hole.Coordinates;
            }
        }
    }

    /// <summary>Whether a point is inside a polygon's area or on its boundary.</summary>
    private static bool InOrOn(double x, double y, Polygon polygon)
    {
        XySequence shell = polygon.Shell.Coordinates;

        if (OnPath(x, y, shell))
        {
            return true;
        }

        if (!Inside(x, y, shell))
        {
            return false;
        }

        foreach (LinearRing hole in polygon.Holes)
        {
            if (hole.IsEmpty)
            {
                continue;
            }

            // On a hole's edge is on the polygon's boundary; strictly inside a hole is outside.
            if (OnPath(x, y, hole.Coordinates))
            {
                return true;
            }

            if (Inside(x, y, hole.Coordinates))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Even–odd ray casting against one closed ring, the boundary already ruled out.</summary>
    private static bool Inside(double x, double y, XySequence ring)
    {
        bool inside = false;
        int n = ring.Count;

        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            double xi = ring.X(i), yi = ring.Y(i), xj = ring.X(j), yj = ring.Y(j);

            if ((yi > y) != (yj > y) && x < ((xj - xi) * (y - yi) / (yj - yi)) + xi)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    /// <summary>Whether a point lies on any segment of a path.</summary>
    private static bool OnPath(double x, double y, XySequence path)
    {
        if (path.Count == 1)
        {
            return path.X(0) == x && path.Y(0) == y;
        }

        for (int i = 0; i + 1 < path.Count; i++)
        {
            if (OnSegment(x, y, path.X(i), path.Y(i), path.X(i + 1), path.Y(i + 1)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool OnSegment(double x, double y, double ax, double ay, double bx, double by) =>
        Orientation(ax, ay, bx, by, x, y) == 0
        && x >= Math.Min(ax, bx) && x <= Math.Max(ax, bx)
        && y >= Math.Min(ay, by) && y <= Math.Max(ay, by);

    /// <summary>Whether any segment of one path meets any segment of another, touching included.</summary>
    private static bool PathsCross(XySequence p, XySequence q)
    {
        if (p.Count == 1)
        {
            return OnPath(p.X(0), p.Y(0), q);
        }

        if (q.Count == 1)
        {
            return OnPath(q.X(0), q.Y(0), p);
        }

        for (int i = 0; i + 1 < p.Count; i++)
        {
            double ax = p.X(i), ay = p.Y(i), bx = p.X(i + 1), by = p.Y(i + 1);
            double minX = Math.Min(ax, bx), maxX = Math.Max(ax, bx);
            double minY = Math.Min(ay, by), maxY = Math.Max(ay, by);

            for (int j = 0; j + 1 < q.Count; j++)
            {
                double cx = q.X(j), cy = q.Y(j), dx = q.X(j + 1), dy = q.Y(j + 1);

                // The box test first: it rejects almost every pair for the price of four comparisons.
                if (Math.Max(cx, dx) < minX || Math.Min(cx, dx) > maxX
                    || Math.Max(cy, dy) < minY || Math.Min(cy, dy) > maxY)
                {
                    continue;
                }

                if (SegmentsMeet(ax, ay, bx, by, cx, cy, dx, dy))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool SegmentsMeet(
        double ax, double ay, double bx, double by, double cx, double cy, double dx, double dy)
    {
        int o1 = Orientation(ax, ay, bx, by, cx, cy);
        int o2 = Orientation(ax, ay, bx, by, dx, dy);
        int o3 = Orientation(cx, cy, dx, dy, ax, ay);
        int o4 = Orientation(cx, cy, dx, dy, bx, by);

        if (o1 != o2 && o3 != o4)
        {
            return true;
        }

        // Collinear or touching at an endpoint: one endpoint lies on the other segment.
        return (o1 == 0 && OnSegment(cx, cy, ax, ay, bx, by))
            || (o2 == 0 && OnSegment(dx, dy, ax, ay, bx, by))
            || (o3 == 0 && OnSegment(ax, ay, cx, cy, dx, dy))
            || (o4 == 0 && OnSegment(bx, by, cx, cy, dx, dy));
    }

    /// <summary>The sign of the turn a → b → c: 1 anticlockwise, -1 clockwise, 0 collinear.</summary>
    private static int Orientation(double ax, double ay, double bx, double by, double cx, double cy)
    {
        double cross = ((bx - ax) * (cy - ay)) - ((by - ay) * (cx - ax));
        return cross > 0 ? 1 : cross < 0 ? -1 : 0;
    }
}
