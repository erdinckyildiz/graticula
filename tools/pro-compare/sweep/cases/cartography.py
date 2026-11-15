"""arcpy.cartography against ArcGIS Pro 3.7: one case for each name grapy builds."""
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


def _xy(I, coords):
    return ", ".join(f"{I['x0'] + x} {I['y0'] + y}" for x, y in coords)


def _square_grid(ap, I, name="cgrid", n=4, size=100):
    """An n × n grid of squares sharing edges, with a value that grows to the north-east."""
    rows = []
    for r in range(n):
        for c in range(n):
            x, y = 50 + c * size, 50 + r * size
            ring = [(x, y), (x, y + size), (x + size, y + size), (x + size, y), (x, y)]
            rows.append((r * n + c + 1, 10 + 5 * r + 3 * c + (7 if (r, c) == (1, 2) else 0), f"POLYGON (({_xy(I, ring)}))"))
    return _fc(ap, I, name, "POLYGON", [("zid", "LONG"), ("v", "DOUBLE")], rows)


def _buildings(ap, I):
    """Three buildings: a rectangle with a 2 m notch, an L shape, and a 4 m shed."""
    def poly(coords):
        return f"POLYGON (({_xy(I, coords + [coords[0]])}))"
    return _fc(ap, I, "buildings", "POLYGON", [("bid", "LONG")], [
        (1, poly([(100, 100), (100, 130), (114, 130), (114, 128), (116, 128), (116, 130), (140, 130), (140, 100)])),
        (2, poly([(200, 100), (200, 160), (220, 160), (220, 120), (260, 120), (260, 100)])),
        (3, poly([(300, 300), (300, 304), (304, 304), (304, 300)])),
    ])


def _dual(ap, I):
    """A road drawn as its two casings 20 m apart, and a lone line."""
    def line(coords):
        return f"LINESTRING ({_xy(I, coords)})"
    return _fc(ap, I, "casings", "POLYLINE", [("cid", "LONG")], [
        (1, line([(50, 200), (250, 200), (450, 260)])),
        (2, line([(50, 220), (250, 220), (450, 280)])),
        (3, line([(100, 500), (300, 450)])),
    ])


@case("cartography.AggregatePolygons")
def _(ap, I, O):
    return ap.cartography.AggregatePolygons(I["polys"], O("out"), "60 Meters", out_table=O("table"))


@case("cartography.CalculateColorTheoremField")
def _(ap, I, O):
    out = ap.management.CopyFeatures(_square_grid(ap, I), O("out"))[0]
    result = ap.cartography.CalculateColorTheoremField(out, "color")
    return result


@case("cartography.CollapseDualLinesToCenterline")
def _(ap, I, O):
    return ap.cartography.CollapseDualLinesToCenterline(_dual(ap, I), O("out"), "30 Meters")


@case("cartography.CreateCartographicPartitions")
def _(ap, I, O):
    return ap.cartography.CreateCartographicPartitions([I["points"], I["lines"]], O("out"), 500, "FEATURES")


@case("cartography.GenerateContiguousCartogram")
def _(ap, I, O):
    return ap.cartography.GenerateContiguousCartogram(_square_grid(ap, I), "v", O("out"), "FLOW-BASED")


@case("cartography.SimplifyBuilding")
def _(ap, I, O):
    return ap.cartography.SimplifyBuilding(_buildings(ap, I), O("out"), "5 Meters", "20 SquareMeters")


@case("cartography.SimplifyLine")
def _(ap, I, O):
    return ap.cartography.SimplifyLine(I["lines"], O("out"), "POINT_REMOVE", "30 Meters")


@case("cartography.SimplifyPolygon")
def _(ap, I, O):
    return ap.cartography.SimplifyPolygon(_buildings(ap, I), O("out"), "POINT_REMOVE", "3 Meters")


@case("cartography.StripMapIndexFeatures")
def _(ap, I, O):
    line = _fc(ap, I, "stripline", "POLYLINE", [("sid", "LONG")],
               [(1, f"LINESTRING ({_xy(I, [(0, 100), (300, 100), (500, 400)])})")])
    return ap.cartography.StripMapIndexFeatures(line, O("out"), "NO_USEPAGEUNIT", None, "200 Meters", "100 Meters",
                                                "HORIZONTAL", 10, 1, "WE_NS")
