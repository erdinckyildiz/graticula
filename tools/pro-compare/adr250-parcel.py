"""ADR-250 condition 2: a parcel fabric in ArcGIS Pro 3.7 (file geodatabase) and in grapy (GeoPackage), built from the
cases of src/grapy/tests/test_parcel.py and test_parcel_lsa.py.

    "C:/Program Files/ArcGIS/Pro/bin/Python/envs/arcgispro-py3/python.exe" adr250-parcel.py pro   <folder> [case ...]
    D:/temp/grapy-env/Scripts/python.exe                                   adr250-parcel.py grapy <folder> <grapy src> [case ...]

Cases: schema, build, seeds, lsa_weighted, lsa_blunder, sequence, gaps. Writes <folder>/adr250_<side>.json, one
document {case: answer or error}. Every class is read with all its fields, so the two sides' field names show as
they are. A tool, not a test: it needs Pro.
"""
import json
import math
import os
import shutil
import sys
import traceback

side, folder = sys.argv[1], sys.argv[2]
rest = sys.argv[3:]
if side == "grapy":
    sys.path.insert(0, rest.pop(0))
    import grapy as arcpy
    EXT = ".gpkg"
else:
    import arcpy
    EXT = ".gdb"
cases = rest or ["schema", "build", "seeds", "topology", "seed_sizes", "sequence", "gaps", "lsa_weighted",
                 "lsa_weighted_c", "lsa_full_c", "dir_true_w", "lsa_blunder_c", "lsa_blunder_w", "lsa_weighted_point"]
# The least-squares cases are compared (adr250-compare.py) where Pro's geodetic adjustment and grapy's grid one see the
# same distances: ADR250_SRID=3035 ADR250_OFFSET=4321000,3210000 (LAEA Europe at its origin, scale 1). At 0,0 in
# EPSG:3395 Pro fails every direction set ("Factorization failed in Math Kernel Library"), and in EPSG:3857 it scales
# a north-south metre by 1/0.99331.
arcpy.env.overwriteOutput = True
# WGS 84 / World Mercator: conformal with scale 1 on the equator, where the cases lie, so Pro's geodetic adjustment and
# grapy's grid adjustment see the same distances (in Web Mercator a north-south metre at the equator is 0.99331 m on
# the ellipsoid, which Pro applies).
SR = arcpy.SpatialReference(int(os.environ.get("ADR250_SRID", "3395")))
DIAGONAL = 100.0 * math.sqrt(2.0)
A = "POLYGON((0 0, 50 0, 100 0, 100 100, 0 100, 0 0))"
B = "POLYGON((100 0, 200 0, 200 100, 100 100, 100 0))"
C = "POLYGON((0 100, 200 100, 200 200, 0 200, 0 100))"


import contextlib


@contextlib.contextmanager
def editing(gdb):
    """Pro edits a fabric's classes only in an edit session; grapy needs none."""
    if side == "grapy":
        yield
        return
    editor = arcpy.da.Editor(gdb)
    editor.startEditing(False, False)
    editor.startOperation()
    try:
        yield
    finally:
        editor.stopOperation()
        editor.stopEditing(True)


def gdb_of(path):
    path = str(path)
    return path[: path.lower().rfind(EXT) + len(EXT)]


OFFSET = [float(v) for v in os.environ.get("ADR250_OFFSET", "0,0").split(",")]


def shape(wkt):
    """The case's local coordinates, moved by ADR250_OFFSET (x,y) when it is set."""
    if OFFSET != [0.0, 0.0]:
        import re
        numbers = re.compile(r"(-?\d+(?:\.\d+)?) (-?\d+(?:\.\d+)?)")
        wkt = numbers.sub(lambda m: f"{float(m.group(1)) + OFFSET[0]} {float(m.group(2)) + OFFSET[1]}", wkt)
    return arcpy.FromWKT(wkt, SR)


def fabric(case):
    work = os.path.join(folder, f"adr250_{side}{os.environ.get('ADR250_TAG', '')}", case)
    if os.path.exists(work):
        shutil.rmtree(work)
    os.makedirs(work)
    gdb = arcpy.management.CreateFileGDB(work, "parcels")[0]
    fds = arcpy.management.CreateFeatureDataset(gdb, "Parcels", SR)[0]
    fab = arcpy.parcel.CreateParcelFabric(fds, "Fab")[0]
    arcpy.parcel.AddParcelType(fab, "Lot")
    return gdb, fds, fab


