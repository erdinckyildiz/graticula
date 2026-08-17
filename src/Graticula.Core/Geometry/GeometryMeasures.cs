using System;
using System.Collections.Generic;

namespace Graticula.Geometries;

/// <summary>
/// Planar area, length and a point inside a polygon.
/// </summary>
/// <remarks>
/// <para>
/// <b>Tier 1, and it is three formulas.</b> The shoelace sum, a sum of segment
/// lengths, and an interior point. Reaching for a geometry library for these
/// would put a Tier 2 type on a Tier 1 path (build-vs-adopt §4) to save
/// arithmetic anybody can check.
/// </para>
/// <para>
/// <b>Every operation here is linear in the vertex count, and that is what makes
/// them safe to expose.</b> A-042's error, found by measurement, was applying one
/// control to two kinds of work: a vertex cap bounds a linear operation exactly,
/// and bounds general overlay not at all — where a 6,408-vertex input cost 490×
/// what a 72,919-vertex one did. Nothing in this file has that shape. One pass,
/// no intermediate structure, no output larger than the input.
/// </para>
/// <para>
/// <b>Planar, and the caller is told so.</b> These treat coordinates as being on
/// a plane. In Web Mercator, area is wrong by a factor of sec²(latitude) — about
/// 1.75 at Istanbul and 4 at Helsinki. Geodesic measurement is a different
/// calculation on the ellipsoid, and offering it silently through the same
/// function is how somebody reports a land area 75% too large.
/// </para>
/// </remarks>
public static class GeometryMeasures
{
    /// <summary>Planar area, by the shoelace formula. Interior rings subtract.</summary>
    /// <param name="geometry">The geometry.</param>
    /// <returns>Area in the square of the coordinate unit. Zero for non-areal input.</returns>
    public static double Area(Geometry geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);

