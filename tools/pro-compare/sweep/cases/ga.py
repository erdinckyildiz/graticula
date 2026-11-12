"""arcpy.ga against ArcGIS Pro 3.7: one case for each of the 26 names grapy builds.

A geostatistical layer is written to a ``.lyrx`` path in the case's work folder: Pro writes its layer file, grapy its
own JSON under that name (ADR-233 §5.2), and each side reads back what it wrote. The layers are IDW layers with a
circular one-sector neighbourhood — grapy refuses IDW's estimated power and default neighbourhood, and Pro makes a
kriging layer only from a model XML whose form is not published, so kriging is not on a shared input here. 3D points
for IDW3D and NearestNeighbor3D are the fixture's points with z = val. A netCDF output is summarised (variables, their
shapes, the coordinate axes' ends, the values' count, minimum, maximum and mean): Pro writes netCDF-4 and grapy
netCDF-3, so neither file is compared byte by byte.
"""
import os

CASES = {}


def case(name):
    def register(function):
        CASES[name] = function
        return function
    return register


def _r(v, n=6):
    return None if v is None else round(float(v), n)


def _nbr(ap):
    return ap.ga.SearchNeighborhoodStandardCircular(250, 0, 10, 3, "ONE_SECTOR")


def _layer(ap, O, name="idw", power=2, I=None):
    folder = O("ga_" + name, "folder")
    path = os.path.join(folder, name + ".lyrx")
    ap.ga.IDW(I["points"], "val", path, power=power, search_neighborhood=_nbr(ap))
    return path


def _points3d(ap, I):
    path = os.path.join(I["gdb"], "points3d")
    if ap.Exists(path):
        return path
    sr = ap.SpatialReference(I["srid"])
    ap.management.CreateFeatureclass(I["gdb"], "points3d", "POINT", has_z="ENABLED", spatial_reference=sr)
    for field, kind in (("val", "DOUBLE"), ("cat", "TEXT")):
        ap.management.AddField(path, field, kind)
    rows = []
    with ap.da.SearchCursor(I["points"], ["val", "cat", "SHAPE@XY"]) as cursor:
        for val, cat, (x, y) in cursor:
            rows.append((val, cat, x, y))
    with ap.da.InsertCursor(path, ["val", "cat", "SHAPE@"]) as cursor:
        for val, cat, x, y in rows:
            cursor.insertRow((val, cat, ap.PointGeometry(ap.Point(x, y, round(val, 3)), sr, True)))
    return path


def _netcdf(path):
    """What a netCDF file holds: each variable's dimensions and shape; for the coordinate axes their first and last
    values, for the others the count of values, their minimum, maximum and mean."""
    import numpy as np
    try:
        import netCDF4
        data = netCDF4.Dataset(path)
        variables = {k: (tuple(v.dimensions), np.ma.filled(v[:].astype("float64"), np.nan)) for k, v in
                     data.variables.items()}
        data.close()
    except ImportError:
        from scipy.io import netcdf_file
        data = netcdf_file(path, "r", mmap=False)
        variables = {}
        for k, v in data.variables.items():
            a = np.array(v[:], dtype="float64")
            fill = getattr(v, "_FillValue", None)
            if fill is not None and not np.isnan(fill):
                a = np.where(a == fill, np.nan, a)
            variables[k] = (tuple(v.dimensions), a)
        data.close()
    out = {}
    for k, (dims, a) in sorted(variables.items()):
        if dims == (k,):
            out[k] = {"n": int(a.size), "first": _r(a[0], 3), "last": _r(a[-1], 3)}
        elif a.ndim >= 2:
            known = a[~np.isnan(a)]
            out[k] = {"shape": list(a.shape), "count": int(known.size), "min": _r(known.min(), 4) if known.size else None,
                      "max": _r(known.max(), 4) if known.size else None,
                      "mean": _r(known.mean(), 4) if known.size else None}
    return out


# ---- the interpolators


@case("ga.IDW")
def _(ap, I, O):
    folder = O("ga", "folder")
    return ap.ga.IDW(I["points"], "val", os.path.join(folder, "idw.lyrx"), O("out", "raster"), 10, 2, _nbr(ap))


@case("ga.GlobalPolynomialInterpolation")
def _(ap, I, O):
    folder = O("ga", "folder")
    return ap.ga.GlobalPolynomialInterpolation(I["points"], "val", os.path.join(folder, "gpi.lyrx"),
                                               O("out", "raster"), 10, 2)