def plain(value):
    if value is None or isinstance(value, (int, float, str)):
        return round(value, 6) if isinstance(value, float) else value
    if hasattr(value, "WKT"):
        return value.WKT
    return str(value)


def table(path, where=None, skip=("created_user", "created_date", "last_edited_user", "last_edited_date")):
    fields = [f.name for f in arcpy.ListFields(path) if f.type not in ("Geometry", "OID") and f.name not in skip
              and not f.name.lower().startswith("shape_")]
    out = []
    with arcpy.da.SearchCursor(path, ["OID@", "SHAPE@WKT"] + fields, where) as cursor:
        for row in cursor:
            out.append({"oid": row[0], "wkt": row[1], **{f: plain(v) for f, v in zip(fields, row[2:])}})
    return out


def record(fds, name, date=None):
    with editing(gdb_of(fds)), arcpy.da.InsertCursor(f"{fds}/Fab_Records", ["Name", "RecordedDate"]) as cursor:
        oid = cursor.insertRow([name, date])
    with arcpy.da.SearchCursor(f"{fds}/Fab_Records", ["OID@", "GlobalID"]) as cursor:
        return next(g for o, g in cursor if o == oid)


def schema():
    gdb, fds, fab = fabric("schema")
    out = {}
    for name in ("Fab_Records", "Fab_Points", "Fab_Connections", "Fab_AdjustmentLines", "Fab_AdjustmentPoints",
                 "Fab_AdjustmentVectors", "Lot", "Lot_Lines"):
        out[name] = [(f.name, f.aliasName, f.type, f.domain, f.defaultValue) for f in arcpy.ListFields(f"{fds}/{name}")]
    out["domains"] = {d.name: sorted((int(k), v) for k, v in d.codedValues.items()) if d.codedValues else None
                      for d in arcpy.da.ListDomains(gdb)}
    return out


def build():
    gdb, fds, fab = fabric("build")
    date = lambda y: __import__("datetime").datetime(y, 1, 1)
    guids = {record(fds, "R1", date(2020)): "R1", record(fds, "R2", date(2021)): "R2", record(fds, "R3"): "R3"}
    names = {v: k for k, v in guids.items()}
    with editing(gdb_of(fds)), arcpy.da.InsertCursor(f"{fds}/Lot", ["Name", "CreatedByRecord", "SHAPE@"]) as cursor:
        cursor.insertRow(["A", names["R1"], shape(A)])
        cursor.insertRow(["B", names["R2"], shape(B)])
        cursor.insertRow(["C", names["R3"], shape(C)])
    out = {}
    try:
        arcpy.parcel.BuildParcelFabric(fab)
        out["build_disabled"] = "ran"
    except Exception as e:
        out["build_disabled"] = str(e)[:300]
    try:
        arcpy.parcel.EnableParcelTopology(fab)
    except Exception as e:
        out["enable"] = str(e)[:300]
    arcpy.parcel.BuildParcelFabric(fab)
    key = lambda g: guids.get(g) or guids.get(str(g).upper()) or g
    lines = table(f"{fds}/Lot_Lines")
    for ln in lines:
        ln["CreatedByRecord"] = key(ln["CreatedByRecord"])
    out["lines"] = lines
    points = table(f"{fds}/Fab_Points")
    for p in points:
        p["CreatedByRecord"] = key(p["CreatedByRecord"])
    out["points"] = points
    out["records"] = [{"Name": r["Name"], "ParcelCount": r["ParcelCount"], "RetiredParcelCount": r["RetiredParcelCount"],
                       "wkt": r["wkt"]} for r in table(f"{fds}/Fab_Records")]
    out["parcels"] = table(f"{fds}/Lot")
    arcpy.parcel.BuildParcelFabric(fab)
    out["again"] = [len(table(f"{fds}/Lot_Lines")), len(table(f"{fds}/Fab_Points"))]
    arcpy.parcel.SetParcelLineLabelPosition(f"{fds}/Lot_Lines")
    out["labels"] = [(r["wkt"], r["LabelPosition"]) for r in table(f"{fds}/Lot_Lines")]
    return out


