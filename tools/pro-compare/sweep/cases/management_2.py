"""arcpy.management, the second half of the built names in sorted order (EnableCOGO to the last), against ArcGIS Pro 3.7.

A tool that writes a dataset writes it through ``O``; a tool that changes a dataset in place changes a copy made here
(CopyFeatures / CopyRows to an ``O`` output), and a tool that answers a value returns ``flat(...)``. Inputs beyond the
shared fixtures (a domain, subtypes, attachments, a LAS file, photos, a mosaic dataset, a topology) are built here with
``ap``, so both sides build them the same way.
"""
import os
import struct

from cases.arcpy_top import _r, flat
from cases.da import _attrs, _contingent, _domains, _gdb

CASES = {}


def case(name):
    def register(function):
        CASES[name] = function
        return function
    return register


def _copy(ap, I, O, source="points", name="out"):
    out = O(name)
    if source == "table":
        ap.management.CopyRows(I["table"], out)
    else:
        ap.management.CopyFeatures(I[source], out)
    return out


def _fields(ap, path, skip=("objectid", "fid", "oid", "shape", "geom", "shape_length", "shape_area")):
    return [[f.name, f.type] for f in ap.ListFields(path) if f.name.lower() not in skip]


def _rows(ap, path, fields, where=None):
    with ap.da.SearchCursor(path, fields, where) as c:
        return [[_r(v, 6) if isinstance(v, float) else v for v in row] for row in c]


def _count(ap, path):
    return int(ap.management.GetCount(path)[0])


def _ext(e):
    return [_r(e.XMin, 3), _r(e.YMin, 3), _r(e.XMax, 3), _r(e.YMax, 3)]


def _table(ap, O, name, fields, rows):
    """A table of the case's own in the shared workspace, written through O."""
    path = O(name)
    folder, base = os.path.split(path)
    ap.management.CreateTable(folder, base)
    for field, kind in fields:
        ap.management.AddField(path, field, kind)
    with ap.da.InsertCursor(path, [f for f, _ in fields]) as c:
        for row in rows:
            c.insertRow(row)
    return path


def _fresh(O, name):
    """An empty folder of the case's own, whatever an earlier run left in it."""
    import shutil
    folder = O(name, "folder")
    shutil.rmtree(folder, ignore_errors=True)
    os.makedirs(folder)
    return folder


def _refusal(function, *args):
    """"ok", or the ERROR code a refusal names — for a case whose documented answer here is a refusal."""
    import re
    try:
        function(*args)
        return "ok"
    except Exception as error:  # noqa: BLE001
        found = re.search(r"ERROR (\d{6})", str(error))
        return found.group(0) if found else f"raised {type(error).__name__}"


def _file(path, text):
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)
    return path


# ---- features -----------------------------------------------------------------------------------------------------


@case("management.FeatureEnvelopeToPolygon")
def _(ap, I, O):
    ap.management.FeatureEnvelopeToPolygon(I["lines"], O("lines"))
    ap.management.FeatureEnvelopeToPolygon(I["polys"], O("polys"))


@case("management.FeatureToLine")
def _(ap, I, O):
    ap.management.FeatureToLine(I["polys"], O("polys"))
    ap.management.FeatureToLine(I["lines"], O("lines"), None, "NO_ATTRIBUTES")


@case("management.FeatureToPoint")
def _(ap, I, O):
    ap.management.FeatureToPoint(I["polys"], O("centroid"))
    ap.management.FeatureToPoint(I["polys"], O("inside"), "INSIDE")
    ap.management.FeatureToPoint(I["lines"], O("lines"))


@case("management.FeatureToPolygon")
def _(ap, I, O):
    ap.management.FeatureToPolygon(I["lines"], O("lines"))
    ap.management.FeatureToPolygon(I["polys"], O("polys"))


@case("management.FeatureVerticesToPoints")
def _(ap, I, O):
    for option in ("ALL", "MID", "START", "END", "BOTH_ENDS", "DANGLE"):
        ap.management.FeatureVerticesToPoints(I["lines"], O(option.lower()), option)
    ap.management.FeatureVerticesToPoints(I["polys"], O("polys"), "ALL")


@case("management.FindIdentical")
def _(ap, I, O):
    ap.management.FindIdentical(I["polys"], O("cat"), ["cat"])
    ap.management.FindIdentical(I["polys"], O("only"), ["cat"], output_record_option="ONLY_DUPLICATES")
    ap.management.FindIdentical(I["polys"], O("shape"), ["Shape", "cat"])


@case("management.GeneratePointsAlongLines")
def _(ap, I, O):
    ap.management.GeneratePointsAlongLines(I["lines"], O("dist"), "DISTANCE", "100 Meters")
    ap.management.GeneratePointsAlongLines(I["lines"], O("pct"), "PERCENTAGE", Percentage=25,
                                           Include_End_Points="END_POINTS")


@case("management.GenerateTessellation")
def _(ap, I, O):
    sr = ap.SpatialReference(I["srid"])
    extent = ap.Extent(I["x0"], I["y0"], I["x0"] + 600, I["y0"] + 600)
    ap.management.GenerateTessellation(O("hex"), extent, "HEXAGON", "10000 SquareMeters", sr)
    ap.management.GenerateTessellation(O("square"), extent, "SQUARE", "10000 SquareMeters", sr)


@case("management.GenerateTransectsAlongLines")
def _(ap, I, O):
    ap.management.GenerateTransectsAlongLines(I["lines"], O("out"), "100 Meters", "40 Meters")
    ap.management.GenerateTransectsAlongLines(I["lines"], O("ends"), "150 Meters", "20 Meters", "END_POINTS")


@case("management.GeodeticDensify")
def _(ap, I, O):
    ap.management.GeodeticDensify(I["lines"], O("geodesic"), "GEODESIC", "50 Meters")
    ap.management.GeodeticDensify(I["polys"], O("polys"), "GEODESIC", "40 Meters")


@case("management.GetCount")
def _(ap, I, O):
    return flat({"points": ap.management.GetCount(I["points"])[0], "table": ap.management.GetCount(I["table"])[0],
                 "layer": ap.management.GetCount(ap.management.MakeFeatureLayer(I["polys"], "gc_lyr",
                                                                                "val > 4")[0])[0]})


@case("management.Integrate")
def _(ap, I, O):
    out = _copy(ap, I, O, "points")
    ap.management.Integrate(out, "40 Meters")


@case("management.JoinField")
def _(ap, I, O):
    out = _copy(ap, I, O, "polys")
    ap.management.JoinField(out, "gid", I["table"], "rid", ["val", "cat"])
    whole = _copy(ap, I, O, "points", "all")
    ap.management.JoinField(whole, "pid", I["table"], "rid")


@case("management.MakeFeatureLayer")
def _(ap, I, O):
    layer = ap.management.MakeFeatureLayer(I["points"], "mfl_lyr", "val > 50")[0]
    ap.management.CopyFeatures(layer, O("out"))
    return flat({"count": _count(ap, layer), "name": getattr(layer, "name", str(layer))})


@case("management.MakeTableView")
def _(ap, I, O):
    view = ap.management.MakeTableView(I["table"], "mtv_view", "val > 5")[0]
    ap.management.CopyRows(view, O("out"))
    return flat({"count": _count(ap, view)})


@case("management.Merge")
def _(ap, I, O):
    ap.management.Merge([I["polys"], I["clip"]], O("out"))
    twin = _copy(ap, I, O, "points", "twin")
    gdb = _gdb(ap, O, "merged")
    source = os.path.join(gdb, "source")
    ap.management.Merge([I["points"], twin], source, add_source="ADD_SOURCE_INFO")
    fields = [f.name for f in ap.ListFields(source) if f.type not in ("OID", "Geometry")]
    rows = sorted([os.path.basename(str(r[0]).replace("\\", "/")), r[1]] for r in
                  ap.da.SearchCursor(source, ["MERGE_SRC", "pid"]))
    return flat({"fields": fields, "rows": rows})


@case("management.MinimumBoundingGeometry")
def _(ap, I, O):
    ap.management.MinimumBoundingGeometry(I["polys"], O("rect_area"), "RECTANGLE_BY_AREA", mbg_fields_option="MBG_FIELDS")
    ap.management.MinimumBoundingGeometry(I["polys"], O("rect_width"), "RECTANGLE_BY_WIDTH",
                                          mbg_fields_option="MBG_FIELDS")
    ap.management.MinimumBoundingGeometry(I["polys"], O("hull"), "CONVEX_HULL")
    ap.management.MinimumBoundingGeometry(I["polys"], O("circle"), "CIRCLE", mbg_fields_option="MBG_FIELDS")
    ap.management.MinimumBoundingGeometry(I["polys"], O("envelope"), "ENVELOPE", mbg_fields_option="MBG_FIELDS")
    ap.management.MinimumBoundingGeometry(I["points"], O("grouped"), "CONVEX_HULL", "LIST", "cat")
    ap.management.MinimumBoundingGeometry(I["points"], O("all"), "ENVELOPE", "ALL")


@case("management.MultipartToSinglepart")
def _(ap, I, O):
    multi = O("multi")
    ap.management.Dissolve(I["polys"], multi, "cat")
    ap.management.MultipartToSinglepart(multi, O("out"))


@case("management.PointsToLine")
def _(ap, I, O):
    ap.management.PointsToLine(I["points"], O("out"), "cat", "pid")
    ap.management.PointsToLine(I["points"], O("closed"), "cat", "pid", "CLOSE")
    ap.management.PointsToLine(I["points"], O("two"), None, "pid", Line_Construction_Method="TWO_POINT")


