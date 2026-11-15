"""arcpy.edit against ArcGIS Pro 3.7: one case for each name grapy builds.

Every edit tool changes its input in place, so each case first copies the fixture (CopyFeatures to an O() output) and
edits the copy; the copy is the output compared.
"""
import os

CASES = {}


def case(name):
    def register(function):
        CASES[name] = function
        return function
    return register


def _fc(ap, I, name, shape, fields, rows):
    """A feature class in the sweep's geodatabase, built once: ``rows`` are (values..., WKT) in local metres."""
    path = os.path.join(I["gdb"], name)
    if ap.Exists(path):
        return path
    sr = ap.SpatialReference(I["srid"])
    ap.management.CreateFeatureclass(I["gdb"], name, shape, spatial_reference=sr)
    for field, kind in fields:
        ap.management.AddField(path, field, kind)
    with ap.da.InsertCursor(path, [f for f, _ in fields] + ["SHAPE@"]) as cursor:
        for *values, wkt in rows:
            cursor.insertRow((*values, ap.FromWKT(wkt, sr)))
    return path


def _line(I, coords):
    return "LINESTRING (" + ", ".join(f"{I['x0'] + x} {I['y0'] + y}" for x, y in coords) + ")"


def _copy(ap, I, O, source):
    return ap.management.CopyFeatures(source, O("out"))[0]


def _links(ap, I):
    """Four displacement links: a shift of (5, -3) m with a little shear."""
    starts = [(0, 0), (600, 0), (600, 600), (0, 600)]
    rows = []
    for n, (x, y) in enumerate(starts, start=1):
        dx, dy = 5 + 0.01 * y, -3 + 0.002 * x
        rows.append((n, _line(I, [(x, y), (x + dx, y + dy)])))
    return _fc(ap, I, "links", "POLYLINE", [("lk", "LONG")], rows)


def _sheets(ap, I):
    """Two map sheets meeting at x = 300 with a 4 m gap: lines on the west sheet end at 298, the east sheet's start
    at 302, slightly offset in y."""
    west = _fc(ap, I, "west", "POLYLINE", [("wid", "LONG"), ("name", "TEXT")], [
        (1, "A", _line(I, [(100, 100), (298, 110)])),
        (2, "B", _line(I, [(100, 300), (298, 300)])),
        (3, "C", _line(I, [(150, 500), (298, 480)])),
    ])
    east = _fc(ap, I, "east", "POLYLINE", [("eid", "LONG"), ("name", "TEXT")], [
        (1, "A", _line(I, [(302, 112), (500, 120)])),
        (2, "B", _line(I, [(302, 297), (500, 280)])),
        (3, "C", _line(I, [(302, 484), (450, 520)])),
    ])
    return west, east


def _shifted(ap, I):
    """The fixture's lines 1, 3 and 4 moved 4 m north and 2 m east: the target the edit tools match the input to."""
    from fixtures import LINES
    rows = []
    for lid, kind, wkt in LINES:
        if lid not in (1, 3, 4):
            continue
        coords = [tuple(float(v) for v in p.split()) for p in wkt[wkt.index("(") + 1:-1].split(",")]
        rows.append((lid, kind.upper(), "LINESTRING (" + ", ".join(f"{x + 2} {y + 4}" for x, y in coords) + ")"))
    return _fc(ap, I, "shifted", "POLYLINE", [("tid", "LONG"), ("kind", "TEXT")], rows)


@case("edit.AlignFeatures")
def _(ap, I, O):
    out = _copy(ap, I, O, I["lines"])
    return ap.edit.AlignFeatures(out, _shifted(ap, I), "10 Meters")


@case("edit.CalculateTransformationErrors")
def _(ap, I, O):
    result = ap.edit.CalculateTransformationErrors(_links(ap, I), O("table"), "AFFINE")
    return [round(float(result.getOutput(i)), 4) if i == 1 else str(result.getOutput(i)) for i in range(result.outputCount)]