def seeds():
    gdb, fds, fab = fabric("seeds")
    r4 = record(fds, "R4")
    corners = [(300, 0), (400, 0), (400, 100), (300, 100)]
    with editing(gdb_of(fds)), arcpy.da.InsertCursor(f"{fds}/Lot_Lines", ["CreatedByRecord", "SHAPE@"]) as cursor:
        for a, b in zip(corners, corners[1:] + corners[:1]):
            cursor.insertRow([r4, shape(f"LINESTRING({a[0]} {a[1]}, {b[0]} {b[1]})")])
    out = {}
    arcpy.parcel.CreateParcelSeeds(fab, "r4")
    out["seeds"] = table(f"{fds}/Lot")
    arcpy.parcel.CreateParcelSeeds(fab, "R4")
    out["again"] = len(table(f"{fds}/Lot"))
    arcpy.parcel.EnableParcelTopology(fab)
    arcpy.parcel.BuildParcelFabric(fab)
    out["built"] = table(f"{fds}/Lot")
    out["points"] = len(table(f"{fds}/Fab_Points"))
    out["records"] = [(r["Name"], r["ParcelCount"]) for r in table(f"{fds}/Fab_Records")]
    return out


DRAWN = {"P1": (0, 0), "P2": (100, 0), "P3": (100.3, 100), "P4": (0.3, 100)}
TRUE = {"P1": (0, 0), "P2": (100, 0), "P3": (100, 100), "P4": (0, 100)}
SIDES = [("P1", "P2", 100.0, None), ("P2", "P3", 100.0, None), ("P3", "P4", 100.0, None), ("P4", "P1", 100.0, None),
         ("P1", "P3", DIAGONAL, None)]
FULL = [("P1", "P2", 100.0, 90.0), ("P2", "P3", 100.0, 0.0), ("P3", "P4", 100.0, 270.0), ("P4", "P1", 100.0, 180.0),
        ("P1", "P3", DIAGONAL, 45.0), ("P2", "P4", DIAGONAL, 315.0)]


def network(case, places, lines, fixed=("P1", "P2"), accuracy=None, parcel=False, constrained=(),
            options_direction_accuracy=30.0, weighted=()):
    """fixed: Is Fixed (Fixed Shape) = Yes; constrained: Adjustment Constraint = 2 (XYZ constrained)."""
    gdb, fds, fab = fabric(case)
    r = record(fds, "R")
    with editing(gdb_of(fds)), arcpy.da.InsertCursor(f"{fds}/Fab_Points", ["Name", "IsFixed", "AdjustmentConstraint",
                                                                           "XYAccuracy", "CreatedByRecord",
                                                                           "SHAPE@"]) as cursor:
        for name, (x, y) in places.items():
            cursor.insertRow([name, 1 if name in fixed else 0, 2 if name in constrained else 1,
                              0.01 if name in weighted else None, r, shape(f"POINT({x} {y})")])
    fields = ["CreatedByRecord", "Distance", "Direction", "DirectionAccuracy", "SHAPE@"]
    direction_accuracy = options_direction_accuracy
    if accuracy is not None:
        fields.insert(3, "DistanceAccuracy")
    with editing(gdb_of(fds)), arcpy.da.InsertCursor(f"{fds}/Lot_Lines", fields) as cursor:
        for a, b, distance, direction in lines:
            (xa, ya), (xb, yb) = places[a], places[b]
            row = [r, distance, direction, direction_accuracy, shape(f"LINESTRING({xa} {ya}, {xb} {yb})")]
            if accuracy is not None:
                row.insert(3, accuracy)
            cursor.insertRow(row)
    if parcel:
        ring = ", ".join(f"{places[p][0]} {places[p][1]}" for p in ("P1", "P2", "P3", "P4", "P1"))
        with editing(gdb_of(fds)), arcpy.da.InsertCursor(f"{fds}/Lot", ["CreatedByRecord", "SHAPE@"]) as cursor:
            cursor.insertRow([r, shape(f"POLYGON(({ring}))")])
    return gdb, fds, fab


