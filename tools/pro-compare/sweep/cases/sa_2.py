"""arcpy.sa, the second half of the built names in sorted order (MajorityFilter to the last), against ArcGIS Pro 3.7.

A tool answers a Raster, which the case returns (the harness dumps its cells, extent, cell size and pixel type); a
second output is saved through ``O(name, "raster")``. Classes — neighbourhoods, remap tables, radii, transformation
functions, vertical factors, topo inputs, solar time configurations — are compared through the tool that uses them,
and their documented properties through the answer. Inputs beyond the shared fixtures (a raster with NoData, a
multiband image, a cost surface, observers, pour points, sources) are built here with ``ap``, so both sides build them
the same way.
"""
import math
import os

import fixtures
from cases.arcpy_top import _r

CASES = {}
_CACHE = {}


def case(name):
    def register(function):
        CASES[name] = function
        return function
    return register


def S(ap):
    """``ap.sa``, with the Spatial Analyst extension checked out on Pro's side."""
    try:
        ap.CheckOutExtension("Spatial")
    except Exception:  # noqa: BLE001
        pass
    return ap.sa


def _grid_arrays(I):
    import numpy as np
    size, cell = I["size"], I["cell"]
    xs = cell / 2 + cell * np.arange(size)
    ys = size * cell - cell / 2 - cell * np.arange(size)
    gx, gy = np.meshgrid(xs, ys)
    dem = np.vectorize(fixtures.dem_value)(gx, gy)
    return gx, gy, dem


def _from_array(ap, I, name, array, nodata=None):
    key = (name, I["work"])
    if key in _CACHE:
        return _CACHE[key]
    sr = ap.SpatialReference(I["srid"])
    path = os.path.join(I["work"], "sa2_" + name + ".tif")
    old = ap.env.outputCoordinateSystem
    ap.env.outputCoordinateSystem = sr
    try:
        ap.NumPyArrayToRaster(array, ap.Point(I["x0"], I["y0"]), I["cell"], I["cell"], nodata).save(path)
    finally:
        ap.env.outputCoordinateSystem = old
    ap.management.DefineProjection(path, sr)
    _CACHE[key] = path
    return path


def _classes(ap, I):
    """A noisy integer raster of classes 1..4, for the filters and region tools."""
    import numpy as np
    gx, gy, dem = _grid_arrays(I)
    return _from_array(ap, I, "classes", (np.floor(dem * 7) % 4 + 1).astype("int32"))


def _holes(ap, I):
    """The DEM with NoData (-9999) in a 5 × 5 block and along one column."""
    import numpy as np
    gx, gy, dem = _grid_arrays(I)
    dem = dem.astype("float32")
    dem[20:25, 30:35] = -9999
    dem[40:55, 10] = -9999
    return _from_array(ap, I, "holes", dem, -9999)


def _small(ap, I):
    """Values in −1.5..1.5, for the trigonometric and hyperbolic functions."""
    gx, gy, dem = _grid_arrays(I)
    return _from_array(ap, I, "small", ((dem - 140) / 30).astype("float32"))


def _cost(ap, I):
    gx, gy, dem = _grid_arrays(I)
    return _from_array(ap, I, "cost", (1 + (dem - 90) / 40).astype("float32"))


def _bits(ap, I):
    import numpy as np
    gx, gy, dem = _grid_arrays(I)
    return _from_array(ap, I, "bits", (np.floor(dem * 37) % 65536).astype("int32"))


def _bands(ap, I, count=8):
    """An image of ``count`` float bands, each a different mix of the DEM and the zones."""
    key = ("bands%d" % count, I["work"])
    if key in _CACHE:
        return _CACHE[key]
    import numpy as np
    gx, gy, dem = _grid_arrays(I)
    zones = 1 + (gx >= 300).astype(int) + 2 * (gy < 300).astype(int)
    parts = []
    for k in range(1, count + 1):
        band = 30 * k + (dem - 90) * (1 + k % 3) + 10 * zones * (k % 2) + 3 * ((gx / 10 + k) % 5)
        parts.append(_from_array(ap, I, "band%d_%d" % (count, k), band.astype("float32")))
    path = os.path.join(I["work"], "sa2_bands%d.tif" % count)
    ap.management.CompositeBands(";".join(parts), path)
    _CACHE[key] = path
    return path


def _rgb(ap, I):
    """A three-band unsigned 8-bit image, as the segmentation tools ask for."""
    key = ("rgb", I["work"])
    if key in _CACHE:
        return _CACHE[key]
    import numpy as np
    gx, gy, dem = _grid_arrays(I)
    zones = 1 + (gx >= 300).astype(int) + 2 * (gy < 300).astype(int)
    r = np.clip((dem - 90) * 2.5, 0, 255).astype("uint8")
    g = (zones * 60).astype("uint8")
    b = (np.floor(dem * 7) % 4 * 60).astype("uint8")
    parts = [_from_array(ap, I, "rgb_" + n, a) for n, a in (("r", r), ("g", g), ("b", b))]
    path = os.path.join(I["work"], "sa2_rgb.tif")
    ap.management.CompositeBands(";".join(parts), path)
    _CACHE[key] = path
    return path


