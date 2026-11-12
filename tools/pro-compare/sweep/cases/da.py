"""arcpy.da against ArcGIS Pro 3.7: cursors, Describe, Walk, domains, subtypes, contingent values, sequences, NumPy and
Arrow conversions, the Editor, versions.

Inputs beyond the shared fixtures — a geodatabase with domains, subtypes, a relationship class — are built here with
``ap``, so both sides build them the same way. Answers are ``flat(...)`` lists, as in arcpy_top.
"""
import os

from cases.arcpy_top import _r, flat

CASES = {}


def case(name):
    def register(function):
        CASES[name] = function
        return function
    return register


def _attrs(obj, names):
    return {n: (lambda v: v if isinstance(v, (type(None), bool, int, float, str, list, tuple, dict)) else str(v))(
        getattr(obj, n, "<none>")) for n in names}


def _gdb(ap, O, name):
    """A fresh geodatabase of the case's own, in its own folder."""
    folder = O(name, "folder")
    return ap.management.CreateFileGDB(folder, name)[0]


# ---- cursors ------------------------------------------------------------------------------------------------------


@case("da.SearchCursor")
def _(ap, I, O):
    out = {}
    with ap.da.SearchCursor(I["points"], ["pid", "val", "SHAPE@X", "SHAPE@Y"], "pid <= 4") as c:
        out["fields"] = list(c.fields)
        out["rows"] = [[r[0], r[1], _r(r[2], 3), _r(r[3], 3)] for r in c]
    with ap.da.SearchCursor(I["polys"], ["gid", "SHAPE@AREA", "SHAPE@LENGTH", "SHAPE@XY", "SHAPE@WKT"],
                            sql_clause=(None, "ORDER BY val DESC")) as c:
        out["ordered"] = [[r[0], _r(r[1], 4), _r(r[2], 4), [_r(v, 4) for v in r[3]]] for r in c]
    with ap.da.SearchCursor(I["polys"], ["SHAPE@WKT"], "gid = 4") as c:
        out["wkt"] = next(c)[0]
    with ap.da.SearchCursor(I["points"], ["pid", "SHAPE@XY"], "pid IN (1, 2)",
                            spatial_reference=ap.SpatialReference(4326)) as c:
        out["projected"] = [[r[0], _r(r[1][0], 7), _r(r[1][1], 7)] for r in c]
    with ap.da.SearchCursor(I["table"], ["cat", "val"], sql_clause=("DISTINCT", None)) as c:
        out["distinct"] = len(list(c))
    folder = O("gp", "folder")
    db = ap.management.CreateSQLiteDatabase(os.path.join(folder, "gp.gpkg"), "GEOPACKAGE")[0]
    ap.management.CopyRows(I["table"], os.path.join(str(db), "rows"))
    with ap.da.SearchCursor(os.path.join(str(db), "rows"), "*") as c:
        out["star"] = list(c.fields)
    with ap.da.SearchCursor(I["points"], ["pid"], "pid < 3") as c:
        first = [r[0] for r in c]
        c.reset()
        out["reset"] = [first, [r[0] for r in c]]
    with ap.da.SearchCursor(I["points"], ["OID@", "SHAPE@JSON"], "pid = 1") as c:
        import json
        row = next(c)
        out["oid"] = row[0]
        out["json"] = sorted(json.loads(row[1]).keys())
    return flat(out)


@case("da.InsertCursor")
def _(ap, I, O):
    out = O("out")
    ap.management.CopyFeatures(I["points"], out)
    with ap.da.InsertCursor(out, ["pid", "val", "cat", "SHAPE@XY"]) as c:
        fields = list(c.fields)
        oid = c.insertRow((101, 2.5, "q", (I["x0"] + 1, I["y0"] + 2)))
        c.insertRow([102, None, None, (I["x0"] + 3, I["y0"] + 4)])
    with ap.da.InsertCursor(out, ["pid", "SHAPE@WKT"]) as c:
        c.insertRow((103, f"POINT ({I['x0'] + 9} {I['y0'] + 9})"))
    return flat({"fields": fields, "oid": oid})


@case("da.UpdateCursor")
def _(ap, I, O):
    out = O("out")
    ap.management.CopyFeatures(I["polys"], out)
    with ap.da.UpdateCursor(out, ["gid", "val", "SHAPE@"], "cat <> 'c'") as c:
        for gid, val, shape in c:
            if gid == 3:
                c.deleteRow()
            else:
                c.updateRow([gid, val * 10, shape.buffer(-5)])
    with ap.da.UpdateCursor(out, ["cat"], sql_clause=(None, "ORDER BY gid")) as c:
        for row in c:
            c.updateRow([row[0].upper()])


# ---- describing ---------------------------------------------------------------------------------------------------


