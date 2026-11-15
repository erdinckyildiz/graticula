"""arcpy.parcel against ArcGIS Pro 3.7: one case for each of the 25 names grapy builds.

Each case makes its own parcel fabric with the documented tools — a workspace of its own, a feature dataset
``Parcels``, a fabric ``Fab`` in it, and a parcel type ``Lot`` — so both sides build it the same way (the fixture's
origin, UTM 35N). Pro edits a fabric's classes only in an edit session (``ap.da.Editor``); grapy's Editor is a
transaction, so the same code runs on both. What a fabric holds is answered as JSON (``_rows``): each row's chosen
fields, a record's global ID as its name, shapes as coordinates relative to the fixture's origin to the millimetre.
An output feature class a tool writes through ``O`` is answered the same way (``_summary``) and deleted, since its
global IDs, GUIDs and editor tracking differ by run. CopyParcels takes only a parcel fabric *layer* in Pro, which
arcpy makes only in a map: not comparable here.
"""
import contextlib
import os
import shutil

CASES = {}
TRACKING = ("created_user", "created_date", "last_edited_user", "last_edited_date")


def case(name):
    def register(function):
        CASES[name] = function
        return function
    return register


A = [(0, 0), (50, 0), (100, 0), (100, 100), (0, 100)]
B = [(100, 0), (200, 0), (200, 100), (100, 100)]
C = [(0, 100), (200, 100), (200, 200), (0, 200)]


def _poly(I, ring):
    ring = list(ring) + [ring[0]]
    return "POLYGON ((" + ", ".join(f"{I['x0'] + x} {I['y0'] + y}" for x, y in ring) + "))"


def _line(I, a, b):
    return f"LINESTRING ({I['x0'] + a[0]} {I['y0'] + a[1]}, {I['x0'] + b[0]} {I['y0'] + b[1]})"


def _shape(ap, I, wkt):
    return ap.FromWKT(wkt, ap.SpatialReference(I["srid"]))


@contextlib.contextmanager
def editing(ap, workspace):
    editor = ap.da.Editor(workspace)
    editor.startEditing(False, False)
    editor.startOperation()
    try:
        yield
    finally:
        editor.stopOperation()
        editor.stopEditing(True)


class Fabric:
    """A workspace of its own (fabric class names are unique in a geodatabase, and in a GeoPackage), its feature
    dataset ``Parcels``, the fabric ``Fab`` in it and the parcel type ``Lot``."""

    def __init__(self, ap, I, O, tag, lot=True):
        self.ap, self.I = ap, I
        self.gdb = _workspace(ap, O, "fabric_" + tag)
        self.fds = os.path.join(self.gdb, "Parcels")
        ap.management.CreateFeatureDataset(self.gdb, "Parcels", ap.SpatialReference(I["srid"]))
        self.fab = ap.parcel.CreateParcelFabric(self.fds, "Fab")[0]
        if lot:
            ap.parcel.AddParcelType(self.fab, "Lot")
        self.names = {}

    def path(self, name):
        return os.path.join(self.fds, name)

    def record(self, name, date=None):
        with editing(self.ap, self.gdb), self.ap.da.InsertCursor(self.path("Fab_Records"),
                                                                 ["Name", "RecordedDate"]) as cursor:
            oid = cursor.insertRow([name, date])
        with self.ap.da.SearchCursor(self.path("Fab_Records"), ["OID@", "GlobalID"]) as cursor:
            guid = next(g for o, g in cursor if o == oid)
        self.names[_guid(guid)] = name
        return guid

    def insert(self, name, fields, rows):
        with editing(self.ap, self.gdb), self.ap.da.InsertCursor(self.path(name), fields + ["SHAPE@"]) as cursor:
            for *values, wkt in rows:
                cursor.insertRow(values + [_shape(self.ap, self.I, wkt)])

    def parcels(self, record_names=(("A", "R1", A), ("B", "R2", B), ("C", "R3", C))):
        guids = {}
        for _, rec, _ in record_names:
            if rec not in guids:
                guids[rec] = self.record(rec)
        self.insert("Lot", ["Name", "CreatedByRecord"], [[n, guids[r], _poly(self.I, ring)]
                                                         for n, r, ring in record_names])
        return guids

    def rows(self, name, fields, where=None):
        return _rows(self.ap, self.I, self.path(name), fields, self.names, where)


