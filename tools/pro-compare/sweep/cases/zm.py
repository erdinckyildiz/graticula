"""Z and M through the general tools against ArcGIS Pro 3.7 (D-412, 2026-11-20).

Every earlier module used 2D fixtures, so none saw grapy's analysis, management and conversion tools write 2D outputs
from 3D inputs (ddd.md 1). These cases feed each general tool inputs with z and m and compare the output's
``hasZ``/``hasM`` (each case's answer) and its shapes with their z and m (compare.py's ``_z_distance`` and
``_m_distance``).

The inputs, built here with ``ap`` so both sides build them the same way: ``zlines`` (PolylineZM: z rising, m the
distance along), ``zpolys`` (PolygonZM, z on a plane, m 0 … n), ``zpoints`` (PointZM) and a 2D ``flat`` polygon
class used as the other side of an overlay.
"""
import os

CASES = {}


def case(name):
    def register(function):
        CASES[name] = function
        return function
    return register


X0, Y0 = 500000.0, 4500000.0


def _zm(ap, I, name, shape, rows):
    path = os.path.join(I["gdb"], name)
    if ap.Exists(path):
        return path
    sr = ap.SpatialReference(I["srid"])
    ap.management.CreateFeatureclass(I["gdb"], name, shape, has_m="ENABLED", has_z="ENABLED", spatial_reference=sr)
    ap.management.AddField(path, "tag", "TEXT")
    with ap.da.InsertCursor(path, ["tag", "SHAPE@"]) as cursor:
        for tag, parts in rows:
            if shape == "POINT":
                x, y, z, m = parts
                geometry = ap.PointGeometry(ap.Point(X0 + x, Y0 + y, z, m), sr, True, True)
            else:
                array = ap.Array([ap.Point(X0 + x, Y0 + y, z, m) for x, y, z, m in parts])
                kind = ap.Polyline if shape == "POLYLINE" else ap.Polygon
                geometry = kind(array, sr, True, True)
            cursor.insertRow((tag, geometry))
    return path


def lines(ap, I):
    return _zm(ap, I, "zlines", "POLYLINE", [
        ("a", [(0, 100, 10.0, 0.0), (200, 100, 30.0, 200.0), (400, 100, 50.0, 400.0)]),
        ("b", [(100, 0, 5.0, 0.0), (100, 300, 35.0, 300.0)]),
    ])


def polys(ap, I):
    def ring(x0, y0, x1, y1, z0):
        pts = [(x0, y0), (x0, y1), (x1, y1), (x1, y0), (x0, y0)]
        # The closing vertex repeats the first's m: Pro stores a ring's end as its start, whatever m it is given
        # (measured 2026-11-20: a closing m of 4 was read back as 0; grapy keeps the 4 — recorded in zm.md).
        return [(x, y, z0 + 0.1 * x + 0.05 * y, float(k % 4)) for k, (x, y) in enumerate(pts)]
    return _zm(ap, I, "zpolys", "POLYGON", [("p", ring(0, 0, 200, 200, 100.0)), ("q", ring(300, 300, 500, 500, 50.0))])


def points(ap, I):
    return _zm(ap, I, "zpoints", "POINT", [("a", (50, 50, 11.0, 1.0)), ("b", (150, 150, 22.0, 2.0)),
                                           ("c", (350, 350, 33.0, 3.0)), ("d", (450, 50, 44.0, 4.0))])


def flat(ap, I):
    """A 2D square over the left half, the other side of the overlays."""
    path = os.path.join(I["gdb"], "zflat")
    if ap.Exists(path):
        return path
    sr = ap.SpatialReference(I["srid"])
    ap.management.CreateFeatureclass(I["gdb"], "zflat", "POLYGON", spatial_reference=sr)
    ap.management.AddField(path, "side", "TEXT")
    with ap.da.InsertCursor(path, ["side", "SHAPE@"]) as cursor:
        wkt = f"POLYGON (({X0 - 50} {Y0 - 50}, {X0 - 50} {Y0 + 250}, {X0 + 150} {Y0 + 250}, {X0 + 150} {Y0 - 50}, " \
              f"{X0 - 50} {Y0 - 50}))"
        cursor.insertRow(("L", ap.FromWKT(wkt, sr)))
    return path


def zm(ap, path):
    d = ap.Describe(path)
    return [bool(d.hasZ), bool(d.hasM)]