@case("da.Describe")
def _(ap, I, O):
    folder = O("gp", "folder")
    db = ap.management.CreateSQLiteDatabase(os.path.join(folder, "gp.gpkg"), "GEOPACKAGE")[0]
    fc = os.path.join(str(db), "polys")
    ap.management.CopyFeatures(I["polys"], fc)
    d = ap.da.Describe(fc)
    keys = ("dataType", "name", "baseName", "shapeType", "featureType", "hasM", "hasZ", "hasOID", "hasSpatialIndex",
            "datasetType", "extension", "canVersion", "isVersioned", "aliasName", "areaFieldName", "lengthFieldName")
    r = ap.da.Describe(I["dem"])
    e = d["extent"]
    return flat({"fc": {k: d.get(k, "<none>") for k in keys}, "fields": [f.name for f in d["fields"]
                                                                          if f.type not in ("OID", "Geometry")],
                 "sr": d["spatialReference"].factoryCode, "extent": [_r(e.XMin, 3), _r(e.YMin, 3), _r(e.XMax, 3),
                                                                      _r(e.YMax, 3)],
                 "raster": {k: r.get(k, "<none>") for k in ("dataType", "bandCount", "format", "height", "width",
                                                             "meanCellWidth", "pixelType", "isInteger")},
                 "table": ap.da.Describe(I["table"])["dataType"]})


@case("da.Walk")
def _(ap, I, O):
    gdb = _gdb(ap, O, "walk")
    sr = ap.SpatialReference(I["srid"])
    ap.management.CreateFeatureDataset(gdb, "fd", sr)
    ap.management.CopyFeatures(I["points"], os.path.join(gdb, "pts"))
    ap.management.CopyFeatures(I["lines"], os.path.join(gdb, "fd", "lns"))
    ap.management.CopyRows(I["table"], os.path.join(gdb, "tab"))
    out = {}
    for label, kwargs in (("all", {}), ("fc", {"datatype": "FeatureClass"}), ("table", {"datatype": "Table"}),
                          ("polyline", {"datatype": "FeatureClass", "type": "Polyline"})):
        # The workspace's own name without its extension: grapy writes CreateFileGDB's .gdb as a .gpkg (its
        # documented INFERRED choice), so "walk.gdb" and "walk.gpkg" name the same workspace.
        out[label] = sorted([os.path.splitext(os.path.basename(d))[0], sorted(n), sorted(f)]
                            for d, n, f in ap.da.Walk(gdb, **kwargs))
    return flat(out)


# ---- domains, subtypes, contingent values, sequences --------------------------------------------------------------


def _domains(ap, O):
    gdb = _gdb(ap, O, "dom")
    ap.management.CreateDomain(gdb, "kinds", "Road kinds", "SHORT", "CODED")
    for code, text in ((1, "road"), (2, "track"), (3, "river")):
        ap.management.AddCodedValueToDomain(gdb, "kinds", code, text)
    ap.management.CreateDomain(gdb, "speed", "Speed", "DOUBLE", "RANGE", "DUPLICATE", "SUM_VALUES")
    ap.management.SetValueForRangeDomain(gdb, "speed", 0, 130)
    return gdb


def _domain(d):
    return _attrs(d, ("name", "description", "domainType", "type", "codedValues", "range", "splitPolicy",
                      "mergePolicy"))


@case("da.ListDomains")
def _(ap, I, O):
    gdb = _domains(ap, O)
    return flat({"domains": sorted((_domain(d) for d in ap.da.ListDomains(gdb)), key=lambda d: d["name"])})


@case("da.Domain")
def _(ap, I, O):
    gdb = _domains(ap, O)
    d = [x for x in ap.da.ListDomains(gdb) if x.name == "kinds"][0]
    return flat({"kinds": _domain(d)})


@case("da.ListSubtypes")
def _(ap, I, O):
    gdb = _domains(ap, O)
    fc = os.path.join(gdb, "lines")
    ap.management.CopyFeatures(I["lines"], fc)
    plain = ap.da.ListSubtypes(fc)
    ap.management.AddField(fc, "stype", "SHORT")
    ap.management.AddField(fc, "kcode", "SHORT")
    ap.management.SetSubtypeField(fc, "stype")
    ap.management.AddSubtype(fc, 1, "Major")
    ap.management.AddSubtype(fc, 2, "Minor")
    ap.management.SetDefaultSubtype(fc, 2)
    ap.management.AssignDomainToField(fc, "kcode", "kinds", "1")
    ap.management.AssignDefaultToField(fc, "kcode", 2, "2")
    subs = ap.da.ListSubtypes(fc)

    def one(s):
        return {"Name": s["Name"], "Default": s["Default"], "SubtypeField": s["SubtypeField"],
                "kcode": [s["FieldValues"]["kcode"][0], getattr(s["FieldValues"]["kcode"][1], "name", None)],
                "fields": sorted(k for k in s["FieldValues"] if k.lower() not in ("objectid", "fid", "shape",
                                                                                  "shape_length"))}
    return flat({"plain": {str(k): [v["Name"], v["Default"], v["SubtypeField"]] for k, v in plain.items()},
                 "subs": {str(k): one(v) for k, v in sorted(subs.items())}})