@case("management.PolygonToLine")
def _(ap, I, O):
    apart = ap.management.MakeFeatureLayer(I["polys"], "ptl_apart", "gid >= 3")[0]
    ap.management.PolygonToLine(apart, O("neighbors"))
    ap.management.PolygonToLine(I["polys"], O("plain"), "IGNORE_NEIGHBORS")


@case("management.Project")
def _(ap, I, O):
    ap.management.Project(I["points"], O("wgs"), ap.SpatialReference(4326))
    ap.management.Project(I["polys"], O("utm36"), ap.SpatialReference(32636))


@case("management.RecalculateFeatureClassExtent")
def _(ap, I, O):
    out = O("out")
    ap.management.CopyFeatures(I["polys"], out)
    with ap.da.UpdateCursor(out, ["gid"]) as c:
        for row in c:
            if row[0] in (3, 4, 5):
                c.deleteRow()
    before = _ext(ap.Describe(out).extent)
    ap.management.RecalculateFeatureClassExtent(out)
    return flat({"before": before, "after": _ext(ap.Describe(out).extent)})


@case("management.RepairGeometry")
def _(ap, I, O):
    out = O("out")
    folder, base = os.path.split(out)
    ap.management.CreateFeatureclass(folder, base, "POLYGON", spatial_reference=ap.SpatialReference(I["srid"]))
    ap.management.AddField(out, "k", "LONG")
    x, y = I["x0"], I["y0"]
    bowtie = f"POLYGON (({x} {y}, {x + 100} {y + 100}, {x + 100} {y}, {x} {y + 100}, {x} {y}))"
    square = f"POLYGON (({x + 200} {y}, {x + 200} {y + 50}, {x + 250} {y + 50}, {x + 250} {y}, {x + 200} {y}))"
    with ap.da.InsertCursor(out, ["k", "SHAPE@WKT"]) as c:
        c.insertRow((1, bowtie))
        c.insertRow((2, square))
        c.insertRow((3, None))
    ap.management.RepairGeometry(out)


@case("management.SplitLine")
def _(ap, I, O):
    ap.management.SplitLine(I["lines"], O("lines"))
    ap.management.SplitLine(I["polys"], O("polys"))


@case("management.SplitLineAtPoint")
def _(ap, I, O):
    ap.management.SplitLineAtPoint(I["lines"], I["points"], O("out"), "20 Meters")


@case("management.UnsplitLine")
def _(ap, I, O):
    split = O("split")
    ap.management.SplitLine(I["lines"], split)
    ap.management.UnsplitLine(split, O("kind"), "kind")
    ap.management.UnsplitLine(split, O("stats"), "kind", [["lid", "COUNT"], ["lid", "MAX"]])


@case("management.SubsetFeatures")
def _(ap, I, O):
    ap.management.SubsetFeatures(I["points"], O("train"), O("test"), 100, "PERCENTAGE_OF_INPUT")
    gdb = _gdb(ap, O, "subset")
    a, b = os.path.join(gdb, "a"), os.path.join(gdb, "b")
    ap.management.SubsetFeatures(I["points"], a, b, 40, "PERCENTAGE_OF_INPUT")
    c, d = os.path.join(gdb, "c"), os.path.join(gdb, "d")
    ap.management.SubsetFeatures(I["points"], c, d, 7, "ABSOLUTE_VALUE")
    return flat({"pct": [_count(ap, a), _count(ap, b)], "abs": [_count(ap, c), _count(ap, d)]})


@case("management.Sort")
def _(ap, I, O):
    out = O("val")
    ap.management.Sort(I["points"], out, [["val", "DESCENDING"]])
    two = O("two")
    ap.management.Sort(I["points"], two, [["cat", "ASCENDING"], ["val", "ASCENDING"]])
    return flat({"val": [r[0] for r in _rows(ap, out, ["pid"])], "two": [r[0] for r in _rows(ap, two, ["pid"])]})


@case("management.XYTableToPoint")
def _(ap, I, O):
    t = _table(ap, O, "xy", [("x", "DOUBLE"), ("y", "DOUBLE"), ("z", "DOUBLE"), ("name", "TEXT")],
               [(I["x0"] + 10 * i, I["y0"] + 20 * i, 5.0 * i, f"p{i}") for i in range(1, 6)])
    ap.management.XYTableToPoint(t, O("out"), "x", "y", None, ap.SpatialReference(I["srid"]))
    ap.management.XYTableToPoint(t, O("z"), "x", "y", "z", ap.SpatialReference(I["srid"]))


@case("management.XYToLine")
def _(ap, I, O):
    t = _table(ap, O, "xy", [("x1", "DOUBLE"), ("y1", "DOUBLE"), ("x2", "DOUBLE"), ("y2", "DOUBLE"), ("k", "LONG")],
               [(I["x0"], I["y0"], I["x0"] + 300, I["y0"] + 400, 1), (I["x0"] + 100, I["y0"], I["x0"] + 100,
                                                                     I["y0"] + 500, 2)])
    sr = ap.SpatialReference(I["srid"])
    ap.management.XYToLine(t, O("planar"), "x1", "y1", "x2", "y2", "PLANAR", "k", sr)
    ap.management.XYToLine(t, O("attributes"), "x1", "y1", "x2", "y2", "PLANAR", "k", sr, "ATTRIBUTES")


@case("management.TableToEllipse")
def _(ap, I, O):
    t = _table(ap, O, "ell", [("x", "DOUBLE"), ("y", "DOUBLE"), ("major", "DOUBLE"), ("minor", "DOUBLE"),
                              ("az", "DOUBLE"), ("k", "LONG")],
               [(I["x0"] + 200, I["y0"] + 200, 100.0, 50.0, 30.0, 1), (I["x0"] + 400, I["y0"] + 400, 60.0, 60.0,
                                                                      0.0, 2)])
    ap.management.TableToEllipse(t, O("out"), "x", "y", "major", "minor", "METERS", "az", "DEGREES", "k",
                                 ap.SpatialReference(I["srid"]))


# ---- tables -------------------------------------------------------------------------------------------------------


@case("management.EncodeField")
def _(ap, I, O):
    out = _copy(ap, I, O, "table")
    ap.management.EncodeField(out, "cat", "ONEHOT")
    cold = _copy(ap, I, O, "table", "cold")
    ap.management.EncodeField(cold, "cat", "ONECOLD")


@case("management.PivotTable")
def _(ap, I, O):
    ap.management.PivotTable(I["table"], ["cat"], "rid", "val", O("out"))


@case("management.ReclassifyField")
def _(ap, I, O):
    out = _copy(ap, I, O, "table")
    ap.management.ReclassifyField(out, "val", "EQUAL_INTERVAL", 3)
    ap.management.ReclassifyField(out, "rid", "QUANTILE", 4, output_field_name="quant")


@case("management.StandardizeField")
def _(ap, I, O):
    out = _copy(ap, I, O, "table")
    ap.management.StandardizeField(out, ["val"], "Z-SCORE")
    ap.management.StandardizeField(out, ["rid"], "MIN-MAX", 0, 10)


@case("management.TransformField")
def _(ap, I, O):
    out = _copy(ap, I, O, "table")
    for method in ("LOG", "SQRT", "INVX", "BOX-COX"):
        ap.management.TransformField(out, ["val"], method)


@case("management.TransposeFields")
def _(ap, I, O):
    t = _table(ap, O, "wide", [("k", "LONG"), ("y2020", "DOUBLE"), ("y2021", "DOUBLE")],
               [(1, 10.0, 11.0), (2, 20.0, 22.0)])
    ap.management.TransposeFields(t, [["y2020", "2020"], ["y2021", "2021"]], O("out"), "year", "value", ["k"])


@case("management.TruncateTable")
def _(ap, I, O):
    out = _copy(ap, I, O, "table")
    ap.management.TruncateTable(out)
    return flat({"count": _count(ap, out)})


@case("management.TableCompare")
def _(ap, I, O):
    same = _copy(ap, I, O, "table", "same")
    changed = _copy(ap, I, O, "table", "changed")
    with ap.da.UpdateCursor(changed, ["val"], "rid = 3") as c:
        for row in c:
            c.updateRow([99.0])
    a = ap.management.TableCompare(I["table"], same, "rid")
    b = ap.management.TableCompare(I["table"], changed, "rid", "ALL", None, None, None, "CONTINUE_COMPARE")
    return flat({"same": a.getOutput(1), "changed": b.getOutput(1)})


@case("management.FeatureCompare")
def _(ap, I, O):
    same = _copy(ap, I, O, "points", "same")
    moved = _copy(ap, I, O, "points", "moved")
    with ap.da.UpdateCursor(moved, ["SHAPE@XY"], "pid = 2") as c:
        for row in c:
            c.updateRow([(row[0][0] + 5, row[0][1])])
    a = ap.management.FeatureCompare(I["points"], same, "pid")
    b = ap.management.FeatureCompare(I["points"], moved, "pid", "ALL", None, "0.001 Meters", continue_compare="CONTINUE_COMPARE")
    return flat({"same": a.getOutput(1), "moved": b.getOutput(1)})


@case("management.FileCompare")
def _(ap, I, O):
    folder = _fresh(O, "files")
    a = _file(os.path.join(folder, "a.txt"), "one\ntwo\nthree\n")
    b = _file(os.path.join(folder, "b.txt"), "one\ntwo\nthree\n")
    c = _file(os.path.join(folder, "c.txt"), "one\nTWO\nthree\n")
    return flat({"same": ap.management.FileCompare(a, b, "ASCII").getOutput(1),
                 "differ": ap.management.FileCompare(a, c, "ASCII", "CONTINUE_COMPARE").getOutput(1)})


# ---- layers and views ---------------------------------------------------------------------------------------------