def adjustment(fds):
    out = {}
    for name in ("Fab_AdjustmentLines", "Fab_AdjustmentPoints", "Fab_AdjustmentVectors"):
        rows = table(f"{fds}/{name}")
        for r in rows:
            for k in [k for k in r if k in ("PointID", "LineID", "GlobalID")]:
                r.pop(k)
        out[name] = rows
    return out


def lsa_weighted():
    gdb, fds, fab = network("lsa_weighted", DRAWN, SIDES, parcel=True)
    out = {}
    try:
        res = arcpy.parcel.AnalyzeParcelsByLeastSquaresAdjustment(fab, "WEIGHTED_LEAST_SQUARES")
        out["messages"] = res.getMessages() if hasattr(res, "getMessages") else None
    except Exception as e:
        out["error"] = str(e)[:500]
        return out
    out.update(adjustment(fds))
    try:
        arcpy.parcel.ApplyParcelLeastSquaresAdjustment(fab, None, "UPDATE_ATTRIBUTES")
        out["applied_points"] = [(p["Name"], p["wkt"], p.get("XYUncertainty"), p.get("EllipseMajor",
                                  p.get("ErrorEllipseSemiMajor"))) for p in table(f"{fds}/Fab_Points")]
        out["applied_parcel"] = [p["wkt"] for p in table(f"{fds}/Lot")]
    except Exception as e:
        out["apply_error"] = str(e)[:500]
    return out


def lsa_run(case, places, lines, kind, **options):
    gdb, fds, fab = network(case, places, lines, **options)
    out = {}
    try:
        res = arcpy.parcel.AnalyzeParcelsByLeastSquaresAdjustment(fab, kind)
        out["messages"] = res.getMessages() if hasattr(res, "getMessages") else None
    except Exception as e:
        out["error"] = str(e)[:500]
        return out
    out.update(adjustment(fds))
    if kind == "WEIGHTED_LEAST_SQUARES":
        try:
            arcpy.parcel.ApplyParcelLeastSquaresAdjustment(fab, None, "UPDATE_ATTRIBUTES")
            out["applied_points"] = [{k: v for k, v in p.items() if k not in ("GlobalID", "CreatedByRecord")}
                                     for p in table(f"{fds}/Fab_Points")]
            out["applied_lines"] = [p["wkt"] for p in table(f"{fds}/Lot_Lines")]
            out["applied_parcel"] = [p["wkt"] for p in table(f"{fds}/Lot")]
        except Exception as e:
            out["apply_error"] = str(e)[:500]
    return out


BLUNDERED = [(a, b, 100.05 if (a, b) == ("P2", "P3") else d, z) for a, b, d, z in FULL]


def lsa_weighted_c():
    return lsa_run("lsa_weighted_c", DRAWN, SIDES, "WEIGHTED_LEAST_SQUARES", fixed=(), constrained=("P1", "P2"),
                   parcel=True)


def lsa_full_c():
    return lsa_run("lsa_full_c", DRAWN, FULL, "WEIGHTED_LEAST_SQUARES", fixed=(), constrained=("P1", "P2"), parcel=True)


def lsa_blunder_c():
    return lsa_run("lsa_blunder_c", TRUE, BLUNDERED, "CONSISTENCY_CHECK", fixed=(), constrained=("P1", "P2"),
                   accuracy=0.01, parcel=True)


def lsa_blunder_free():
    return lsa_run("lsa_blunder_free", TRUE, BLUNDERED, "CONSISTENCY_CHECK", fixed=(), accuracy=0.01, parcel=True)


def lsa_blunder_w():
    return lsa_run("lsa_blunder_w", TRUE, BLUNDERED, "WEIGHTED_LEAST_SQUARES", fixed=(), constrained=("P1", "P2"),
                   accuracy=0.01, parcel=True)


def dir_true_cc():
    return lsa_run("dir_true_cc", TRUE, FULL, "CONSISTENCY_CHECK", fixed=(), constrained=("P1", "P2"), parcel=True)


def dir_true_w():
    return lsa_run("dir_true_w", TRUE, FULL, "WEIGHTED_LEAST_SQUARES", fixed=(), constrained=("P1", "P2"))


def dir_noacc():
    return lsa_run("dir_noacc", TRUE, FULL, "WEIGHTED_LEAST_SQUARES", fixed=(), constrained=("P1", "P2"),
                   options_direction_accuracy=None)