def _fc(ap, I, name, shape, fields, rows):
    key = (name, I["work"])
    if key in _CACHE:
        return _CACHE[key]
    sr = ap.SpatialReference(I["srid"])
    path = os.path.join(I["gdb"], "sa2_" + name)
    if ap.Exists(path):
        ap.management.Delete(path)
    ap.management.CreateFeatureclass(I["gdb"], "sa2_" + name, shape, spatial_reference=sr)
    for field, kind in fields:
        ap.management.AddField(path, field, kind)
    with ap.da.InsertCursor(path, [f for f, _ in fields] + ["SHAPE@"]) as cursor:
        for *values, wkt in rows:
            cursor.insertRow((*values, ap.FromWKT(wkt, sr)))
    _CACHE[key] = path
    return path


def _pt(I, x, y):
    return f"POINT ({I['x0'] + x} {I['y0'] + y})"


def _observers(ap, I):
    return _fc(ap, I, "observers", "POINT", [("oid_", "LONG")],
               [(1, _pt(I, 305, 305)), (2, _pt(I, 95, 515)), (3, _pt(I, 515, 85))])


def _sources(ap, I):
    return _fc(ap, I, "sources", "POINT", [("sid", "LONG")], [(1, _pt(I, 105, 105)), (2, _pt(I, 455, 485))])


def _destinations(ap, I):
    return _fc(ap, I, "dest", "POINT", [("did", "LONG")], [(1, _pt(I, 515, 105)), (2, _pt(I, 95, 455))])


def _pour(ap, I):
    return _fc(ap, I, "pour", "POINT", [("pid", "LONG")], [(1, _pt(I, 455, 155)), (2, _pt(I, 105, 305))])


def _pts10(ap, I):
    return _fc(ap, I, "pts10", "POINT", [("val", "DOUBLE")],
               [(v, f"POINT ({x} {y})") for _, x, y, v, _ in fixtures._points()[:10]])


def _contours(ap, I):
    def ring(h):
        lo, hi = 300 - h, 300 + h
        return ("LINESTRING (" + ", ".join(f"{I['x0'] + x} {I['y0'] + y}" for x, y in
                                           ((lo, lo), (lo, hi), (hi, hi), (hi, lo), (lo, lo))) + ")")
    return _fc(ap, I, "contours", "POLYLINE", [("elev", "DOUBLE")],
               [(150.0, ring(60)), (130.0, ring(150)), (110.0, ring(250))])


def _valley(ap, I):
    """A DEM without pits: a main valley along y = 300 falling west, and two tributaries joining it, so every cell
    drains off the west edge. (The shared DEM's pit gives Flow Direction a two-cell loop, which is a sa.FlowDirection /
    FlowAccumulation question, not one of the tools here.)"""
    import numpy as np
    gx, gy, dem = _grid_arrays(I)
    d = np.full(gx.shape, np.inf)
    for (ax, ay), (bx, by) in (((0, 300), (600, 300)), ((450, 560), (250, 300)), ((520, 40), (380, 300))):
        vx, vy = bx - ax, by - ay
        t = np.clip(((gx - ax) * vx + (gy - ay) * vy) / (vx * vx + vy * vy), 0, 1)
        d = np.minimum(d, np.hypot(gx - ax - t * vx, gy - ay - t * vy))
    return _from_array(ap, I, "valley", (100 + 0.05 * gx + 0.08 * d + 0.0007 * gy).astype("float32"))


def _flowdir(ap, I):
    return S(ap).FlowDirection(_valley(ap, I))


def _streams(ap, I):
    sa = S(ap)
    fd = sa.FlowDirection(_valley(ap, I))
    acc = sa.FlowAccumulation(fd)
    return sa.SetNull(acc, 1, "VALUE < 40"), fd


def _write(path, text):
    with open(path, "w", encoding="ascii") as f:
        f.write(text)
    return path


def _work_file(I, name, text):
    return _write(os.path.join(I["work"], "sa2_" + name), text)


def _save(raster, path):
    raster.save(path)
    return path


def _split(O, raster, prefix="band"):
    """A raster of several bands, saved whole and then band by band from the saved file (the harness's dump reads one
    band at a time on grapy's side; Pro saves a function raster's own getRasterBands() band 2 as band 1)."""
    count = int(getattr(raster, "bandCount", 1) or 1)
    if count == 1:
        raster.save(O(prefix + "1", "raster"))
        return {"bandCount": 1}
    first = O(prefix + "1", "raster")
    whole = first[:-4] + "_all.tif"      # not an O output: the harness dumps one band at a time
    raster.save(whole)
    ap = __import__("arcpy") if type(raster).__module__.startswith("arcpy") else __import__("grapy")
    # The saved file's bands, not "Band_n" paths: Pro names a VectorField's bands Vector-Magnitude and Vector-Direction.
    for k, band in enumerate(ap.Raster(whole).getRasterBands(), 1):
        band.save(first if k == 1 else O("%s%d" % (prefix, k), "raster"))
    return {"bandCount": count}