@case("management.SelectLayerByAttribute")
def _(ap, I, O):
    layer = ap.management.MakeFeatureLayer(I["points"], "sba_lyr")[0]
    out = {}
    ap.management.SelectLayerByAttribute(layer, "NEW_SELECTION", "val > 50")
    out["new"] = _count(ap, layer)
    ap.management.SelectLayerByAttribute(layer, "SUBSET_SELECTION", "cat = 'a'")
    out["subset"] = _count(ap, layer)
    ap.management.SelectLayerByAttribute(layer, "ADD_TO_SELECTION", "pid < 4")
    out["add"] = _count(ap, layer)
    ap.management.SelectLayerByAttribute(layer, "REMOVE_FROM_SELECTION", "pid = 1")
    out["remove"] = _count(ap, layer)
    ap.management.SelectLayerByAttribute(layer, "SWITCH_SELECTION")
    out["switch"] = _count(ap, layer)
    ap.management.SelectLayerByAttribute(layer, "NEW_SELECTION", "val > 50", "INVERT")
    out["invert"] = _count(ap, layer)
    ap.management.SelectLayerByAttribute(layer, "CLEAR_SELECTION")
    out["clear"] = _count(ap, layer)
    return flat(out)


@case("management.SelectLayerByLocation")
def _(ap, I, O):
    layer = ap.management.MakeFeatureLayer(I["points"], "sbl_lyr")[0]
    out = {}
    for relation, kwargs in (("INTERSECT", {}), ("WITHIN_A_DISTANCE", {"search_distance": "30 Meters"}),
                             ("COMPLETELY_WITHIN", {}), ("HAVE_THEIR_CENTER_IN", {})):
        ap.management.SelectLayerByLocation(layer, relation, I["polys"], **kwargs)
        out[relation] = _count(ap, layer)
    ap.management.SelectLayerByLocation(layer, "INTERSECT", I["polys"], invert_spatial_relationship="INVERT")
    out["invert"] = _count(ap, layer)
    ap.management.SelectLayerByLocation(layer, "INTERSECT", I["clip"], selection_type="SUBSET_SELECTION")
    out["subset"] = _count(ap, layer)
    lines = ap.management.MakeFeatureLayer(I["lines"], "sbl_lines")[0]
    ap.management.SelectLayerByLocation(lines, "SHARE_A_LINE_SEGMENT_WITH", I["lines"])
    out["share"] = _count(ap, lines)
    return flat(out)


@case("management.RemoveJoin")
def _(ap, I, O):
    layer = ap.management.MakeFeatureLayer(I["polys"], "rj_lyr")[0]
    ap.management.AddJoin(layer, "gid", I["table"], "rid")
    own = ("shape_length", "shape_area")
    joined = len([f for f in ap.ListFields(layer) if f.name.split(".")[-1].lower() not in own])
    ap.management.RemoveJoin(layer)
    return flat({"joined": joined, "after": [f.name for f in ap.ListFields(layer)
                                             if f.type not in ("OID", "Geometry") and f.name.lower() not in own]})


# ---- schema: indexes, domains, subtypes ---------------------------------------------------------------------------


@case("management.RemoveIndex")
def _(ap, I, O):
    out = _copy(ap, I, O, "points")
    ap.management.AddIndex(out, ["val"], "val_idx")
    ap.management.AddIndex(out, ["pid"], "pid_idx")
    before = sorted(i.name for i in ap.ListIndexes(out) if i.name.endswith("_idx"))
    ap.management.RemoveIndex(out, ["val_idx"])
    return flat({"before": before, "after": sorted(i.name for i in ap.ListIndexes(out) if i.name.endswith("_idx"))})


@case("management.RemoveSpatialIndex")
def _(ap, I, O):
    out = _copy(ap, I, O, "points")
    before = ap.Describe(out).hasSpatialIndex
    ap.management.RemoveSpatialIndex(out)
    return flat({"before": before, "after": ap.Describe(out).hasSpatialIndex})


@case("management.RemoveDomainFromField")
def _(ap, I, O):
    gdb = _domains(ap, O)
    fc = os.path.join(gdb, "lines")
    ap.management.CopyFeatures(I["lines"], fc)
    ap.management.AddField(fc, "kcode", "SHORT")
    ap.management.AssignDomainToField(fc, "kcode", "kinds")
    before = [f.domain for f in ap.ListFields(fc, "kcode")]
    ap.management.RemoveDomainFromField(fc, "kcode")
    return flat({"before": before, "after": [f.domain for f in ap.ListFields(fc, "kcode")]})


@case("management.SetValueForRangeDomain")
def _(ap, I, O):
    gdb = _domains(ap, O)
    ap.management.SetValueForRangeDomain(gdb, "speed", 10, 90)
    d = [x for x in ap.da.ListDomains(gdb) if x.name == "speed"][0]
    return flat({"range": list(d.range)})


@case("management.SortCodedValueDomain")
def _(ap, I, O):
    gdb = _domains(ap, O)
    out = {}
    for by, order in (("DESCRIPTION", "DESCENDING"), ("CODE", "DESCENDING"), ("DESCRIPTION", "ASCENDING")):
        ap.management.SortCodedValueDomain(gdb, "kinds", by, order)
        d = [x for x in ap.da.ListDomains(gdb) if x.name == "kinds"][0]
        out[f"{by}_{order}"] = [[k, v] for k, v in d.codedValues.items()]
    return flat(out)


@case("management.TableToDomain")
def _(ap, I, O):
    gdb = _domains(ap, O)
    t = _table(ap, O, "codes", [("code", "SHORT"), ("label", "TEXT")], [(10, "ten"), (20, "twenty")])
    ap.management.TableToDomain(t, "code", "label", gdb, "fromtable", "From a table")
    ap.management.TableToDomain(t, "code", "label", gdb, "kinds", update_option="APPEND")
    out = {}
    for d in sorted(ap.da.ListDomains(gdb), key=lambda d: d.name):
        if d.name in ("fromtable", "kinds"):
            out[d.name] = {"description": d.description, "type": d.type, "codes": sorted(
                [str(k), v] for k, v in d.codedValues.items())}
    return flat(out)


def _subtyped(ap, I, O):
    gdb = _domains(ap, O)
    fc = os.path.join(gdb, "lines")
    ap.management.CopyFeatures(I["lines"], fc)
    ap.management.AddField(fc, "stype", "SHORT")
    ap.management.SetSubtypeField(fc, "stype")
    ap.management.AddSubtype(fc, 1, "Major")
    ap.management.AddSubtype(fc, 2, "Minor")
    ap.management.AddSubtype(fc, 3, "Other")
    return fc


def _subs(ap, fc):
    return {str(k): [v["Name"], v["Default"], v["SubtypeField"]] for k, v in sorted(ap.da.ListSubtypes(fc).items())}


@case("management.SetSubtypeField")
def _(ap, I, O):
    fc = _subtyped(ap, I, O)
    out = {"set": _subs(ap, fc), "describe": ap.Describe(fc).subtypeFieldName}
    ap.management.SetSubtypeField(fc, clear_value=True)
    out["cleared"] = _subs(ap, fc)
    return flat(out)


@case("management.SetDefaultSubtype")
def _(ap, I, O):
    fc = _subtyped(ap, I, O)
    ap.management.SetDefaultSubtype(fc, 3)
    return flat({"subs": _subs(ap, fc), "default": ap.Describe(fc).defaultSubtypeCode})


@case("management.RemoveSubtype")
def _(ap, I, O):
    fc = _subtyped(ap, I, O)
    ap.management.RemoveSubtype(fc, ["2"])
    return flat({"subs": _subs(ap, fc)})


@case("management.Rename")
def _(ap, I, O):
    gdb = _gdb(ap, O, "ren")
    a = os.path.join(gdb, "first")
    ap.management.CopyFeatures(I["points"], a)
    b = os.path.join(gdb, "second")
    ap.management.Rename(a, b)
    return flat({"old": bool(ap.Exists(a)), "new": bool(ap.Exists(b)), "count": _count(ap, b)})


# ---- rasters ------------------------------------------------------------------------------------------------------


@case("management.Flip")
def _(ap, I, O):
    ap.management.Flip(I["dem"], O("out", "raster"))


@case("management.Mirror")
def _(ap, I, O):
    ap.management.Mirror(I["dem"], O("out", "raster"))


@case("management.Shift")
def _(ap, I, O):
    ap.management.Shift(I["dem"], O("out", "raster"), 15, 25)


@case("management.Resample")
def _(ap, I, O):
    ap.management.Resample(I["dem"], O("nearest", "raster"), "15 15", "NEAREST")
    ap.management.Resample(I["zones"], O("zones", "raster"), "30", "NEAREST")


@case("management.GetCellValue")
def _(ap, I, O):
    x, y = I["x0"], I["y0"]
    return flat({"dem": ap.management.GetCellValue(I["dem"], f"{x + 105} {y + 255}")[0],
                 "zones": ap.management.GetCellValue(I["zones"], f"{x + 455} {y + 55}")[0],
                 "band": ap.management.GetCellValue(I["dem"], f"{x + 5} {y + 5}", "1")[0]})


