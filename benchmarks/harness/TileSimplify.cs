using NetTopologySuite.Geometries;

namespace GisBench;

/// <summary>
/// Douglas–Peucker for the tile path. Ours, not NTS's — the second Tier 1
/// primitive, written after the first measurement showed simplification was
/// 55% of a low-zoom tile once clipping was fixed.
///
/// Three differences from <c>DouglasPeuckerSimplifier</c>, and they are the
/// whole point:
///
///  1. It runs AFTER the tile-space transform, not before. Quantisation to a
///     4096-unit integer grid already collapses vertices; simplifying first
///     and quantising second does the expensive work on points that the next
///     stage was going to merge anyway.
///  2. It works on flat double[] arrays. NTS <c>Coordinate</c> is a class, so
///     a 215k-vertex polygon is 215k heap objects before any work starts.
///  3. It does not repair topology. NTS calls IsValid on every simplified
///     polygon and Buffer(0) — a full overlay — on any that fails. A tile is
///     a picture; that budget belongs to the analytical path, not this one.
///
/// The cost of (3) is real and is stated rather than hidden: self-intersections
/// introduced by simplification survive into the tile. Renderers tolerate them.
/// Anything that computes with the geometry must not use this path.
/// </summary>
internal static class TileSimplify
{
    /// <summary>Vertices in and out of the last call, for reporting.</summary>
    internal struct Stats
    {
        public int In;
        public int Out;
        public int RingsDropped;
    }

    internal static Geometry? Simplify(Geometry g, double tol, GeometryFactory f, ref Stats st)
    {
        switch (g)
        {
            case Polygon p:
                return SimplifyPolygon(p, tol, f, ref st);

            case MultiPolygon mp:
            {
                var parts = new List<Polygon>(mp.NumGeometries);
                for (int i = 0; i < mp.NumGeometries; i++)
                {
                    var sp = SimplifyPolygon((Polygon)mp.GetGeometryN(i), tol, f, ref st);
                    if (sp is not null) parts.Add(sp);
                }
                if (parts.Count == 0) return null;
                return parts.Count == 1 ? parts[0] : f.CreateMultiPolygon(parts.ToArray());
            }

            case LineString ls:
            {
                var cs = SimplifyRing(ls.Coordinates, tol, closed: false, ref st);
                return cs is null || cs.Length < 2 ? null : f.CreateLineString(cs);
            }

            case Point:
                st.In++; st.Out++;
                return g;

            case GeometryCollection gc:
            {
                var parts = new List<Geometry>(gc.NumGeometries);
                for (int i = 0; i < gc.NumGeometries; i++)
                {
                    var sg = Simplify(gc.GetGeometryN(i), tol, f, ref st);
                    if (sg is not null) parts.Add(sg);
                }
                return parts.Count == 0 ? null : f.BuildGeometry(parts);
            }

            default:
                return g;
        }
    }

    private static Polygon? SimplifyPolygon(Polygon p, double tol, GeometryFactory f, ref Stats st)
    {
        var shell = SimplifyRing(p.ExteriorRing.Coordinates, tol, closed: true, ref st);
        if (shell is null) { st.RingsDropped++; return null; }

        LinearRing[]? holes = null;
        if (p.NumInteriorRings > 0)
        {
            var kept = new List<LinearRing>(p.NumInteriorRings);
            for (int i = 0; i < p.NumInteriorRings; i++)
            {
                var h = SimplifyRing(p.GetInteriorRingN(i).Coordinates, tol, closed: true, ref st);
                if (h is null) { st.RingsDropped++; continue; }
                kept.Add(f.CreateLinearRing(h));
            }
            holes = kept.ToArray();
        }

        return f.CreatePolygon(f.CreateLinearRing(shell), holes);
    }

