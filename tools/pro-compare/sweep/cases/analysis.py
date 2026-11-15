"""arcpy.analysis against ArcGIS Pro 3.7: one case for each name grapy builds."""
import os

CASES = {}


def case(name):
    def register(function):
        CASES[name] = function
        return function
    return register


def _grid(ap, I):
    """Four 300 m squares over the fixture's square, named z1 (lower left) … z4 (upper right), with a count field."""
    path = os.path.join(I["gdb"], "grid")
    if ap.Exists(path):
        return path
    sr = ap.SpatialReference(I["srid"])
    ap.management.CreateFeatureclass(I["gdb"], "grid", "POLYGON", spatial_reference=sr)
    ap.management.AddField(path, "zone", "TEXT")
    ap.management.AddField(path, "people", "LONG")
    x0, y0 = I["x0"], I["y0"]
    with ap.da.InsertCursor(path, ["zone", "people", "SHAPE@"]) as cursor:
        for n, (dx, dy) in enumerate(((0, 0), (300, 0), (0, 300), (300, 300)), start=1):
            x, y = x0 + dx, y0 + dy
            wkt = f"POLYGON (({x} {y}, {x} {y + 300}, {x + 300} {y + 300}, {x + 300} {y}, {x} {y}))"
            cursor.insertRow((f"z{n}", 100 * n, ap.FromWKT(wkt, sr)))
    return path


def _workspace_summary(ap, workspace):
    """What a tool wrote into a workspace: each feature class or table by name, its row count, its fields (less the
    ones a format adds) and, for polygons, its total area."""
    old = ap.env.workspace
    ap.env.workspace = workspace
    try:
        out = {}
        for name in sorted((ap.ListFeatureClasses() or []) + (ap.ListTables() or [])):
            name = os.path.join(workspace, os.path.basename(name))
            fields = sorted(f.name.lower() for f in ap.ListFields(name)
                            if f.type not in ("OID", "Geometry") and f.name.lower() not in ("shape_length",
                                                                                            "shape_area"))
            count = int(ap.management.GetCount(name)[0])
            area = None
            desc = ap.Describe(name)
            if getattr(desc, "shapeType", None) == "Polygon":
                with ap.da.SearchCursor(name, ["SHAPE@AREA"]) as rows:
                    area = round(sum(r[0] for r in rows), 3)
            out[os.path.splitext(os.path.basename(name))[0].lower()] = {"rows": count, "fields": fields,
                                                                        "area": area}
        return out
    finally:
        ap.env.workspace = old


def _new_gdb(ap, I, O, name):
    import shutil
    folder = O(name, "folder")
    for entry in os.listdir(folder):     # the harness keeps its work folders between runs
        target = os.path.join(folder, entry)
        shutil.rmtree(target) if os.path.isdir(target) else os.remove(target)
    return ap.management.CreateFileGDB(folder, "split")[0]


@case("analysis.ApportionPolygon")
def _(ap, I, O):
    return ap.analysis.ApportionPolygon(I["polys"], "val", _grid(ap, I), O("out"), "AREA")


@case("analysis.AssignWeightsByPairwiseComparison")
def _(ap, I, O):
    return ap.analysis.AssignWeightsByPairwiseComparison(["slope", "landuse", "distance"], O("out"), "ADD_MATRIX",
                                                         "Variables slope landuse 3; slope distance 5; landuse distance 2")


@case("analysis.Buffer")
def _(ap, I, O):
    return ap.analysis.Buffer(I["points"], O("out"), "25 Meters")


@case("analysis.Clip")
def _(ap, I, O):
    return ap.analysis.Clip(I["polys"], I["clip"], O("out"))


@case("analysis.CountOverlappingFeatures")
def _(ap, I, O):
    result = ap.analysis.CountOverlappingFeatures(I["polys"], O("out"), 1, O("table"))
    # ORIG_NAME is the input's path, which differs by machine and format: compare its last part only.
    with ap.da.UpdateCursor(O("table"), ["ORIG_NAME"]) as rows:
        for (name,) in rows:
            rows.updateRow((name.replace("\\", "/").split("/")[-1],))
    return result


@case("analysis.CreateThiessenPolygons")
def _(ap, I, O):
    return ap.analysis.CreateThiessenPolygons(I["points"], O("out"), "ALL")


@case("analysis.Erase")
def _(ap, I, O):
    return ap.analysis.Erase(I["polys"], I["clip"], O("out"))


@case("analysis.Frequency")
def _(ap, I, O):
    return ap.analysis.Frequency(I["points"], O("out"), "cat", "val")