# --- Filters, regions and zones -----------------------------------------------------------------------------------

@case("sa.MajorityFilter")
def _(ap, I, O):
    sa = S(ap)
    _save(sa.MajorityFilter(_classes(ap, I), "EIGHT", "HALF"), O("eight_half", "raster"))
    return sa.MajorityFilter(_classes(ap, I))


@case("sa.Nibble")
def _(ap, I, O):
    sa = S(ap)
    return sa.Nibble(_classes(ap, I), _holes(ap, I), "ALL_VALUES")


@case("sa.RegionGroup")
def _(ap, I, O):
    sa = S(ap)
    _save(sa.RegionGroup(_classes(ap, I)), O("four", "raster"))
    return sa.RegionGroup(_classes(ap, I), "EIGHT", "WITHIN", "NO_LINK")


@case("sa.Shrink")
def _(ap, I, O):
    sa = S(ap)
    return sa.Shrink(_classes(ap, I), 1, [1, 2])


@case("sa.ZonalFill")
def _(ap, I, O):
    sa = S(ap)
    return sa.ZonalFill(I["zones"], I["dem"])


@case("sa.ZonalStatistics")
def _(ap, I, O):
    sa = S(ap)
    _save(sa.ZonalStatistics(I["zones"], "Value", I["dem"], "STD"), O("std", "raster"))
    _save(sa.ZonalStatistics(I["zones"], "Value", _holes(ap, I), "MAXIMUM", "DATA"), O("max_holes", "raster"))
    return sa.ZonalStatistics(I["zones"], "Value", I["dem"], "MEAN")


@case("sa.ZonalStatisticsAsTable")
def _(ap, I, O):
    sa = S(ap)
    sa.ZonalStatisticsAsTable(I["zones"], "Value", _classes(ap, I), O("integer"), "DATA", "ALL")
    sa.ZonalStatisticsAsTable(I["zones"], "Value", I["dem"], O("pct"), "DATA", "PERCENTILE", None, 25)
    result = sa.ZonalStatisticsAsTable(I["zones"], "Value", I["dem"], O("out"), "DATA", "ALL")
    return [os.path.basename(str(result.getOutput(i))) for i in range(result.outputCount)]


@case("sa.ZonalRemap")
def _(ap, I, O):
    sa = S(ap)
    table = O("table")
    folder, base = os.path.split(table)
    ap.management.CreateTable(folder, base)
    for field, kind in (("ZoneID", "LONG"), ("ZoneMin", "DOUBLE"), ("ZoneMax", "DOUBLE"), ("ZoneValue", "LONG")):
        ap.management.AddField(table, field, kind)
    with ap.da.InsertCursor(table, ["ZoneID", "ZoneMin", "ZoneMax", "ZoneValue"]) as c:
        for row in ((1, 100, 130, 1), (2, 120, 160, 2), (3, 90, 140, 3), (4, 100, 200, 4)):
            c.insertRow(row)
    return sa.ZonalRemap(I["dem"], table, I["zones"])


# --- Local arithmetic and trigonometry ----------------------------------------------------------------------------

@case("sa.Minus")
def _(ap, I, O):
    sa = S(ap)
    _save(sa.Minus(I["zones"], 1), O("int", "raster"))
    return sa.Minus(I["dem"], I["zones"])


@case("sa.Mod")
def _(ap, I, O):
    sa = S(ap)
    _save(sa.Mod(_classes(ap, I), 3), O("int", "raster"))
    return sa.Mod(I["dem"], 7)


@case("sa.Negate")
def _(ap, I, O):
    sa = S(ap)
    _save(sa.Negate(I["zones"]), O("int", "raster"))
    return sa.Negate(I["dem"])


@case("sa.NotEqual")
def _(ap, I, O):
    sa = S(ap)
    return sa.NotEqual(I["zones"], 2)


@case("sa.Over")
def _(ap, I, O):
    sa = S(ap)
    return sa.Over(sa.Minus(I["zones"], 1), I["dem"])


@case("sa.Plus")
def _(ap, I, O):
    sa = S(ap)
    _save(sa.Plus(I["zones"], 2), O("int", "raster"))
    return sa.Plus(I["dem"], I["zones"])


@case("sa.Power")
def _(ap, I, O):
    sa = S(ap)
    _save(sa.Power(I["zones"], 2), O("int", "raster"))
    return sa.Power(I["dem"], 0.5)


@case("sa.Times")
def _(ap, I, O):
    sa = S(ap)
    _save(sa.Times(I["zones"], 3), O("int", "raster"))
    return sa.Times(I["dem"], I["zones"])


@case("sa.RoundDown")
def _(ap, I, O):
    return S(ap).RoundDown(I["dem"])


