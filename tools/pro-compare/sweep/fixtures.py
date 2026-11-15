"""The sweep's inputs, defined once and built by each side with its own arcpy: the same coordinates, attributes and
cells in a file geodatabase (Pro) or a GeoPackage (grapy).

Everything is in UTM zone 35N (EPSG:32635), metres, in a 600 m square whose lower-left corner is (500000, 4500000), so
no tool's answer depends on a projection's scale. Coordinates come from a fixed linear congruential sequence, not a
library's random generator, so both sides see the same numbers.
"""
import math
import os

SRID = 32635
X0, Y0 = 500000.0, 4500000.0
CELL = 10.0
SIZE = 60          # cells on a side: 600 m


def _lcg(seed=12345):
    state = seed
    while True:
        state = (1103515245 * state + 12345) % 2 ** 31
        yield state / 2 ** 31


def _points():
    r = _lcg()
    out = []
    for i in range(1, 31):
        x = X0 + 20 + 560 * next(r)
        y = Y0 + 20 + 560 * next(r)
        out.append((i, round(x, 3), round(y, 3), round(10 + 90 * next(r), 3), "abc"[i % 3]))
    return out


def _poly(coords):
    ring = coords + [coords[0]]
    return "POLYGON ((" + ", ".join(f"{X0 + x} {Y0 + y}" for x, y in ring) + "))"


def _line(coords):
    return "LINESTRING (" + ", ".join(f"{X0 + x} {Y0 + y}" for x, y in coords) + ")"


POLYS = [
    (1, "a", 5.0, _poly([(50, 50), (50, 250), (250, 250), (250, 50)])),
    (2, "a", 7.5, _poly([(200, 200), (200, 400), (400, 400), (400, 200)])),       # overlaps 1 by 50 × 50
    (3, "b", 2.0, _poly([(450, 50), (450, 150), (550, 150), (550, 50)])),
    (4, "b", 9.0, _poly([(100, 450), (300, 550), (100, 550)])),                    # a triangle
    (5, "c", 4.0, _poly([(420, 300), (420, 560), (560, 560), (560, 300)])),
    (6, "c", 1.0, _poly([(260, 60), (260, 140), (340, 140), (340, 60)])),         # touches nothing
]
LINES = [
    (1, "road", _line([(0, 300), (600, 300)])),
    (2, "road", _line([(300, 0), (300, 600)])),
    (3, "track", _line([(50, 50), (200, 120), (350, 80), (550, 200)])),
    (4, "track", _line([(100, 500), (250, 420)])),
    (5, "river", _line([(0, 580), (150, 520), (320, 560), (480, 470), (600, 500)])),
    (6, "road", _line([(300, 300), (450, 450)])),                                   # starts on 1 and 2's crossing
]
CLIP = [(1, _poly([(0, 0), (0, 320), (320, 320), (320, 0)]))]


def dem_value(x, y):
    """A tilted plane with a hill at (300, 300) and a pit at (450, 150), in metres above (X0, Y0)."""
    return (100 + 0.05 * x + 0.02 * y + 40 * math.exp(-((x - 300) ** 2 + (y - 300) ** 2) / 15000)
            - 25 * math.exp(-((x - 450) ** 2 + (y - 150) ** 2) / 5000))


def build(ap, work):
    import numpy as np
    sr = ap.SpatialReference(SRID)
    gdb = ap.management.CreateFileGDB(work, "sweep")[0]
    inputs = {"gdb": gdb, "work": work, "srid": SRID, "x0": X0, "y0": Y0, "cell": CELL, "size": SIZE}

    def fc(name, shape, fields, rows):
        path = ap.management.CreateFeatureclass(gdb, name, shape, spatial_reference=sr)[0]
        for field, kind in fields:
            ap.management.AddField(path, field, kind)
        with ap.da.InsertCursor(path, [f for f, _ in fields] + ["SHAPE@"]) as cursor:
            for *values, wkt in rows:
                cursor.insertRow((*values, ap.FromWKT(wkt, sr)))
        inputs[name] = path

    fc("points", "POINT", [("pid", "LONG"), ("val", "DOUBLE"), ("cat", "TEXT")],
       [(i, v, c, f"POINT ({x} {y})") for i, x, y, v, c in _points()])
    fc("lines", "POLYLINE", [("lid", "LONG"), ("kind", "TEXT")], LINES)
    fc("polys", "POLYGON", [("gid", "LONG"), ("cat", "TEXT"), ("val", "DOUBLE")], POLYS)
    fc("clip", "POLYGON", [("cid", "LONG")], CLIP)

    table = ap.management.CreateTable(gdb, "rows")[0]
    for field, kind in (("rid", "LONG"), ("val", "DOUBLE"), ("cat", "TEXT")):
        ap.management.AddField(table, field, kind)
    with ap.da.InsertCursor(table, ["rid", "val", "cat"]) as cursor:
        for i in range(1, 11):
            cursor.insertRow((i, i * 1.5, "ab"[i % 2]))
    inputs["table"] = table

    xs = CELL / 2 + CELL * np.arange(SIZE)
    ys = SIZE * CELL - CELL / 2 - CELL * np.arange(SIZE)
    gx, gy = np.meshgrid(xs, ys)
    dem = np.vectorize(dem_value)(gx, gy).astype("float32")
    zones = (1 + (gx >= 300).astype(int) + 2 * (gy < 300).astype(int)).astype("int32")   # four quarters, 1..4
    ap.env.outputCoordinateSystem = sr
    for name, array, nodata in (("dem", dem, None), ("zones", zones, None)):
        path = os.path.join(work, name + ".tif")
        ap.NumPyArrayToRaster(array, ap.Point(X0, Y0), CELL, CELL, nodata).save(path)
        ap.management.DefineProjection(path, sr)
        inputs[name] = path
    ap.env.outputCoordinateSystem = None
    return inputs