@case("ga.LocalPolynomialInterpolation")
def _(ap, I, O):
    folder = O("ga", "folder")
    return ap.ga.LocalPolynomialInterpolation(I["points"], "val", os.path.join(folder, "lpi.lyrx"), O("out", "raster"),
                                              10, 1, ap.ga.SearchNeighborhoodStandardCircular(300, 0, 15, 10,
                                                                                              "ONE_SECTOR"),
                                              "EPANECHNIKOV", 300, "NO_USE_CONDITION_NUMBER", None, None, "PREDICTION")


@case("ga.RadialBasisFunctions")
def _(ap, I, O):
    folder = O("ga", "folder")
    return ap.ga.RadialBasisFunctions(I["points"], "val", os.path.join(folder, "rbf.lyrx"), O("out", "raster"), 10,
                                      _nbr(ap), "MULTIQUADRIC_FUNCTION", 20)


@case("ga.IDW3D")
def _(ap, I, O):
    nc = os.path.join(O("nc", "folder"), "idw3d.nc")
    result = ap.ga.IDW3D(_points3d(ap, I), "val", nc, 2, 10, O("cv"), "100 Meters", "100 Meters", "20 Meters", None,
                         None, None, ap.ga.SearchNeighborhoodStandard3D(300, 10, 3, "ONE_SECTOR"))
    derived = [_r(result.getOutput(i), 4) for i in range(result.outputCount)
               if str(result.getOutput(i)).replace(".", "").replace("-", "").isdigit()]
    return {"netcdf": _netcdf(nc), "derived": derived}


@case("ga.NearestNeighbor3D")
def _(ap, I, O):
    nc = os.path.join(O("nc", "folder"), "nn3d.nc")
    ap.ga.NearestNeighbor3D(_points3d(ap, I), "cat", nc, "100 Meters", "100 Meters", "20 Meters")
    return _netcdf(nc)


# ---- reading a layer


@case("ga.GALayerToGrid")
def _(ap, I, O):
    return ap.ga.GALayerToGrid(_layer(ap, O, I=I), O("out", "raster"), 10, 1, 1)


@case("ga.GALayerToRasters")
def _(ap, I, O):
    return ap.ga.GALayerToRasters(_layer(ap, O, I=I), O("out", "raster"), "PREDICTION", None, 10, 1, 1)


@case("ga.GALayerToPoints")
def _(ap, I, O):
    layer = _layer(ap, O, I=I)
    ap.ga.GALayerToPoints(layer, I["points"], None, O("fid"), "FID_ONLY")
    return ap.ga.GALayerToPoints(layer, I["points"], "val", O("out"), "ALL")


@case("ga.GALayerToContour")
def _(ap, I, O):
    return ap.ga.GALayerToContour(_layer(ap, O, I=I), "CONTOUR", O("out"), "DRAFT", "MANUAL", None, [40, 50, 60])


@case("ga.GACalculateZValue")
def _(ap, I, O):
    layer = _layer(ap, O, I=I)
    return [_r(ap.ga.GACalculateZValue(layer, f"{I['x0'] + x} {I['y0'] + y}")[0], 6)
            for x, y in ((300, 300), (100, 450), (510, 90))]


@case("ga.CrossValidation")
def _(ap, I, O):
    cv = ap.ga.CrossValidation(_layer(ap, O, I=I), O("out"))
    return {k: _r(getattr(cv, k), 6) for k in ("count", "meanError", "rootMeanSquare")}


@case("ga.CrossValidationResult")
def _(ap, I, O):
    cv = ap.ga.CrossValidation(_layer(ap, O, I=I))
    return {"type": type(cv).__name__, **{k: (None if getattr(cv, k, None) is None else _r(getattr(cv, k), 6))
                                          for k in ("count", "meanError", "rootMeanSquare", "meanStandardized",
                                                    "averageStandard", "rootMeanSquareStandardized")}}


@case("ga.GeostatisticalDatasets")
def _(ap, I, O):
    g = ap.ga.GeostatisticalDatasets(_layer(ap, O, I=I))
    return {"dataset1": os.path.basename(str(g.dataset1)), "dataset1Field": g.dataset1Field}


@case("ga.GACreateGeostatisticalLayer")
def _(ap, I, O):
    layer = _layer(ap, O, I=I)
    data = ap.ga.GeostatisticalDatasets(layer)
    data.dataset1 = I["points"]
    data.dataset1Field = "pid"
    out = os.path.join(O("ga2", "folder"), "pid.lyrx")
    ap.ga.GACreateGeostatisticalLayer(layer, data, out)
    return ap.ga.GALayerToGrid(out, O("out", "raster"), 10, 1, 1)