@case("sa.RoundUp")
def _(ap, I, O):
    return S(ap).RoundUp(I["dem"])


@case("sa.Sin")
def _(ap, I, O):
    return S(ap).Sin(_small(ap, I))


@case("sa.SinH")
def _(ap, I, O):
    return S(ap).SinH(_small(ap, I))


@case("sa.Tan")
def _(ap, I, O):
    return S(ap).Tan(_small(ap, I))


@case("sa.TanH")
def _(ap, I, O):
    return S(ap).TanH(_small(ap, I))


@case("sa.Square")
def _(ap, I, O):
    sa = S(ap)
    _save(sa.Square(I["zones"]), O("int", "raster"))
    return sa.Square(_small(ap, I))


@case("sa.SquareRoot")
def _(ap, I, O):
    sa = S(ap)
    _save(sa.SquareRoot(I["zones"]), O("int", "raster"))
    return sa.SquareRoot(_small(ap, I))


@case("sa.Test")
def _(ap, I, O):
    return S(ap).Test(_holes(ap, I), "VALUE > 120")


@case("sa.SetNull")
def _(ap, I, O):
    sa = S(ap)
    _save(sa.SetNull(I["dem"], 5, "VALUE < 110"), O("constant", "raster"))
    return sa.SetNull(I["zones"], I["dem"], "VALUE = 2")


@case("sa.Pick")
def _(ap, I, O):
    sa = S(ap)
    return sa.Pick(I["zones"], [I["dem"], 5, _small(ap, I), 7])


@case("sa.Popularity")
def _(ap, I, O):
    sa = S(ap)
    return sa.Popularity(1, [I["zones"], _classes(ap, I), sa.Minus(I["zones"], 0), 3])


@case("sa.Rank")
def _(ap, I, O):
    sa = S(ap)
    return sa.Rank(2, [I["dem"], _small(ap, I), _cost(ap, I)])


@case("sa.RasterCalculator")
def _(ap, I, O):
    sa = S(ap)
    return sa.RasterCalculator([I["dem"], I["zones"]], ["a", "b"], "a + b * 2")


@case("sa.WSTable")
def _(ap, I, O):
    sa = S(ap)
    table = sa.WSTable([[I["dem"], "VALUE", 0.5], [I["zones"], "VALUE", 2]])
    _save(sa.WeightedSum(table), O("sum", "raster"))
    return [len(table.weightedSumTable), [round(float(r[2]), 6) for r in table.weightedSumTable],
            str(table).split()[-2:]]


@case("sa.WeightedSum")
def _(ap, I, O):
    sa = S(ap)
    return sa.WeightedSum(sa.WSTable([[I["dem"], "VALUE", 0.5], [I["zones"], "VALUE", 2], [_holes(ap, I), "VALUE", 1]]))


@case("sa.TransposeBits")
def _(ap, I, O):
    sa = S(ap)
    return sa.TransposeBits(_bits(ap, I), [0, 1, 5], [2, 3, 0], 0)


@case("sa.Slice")
def _(ap, I, O):
    sa = S(ap)
    _save(sa.Slice(I["dem"], 4, "EQUAL_AREA"), O("area", "raster"))
    return sa.Slice(I["dem"], 5, "EQUAL_INTERVAL")


@case("sa.Threshold")
def _(ap, I, O):
    return S(ap).Threshold(I["dem"])


# --- Reclassification ---------------------------------------------------------------------------------------------

@case("sa.Reclassify")
def _(ap, I, O):
    sa = S(ap)
    _save(sa.Reclassify(I["zones"], "Value", sa.RemapValue([[1, 10], [3, 30]]), "NODATA"), O("nodata", "raster"))
    return sa.Reclassify(I["dem"], "Value", sa.RemapRange([[90, 120, 1], [120, 150, 2], [150, 200, 3]]))


@case("sa.RemapValue")
def _(ap, I, O):
    sa = S(ap)
    remap = sa.RemapValue([[1, 10], [2, 20], [3, "NODATA"], [4, 40]])
    _save(sa.Reclassify(I["zones"], "Value", remap), O("out", "raster"))
    return [str(x) for row in remap.remapTable for x in row] + [str(remap)]


@case("sa.RemapRange")
def _(ap, I, O):
    sa = S(ap)
    remap = sa.RemapRange([[90, 110, 1], [110, 130, 2], [130, 160, 3], [160, 200, "NODATA"]])
    _save(sa.Reclassify(I["dem"], "Value", remap), O("out", "raster"))
    return [str(x) for row in remap.remapTable for x in row] + [str(remap)]


@case("sa.Remap")
def _(ap, I, O):
    sa = S(ap)
    _save(sa.Remap(I["zones"], [1, 2, 3, 5], [10, 30], None, False, 99), O("replace", "raster"))
    return sa.Remap(I["dem"], [90, 120, 120, 140], [1, 2], [160, 200])