@case("management.GetRasterProperties")
def _(ap, I, O):
    out = {}
    for prop in ("MINIMUM", "MAXIMUM", "MEAN", "STD", "UNIQUEVALUECOUNT", "TOP", "LEFT", "RIGHT", "BOTTOM",
                 "CELLSIZEX", "CELLSIZEY", "VALUETYPE", "COLUMNCOUNT", "ROWCOUNT", "BANDCOUNT", "ANYNODATA",
                 "ALLNODATA", "SENSORNAME", "PRODUCTNAME", "ACQUISITIONDATE", "SOURCETYPE", "CLOUDCOVER",
                 "SUNAZIMUTH", "SUNELEVATION", "SENSORAZIMUTH", "SENSORELEVATION", "OFFNADIR", "WAVELENGTH"):
        try:
            value = ap.management.GetRasterProperties(I["dem"], prop)[0]
            try:
                value = round(float(value), 4)
            except ValueError:
                pass
        except Exception as error:  # noqa: BLE001 — a property the raster does not carry is answered by refusal
            value = f"raised {type(error).__name__}"
        out[prop] = value
    out["zones_unique"] = ap.management.GetRasterProperties(I["zones"], "UNIQUEVALUECOUNT")[0]
    return flat(out)


@case("management.ExportRasterWorldFile")
def _(ap, I, O):
    out = O("copy", "raster")
    ap.management.CopyRaster(I["dem"], out)
    ap.management.ExportRasterWorldFile(out)
    world = os.path.splitext(out)[0] + ".tfw"
    return flat({"lines": [_r(float(v), 6) for v in open(world).read().split()]})


def _small_raster(ap, I, O, name, value, x, y, cols, rows):
    import numpy as np
    path = O(name, "raster")
    ap.env.outputCoordinateSystem = ap.SpatialReference(I["srid"])
    ap.NumPyArrayToRaster(np.full((rows, cols), value, dtype="float32"), ap.Point(I["x0"] + x, I["y0"] + y),
                          I["cell"], I["cell"]).save(path)
    ap.env.outputCoordinateSystem = None
    return path


@case("management.Mosaic")
def _(ap, I, O):
    target = O("target", "raster")
    ap.management.CopyRaster(I["dem"], target)
    patch = _small_raster(ap, I, O, "patch", 500.0, 100, 100, 10, 5)
    ap.management.Mosaic([patch], target, "LAST")
    blend = O("blend", "raster")
    ap.management.CopyRaster(I["dem"], blend)
    ap.management.Mosaic([patch], blend, "MAXIMUM")


@case("management.MosaicToNewRaster")
def _(ap, I, O):
    a = _small_raster(ap, I, O, "a", 1.0, 0, 0, 10, 10)
    b = _small_raster(ap, I, O, "b", 2.0, 50, 50, 10, 10)
    folder, name = os.path.split(O("out", "raster"))
    ap.management.MosaicToNewRaster([a, b], folder, name, ap.SpatialReference(I["srid"]), "32_BIT_FLOAT", None, 1,
                                    "LAST")
    folder2, name2 = os.path.split(O("mean", "raster"))
    ap.management.MosaicToNewRaster([a, b], folder2, name2, ap.SpatialReference(I["srid"]), "32_BIT_FLOAT", None, 1,
                                    "MEAN")


@case("management.RasterToDTED")
def _(ap, I, O):
    import numpy as np
    wgs = O("wgs", "raster")
    ap.env.outputCoordinateSystem = ap.SpatialReference(4326)
    cells = (100 + np.add.outer(np.arange(40), np.arange(50))).astype("float32")
    ap.NumPyArrayToRaster(cells, ap.Point(27.0, 40.6), 0.0001, 0.0001).save(wgs)
    ap.env.outputCoordinateSystem = None
    ap.management.DefineProjection(wgs, ap.SpatialReference(4326))
    folder = _fresh(O, "dted")
    ap.management.RasterToDTED(wgs, folder, "DTED_2")
    return flat({"files": [f.lower() for f in _generated(folder)]})


# ---- geodatabase behaviours: editor tracking, COGO, storage, upgrade ---------------------------------------------


@case("management.EnableCOGO")
def _(ap, I, O):
    out = _copy(ap, I, O, "lines")
    ap.management.EnableCOGO(out)
    return flat({"fields": _fields(ap, out), "isCOGOEnabled": getattr(ap.Describe(out), "isCOGOEnabled", None)})


@case("management.EnableEditorTracking")
def _(ap, I, O):
    out = _copy(ap, I, O, "points")
    ap.management.EnableEditorTracking(out, "made_by", "made_on", "edited_by", "edited_on", "ADD_FIELDS", "UTC")
    d = ap.Describe(out)
    return flat({"fields": _fields(ap, out), "describe": _attrs(d, (
        "editorTrackingEnabled", "creatorFieldName", "createdAtFieldName", "editorFieldName", "editedAtFieldName",
        "isTimeInUTC"))})


@case("management.EnableLastEditTime")
def _(ap, I, O):
    out = _copy(ap, I, O, "points")
    ap.management.EnableLastEditTime(out)
    return flat({"fields": _fields(ap, out)})


@case("management.MigrateDateFieldToHighPrecision")
def _(ap, I, O):
    out = _copy(ap, I, O, "table")
    ap.management.AddField(out, "when", "DATE")
    before = [[f.name, f.type, f.length] for f in ap.ListFields(out, "when")]
    ap.management.MigrateDateFieldToHighPrecision(out, ["when"])
    return flat({"before": before, "after": [[f.name, f.type, f.length] for f in ap.ListFields(out, "when")]})


@case("management.MigrateObjectIDTo64Bit")
def _(ap, I, O):
    out = _copy(ap, I, O, "points")
    before = [[f.type, f.length] for f in ap.ListFields(out) if f.type == "OID"]
    ap.management.MigrateObjectIDTo64Bit(out)
    return flat({"before": before, "after": [[f.type, f.length] for f in ap.ListFields(out) if f.type == "OID"],
                 "count": _count(ap, out)})


@case("management.UpgradeGDB")
def _(ap, I, O):
    gdb = _gdb(ap, O, "upg")
    # A geodatabase already at the current release: Pro refuses it (ERROR 001046); the check alone passes.
    return flat({"upgrade": _refusal(ap.management.UpgradeGDB, gdb, "PREREQUISITE_CHECK", "UPGRADE"),
                 "check": _refusal(ap.management.UpgradeGDB, gdb, "PREREQUISITE_CHECK", "NO_UPGRADE")})


@case("management.RebuildIndexes")
def _(ap, I, O):
    gdb = _gdb(ap, O, "rix")
    ap.management.CopyFeatures(I["points"], os.path.join(gdb, "pts"))
    r = ap.management.RebuildIndexes(gdb, "NO_SYSTEM", ["pts"], "ALL")
    return flat({"outputs": r.outputCount})


@case("management.RegisterWithGeodatabase")
def _(ap, I, O):
    gdb = _gdb(ap, O, "reg")
    ap.management.CopyFeatures(I["points"], os.path.join(gdb, "pts"))
    # A class the geodatabase made is registered already: Pro refuses it (ERROR 001050).
    return flat({"again": _refusal(ap.management.RegisterWithGeodatabase, os.path.join(gdb, "pts"))})


# ---- versioning, archiving (enterprise in Pro; tried on a mobile geodatabase) ------------------------------------


def _mobile(ap, I, O, name):
    gdb = _gdb(ap, O, name)
    fc = os.path.join(gdb, "pts")
    ap.management.CopyFeatures(I["points"], fc)
    return gdb, fc


@case("management.RegisterAsVersioned")
def _(ap, I, O):
    gdb, fc = _mobile(ap, I, O, "ver")
    ap.management.RegisterAsVersioned(fc)
    return flat({"isVersioned": ap.Describe(fc).isVersioned})


@case("management.UnregisterAsVersioned")
def _(ap, I, O):
    gdb, fc = _mobile(ap, I, O, "unver")
    ap.management.UnregisterAsVersioned(fc)
    return flat({"isVersioned": ap.Describe(fc).isVersioned})


@case("management.ReconcileVersion")
def _(ap, I, O):
    gdb, fc = _mobile(ap, I, O, "rec1")
    ap.management.ReconcileVersion(gdb, "edit", "sde.DEFAULT")
    return flat({"count": _count(ap, fc)})


@case("management.ReconcileVersions")
def _(ap, I, O):
    gdb, fc = _mobile(ap, I, O, "recn")
    ap.management.ReconcileVersions(gdb, "ALL_VERSIONS", "sde.DEFAULT")
    return flat({"count": _count(ap, fc)})


@case("management.TrimArchiveHistory")
def _(ap, I, O):
    gdb, fc = _mobile(ap, I, O, "trim")
    ap.management.TrimArchiveHistory(fc, "DELETE", "1/1/2020")
    return flat({"count": _count(ap, fc)})


@case("management.PruneBranchHistory")
def _(ap, I, O):
    gdb, fc = _mobile(ap, I, O, "prune")
    log = os.path.join(_fresh(O, "files"), "log.txt")
    # A class that is not branch versioned: Pro refuses it (ERROR 161027).
    return flat({"refusal": _refusal(ap.management.PruneBranchHistory, fc, log, "REPORT_ONLY")})


@case("management.RemoveFieldConflictFilter")
def _(ap, I, O):
    gdb, fc = _mobile(ap, I, O, "fcf")
    ap.management.RemoveFieldConflictFilter(fc, ["val"])
    return flat({"filters": list(ap.da.ListFieldConflictFilters(fc))})


# ---- attribute rules --------------------------------------------------------------------------------------------


def _ruled(ap, I, O, name="rules", rules=(("double_val", "$feature.val * 2", "val2"),)):
    gdb = _gdb(ap, O, name)
    fc = os.path.join(gdb, "pts")
    ap.management.CopyFeatures(I["points"], fc)
    ap.management.AddField(fc, "val2", "DOUBLE")
    ap.management.AddField(fc, "val3", "DOUBLE")
    ap.management.AddGlobalIDs(fc)
    for rule, expression, field in rules:
        ap.management.AddAttributeRule(fc, rule, "CALCULATION", expression, "EDITABLE", "INSERT;UPDATE",
                                       field=field)
    return gdb, fc