def dir_only():
    lines = [(a, b, d if (a, b) == ("P1", "P2") else None, z) for a, b, d, z in FULL]
    return lsa_run("dir_only", TRUE, lines, "WEIGHTED_LEAST_SQUARES", fixed=(), constrained=("P1", "P2"))


def dir_out2():
    lines = [(a, b, d, None if a in ("P3", "P4") else z) for a, b, d, z in FULL]
    return lsa_run("dir_out2", DRAWN, lines, "WEIGHTED_LEAST_SQUARES", fixed=(), constrained=("P1", "P2"))


def dir_single():
    lines = [(a, b, d, z if (a, b) == ("P1", "P2") else None) for a, b, d, z in FULL]
    return lsa_run("dir_single", DRAWN, lines, "WEIGHTED_LEAST_SQUARES", fixed=(), constrained=("P1", "P2"))


def dir_two_at_p1():
    lines = [(a, b, d, z if a == "P1" else None) for a, b, d, z in FULL]
    return lsa_run("dir_two_at_p1", DRAWN, lines, "WEIGHTED_LEAST_SQUARES", fixed=(), constrained=("P1", "P2"))


def lsa_weighted_point():
    """P1 constrained, P2 only weighted (XY Accuracy 0.01 m): does a weighted point count as control?"""
    return lsa_run("lsa_weighted_point", DRAWN, SIDES, "WEIGHTED_LEAST_SQUARES", fixed=(), constrained=("P1",),
                   weighted=("P2",))


def lsa_blunder():
    blundered = [(a, b, 100.05 if (a, b) == ("P2", "P3") else d, z) for a, b, d, z in FULL]
    gdb, fds, fab = network("lsa_blunder", TRUE, blundered, accuracy=0.01, parcel=True)
    out = {}
    try:
        res = arcpy.parcel.AnalyzeParcelsByLeastSquaresAdjustment(fab, "CONSISTENCY_CHECK")
        out["messages"] = res.getMessages() if hasattr(res, "getMessages") else None
    except Exception as e:
        out["error"] = str(e)[:500]
        return out
    out.update(adjustment(fds))
    return out


def messages(call):
    try:
        result = call()
        return "ok " + (result.getMessages(1) if hasattr(result, "getMessages") else arcpy.GetMessages(1))[:200]
    except Exception as e:
        return "ERR " + str(e)[:200]


def topology():
    """Is a new fabric's topology enabled, and may a type be added to an enabled one?"""
    gdb, fds, fab = fabric("topology")
    return {"enable_new": messages(lambda: arcpy.parcel.EnableParcelTopology(fab)),
            "add_type_enabled": messages(lambda: arcpy.parcel.AddParcelType(fab, "Tax")),
            "disable": messages(lambda: arcpy.parcel.DisableParcelTopology(fab)),
            "disable_again": messages(lambda: arcpy.parcel.DisableParcelTopology(fab))}


def seed_sizes():
    """A seed's size in loops 10, 100 and 1,000 m square and 400 by 20 m; then a whole-fabric build, and one named by
    the record."""
    gdb, fds, fab = fabric("seed_sizes")
    r5 = record(fds, "R5")
    loops = {"s10": (500, 0, 10, 10), "s100": (700, 0, 100, 100), "s1000": (2000, 0, 1000, 1000),
             "rect": (5000, 0, 400, 20)}
    with editing(gdb_of(fds)), arcpy.da.InsertCursor(f"{fds}/Lot_Lines", ["CreatedByRecord", "SHAPE@"]) as cursor:
        for x0, y0, w, h in loops.values():
            corners = [(x0, y0), (x0 + w, y0), (x0 + w, y0 + h), (x0, y0 + h)]
            for a, b in zip(corners, corners[1:] + corners[:1]):
                cursor.insertRow([r5, shape(f"LINESTRING({a[0]} {a[1]}, {b[0]} {b[1]})")])
    arcpy.parcel.CreateParcelSeeds(fab, "R5")

    def held():
        out = []
        with arcpy.da.SearchCursor(f"{fds}/Lot", ["SHAPE@", "IsSeed"]) as cursor:
            for g, seed in cursor:
                out.append({"area": round(g.area, 4), "seed": seed, "centre": (round(g.centroid.X, 3), round(g.centroid.Y, 3))})
        return sorted(out, key=lambda r: r["centre"])
    out = {"seeds": held()}
    arcpy.parcel.BuildParcelFabric(fab)
    out["whole_build"] = held()
    out["count_after_whole_build"] = [(r["Name"], r["ParcelCount"]) for r in table(f"{fds}/Fab_Records")]
    arcpy.parcel.BuildParcelFabric(fab, None, "R5")
    out["record_build"] = held()
    return out