@case("sa.ReclassByASCIIFile")
def _(ap, I, O):
    sa = S(ap)
    _save(sa.ReclassByASCIIFile(I["zones"], _work_file(I, "ranges.txt", "0 1 : 10\n1 3 : 20\n"), "NODATA"),
          O("ranges", "raster"))
    # Every line in one form is what the page asks; Pro reads a mix too (measured 2026-11-08).
    return sa.ReclassByASCIIFile(I["zones"], _work_file(I, "remap.txt", "1 : 10\n2 : 20\n3 4 : 30\n"))


@case("sa.ReclassByTable")
def _(ap, I, O):
    sa = S(ap)
    table = O("remap")
    folder, base = os.path.split(table)
    ap.management.CreateTable(folder, base)
    for field in ("FROM_", "TO_", "OUT_"):
        ap.management.AddField(table, field, "LONG")
    with ap.da.InsertCursor(table, ["FROM_", "TO_", "OUT_"]) as c:
        for row in ((1, 2, 5), (3, 3, 7)):
            c.insertRow(row)
    return sa.ReclassByTable(I["zones"], table, "FROM_", "TO_", "OUT_")


@case("sa.RescaleByFunction")
def _(ap, I, O):
    sa = S(ap)
    _save(sa.RescaleByFunction(I["dem"], sa.TfLinear(110, 160), 0, 100), O("linear", "raster"))
    return sa.RescaleByFunction(I["dem"])


def _tf(name, *explicit):
    @case("sa." + name)
    def run(ap, I, O):
        sa = S(ap)
        cls = getattr(sa, name)
        _save(sa.RescaleByFunction(I["dem"], cls(*explicit), 1, 10), O("explicit", "raster"))
        return sa.RescaleByFunction(I["dem"], cls(), 1, 10)
    return run


_tf("TfExponential", 100, 0.05)
_tf("TfGaussian", 140, 0.01)
_tf("TfLarge", 140, 5)
_tf("TfLinear", 110, 170, 100, 0, 175, 10)
_tf("TfLogarithm", 90, 1)
_tf("TfLogisticDecay", 100, 180, 99)
_tf("TfLogisticGrowth", 100, 180, 1)
_tf("TfMSLarge", 1, 1)
_tf("TfMSSmall", 1, 1)
_tf("TfNear", 140, 0.01)
_tf("TfPower", 100, 2)
_tf("TfSmall", 140, 5)
_tf("TfSymmetricLinear", 110, 170)


# --- Neighbourhoods and point surfaces ----------------------------------------------------------------------------

def _nbr_props(n, names):
    out = {k: (_r(getattr(n, k), 6) if isinstance(getattr(n, k), float) else getattr(n, k)) for k in names}
    out["str"] = str(n)
    return out


@case("sa.NbrAnnulus")
def _(ap, I, O):
    sa = S(ap)
    n = sa.NbrAnnulus(1, 3, "CELL")
    _save(sa.FocalStatistics(I["dem"], n, "MEAN"), O("focal", "raster"))
    return _nbr_props(n, ("innerRadius", "outerRadius", "units"))


@case("sa.NbrCircle")
def _(ap, I, O):
    sa = S(ap)
    n = sa.NbrCircle(25, "MAP")
    _save(sa.FocalStatistics(I["dem"], n, "MEAN"), O("focal", "raster"))
    return _nbr_props(n, ("radius", "units"))


@case("sa.NbrRectangle")
def _(ap, I, O):
    sa = S(ap)
    n = sa.NbrRectangle(5, 3, "CELL")
    _save(sa.FocalStatistics(I["dem"], n, "MAXIMUM"), O("focal", "raster"))
    return _nbr_props(n, ("width", "height", "units"))


@case("sa.NbrIrregular")
def _(ap, I, O):
    sa = S(ap)
    path = _work_file(I, "kernel.txt", "3 3\n0 1 0\n1 1 1\n0 1 0\n")
    n = sa.NbrIrregular(path)
    _save(sa.FocalStatistics(I["dem"], n, "SUM"), O("focal", "raster"))
    # Pro answers the path in single quotes; the file's name is what is compared.
    return os.path.basename(str(n.inKernelFile).strip("'\""))


@case("sa.NaturalNeighbor")
def _(ap, I, O):
    return S(ap).NaturalNeighbor(I["points"], "val", 10)


@case("sa.PointDensity")
def _(ap, I, O):
    sa = S(ap)
    _save(sa.PointDensity(I["points"], "NONE", 10), O("default", "raster"))
    return sa.PointDensity(I["points"], "val", 10, sa.NbrCircle(60, "MAP"), "SQUARE_MAP_UNITS")


@case("sa.PointStatistics")
def _(ap, I, O):
    sa = S(ap)
    return sa.PointStatistics(I["points"], "val", 10, sa.NbrRectangle(5, 5, "CELL"), "MEAN")


@case("sa.RadiusFixed")
def _(ap, I, O):
    sa = S(ap)
    r = sa.RadiusFixed(120, 3)
    _save(sa.Idw(I["points"], "val", 10, 2, r), O("idw", "raster"))
    return {"distance": r.distance, "minNumberOfPoints": r.minNumberOfPoints, "str": str(r)}