def _one(tool, src):
    """A case that runs ``tool(ap, I, input, output)`` on one input and answers the output's [hasZ, hasM]."""
    def run(ap, I, O):
        out = O("out")
        tool(ap, I, src(ap, I), out)
        return zm(ap, out)
    return run


def register(name, tool, *sources):
    for src in sources:
        CASES[f"{name}.{src.__name__}"] = _one(tool, src)


# ---- analysis ---------------------------------------------------------------------------------------------------
register("analysis.Clip", lambda ap, I, a, o: ap.analysis.Clip(a, flat(ap, I), o), lines, polys, points)
register("analysis.Erase", lambda ap, I, a, o: ap.analysis.Erase(a, flat(ap, I), o), lines, polys, points)
register("analysis.PairwiseClip", lambda ap, I, a, o: ap.analysis.PairwiseClip(a, flat(ap, I), o), lines, polys)
register("analysis.PairwiseErase", lambda ap, I, a, o: ap.analysis.PairwiseErase(a, flat(ap, I), o), lines, polys)
register("analysis.Intersect", lambda ap, I, a, o: ap.analysis.Intersect([a, flat(ap, I)], o), lines, polys, points)
register("analysis.PairwiseIntersect", lambda ap, I, a, o: ap.analysis.PairwiseIntersect([a, flat(ap, I)], o),
         lines, polys)
register("analysis.Union", lambda ap, I, a, o: ap.analysis.Union([a, flat(ap, I)], o), polys)
register("analysis.Identity", lambda ap, I, a, o: ap.analysis.Identity(a, flat(ap, I), o), lines, polys)
register("analysis.SymDiff", lambda ap, I, a, o: ap.analysis.SymDiff(a, flat(ap, I), o), polys)
register("analysis.Update", lambda ap, I, a, o: ap.analysis.Update(a, flat(ap, I), o), polys)
register("analysis.Select", lambda ap, I, a, o: ap.analysis.Select(a, o, "tag = 'a' OR tag = 'p'"), lines, polys,
         points)
register("analysis.SpatialJoin", lambda ap, I, a, o: ap.analysis.SpatialJoin(a, flat(ap, I), o), lines, points)
register("analysis.Buffer", lambda ap, I, a, o: ap.analysis.Buffer(a, o, "10 Meters"), lines, points)
register("analysis.PairwiseBuffer", lambda ap, I, a, o: ap.analysis.PairwiseBuffer(a, o, "10 Meters"), lines)
register("analysis.PairwiseDissolve", lambda ap, I, a, o: ap.analysis.PairwiseDissolve(a, o), lines, polys)

# ---- management -------------------------------------------------------------------------------------------------
register("management.CopyFeatures", lambda ap, I, a, o: ap.management.CopyFeatures(a, o), lines, polys, points)
register("management.Dissolve", lambda ap, I, a, o: ap.management.Dissolve(a, o, "tag"), lines, polys)
register("management.Merge", lambda ap, I, a, o: ap.management.Merge(
    [a, ap.management.CopyFeatures(a, o + "_twin")[0]], o), lines, polys, points)
register("management.Sort", lambda ap, I, a, o: ap.management.Sort(a, o, [["tag", "DESCENDING"]]), lines, polys,
         points)
register("management.MultipartToSinglepart", lambda ap, I, a, o: ap.management.MultipartToSinglepart(a, o), lines,
         polys)
register("management.FeatureToPoint", lambda ap, I, a, o: ap.management.FeatureToPoint(a, o), lines, polys)
register("management.FeatureVerticesToPoints",
         lambda ap, I, a, o: ap.management.FeatureVerticesToPoints(a, o, "ALL"), lines, polys)
register("management.FeatureToLine", lambda ap, I, a, o: ap.management.FeatureToLine(a, o), lines, polys)
register("management.FeatureToPolygon", lambda ap, I, a, o: ap.management.FeatureToPolygon(a, o), polys)
register("management.PolygonToLine", lambda ap, I, a, o: ap.management.PolygonToLine(a, o), polys)
register("management.SplitLine", lambda ap, I, a, o: ap.management.SplitLine(a, o), lines, polys)
register("management.Project", lambda ap, I, a, o: ap.management.Project(a, o, ap.SpatialReference(32636)), lines,
         points)
register("management.Eliminate", lambda ap, I, a, o: ap.management.Eliminate(
    ap.management.SelectLayerByAttribute(ap.management.MakeFeatureLayer(a, "zelim")[0], "NEW_SELECTION",
                                         "tag = 'q'")[0], o), polys)
