using NetTopologySuite.Geometries;

namespace GisBench;

/// <summary>
/// Rectangle clipping for the tile path.
///
/// Written to test a hypothesis produced by measurement: the first run of
/// endpoint C showed <c>NTS.Intersection</c> consuming 79% of the request. That
/// call runs general polygon-polygon overlay — robust predicates, snap-rounding,
/// the whole OverlayNG machinery — to clip against an axis-aligned rectangle,
/// which does not need any of it.
///
/// Two optimisations, in order of expected value:
///
/// 1. <b>Trivial accept and reject by bounding box.</b> In a dense urban tile
///    most buildings are entirely inside the tile envelope and need no clipping
///    whatsoever. This is a comparison, not an overlay.
/// 2. <b>Sutherland–Hodgman</b> for the ones that genuinely straddle the
///    boundary — four half-plane passes over a coordinate array.
///
/// The known limitation is honest and accepted: Sutherland–Hodgman can emit
/// degenerate connecting edges along the boundary for concave polygons. Tile
/// renderers tolerate this and it is what tile pipelines generally do. It would
/// NOT be acceptable for analytical overlay, which is exactly why
/// build-vs-adopt keeps real topology with NTS and only the hot path here
/// (docs/build-vs-adopt-policy.md §4).
/// </summary>
public static class RectClip
{
    public enum Result { Outside, Inside, Clipped }

    /// <summary>
    /// Clip <paramref name="g"/> to the rectangle. Returns Inside without
    /// touching the geometry when it is wholly contained — the case that
    /// dominates a dense tile.
    /// </summary>
    public static Result Clip(Geometry g, Envelope box, GeometryFactory f, out Geometry? outGeom)
    {
        var e = g.EnvelopeInternal;

        if (!box.Intersects(e)) { outGeom = null; return Result.Outside; }
        if (box.Contains(e)) { outGeom = g; return Result.Inside; }

        outGeom = g switch
        {
            Polygon p => ClipPolygon(p, box, f),
            MultiPolygon mp => ClipMulti(mp, box, f),
            _ => null
        };

        if (outGeom is null || outGeom.IsEmpty) { outGeom = null; return Result.Outside; }
        return Result.Clipped;
    }

    private static Geometry? ClipMulti(MultiPolygon mp, Envelope box, GeometryFactory f)
    {
        var parts = new List<Polygon>(mp.NumGeometries);
        foreach (var geom in mp.Geometries)
        {
            var p = (Polygon)geom;
            if (!box.Intersects(p.EnvelopeInternal)) continue;
            if (box.Contains(p.EnvelopeInternal)) { parts.Add(p); continue; }
            if (ClipPolygon(p, box, f) is Polygon cp && !cp.IsEmpty) parts.Add(cp);
        }
        return parts.Count == 0 ? null
             : parts.Count == 1 ? parts[0]
             : f.CreateMultiPolygon(parts.ToArray());
    }

    private static Geometry? ClipPolygon(Polygon p, Envelope box, GeometryFactory f)
    {
        var shell = ClipRing(p.ExteriorRing.Coordinates, box);
        if (shell is null) return null;

        LinearRing[] holes = [];
        if (p.NumInteriorRings > 0)
        {
            var kept = new List<LinearRing>(p.NumInteriorRings);
            foreach (var r in p.InteriorRings)
            {
                var h = ClipRing(r.Coordinates, box);
                if (h is not null) kept.Add(f.CreateLinearRing(h));
            }
            holes = kept.ToArray();
        }

        return f.CreatePolygon(f.CreateLinearRing(shell), holes);
    }

    /// <summary>Sutherland–Hodgman against the four half-planes.</summary>
    private static Coordinate[]? ClipRing(Coordinate[] ring, Envelope box)
    {
        // Drop the duplicated closing coordinate while working.
        int n = ring.Length;
        if (n > 1 && ring[0].Equals2D(ring[n - 1])) n--;
        if (n < 3) return null;

        var cur = new List<Coordinate>(n + 8);
        for (int i = 0; i < n; i++) cur.Add(ring[i]);

        cur = ClipHalfPlane(cur, 0, box.MinX);   // x >= minX
        if (cur.Count < 3) return null;
        cur = ClipHalfPlane(cur, 1, box.MaxX);   // x <= maxX
        if (cur.Count < 3) return null;
        cur = ClipHalfPlane(cur, 2, box.MinY);   // y >= minY
        if (cur.Count < 3) return null;
        cur = ClipHalfPlane(cur, 3, box.MaxY);   // y <= maxY
        if (cur.Count < 3) return null;

        cur.Add(new Coordinate(cur[0].X, cur[0].Y)); // re-close
        return cur.ToArray();
    }

    private static List<Coordinate> ClipHalfPlane(List<Coordinate> input, int edge, double v)
    {
        var outp = new List<Coordinate>(input.Count + 4);
        int count = input.Count;
        for (int i = 0; i < count; i++)
        {
            var a = input[i];
            var b = input[(i + 1) % count];
            bool ain = Inside(a, edge, v);
            bool bin = Inside(b, edge, v);

            if (ain) outp.Add(a);
            if (ain != bin) outp.Add(Intersect(a, b, edge, v));
        }
        return outp;
    }

    private static bool Inside(Coordinate c, int edge, double v) => edge switch
    {
        0 => c.X >= v,
        1 => c.X <= v,
        2 => c.Y >= v,
        _ => c.Y <= v
    };

    private static Coordinate Intersect(Coordinate a, Coordinate b, int edge, double v)
    {
        if (edge <= 1)
        {
            double t = (v - a.X) / (b.X - a.X);
            return new Coordinate(v, a.Y + t * (b.Y - a.Y));
        }
        else
        {
            double t = (v - a.Y) / (b.Y - a.Y);
            return new Coordinate(a.X + t * (b.X - a.X), v);
        }
    }
}
