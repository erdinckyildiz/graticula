"""arcpy.ia against ArcGIS Pro 3.7: one case for each name grapy builds in arcpy.ia that is not also a name grapy builds
in arcpy.sa (those are the same function and were compared under sa — cases/sa_1.py and sa_2.py).

The inputs beyond the shared fixtures are sa_1's (the eight-band image, the integer rasters, the training polygons,
the six-slice netCDF) and, built here with ``ap``: a 16-day series over nine years for the trend and change tools, a
second variable for the correlation, point features with a date for the spatiotemporal interpolation, detections for
the deep-learning post-processing tools. Pro's change-analysis tools read the series from a CRF copy (CCDC fails on
the netCDF itself, ADR-236's comparison); grapy refuses CRF and reads the netCDF — the only side-dependent step.
"""
import datetime
import math
import os

import numpy as np

from . import sa_1 as S

CASES = {}
_BUILT = {}


def case(name):
    def register(function):
        CASES[name] = function
        return function
    return register


def _pro(ap):
    return ap.__name__ == "arcpy"


def _three(ap, I):
    """Bands 2, 3 and 4 of the image as one three-band raster."""
    path = os.path.join(I["work"], "ia_three.tif")
    if "three" not in _BUILT:
        ap.sa.CompositeBand([S._single(ap, I, k) for k in (2, 3, 4)]).save(path)
        _BUILT["three"] = path
    return path


def _four(ap, I):
    path = os.path.join(I["work"], "ia_four.tif")
    if "four" not in _BUILT:
        ap.sa.CompositeBand([S._single(ap, I, k) for k in (1, 2, 3, 4)]).save(path)
        _BUILT["four"] = path
    return path


def _holes(ap, I, k):
    """Band k of the image with a NoData block that differs per k."""
    gx, gy = S._grid(I)
    a = S._band(I, k)
    a[5 * k:5 * k + 8, 10:20 + 3 * k] = -9999
    return S._save(ap, I, f"hole{k}", a, -9999)