@case("sa.RadiusVariable")
def _(ap, I, O):
    sa = S(ap)
    r = sa.RadiusVariable(5, 200)
    _save(sa.Idw(I["points"], "val", 10, 2, r), O("idw", "raster"))
    return {"numberOfPoints": r.numberOfPoints, "maxDistance": r.maxDistance, "str": str(r)}


@case("sa.Spline")
def _(ap, I, O):
    sa = S(ap)
    _save(sa.Spline(_pts10(ap, I), "val", 10, "TENSION", 0.1, 12), O("tension", "raster"))
    return sa.Spline(_pts10(ap, I), "val", 10, "REGULARIZED", 0.1, 12)


@case("sa.SplineWithBarriers")
def _(ap, I, O):
    sa = S(ap)
    return sa.SplineWithBarriers(I["points"], "val", I["lines"], 10, 0)


@case("sa.Trend")
def _(ap, I, O):
    sa = S(ap)
    _save(sa.Trend(I["points"], "val", 10, 2), O("order2", "raster"))
    return sa.Trend(I["points"], "val", 10, 1, "LINEAR")


@case("sa.Point")
def _(ap, I, O):
    p = S(ap).Point(1.5, 2.5, 3.5)
    return {"X": p.X, "Y": p.Y, "Z": p.Z, "same_as_arcpy": type(p).__name__ == type(ap.Point()).__name__}


# --- Surface ------------------------------------------------------------------------------------------------------

@case("sa.Slope")
def _(ap, I, O):
    sa = S(ap)
    _save(sa.Slope(I["dem"], "PERCENT_RISE"), O("percent", "raster"))
    _save(sa.Slope(_holes(ap, I), "DEGREE"), O("holes", "raster"))
    return sa.Slope(I["dem"], "DEGREE")


@case("sa.SurfaceParameters")
def _(ap, I, O):
    sa = S(ap)
    _save(sa.SurfaceParameters(I["dem"], "ASPECT"), O("aspect", "raster"))
    return sa.SurfaceParameters(I["dem"], "SLOPE")


@case("sa.Viewshed")
def _(ap, I, O):
    return S(ap).Viewshed(I["dem"], _observers(ap, I))


@case("sa.Viewshed2")
def _(ap, I, O):
    return S(ap).Viewshed2(I["dem"], _observers(ap, I))


@case("sa.Visibility")
def _(ap, I, O):
    return S(ap).Visibility(I["dem"], _observers(ap, I), None, "FREQUENCY", "ZERO")


@case("sa.ObserverPoints")
def _(ap, I, O):
    return S(ap).ObserverPoints(I["dem"], _observers(ap, I))


# --- Hydrology ----------------------------------------------------------------------------------------------------

@case("sa.Sink")
def _(ap, I, O):
    # The flow direction is saved too: the shared DEM's pit makes a two-cell loop, and which codes each side writes
    # there decides what Sink sees.
    fd = S(ap).FlowDirection(I["dem"])
    fd.save(O("flowdir", "raster"))
    return S(ap).Sink(fd)


@case("sa.SnapPourPoint")
def _(ap, I, O):
    sa = S(ap)
    acc = sa.FlowAccumulation(_flowdir(ap, I))
    return sa.SnapPourPoint(_pour(ap, I), acc, 30, "pid")


@case("sa.Watershed")
def _(ap, I, O):
    sa = S(ap)
    return sa.Watershed(_flowdir(ap, I), _pour(ap, I), "pid")


@case("sa.StreamLink")
def _(ap, I, O):
    sa = S(ap)
    streams, fd = _streams(ap, I)
    return sa.StreamLink(streams, fd)


@case("sa.StreamOrder")
def _(ap, I, O):
    sa = S(ap)
    streams, fd = _streams(ap, I)
    _save(sa.StreamOrder(streams, fd, "SHREVE"), O("shreve", "raster"))
    return sa.StreamOrder(streams, fd, "STRAHLER")


# --- Distance -----------------------------------------------------------------------------------------------------

@case("sa.PathDistance")
def _(ap, I, O):
    sa = S(ap)
    return sa.PathDistance(_sources(ap, I), _cost(ap, I), I["dem"])


@case("sa.PathAllocation")
def _(ap, I, O):
    sa = S(ap)
    return sa.PathAllocation(_sources(ap, I), _cost(ap, I), I["dem"], source_field="sid")


@case("sa.PathBackLink")
def _(ap, I, O):
    sa = S(ap)
    return sa.PathBackLink(_sources(ap, I), _cost(ap, I), I["dem"])


def _vf(name, *explicit):
    @case("sa." + name)
    def run(ap, I, O):
        sa = S(ap)
        cls = getattr(sa, name)
        args = explicit
        if name == "VfTable":
            args = (_work_file(I, "vf.txt", "-90 -1\n-45 3\n0 1\n45 2\n90 -1\n"),)
        if args:
            _save(sa.PathDistance(_sources(ap, I), _cost(ap, I), None, None, None, I["dem"], cls(*args)),
                  O("explicit", "raster"))
        if name == "VfTable":
            return None
        return sa.PathDistance(_sources(ap, I), _cost(ap, I), None, None, None, I["dem"], cls())
    return run