        return geometry switch
        {
            Polygon polygon => Math.Abs(SignedArea(polygon.Shell))
                - SumOf(polygon.Holes, hole => Math.Abs(SignedArea(hole))),
            MultiPolygon multi => SumOf(multi.Parts, Area),
            _ => 0,
        };
    }

    /// <summary>Coordinates in a geometry, holes and parts included.</summary>
    /// <param name="geometry">The geometry.</param>
    /// <returns>How many coordinate pairs it holds.</returns>
    /// <remarks>
    /// <b>The unit the work scales with, which feature count is not.</b> A
    /// hundred parcels and a hundred national outlines are the same row count
    /// and three orders of magnitude apart in decoding, serialising and
    /// transmitting — so a measurement in features cannot tell a cheap
    /// result from an expensive one, and D-30's whole difficulty was numbers
    /// that could not say why.
    /// </remarks>
    public static long CoordinateCount(Geometry geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);

        return geometry switch
        {
            Point => 1,
            LineString line => line.Coordinates.Count,
            Polygon polygon =>
                polygon.Shell.Coordinates.Count
                + SumOfLong(polygon.Holes, hole => hole.Coordinates.Count),
            MultiPolygon multi => SumOfLong(multi.Parts, CoordinateCount),
            MultiLineString lines => SumOfLong(lines.Parts, CoordinateCount),
            MultiPoint points => points.Parts.Count,
            _ => 0,
        };
    }

    private static long SumOfLong<T>(IReadOnlyList<T> items, Func<T, long> measure)
    {
        long total = 0;

        for (int i = 0; i < items.Count; i++)
        {
            total += measure(items[i]);
        }

        return total;
    }

    /// <summary>
    /// Planar length: a polygon's perimeter, a line's length, zero for a point.
    /// </summary>
    /// <param name="geometry">The geometry.</param>
    /// <returns>Length in the coordinate unit.</returns>
    /// <remarks>
    /// A polygon's perimeter includes its holes. ArcGIS reports it that way, and
    /// a perimeter that ignored an interior ring would understate the boundary
    /// of a field with a lake in it.
    /// </remarks>
    public static double Length(Geometry geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);

        return geometry switch
        {
            // LinearRing derives from LineString, so this arm covers both.
            LineString line => LengthOf(line.Coordinates),
            Polygon polygon => LengthOf(polygon.Shell.Coordinates)
                + SumOf(polygon.Holes, hole => LengthOf(hole.Coordinates)),
            MultiLineString multi => SumOf(multi.Parts, Length),
            MultiPolygon multi => SumOf(multi.Parts, Length),
            _ => 0,
        };
    }

    /// <summary>
    /// A point guaranteed to lie inside a polygon.
    /// </summary>
    /// <param name="geometry">The geometry.</param>
    /// <returns>The point, or null when there is no area to be inside of.</returns>
    /// <remarks>
    /// <para>
    /// <b>Not the centroid.</b> The centroid of a crescent, a horseshoe or any
    /// sufficiently concave polygon falls outside it — so a label placed there
    /// sits in the sea, which is the single most visible way a map looks wrong.
    /// This is what ArcGIS calls a label point and PostGIS calls
    /// <c>ST_PointOnSurface</c>.
    /// </para>
    /// <para>
    /// <b>The method is a horizontal scanline.</b> Take the y midway down the
    /// widest gap between distinct vertex heights, intersect the boundary with
    /// it, and take the middle of the widest interior span. One pass over the
    /// edges per candidate, and the candidate is chosen from the vertex list —
    /// so it is linear, which is why it belongs in this file rather than behind
    /// Q-97.
    /// </para>
    /// </remarks>
    public static Point? LabelPoint(Geometry geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);

        Polygon? largest = LargestPolygon(geometry);

        if (largest is null || largest.IsEmpty)
        {
            return null;
        }

        double y = ScanlineY(largest.Shell);
        List<double> crossings = [];

        Cross(largest.Shell, y, crossings);

        foreach (LinearRing hole in largest.Holes)
        {
            Cross(hole, y, crossings);
        }

        crossings.Sort();

        // Pairs of crossings bound the inside. The widest pair is the roomiest
        // place on this line, which is where a label wants to be.
        double bestX = double.NaN;
        double widest = -1;

        for (int i = 0; i + 1 < crossings.Count; i += 2)
        {
            double width = crossings[i + 1] - crossings[i];

            if (width > widest)
            {
                widest = width;
                bestX = (crossings[i] + crossings[i + 1]) / 2;
            }
        }

        // A degenerate polygon — zero height, or a scanline that met no pair —
        // has no interior for a point to be in. A vertex is a truthful fallback
        // in a way an invented coordinate is not.
        return double.IsNaN(bestX)
            ? new Point(largest.Shell.Coordinates.X(0), largest.Shell.Coordinates.Y(0))
            : new Point(bestX, y);
    }

    /// <summary>Twice the signed area, halved. Positive is counter-clockwise.</summary>
    /// <summary>Twice the shoelace sum, halved, about a local origin.</summary>
    /// <remarks>
    /// <para>
    /// <b>The subtraction is the whole point, and leaving it out was a real
    /// defect.</b> The plain shoelace multiplies coordinates together: in Web
    /// Mercator those are around 3,200,000 by 5,000,000, so each product is about
    /// 1.6 × 10¹³. A double carries roughly sixteen significant digits, which
    /// puts the rounding error on each product near 0.003 — and the answer for a
    /// small parcel is a few hundred. The terms very nearly cancel, so what
    /// survives is the difference of large numbers each already wrong in the
    /// third decimal.
    /// </para>
    /// <para>
    /// <b>Measured, not reasoned about.</b> A 638 m² park in EPSG:3857 came back
    /// 0.0013 m² out — two parts per million, which sounds like nothing until
    /// the feature is a 2 m² utility box, where the same absolute error is
    /// 0.07 per cent. It was found because a cut's pieces did not add up to
    /// their target, and the pieces were innocent: both sides of that comparison
    /// were this function.
    /// </para>
    /// <para>
    /// <b>Translating a polygon does not change its area</b>, so subtracting the
    /// first vertex from every coordinate is free in exact arithmetic and
    /// removes the cancellation in floating point. This is what PostGIS's
    /// <c>ptarray_signed_area</c> does, which is why <c>ST_Area</c> agreed with
    /// itself to eleven digits while we disagreed at six.
    /// </para>
    /// </remarks>
    private static double SignedArea(LinearRing ring)
    {
        XySequence points = ring.Coordinates;

        int n = points.Count - 1;

        if (n < 3)
        {
            return 0;
        }

        double originX = points.X(0);
        double originY = points.Y(0);

        double total = 0;

        // From 1 rather than 0: the first term is identically zero once the
        // origin is the first vertex, and so is the last.
        for (int i = 1; i < n; i++)
        {
            total += ((points.X(i) - originX) * (points.Y(i + 1) - originY))
                   - ((points.X(i + 1) - originX) * (points.Y(i) - originY));
        }

        return total / 2;
    }

    private static double LengthOf(XySequence points)
    {
        double total = 0;

        for (int i = 0, n = points.Count - 1; i < n; i++)
        {
            double dx = points.X(i + 1) - points.X(i);
            double dy = points.Y(i + 1) - points.Y(i);
            total += Math.Sqrt((dx * dx) + (dy * dy));
        }

        return total;
    }

    /// <summary>The y to scan along: the middle of the tallest gap between vertices.</summary>
    /// <remarks>
    /// Picking the polygon's mid-height instead is the obvious choice and is
    /// wrong for a C-shape lying on its side, where mid-height passes through
    /// the gap. The tallest gap between distinct vertex heights is where the
    /// polygon is least likely to be pinched.
    /// </remarks>
    private static double ScanlineY(LinearRing shell)
    {
        XySequence points = shell.Coordinates;
        List<double> heights = new(points.Count);

        for (int i = 0; i < points.Count; i++)
        {
            heights.Add(points.Y(i));
        }

        heights.Sort();

        double best = (heights[0] + heights[^1]) / 2;
        double widest = -1;

        for (int i = 0; i + 1 < heights.Count; i++)
        {
            double gap = heights[i + 1] - heights[i];

            if (gap > widest)
            {
                widest = gap;
                best = heights[i] + (gap / 2);
            }
        }

        return best;
    }

    /// <summary>Where a horizontal line at <paramref name="y"/> meets a ring.</summary>
    private static void Cross(LinearRing ring, double y, List<double> into)
    {
        XySequence points = ring.Coordinates;

        for (int i = 0, n = points.Count - 1; i < n; i++)
        {
            double y0 = points.Y(i);
            double y1 = points.Y(i + 1);

            // A half-open test, so a vertex exactly on the line is counted once
            // rather than twice. Counting it twice pairs the crossings wrongly
            // and puts the label outside.
            if ((y0 <= y && y1 > y) || (y1 <= y && y0 > y))
            {
                double x0 = points.X(i);
                double t = (y - y0) / (y1 - y0);
                into.Add(x0 + (t * (points.X(i + 1) - x0)));
            }
        }
    }

    /// <summary>The biggest polygon in whatever was handed over.</summary>
    private static Polygon? LargestPolygon(Geometry geometry)
    {
        switch (geometry)
        {
            case Polygon polygon:
                return polygon;

            case MultiPolygon multi:
                Polygon? best = null;
                double bestArea = -1;

                // The biggest part, not the first. A country with an island gets
                // its label on the mainland, which is where somebody looking for
                // the name expects to find it.
                foreach (Polygon candidate in multi.Parts)
                {
                    double area = Area(candidate);

                    if (area > bestArea)
                    {
                        bestArea = area;
                        best = candidate;
                    }
                }

                return best;

            default:
                return null;
        }
    }

    private static double SumOf<T>(IEnumerable<T> items, Func<T, double> of)
    {
        double total = 0;

        foreach (T item in items)
        {
            total += of(item);
        }

        return total;
    }
}