    /// <summary>
    /// Returns null when the ring collapsed to nothing worth drawing: fewer than
    /// four points, or zero area on the integer grid. A collapsed ring is not an
    /// error — at z12 a building smaller than a tile unit genuinely has no
    /// picture to contribute.
    /// </summary>
    private static Coordinate[]? SimplifyRing(Coordinate[] src, double tol, bool closed, ref Stats st)
    {
        int n = src.Length;
        st.In += n;
        if (n == 0) return null;

        // Flat arrays. The allocation is two doubles per vertex against NTS's
        // object header plus two doubles plus a reference. Sized n+1 because a
        // ring that arrives unclosed gets its closing point appended below.
        var x = new double[n + 1];
        var y = new double[n + 1];
        int m = 0;

        // Pass 1: drop consecutive duplicates. After quantisation there are a
        // lot of them, and DP does not need to see them.
        for (int i = 0; i < n; i++)
        {
            double cx = src[i].X, cy = src[i].Y;
            if (m > 0 && x[m - 1] == cx && y[m - 1] == cy) continue;
            x[m] = cx; y[m] = cy; m++;
        }

        if (closed)
        {
            // Keep the ring explicitly closed for the DP endpoints.
            if (m > 1 && (x[0] != x[m - 1] || y[0] != y[m - 1])) { x[m] = x[0]; y[m] = y[0]; m++; }
            if (m < 4) return null;
        }
        else if (m < 2) return null;

        var keep = new bool[m];
        keep[0] = true;
        keep[m - 1] = true;
        Dp(x, y, 0, m - 1, tol * tol, keep);

        int outN = 0;
        for (int i = 0; i < m; i++) if (keep[i]) outN++;

        if (closed && outN < 4) return null;
        if (!closed && outN < 2) return null;

        var dst = new Coordinate[outN];
        int k = 0;
        for (int i = 0; i < m; i++) if (keep[i]) dst[k++] = new Coordinate(x[i], y[i]);

        if (closed && SignedArea2(dst) == 0) return null;

        st.Out += outN;
        return dst;
    }

    /// <summary>
    /// Iterative Douglas–Peucker. Iterative rather than recursive because the
    /// dataset has a 215,488-vertex polygon in it and a stack overflow in a
    /// benchmark would be an embarrassing way to learn that.
    /// </summary>
    private static void Dp(double[] x, double[] y, int first, int last, double tolSq, bool[] keep)
    {
        var stack = new Stack<(int First, int Last)>();
        stack.Push((first, last));

        while (stack.Count > 0)
        {
            var (a, b) = stack.Pop();
            if (b <= a + 1) continue;

            double ax = x[a], ay = y[a], bx = x[b], by = y[b];
            double dx = bx - ax, dy = by - ay;
            double segLenSq = dx * dx + dy * dy;

            double maxSq = -1;
            int maxAt = -1;

            for (int i = a + 1; i < b; i++)
            {
                double px = x[i] - ax, py = y[i] - ay;
                double dSq;

                if (segLenSq == 0)
                {
                    // Degenerate segment — the ring closed on itself. Distance
                    // to the point, not to the line.
                    dSq = px * px + py * py;
                }
                else
                {
                    double t = (px * dx + py * dy) / segLenSq;
                    if (t <= 0) dSq = px * px + py * py;
                    else if (t >= 1) { double qx = x[i] - bx, qy = y[i] - by; dSq = qx * qx + qy * qy; }
                    else
                    {
                        double cross = px * dy - py * dx;
                        dSq = cross * cross / segLenSq;
                    }
                }

                if (dSq > maxSq) { maxSq = dSq; maxAt = i; }
            }

            if (maxSq > tolSq && maxAt > 0)
            {
                keep[maxAt] = true;
                stack.Push((a, maxAt));
                stack.Push((maxAt, b));
            }
        }
    }

    private static double SignedArea2(Coordinate[] r)
    {
        double s = 0;
        for (int i = 0, j = r.Length - 1; i < r.Length; j = i++)
            s += (r[j].X - r[i].X) * (r[j].Y + r[i].Y);
        return s;
    }
}