def _rules(ap, fc):
    return sorted([_attrs(r, ("name", "type", "evaluationOrder", "fieldName", "isEnabled", "triggeringEvents",
                              "batch", "scriptExpression")) for r in ap.Describe(fc).attributeRules],
                  key=lambda r: r["name"])


def _csv(path):
    import csv
    with open(path, newline="", encoding="utf-8-sig") as f:
        return list(csv.reader(f))


@case("management.ExportAttributeRules")
def _(ap, I, O):
    gdb, fc = _ruled(ap, I, O)
    out = os.path.join(_fresh(O, "files"), "rules.csv")
    ap.management.ExportAttributeRules(fc, out)
    rows = _csv(out)
    header = rows[0]
    keep = [h for h in header if h.upper() in ("NAME", "TYPE", "FIELDNAME", "SCRIPTEXPRESSION", "TRIGGERINGEVENTS",
                                                "ISEDITABLE", "BATCH", "EVALUATIONORDER", "ISENABLED")]
    return flat({"header": header, "rows": [[r[header.index(k)] for k in keep] for r in rows[1:]]})


@case("management.ImportAttributeRules")
def _(ap, I, O):
    gdb, fc = _ruled(ap, I, O)
    out = os.path.join(_fresh(O, "files"), "rules.csv")
    ap.management.ExportAttributeRules(fc, out)
    target = os.path.join(gdb, "pts2")
    ap.management.CopyFeatures(I["points"], target)
    ap.management.AddField(target, "val2", "DOUBLE")
    ap.management.AddGlobalIDs(target)
    ap.management.ImportAttributeRules(target, out)
    return flat({"rules": _rules(ap, target)})


@case("management.ReorderAttributeRule")
def _(ap, I, O):
    gdb, fc = _ruled(ap, I, O, rules=(("r1", "$feature.val * 2", "val2"), ("r2", "$feature.val2 + 1", "val3")))
    before = _rules(ap, fc)
    ap.management.ReorderAttributeRule(fc, "r2", 1)
    return flat({"before": [[r["name"], r["evaluationOrder"]] for r in before],
                 "after": [[r["name"], r["evaluationOrder"]] for r in _rules(ap, fc)]})


@case("management.EvaluateRules")
def _(ap, I, O):
    gdb = _gdb(ap, O, "eval")
    fc = os.path.join(gdb, "pts")
    ap.management.CopyFeatures(I["points"], fc)
    ap.management.AddField(fc, "val2", "DOUBLE")
    ap.management.AddGlobalIDs(fc)
    ap.management.EnableEditorTracking(fc, "made_by", "made_on", "edited_by", "edited_on", "ADD_FIELDS")
    ap.management.AddAttributeRule(fc, "batch_rule", "CALCULATION", "$feature.val * 2", "EDITABLE", None,
                                   field="val2", batch="BATCH")
    ap.management.EvaluateRules(gdb, "CALCULATION_RULES")
    return flat({"vals": _rows(ap, fc, ["pid", "val2"], "pid <= 5")})


def _generated(folder, sidecars=False):
    """The files written under a folder, by name. Without ``sidecars``, the ``.xml`` and ``schema.ini`` files ArcGIS's
    table writer puts beside a CSV file are left out (grapy does not write them; recorded in the results)."""
    found = []
    for root, _, files in os.walk(folder):
        found += [os.path.relpath(os.path.join(root, f), folder).replace("\\", "/") for f in files
                  if sidecars or not (f.lower().endswith((".xml", ".ovr")) or f.lower() == "schema.ini")]
    return sorted(found)


def _rule_csv(folder):
    """Each rule file's row, its script left out (each side writes an Arcade script of its own)."""
    out = []
    for name in _generated(folder):
        if name.endswith(".csv") and not name.endswith("ID_Table.csv"):
            rows = _csv(os.path.join(folder, name))
            out.append([[h, v] for h, v in zip(rows[0], rows[1]) if h != "SCRIPTEXPRESSION"])
    return out


@case("management.GenerateIDAttributeRule")
def _(ap, I, O):
    gdb = _gdb(ap, O, "genid")
    fc = os.path.join(gdb, "pts")
    ap.management.CopyFeatures(I["points"], fc)
    ap.management.AddField(fc, "asset_id", "TEXT")
    folder = _fresh(O, "rules")
    ap.management.GenerateIDAttributeRule(fc, folder, "asset_id")
    ids = [r[:2] + r[3:] for r in _csv(os.path.join(folder, "pts", "ID_Table.csv"))]   # the sequence name is random
    return flat({"files": _generated(folder), "rule": _rule_csv(folder), "ids": ids})


@case("management.GenerateSpatialJoinAttributeRule")
def _(ap, I, O):
    gdb = _gdb(ap, O, "gensj")
    fc, polys = os.path.join(gdb, "pts"), os.path.join(gdb, "polys")
    ap.management.CopyFeatures(I["points"], fc)
    ap.management.CopyFeatures(I["polys"], polys)
    ap.management.AddField(fc, "zone_cat", "TEXT")
    folder = _fresh(O, "rules")
    field_map = ap.FieldMap()
    field_map.addInputField(polys, "cat")
    out = field_map.outputField
    out.name = "zone_cat"
    field_map.outputField = out
    mappings = ap.FieldMappings()
    mappings.addFieldMap(field_map)
    ap.management.GenerateSpatialJoinAttributeRule(fc, folder, [polys], mappings)
    return flat({"files": _generated(folder), "rule": _rule_csv(folder)})


@case("management.GenerateSymbolRotationAttributeRule")
def _(ap, I, O):
    gdb = _gdb(ap, O, "genrot")
    fc, lines = os.path.join(gdb, "pts"), os.path.join(gdb, "lines")
    ap.management.CopyFeatures(I["points"], fc)
    ap.management.CopyFeatures(I["lines"], lines)
    ap.management.AddField(fc, "rot", "DOUBLE")
    folder = _fresh(O, "rules")
    ap.management.GenerateSymbolRotationAttributeRule(fc, folder, "rot", [lines])
    return flat({"files": _generated(folder), "rule": _rule_csv(folder)})


@case("management.GenerateUpdateRelatedAttributeRule")
def _(ap, I, O):
    gdb = _gdb(ap, O, "genrel")
    fc, polys = os.path.join(gdb, "pts"), os.path.join(gdb, "polys")
    ap.management.CopyFeatures(I["points"], fc)
    ap.management.CopyFeatures(I["polys"], polys)
    ap.management.AddField(polys, "n", "LONG")
    folder = _fresh(O, "rules")
    ap.management.GenerateUpdateRelatedAttributeRule(fc, folder, [[polys, "cat", "SPATIAL_ONLY", "cat", "SET_VALUE"]])
    return flat({"files": _generated(folder), "rule": _rule_csv(folder)})


# ---- contingent values --------------------------------------------------------------------------------------------


def _cvfiles(O):
    folder = _fresh(O, "files")
    return os.path.join(folder, "groups.csv"), os.path.join(folder, "values.csv")


@case("management.ExportContingentValues")
def _(ap, I, O):
    fc = _contingent(ap, I, O)
    groups, values = _cvfiles(O)
    ap.management.ExportContingentValues(fc, groups, values)
    return flat({"groups": _csv(groups), "values": _csv(values)})


@case("management.ImportContingentValues")
def _(ap, I, O):
    fc = _contingent(ap, I, O)
    groups, values = _cvfiles(O)
    ap.management.ExportContingentValues(fc, groups, values)
    target = os.path.join(os.path.dirname(fc), "lines2")
    ap.management.CopyFeatures(I["lines"], target)
    ap.management.AddField(target, "kcode", "SHORT")
    ap.management.AddField(target, "speed", "DOUBLE")
    ap.management.AssignDomainToField(target, "kcode", "kinds")
    ap.management.AssignDomainToField(target, "speed", "speed")
    ap.management.ImportContingentValues(target, groups, values, "REPLACE")
    return flat({"values": sorted([c.fieldGroupName, [[v.name, v.type, str(v.code)] for v in c.values]]
                                  for c in ap.da.ListContingentValues(target))})


@case("management.GenerateContingentValues")
def _(ap, I, O):
    # A group that does not restrict, so the data may hold any combination: Pro's cursors do not enforce a restrictive
    # group's contingent values and grapy's do (recorded as an open difference).
    gdb = _domains(ap, O)
    fc = os.path.join(gdb, "lines")
    ap.management.CopyFeatures(I["lines"], fc)
    ap.management.AddField(fc, "kcode", "SHORT")
    ap.management.AddField(fc, "speed", "DOUBLE")
    ap.management.AssignDomainToField(fc, "kcode", "kinds")
    ap.management.AssignDomainToField(fc, "speed", "speed")
    ap.management.CreateFieldGroup(fc, "grp", ["kcode", "speed"], "DO_NOT_RESTRICT")
    with ap.da.UpdateCursor(fc, ["lid", "kcode", "speed"]) as c:
        for lid, _, _ in c:
            c.updateRow([lid, 1 + lid % 3, 60.0 + lid])
    groups, values = _cvfiles(O)
    ap.management.GenerateContingentValues(fc, groups, values, "DATA", ["grp"])
    return flat({"groups": _csv(groups), "values": _csv(values)})


@case("management.RemoveContingentValue")
def _(ap, I, O):
    fc = _contingent(ap, I, O)
    first = ap.da.ListContingentValues(fc)[0]
    ap.management.RemoveContingentValue(fc, first.id)
    return flat({"left": [[[v.name, v.type] for v in c.values] for c in ap.da.ListContingentValues(fc)]})


# ---- topology and relationship rules -----------------------------------------------------------------------------