def _guid(value):
    return None if value is None else str(value).upper().strip("{}")


def _coords(I, shape):
    out = []
    for part in shape:
        for p in part:
            if p is not None:
                out.append((round(p.X - I["x0"], 3), round(p.Y - I["y0"], 3)))
    return out


def _geometry(I, shape):
    if shape is None:
        return None
    kind = shape.type.lower()
    if kind == "point":
        p = shape.firstPoint
        return [round(p.X - I["x0"], 3), round(p.Y - I["y0"], 3)]
    if kind == "polyline":
        return [list(c) for c in _coords(I, shape)]
    coords = _coords(I, shape)
    return {"area": round(shape.area, 3), "vertices": sorted({c for c in coords})}


def _rows(ap, I, path, fields, names=None, where=None):
    """Each row's fields (a GUID as the record name it is, or 'guid'), and its shape; sorted, so row order is not
    compared."""
    names = names or {}
    out = []
    with ap.da.SearchCursor(path, fields + ["SHAPE@"], where) as cursor:
        for row in cursor:
            values = []
            for v in row[:-1]:
                if isinstance(v, float):
                    v = round(v, 4)
                elif isinstance(v, str) and len(v) == 38 and v.startswith("{"):
                    v = names.get(_guid(v), "guid")
                elif v is not None and not isinstance(v, (int, str)):
                    v = str(v)
                values.append(v)
            out.append(values + [_geometry(I, row[-1])])
    return sorted(out, key=lambda r: str(r))


def _workspace(ap, O, name):
    folder = O(name, "folder")
    for entry in os.listdir(folder):
        target = os.path.join(folder, entry)
        shutil.rmtree(target) if os.path.isdir(target) else os.remove(target)
    return ap.management.CreateFileGDB(folder, "work")[0]


def _count(ap, path):
    return int(ap.management.GetCount(path)[0])


def _summary(ap, I, path):
    """An output a tool wrote: its field names (less the run-dependent ones — global IDs, GUIDs, editor tracking — and
    the format's own) and its rows without them; then the output is deleted, so the harness does not dump it with
    those fields (Pro will not delete a fabric output's CreatedByRecord: ERROR 002557)."""
    fields = [f for f in ap.ListFields(path) if f.type not in ("OID", "Geometry", "GlobalID")
              and f.name.lower() != "globalid" and not f.name.lower().startswith("shape_")]
    kept = [f.name for f in fields if f.type not in ("GUID", "Guid") and f.name.lower() not in TRACKING
            and f.name.upper() not in ("SOURCE_GUID", "CREATEDBYRECORD", "RETIREDBYRECORD", "GLOBALID")]
    out = {"fields": sorted(f.name for f in fields if f.name.lower() not in TRACKING),
           "rows": _rows(ap, I, path, kept)}
    ap.management.Delete(path)
    return out


def _built(ap, I, O, tag, lot=True):
    f = Fabric(ap, I, O, tag, lot)
    f.parcels()
    ap.parcel.BuildParcelFabric(f.fab)
    return f


LINE_FIELDS = ["CreatedByRecord", "RetiredByRecord", "Distance", "Direction", "LabelPosition", "IsSeed"]


# ---- creating a fabric and its types


@case("parcel.CreateParcelFabric")
def _(ap, I, O):
    f = Fabric(ap, I, O, "create", lot=False)
    names = ("Fab_Records", "Fab_Points", "Fab_Connections", "Fab_AdjustmentLines", "Fab_AdjustmentPoints",
             "Fab_AdjustmentVectors", "Fab_Topology")
    return {n: bool(ap.Exists(f.path(n))) for n in names}