_vf("VfBinary", 1, -20, 20)
_vf("VfLinear", 1, -60, 60, 0.02)
_vf("VfSymLinear", 1, -60, 60, 0.02)
_vf("VfInverseLinear", 1, -30, 30, -0.02)
_vf("VfSymInverseLinear", 1, -30, 30, -0.02)
_vf("VfCos", -80, 80, 2)
_vf("VfSec", -80, 80, 2)
_vf("VfCosSec", -80, 80, 1, 2)
_vf("VfSecCos", -80, 80, 2, 1)
_vf("VfHikingTime", -60, 60)
_vf("VfBidirHikingTime", -60, 60)
_vf("VfTable")


def _accumulation(ap, I, O, tag):
    sa = S(ap)
    # The back-direction raster is Distance Accumulation's (another sweep's name), so it is not dumped here.
    back = os.path.join(I["work"], "sa2_back_%s.tif" % tag)
    dist = sa.DistanceAccumulation(_sources(ap, I), in_cost_raster=_cost(ap, I), out_back_direction_raster=back)
    return dist, back


@case("sa.OptimalPathAsRaster")
def _(ap, I, O):
    sa = S(ap)
    dist, back = _accumulation(ap, I, O, "raster")
    return sa.OptimalPathAsRaster(_destinations(ap, I), dist, back, "did")


@case("sa.OptimalPathAsLine")
def _(ap, I, O):
    sa = S(ap)
    dist, back = _accumulation(ap, I, O, "line")
    sa.OptimalPathAsLine(_destinations(ap, I), dist, back, O("out"), "did")


# --- Image and multiband ------------------------------------------------------------------------------------------

def _index(name, *args):
    @case("sa." + name)
    def run(ap, I, O):
        sa = S(ap)
        return getattr(sa, name)(_bands(ap, I), *args)
    return run


for _name in ("NBR", "NDBI", "NDMI", "NDSI", "NDVI", "NDVIre", "NDWI", "PVI", "RTVICore", "SAVI", "SRre", "TSAVI",
              "VARI"):
    _index(_name)


@case("sa.PrincipalComponents")
def _(ap, I, O):
    sa = S(ap)
    return _split(O, sa.PrincipalComponents(_bands(ap, I, 3), 2))


@case("sa.SpectralConversion")
def _(ap, I, O):
    sa = S(ap)
    return _split(O, sa.SpectralConversion(_bands(ap, I, 3), [0.1, 0.9, 0, 0.5, 0.25, 0.25, 0, 0, 1]))


@case("sa.Stretch")
def _(ap, I, O):
    sa = S(ap)
    _save(sa.Stretch(I["dem"], "StdDev", num_stddev=2), O("stddev", "raster"))
    return sa.Stretch(I["dem"], "MinMax", 100, 180)


@case("sa.Resample")
def _(ap, I, O):
    sa = S(ap)
    _save(sa.Resample(I["dem"], "NearestNeighbor", None, 20), O("nearest", "raster"))
    return sa.Resample(I["dem"], "Bilinear", None, 20)


@case("sa.Speckle")
def _(ap, I, O):
    return S(ap).Speckle(I["dem"], "Lee", "3x3", "Multiplicative", None, None, 1, 1)


@case("sa.UnitConversion")
def _(ap, I, O):
    return S(ap).UnitConversion(I["dem"], "Meters", "Feet")


@case("sa.VectorField")
def _(ap, I, O):
    sa = S(ap)
    return _split(O, sa.VectorField(_small(ap, I), _cost(ap, I), "Vector-UV", "Geographic", "Vector-MagDir"))


@case("sa.WindChill")
def _(ap, I, O):
    sa = S(ap)
    return sa.WindChill(_small(ap, I), _cost(ap, I), "Celsius", "m/s", "Celsius")


@case("sa.SegmentMeanShift")
def _(ap, I, O):
    return _split(O, S(ap).SegmentMeanShift(_rgb(ap, I), 15.5, 15, 20))


@case("sa.SegMeanShift")
def _(ap, I, O):
    return _split(O, S(ap).SegMeanShift(_rgb(ap, I), 15.5, 15, None, None, 20))


@case("sa.RemoveRasterSegmentTilingArtifacts")
def _(ap, I, O):
    sa = S(ap)
    return _split(O, sa.RemoveRasterSegmentTilingArtifacts(sa.SegmentMeanShift(_rgb(ap, I), 15.5, 15, 20), 20, 20))


@case("sa.Subset")
def _(ap, I, O):
    return S(ap).Subset(I["dem"])


# --- Raster objects -----------------------------------------------------------------------------------------------