_POWER = "/model[@name = 'IDW']/value[@name = 'Power']"


@case("ga.GAGetModelParameter")
def _(ap, I, O):
    return ap.ga.GAGetModelParameter(_layer(ap, O, I=I, power=3), _POWER)[0]


@case("ga.GASetModelParameter")
def _(ap, I, O):
    layer = _layer(ap, O, I=I)
    model = os.path.join(O("model", "folder"), "idw_power1.xml")
    ap.ga.GASetModelParameter(layer, _POWER, "1", model)
    out = os.path.join(O("ga2", "folder"), "p1.lyrx")
    data = ap.ga.GeostatisticalDatasets(layer)
    ap.ga.GACreateGeostatisticalLayer(model, data, out)
    return ap.ga.GALayerToGrid(out, O("out", "raster"), 10, 1, 1)


@case("ga.CompareGeostatisticalLayers")
def _(ap, I, O):
    layers = [_layer(ap, O, name=f"idw{p}", power=p, I=I) for p in (1, 2, 3)]
    ap.ga.CompareGeostatisticalLayers(layers, O("single"), None, "SINGLE", "ACCURACY")
    ap.ga.CompareGeostatisticalLayers(layers, O("average"), None, "AVERAGE_RANK", None, None,
                                      [["ACCURACY", 1], ["BIAS", 2], ["WORST_CASE", 1]])
    # The layers' paths differ by side: keep the file name of whatever field holds one.
    for table in (O("single"), O("average")):
        fields = [f.name for f in ap.ListFields(table) if f.type == "String"]
        with ap.da.UpdateCursor(table, fields) as rows:
            for row in rows:
                rows.updateRow([None if v is None else os.path.basename(str(v).replace("\\", "/")) for v in row])
    return None


@case("ga.GAMovingWindowKriging")
def _(ap, I, O):
    # A kriging model source is required (the page); an IDW layer is the only one both sides can make here.
    return ap.ga.GAMovingWindowKriging(_layer(ap, O, I=I), f"{I['points']} X=Shape Y=Shape F1=val", I["points"], 12,
                                       O("out"))


@case("ga.GANeighborhoodSelection")
def _(ap, I, O):
    layer = "nbr_sel"
    ap.ga.GANeighborhoodSelection(I["points"], layer, f"{I['x0'] + 300} {I['y0'] + 300}", 10, 3, 150, 200, 30,
                                  "ONE_SECTOR")
    return ap.management.CopyFeatures(layer, O("out"))


@case("ga.SubsetFeatures")
def _(ap, I, O):
    ap.ga.SubsetFeatures(I["points"], O("train"), O("test"), 100, "PERCENTAGE_OF_INPUT")
    return None


# ---- the search neighbourhood classes


def _attrs(obj, names):
    return {"str": str(obj), "nbrType": getattr(obj, "nbrType", None),
            **{n: getattr(obj, n, None) for n in names}}


@case("ga.SearchNeighborhoodStandard")
def _(ap, I, O):
    return _attrs(ap.ga.SearchNeighborhoodStandard(300, 200, 45, 15, 5, "FOUR_SECTORS"),
                  ("majorSemiaxis", "minorSemiaxis", "angle", "nbrMax", "nbrMin", "sectorType"))


@case("ga.SearchNeighborhoodSmooth")
def _(ap, I, O):
    return _attrs(ap.ga.SearchNeighborhoodSmooth(300, 200, 45, 0.3),
                  ("majorSemiaxis", "minorSemiaxis", "angle", "smoothFactor"))


@case("ga.SearchNeighborhoodStandardCircular")
def _(ap, I, O):
    return _attrs(ap.ga.SearchNeighborhoodStandardCircular(250, 10, 12, 4, "EIGHT_SECTORS"),
                  ("radius", "angle", "nbrMax", "nbrMin", "sectorType"))


@case("ga.SearchNeighborhoodSmoothCircular")
def _(ap, I, O):
    return _attrs(ap.ga.SearchNeighborhoodSmoothCircular(250, 0.6), ("radius", "smoothFactor"))


@case("ga.SearchNeighborhoodStandard3D")
def _(ap, I, O):
    return _attrs(ap.ga.SearchNeighborhoodStandard3D(300, 10, 3, "FOUR_SECTORS"),
                  ("radius", "nbrMax", "nbrMin", "sectorType"))