def _topology(ap, I, O, name="topo"):
    gdb = _gdb(ap, O, name)
    ap.management.CreateFeatureDataset(gdb, "fd", ap.SpatialReference(I["srid"]))
    fd = os.path.join(gdb, "fd")
    polys, lines = os.path.join(fd, "polys"), os.path.join(fd, "lines")
    ap.management.CopyFeatures(I["polys"], polys)
    ap.management.CopyFeatures(I["lines"], lines)
    topo = ap.management.CreateTopology(fd, "topo", 0.001)[0]
    ap.management.AddFeatureClassToTopology(topo, polys, 1, 1)
    ap.management.AddFeatureClassToTopology(topo, lines, 1, 1)
    ap.management.AddRuleToTopology(topo, "Must Not Overlap (Area)", polys)
    ap.management.AddRuleToTopology(topo, "Must Not Have Dangles (Line)", lines)
    return gdb, fd, topo


def _topo_desc(ap, topo):
    d = ap.Describe(topo)
    return {"clusterTolerance": _r(d.clusterTolerance, 6),
            "classes": sorted(os.path.basename(str(n)) for n in d.featureClassNames)}


def _topo_errors(ap, topo, O):
    egdb = _gdb(ap, O, "err")
    ap.management.ExportTopologyErrors(topo, egdb, "e")
    out = {}
    for k in ("point", "line", "poly"):
        path = os.path.join(egdb, f"e_{k}")
        out[k] = {"fields": [f.name for f in ap.ListFields(path) if f.type not in ("OID", "Geometry")
                             and f.name.lower() not in ("shape_length", "shape_area")],
                  "rows": sorted([str(r[0]), _r(r[1], 2)] for r in ap.da.SearchCursor(
                      path, ["RuleType", "SHAPE@AREA" if k == "poly" else "SHAPE@LENGTH"]))}
    return out


@case("management.ValidateTopology")
def _(ap, I, O):
    gdb, fd, topo = _topology(ap, I, O)
    ap.management.ValidateTopology(topo)
    errors = _topo_errors(ap, topo, O)
    return flat({"desc": _topo_desc(ap, topo), "counts": [len(errors[k]["rows"]) for k in ("point", "line", "poly")]})


@case("management.ExportTopologyErrors")
def _(ap, I, O):
    gdb, fd, topo = _topology(ap, I, O, "topoerr")
    ap.management.ValidateTopology(topo)
    return flat(_topo_errors(ap, topo, O))


@case("management.RemoveFeatureClassFromTopology")
def _(ap, I, O):
    gdb, fd, topo = _topology(ap, I, O, "toporm")
    ap.management.RemoveFeatureClassFromTopology(topo, "lines")
    return flat({"desc": _topo_desc(ap, topo)})


@case("management.RemoveRuleFromTopology")
def _(ap, I, O):
    gdb, fd, topo = _topology(ap, I, O, "toporule")
    for class_id in range(1, 30):
        try:
            ap.management.RemoveRuleFromTopology(topo, f"Must Not Have Dangles ({class_id})")
            break
        except Exception:  # noqa: BLE001 — not this id
            continue
    ap.management.ValidateTopology(topo)
    errors = _topo_errors(ap, topo, O)
    return flat({"desc": _topo_desc(ap, topo), "counts": [len(errors[k]["rows"]) for k in ("point", "line", "poly")]})


@case("management.SetClusterTolerance")
def _(ap, I, O):
    gdb, fd, topo = _topology(ap, I, O, "topotol")
    ap.management.SetClusterTolerance(topo, 0.05)
    return flat({"desc": _topo_desc(ap, topo)})


@case("management.RemoveRuleFromRelationshipClass")
def _(ap, I, O):
    gdb = _gdb(ap, O, "relrule")
    origin, dest = os.path.join(gdb, "polys"), os.path.join(gdb, "rows")
    ap.management.CopyFeatures(I["polys"], origin)
    ap.management.CopyRows(I["table"], dest)
    ap.management.AddField(dest, "gid", "LONG")
    ap.management.AddField(dest, "stype", "SHORT")
    ap.management.SetSubtypeField(dest, "stype")
    ap.management.AddSubtype(dest, 1, "One")
    ap.management.AddSubtype(dest, 2, "Two")
    rc = os.path.join(gdb, "poly_rows")
    ap.management.CreateRelationshipClass(origin, dest, rc, "SIMPLE", "rows", "poly", "NONE", "ONE_TO_MANY", "NONE",
                                          "gid", "gid")
    ap.management.AddRuleToRelationshipClass(rc, None, 0, 5, "One", 0, 3)
    ap.management.AddRuleToRelationshipClass(rc, None, 0, 5, "Two", 1, 2)
    before = len(ap.Describe(rc).relationshipRules)
    ap.management.RemoveRuleFromRelationshipClass(rc, None, "Two")
    after = [[r.originSubtypeCode, r.originMinimumCardinality, r.originMaximumCardinality, r.destinationSubtypeCode,
              r.destinationMinimumCardinality, r.destinationMaximumCardinality]
             for r in ap.Describe(rc).relationshipRules]
    return flat({"before": before, "after": after})


@case("management.TableToRelationshipClass")
def _(ap, I, O):
    gdb = _gdb(ap, O, "relt")
    origin, dest = os.path.join(gdb, "polys"), os.path.join(gdb, "pts")
    ap.management.CopyFeatures(I["polys"], origin)
    ap.management.CopyFeatures(I["points"], dest)
    link = os.path.join(gdb, "link")
    ap.management.CreateTable(gdb, "link")
    ap.management.AddField(link, "gid", "LONG")
    ap.management.AddField(link, "pid", "LONG")
    ap.management.AddField(link, "w", "DOUBLE")
    with ap.da.InsertCursor(link, ["gid", "pid", "w"]) as c:
        for g, p in ((1, 1), (1, 2), (2, 3), (5, 4)):
            c.insertRow((g, p, g * 0.5))
    rc = os.path.join(gdb, "poly_pts")
    ap.management.TableToRelationshipClass(origin, dest, rc, "SIMPLE", "pts", "polys", "NONE", "MANY_TO_MANY", link,
                                           ["w"], "gid", "gid", "pid", "pid")
    d = ap.Describe(rc)
    pairs = sorted(list(map(list, pair)) for pair in ap.da.SearchRelatedRecords(rc, "*", ["gid"], ["pid"]))
    return flat({"desc": _attrs(d, ("cardinality", "isAttributed", "isComposite", "forwardPathLabel",
                                    "backwardPathLabel", "notification")), "pairs": pairs})


# ---- attachments and photos ---------------------------------------------------------------------------------------


def _attached(ap, I, O, name="att"):
    gdb = _gdb(ap, O, name)
    fc = os.path.join(gdb, "pts")
    ap.management.CopyFeatures(I["points"], fc)
    folder = O(name + "_files", "folder")
    for pid in (1, 2, 3):
        _file(os.path.join(folder, f"{pid}.txt"), f"attachment for point {pid}\n")
    _file(os.path.join(folder, "2_more.txt"), "a second one for point 2\n")
    ap.management.EnableAttachments(fc)
    match = os.path.join(gdb, "match")
    ap.management.GenerateAttachmentMatchTable(fc, folder, match, "pid", "*.txt", "ABSOLUTE", "PREFIX")
    ap.management.AddAttachments(fc, "pid", match, "MatchID", "Filename")
    return gdb, fc, folder, match


def _attach_rows(ap, fc):
    return sorted([r[0], r[1]] for r in ap.da.SearchCursor(fc + "__ATTACH", ["ATT_NAME", "DATA_SIZE"]))


@case("management.GenerateAttachmentMatchTable")
def _(ap, I, O):
    gdb = _gdb(ap, O, "gamt")
    fc = os.path.join(gdb, "pts")
    ap.management.CopyFeatures(I["points"], fc)
    folder = _fresh(O, "files")
    for name in ("1.txt", "2.txt", "2_more.txt", "40.txt"):
        _file(os.path.join(folder, name), name)
    out = {}
    for pattern in ("EXACT", "PREFIX", "ANY"):
        match = os.path.join(gdb, "m_" + pattern.lower())
        ap.management.GenerateAttachmentMatchTable(fc, folder, match, "pid", "*.txt", "RELATIVE", pattern)
        out[pattern] = {"fields": [f.name for f in ap.ListFields(match) if f.type != "OID"],
                        "rows": sorted([r[0], r[1]] for r in ap.da.SearchCursor(match, ["MatchID", "Filename"]))}
    return flat(out)


@case("management.ExportAttachments")
def _(ap, I, O):
    gdb, fc, folder, match = _attached(ap, I, O, "exatt")
    out = _fresh(O, "out")
    ap.management.ExportAttachments(fc, out)
    named = _fresh(O, "named")
    ap.management.ExportAttachments(fc, named, None, "PREFIX", ["pid"])
    return flat({"original": _generated(out), "named": _generated(named)})


@case("management.RemoveAttachments")
def _(ap, I, O):
    gdb, fc, folder, match = _attached(ap, I, O, "rmatt")
    before = _attach_rows(ap, fc)
    drop = os.path.join(gdb, "unwanted")
    ap.management.CreateTable(gdb, "unwanted")
    ap.management.AddField(drop, "pid", "LONG")
    ap.management.AddField(drop, "fname", "TEXT")
    with ap.da.InsertCursor(drop, ["pid", "fname"]) as c:
        c.insertRow((2, "2_more.txt"))
    ap.management.RemoveAttachments(fc, "pid", drop, "pid", "fname")
    return flat({"before": before, "after": _attach_rows(ap, fc)})