@case("parcel.AddParcelType")
def _(ap, I, O):
    f = Fabric(ap, I, O, "addtype", lot=False)
    result = ap.parcel.AddParcelType(f.fab, "Tax", "TOPOLOGY_POLYGON", "STRATA_PARCELS")
    outputs = [os.path.basename(str(result.getOutput(i))) for i in range(result.outputCount)]
    return {"outputs": outputs,
            "Tax": sorted(x.name for x in ap.ListFields(f.path("Tax")) if x.type not in ("OID", "Geometry")
                          and not x.name.lower().startswith("shape_")),
            "Tax_Lines": sorted(x.name for x in ap.ListFields(f.path("Tax_Lines")) if x.type not in ("OID", "Geometry")
                                and not x.name.lower().startswith("shape_"))}


@case("parcel.RemoveParcelType")
def _(ap, I, O):
    f = Fabric(ap, I, O, "removetype")
    ap.parcel.AddParcelType(f.fab, "Tax")
    ap.parcel.DisableParcelTopology(f.fab)
    result = ap.parcel.RemoveParcelType(f.fab, "Tax")
    out = {"outputs": [os.path.basename(str(result.getOutput(i))) for i in range(result.outputCount)],
           "Tax_exists": bool(ap.Exists(f.path("Tax")))}
    try:
        ap.parcel.AddParcelType(f.fab, "Tax")
        out["add_again"] = "ok"
    except Exception:  # noqa: BLE001 — what each side says is the answer
        out["add_again"] = "error"
    return out


def _severity(ap, call):
    """2 when the call raises, 1 when it warns, else 0. Pro answers the last tool's messages; grapy keeps one list for
    the run (arcpy_top's open difference 1, the message model), so on grapy's side only the warnings the call added
    are counted."""
    before = len(ap.GetMessages(1).splitlines()) if ap.__name__ != "arcpy" else 0
    try:
        call()
    except Exception:  # noqa: BLE001
        return 2
    if ap.__name__ == "arcpy":
        return int(ap.GetMaxSeverity())
    return 1 if len(ap.GetMessages(1).splitlines()) > before else 0


@case("parcel.EnableParcelTopology")
def _(ap, I, O):
    f = Fabric(ap, I, O, "enable")
    return {"on_new": _severity(ap, lambda: ap.parcel.EnableParcelTopology(f.fab)),
            "after_disable": (_severity(ap, lambda: ap.parcel.DisableParcelTopology(f.fab)),
                              _severity(ap, lambda: ap.parcel.EnableParcelTopology(f.fab)))}


@case("parcel.DisableParcelTopology")
def _(ap, I, O):
    f = Fabric(ap, I, O, "disable")
    return {"first": _severity(ap, lambda: ap.parcel.DisableParcelTopology(f.fab)),
            "again": _severity(ap, lambda: ap.parcel.DisableParcelTopology(f.fab))}


# ---- records, seeds and building


@case("parcel.CreateParcelRecords")
def _(ap, I, O):
    f = Fabric(ap, I, O, "records")
    ap.management.AddField(f.path("Lot"), "Deed", "TEXT")
    f.insert("Lot", ["Name", "Deed"], [["A", "D-1", _poly(I, A)], ["B", "d-1", _poly(I, B)],
                                       ["C", "D-2", _poly(I, C)]])
    ap.parcel.CreateParcelRecords(f.path("Lot"), "Deed")
    with ap.da.SearchCursor(f.path("Fab_Records"), ["Name", "GlobalID"]) as cursor:
        for name, guid in cursor:
            f.names[_guid(guid)] = name
    return {"records": f.rows("Fab_Records", ["Name", "ParcelCount", "RetiredParcelCount"]),
            "parcels": [r[:2] for r in f.rows("Lot", ["Name", "CreatedByRecord"])]}