register("management.MinimumBoundingGeometry",
         lambda ap, I, a, o: ap.management.MinimumBoundingGeometry(a, o, "ENVELOPE"), lines, polys)
register("management.ExportFeatures", lambda ap, I, a, o: ap.conversion.ExportFeatures(a, o), lines, polys, points)


@case("management.Append.lines")
def append_lines(ap, I, O):
    out = O("out")
    ap.management.CopyFeatures(lines(ap, I), out)
    ap.management.Append(lines(ap, I), out, "NO_TEST")
    return zm(ap, out)


@case("management.Densify.lines")
def densify_lines(ap, I, O):
    out = O("out")
    ap.management.CopyFeatures(lines(ap, I), out)
    ap.edit.Densify(out, "DISTANCE", "50 Meters")
    return zm(ap, out)


@case("management.SplitLineAtPoint.lines")
def split_at_point(ap, I, O):
    out = O("out")
    ap.management.SplitLineAtPoint(lines(ap, I), points(ap, I), out, "60 Meters")
    return zm(ap, out)


@case("analysis.Clip.lines_into_2d")
def clip_into_2d(ap, I, O):
    """A 2D clip of a 3D input into an output whose z the environment turns off: outputZFlag Disabled."""
    out = O("out")
    old = ap.env.outputZFlag
    ap.env.outputZFlag = "Disabled"
    try:
        ap.analysis.Clip(lines(ap, I), flat(ap, I), out)
    finally:
        ap.env.outputZFlag = old
    return zm(ap, out)


# ---- more tools that make shapes rather than cut them (what Pro writes them with is measured, not assumed) ------
register("analysis.GraphicBuffer", lambda ap, I, a, o: ap.analysis.GraphicBuffer(a, o, "10 Meters"), lines)
register("analysis.MultipleRingBuffer",
         lambda ap, I, a, o: ap.analysis.MultipleRingBuffer(a, o, [10, 20], "Meters"), points)
register("analysis.CreateThiessenPolygons", lambda ap, I, a, o: ap.analysis.CreateThiessenPolygons(a, o), points)
register("analysis.CountOverlappingFeatures", lambda ap, I, a, o: ap.analysis.CountOverlappingFeatures(a, o), polys)
register("management.FeatureEnvelopeToPolygon",
         lambda ap, I, a, o: ap.management.FeatureEnvelopeToPolygon(a, o), lines, polys)
register("management.GeneratePointsAlongLines",
         lambda ap, I, a, o: ap.management.GeneratePointsAlongLines(a, o, "DISTANCE", "100 Meters"), lines)
register("management.PointsToLine", lambda ap, I, a, o: ap.management.PointsToLine(a, o), points)
register("cartography.SimplifyLine",
         lambda ap, I, a, o: ap.cartography.SimplifyLine(a, o, "POINT_REMOVE", "1 Meters"), lines)
register("cartography.SmoothLine", lambda ap, I, a, o: ap.cartography.SmoothLine(a, o, "PAEK", "50 Meters"), lines)
register("conversion.FeatureClassToFeatureClass",
         lambda ap, I, a, o: ap.conversion.FeatureClassToFeatureClass(a, os.path.dirname(o), os.path.basename(o)),
         lines, points)


# ---- a 2D input with a 3D second input: which inputs "Same As Input" reads -------------------------------------
@case("analysis.Clip.flat_by_3d")
def clip_flat_by_3d(ap, I, O):
    out = O("out")
    ap.analysis.Clip(flat(ap, I), polys(ap, I), out)
    return zm(ap, out)


@case("analysis.Intersect.flat_first")
def intersect_flat_first(ap, I, O):
    out = O("out")
    ap.analysis.Intersect([flat(ap, I), polys(ap, I)], out)
    return zm(ap, out)


@case("analysis.Erase.flat_by_3d")
def erase_flat_by_3d(ap, I, O):
    out = O("out")
    ap.analysis.Erase(flat(ap, I), polys(ap, I), out)
    return zm(ap, out)


@case("management.CopyFeatures.flat_m_enabled")
def copy_flat_m_enabled(ap, I, O):
    out = O("out")
    old = ap.env.outputMFlag
    ap.env.outputMFlag = "Enabled"
    try:
        ap.management.CopyFeatures(flat(ap, I), out)
    finally:
        ap.env.outputMFlag = old
    return zm(ap, out)