@case("management.UpgradeAttachments")
def _(ap, I, O):
    gdb, fc, folder, match = _attached(ap, I, O, "upatt")
    ap.management.UpgradeAttachments(fc)
    return flat({"fields": [[f.name, f.type] for f in ap.ListFields(fc + "__ATTACH")], "rows": _attach_rows(ap, fc)})


def _dms(value):
    d = int(value)
    m = int((value - d) * 60)
    s = round(((value - d) * 60 - m) * 60 * 1000)
    return (float(d), float(m), s / 1000.0)


def _photos(O, name="photos"):
    """Three JPEGs with EXIF GPS positions and times, and one without a position."""
    from PIL import Image
    folder = O(name, "folder")
    shots = [("a.jpg", 40.60, 27.10, "2026:11:01 10:00:00"), ("b.jpg", 40.61, 27.12, "2026:11:01 10:05:00"),
             ("c.jpg", 40.62, 27.15, "2026:11:01 11:30:00"), ("d.jpg", None, None, "2026:11:01 10:02:00")]
    for file, lat, lon, when in shots:
        image = Image.new("RGB", (8, 8), (120, 160, 200))
        exif = Image.Exif()
        exif[0x0132] = when
        exif.get_ifd(0x8769)[0x9003] = when
        if lat is not None:
            gps = exif.get_ifd(0x8825)
            gps[1], gps[2], gps[3], gps[4] = "N", _dms(lat), "E", _dms(lon)
        image.save(os.path.join(folder, file), exif=exif)
    return folder


@case("management.GeoTaggedPhotosToPoints")
def _(ap, I, O):
    folder = _photos(O)
    gdb = _gdb(ap, O, "geotag")
    out, bad = os.path.join(gdb, "pts"), os.path.join(gdb, "bad")
    ap.management.GeoTaggedPhotosToPoints(folder, out, bad, "ONLY_GEOTAGGED", "NO_ATTACHMENTS")
    fields = [f.name for f in ap.ListFields(out) if f.type not in ("OID", "Geometry")]
    rows = sorted([os.path.basename(str(r[0])), str(r[1]), _r(r[2][0], 5), _r(r[2][1], 5)]
                  for r in ap.da.SearchCursor(out, ["Name", "DateTime", "SHAPE@XY"]))
    return flat({"fields": fields, "rows": rows, "invalid": _count(ap, bad),
                 "srid": ap.Describe(out).spatialReference.factoryCode})


@case("management.MatchPhotosToRowsByTime")
def _(ap, I, O):
    import datetime
    folder = _photos(O, "mphotos")
    gdb = _gdb(ap, O, "mphoto")
    table = os.path.join(gdb, "visits")
    ap.management.CreateTable(gdb, "visits")
    ap.management.AddField(table, "k", "LONG")
    ap.management.AddField(table, "t", "DATE")
    with ap.da.InsertCursor(table, ["k", "t"]) as c:
        c.insertRow((1, datetime.datetime(2026, 11, 1, 10, 0, 30)))
        c.insertRow((2, datetime.datetime(2026, 11, 1, 11, 0, 0)))
    out, unmatched = os.path.join(gdb, "matched"), os.path.join(gdb, "unmatched")
    ap.management.MatchPhotosToRowsByTime(folder, table, "t", out, unmatched, "NO_ATTACHMENTS", 120)
    fields = [f.name for f in ap.ListFields(out) if f.type != "OID"]
    rows = sorted([os.path.basename(v) if isinstance(v, str) else str(v) for v in r]
                  for r in ap.da.SearchCursor(out, [f for f in fields]))
    return flat({"fields": fields, "rows": rows, "unmatched": _count(ap, unmatched)})


# ---- mosaic datasets ----------------------------------------------------------------------------------------------


def _mosaic(ap, I, O, name="md"):
    """A mosaic dataset of two rasters: the DEM (copied into a folder of the case's own) and a 10 × 5 patch."""
    gdb = _gdb(ap, O, name)
    folder = _fresh(O, name + "_rasters")
    dem = os.path.join(folder, "dem.tif")
    ap.management.CopyRaster(I["dem"], dem)
    patch = _small_raster(ap, I, O, name + "_patch", 500.0, 100, 100, 10, 5)
    md = ap.management.CreateMosaicDataset(gdb, "md", ap.SpatialReference(I["srid"]))[0]
    ap.management.AddRastersToMosaicDataset(md, "Raster Dataset", [dem, patch])
    return gdb, md, folder


def _items(ap, md):
    return sorted([str(r[0]), _r(r[1], 2)] for r in ap.da.SearchCursor(md, ["Name", "SHAPE@AREA"]))


@case("management.ExportMosaicDatasetGeometry")
def _(ap, I, O):
    gdb, md, folder = _mosaic(ap, I, O)
    ap.management.ExportMosaicDatasetGeometry(md, O("footprint"), None, "FOOTPRINT")
    ap.management.ExportMosaicDatasetGeometry(md, O("boundary"), None, "BOUNDARY")


@case("management.ExportMosaicDatasetItems")
def _(ap, I, O):
    gdb, md, folder = _mosaic(ap, I, O, "mdexp")
    out = _fresh(O, "out")
    ap.management.ExportMosaicDatasetItems(md, out, "item", None, "TIFF")
    return flat({"files": [f for f in _generated(out) if f.lower().endswith(".tif")]})


@case("management.ExportMosaicDatasetPaths")
def _(ap, I, O):
    gdb, md, folder = _mosaic(ap, I, O, "mdpaths")
    table = os.path.join(gdb, "paths")
    ap.management.ExportMosaicDatasetPaths(md, table)
    fields = [f.name for f in ap.ListFields(table) if f.type != "OID"]
    rows = sorted([os.path.basename(str(v)) if isinstance(v, str) else v for v in r]
                  for r in ap.da.SearchCursor(table, fields))
    return flat({"fields": fields, "rows": rows})


@case("management.ImportMosaicDatasetGeometry")
def _(ap, I, O):
    gdb, md, folder = _mosaic(ap, I, O, "mdimp")
    shapes = os.path.join(gdb, "shapes")
    ap.management.CreateFeatureclass(gdb, "shapes", "POLYGON", spatial_reference=ap.SpatialReference(I["srid"]))
    ap.management.AddField(shapes, "item", "LONG")
    x, y = I["x0"], I["y0"]
    with ap.da.InsertCursor(shapes, ["item", "SHAPE@WKT"]) as c:
        c.insertRow((1, f"POLYGON (({x + 50} {y + 50}, {x + 50} {y + 550}, {x + 550} {y + 550}, {x + 550} {y + 50}, "
                        f"{x + 50} {y + 50}))"))
    oid = ap.Describe(md).OIDFieldName
    ap.management.ImportMosaicDatasetGeometry(md, "FOOTPRINT", oid, shapes, "item")
    return flat({"items": _items(ap, md)})


@case("management.MakeMosaicLayer")
def _(ap, I, O):
    gdb, md, folder = _mosaic(ap, I, O, "mdlyr")
    oid = ap.Describe(md).OIDFieldName
    layer = ap.management.MakeMosaicLayer(md, "md_lyr", f"{oid} = 1")[0]
    ap.management.CopyRaster(layer, O("out", "raster"))


@case("management.RemoveRastersFromMosaicDataset")
def _(ap, I, O):
    gdb, md, folder = _mosaic(ap, I, O, "mdrm")
    ap.management.RemoveRastersFromMosaicDataset(md, "Name = 'dem'")
    return flat({"items": _items(ap, md)})


@case("management.RepairMosaicDatasetPaths")
def _(ap, I, O):
    gdb, md, folder = _mosaic(ap, I, O, "mdrep")
    moved = _fresh(O, "moved")
    import shutil
    for f in os.listdir(folder):
        if f.lower().startswith("dem."):
            shutil.move(os.path.join(folder, f), os.path.join(moved, f))
    ap.management.RepairMosaicDatasetPaths(md, [[os.path.normpath(folder), os.path.normpath(moved)]])
    table = os.path.join(gdb, "paths")
    ap.management.ExportMosaicDatasetPaths(md, table)
    rows = sorted(os.path.basename(os.path.dirname(str(r[0]))) for r in ap.da.SearchCursor(table, ["Path"]))
    return flat({"folders": rows})


@case("management.SetMosaicDatasetProperties")
def _(ap, I, O):
    gdb, md, folder = _mosaic(ap, I, O, "mdset")
    ap.management.SetMosaicDatasetProperties(md, 3000, 2500, "None;JPEG;LZ77", "LZ77", 70, 0.5, "BILINEAR")
    d = ap.Describe(md)
    return flat({"desc": _attrs(d, ("maxRequestSizeX", "maxRequestSizeY", "allowedCompressionMethods",
                                    "defaultCompressionMethod", "JPEGQuality", "LERCTolerance",
                                    "defaultResamplingMethod"))})


@case("management.SynchronizeMosaicDataset")
def _(ap, I, O):
    gdb, md, folder = _mosaic(ap, I, O, "mdsync")
    ap.management.SynchronizeMosaicDataset(md)
    return flat({"items": _items(ap, md)})


# ---- ortho mapping (frame imagery and a block adjustment) --------------------------------------------------------


@case("management.ExportFrameAndCameraParameters")
def _(ap, I, O):
    gdb, md, folder = _mosaic(ap, I, O, "mdframe")
    out = os.path.join(_fresh(O, "files"), "frames.csv")
    ap.management.ExportFrameAndCameraParameters(md, out)
    return flat({"rows": _csv(out)})