@case("parcel.CreateParcelSeeds")
def _(ap, I, O):
    f = Fabric(ap, I, O, "seeds")
    r4 = f.record("R4")
    corners = [(300, 0), (400, 0), (400, 100), (300, 100)]
    f.insert("Lot_Lines", ["CreatedByRecord"], [[r4, _line(I, a, b)] for a, b in zip(corners, corners[1:] + corners[:1])])
    ap.parcel.CreateParcelSeeds(f.fab, "r4")
    seeds = []
    with ap.da.SearchCursor(f.path("Lot"), ["IsSeed", "CreatedByRecord", "SHAPE@"]) as cursor:
        for seed, rec, shape in cursor:
            c = shape.centroid
            seeds.append([seed, f.names.get(_guid(rec)), round(shape.area, 2), round(c.X - I["x0"], 3),
                          round(c.Y - I["y0"], 3)])
    return {"seeds": seeds, "again": _severity(ap, lambda: ap.parcel.CreateParcelSeeds(f.fab, "R4")),
            "count_after_again": _count(ap, f.path("Lot"))}


@case("parcel.BuildParcelFabric")
def _(ap, I, O):
    f = _built(ap, I, O, "build")
    return {"lines": f.rows("Lot_Lines", ["CreatedByRecord"]),
            "points": f.rows("Fab_Points", ["CreatedByRecord"]),
            "records": f.rows("Fab_Records", ["Name", "ParcelCount"])}


@case("parcel.DeleteParcels")
def _(ap, I, O):
    f = _built(ap, I, O, "delete")
    layer = ap.management.MakeFeatureLayer(f.path("Lot"), "del_lot")[0]
    ap.management.SelectLayerByAttribute(layer, "NEW_SELECTION", "Name = 'B'")
    ap.parcel.DeleteParcels(layer)
    return {"parcels": [r[0] for r in f.rows("Lot", ["Name"])],
            "lines": [r[-1] for r in f.rows("Lot_Lines", [])],
            "points": [r[-1] for r in f.rows("Fab_Points", [])]}


@case("parcel.DuplicateParcels")
def _(ap, I, O):
    f = _built(ap, I, O, "duplicate")
    ap.parcel.AddParcelType(f.fab, "Tax")
    layer = ap.management.MakeFeatureLayer(f.path("Lot"), "dup_lot")[0]
    ap.management.SelectLayerByAttribute(layer, "NEW_SELECTION", "Name = 'A'")
    ap.parcel.DuplicateParcels(layer, "R9", "Tax")
    with ap.da.SearchCursor(f.path("Fab_Records"), ["Name", "GlobalID"]) as cursor:
        for name, guid in cursor:
            f.names[_guid(guid)] = name
    return {"tax": f.rows("Tax", ["Name", "CreatedByRecord"]), "tax_lines": f.rows("Tax_Lines", ["CreatedByRecord"]),
            "records": [r[0] for r in f.rows("Fab_Records", ["Name"])]}


# ---- selecting, labelling, merging


@case("parcel.SelectParcelFeatures")
def _(ap, I, O):
    f = _built(ap, I, O, "select")
    layer = ap.management.MakeFeatureLayer(f.path("Lot"), "sel_lot")[0]
    ap.management.SelectLayerByAttribute(layer, "NEW_SELECTION", "Name = 'A'")
    result = ap.parcel.SelectParcelFeatures(layer)
    lines, points = result.getOutput(0), result.getOutput(1)
    return {"lines": sorted(str(r[-1]) for r in _rows(ap, I, lines, [])),
            "points": sorted(str(r[-1]) for r in _rows(ap, I, points, []))}


@case("parcel.SetParcelLineLabelPosition")
def _(ap, I, O):
    f = _built(ap, I, O, "labels")
    ap.parcel.SetParcelLineLabelPosition(f.path("Lot_Lines"))
    return f.rows("Lot_Lines", ["LabelPosition"])