@case("analysis.GenerateNearTable")
def _(ap, I, O):
    return ap.analysis.GenerateNearTable(I["points"], I["lines"], O("out"), None, "LOCATION", "ANGLE", "ALL", 2)


@case("analysis.GenerateOriginDestinationLinks")
def _(ap, I, O):
    return ap.analysis.GenerateOriginDestinationLinks(I["points"], I["polys"], O("out"), num_nearest=2)


@case("analysis.GraphicBuffer")
def _(ap, I, O):
    return ap.analysis.GraphicBuffer(I["polys"], O("out"), "10 Meters")


@case("analysis.Identity")
def _(ap, I, O):
    return ap.analysis.Identity(I["polys"], I["clip"], O("out"))


@case("analysis.Intersect")
def _(ap, I, O):
    return ap.analysis.Intersect([I["polys"], I["clip"]], O("out"))


@case("analysis.MultipleRingBuffer")
def _(ap, I, O):
    return ap.analysis.MultipleRingBuffer(I["points"], O("out"), [10, 20], "Meters")


@case("analysis.Near")
def _(ap, I, O):
    out = ap.management.CopyFeatures(I["points"], O("out"))[0]
    return ap.analysis.Near(out, I["lines"], None, "LOCATION", "ANGLE")


@case("analysis.PairwiseBuffer")
def _(ap, I, O):
    return ap.analysis.PairwiseBuffer(I["points"], O("out"), "25 Meters")


@case("analysis.PairwiseClip")
def _(ap, I, O):
    return ap.analysis.PairwiseClip(I["polys"], I["clip"], O("out"))


@case("analysis.PairwiseDissolve")
def _(ap, I, O):
    return ap.analysis.PairwiseDissolve(I["polys"], O("out"), "cat", [["val", "SUM"]])


@case("analysis.PairwiseErase")
def _(ap, I, O):
    return ap.analysis.PairwiseErase(I["polys"], I["clip"], O("out"))


@case("analysis.PairwiseIntersect")
def _(ap, I, O):
    return ap.analysis.PairwiseIntersect([I["polys"], I["clip"]], O("out"))


@case("analysis.PairwisePolygonNeighbors")
def _(ap, I, O):
    return ap.analysis.PairwisePolygonNeighbors(_grid(ap, I), O("out"), "zone")


@case("analysis.PointDistance")
def _(ap, I, O):
    return ap.analysis.PointDistance(I["points"], I["points"], O("out"), "100 Meters")


@case("analysis.PolygonNeighbors")
def _(ap, I, O):
    return ap.analysis.PolygonNeighbors(_grid(ap, I), O("out"), "zone")


@case("analysis.RemoveOverlapMultiple")
def _(ap, I, O):
    return ap.analysis.RemoveOverlapMultiple(I["polys"], O("out"))


@case("analysis.Select")
def _(ap, I, O):
    return ap.analysis.Select(I["points"], O("out"), "val > 50")


@case("analysis.SpatialJoin")
def _(ap, I, O):
    return ap.analysis.SpatialJoin(I["polys"], I["points"], O("out"))


@case("analysis.Split")
def _(ap, I, O):
    gdb = _new_gdb(ap, I, O, "ws")
    ap.analysis.Split(I["polys"], _grid(ap, I), "zone", gdb)
    return _workspace_summary(ap, gdb)


@case("analysis.SplitByAttributes")
def _(ap, I, O):
    gdb = _new_gdb(ap, I, O, "ws")
    ap.analysis.SplitByAttributes(I["polys"], gdb, "cat")
    return _workspace_summary(ap, gdb)


@case("analysis.Statistics")
def _(ap, I, O):
    return ap.analysis.Statistics(I["points"], O("out"), [["val", "SUM"], ["val", "MEAN"], ["pid", "COUNT"]], "cat")


@case("analysis.SummarizeWithin")
def _(ap, I, O):
    return ap.analysis.SummarizeWithin(_grid(ap, I), I["points"], O("out"), "KEEP_ALL", [["val", "SUM"]])


@case("analysis.SymDiff")
def _(ap, I, O):
    return ap.analysis.SymDiff(I["polys"], I["clip"], O("out"))


@case("analysis.TableSelect")
def _(ap, I, O):
    return ap.analysis.TableSelect(I["table"], O("out"), "val > 5")


@case("analysis.TabulateIntersection")
def _(ap, I, O):
    return ap.analysis.TabulateIntersection(_grid(ap, I), "zone", I["polys"], O("out"), "cat")


@case("analysis.Union")
def _(ap, I, O):
    return ap.analysis.Union([I["polys"], I["clip"]], O("out"))


@case("analysis.Update")
def _(ap, I, O):
    return ap.analysis.Update(I["polys"], I["clip"], O("out"))