@case("management.GenerateBlockAdjustmentReport")
def _(ap, I, O):
    gdb, md, folder = _mosaic(ap, I, O, "mdblock")
    sol = os.path.join(gdb, "solution")
    ap.management.CreateTable(gdb, "solution")
    pts = os.path.join(gdb, "solpts")
    ap.management.CreateFeatureclass(gdb, "solpts", "POINT", spatial_reference=ap.SpatialReference(I["srid"]))
    out = os.path.join(_fresh(O, "files"), "report.html")
    ap.management.GenerateBlockAdjustmentReport(md, sol, pts, out)
    return flat({"written": os.path.exists(out)})


@case("management.MatchControlPoints")
def _(ap, I, O):
    gdb, md, folder = _mosaic(ap, I, O, "mdmatch")
    cps = os.path.join(gdb, "cps")
    ap.management.CreateFeatureclass(gdb, "cps", "POINT", spatial_reference=ap.SpatialReference(I["srid"]))
    out = os.path.join(gdb, "matched")
    ap.management.MatchControlPoints(md, cps, out)
    return flat({"count": _count(ap, out)})


# ---- LAS, TIN, 3D objects, multidimensional ----------------------------------------------------------------------


def _write_las(path, points, srid):
    """A LAS 1.2 file of point data record format 0, with a GeoTIFF key directory naming ``srid`` — written by hand,
    so both sides read the same bytes."""
    scale, offset = 0.01, (500000.0, 4500000.0, 0.0)
    keys = struct.pack("<16H", 1, 1, 0, 3, 1024, 0, 1, 1, 1025, 0, 1, 1, 3072, 0, 1, srid)
    vlr = struct.pack("<H16sHH32s", 0, b"LASF_Projection", 34735, len(keys), b"GeoKeyDirectoryTag") + keys
    offset_to_points = 227 + len(vlr)
    xs, ys, zs = [p[0] for p in points], [p[1] for p in points], [p[2] for p in points]
    by_return = [len(points), 0, 0, 0, 0]
    header = struct.pack("<4sHH16sBB32s32sHHHIIBHI5I3d3d6d", b"LASF", 0, 0, bytes(16), 1, 2, b"sweep", b"sweep",
                         310, 2026, 227, offset_to_points, 1, 0, 20, len(points), *by_return, scale, scale, scale,
                         *offset, max(xs), min(xs), max(ys), min(ys), max(zs), min(zs))
    assert len(header) == 227, len(header)
    with open(path, "wb") as f:
        f.write(header)
        f.write(vlr)
        for x, y, z, cls, intensity in points:
            f.write(struct.pack("<iiiHBBbBH", round((x - offset[0]) / scale), round((y - offset[1]) / scale),
                                round((z - offset[2]) / scale), intensity, 0b00001001, cls, 0, 0, 1))
    return path


def _las_points(I):
    from fixtures import dem_value
    out = []
    for i in range(30):
        for j in range(30):
            x, y = 10 + 20 * i, 10 + 20 * j
            z = dem_value(x, y)
            ground = (i + j) % 4 != 0
            out.append((I["x0"] + x, I["y0"] + y, z if ground else z + 8.0, 2 if ground else 1, 100 + (i * j) % 50))
    return out


def _lasd(ap, I, O, name="las"):
    folder = _fresh(O, name)
    a = _write_las(os.path.join(folder, "a.las"), [p for p in _las_points(I) if p[0] < I["x0"] + 300], I["srid"])
    b = _write_las(os.path.join(folder, "b.las"), [p for p in _las_points(I) if p[0] >= I["x0"] + 300], I["srid"])
    lasd = os.path.join(folder, "all.lasd")
    ap.management.CreateLasDataset([a, b], lasd, "NO_RECURSION", None, ap.SpatialReference(I["srid"]), "COMPUTE_STATS")
    return folder, lasd, a, b


def _las_header(path):
    with open(path, "rb") as f:
        head = f.read(227)
    count = struct.unpack_from("<I", head, 107)[0]
    bounds = struct.unpack_from("<6d", head, 179)
    return [count] + [_r(v, 2) for v in bounds]


@case("management.MakeLasDatasetLayer")
def _(ap, I, O):
    folder, lasd, a, b = _lasd(ap, I, O, "mlas")
    layer = ap.management.MakeLasDatasetLayer(lasd, "las_ground", [2])[0]
    ap.management.LasPointStatsAsRaster(layer, O("ground", "raster"), "POINT_COUNT", "CELLSIZE", 100)


@case("management.LasPointStatsAsRaster")
def _(ap, I, O):
    folder, lasd, a, b = _lasd(ap, I, O, "plas")
    ap.management.LasPointStatsAsRaster(lasd, O("count", "raster"), "POINT_COUNT", "CELLSIZE", 50)
    ap.management.LasPointStatsAsRaster(lasd, O("zrange", "raster"), "Z_RANGE", "CELLSIZE", 50)
    ap.management.LasPointStatsAsRaster(lasd, O("classes", "raster"), "PREDOMINANT_CLASS", "CELLSIZE", 50)


@case("management.LasDatasetStatistics")
def _(ap, I, O):
    folder, lasd, a, b = _lasd(ap, I, O, "slas")
    out = os.path.join(_fresh(O, "files"), "stats.csv")
    ap.management.LasDatasetStatistics(lasd, "OVERWRITE_EXISTING_STATS", out, "DATASET", "COMMA")
    return flat({"rows": _csv(out)})


@case("management.InterpolateFromPointCloud")
def _(ap, I, O):
    folder, lasd, a, b = _lasd(ap, I, O, "ilas")
    ap.management.InterpolateFromPointCloud(lasd, O("dsm", "raster"), 20, "TRIANGULATION", "NONE", "DSM")


@case("management.ProjectLAS")
def _(ap, I, O):
    folder, lasd, a, b = _lasd(ap, I, O, "prlas")
    out = _fresh(O, "projected")
    ap.management.ProjectLAS(lasd, out, ap.SpatialReference(32636))
    files = sorted(f for f in os.listdir(out) if f.lower().endswith(".las"))
    return flat({"files": files, "headers": [_las_header(os.path.join(out, f)) for f in files]})


@case("management.RemoveFilesFromLasDataset")
def _(ap, I, O):
    folder, lasd, a, b = _lasd(ap, I, O, "rmlas")
    ap.management.RemoveFilesFromLasDataset(lasd, [os.path.normpath(b)])
    return flat({"fileCount": ap.Describe(lasd).fileCount, "pointCount": ap.Describe(lasd).pointCount})


@case("management.MakeTinLayer")
def _(ap, I, O):
    folder = O("tin", "folder")
    tin = os.path.join(folder, "t")
    ap.ddd.CreateTin(tin, ap.SpatialReference(I["srid"]), [[I["points"], "val", "Mass_Points", "<None>"]])
    layer = ap.management.MakeTinLayer(tin, "tin_lyr")[0]
    return flat({"layer": ap.Describe(layer).dataType, "tin": ap.Describe(tin).dataType})


def _cube_obj(path, size=10.0):
    v = [(0, 0, 0), (size, 0, 0), (size, size, 0), (0, size, 0), (0, 0, size), (size, 0, size), (size, size, size),
         (0, size, size)]
    faces = [(1, 4, 3, 2), (5, 6, 7, 8), (1, 2, 6, 5), (2, 3, 7, 6), (3, 4, 8, 7), (4, 1, 5, 8)]
    return _file(path, "".join(f"v {x} {y} {z}\n" for x, y, z in v) + "".join(
        "f " + " ".join(map(str, f)) + "\n" for f in faces))


def _imported(ap, O, name):
    """A cube model imported into a new 3D object class (the documented default strategy, a new class's own
    coordinate system), answered with the class."""
    gdb = _gdb(ap, O, name)
    fc = os.path.join(gdb, "objects")
    folder = O(name + "_models", "folder")
    _cube_obj(os.path.join(folder, "cube.obj"))
    ap.management.Import3DObjects([os.path.join(folder, "cube.obj")], fc)
    return fc


def _mp_rows(ap, fc):
    rows = []
    for shape, in ap.da.SearchCursor(fc, ["SHAPE@"]):
        e = shape.extent
        rows.append([_r(e.XMin, 3), _r(e.YMin, 3), _r(e.ZMin, 3), _r(e.XMax, 3), _r(e.YMax, 3), _r(e.ZMax, 3)])
    return sorted(rows)


@case("management.Import3DObjects")
def _(ap, I, O):
    fc = _imported(ap, O, "imp3d")
    d = ap.Describe(fc)
    return flat({"rows": _mp_rows(ap, fc), "shapeType": d.shapeType, "srid": d.spatialReference.factoryCode,
                 "fields": [f.name for f in ap.ListFields(fc) if f.type not in ("OID", "Geometry")
                            and f.name.lower() not in ("shape_length", "shape_area")]})


@case("management.Export3DObjects")
def _(ap, I, O):
    fc = _imported(ap, O, "exp3d")
    out = _fresh(O, "out")
    ap.management.Export3DObjects(fc, out, "FMT3D_OBJ")
    return flat({"files": _generated(out)})


@case("management.ExtractSubDataset")
def _(ap, I, O):
    import numpy as np
    from scipy.io import netcdf_file
    folder = O("nc", "folder")
    path = os.path.join(folder, "two.nc")
    f = netcdf_file(path, "w")
    f.createDimension("y", 6)
    f.createDimension("x", 8)
    xs = f.createVariable("x", "d", ("x",))
    xs[:] = I["x0"] + 5 + 10 * np.arange(8)
    ys = f.createVariable("y", "d", ("y",))
    ys[:] = I["y0"] + 55 - 10 * np.arange(6)
    for name, factor in (("alpha", 1.0), ("beta", 10.0)):
        v = f.createVariable(name, "f", ("y", "x"))
        v[:] = (np.arange(48).reshape(6, 8) * factor).astype("float32")
    f.close()
    ap.management.ExtractSubDataset(path, O("second", "raster"), "2")
