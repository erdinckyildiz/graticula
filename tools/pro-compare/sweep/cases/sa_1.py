"""arcpy.sa against ArcGIS Pro 3.7, first half: one case for each name grapy builds, from the first in Python's sorted
order through MTVI2.

Spatial Analyst's tools answer a Raster; a case returns it and the harness dumps it (cells, extent, cell size, pixel
type). The inputs beyond the shared fixtures — a ramp crossing the trigonometric domains, a DEM with a NoData hole,
two integer rasters, an eight-band reflectance image, a multidimensional netCDF, source points, training polygons — are
built here with ``ap``, so both sides build them the same way. A class (a fuzzy function, a horizontal factor, a
kriging model, an extent) is compared through the tool that takes it.
"""
import math
import os

import numpy as np

CASES = {}
_BUILT = {}


def case(name):
    def register(function):
        CASES[name] = function
        return function
    return register


# ---- inputs -------------------------------------------------------------------------------------------------------


def _grid(I):
    size, cell = I["size"], I["cell"]
    xs = cell / 2 + cell * np.arange(size)
    ys = size * cell - cell / 2 - cell * np.arange(size)
    return np.meshgrid(xs, ys)


def _save(ap, I, name, array, nodata=None):
    """A raster from cells on the fixture's grid (a 3-D array is a multiband raster, bands first)."""
    if name in _BUILT:
        return _BUILT[name]
    path = os.path.join(I["work"], "sa1_" + name + ".tif")
    sr = ap.SpatialReference(I["srid"])
    ap.env.outputCoordinateSystem = sr
    try:
        ap.NumPyArrayToRaster(array, ap.Point(I["x0"], I["y0"]), I["cell"], I["cell"], nodata).save(path)
    finally:
        ap.env.outputCoordinateSystem = None
    ap.management.DefineProjection(path, sr)
    _BUILT[name] = path
    return path


def _ramp(ap, I):
    """−3.3 … 3.3, rising west to east, a little south to north: crosses every trigonometric function's domain."""
    gx, gy = _grid(I)
    return _save(ap, I, "ramp", ((gx - 300) / 100 + (gy - 300) / 1000).astype("float32"))


def _ramp2(ap, I):
    gx, gy = _grid(I)
    return _save(ap, I, "ramp2", ((gy - 300) / 120 - (gx - 300) / 900).astype("float32"))


def _ndem(ap, I):
    """The DEM with a NoData hole of 10 × 15 cells and a lone NoData cell."""
    import fixtures
    gx, gy = _grid(I)
    dem = np.vectorize(fixtures.dem_value)(gx, gy).astype("float32")
    dem[10:20, 20:35] = -9999
    dem[45, 7] = -9999
    return _save(ap, I, "ndem", dem, -9999)