@case("sa.Raster")
def _(ap, I, O):
    r = S(ap).Raster(I["dem"])
    e = r.extent
    return {"minimum": _r(r.minimum, 4), "maximum": _r(r.maximum, 4), "mean": _r(r.mean, 4),
            "standardDeviation": _r(r.standardDeviation, 4), "width": r.width, "height": r.height,
            "bandCount": r.bandCount, "isInteger": r.isInteger, "pixelType": r.pixelType,
            "extent": [_r(e.XMin, 3), _r(e.YMin, 3), _r(e.XMax, 3), _r(e.YMax, 3)], "format": r.format}


@case("sa.RasterInfo")
def _(ap, I, O):
    sa = S(ap)
    ri = sa.Raster(I["dem"]).getRasterInfo()
    e = ri.getExtent()
    fresh = sa.RasterInfo()
    return {"bands": ri.getBandCount(), "cell": [_r(v, 6) for v in ri.getCellSize()], "pixel": ri.getPixelType(),
            "extent": [_r(e.XMin, 3), _r(e.YMin, 3), _r(e.XMax, 3), _r(e.YMax, 3)],
            "srid": ri.getSpatialReference().factoryCode, "fresh_bands": fresh.getBandCount()}


@case("sa.RasterCellIterator")
def _(ap, I, O):
    sa = S(ap)
    r = sa.Raster(_holes(ap, I))
    out = sa.Raster(r.getRasterInfo())
    n, total = 0, 0.0
    with sa.RasterCellIterator({"rasters": [r, out], "skipNoData": [r]}) as cells:
        for i, j in cells:
            n += 1
            total += r[i, j]
            out[i, j] = r[i, j] * 2
    out.save(O("doubled", "raster"))
    return [n, _r(total, 2)]


# --- Solar --------------------------------------------------------------------------------------------------------

@case("sa.PointsSolarRadiation")
def _(ap, I, O):
    sa = S(ap)
    sa.PointsSolarRadiation(I["dem"], _observers(ap, I), O("out"), 0, 40, 200, sa.TimeWithinDay(172, 8, 16))


def _time(name, *args):
    @case("sa." + name)
    def run(ap, I, O):
        sa = S(ap)
        config = getattr(sa, name)(*args)
        sa.PointsSolarRadiation(I["dem"], _observers(ap, I), O("out"), 0, 40, 200, config, 14, 2)
        return str(config)
    return run


_time("TimeWithinDay", 172, 8, 16)
_time("TimeMultipleDays", 2026, 100, 130)
_time("TimeSpecialDays")
_time("TimeWholeYear", 2026)


@case("sa.SolarRadiationGraphics")
def _(ap, I, O):
    sa = S(ap)
    return _split(O, sa.SolarRadiationGraphics(I["dem"], _observers(ap, I), 200, 0, 32, 40))


@case("sa.RasterSolarRadiation")
def _(ap, I, O):
    sa = S(ap)
    return sa.RasterSolarRadiation(I["dem"], "6/21/2026 8:00:00 AM", "6/21/2026 4:00:00 PM")


# --- Topo to Raster -----------------------------------------------------------------------------------------------

def _topo_run(ap, I, inputs):
    return S(ap).TopoToRaster(inputs, 10, f"{I['x0']} {I['y0']} {I['x0'] + 600} {I['y0'] + 600}")


@case("sa.TopoToRaster")
def _(ap, I, O):
    sa = S(ap)
    return _topo_run(ap, I, [sa.TopoPointElevation([[I["points"], "val"]])])


@case("sa.TopoPointElevation")
def _(ap, I, O):
    sa = S(ap)
    return _topo_run(ap, I, [sa.TopoPointElevation([[_pts10(ap, I), "val"]])])


@case("sa.TopoContour")
def _(ap, I, O):
    sa = S(ap)
    return _topo_run(ap, I, [sa.TopoContour([[_contours(ap, I), "elev"]])])


def _topo_with(name, layer):
    @case("sa." + name)
    def run(ap, I, O):
        sa = S(ap)
        features = I[layer] if layer in I else _pour(ap, I)
        arg = [[features, "NONE"]] if name == "TopoSink" else [features]
        return _topo_run(ap, I, [sa.TopoContour([[_contours(ap, I), "elev"]]), getattr(sa, name)(arg)])
    return run


_topo_with("TopoBoundary", "clip")
_topo_with("TopoCliff", "lines")
_topo_with("TopoCoast", "clip")
_topo_with("TopoExclusion", "clip")
_topo_with("TopoLake", "clip")
_topo_with("TopoSink", "pour")
_topo_with("TopoStream", "lines")


@case("sa.TopoToRasterByFile")
def _(ap, I, O):
    sa = S(ap)
    contours = _contours(ap, I)
    text = (f"Contour {contours} elev\nEXTENT {I['x0']} {I['y0']} {I['x0'] + 600} {I['y0'] + 600}\n"
            "CELL_SIZE 10\nENFORCE ON\n")
    return sa.TopoToRasterByFile(_work_file(I, "topo.txt", text))