def sequence():
    gdb, fds, fab = fabric("sequence")
    r1 = record(fds, "R1")
    with editing(gdb_of(fds)), arcpy.da.InsertCursor(f"{fds}/Lot", ["Name", "CreatedByRecord", "SHAPE@"]) as cursor:
        cursor.insertRow(["A", r1, shape(A)])
    arcpy.parcel.EnableParcelTopology(fab)
    arcpy.parcel.BuildParcelFabric(fab)
    out = {}
    for corner, pf, lf in (("NE", "P#", "ABC-##1"), ("SW", "#", "#"), ("NW", "#", "#"), ("SE", "#", "#")):
        target = arcpy.management.CreateFeatureDataset(gdb, f"Seq{corner}", SR)[0]
        layer = arcpy.management.MakeFeatureLayer(f"{fds}/Lot", f"a{corner}", "Name = 'A'")[0]
        arcpy.management.SelectLayerByAttribute(layer, "NEW_SELECTION", "Name = 'A'")
        try:
            arcpy.parcel.ExportSequencedParcelFeatures(layer, target, corner, pf, lf)
        except Exception as e:
            out[corner] = str(e)[:400]
            continue
        arcpy.env.workspace = target
        got = {}
        names = (arcpy.ListFeatureClasses() if side == "pro" else ["Lot", "Lot_Lines", "Fab_Points", "Fab_Records"])
        for name in names or []:
            got[name] = [(r["wkt"], r.get("Sequence")) for r in table(f"{target}/{name}")]
        out[corner] = got
    return out


def gaps():
    gdb, fds, fab = fabric("gaps")
    boxes = {"P1": (0, 0, 10, 10), "P2": (11, 0, 20, 10), "P3": (0, 10, 20, 20), "P4": (0, -10, 20, 0),
             "P5": (19.5, 0, 25, 10), "P6": (0, 0, 5, 5)}
    with editing(gdb_of(fds)), arcpy.da.InsertCursor(f"{fds}/Lot", ["Name", "SHAPE@"]) as cursor:
        for name, (x0, y0, x1, y1) in boxes.items():
            cursor.insertRow([name, shape(f"POLYGON(({x0} {y0}, {x1} {y0}, {x1} {y1}, {x0} {y1}, {x0} {y0}))")])
    out = {}
    go = arcpy.parcel.FindGapsAndOverlaps([f"{fds}/Lot"], f"{gdb}/go")[0]
    out["within"] = [(r["wkt"], {k: v for k, v in r.items() if k not in ("wkt", "oid")}) for r in table(go)]
    out["within_area"] = [round(arcpy.FromWKT(r["wkt"], SR).area, 6) for r in table(go)]
    first = arcpy.management.MakeFeatureLayer(f"{fds}/Lot", "first", "Name <> 'P5'")[0]
    second = arcpy.management.MakeFeatureLayer(f"{fds}/Lot", "second", "Name = 'P5'")[0]
    go2 = arcpy.parcel.FindGapsAndOverlaps([first, second], f"{gdb}/go2", "2 Meters", "BETWEEN_LAYERS")[0]
    out["between"] = [(round(arcpy.FromWKT(r["wkt"], SR).area, 6), {k: v for k, v in r.items() if k not in ("wkt", "oid")})
                      for r in table(go2)]
    return out


result = {}
for case in cases:
    try:
        result[case] = globals()[case]()
    except Exception:
        result[case] = {"exception": traceback.format_exc()[-1500:]}
    print(case, "done", flush=True)
path = os.path.join(folder, f"adr250_{side}{os.environ.get('ADR250_TAG', '')}.json")
previous = json.load(open(path)) if os.path.exists(path) else {}
previous.update(result)
json.dump(previous, open(path, "w"), indent=1, default=str)