def _ints(ap, I):
    """0 … 15, in 40 m blocks."""
    gx, gy = _grid(I)
    return _save(ap, I, "ints", ((gx // 40 + 3 * (gy // 40)) % 16).astype("int32"))


def _ints2(ap, I):
    """0 … 4, in 40 m rows."""
    gx, gy = _grid(I)
    return _save(ap, I, "ints2", ((gy // 40 + gx // 120) % 5).astype("int32"))


def _iramp(ap, I):
    gx, gy = _grid(I)
    return _save(ap, I, "iramp", ((gx - 300) // 60).astype("int32"))


def _band(I, k):
    gx, gy = _grid(I)
    u, v = gx / 600, gy / 600
    return (0.04 * k + 0.15 * u * ((k % 3) + 1) / 3 + 0.1 * v * ((k % 2) + 1) / 2
            + 0.02 * np.sin(gx / 37 + k)).astype("float32")


def _img(ap, I):
    """Eight bands of reflectance, 0.04 … 0.6, each a different mix of east, north and a ripple."""
    return _save(ap, I, "img", np.stack([_band(I, k) for k in range(1, 9)]))


def _single(ap, I, k):
    return _save(ap, I, f"b{k}", _band(I, k))


def _cost(ap, I):
    import fixtures
    gx, gy = _grid(I)
    dem = np.vectorize(fixtures.dem_value)(gx, gy)
    return _save(ap, I, "cost", (1 + (dem - 100) / 10).astype("float32"))


def _three(ap, I):
    gx, gy = _grid(I)
    a = _save(ap, I, "s1", (gx / 10).astype("float32"))
    b = _save(ap, I, "s2", (gy / 10).astype("float32"))
    c = _save(ap, I, "s3", (30 + 5 * np.sin(gx / 50) * np.cos(gy / 70)).astype("float32"))
    return [a, b, c]


def _points(ap, I, name, rows, fields=(("sid", "LONG"),)):
    """A point feature class of (values…, dx, dy) rows, offsets from the fixture's corner."""
    path = os.path.join(I["gdb"], name)
    if name in _BUILT:
        return _BUILT[name]
    sr = ap.SpatialReference(I["srid"])
    ap.management.CreateFeatureclass(I["gdb"], name, "POINT", spatial_reference=sr)
    for field, kind in fields:
        ap.management.AddField(path, field, kind)
    with ap.da.InsertCursor(path, [f for f, _ in fields] + ["SHAPE@XY"]) as cursor:
        for *values, dx, dy in rows:
            cursor.insertRow((*values, (I["x0"] + dx, I["y0"] + dy)))
    _BUILT[name] = path
    return path


def _sources(ap, I):
    return _points(ap, I, "sources", [(1, 55, 55), (2, 545, 315), (3, 205, 525)])


def _destinations(ap, I):
    return _points(ap, I, "dests", [(10, 455, 545), (20, 305, 105)])


def _training(ap, I):
    """Three classes of polygons, each over a corner of the image the bands tell apart."""
    path = os.path.join(I["gdb"], "train")
    if "train" in _BUILT:
        return path
    sr = ap.SpatialReference(I["srid"])
    ap.management.CreateFeatureclass(I["gdb"], "train", "POLYGON", spatial_reference=sr)
    ap.management.AddField(path, "classname", "TEXT")
    ap.management.AddField(path, "classvalue", "LONG")
    x0, y0 = I["x0"], I["y0"]
    rows = [("low", 1, (20, 20, 140, 140)), ("east", 2, (440, 40, 580, 180)), ("north", 3, (40, 440, 180, 580)),
            ("east", 2, (460, 300, 560, 400))]
    with ap.da.InsertCursor(path, ["classname", "classvalue", "SHAPE@"]) as cursor:
        for name, value, (a, b, c, d) in rows:
            wkt = (f"POLYGON (({x0 + a} {y0 + b}, {x0 + a} {y0 + d}, {x0 + c} {y0 + d}, {x0 + c} {y0 + b}, "
                   f"{x0 + a} {y0 + b}))")
            cursor.insertRow((name, value, ap.FromWKT(wkt, sr)))
    _BUILT["train"] = path
    return path


def _md(ap, I):
    """A multidimensional netCDF (CF): one variable, ``temp``, six monthly slices over the fixture's grid, with three
    NoData cells in the third slice."""
    if "md" in _BUILT:
        return _BUILT["md"]
    from scipy.io import netcdf_file
    import fixtures
    path = os.path.join(I["work"], "sa1_md.nc")
    gx, gy = _grid(I)
    dem = np.vectorize(fixtures.dem_value)(gx, gy)
    when = [14, 45, 195, 380, 411, 561]           # days since 2000-01-01: Jan, Feb, Jul 2000; Jan, Feb, Jul 2001
    f = netcdf_file(path, "w", version=2)
    f.createDimension("time", len(when))
    f.createDimension("y", I["size"])
    f.createDimension("x", I["size"])
    t = f.createVariable("time", "f8", ("time",))
    t.units, t.calendar, t.axis, t.standard_name = b"days since 2000-01-01 00:00:00", b"standard", b"T", b"time"
    t[:] = when
    y = f.createVariable("y", "f8", ("y",))
    y.units, y.standard_name, y.axis = b"m", b"projection_y_coordinate", b"Y"
    y[:] = I["y0"] + gy[:, 0]
    x = f.createVariable("x", "f8", ("x",))
    x.units, x.standard_name, x.axis = b"m", b"projection_x_coordinate", b"X"
    x[:] = I["x0"] + gx[0, :]
    v = f.createVariable("temp", "f4", ("time", "y", "x"))
    v._FillValue = np.float32(-9999)
    cells = np.stack([(dem - 100) / 4 + 10 * math.sin(d / 58.1) + i for i, d in enumerate(when)]).astype("float32")
    cells[2, 5, 5] = cells[2, 30, 40] = cells[2, 59, 0] = -9999
    v[:] = cells
    f.close()
    _BUILT["md"] = path
    return path


def _mdsummary(ap, r):
    """A multidimensional answer: its variables, each slice's dimension values, and each slice's mean, minimum and
    NoData count."""
    if isinstance(r, str):
        r = ap.Raster(r, True)
    cells = np.asarray(r.read(), dtype="float64")
    nd = r.noDataValue
    if nd is not None:
        cells = np.where(cells == float(nd), np.nan, cells)
    slices = []
    for i, s in enumerate(r.slices):
        a = cells[i]
        # Pro writes a time as 2000-01-15T00:00:00+00:00, grapy without the offset: the offset is left out here.
        slices.append({"dims": {k: str(v).replace("+00:00", "") for k, v in s.items()}, "mean": round(float(np.nanmean(a)), 4),
                       "min": round(float(np.nanmin(a)), 4), "nodata": int(np.isnan(a).sum())})
    return {"multidimensional": bool(r.isMultidimensional), "variables": list(r.variables), "shape": list(cells.shape),
            "pixelType": r.pixelType, "slices": slices}


def _with_env(ap, I, function, **settings):
    old = {k: getattr(ap.env, k) for k in settings}
    try:
        for k, v in settings.items():
            setattr(ap.env, k, v)
        return function()
    finally:
        for k, v in old.items():
            setattr(ap.env, k, v)


def _try(function):
    """An option grapy refuses by name (NotBuiltError) is left out, so the rest of the case is still compared; the output
    it would have written then shows as written on Pro's side only."""
    try:
        function()
    except Exception as error:  # noqa: BLE001
        if type(error).__name__ != "NotBuiltError":
            raise


def _gsg(path):
    """A signature file's numbers, comments and the layers' names (a path on Pro's side) left out, rounded."""
    out = []
    for line in open(path, encoding="utf-8", errors="replace"):
        if line.strip().startswith("#") or (line.startswith("/*") and len(line.split()) > 2):
            continue
        for token in line.split():
            try:
                out.append(round(float(token), 3))
            except ValueError:
                out.append(token)
    return out


def _signature(ap, I, O):
    path = os.path.join(I["work"], "sa1_sig.gsg")
    if "sig" not in _BUILT:
        ap.sa.CreateSignatures([_single(ap, I, k) for k in (2, 3, 4)], _training(ap, I), path, "COVARIANCE",
                               "classvalue")
        _BUILT["sig"] = path
    return path


def _fd(ap, I):
    if "fd" not in _BUILT:
        path = os.path.join(I["work"], "sa1_fd.tif")
        ap.sa.FlowDirection(I["dem"]).save(path)   # unfilled: the pit is a sink, and there is no flat
        _BUILT["fd"] = path
    return _BUILT["fd"]


def _distance_and_backlink(ap, I):
    if "cd" not in _BUILT:
        cd, bl = os.path.join(I["work"], "sa1_cd.tif"), os.path.join(I["work"], "sa1_bl.tif")
        ap.sa.CostDistance(_sources(ap, I), _cost(ap, I), out_backlink_raster=bl).save(cd)
        _BUILT["cd"] = (cd, bl)
    return _BUILT["cd"]


# ---- one input --------------------------------------------------------------------------------------------------

for _name in ("ACos", "ACosH", "ASin", "ASinH", "ATan", "ATanH", "Cos", "CosH", "Exp", "Exp10", "Exp2", "Float",
              "Int", "Ln", "Log10", "Log2", "Abs"):
    def _unary(ap, I, O, _name=_name):
        out = getattr(ap.sa, _name)(_ramp(ap, I))
        if _name in ("Abs", "Float", "Exp2", "Ln"):
            getattr(ap.sa, _name)(_iramp(ap, I)).save(O("int", "raster"))
        return out
    case("sa." + _name)(_unary)


@case("sa.ATan2")
def _(ap, I, O):
    return ap.sa.ATan2(_ramp(ap, I), _ramp2(ap, I))


@case("sa.Divide")
def _(ap, I, O):
    ap.sa.Divide(_ints(ap, I), _ints2(ap, I)).save(O("int", "raster"))
    return ap.sa.Divide(I["dem"], _ramp(ap, I))


@case("sa.Diff")
def _(ap, I, O):
    ap.sa.Diff(I["dem"], _ndem(ap, I)).save(O("float", "raster"))
    return ap.sa.Diff(_ints(ap, I), _ints2(ap, I))


for _name in ("BitwiseAnd", "BitwiseOr", "BitwiseXOr", "BooleanAnd", "BooleanOr", "BooleanXOr", "EqualTo",
              "GreaterThan", "GreaterThanEqual", "LessThan", "LessThanEqual", "CombinatorialAnd", "CombinatorialOr",
              "CombinatorialXOr"):
    def _binary(ap, I, O, _name=_name):
        return getattr(ap.sa, _name)(_ints(ap, I), _ints2(ap, I))
    case("sa." + _name)(_binary)


@case("sa.BitwiseLeftShift")
def _(ap, I, O):
    return ap.sa.BitwiseLeftShift(_ints(ap, I), _ints2(ap, I))


@case("sa.BitwiseRightShift")
def _(ap, I, O):
    return ap.sa.BitwiseRightShift(_ints(ap, I), 2)


@case("sa.BitwiseNot")
def _(ap, I, O):
    return ap.sa.BitwiseNot(_ints(ap, I))


@case("sa.BooleanNot")
def _(ap, I, O):
    ap.sa.BooleanNot(_ramp(ap, I)).save(O("float", "raster"))
    return ap.sa.BooleanNot(_ints2(ap, I))


@case("sa.IsNull")
def _(ap, I, O):
    return ap.sa.IsNull(_ndem(ap, I))


@case("sa.Con")
def _(ap, I, O):
    ap.sa.Con(I["zones"], 1, 0, "VALUE >= 3").save(O("int", "raster"))
    ap.sa.Con(_ndem(ap, I), _ndem(ap, I), I["dem"], "VALUE > 130").save(O("nd", "raster"))
    return ap.sa.Con(I["dem"], I["dem"], 0, "VALUE > 130")


# ---- several inputs -----------------------------------------------------------------------------------------------


@case("sa.Arithmetic")
def _(ap, I, O):
    ap.sa.Arithmetic(_ints(ap, I), _ints2(ap, I), "Multiply").save(O("int", "raster"))
    return ap.sa.Arithmetic(I["dem"], _ramp(ap, I), "Minus")


@case("sa.CellStatistics")
def _(ap, I, O):
    ap.sa.CellStatistics([I["dem"], _ndem(ap, I), _ramp(ap, I)], "MAXIMUM", "NODATA").save(O("max", "raster"))
    ap.sa.CellStatistics([_ints(ap, I), _ints2(ap, I), I["zones"]], "MAJORITY", "DATA").save(O("maj", "raster"))
    ap.sa.CellStatistics([_ints(ap, I), _ints2(ap, I), I["zones"]], "STD", "DATA").save(O("std", "raster"))
    ap.sa.CellStatistics([_ints(ap, I), _ints2(ap, I), I["zones"], 3], "MINORITY", "DATA").save(O("min", "raster"))
    return ap.sa.CellStatistics([I["dem"], _ndem(ap, I), _ramp(ap, I)], "MEAN", "DATA")


@case("sa.EqualToFrequency")
def _(ap, I, O):
    ap.sa.EqualToFrequency(_ints2(ap, I), [_ints(ap, I), I["zones"]]).save(O("rasters", "raster"))
    return ap.sa.EqualToFrequency(_ints2(ap, I), [_ints(ap, I), I["zones"], 2])


@case("sa.GreaterThanFrequency")
def _(ap, I, O):
    return ap.sa.GreaterThanFrequency(_three(ap, I)[2], _three(ap, I)[:2] + [I["dem"]])


@case("sa.LessThanFrequency")
def _(ap, I, O):
    return ap.sa.LessThanFrequency(_three(ap, I)[2], _three(ap, I)[:2] + [I["dem"]])


@case("sa.HighestPosition")
def _(ap, I, O):
    return ap.sa.HighestPosition(_three(ap, I))


@case("sa.LowestPosition")
def _(ap, I, O):
    return ap.sa.LowestPosition(_three(ap, I))


@case("sa.ArgStatistics")
def _(ap, I, O):
    ap.sa.ArgStatistics(_three(ap, I), "MIN").save(O("min", "raster"))
    return ap.sa.ArgStatistics(_three(ap, I), "MAX")


@case("sa.InList")
def _(ap, I, O):
    return ap.sa.InList(_ints2(ap, I), [I["zones"], 0])


@case("sa.Combine")
def _(ap, I, O):
    return ap.sa.Combine([I["zones"], _ints2(ap, I)])


@case("sa.Lookup")
def _(ap, I, O):
    return ap.sa.Lookup(I["zones"], "Count")


@case("sa.Corridor")
def _(ap, I, O):
    a = os.path.join(I["work"], "sa1_corr_a.tif")
    b = os.path.join(I["work"], "sa1_corr_b.tif")
    ap.sa.CostDistance(_points(ap, I, "src_a", [(1, 55, 55)]), _cost(ap, I)).save(a)
    ap.sa.CostDistance(_points(ap, I, "src_b", [(1, 545, 545)]), _cost(ap, I)).save(b)
    return ap.sa.Corridor(a, b)


# ---- surfaces and neighbourhoods ----------------------------------------------------------------------------------


@case("sa.Aspect")
def _(ap, I, O):
    return ap.sa.Aspect(I["dem"])


@case("sa.Hillshade")
def _(ap, I, O):
    _try(lambda: ap.sa.Hillshade(I["dem"], 200, 20, "SHADOWS", 3).save(O("shadows", "raster")))
    return ap.sa.Hillshade(I["dem"], 315, 45, "NO_SHADOWS", 1)


@case("sa.Curvature")
def _(ap, I, O):
    return ap.sa.Curvature(I["dem"], 1, O("profile", "raster"), O("plan", "raster"))


@case("sa.Aggregate")
def _(ap, I, O):
    ap.sa.Aggregate(I["dem"], 7, "MAXIMUM", "TRUNCATE", "DATA").save(O("truncate", "raster"))
    ap.sa.Aggregate(_ndem(ap, I), 4, "SUM", "EXPAND", "NODATA").save(O("nodata", "raster"))
    ap.sa.Aggregate(_ndem(ap, I), 4, "MEDIAN", "EXPAND", "DATA").save(O("median", "raster"))
    return ap.sa.Aggregate(I["dem"], 7, "MEAN", "EXPAND", "DATA")


@case("sa.BlockStatistics")
def _(ap, I, O):
    ap.sa.BlockStatistics(_ndem(ap, I), ap.sa.NbrRectangle(4, 4, "CELL"), "RANGE", "NODATA").save(O("nodata", "raster"))
    ap.sa.BlockStatistics(_ints(ap, I), ap.sa.NbrRectangle(7, 7, "CELL"), "MAJORITY").save(O("majority", "raster"))
    return ap.sa.BlockStatistics(I["dem"], ap.sa.NbrRectangle(4, 4, "CELL"), "MEAN")


@case("sa.FocalStatistics")
def _(ap, I, O):
    ap.sa.FocalStatistics(_ndem(ap, I), ap.sa.NbrRectangle(3, 3, "CELL"), "RANGE", "NODATA").save(O("nodata", "raster"))
    ap.sa.FocalStatistics(I["dem"], ap.sa.NbrCircle(3, "CELL"), "STD").save(O("circle", "raster"))
    ap.sa.FocalStatistics(_ints(ap, I), ap.sa.NbrRectangle(5, 5, "CELL"), "MAJORITY").save(O("majority", "raster"))
    return ap.sa.FocalStatistics(_ndem(ap, I), ap.sa.NbrRectangle(3, 3, "CELL"), "MEAN", "DATA")


@case("sa.Filter")
def _(ap, I, O):
    ap.sa.Filter(_ndem(ap, I), "HIGH", "DATA").save(O("high", "raster"))
    return ap.sa.Filter(I["dem"], "LOW")


@case("sa.Expand")
def _(ap, I, O):
    ap.sa.Expand(_ints2(ap, I), 1, [0], "DISTANCE").save(O("distance", "raster"))
    return ap.sa.Expand(I["zones"], 2, [1, 3], "MORPHOLOGICAL")


@case("sa.CutFill")
def _(ap, I, O):
    import fixtures
    gx, gy = _grid(I)
    after = np.vectorize(fixtures.dem_value)(gx, gy).astype("float32")
    after[20:40, 20:40] = 130.0                     # the hill's top cut, its side filled, to one level
    path = _save(ap, I, "after", after)
    _with_env(ap, I, lambda: ap.sa.CutFill(I["dem"], path, 1).save(O("out", "raster")), scratchWorkspace=I["work"],
              workspace=I["work"])
    rows = []
    with ap.da.SearchCursor(O("out", "raster"), ["VALUE", "COUNT", "AREA", "VOLUME"]) as cursor:
        for row in cursor:
            rows.append([round(float(v), 2) for v in row])
    return sorted(rows)


@case("sa.Contour")
def _(ap, I, O):
    _try(lambda: ap.sa.Contour(I["dem"], O("poly"), 10, 105, 1, "CONTOUR_POLYGON"))
    return ap.sa.Contour(I["dem"], O("out"), 10, 105)


@case("sa.ContourList")
def _(ap, I, O):
    return ap.sa.ContourList(I["dem"], O("out"), [112.5, 130, 151])


@case("sa.ContourWithBarriers")
def _(ap, I, O):
    return ap.sa.ContourWithBarriers(I["dem"], O("out"), I["clip"], "POLYLINES", None, "NO_EXPLICIT_VALUES_ONLY", 0,
                                     10)


@case("sa.AddSurfaceInformation")
def _(ap, I, O):
    lines = ap.management.CopyFeatures(I["lines"], O("lines"))[0]
    points = ap.management.CopyFeatures(I["points"], O("points"))[0]
    _try(lambda: ap.sa.AddSurfaceInformation(lines, I["dem"],
                                             "Z_MIN;Z_MAX;Z_MEAN;SURFACE_LENGTH;MIN_SLOPE;MAX_SLOPE;AVG_SLOPE", "BILINEAR"))
    ap.sa.AddSurfaceInformation(points, I["dem"], "Z", "BILINEAR")


@case("sa.InterpolateShape")
def _(ap, I, O):
    ap.sa.InterpolateShape(I["dem"], I["lines"], O("out"), None, 1, "BILINEAR")
    zs = []
    with ap.da.SearchCursor(O("out"), ["lid", "SHAPE@"]) as cursor:
        for lid, shape in sorted(cursor, key=lambda r: r[0]):
            zs.append([lid, [round(p.Z, 5) for part in shape for p in part if p is not None]])
    return zs


@case("sa.ElevationVoidFill")
def _(ap, I, O):
    return ap.sa.ElevationVoidFill(_ndem(ap, I), 0)


@case("sa.AreaSolarRadiation")
def _(ap, I, O):
    return ap.sa.AreaSolarRadiation(I["dem"], 40.65, 200, ap.sa.TimeWithinDay(172, 8, 16), 14, 1.0)


# ---- hydrology ----------------------------------------------------------------------------------------------------


@case("sa.Fill")
def _(ap, I, O):
    _try(lambda: ap.sa.Fill(I["dem"], 10).save(O("limit", "raster")))
    return ap.sa.Fill(I["dem"])


@case("sa.FlowDirection")
def _(ap, I, O):
    _try(lambda: ap.sa.FlowDirection(I["dem"], "NORMAL", O("drop", "raster")).save(O("dropped", "raster")))
    _try(lambda: ap.sa.FlowDirection(ap.sa.Fill(I["dem"]), "FORCE").save(O("filled", "raster")))
    return ap.sa.FlowDirection(I["dem"])


@case("sa.FlowAccumulation")
def _(ap, I, O):
    ap.sa.FlowAccumulation(_fd(ap, I), _ramp(ap, I), "FLOAT").save(O("weighted", "raster"))
    return ap.sa.FlowAccumulation(_fd(ap, I))


@case("sa.FlowLength")
def _(ap, I, O):
    ap.sa.FlowLength(_fd(ap, I), "UPSTREAM").save(O("upstream", "raster"))
    return ap.sa.FlowLength(_fd(ap, I), "DOWNSTREAM")


@case("sa.Basin")
def _(ap, I, O):
    return ap.sa.Basin(_fd(ap, I))


@case("sa.DarcyFlow")
def _(ap, I, O):
    return ap.sa.DarcyFlow(*_darcy(ap, I), O("direction", "raster"), O("magnitude", "raster"))


@case("sa.DarcyVelocity")
def _(ap, I, O):
    return ap.sa.DarcyVelocity(*_darcy(ap, I), O("direction", "raster"))


def _darcy(ap, I):
    gx, gy = _grid(I)
    head = _save(ap, I, "head", (200 - gx / 10 - gy / 30 + 3 * np.sin(gx / 90)).astype("float32"))
    porosity = _save(ap, I, "porosity", (0.2 + 0.1 * gy / 600).astype("float32"))
    thickness = _save(ap, I, "thickness", (10 + gx / 60).astype("float32"))
    transmissivity = _save(ap, I, "transmissivity", (0.5 + gy / 1200).astype("float32"))
    return head, porosity, thickness, transmissivity


# ---- distance -----------------------------------------------------------------------------------------------------


@case("sa.CostDistance")
def _(ap, I, O):
    return ap.sa.CostDistance(_sources(ap, I), _cost(ap, I), None, O("backlink", "raster"))


@case("sa.CostBackLink")
def _(ap, I, O):
    return ap.sa.CostBackLink(_sources(ap, I), _cost(ap, I), None, O("distance", "raster"))


@case("sa.CostAllocation")
def _(ap, I, O):
    return ap.sa.CostAllocation(_sources(ap, I), _cost(ap, I), None, None, "sid", O("distance", "raster"))


@case("sa.CostPath")
def _(ap, I, O):
    cd, bl = _distance_and_backlink(ap, I)
    ap.sa.CostPath(_destinations(ap, I), cd, bl, "BEST_SINGLE", "sid").save(O("single", "raster"))
    return ap.sa.CostPath(_destinations(ap, I), cd, bl, "EACH_CELL", "sid")


@case("sa.CostPathAsPolyline")
def _(ap, I, O):
    cd, bl = _distance_and_backlink(ap, I)
    ap.sa.CostPathAsPolyline(_destinations(ap, I), cd, bl, O("each"), "EACH_ZONE", "sid")
    return ap.sa.CostPathAsPolyline(_destinations(ap, I), cd, bl, O("out"), "BEST_SINGLE", "sid")


@case("sa.EucDistance")
def _(ap, I, O):
    _try(lambda: ap.sa.EucDistance(_sources(ap, I), None, 10, O("direction", "raster")).save(O("with_direction", "raster")))
    return ap.sa.EucDistance(_sources(ap, I), None, 10)


@case("sa.EucDirection")
def _(ap, I, O):
    return ap.sa.EucDirection(_sources(ap, I), 250, 10, O("distance", "raster"))


@case("sa.EucAllocation")
def _(ap, I, O):
    return ap.sa.EucAllocation(_sources(ap, I), None, None, 10, "sid", O("distance", "raster"))


@case("sa.DistanceAccumulation")
def _(ap, I, O):
    return ap.sa.DistanceAccumulation(_sources(ap, I), None, I["dem"], _cost(ap, I),
                                      out_back_direction_raster=O("back", "raster"))


@case("sa.DistanceAllocation")
def _(ap, I, O):
    return ap.sa.DistanceAllocation(_sources(ap, I), None, I["dem"], _cost(ap, I),
                                    out_distance_accumulation_raster=O("distance", "raster"), source_field="sid")


def _horizontal(ap, I, O, factor):
    gx, gy = _grid(I)
    hdir = _save(ap, I, "hdir", ((gx * 0.3 + gy * 0.1) % 360).astype("float32"))
    return ap.sa.PathDistance(_sources(ap, I), _cost(ap, I), None, hdir, factor)


@case("sa.HfBinary")
def _(ap, I, O):
    return _horizontal(ap, I, O, ap.sa.HfBinary(1.0, 60))


@case("sa.HfForward")
def _(ap, I, O):
    return _horizontal(ap, I, O, ap.sa.HfForward(0.5, 1.5))


@case("sa.HfLinear")
def _(ap, I, O):
    return _horizontal(ap, I, O, ap.sa.HfLinear(0.5, 120, 0.02))


@case("sa.HfInverseLinear")
def _(ap, I, O):
    return _horizontal(ap, I, O, ap.sa.HfInverseLinear(2.0, 150, -0.01))


@case("sa.HfTable")
def _(ap, I, O):
    path = os.path.join(I["work"], "sa1_hf.txt")
    with open(path, "w") as f:
        f.write("0 1.0\n45 1.5\n90 2.0\n135 3.0\n180 4.0\n")
    return _horizontal(ap, I, O, ap.sa.HfTable(path))


# ---- creation and extraction --------------------------------------------------------------------------------------


@case("sa.CreateConstantRaster")
def _(ap, I, O):
    e = ap.Extent(I["x0"], I["y0"], I["x0"] + 200, I["y0"] + 150)
    ap.sa.CreateConstantRaster(7, "INTEGER", 25, e).save(O("int", "raster"))
    return ap.sa.CreateConstantRaster(5.5, "FLOAT", 10, e)


def _describe_random(ap, r):
    a = ap.RasterToNumPyArray(r).astype("float64")
    e = r.extent
    return {"extent": [round(v, 3) for v in (e.XMin, e.YMin, e.XMax, e.YMax)], "cell": round(r.meanCellWidth, 3),
            "pixelType": r.pixelType, "shape": list(a.shape), "min_ge_0": bool(a.min() >= 0),
            "max_le_1": bool(a.max() <= 1), "mean": round(float(a.mean()), 1), "std": round(float(a.std()), 1)}


@case("sa.CreateRandomRaster")
def _(ap, I, O):
    return _describe_random(ap, ap.sa.CreateRandomRaster(42, 10, ap.Extent(I["x0"], I["y0"], I["x0"] + 600,
                                                                               I["y0"] + 400)))


@case("sa.CreateNormalRaster")
def _(ap, I, O):
    r = ap.sa.CreateNormalRaster(10, ap.Extent(I["x0"], I["y0"], I["x0"] + 600, I["y0"] + 400))
    out = _describe_random(ap, r)
    out.pop("min_ge_0")
    out.pop("max_le_1")
    return out


@case("sa.Extent")
def _(ap, I, O):
    e = ap.sa.Extent(I["x0"] + 100, I["y0"] + 50, I["x0"] + 400, I["y0"] + 350)
    out = ap.sa.ExtractByRectangle(I["dem"], e, "INSIDE")
    return out


@case("sa.ExtractByAttributes")
def _(ap, I, O):
    ap.sa.ExtractByAttributes(I["dem"], "VALUE > 140").save(O("float", "raster"))
    return ap.sa.ExtractByAttributes(I["zones"], "VALUE >= 3")


@case("sa.ExtractByCircle")
def _(ap, I, O):
    ap.sa.ExtractByCircle(I["dem"], ap.Point(I["x0"] + 300, I["y0"] + 300), 150, "OUTSIDE").save(O("out", "raster"))
    return ap.sa.ExtractByCircle(I["dem"], ap.Point(I["x0"] + 300, I["y0"] + 300), 150, "INSIDE")


@case("sa.ExtractByMask")
def _(ap, I, O):
    ap.sa.ExtractByMask(I["dem"], I["polys"], "OUTSIDE").save(O("outside", "raster"))
    ap.sa.ExtractByMask(I["dem"], _ndem(ap, I)).save(O("raster_mask", "raster"))
    return ap.sa.ExtractByMask(I["dem"], I["polys"])


@case("sa.ExtractByPoints")
def _(ap, I, O):
    pts = [ap.Point(I["x0"] + dx, I["y0"] + dy) for dx, dy in ((55, 55), (305, 295), (590, 590), (123, 456))]
    ap.sa.ExtractByPoints(I["dem"], pts, "OUTSIDE").save(O("outside", "raster"))
    return ap.sa.ExtractByPoints(I["dem"], pts, "INSIDE")


@case("sa.ExtractByPolygon")
def _(ap, I, O):
    pts = [ap.Point(I["x0"] + dx, I["y0"] + dy) for dx, dy in ((100, 100), (500, 150), (300, 520), (100, 100))]
    ap.sa.ExtractByPolygon(I["dem"], pts, "OUTSIDE").save(O("outside", "raster"))
    return ap.sa.ExtractByPolygon(I["dem"], pts, "INSIDE")


@case("sa.ExtractByRectangle")
def _(ap, I, O):
    e = ap.Extent(I["x0"] + 105, I["y0"] + 55, I["x0"] + 395, I["y0"] + 355)
    ap.sa.ExtractByRectangle(I["dem"], e, "OUTSIDE").save(O("outside", "raster"))
    return ap.sa.ExtractByRectangle(I["dem"], e, "INSIDE")


@case("sa.ExtractValuesToPoints")
def _(ap, I, O):
    ap.sa.ExtractValuesToPoints(I["points"], _ndem(ap, I), O("plain"), "NONE", "VALUE_ONLY")
    return ap.sa.ExtractValuesToPoints(I["points"], I["dem"], O("out"), "INTERPOLATE", "ALL")


@case("sa.ExtractMultiValuesToPoints")
def _(ap, I, O):
    points = ap.management.CopyFeatures(I["points"], O("out"))[0]
    ap.sa.ExtractMultiValuesToPoints(points, [[I["dem"], "dem_v"], [I["zones"], "zone_v"], [_ndem(ap, I), "nd_v"]],
                                     "BILINEAR")
    points = ap.management.CopyFeatures(I["points"], O("nearest"))[0]
    ap.sa.ExtractMultiValuesToPoints(points, [[I["dem"], "dem_v"], [I["zones"], "zone_v"]], "NONE")


@case("sa.ApplyEnvironment")
def _(ap, I, O):
    e = ap.Extent(I["x0"] + 100, I["y0"] + 100, I["x0"] + 400, I["y0"] + 450)
    _with_env(ap, I, lambda: ap.sa.ApplyEnvironment(I["dem"]).save(O("out", "raster")), extent=e, cellSize=20)
    _try(lambda: _with_env(ap, I, lambda: ap.sa.ApplyEnvironment(I["dem"]).save(O("mask", "raster")), mask=I["clip"]))


# ---- interpolation and density ------------------------------------------------------------------------------------


@case("sa.Idw")
def _(ap, I, O):
    ap.sa.Idw(I["points"], "val", 10, 2, ap.sa.RadiusFixed(150, 3)).save(O("fixed", "raster"))
    return ap.sa.Idw(I["points"], "val", 10, 2, ap.sa.RadiusVariable(12))


def _kriging(ap, I, model):
    return ap.sa.Kriging(I["points"], "val", model, 10, ap.sa.RadiusVariable(12))


@case("sa.Kriging")
def _(ap, I, O):
    return _kriging(ap, I, ap.sa.KrigingModelOrdinary("SPHERICAL", 20))


@case("sa.KrigingModelOrdinary")
def _(ap, I, O):
    return _kriging(ap, I, ap.sa.KrigingModelOrdinary("EXPONENTIAL", 20, 300, 600, 50))


@case("sa.KrigingModelUniversal")
def _(ap, I, O):
    return _kriging(ap, I, ap.sa.KrigingModelUniversal("LINEARDRIFT", 20, 300, 600, 50))


@case("sa.KernelDensity")
def _(ap, I, O):
    ap.sa.KernelDensity(I["points"], "val", 10, 120, "SQUARE_KILOMETERS").save(O("weighted", "raster"))
    return ap.sa.KernelDensity(I["points"], "NONE", 10, 120, "SQUARE_KILOMETERS", "DENSITIES", "PLANAR")


@case("sa.LineDensity")
def _(ap, I, O):
    return ap.sa.LineDensity(I["lines"], "NONE", 10, 60, "SQUARE_KILOMETERS")


@case("sa.LineStatistics")
def _(ap, I, O):
    ap.sa.LineStatistics(I["lines"], "lid", 10, 60, "LENGTH").save(O("length", "raster"))
    return ap.sa.LineStatistics(I["lines"], "lid", 10, 60, "MEAN")


@case("sa.HeatIndex")
def _(ap, I, O):
    gx, gy = _grid(I)
    t = _save(ap, I, "temp_f", (70 + 35 * gx / 600).astype("float32"))
    rh = _save(ap, I, "rh", (20 + 75 * gy / 600).astype("float32"))
    ap.sa.HeatIndex(t, rh, "Fahrenheit", "Celsius").save(O("celsius", "raster"))
    return ap.sa.HeatIndex(t, rh)


# ---- fuzzy --------------------------------------------------------------------------------------------------------


@case("sa.FuzzyMembership")
def _(ap, I, O):
    ap.sa.FuzzyMembership(I["dem"], ap.sa.FuzzyLinear(110, 160), "VERY").save(O("very", "raster"))
    return ap.sa.FuzzyMembership(I["dem"], ap.sa.FuzzyLinear(110, 160), "SOMEWHAT")


for _name, _args in (("FuzzyGaussian", (130, 0.01)), ("FuzzyLarge", (130, 5)), ("FuzzyLinear", (160, 110)),
                     ("FuzzyMSLarge", (1, 1)), ("FuzzyMSSmall", (1, 0.5)), ("FuzzyNear", (130, 0.01)),
                     ("FuzzySmall", (130, 5))):
    def _fuzzy(ap, I, O, _name=_name, _args=_args):
        return ap.sa.FuzzyMembership(I["dem"], getattr(ap.sa, _name)(*_args))
    case("sa." + _name)(_fuzzy)


@case("sa.FuzzyOverlay")
def _(ap, I, O):
    a = os.path.join(I["work"], "sa1_fz_a.tif")
    b = os.path.join(I["work"], "sa1_fz_b.tif")
    ap.sa.FuzzyMembership(I["dem"], ap.sa.FuzzyLinear(110, 160)).save(a)
    ap.sa.FuzzyMembership(_ramp(ap, I), ap.sa.FuzzyGaussian(0, 0.5)).save(b)
    for kind in ("OR", "PRODUCT", "SUM", "GAMMA"):
        ap.sa.FuzzyOverlay([a, b], kind, 0.8).save(O(kind.lower(), "raster"))
    return ap.sa.FuzzyOverlay([a, b], "AND")


# ---- bands and indices --------------------------------------------------------------------------------------------


for _name, _args in (("BAI", (3, 4)), ("CIg", (7, 3)), ("CIre", (7, 6)), ("ClayMinerals", (6, 7)),
                     ("EVI", (5, 4, 2)), ("FerrousMinerals", (6, 5)), ("GEMI", (4, 3)), ("GNDVI", (5, 2)),
                     ("GVITM", (1, 2, 3, 4, 5, 7)), ("IronOxide", (4, 2)), ("MSAVI", (4, 3)), ("MTVI2", (4, 3, 2))):
    def _index(ap, I, O, _name=_name, _args=_args):
        return getattr(ap.sa, _name)(_img(ap, I), *_args)
    case("sa." + _name)(_index)


@case("sa.BandArithmetic")
def _(ap, I, O):
    _try(lambda: ap.sa.BandArithmetic(_img(ap, I), "(B4 - B3) / (B4 + B3) * B1", 0).save(O("expression", "raster")))
    ap.sa.BandArithmetic(_img(ap, I), "4 3 0.33", 2).save(O("savi", "raster"))
    return ap.sa.BandArithmetic(_img(ap, I), "4 3", 1)


@case("sa.CompositeBand")
def _(ap, I, O):
    return ap.sa.CompositeBand([_single(ap, I, k) for k in (2, 3, 4)])


@case("sa.ExtractBand")
def _(ap, I, O):
    return ap.sa.ExtractBand(_img(ap, I), [2, 5, 7])


def _rgb(ap, I):
    gx, gy = _grid(I)
    return _save(ap, I, "rgb", np.stack([(gx * 0.42) % 256, (gy * 0.42) % 256, (gx + gy) * 0.2 % 256]).astype("uint8"))


@case("sa.ColorspaceConversion")
def _(ap, I, O):
    hsv = os.path.join(I["work"], "sa1_hsv.tif")
    ap.sa.ColorspaceConversion(_rgb(ap, I), "rgb_to_hsv").save(hsv)
    ap.sa.ColorspaceConversion(hsv, "hsv_to_rgb").save(O("back", "raster"))
    return ap.Raster(hsv)


@case("sa.ColormapToRGB")
def _(ap, I, O):
    from PIL import Image
    path = os.path.join(I["work"], "sa1_palette.tif")
    if not os.path.exists(path):
        cells = np.asarray(ap.RasterToNumPyArray(I["zones"]), dtype="uint8")
        image = Image.fromarray(cells, "P")
        palette = [0, 0, 0, 200, 30, 30, 30, 200, 30, 30, 30, 200, 220, 220, 20] + [0] * (256 * 3 - 15)
        image.putpalette(palette)
        image.save(path)
        with open(path[:-4] + ".tfw", "w") as f:
            f.write(f"{I['cell']}\n0\n0\n{-I['cell']}\n{I['x0'] + I['cell'] / 2}\n"
                    f"{I['y0'] + I['size'] * I['cell'] - I['cell'] / 2}\n")
    return ap.sa.ColormapToRGB(path)


@case("sa.Complex")
def _(ap, I, O):
    gx, gy = _grid(I)
    path = _save(ap, I, "complex", ((gx / 100) + 1j * (gy / 100)).astype("complex64"))
    return ap.sa.Complex(path)


@case("sa.LinearUnmixing")
def _(ap, I, O):
    ap.sa.LinearUnmixing(_img(ap, I), _training(ap, I), "SUM_TO_ONE;NON_NEGATIVE").save(O("constrained", "raster"))
    return ap.sa.LinearUnmixing(_img(ap, I), _training(ap, I))


# ---- classification -----------------------------------------------------------------------------------------------


@case("sa.CreateSignatures")
def _(ap, I, O):
    return _gsg(_signature(ap, I, O))


@case("sa.EditSignatures")
def _(ap, I, O):
    remap = os.path.join(I["work"], "sa1_remap.txt")
    with open(remap, "w") as f:
        f.write("1 : 5\n3 : -9999\n")
    out = os.path.join(I["work"], "sa1_edited.gsg")
    ap.sa.EditSignatures([_single(ap, I, k) for k in (2, 3, 4)], _signature(ap, I, O), remap, out)
    return _gsg(out)


@case("sa.ClassProbability")
def _(ap, I, O):
    return ap.sa.ClassProbability([_single(ap, I, k) for k in (2, 3, 4)], _signature(ap, I, O), 100)


@case("sa.InspectTrainingSamples")
def _(ap, I, O):
    ecd = os.path.join(I["work"], "sa1_ml.ecd")
    three = os.path.join(I["work"], "sa1_three.tif")
    ap.sa.CompositeBand([_single(ap, I, k) for k in (2, 3, 4)]).save(three)
    ap.sa.TrainMaximumLikelihoodClassifier(three, _training(ap, I), ecd)
    return ap.sa.InspectTrainingSamples(three, _training(ap, I), ecd, O("samples"), O("misclassified", "raster"))


@case("sa.ComputeSegmentAttributes")
def _(ap, I, O):
    # Pro takes only a raster it has marked segmented — Segment Mean Shift's output (a raster of segment numbers is
    # refused, ERROR 001647) — so the segments are each side's own Segment Mean Shift (grapy's is an equivalent).
    segments = os.path.join(I["work"], "sa1_segments.tif")
    ap.sa.SegmentMeanShift(_rgb(ap, I)).save(segments)
    return ap.sa.ComputeSegmentAttributes(segments, _img(ap, I), "COLOR;MEAN;STD;COUNT;COMPACTNESS;RECTANGULARITY")


@case("sa.ExportTrainingDataForDeepLearning")
def _(ap, I, O):
    import shutil
    folder = O("chips", "folder")
    for entry in os.listdir(folder):
        target = os.path.join(folder, entry)
        shutil.rmtree(target) if os.path.isdir(target) else os.remove(target)
    out = os.path.join(folder, "out")
    rgb = _rgb(ap, I)
    ap.sa.ExportTrainingDataForDeepLearning(rgb, out, _training(ap, I), "TIFF", 32, 32, 32, 32,
                                            "ONLY_TILES_WITH_FEATURES", "Classified_Tiles", 0, "classvalue")
    files = []
    for root, _, names in os.walk(out):
        for n in names:
            files.append(os.path.relpath(os.path.join(root, n), out).replace("\\", "/"))
    return sorted(files)


@case("sa.DeepLearningModelToEcd")
def _(ap, I, O):
    raise NotImplementedError("needs a pixel classification model (.dlpk) trained on this side")


# ---- multidimensional ---------------------------------------------------------------------------------------------


@case("sa.AggregateMultidimensionalRaster")
def _(ap, I, O):
    return _mdsummary(ap, ap.sa.AggregateMultidimensionalRaster(ap.Raster(_md(ap, I), True), "StdTime", "MEAN",
                                                                "temp", "INTERVAL_KEYWORD", "YEARLY"))


@case("sa.Anomaly")
def _(ap, I, O):
    return _mdsummary(ap, ap.sa.Anomaly(ap.Raster(_md(ap, I), True), "StdTime", "DIFFERENCE_FROM_MEAN"))


@case("sa.GenerateMultidimensionalAnomaly")
def _(ap, I, O):
    return _mdsummary(ap, ap.sa.GenerateMultidimensionalAnomaly(ap.Raster(_md(ap, I), True), "temp",
                                                                "PERCENT_DIFFERENCE_FROM_MEAN", "ALL"))


@case("sa.DimensionalMovingStatistics")
def _(ap, I, O):
    return _mdsummary(ap, ap.sa.DimensionalMovingStatistics(ap.Raster(_md(ap, I), True), "StdTime", 1, 1, "DATA",
                                                            "MEAN"))


@case("sa.Foreach")
def _(ap, I, O):
    # Slope, Aspect and Statistics answer NoData everywhere on Pro's side over this netCDF (no coordinate system);
    # Arithmetic and Remap, by their raster function argument names, answer cells.
    md = ap.Raster(_md(ap, I), True)
    return {"times": _mdsummary(ap, ap.sa.Foreach(md, "Arithmetic", {"Raster2": 2, "Operation": 3})),
            "remap": _mdsummary(ap, ap.sa.Foreach(md, "Remap", {"InputRanges": [0, 10, 10, 100],
                                                                 "OutputValues": [1, 2]}))}