@case("parcel.MergeCollinearParcelBoundaries")
def _(ap, I, O):
    f = _built(ap, I, O, "merge")
    ap.parcel.MergeCollinearParcelBoundaries(f.path("Lot_Lines"), "0.1 Meters")
    return {"lines": f.rows("Lot_Lines", ["CreatedByRecord", "Distance", "COGOType"]),
            "points": [r[-1] for r in f.rows("Fab_Points", [])]}


# ---- least squares (adr250-parcel.py's lsa_weighted_c network: P1 and P2 XYZ constrained, four sides and a
# diagonal measured, P3 and P4 drawn 0.3 m east of where the measurements put them)

DRAWN = {"P1": (0, 0), "P2": (100, 0), "P3": (100.3, 100), "P4": (0.3, 100)}
SIDES = [("P1", "P2", 100.0), ("P2", "P3", 100.0), ("P3", "P4", 100.0), ("P4", "P1", 100.0),
         ("P1", "P3", 100.0 * 2 ** 0.5)]


def _network(ap, I, O, tag):
    f = Fabric(ap, I, O, tag)
    r = f.record("R")
    f.insert("Fab_Points", ["Name", "IsFixed", "AdjustmentConstraint", "CreatedByRecord"],
             [[n, 0, 2 if n in ("P1", "P2") else 1, r, f"POINT ({I['x0'] + x} {I['y0'] + y})"]
              for n, (x, y) in DRAWN.items()])
    f.insert("Lot_Lines", ["CreatedByRecord", "Distance"],
             [[r, d, _line(I, DRAWN[a], DRAWN[b])] for a, b, d in SIDES])
    ring = [DRAWN[p] for p in ("P1", "P2", "P3", "P4")]
    f.insert("Lot", ["CreatedByRecord"], [[r, _poly(I, ring)]])
    return f


@case("parcel.AnalyzeParcelsByLeastSquaresAdjustment")
def _(ap, I, O):
    f = _network(ap, I, O, "lsa")
    ap.parcel.AnalyzeParcelsByLeastSquaresAdjustment(f.fab, "WEIGHTED_LEAST_SQUARES")
    points = f.rows("Fab_AdjustmentPoints", ["Name"]) if "Name" in [x.name for x in ap.ListFields(
        f.path("Fab_AdjustmentPoints"))] else f.rows("Fab_AdjustmentPoints", [])
    return {"points": [[round(c, 3) for c in r[-1]] for r in points],
            "counts": [_count(ap, f.path(n)) for n in ("Fab_AdjustmentPoints", "Fab_AdjustmentLines",
                                                        "Fab_AdjustmentVectors")]}


@case("parcel.ApplyParcelLeastSquaresAdjustment")
def _(ap, I, O):
    f = _network(ap, I, O, "apply")
    ap.parcel.AnalyzeParcelsByLeastSquaresAdjustment(f.fab, "WEIGHTED_LEAST_SQUARES")
    ap.parcel.ApplyParcelLeastSquaresAdjustment(f.fab, None, "NO_UPDATE_ATTRIBUTES")
    return {"points": f.rows("Fab_Points", ["Name"]), "lines": [r[-1] for r in f.rows("Lot_Lines", [])],
            "parcel": [r[-1] for r in f.rows("Lot", [])]}


# ---- copying and appending


def _counts(ap, fds, names):
    return {n: _count(ap, os.path.join(fds, n)) if ap.Exists(os.path.join(fds, n)) else None for n in names}


CLASSES = ("Lot", "Lot_Lines", "Fab_Points", "Fab_Records", "Fab_Connections")