@case("edit.Densify")
def _(ap, I, O):
    out = _copy(ap, I, O, I["polys"])
    return ap.edit.Densify(out, "DISTANCE", "30 Meters")


@case("edit.EdgematchFeatures")
def _(ap, I, O):
    west, east = _sheets(ap, I)
    links = ap.edit.GenerateEdgematchLinks(west, east, O("links"), "10 Meters")[0]
    out = _copy(ap, I, O, west)
    return ap.edit.EdgematchFeatures(out, links, "MOVE_ENDPOINT")


@case("edit.ErasePoint")
def _(ap, I, O):
    out = _copy(ap, I, O, I["points"])
    return ap.edit.ErasePoint(out, I["clip"], "INSIDE")


@case("edit.ExtendLine")
def _(ap, I, O):
    lines = _fc(ap, I, "dangles", "POLYLINE", [("did", "LONG")], [
        (1, _line(I, [(100, 100), (100, 500)])),
        (2, _line(I, [(130, 300), (400, 300)])),     # its west end is 30 m short of line 1
        (3, _line(I, [(400, 100), (400, 250)])),     # 50 m short of line 2
        (4, _line(I, [(500, 100), (500, 200)])),     # nothing within reach
    ])
    out = _copy(ap, I, O, lines)
    return ap.edit.ExtendLine(out, "60 Meters", "FEATURE")


@case("edit.FlipLine")
def _(ap, I, O):
    out = _copy(ap, I, O, I["lines"])
    return ap.edit.FlipLine(out)


@case("edit.Generalize")
def _(ap, I, O):
    out = _copy(ap, I, O, I["lines"])
    return ap.edit.Generalize(out, "20 Meters")


@case("edit.GenerateEdgematchLinks")
def _(ap, I, O):
    west, east = _sheets(ap, I)
    return ap.edit.GenerateEdgematchLinks(west, east, O("out"), "10 Meters")


@case("edit.GenerateRubbersheetLinks")
def _(ap, I, O):
    return ap.edit.GenerateRubbersheetLinks(I["lines"], _shifted(ap, I), O("out"), "10 Meters")


@case("edit.RubbersheetFeatures")
def _(ap, I, O):
    out = _copy(ap, I, O, I["points"])
    return ap.edit.RubbersheetFeatures(out, _links(ap, I), None, "LINEAR")


@case("edit.Snap")
def _(ap, I, O):
    out = _copy(ap, I, O, I["points"])
    return ap.edit.Snap(out, [[I["lines"], "VERTEX", "40 Meters"], [I["lines"], "EDGE", "15 Meters"]])


@case("edit.TransferAttributes")
def _(ap, I, O):
    target = _copy(ap, I, O, _shifted(ap, I))
    return ap.edit.TransferAttributes(I["lines"], target, "lid", "10 Meters")


@case("edit.TransformFeatures")
def _(ap, I, O):
    out = _copy(ap, I, O, I["polys"])
    result = ap.edit.TransformFeatures(out, _links(ap, I), "AFFINE")
    return result


@case("edit.TrimLine")
def _(ap, I, O):
    lines = _fc(ap, I, "overshoots", "POLYLINE", [("oid_", "LONG")], [
        (1, _line(I, [(100, 100), (100, 500)])),
        (2, _line(I, [(80, 300), (400, 300)])),      # overshoots line 1 by 20 m
        (3, _line(I, [(300, 280), (300, 320)])),     # crosses line 2 with 20 m each side
        (4, _line(I, [(500, 100), (520, 100)])),     # free-standing, 20 m long
    ])
    out = _copy(ap, I, O, lines)
    return ap.edit.TrimLine(out, "25 Meters", "DELETE_SHORT")


@case("edit.UpdateCOGO")
def _(ap, I, O):
    out = _copy(ap, I, O, I["lines"])
    ap.management.EnableCOGO(out)
    return ap.edit.UpdateCOGO(out)