def _u8(ap, I):
    """ints3 as 8-bit cells: Compute Accuracy For Pixel Classification takes no more than 16 bits per pixel."""
    gx, gy = S._grid(I)
    return S._save(ap, I, "u8", ((gx // 60 + gy // 100) % 4).astype("uint8"))


def _ints3(ap, I):
    gx, gy = S._grid(I)
    return S._save(ap, I, "ints3", ((gx // 60 + gy // 100) % 4).astype("int32"))


def _nc(I, name, var, when, cells, nodata=-9999.0, units=b"days since 2000-01-01 00:00:00", kind="f4"):
    from scipy.io import netcdf_file
    path = os.path.join(I["work"], name)
    ny, nx = cells.shape[1:]
    f = netcdf_file(path, "w", version=2)
    f.createDimension("time", len(when))
    f.createDimension("y", ny)
    f.createDimension("x", nx)
    t = f.createVariable("time", "f8", ("time",))
    t.units, t.calendar, t.axis, t.standard_name = units, b"standard", b"T", b"time"
    t[:] = when
    cell = 30.0 if nx == 20 else I["cell"]
    y = f.createVariable("y", "f8", ("y",))
    y.units, y.standard_name, y.axis = b"m", b"projection_y_coordinate", b"Y"
    y[:] = I["y0"] + ny * cell - cell / 2 - cell * np.arange(ny)
    x = f.createVariable("x", "f8", ("x",))
    x.units, x.standard_name, x.axis = b"m", b"projection_x_coordinate", b"X"
    x[:] = I["x0"] + cell / 2 + cell * np.arange(nx)
    v = f.createVariable(var, kind, ("time", "y", "x"))
    v._FillValue = np.array(nodata, dtype=kind)
    v[:] = cells.astype(kind)
    f.close()
    return path


def _series(ap, I):
    """'ndvi', 20 x 20 cells at 30 m, every 16 days from 2014-01-01 to 2022-12-31: a seasonal cycle, a per-row trend,
    a deterministic ripple for noise, and a drop of 0.25 from 2018-06-15 in the right half (ADR-236's series)."""
    if "series" in _BUILT:
        return _BUILT["series"]
    n = 20
    start = datetime.date(2014, 1, 1)
    days = []
    d = start
    while d < datetime.date(2023, 1, 1):
        days.append((d - datetime.date(2000, 1, 1)).days)
        d += datetime.timedelta(days=16)
    yy, xx = np.mgrid[0:n, 0:n]
    cells = np.empty((len(days), n, n))
    drop = (datetime.date(2018, 6, 15) - datetime.date(2000, 1, 1)).days
    for k, day in enumerate(days):
        dd = day - days[0]
        cells[k] = (0.5 + (yy - n / 2) * 2e-6 * dd + (0.15 + 0.01 * xx) * np.cos(2 * np.pi * dd / 365.25 - 0.3 - 0.02 * yy)
                    + 0.02 * np.sin(1.7 * k + 0.9 * xx + 1.3 * yy))
        if day >= drop:
            cells[k, :, n // 2:] -= 0.25
    path = _nc(I, "ia_series.nc", "ndvi", days, cells)
    if _pro(ap):
        crf = os.path.join(I["work"], "ia_series.crf")
        if not ap.Exists(crf):
            ap.management.CopyRaster(path, crf, process_as_multidimensional="ALL_SLICES")
        _BUILT["series_change"] = crf
    else:
        _BUILT["series_change"] = path
    _BUILT["series"] = path
    return path


def _md2(ap, I):
    """A second six-slice variable on _md's times: temp's cells squared over 100 plus a ripple."""
    if "md2" in _BUILT:
        return _BUILT["md2"]
    src = ap.Raster(S._md(ap, I), True)
    cells = np.asarray(src.read(), dtype="float64")
    if cells.ndim == 4:
        cells = cells[..., 0] if cells.shape[-1] == 1 else cells[:, 0]
    cells = np.where(cells <= -9000, np.nan, cells)
    gx, gy = S._grid(I)
    out = np.stack([c * c / 100 + np.sin(gx / 40 + i) for i, c in enumerate(cells)])
    out = np.where(np.isnan(out), -9999, out)
    _BUILT["md2"] = _nc(I, "ia_md2.nc", "rain", [14, 45, 195, 380, 411, 561], out)
    return _BUILT["md2"]


def _mdpca(ap, I):
    """'v', six slices of different patterns (no NoData): east, north, a ripple and mixes, so that several components
    carry variance."""
    if "mdpca" in _BUILT:
        return _BUILT["mdpca"]
    gx, gy = S._grid(I)
    e, n, r = gx / 600, gy / 600, np.sin(gx / 47) * np.cos(gy / 61)
    cells = np.stack([e, n, r, e + n, e - 0.5 * r, 2 * n + 0.3 * r + 0.1 * e * e])
    _BUILT["mdpca"] = _nc(I, "ia_mdpca.nc", "v", [14, 45, 195, 380, 411, 561], 10 * cells)
    return _BUILT["mdpca"]


def _classes_md(ap, I):
    """An integer variable 'lc', classes 1…4, over three yearly slices that move a little each year."""
    if "lcmd" in _BUILT:
        return _BUILT["lcmd"]
    gx, gy = S._grid(I)
    cells = np.stack([1 + ((gx + 40 * i) // 150 + gy // 300) % 4 for i in range(3)])
    _BUILT["lcmd"] = _nc(I, "ia_lc.nc", "lc", [0, 366, 731], cells, nodata=0, kind="i4")
    return _BUILT["lcmd"]


def _features(ap, I, name, shape, fields, rows):
    path = os.path.join(I["gdb"], name)
    if name in _BUILT:
        return path
    sr = ap.SpatialReference(I["srid"])
    ap.management.CreateFeatureclass(I["gdb"], name, shape, spatial_reference=sr)
    for field, kind in fields:
        ap.management.AddField(path, field, kind)
    with ap.da.InsertCursor(path, [f for f, _ in fields] + ["SHAPE@"]) as cursor:
        for *values, wkt in rows:
            cursor.insertRow((*values, ap.FromWKT(wkt, sr)))
    _BUILT[name] = path
    return path


def _box(I, a, b, c, d):
    x0, y0 = I["x0"], I["y0"]
    return (f"POLYGON (({x0 + a} {y0 + b}, {x0 + a} {y0 + d}, {x0 + c} {y0 + d}, {x0 + c} {y0 + b}, "
            f"{x0 + a} {y0 + b}))")


def _detections(ap, I):
    """Overlapping boxes as an object detector writes them: Confidence and Class."""
    rows = [(0.9, "car", _box(I, 100, 100, 200, 200)), (0.8, "car", _box(I, 120, 110, 210, 205)),
            (0.7, "car", _box(I, 190, 190, 260, 260)), (0.95, "tree", _box(I, 105, 105, 195, 195)),
            (0.6, "tree", _box(I, 400, 400, 480, 470)), (0.85, "tree", _box(I, 410, 395, 485, 475)),
            (0.5, "car", _box(I, 300, 50, 360, 90))]
    return _features(ap, I, "detections", "POLYGON", [("Confidence", "DOUBLE"), ("Class", "TEXT")], rows)


def _truth(ap, I):
    rows = [("car", _box(I, 105, 100, 205, 200)), ("tree", _box(I, 405, 400, 480, 475)),
            ("car", _box(I, 500, 500, 560, 560)), ("tree", _box(I, 100, 100, 200, 200))]
    return _features(ap, I, "truth", "POLYGON", [("Class", "TEXT")], rows)


# ---- local statistics over a list of rasters (raster functions) ----------------------------------------------------


for _name in ("Majority", "Minority", "Variety", "Median"):
    def _local_int(ap, I, O, _name=_name):
        getattr(ap.ia, _name)([S._ints(ap, I), S._ints2(ap, I), _ints3(ap, I)]).save(O("ints", "raster"))
        return getattr(ap.ia, _name)([_holes(ap, I, 1), _holes(ap, I, 2), _holes(ap, I, 3)], ignore_nodata=True)
    case("ia." + _name)(_local_int)

for _name in ("Max", "Mean", "Min", "Range", "StdDev", "Sum"):
    def _local(ap, I, O, _name=_name):
        rasters = [_holes(ap, I, 1), _holes(ap, I, 2), _holes(ap, I, 3)]
        getattr(ap.ia, _name)(rasters, ignore_nodata=True).save(O("data", "raster"))
        getattr(ap.ia, _name)([S._ints(ap, I), S._ints2(ap, I), _ints3(ap, I)]).save(O("ints", "raster"))
        return getattr(ap.ia, _name)(rasters)
    case("ia." + _name)(_local)


@case("ia.Percentile")
def _(ap, I, O):
    rasters = [_holes(ap, I, k) for k in (1, 2, 3, 4)]
    ap.ia.Percentile(rasters, 50, "AUTO_DETECT", ignore_nodata=True).save(O("p50", "raster"))
    ap.ia.Percentile([S._ints(ap, I), S._ints2(ap, I), _ints3(ap, I)], 75, "NEAREST").save(O("ints", "raster"))
    return ap.ia.Percentile(rasters, 90, "LINEAR", ignore_nodata=True)


@case("ia.Merge")
def _(ap, I, O):
    rasters = [_holes(ap, I, 1), _holes(ap, I, 2), _holes(ap, I, 3)]
    for kind in ("LAST", "MIN", "MAX", "MEAN", "SUM"):
        ap.ia.Merge(rasters, kind).save(O(kind.lower(), "raster"))
    return ap.ia.Merge(rasters, "FIRST")


@case("ia.Buffered")
def _(ap, I, O):
    return ap.ia.Buffered(ap.Raster(I["dem"]))


@case("ia.SR")
def _(ap, I, O):
    return ap.ia.SR(S._img(ap, I), 5, 4)


@case("ia.SubsetBands")
def _(ap, I, O):
    # bands is one space-delimited string (Pro refuses a list, TypeError).
    ap.ia.SubsetBands(S._img(ap, I), "BY_IDS", "6 2").save(O("ids", "raster"))
    ap.ia.SubsetBands(S._img(ap, I), "BY_IDS", "2-4 7").save(O("range", "raster"))
    return ap.ia.SubsetBands(S._img(ap, I), "BY_IDS", "1 3 5")


# ---- classification ----------------------------------------------------------------------------------------------


def _ecd(ap, I, kind):
    path = os.path.join(I["work"], f"ia_{kind}.ecd")
    if kind in _BUILT:
        return path
    three, train = _three(ap, I), S._training(ap, I)
    if kind == "ml":
        ap.ia.TrainMaximumLikelihoodClassifier(three, train, path)
    elif kind == "svm":
        ap.ia.TrainSupportVectorMachineClassifier(three, train, path, max_samples_per_class=500)
    elif kind == "knn":
        ap.ia.TrainKNearestNeighborClassifier(three, train, path, kNN=3)
    elif kind == "iso":
        ap.ia.TrainIsoClusterClassifier(three, 4, path, skip_factor=2)   # the default 10 leaves 36 samples (Pro: too few)
    _BUILT[kind] = path
    return path


@case("ia.TrainMaximumLikelihoodClassifier")
def _(ap, I, O):
    return ap.ia.ClassifyRaster(_three(ap, I), _ecd(ap, I, "ml"))


@case("ia.TrainSupportVectorMachineClassifier")
def _(ap, I, O):
    return ap.ia.ClassifyRaster(_three(ap, I), _ecd(ap, I, "svm"))


@case("ia.TrainKNearestNeighborClassifier")
def _(ap, I, O):
    return ap.ia.ClassifyRaster(_three(ap, I), _ecd(ap, I, "knn"))


@case("ia.TrainIsoClusterClassifier")
def _(ap, I, O):
    return ap.ia.ClassifyRaster(_three(ap, I), _ecd(ap, I, "iso"))


@case("ia.ClassifyRaster")
def _(ap, I, O):
    return ap.ia.ClassifyRaster(_three(ap, I), _ecd(ap, I, "ml"))


@case("ia.Classify")
def _(ap, I, O):
    return ap.ia.Classify(_three(ap, I), None, _ecd(ap, I, "ml"))


@case("ia.MLClassify")
def _(ap, I, O):
    return ap.ia.MLClassify(_three(ap, I), S._signature(ap, I, O))


@case("ia.Pansharpen")
def _(ap, I, O):
    # Pro sharpens only integer imagery here: a 32-bit floating-point pair answers "Not implemented" for every method
    # but Gram-Schmidt. So a 16-bit four-band image at 10 m and a 16-bit pan at 5 m, as a sensor delivers them.
    ms, pan = _pansharpen_inputs(ap, I)
    for kind in ("Brovey", "IHS", "SimpleMean", "Gram-Schmidt"):
        ap.ia.Pansharpen(pan, ms, None, True, [0.166, 0.167, 0.167, 0.5], kind).save(O(kind.lower(), "raster"))
    return ap.ia.Pansharpen(pan, ms, None, True, [0.166, 0.167, 0.167, 0.5], "ESRI")


def _pansharpen_inputs(ap, I):
    if "pan" in _BUILT:
        return _BUILT["pan"]
    sr = ap.SpatialReference(I["srid"])
    gx, gy = np.meshgrid(np.arange(60), np.arange(60))
    ms = np.stack([100 + 3 * gx + k * 20 + 10 * np.sin(gy / 5 + k) for k in range(4)]).astype("uint16")
    gx2, gy2 = np.meshgrid(np.arange(120), np.arange(120))
    pan = (150 + 1.5 * gx2 + 12 * np.sin(gy2 / 10)).astype("uint16")
    paths = (os.path.join(I["work"], "ia_ms16.tif"), os.path.join(I["work"], "ia_pan16.tif"))
    ap.env.outputCoordinateSystem = sr
    try:
        ap.NumPyArrayToRaster(ms, ap.Point(I["x0"], I["y0"]), 10, 10).save(paths[0])
        ap.NumPyArrayToRaster(pan, ap.Point(I["x0"], I["y0"]), 5, 5).save(paths[1])
    finally:
        ap.env.outputCoordinateSystem = None
    for path in paths:
        ap.management.DefineProjection(path, sr)
    _BUILT["pan"] = paths
    return paths


@case("ia.RegionGrow")
def _(ap, I, O):
    seeds = S._points(ap, I, "seeds", [(1, 20, 0.5, 105, 205), (2, 30, 0.2, 305, 305), (3, 10, 1.0, 455, 155)],
                      fields=(("fill", "LONG"), ("radius", "LONG"), ("sim", "DOUBLE")))
    return ap.ia.RegionGrow(I["dem"], seeds, "radius", "sim", "fill")


def _bands(ap, raster):
    """A raster's band names, multidimensional flag, and each band's mean, minimum, maximum and NoData count, read
    through the raster's own read() (a multidimensional trend raster is one slice of several bands)."""
    r = raster if hasattr(raster, "extent") else ap.Raster(raster, True)
    cells = np.asarray(r.read(), dtype="float64")
    nd = r.noDataValue
    if nd is not None:
        cells = np.where(cells == float(nd), np.nan, cells)
    cells = cells.reshape(-1, cells.shape[-1]) if cells.ndim == 2 else cells
    # read() answers (rows, columns, bands) or (slices, rows, columns, bands): bands last.
    bands = cells.reshape(-1, cells.shape[-1]).T
    return {"bandNames": [str(b) for b in getattr(r, "bandNames", [])],
            "bands": [[round(float(np.nanmean(b)), 4), round(float(np.nanmin(b)), 4), round(float(np.nanmax(b)), 4),
                       int(np.isnan(b).sum())] for b in bands]}


# ---- multidimensional ----------------------------------------------------------------------------------------------


def _mdout(ap, I, name):
    """A multidimensional output's path: Pro writes CRF, grapy netCDF."""
    return os.path.join(I["work"], "ia_" + name + (".crf" if _pro(ap) else ".nc"))


def _trend(ap, I):
    path = _mdout(ap, I, "trend")
    if "trend" not in _BUILT:
        ap.ia.GenerateTrendRaster(_series(ap, I), "StdTime", "ndvi", "LINEAR", None, "DATA", None, "YEARS",
                                  "RMSE", "R2", "SLOPEPVALUE").save(path)
        _BUILT["trend"] = path
    return path


def _harmonic(ap, I):
    path = _mdout(ap, I, "harm")
    if "harm" not in _BUILT:
        ap.ia.GenerateTrendRaster(_series(ap, I), "StdTime", "ndvi", "HARMONIC", 1, "DATA", 1, "YEARS",
                                  "RMSE").save(path)
        _BUILT["harm"] = path
    return path


@case("ia.GenerateTrendRaster")
def _(ap, I, O):
    poly = _mdout(ap, I, "poly")
    ap.ia.GenerateTrendRaster(_series(ap, I), "StdTime", "ndvi", "POLYNOMIAL", 2, "DATA", None, "YEARS",
                              "RMSE", "R2").save(poly)
    return {"linear": _bands(ap, _trend(ap, I)), "harmonic": _bands(ap, _harmonic(ap, I)), "poly": _bands(ap, poly)}


@case("ia.GenerateTrend")
def _(ap, I, O):
    harmonic = ap.ia.GenerateTrend(ap.Raster(_series(ap, I), True), "StdTime", "HARMONIC", 1, "YEARS", 1)
    linear = ap.ia.GenerateTrend(ap.Raster(_series(ap, I), True), "StdTime", "LINEAR", rmse=True, r2=True)
    return {"linear": _bands(ap, linear), "harmonic": _bands(ap, harmonic)}


@case("ia.PredictUsingTrendRaster")
def _(ap, I, O):
    return S._mdsummary(ap, ap.ia.PredictUsingTrendRaster(_harmonic(ap, I), "ndvi", "BY_VALUE",
                                                          ["2023-03-01T00:00:00", "2023-08-01T00:00:00"]))


@case("ia.PredictUsingTrend")
def _(ap, I, O):
    return S._mdsummary(ap, ap.ia.PredictUsingTrend(ap.Raster(_trend(ap, I)), "BY_VALUE",
                                                    ["2023-03-01T00:00:00", "2024-03-01T00:00:00"]))


@case("ia.TrendToRGB")
def _(ap, I, O):
    # Pro saves this raster only as CRF ("Unspecified error" for a GeoTIFF).
    out = {}
    for name, source, model in (("linear", _trend, "LINEAR"), ("harmonic", _harmonic, "HARMONIC")):
        path = os.path.join(I["work"], f"ia_rgb_{name}" + (".crf" if _pro(ap) else ".tif"))
        ap.ia.TrendToRGB(ap.Raster(source(ap, I), True), model).save(path)
        out[name] = _bands(ap, ap.Raster(path))
    return out


@case("ia.FindArgumentStatistics")
def _(ap, I, O):
    # Pro saves this output only as CRF (a GeoTIFF is "Unspecified error"); grapy as netCDF.
    md = S._md(ap, I)
    out = {}
    for name, args in (("argmax", ("ARGUMENT_MAX",)), ("argmin", ("ARGUMENT_MIN",)),
                       ("duration", ("DURATION", 20, 40, None, "DATA"))):
        path = _mdout(ap, I, "fas_" + name)
        ap.ia.FindArgumentStatistics(md, "StdTime", "ALL", None, "temp", *args).save(path)
        out[name] = _bands(ap, path)
    return out


@case("ia.MultidimensionalPrincipalComponents")
def _(ap, I, O):
    # Pro writes out_pc as a CRF whatever its name; grapy writes the GeoTIFF it is given.
    pc = os.path.join(I["work"], "ia_pc.crf" if _pro(ap) else "ia_pc.tif")
    ap.ia.MultidimensionalPrincipalComponents(_mdpca(ap, I), "DIMENSION_REDUCTION", "StdTime", pc, O("loadings"),
                                              O("eigen"), "v", 3)
    # Components are defined up to their sign: each band is compared as |cells|.
    r = ap.Raster(pc)
    cells = np.asarray(ap.RasterToNumPyArray(r), dtype="float64")
    if r.noDataValue is not None:
        cells = np.where(cells == float(r.noDataValue), np.nan, cells)
    cells = np.abs(cells).reshape(-1, *cells.shape[-2:])
    return {"bands": int(r.bandCount), "mean": [round(float(np.nanmean(b)), 4) for b in cells],
            "max": [round(float(np.nanmax(b)), 4) for b in cells], "nodata": [int(np.isnan(b).sum()) for b in cells]}


@case("ia.MultidimensionalRasterCorrelation")
def _(ap, I, O):
    return ap.ia.MultidimensionalRasterCorrelation(S._md(ap, I), _md2(ap, I), "StdTime", "temp", "StdTime", "rain",
                                                   "PEARSON")


@case("ia.InterpolateFromSpatiotemporalPoints")
def _(ap, I, O):
    path = os.path.join(I["gdb"], "stpoints")
    if "stpoints" not in _BUILT:
        sr = ap.SpatialReference(I["srid"])
        ap.management.CreateFeatureclass(I["gdb"], "stpoints", "POINT", spatial_reference=sr)
        ap.management.AddField(path, "temp", "DOUBLE")
        ap.management.AddField(path, "obs", "DATE")
        import fixtures
        r = fixtures._lcg(99)
        with ap.da.InsertCursor(path, ["temp", "obs", "SHAPE@XY"]) as cursor:
            for day in range(3):
                for _ in range(12):
                    x, y = I["x0"] + 600 * next(r), I["y0"] + 600 * next(r)
                    cursor.insertRow((round(10 + 20 * next(r), 2), datetime.datetime(2024, 5, 1 + day, 12),
                                      (x, y)))
        _BUILT["stpoints"] = path
    out = {}
    for method in ("NEAREST_NEIGHBOR", "IDW", "MEAN"):
        r = ap.ia.InterpolateFromSpatiotemporalPoints(path, "temp", "obs", "DAILY", 50, method)
        out[method] = S._mdsummary(ap, r)
        e = r.extent
        out[method]["extent"] = [round(e.XMin - I["x0"], 3), round(e.YMin - I["y0"], 3), round(e.XMax - I["x0"], 3),
                                 round(e.YMax - I["y0"], 3)]
        cells = np.asarray(r.read(), dtype="float64")[0, ..., 0]
        nd = r.noDataValue
        out[method]["firstSlice"] = [[None if (nd is not None and v == float(nd)) or np.isnan(v) else round(float(v), 3)
                                      for v in row] for row in cells]
    return out


@case("ia.SummarizeCategoricalRaster")
def _(ap, I, O):
    # Pro fails on the netCDF itself (ERROR 999999) and summarises a CRF copy with an attribute table; grapy reads the
    # netCDF (it refuses CRF).
    src = _classes_md(ap, I)
    if _pro(ap):
        crf = os.path.join(I["work"], "ia_lc.crf")
        if not ap.Exists(crf):
            ap.management.CopyRaster(src, crf, process_as_multidimensional="ALL_SLICES")
            ap.management.BuildRasterAttributeTable(crf)
        src = crf
    out = os.path.join(I["work"], "ia_summary.csv")
    ap.ia.SummarizeCategoricalRaster(src, out)
    rows = []
    for line in open(out, encoding="utf-8-sig"):
        cells = []
        for cell in line.strip().split(","):
            try:
                cells.append(round(float(cell), 3))
            except ValueError:
                cells.append(cell.strip())
        rows.append(cells)
    return rows


# ---- change ---------------------------------------------------------------------------------------------------------


@case("ia.ComputeChangeRaster")
def _(ap, I, O):
    ap.ia.ComputeChangeRaster(S._ints2(ap, I), _ints3(ap, I), "CATEGORICAL_DIFFERENCE").save(O("categorical", "raster"))
    ap.ia.ComputeChangeRaster(I["dem"], S._ndem(ap, I), "RELATIVE_DIFFERENCE").save(O("relative", "raster"))
    return ap.ia.ComputeChangeRaster(I["dem"], S._ndem(ap, I), "DIFFERENCE")


@case("ia.ComputeChange")
def _(ap, I, O):
    ap.ia.ComputeChange(I["dem"], S._ndem(ap, I), "RELATIVE_DIFFERENCE").save(O("relative", "raster"))
    return ap.ia.ComputeChange(I["dem"], S._ndem(ap, I), "DIFFERENCE")


def _ccdc(ap, I):
    path = os.path.join(I["work"], "ia_ccdc.crf" if _pro(ap) else "ia_ccdc.nc")
    if "ccdc" not in _BUILT:
        _series(ap, I)
        ap.ia.AnalyzeChangesUsingCCDC(_BUILT["series_change"], None, None, 0.99, 6, 1).save(path)
        _BUILT["ccdc"] = path
    return path


def _landtrendr(ap, I):
    path = os.path.join(I["work"], "ia_lt.crf" if _pro(ap) else "ia_lt.nc")
    if "lt" not in _BUILT:
        _series(ap, I)
        ap.ia.AnalyzeChangesUsingLandTrendr(_BUILT["series_change"]).save(path)
        _BUILT["lt"] = path
    return path


def _detected(ap, I, source, name, *args):
    """A Detect Change raster's cells, rounded (20 x 20): Pro saves it only as CRF ("Unspecified error" for a
    GeoTIFF), grapy as GeoTIFF."""
    path = os.path.join(I["work"], "ia_dc_" + name + (".crf" if _pro(ap) else ".tif"))
    ap.ia.DetectChangeUsingChangeAnalysisRaster(source, *args).save(path)
    r = ap.Raster(path)
    cells = np.asarray(ap.RasterToNumPyArray(r), dtype="float64")
    if r.noDataValue is not None:
        cells = np.where(cells == float(r.noDataValue), np.nan, cells)
    return {"bands": int(r.bandCount), "cells": [[None if np.isnan(v) else round(float(v), 3) for v in row]
                                                for row in cells.reshape(-1, cells.shape[-1])]}


@case("ia.AnalyzeChangesUsingCCDC")
def _(ap, I, O):
    return {"largest": _detected(ap, I, _ccdc(ap, I), "ccdc_largest", "TIME_OF_LARGEST_CHANGE"),
            "latest": _detected(ap, I, _ccdc(ap, I), "ccdc_latest", "TIME_OF_LATEST_CHANGE")}


@case("ia.AnalyzeChangesUsingLandTrendr")
def _(ap, I, O):
    return {"largest": _detected(ap, I, _landtrendr(ap, I), "lt_largest", "TIME_OF_LARGEST_CHANGE"),
            "latest": _detected(ap, I, _landtrendr(ap, I), "lt_latest", "TIME_OF_LATEST_CHANGE")}


@case("ia.DetectChangeUsingChangeAnalysisRaster")
def _(ap, I, O):
    return {"count": _detected(ap, I, _ccdc(ap, I), "count", "NUM_OF_CHANGES", 3),
            "earliest": _detected(ap, I, _ccdc(ap, I), "earliest", "TIME_OF_EARLIEST_CHANGE")}


@case("ia.DetectChangeUsingChangeAnalysis")
def _(ap, I, O):
    return ap.ia.DetectChangeUsingChangeAnalysis(ap.Raster(_ccdc(ap, I), True), "TIME_OF_LATEST_CHANGE", 1)


# ---- collections, arrays, pixel blocks ------------------------------------------------------------------------------


@case("ia.RasterCollection")
def _(ap, I, O):
    rc = ap.ia.RasterCollection([S._single(ap, I, k) for k in (2, 3, 4)], {"name": ["b2", "b3", "b4"],
                                                                           "rank": [3, 1, 2]})
    rc.mean().save(O("mean", "raster"))
    rc.max().save(O("max", "raster"))
    ranked = rc.sort("rank")
    return {"count": rc.count, "names": [i["name"] for i in ranked],
            "filtered": rc.filterByAttribute("rank", "EQUALS", 1).count if hasattr(rc, "filterByAttribute") else None}


@case("ia.RasterToXarray")
def _(ap, I, O):
    ds = ap.ia.RasterToXarray(ap.Raster(S._md(ap, I), True))
    out = {"type": type(ds).__name__, "vars": list(ds.data_vars)}
    for v in ds.data_vars:
        a = np.asarray(ds[v].values, dtype="float64")
        valid = a[np.isfinite(a) & (a > -9000)]                 # Pro leaves -9999 in the array, grapy NaN
        out[v] = {"dims": list(ds[v].dims), "shape": list(a.shape), "sum": round(float(valid.sum()), 1),
                  "first": round(float(a.reshape(-1)[0]), 4), "nodata": int(a.size - valid.size)}
    for c in ds[list(ds.data_vars)[0]].dims:
        values = ds[c].values
        out["coord_" + c] = [str(values[0])[:19], str(values[-1])[:19]]
    return out


@case("ia.XarrayToRaster")
def _(ap, I, O):
    return S._mdsummary(ap, ap.ia.XarrayToRaster(ap.ia.RasterToXarray(ap.Raster(S._md(ap, I), True))))


@case("ia.PixelBlockCollection")
def _(ap, I, O):
    # A block past the raster's edge is padded (Pro: with the pixel type's lowest value); sums are of the cells inside.
    pbc = ap.ia.PixelBlockCollection(ap.Raster(I["dem"]), (25, 25), (25, 25))
    ny, nx = pbc.size
    blocks = []
    for row in range(ny):
        for col in range(nx):
            a = np.asarray(pbc[row, col].getData(), dtype="float64")
            inside = a[:max(0, 60 - 25 * row), :max(0, 60 - 25 * col)]
            blocks.append([list(a.shape), round(float(inside.sum()), 2)])
    two = ap.ia.PixelBlockCollection([ap.Raster(I["dem"]), ap.Raster(S._ints(ap, I))], (40, 40))
    pair = two[1, 0]
    n = 0
    pbc.reset()
    while True:
        try:
            next(pbc)
            n += 1
        except StopIteration:
            break
    return {"size": list(pbc.size), "blocks": blocks, "pair": [list(np.asarray(b.getData()).shape) for b in pair],
            "pairSums": [round(float(np.asarray(b.getData(), dtype="float64")[:20].sum()), 2) for b in pair],
            "next": n}


@case("ia.PixelBlock")
def _(ap, I, O):
    pbc = ap.ia.PixelBlockCollection(ap.Raster(I["dem"]), (25, 25), (25, 25))
    block = pbc[0, 1]
    data = np.asarray(block.getData(), dtype="float64")
    made = ap.ia.PixelBlock(np.zeros((2, 3, 1)), ap.Extent(0, 0, 3, 2))
    return {"type": type(block).__name__, "shape": list(data.shape), "sum": round(float(data.sum()), 2),
            "cell": [round(float(c), 3) for c in block.cell_size], "tlc": [round(block.tlc.X, 3), round(block.tlc.Y, 3)],
            "made": list(np.asarray(made.getData()).shape)}


# ---- deep-learning post-processing (no model) -----------------------------------------------------------------------


@case("ia.NonMaximumSuppression")
def _(ap, I, O):
    ap.ia.NonMaximumSuppression(_detections(ap, I), "Confidence", O("byclass"), "Class", 0.3)
    return ap.ia.NonMaximumSuppression(_detections(ap, I), "Confidence", O("out"), None, 0.3)


@case("ia.ComputeAccuracyForObjectDetection")
def _(ap, I, O):
    ap.ia.ComputeAccuracyForObjectDetection(_detections(ap, I), _truth(ap, I), O("accuracy"), None, "Class", "Class",
                                            0.5)
    return None


@case("ia.ComputeAccuracyForPixelClassification")
def _(ap, I, O):
    # A perfect classification with equalized sampling: the matrix's counts do not depend on where the points fall.
    return ap.ia.ComputeAccuracyForPixelClassification(_u8(ap, I), _u8(ap, I), O("matrix"), 200,
                                                       "EQUALIZED_STRATIFIED_RANDOM")


# ---- not comparable here: each case says why, and both sides record it ----------------------------------------------


def _not_comparable(reason):
    def run(ap, I, O):
        raise NotImplementedError(reason)
    return run


_NO_TORCH = ("needs a trained deep learning model and a deep learning framework; Pro 3.7 here has no PyTorch or "
             "TensorFlow in arcgispro-py3 (the Deep Learning Libraries installer is not installed), and Pro reads only "
             "Esri model packages")
for _name in ("ClassifyPixelsUsingDeepLearning", "ClassifyObjectsUsingDeepLearning", "DetectObjectsUsingDeepLearning",
              "DetectChangeUsingDeepLearning", "TranslatePixelsUsingDeepLearning", "TrainDeepLearningModel",
              "TrainUsingAutoDL"):
    case("ia." + _name)(_not_comparable(_NO_TORCH))
case("ia.ExtractFeaturesUsingAIModels")(_not_comparable(
    "its pretrained models are Esri's from ArcGIS Living Atlas (a portal sign-in and the network) and it needs the "
    "deep learning framework Pro lacks here"))
case("ia.TransformImageShapes")(_not_comparable(
    "its input is the pixel-space detections (iShape, ImageID) Detect Objects Using Deep Learning writes, which Pro "
    "cannot make here (no framework), over a Frame Camera mosaic dataset"))
_NO_SLC = ("needs a Sentinel-1 SLC product (bursts, complex data, or a reference and secondary pair); none can be had "
           "without an account (ADR-237 condition 3) and this machine holds only one IW GRD scene")
for _name in ("Deburst", "ComputeCoherence", "GenerateInterferogram", "ApplyComplexDataFilter", "UnwrapPhase",
              "ApplyCoregistration", "GenerateMultitemporalCoherence"):
    case("ia." + _name)(_not_comparable(_NO_SLC))
case("ia.ApplyOrbitCorrection")(_not_comparable(
    "needs a precise orbit file for the scene, which comes from the Copernicus Data Space Ecosystem (network and an "
    "account); Pro's own download is DownloadOrbitFile"))


# ---- motion imagery metadata, mensuration -----------------------------------------------------------------------------


@case("ia.ConvertVideoMetadata")
def _(ap, I, O):
    src = os.path.join(I["work"], "ia_video.csv")
    with open(src, "w", newline="") as f:
        f.write("when,lat,lon,alt_ft,hdg,note\n")
        for k in range(4):
            f.write(f"2024-05-01T12:00:{k:02d}.000,{41 + k * 0.001:.4f},{29 + k * 0.002:.4f},{1000 + 10 * k},{90 + k},x{k}\n")
    out = os.path.join(I["work"], "ia_video_out.csv")
    mapping = os.path.join(I["work"], "ia_video_map.csv")
    # Latitude and longitude take XY, DD, DDM or DMS (Pro: "Degrees" is ERROR 000800).
    # Pro needs the timestamp matched (ERROR 002659 without it).
    for old in (out, mapping):
        if os.path.exists(old):
            os.remove(old)
    ap.ia.ConvertVideoMetadata(src, out, mapping, [["Precision Time Stamp", "when", "YYYY-MM-DDTHH:MM:SS.SSS"],
                                                   ["Sensor Latitude", "lat", "DD"],
                                                   ["Sensor Longitude", "lon", "DD"],
                                                   ["Sensor True Altitude", "alt_ft", "Feet"],
                                                   ["Platform Heading Angle", "hdg", "Degrees"]])

    def rows(path):
        out_rows = []
        for line in open(path, encoding="utf-8-sig"):
            cells = []
            for cell in line.strip().split(","):
                try:
                    cells.append(round(float(cell), 4))
                except ValueError:
                    cells.append(cell.strip())
            out_rows.append(cells)
        return out_rows
    return {"out": rows(out), "mapping": rows(mapping) if os.path.exists(mapping) else None}


def _rpc_image(I):
    """A 100 × 100 image with an RPC00B model in its TIFF tag (50844): row = −latitude and column = longitude, in
    normalised terms, around (41° N, 29° E), 0.01° a side, with no height term."""
    from PIL import Image, TiffImagePlugin
    path = os.path.join(I["work"], "ia_rpc.tif")
    if "rpc" in _BUILT:
        return path
    image = Image.fromarray((np.add.outer(np.arange(100), np.arange(100)) % 256).astype("uint8"), "L")
    line_num, line_den, samp_num, samp_den = [0.0] * 20, [1.0] + [0.0] * 19, [0.0] * 20, [1.0] + [0.0] * 19
    line_num[2] = -1.0           # P, latitude
    samp_num[1] = 1.0            # L, longitude
    rpc = [0.0, 0.0, 50.0, 50.0, 41.0, 29.0, 100.0, 50.0, 50.0, 0.005, 0.005, 500.0] + line_num + line_den + \
        samp_num + samp_den
    info = TiffImagePlugin.ImageFileDirectory_v2()
    info[50844] = tuple(rpc)
    info.tagtype[50844] = 12
    image.save(path, tiffinfo=info)
    # A world file agreeing with the model (0.0001° a pixel from 28.995° E, 41.005° N): grapy opens only north-up grids.
    with open(path[:-4] + ".tfw", "w") as f:
        f.write("\n".join(["0.0001", "0", "0", "-0.0001", "28.99505", "41.00495"]) + "\n")
    _BUILT["rpc"] = path
    return path


@case("ia.Mensuration")
def _(ap, I, O):
    m = ap.ia.Mensuration(ap.Raster(_rpc_image(I)))
    p = m.point((50, 50))
    corner = m.point((10, 10))
    d = m.distance((10, 10), (90, 10))
    a = m.area([(10, 10), (90, 10), (90, 90), (10, 90)])
    closed = m.area([(10, 10), (90, 10), (90, 90), (10, 90), (10, 10)])
    north = m.distance((10, 10), (10, 90))
    return {"point": [round(p.lon, 6), round(p.lat, 6), round(p.elev, 2)],
            "corner": [round(corner.lon, 6), round(corner.lat, 6)],
            "distance": [round(d.distance, 2), round(d.azimuth, 3)], "area": [round(a.area, 0), round(a.perimeter, 2)],
            "closed": [round(closed.area, 0), round(closed.perimeter, 2)],
            "north": [round(north.distance, 2), round(north.azimuth, 3)],
            "sensor": [p.sensor_name, d.sensor_name, a.sensor_name]}
_GRD = ("not run in this sweep: Pro takes it on a Sentinel-1 product, and the only one here is a full IW GRD scene "
        "(1.4 GB, 26,325 x 21,331), each tool a whole-scene run on both sides with disk C full on 2026-11-08; "
        "ADR-237 condition 3 compared RemoveThermalNoise, ApplyRadiometricCalibration and "
        "ApplyGeometricTerrainCorrection on that scene with Pro 3.7 on 2026-10-31")
for _name in ("RemoveThermalNoise", "ApplyRadiometricCalibration", "ApplyGeometricTerrainCorrection",
              "ApplyRadiometricTerrainFlattening", "GenerateRadiometricTerrainCorrectedData", "ConvertSARUnits",
              "Multilook", "Despeckle", "ComputeSARIndices", "GenerateMaskedSARRaster", "DetectDarkOceanAreas",
              "DetectBrightOceanObjects", "ExtractOceanWinds", "RadarCalibration", "S1RadiometricCalibration",
              "S1ThermalNoiseRemoval"):
    case("ia." + _name)(_not_comparable(_GRD))