@case("parcel.CopyParcels")
def _(ap, I, O):
    f = _built(ap, I, O, "copy")
    target = _workspace(ap, O, "target")
    layer = ap.management.MakeFeatureLayer(f.path("Lot"), "copy_lot")[0]
    ap.management.SelectLayerByAttribute(layer, "NEW_SELECTION", "Name IN ('A', 'B')")
    result = ap.parcel.CopyParcels(layer, target, "Copied", "Fab")
    return {"outputs": [os.path.basename(str(result.getOutput(i))) for i in range(result.outputCount)],
            "counts": _counts(ap, os.path.join(target, "Copied"), CLASSES)}


@case("parcel.AppendParcels")
def _(ap, I, O):
    source = _built(ap, I, O, "append_src")
    target = Fabric(ap, I, O, "append_dst")
    ap.parcel.AppendParcels(source.fab, target.fab, "KEEP_BOTH")
    return {"counts": _counts(ap, target.fds, CLASSES),
            "parcels": [r[0] for r in target.rows("Lot", ["Name"])]}


# ---- exports and imports


@case("parcel.ExportParcelRecordFeatures")
def _(ap, I, O):
    f = _built(ap, I, O, "export")
    target = ap.management.CreateFeatureDataset(_workspace(ap, O, "export_out"), "Out",
                                                ap.SpatialReference(I["srid"]))[0]
    layer = ap.management.MakeFeatureLayer(f.path("Fab_Records"), "exp_rec")[0]
    ap.management.SelectLayerByAttribute(layer, "NEW_SELECTION", "Name = 'R2'")
    ap.parcel.ExportParcelRecordFeatures(layer, target)
    names = ("Lot", "Lot_Lines", "Fab_Points", "Fab_Records", "Fab_Connections")
    out = {}
    for n in names:
        p = os.path.join(target, n)
        out[n] = None if not ap.Exists(p) else sorted(
            str(r[:-1]) + str(r[-1]) for r in _rows(ap, I, p, ["ExportedStatus"] if "ExportedStatus" in
                                                     [x.name for x in ap.ListFields(p)] else []))
    return out


@case("parcel.ExportSequencedParcelFeatures")
def _(ap, I, O):
    f = Fabric(ap, I, O, "sequence")
    f.parcels((("A", "R1", A),))
    ap.parcel.BuildParcelFabric(f.fab)
    target = ap.management.CreateFeatureDataset(_workspace(ap, O, "sequence_out"), "Out",
                                                ap.SpatialReference(I["srid"]))[0]
    layer = ap.management.MakeFeatureLayer(f.path("Lot"), "seq_lot")[0]
    ap.management.SelectLayerByAttribute(layer, "NEW_SELECTION", "Name = 'A'")
    ap.parcel.ExportSequencedParcelFeatures(layer, target, "NE", "P#", "ABC-##1")
    out = {}
    for n in ("Lot_Lines", "Fab_Points"):
        p = os.path.join(target, n)
        out[n] = _rows(ap, I, p, ["Sequence"]) if ap.Exists(p) else None
    return out


@case("parcel.ImportParcelFabricPoints")
def _(ap, I, O):
    f = _built(ap, I, O, "import")
    source = os.path.join(I["gdb"], "pf_import_src")
    if ap.Exists(source):
        ap.management.Delete(source)
    ap.management.CreateFeatureclass(I["gdb"], "pf_import_src", "POINT", spatial_reference=ap.SpatialReference(I["srid"]))
    ap.management.AddField(source, "Name", "TEXT")
    with ap.da.InsertCursor(source, ["Name", "SHAPE@"]) as cursor:
        for name, (x, y) in (("Q1", (100.2, 0.1)), ("Q2", (200, 200.3)), ("Q3", (300, 300))):
            cursor.insertRow([name, _shape(ap, I, f"POINT ({I['x0'] + x} {I['y0'] + y})")])
    ap.parcel.ImportParcelFabricPoints(source, f.fab, "PROXIMITY", "1 Meters", "ALL", "R7")
    return {"points": f.rows("Fab_Points", ["Name"]), "lot": [r[-1]["vertices"] for r in f.rows("Lot", [])]}


# ---- checks