def _contingent(ap, I, O):
    gdb = _domains(ap, O)
    fc = os.path.join(gdb, "lines")
    ap.management.CopyFeatures(I["lines"], fc)
    ap.management.AddField(fc, "kcode", "SHORT")
    ap.management.AddField(fc, "speed", "DOUBLE")
    ap.management.AssignDomainToField(fc, "kcode", "kinds")
    ap.management.AssignDomainToField(fc, "speed", "speed")
    ap.management.CreateFieldGroup(fc, "grp", ["kcode", "speed"])
    ap.management.AddContingentValue(fc, "grp", [["kcode", "CODED_VALUE", 1], ["speed", "RANGE", "50;130"]])
    ap.management.AddContingentValue(fc, "grp", [["kcode", "CODED_VALUE", 2], ["speed", "ANY"]])
    return fc


def _cfv(v):
    return _attrs(v, ("fieldName", "name", "type", "code", "codedValue", "range", "minValue", "maxValue"))


def _cv(c):
    out = _attrs(c, ("id", "fieldGroupName", "subtype", "isRetired", "type"))
    out["values"] = [_cfv(v) for v in getattr(c, "values", [])]
    return out


@case("da.ListContingentValues")
def _(ap, I, O):
    fc = _contingent(ap, I, O)
    return flat({"all": [_cv(c) for c in ap.da.ListContingentValues(fc)],
                 "grp": len(ap.da.ListContingentValues(fc, "grp"))})


@case("da.ContingentValue")
def _(ap, I, O):
    fc = _contingent(ap, I, O)
    c = ap.da.ListContingentValues(fc)[0]
    return flat({"cv": _cv(c)})


@case("da.ContingentFieldValue")
def _(ap, I, O):
    fc = _contingent(ap, I, O)
    values = ap.da.ListContingentValues(fc)[1].values
    return flat({"values": [_cfv(v) for v in values]})


def _sequences(ap, O):
    gdb = _gdb(ap, O, "seq")
    ap.management.CreateDatabaseSequence(gdb, "ids", 10, 5)
    ap.management.CreateDatabaseSequence(gdb, "other")
    return gdb


def _sequence(s):
    return _attrs(s, ("name", "startValue", "incrementValue", "currentValue"))


@case("da.ListDatabaseSequences")
def _(ap, I, O):
    gdb = _sequences(ap, O)
    return flat({"all": sorted((_sequence(s) for s in ap.da.ListDatabaseSequences(gdb)), key=lambda s: s["name"])})


@case("da.DatabaseSequence")
def _(ap, I, O):
    gdb = _sequences(ap, O)
    s = [x for x in ap.da.ListDatabaseSequences(gdb) if x.name == "ids"][0]
    return flat({"ids": _sequence(s)})


# ---- relationships ------------------------------------------------------------------------------------------------


@case("da.SearchRelatedRecords")
def _(ap, I, O):
    gdb = _gdb(ap, O, "rel")
    origin, dest = os.path.join(gdb, "polys"), os.path.join(gdb, "rows")
    ap.management.CopyFeatures(I["polys"], origin)
    ap.management.CopyRows(I["table"], dest)
    ap.management.AddField(dest, "gid", "LONG")
    with ap.da.UpdateCursor(dest, ["rid", "gid"]) as c:
        for rid, _ in c:
            c.updateRow([rid, (rid % 3) + 1])
    rc = os.path.join(gdb, "poly_rows")
    ap.management.CreateRelationshipClass(origin, dest, rc, "SIMPLE", "rows", "poly", "NONE", "ONE_TO_MANY", "NONE",
                                          "gid", "gid")
    forward = sorted(list(map(list, pair)) for pair in ap.da.SearchRelatedRecords(rc, "*", ["gid"], ["rid"]))
    some = sorted(list(map(list, pair)) for pair in ap.da.SearchRelatedRecords(rc, [1], ["gid"], ["rid", "val"]))
    return flat({"forward": forward, "one": some})


# ---- NumPy and Arrow ----------------------------------------------------------------------------------------------


def _array(a):
    return {"names": list(a.dtype.names), "kinds": [a.dtype[n].kind for n in a.dtype.names], "count": len(a),
            "rows": [[(_r(v, 4) if isinstance(v, float) else (list(map(lambda x: _r(x, 3), v)) if hasattr(v, "__len__")
                       and not isinstance(v, str) else (v.item() if hasattr(v, "item") else v))) for v in row]
                     for row in a.tolist()][:6]}