@case("parcel.FindAdjacentParcelPoints")
def _(ap, I, O):
    f = _built(ap, I, O, "adjacent")
    r = f.record("R5")
    f.insert("Fab_Points", ["Name", "CreatedByRecord"], [["near", r, f"POINT ({I['x0'] + 100.05} {I['y0'] + 0.0})"]])
    ap.parcel.FindAdjacentParcelPoints(f.path("Fab_Points"), O("out"), "0.1 Meters")
    return _summary(ap, I, O("out"))


@case("parcel.FindDisconnectedParcelPoints")
def _(ap, I, O):
    f = _built(ap, I, O, "disconnected")
    r = f.record("R5")
    f.insert("Fab_Points", ["Name", "CreatedByRecord"], [["alone", r, f"POINT ({I['x0'] + 400} {I['y0'] + 400})"],
                                                         ["edge", r, f"POINT ({I['x0'] + 150} {I['y0'] + 200})"]])
    ap.parcel.FindDisconnectedParcelPoints(f.path("Fab_Points"), O("out"))
    ap.parcel.FindDisconnectedParcelPoints(f.path("Fab_Points"), O("edge"), "IGNORE_FIXED", "IGNORE_CONSTRAINED",
                                           "CONSIDER_EDGE")
    return {"default": _summary(ap, I, O("out")), "edge": _summary(ap, I, O("edge"))}


@case("parcel.FindGapsAndOverlaps")
def _(ap, I, O):
    f = Fabric(ap, I, O, "gaps")
    boxes = {"P1": (0, 0, 10, 10), "P2": (11, 0, 20, 10), "P3": (0, 10, 20, 20), "P4": (0, -10, 20, 0),
             "P5": (19.5, 0, 25, 10), "P6": (0, 0, 5, 5)}
    f.insert("Lot", ["Name"], [[n, _poly(I, [(x0, y0), (x1, y0), (x1, y1), (x0, y1)])]
                               for n, (x0, y0, x1, y1) in boxes.items()])
    ap.parcel.FindGapsAndOverlaps([f.path("Lot")], O("out"))
    for field in ("ParcelInput1", "ParcelInput2"):
        if field in [x.name for x in ap.ListFields(O("out"))]:
            with ap.da.UpdateCursor(O("out"), [field]) as rows:
                for (v,) in rows:
                    rows.updateRow([None if v is None else os.path.basename(str(v).replace("\\", "/"))])
    return _summary(ap, I, O("out"))


@case("parcel.ValidateParcelFabric")
def _(ap, I, O):
    f = Fabric(ap, I, O, "validate")
    r = f.record("R1")
    # A parcel with no lines: its boundary is not covered by lines (a system rule), and its lines' rule finds none.
    f.insert("Lot", ["CreatedByRecord"], [[r, _poly(I, A)]])
    ap.parcel.ValidateParcelFabric(f.fab)
    folder = O("errors", "folder")
    for entry in os.listdir(folder):
        target = os.path.join(folder, entry)
        shutil.rmtree(target) if os.path.isdir(target) else os.remove(target)
    errors = ap.management.CreateFileGDB(folder, "errors")[0]
    result = ap.management.ExportTopologyErrors(f.path("Fab_Topology"), errors, "e")
    out = {}
    for i in range(result.outputCount):
        p = str(result.getOutput(i))
        if ap.Exists(p):
            with ap.da.SearchCursor(p, ["RuleDescription", "OriginObjectClassName", "IsException"]) as cursor:
                out[os.path.basename(p)] = sorted([str(v) for v in row] for row in cursor)
    return out


@case("parcel.GenerateParcelFabricLinks")
def _(ap, I, O):
    f = _built(ap, I, O, "links")
    ap.parcel.GenerateParcelFabricLinks(f.fab, O("links"), O("anchors"), "2020-01-01")
    return {"links": _summary(ap, I, O("links")), "anchors": _summary(ap, I, O("anchors"))}