@case("da.TableToNumPyArray")
def _(ap, I, O):
    a = ap.da.TableToNumPyArray(I["table"], ["rid", "val", "cat"], "rid < 5")
    b = ap.da.TableToNumPyArray(I["table"], ["val"], skip_nulls=True)
    return flat({"a": _array(a), "b.count": len(b), "dtype": [str(a.dtype["rid"]), str(a.dtype["val"])]})


@case("da.FeatureClassToNumPyArray")
def _(ap, I, O):
    a = ap.da.FeatureClassToNumPyArray(I["points"], ["pid", "val", "SHAPE@XY"], "pid <= 3")
    v = ap.da.FeatureClassToNumPyArray(I["lines"], ["lid", "SHAPE@X"], "lid = 3", explode_to_points=True)
    return flat({"a": _array(a), "explode": [len(v), [_r(x, 3) for x in v["SHAPE@X"]]]})


@case("da.NumPyArrayToTable")
def _(ap, I, O):
    import numpy as np
    a = np.array([(1, 2.5, "x"), (2, 3.5, "yy")], dtype=[("n", "<i4"), ("v", "<f8"), ("s", "U10")])
    ap.da.NumPyArrayToTable(a, O("out"))


@case("da.NumPyArrayToFeatureClass")
def _(ap, I, O):
    import numpy as np
    a = np.array([(1, I["x0"] + 10.0, I["y0"] + 20.0), (2, I["x0"] + 30.0, I["y0"] + 40.0)],
                 dtype=[("n", "<i4"), ("x", "<f8"), ("y", "<f8")])
    ap.da.NumPyArrayToFeatureClass(a, O("out"), ["x", "y"], ap.SpatialReference(I["srid"]))


@case("da.ExtendTable")
def _(ap, I, O):
    import numpy as np
    out = O("out")
    ap.management.CopyRows(I["table"], out)
    a = np.array([(1, 10.0), (3, 30.0), (99, 0.5)], dtype=[("rid", "<i4"), ("extra", "<f8")])
    ap.da.ExtendTable(out, "rid", a, "rid")


@case("da.CreateTable")
def _(ap, I, O):
    answer = ap.da.CreateTable(O("out"), [("name", "TEXT"), ("n", "LONG"), ("bad name", "DOUBLE")])
    return flat({"fields": list(answer[1]) if isinstance(answer, (list, tuple)) and len(answer) > 1 else None})


@case("da.TableToArrowTable")
def _(ap, I, O):
    t = ap.da.TableToArrowTable(I["table"], ["rid", "val", "cat"], "rid <= 3")
    g = ap.da.TableToArrowTable(I["points"], ["pid"], "pid = 1", geometry_encoding="WKT")
    return flat({"names": t.column_names, "types": [str(x) for x in t.schema.types], "rows": t.num_rows,
                 "rid": t.column("rid").to_pylist(), "geom": [c for c in g.column_names],
                 "geom.meta": {k.decode(): v.decode()[:20] for k, v in (g.schema.field(g.column_names[-1]).metadata
                                                                         or {}).items()
                               if k.decode() != "esri.sr_wkt"}})


# ---- editing and versions -----------------------------------------------------------------------------------------


@case("da.Editor")
def _(ap, I, O):
    gdb = _gdb(ap, O, "edit")
    fc = os.path.join(gdb, "pts")
    ap.management.CopyFeatures(I["points"], fc)
    with ap.da.Editor(gdb):
        with ap.da.UpdateCursor(fc, ["val"], "pid <= 3") as c:
            for row in c:
                c.updateRow([row[0] + 1000])
    e = ap.da.Editor(gdb)
    e.startEditing(False, False)
    e.startOperation()
    with ap.da.InsertCursor(fc, ["pid", "SHAPE@XY"]) as c:
        c.insertRow((500, (I["x0"], I["y0"])))
    e.stopOperation()
    editing = e.isEditing
    e.stopEditing(False)
    with ap.da.SearchCursor(fc, ["pid", "val"], "pid <= 3 OR pid = 500") as c:
        rows = sorted([r[0], _r(r[1], 3)] for r in c)
    return flat({"rows": rows, "isEditing": [editing, e.isEditing]})


@case("da.ListVersions")
def _(ap, I, O):
    return flat({"gdb": [v.name for v in ap.da.ListVersions(I["gdb"])]})


@case("da.Version")
def _(ap, I, O):
    versions = ap.da.ListVersions(I["gdb"])
    return flat({"first": _attrs(versions[0], ("name", "parentVersionName", "access", "isOwner"))})


@case("da.ListFieldConflictFilters")
def _(ap, I, O):
    return flat({"points": list(ap.da.ListFieldConflictFilters(I["points"]))})
