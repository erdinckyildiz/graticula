"""arcpy.ddd — 3D Analyst's tools grapy builds, by their documented names and parameters (ADR-224).

3D Analyst's raster tools are geoprocessing tools: each writes its output to a path and answers a Result, where
Spatial Analyst's answer a Raster. Where a ddd tool is the same computation as an ``arcpy.sa`` function, it calls that
function and saves what it answers; the parameter order and defaults are the ddd page's.

Not built, each refused by name with its reason in ``__getattr__`` below:

- Point cloud deep learning tools, and the multipatch and mesh tools whose method the pages leave open (ADR-238). TINs
  and LAS datasets are held since ADR-229 and terrain datasets since ADR-234; the tools on them not built give their own
  reasons.
- Tools on 3D features whose method or output the documentation leaves open, each with its reason (feature classes
  carry z and m since grapy's GeoPackage Z/M support).
- Tools whose algorithm the documentation describes without specifying (Kriging, Spline, Natural Neighbor, Topo to
  Raster, Viewshed, Visibility …).
"""
from __future__ import annotations

import numpy as np

from . import sa
from ._raster import Raster, _combine, _refuse_environment, as_raster
from ._result import Result
from ._runtime import ExecuteError, NotBuiltError


def _refuse(tool, parameter, value, why=""):
    raise NotBuiltError(f"arcpy.ddd.{tool}: {parameter} '{value}' is not built in grapy{': ' + why if why else ''}.")


def _saved(raster, out_raster):
    return Result(raster.save(out_raster))


def _integer_output(raster, tool):
    """The tools whose output the documentation says is always integer: a kept floating-point value is refused,
    since the documentation does not say how it would become an integer."""
    if not raster.isInteger:
        raise NotBuiltError(f"arcpy.ddd.{tool}: the output is always integer, and the documentation does not say how a "
                            "floating-point value kept by missing_values 'DATA' becomes one; map every value, or use "
                            "'NODATA'.")
    return raster


# ---- the same computation as arcpy.sa: called and saved


def Slope(in_raster, out_raster, output_measurement="DEGREE", z_factor=1, method="PLANAR", z_unit="METER",
          analysis_target_device="GPU_THEN_CPU"):
    """arcpy.sa.Slope, saved. INFERRED: the default measurement is ``DEGREE``; the ddd page lists the two values without
    marking one, and the sa page's default is ``DEGREE``."""
    return _saved(sa.Slope(in_raster, output_measurement or "DEGREE", z_factor, method, z_unit), out_raster)


def Aspect(in_raster, out_raster, method="PLANAR", z_unit="METER", project_geodesic_azimuths="GEODESIC_AZIMUTHS",
           analysis_target_device="GPU_THEN_CPU"):
    """arcpy.sa.Aspect, saved. ``project_geodesic_azimuths`` applies only to the geodesic method, which is refused."""
    return _saved(sa.Aspect(in_raster, method, z_unit), out_raster)


def HillShade(in_raster, out_raster, azimuth=315, altitude=45, model_shadows="NO_SHADOWS", z_factor=1):
    """arcpy.sa.Hillshade, saved."""
    return _saved(sa.Hillshade(in_raster, azimuth, altitude, model_shadows, z_factor), out_raster)


def Reclassify(in_raster, reclass_field, remap, out_raster, missing_values="DATA"):
    """arcpy.sa.Reclassify, saved. The ddd output is always integer."""
    return _saved(_integer_output(sa.Reclassify(in_raster, reclass_field, remap, missing_values), "Reclassify"), out_raster)


def ReclassByTable(in_raster, in_remap_table, from_value_field, to_value_field, output_value_field, out_raster,
                   missing_values="DATA"):
    """arcpy.sa.ReclassByTable, saved."""
    return _saved(sa.ReclassByTable(in_raster, in_remap_table, from_value_field, to_value_field, output_value_field,
                                    missing_values), out_raster)


def Slice(in_raster, out_raster, number_zones=None, slice_type="EQUAL_INTERVAL", base_output_zone=1, nodata_to_value=None,
          class_interval_size=None):
    """arcpy.sa.Slice, saved; a ``nodata_to_value`` writes that integer where the input is NoData."""
    kind = str(slice_type or "EQUAL_INTERVAL").upper()
    if kind not in ("EQUAL_INTERVAL", "EQUAL_AREA"):
        _refuse("Slice", "slice_type", slice_type)
    if number_zones in (None, "", "#"):
        raise ExecuteError(f"ERROR 000735: Slice needs number_zones for {kind}.")
    sliced = sa.Slice(in_raster, number_zones, kind, base_output_zone)
    if nodata_to_value not in (None, "", "#"):
        array = np.where(sliced._mask, int(nodata_to_value), sliced._array).astype(sliced._array.dtype)
        sliced = Raster._of(array, np.zeros(array.shape, bool), sliced._grid)
    return _saved(sliced, out_raster)


def Plus(in_raster_or_constant1, in_raster_or_constant2, out_raster):
    """arcpy.sa.Plus, saved: integer when both inputs are."""
    return _saved(sa.Plus(in_raster_or_constant1, in_raster_or_constant2), out_raster)


def Minus(in_raster_or_constant1, in_raster_or_constant2, out_raster):
    """arcpy.sa.Minus, saved."""
    return _saved(sa.Minus(in_raster_or_constant1, in_raster_or_constant2), out_raster)


def Times(in_raster_or_constant1, in_raster_or_constant2, out_raster):
    """arcpy.sa.Times, saved."""
    return _saved(sa.Times(in_raster_or_constant1, in_raster_or_constant2), out_raster)


def Float(in_raster_or_constant, out_raster):
    """arcpy.sa.Float, saved."""
    return _saved(sa.Float(in_raster_or_constant), out_raster)


def Int(in_raster_or_constant, out_raster):
    """arcpy.sa.Int, saved: the values truncated."""
    return _saved(sa.Int(in_raster_or_constant), out_raster)


def Divide(in_raster_or_constant1, in_raster_or_constant2, out_raster):
    """The first input divided by the second, cell by cell; NoData where the divisor is zero. As the ddd page documents,
    two integer inputs give an integer division (3 / 2 is 1) and an integer raster; otherwise the output is floating
    point. This is not arcpy.sa.Divide as grapy builds it, which is always floating point, so it is computed here.

    INFERRED: the integer division truncates toward zero (−3 / 2 is −1); the page's only example is positive.
    """
    _refuse_environment("arcpy.ddd.Divide")
    values = [as_raster(v, "Divide") if isinstance(v, str) else v for v in (in_raster_or_constant1, in_raster_or_constant2)]

    def integer(v):
        return v.isInteger if isinstance(v, Raster) else isinstance(v, (int, np.integer)) and not isinstance(v, bool)

    if all(integer(v) for v in values):
        def operation(x, y):
            y = np.asarray(y)
            zero = y == 0
            safe = np.where(zero, 1, y)
            return (np.trunc(np.asarray(x, float) / safe)).astype(np.int32), zero
        return _saved(_combine(values, operation, keep_integer=True, tool="Divide"), out_raster)
    return _saved(sa.Divide(*values), out_raster)



# ---- also arcpy.sa's computations, saved or written


def Idw(in_point_features, z_field, out_raster, cell_size=None, power=2, search_radius=None,
        in_barrier_polyline_features=None):
    """arcpy.sa.Idw, saved."""
    return _saved(sa.Idw(in_point_features, z_field, cell_size, power, search_radius, in_barrier_polyline_features),
                  out_raster)


def Trend(in_point_features, z_field, out_raster, cell_size=None, order=1, regression_type="LINEAR", out_rms_file=None):
    """arcpy.sa.Trend, saved."""
    return _saved(sa.Trend(in_point_features, z_field, cell_size, order, regression_type, out_rms_file), out_raster)


def Curvature(in_raster, out_curvature_raster, z_factor=1, out_profile_curve_raster=None, out_plan_curve_raster=None):
    """arcpy.sa.Curvature, saved."""
    return _saved(sa.Curvature(in_raster, z_factor, out_profile_curve_raster, out_plan_curve_raster), out_curvature_raster)


def Contour(in_raster, out_polyline_features, contour_interval, base_contour=0, z_factor=1, contour_type="CONTOUR",
            max_vertices_per_feature=None):
    """arcpy.sa.Contour: the same tool and parameters."""
    return sa.Contour(in_raster, out_polyline_features, contour_interval, base_contour, z_factor, contour_type,
                      max_vertices_per_feature)


def ContourList(in_raster, out_polyline_features, contour_values, contour_type="CONTOUR"):
    """arcpy.sa.ContourList: the same tool and parameters."""
    return sa.ContourList(in_raster, out_polyline_features, contour_values, contour_type)


def AddSurfaceInformation(in_feature_class, in_surface, out_property, method="BILINEAR", sample_distance=None, z_factor=1,
                          pyramid_level_resolution=0, noise_filtering=None):
    """arcpy.sa.AddSurfaceInformation: the same tool and parameters."""
    return sa.AddSurfaceInformation(in_feature_class, in_surface, out_property, method, sample_distance, z_factor,
                                    pyramid_level_resolution, noise_filtering)



# ---- equivalents (ADR-224 §5.6): arcpy.sa's, saved


from ._runtime import equivalent as _equivalent  # noqa: E402


@_equivalent("ordinary and universal kriging with a semivariogram fitted by weighted least squares",
             "Cressie 1985; Cressie 1993, §3.2–3.4; Johnston et al. 2001, Appendix A")
def Kriging(in_point_features, z_field, out_surface_raster, semiVariogram_props=None, cell_size=None, search_radius=None,
            out_variance_prediction_raster=None):
    """EQUIVALENT: arcpy.sa.Kriging (Cressie's ordinary and universal kriging), saved; see its docstring."""
    return _saved(sa.Kriging(in_point_features, z_field, semiVariogram_props, cell_size, search_radius,
                             out_variance_prediction_raster), out_surface_raster)


@_equivalent("Sibson's natural neighbour interpolation", "Sibson 1981")
def NaturalNeighbor(in_point_features, z_field, out_raster, cell_size=None):
    """EQUIVALENT: arcpy.sa.NaturalNeighbor (Sibson 1981), saved; see its docstring."""
    return _saved(sa.NaturalNeighbor(in_point_features, z_field, cell_size), out_raster)


@_equivalent("R3 line-of-sight viewshed", "Franklin and Ray 1994; How Viewshed works")
def Viewshed(in_raster, in_observer_features, out_raster, z_factor=1, curvature_correction="FLAT_EARTH",
             refractivity_coefficient=0.13, out_agl_raster=None):
    """EQUIVALENT: arcpy.sa.Viewshed (R3 line of sight), saved; see its docstring."""
    return _saved(sa.Viewshed(in_raster, in_observer_features, z_factor, curvature_correction, refractivity_coefficient,
                              out_agl_raster), out_raster)


@_equivalent("R3 line-of-sight visibility", "Franklin and Ray 1994; How Viewshed works")
def Visibility(in_raster, in_observer_features, out_raster, out_agl_raster=None, analysis_type="FREQUENCY",
               nonvisible_cell_value="ZERO", z_factor=1, curvature_correction="FLAT_EARTH", refractivity_coefficient=0.13,
               surface_offset=None, observer_elevation=None, observer_offset=None, inner_radius=None, outer_radius=None,
               horizontal_start_angle=None, horizontal_end_angle=None, vertical_upper_angle=None,
               vertical_lower_angle=None):
    """EQUIVALENT: arcpy.sa.Visibility (R3 line of sight with the observer parameters), saved; see its docstring.

    INFERRED: the parameters follow arcpy.sa.Visibility's order with ``out_raster`` third, as every ddd raster tool
    places its output."""
    return _saved(sa.Visibility(in_raster, in_observer_features, out_agl_raster, analysis_type, nonvisible_cell_value,
                                z_factor, curvature_correction, refractivity_coefficient, surface_offset,
                                observer_elevation, observer_offset, inner_radius, outer_radius, horizontal_start_angle,
                                horizontal_end_angle, vertical_upper_angle, vertical_lower_angle), out_raster)


@_equivalent("the regularized spline and the spline with tension", "Mitas and Mitasova 1988; Franke 1982")
def Spline(in_point_features, z_field, out_raster, cell_size=None, spline_type="REGULARIZED", weight=0.1, number_points=12):
    """EQUIVALENT: arcpy.sa.Spline (Mitas and Mitasova's splines), saved; see its docstring."""
    return _saved(sa.Spline(in_point_features, z_field, cell_size, spline_type, weight, number_points), out_raster)


# ---- refused, by name, with the reason

_GROUP_2 = "needs a data model this server does not hold yet (ADR-224 group 2)"
# Superseded for every name below by its own reason after grapy's Z/M support; kept as the first classification.
_NO_Z = "it works on 3D features in a way its documentation leaves open"
_UNSPECIFIED = "its algorithm is described in the documentation but not specified closely enough to reproduce"
_NO_RAT = "its answer is held in a raster attribute table, which grapy's rasters do not carry yet"

_REFUSED = {name: _GROUP_2 for name in (
    "AddFeatureClassToTerrain", "AddTerrainPoints", "AddTerrainPyramidLevel", "AppendTerrainPoints", "BuildTerrain",
    "ChangeTerrainReferenceScale", "ChangeTerrainResolutionBounds", "CreateTerrain", "DeleteTerrainPoints",
    "RemoveFeatureClassFromTerrain", "RemoveTerrainPoints", "RemoveTerrainPyramidLevel", "ReplaceTerrainPoints",
    "TerrainToPoints", "TerrainToRaster", "TerrainToTin", "BIMToTerrain",
    "CopyTin", "CreateTin", "DecimateTinNodes", "DelineateTinDataArea", "EditTin", "LandXMLToTin", "RasterTin",
    "TINToMultipatch", "TinAspect", "TinContour", "TinDifference", "TinDomain", "TinEdge", "TinLine", "TinNode",
    "TinPolygonTag", "TinPolygonVolume", "TinRaster", "TinSlope", "TinTriangle",
    "ChangeLasClassCodes", "ClassifyLasBuilding", "ClassifyLasByHeight", "ClassifyLasGround", "ClassifyLasNoise",
    "ClassifyLasOverlap", "ColorizeLas", "ExtractLas", "LASToMultipoint", "LasBuildingMultipatch", "LasDatasetToTin",
    "LasHeightMetrics", "LasPointStatsByArea", "LocateLasPointsByProximity", "SetLasClassCodesUsingFeatures",
    "SetLasClassCodesUsingRaster", "ThinLas", "TileLas", "PointFileInformation",
    "ClassifyPointCloudUsingTrainedModel", "DetectObjectsFromPointCloudUsingTrainedModel",
    "EvaluatePointCloudClassificationModel", "ExtractObjectsFromPointCloud", "ExtractPowerLinesFromPointCloud",
    "ExtractRailsFromPointCloud", "ExtractMeshFeaturesUsingPointCloud", "PreparePointCloudObjectDetectionTrainingData",
    "PreparePointCloudTrainingData", "TrainPointCloudClassificationModel", "TrainPointCloudObjectDetectionModel",
    "ExtractLOD2Buildings", "ExtractMultipatchFromMesh",
    "EncloseMultiPatch", "Import3DFiles", "InterpolatePolyToPatch", "Intersect3DLineWithMultiPatch", "MultiPatchFootprint",
    "IsClosed3D", "Inside3D", "Intersect3D", "Union3D", "Difference3D", "ExtrudeBetween", "SunShadowVolume",
    "SurfaceAspect", "SurfaceSlope", "SurfaceContour", "SurfaceDifference", "LocateOutliers",
)}
_REFUSED.update({name: _NO_Z for name in (
    "AddZInformation", "CalculateMissingZValues", "FeatureTo3DByAttribute", "UpdateFeatureZ", "Simplify3DLine",
    "GeneratePointsAlong3DLines", "Buffer3D", "Near3D", "Intersect3DLines", "Intersect3DLineWithSurface", "LineOfSight",
    "ConstructSightLines", "Intervisibility", "InterpolateShape", "RasterDomain", "RasterToMultipoint",
    "FeatureClassZToASCII", "ASCII3DToFeatureClass", "MinimumBoundingVolume", "Layer3DToFeatureClass",
)})
_REFUSED.update({name: _UNSPECIFIED for name in (
    "Kriging", "NaturalNeighbor", "Spline", "SplineWithBarriers", "TopoToRaster", "TopoToRasterByFile", "Viewshed",
    "Viewshed2", "Visibility", "ObserverPoints", "SurfaceVolume", "SurfaceParameters", "StackProfile",
    "ContourWithBarriers",
)})
_REFUSED.update({
    "CutFill": _NO_RAT + " — the VOLUME and AREA of each region",
    "Lookup": _NO_RAT,
    "ReclassByASCIIFile": "the documentation does not specify the remap file's format",
    "LayerToKML": "it reads map layers, which a user tool on the server does not have",
    "MapToKML": "it reads a map, which a user tool on the server does not have",
    "Skyline": _NO_Z, "SkylineBarrier": _NO_Z, "SkylineGraph": _NO_Z,
})


_REFUSED.update({
    "PolygonVolume": "its surfaces are LAS datasets, terrains and TINs, data models this server does not hold yet "
                     "(ADR-224 group 2)",
    "SunShadowFrequency": "its obstructions are multipatch features, a data model this server does not hold yet "
                          "(ADR-224 group 2)",
    "FenceDiagram": "it writes multipatch features, a data model this server does not hold yet (ADR-224 group 2)",
    "EnforceRiverMonotonicity": _NO_Z, "GenerateClearanceSurface": _NO_Z,
    "RegularizeBuildingFootprint": _UNSPECIFIED, "RegularizeAdjacentBuildingFootprint": _UNSPECIFIED,
    "FeaturesFromCityEngineRules": "it runs CityEngine rule packages (.rpk), which need Esri's procedural engine "
                                   "(ADR-224 group 3)",
    "SurfaceLength": "its reference page could not be read (404 on 2026-10-29)",
    "SurfaceSpot": "its reference page could not be read (404 on 2026-10-29)",
})


for _built in ("Kriging", "NaturalNeighbor", "Viewshed", "Visibility", "Spline"):
    _REFUSED.pop(_built, None)


# ---- Z on features (grapy's feature classes carry z and m since the GeoPackage Z/M support)

import math as _math  # noqa: E402

import shapely as _shapely  # noqa: E402


def _z_class(path, tool):
    from . import _gpkg
    connection, table = _gpkg.table_of(path)
    try:
        columns, kind, srid, _ = _gpkg.describe(connection, table)
        has_z, has_m = _gpkg.dimensions(connection, table)
    finally:
        connection.close()
    return columns, kind, srid, has_z, has_m


def _zrows(shape):
    """(x, y, z) of every vertex, in order."""
    return _shapely.get_coordinates(shape, include_z=True)


def _lines_of(shape):
    """Each line part, and each polygon ring, as an (n, 3) array of x, y, z."""
    parts = []
    for part in getattr(shape, "geoms", [shape]):
        if part.geom_type == "Polygon":
            for ring in [part.exterior, *part.interiors]:
                parts.append(_shapely.get_coordinates(ring, include_z=True))
        elif part.geom_type in ("LineString", "LinearRing"):
            parts.append(_shapely.get_coordinates(part, include_z=True))
    return parts


def FeatureTo3DByAttribute(in_features, out_feature_class, height_field, to_height_field=None):
    """3D features from 2D ones, each vertex given the height in ``height_field``; a line with ``to_height_field``
    starts at the first height and ends at the second, its other vertices interpolated on the slope between them, as
    documented. Fields are copied.

    INFERRED: the slope runs with the 2D distance along the line; ``to_height_field`` on a point, multipoint or polygon
    is ignored as the page applies it to lines only; a null height leaves the feature out; m-values are kept."""
    from . import _gpkg, da
    from .analysis import _out
    columns, kind, srid, _, has_m = _z_class(in_features, "FeatureTo3DByAttribute")
    names = [c.name for c in columns]
    lower = {n.lower(): n for n in names}
    first = lower.get(str(height_field).lower())
    second = lower.get(str(to_height_field).lower()) if to_height_field not in (None, "", "#") else None
    if first is None:
        raise ExecuteError(f"ERROR 000728: '{height_field}' is not a field of {in_features}.")
    out = _out(out_feature_class)
    _gpkg.create(out, kind, srid, columns, True, has_m)
    with da.SearchCursor(in_features, [*names, "SHAPE@"]) as rows, da.InsertCursor(out, [*names, "SHAPE@"]) as written:
        for row in rows:
            values = dict(zip(names, row[:-1]))
            shape = row[-1]._shape if row[-1] is not None else None
            if shape is None or values[first] is None:
                continue
            start = float(values[first])
            end = float(values[second]) if second is not None and values[second] is not None else None
            if end is not None and kind == "Polyline":
                total = shape.length
                parts, travelled = [], 0.0
                for part in getattr(shape, "geoms", [shape]):
                    xy = _shapely.get_coordinates(part)
                    steps = np.hypot(np.diff(xy[:, 0]), np.diff(xy[:, 1]))
                    distance = travelled + np.concatenate([[0.0], np.cumsum(steps)])
                    travelled = distance[-1]
                    zs = start + (end - start) * (distance / total if total else 0.0)
                    parts.append([(float(x), float(y), float(z)) for (x, y), z in zip(xy, zs)])
                built = _shapely.MultiLineString(parts)
                written.insertRow((*row[:-1], _gpkg.rebuild(built, True, True) if has_m else built))
            else:
                written.insertRow((*row[:-1], _gpkg.rebuild(shape, True, has_m, None,
                                                             lambda z, xy, h=start: np.full(len(xy), h))))
    return Result(out)


_SLOPES = ("MIN_SLOPE", "MAX_SLOPE", "AVG_SLOPE")


def AddZInformation(in_feature_class, out_property, noise_filtering=None):
    """Fields of each 3D feature's z-properties added and filled, as documented: ``Z`` of a point; ``POINT_COUNT`` of a
    multipoint; ``Z_MIN``, ``Z_MAX``; ``Z_MEAN`` — for lines and polygon boundaries each segment's mid-point z weighted
    by its 3D length; ``LENGTH_3D``; ``VERTEX_COUNT``; and ``MIN_SLOPE``, ``MAX_SLOPE`` and ``AVG_SLOPE`` in percent grade
    per segment — the one closest to 0, the largest, and the mean weighted by 3D length — with −1 for a vertical
    segment. ``noise_filtering`` leaves out segments shorter (in 3D) than it.

    SURFACE_AREA and VOLUME of a multipatch (ADR-238): the 3D area of its faces, and the volume a closed one encloses
    — 0 for one that is not closed, as Esri's knowledge base says Add Z Information gives (INFERRED, the tool page being
    silent). Refused: SURFACE_AREA and VOLUME on a class that is not a multipatch; a 2D feature class.

    INFERRED: each field is named as its property and is a double (POINT_COUNT and VERTEX_COUNT integers); a multipoint's
    Z_MEAN is the plain mean of its points; slopes are magnitudes, so the one closest to 0 is the least steep; noise
    filtering applies to the slopes and Z_MEAN, the parts the page describes it for; a polygon's VERTEX_COUNT counts each
    ring's closing vertex."""
    from . import _gpkg, _multipatch, da
    from .management import _list
    columns, kind, _, has_z, _ = _z_class(in_feature_class, "AddZInformation")
    if not has_z:
        _refuse("AddZInformation", "in_feature_class", in_feature_class, "it is a 2D feature class")
    wanted = [p.upper() for p in _list(out_property)]
    for p in wanted:
        if p in ("SURFACE_AREA", "VOLUME") and not _multipatch.is_multipatch(in_feature_class):
            _refuse("AddZInformation", "out_property", p, "it is a multipatch property, and the input is not a "
                    "multipatch class")
        if p not in ("Z", "POINT_COUNT", "Z_MIN", "Z_MAX", "Z_MEAN", "LENGTH_3D", "VERTEX_COUNT", "SURFACE_AREA",
                     "VOLUME", *_SLOPES):
            _refuse("AddZInformation", "out_property", p)
    noise = float(noise_filtering) if noise_filtering not in (None, "", "#", "NO_FILTER") else 0.0
    existing = {c.name.lower() for c in columns}
    for p in wanted:
        if p.lower() not in existing:
            _gpkg.add_column(in_feature_class, _gpkg.Column(p, "Integer" if p in ("POINT_COUNT", "VERTEX_COUNT")
                                                            else "Double"))
    with da.UpdateCursor(in_feature_class, [*wanted, "SHAPE@"]) as rows:
        for row in rows:
            shape = row[-1]._shape if row[-1] is not None else None
            if shape is None:
                continue
            values = []
            z = _zrows(shape)[:, 2]
            segments = []
            for part in _lines_of(shape):
                for a, b in zip(part[:-1], part[1:]):
                    run = _math.hypot(b[0] - a[0], b[1] - a[1])
                    length = _math.hypot(run, b[2] - a[2])
                    slope = -1.0 if run == 0 and length > 0 else (abs(b[2] - a[2]) / run * 100 if run else 0.0)
                    segments.append((length, (a[2] + b[2]) / 2, slope))
            kept = [s for s in segments if s[0] >= noise and s[0] > 0]
            for p in wanted:
                if p == "Z":
                    values.append(float(z[0]) if shape.geom_type == "Point" else None)
                elif p == "POINT_COUNT":
                    values.append(len(z) if shape.geom_type == "MultiPoint" else None)
                elif p == "Z_MIN":
                    values.append(float(z.min()))
                elif p == "Z_MAX":
                    values.append(float(z.max()))
                elif p == "Z_MEAN":
                    if segments:
                        total = sum(s[0] for s in kept)
                        values.append(sum(s[0] * s[1] for s in kept) / total if total else None)
                    else:
                        values.append(float(z.mean()))
                elif p == "LENGTH_3D":
                    values.append(sum(s[0] for s in segments) if segments else None)
                elif p == "VERTEX_COUNT":
                    values.append(len(z) if segments else None)
                elif p == "SURFACE_AREA":
                    values.append(_multipatch.surface_area(_multipatch.triangles(shape)))
                elif p == "VOLUME":
                    values.append(_multipatch.signed_volume(_multipatch.triangles(shape))
                                  if _multipatch.is_closed(shape) else 0.0)
                else:
                    slopes = [s for s in kept if s[2] >= 0]
                    vertical = any(s[2] < 0 for s in kept)
                    if p == "MIN_SLOPE":
                        values.append(min((s[2] for s in slopes), default=-1.0 if vertical else None))
                    elif p == "MAX_SLOPE":
                        values.append(-1.0 if vertical else max((s[2] for s in slopes), default=None))
                    else:
                        total = sum(s[0] for s in slopes)
                        values.append(sum(s[0] * s[2] for s in slopes) / total if total else None)
            rows.updateRow([*values, row[-1]])
    return Result(in_feature_class)


def _segment_point(p, a, b):
    """The point of segment a–b nearest p, in three dimensions."""
    d = b - a
    length2 = float(d @ d)
    t = 0.0 if length2 == 0 else max(0.0, min(1.0, float((p - a) @ d) / length2))
    return a + t * d


def Near3D(in_features, near_features, search_radius=None, location="NO_LOCATION", angle="NO_ANGLE", delta="NO_DELTA"):
    """For each input point, the nearest near feature in three dimensions: ``NEAR_FID`` (−1 when none is within the
    radius), ``NEAR_DIST`` (horizontal) and ``NEAR_DIST3`` (3D), and as asked the nearest points (``NEAR_FROMX`` …
    ``NEAR_Z``), the angles (``NEAR_ANG_H`` arithmetic, ``NEAR_ANG_V`` 0 horizontal, 90 up, −90 down) and the axis
    distances (``NEAR_DELTX`` …); ``NEAR_FC`` with several near classes. As documented.

    Built: input points; near points, multipoints and lines (the nearest point on each 3D segment). Refused: other input
    shapes and polygon near features, whose nearest point on a non-planar 3D polygon the page does not define.

    INFERRED: the radius is measured in 3D; on a tie the first near feature in class and ObjectID order wins; with no
    match the other fields are −1; the deltas run from the input to the near point."""
    from . import _gpkg, da
    from .management import _list
    _, kind, srid, has_z, _ = _z_class(in_features, "Near3D")
    if kind != "Point":
        _refuse("Near3D", "in_features", kind, "only point inputs are built")
    paths = _list(near_features)
    targets = []
    for path in paths:
        _, nkind, _, nz, _ = _z_class(path, "Near3D")
        if nkind == "Polygon":
            _refuse("Near3D", "near_features", path, "the nearest point on a 3D polygon is not defined")
        if not (has_z and nz):
            _refuse("Near3D", "near_features", path, "all inputs must have z-values, as documented")
        with da.SearchCursor(path, ["OID@", "SHAPE@"]) as rows:
            for oid, shape in rows:
                if shape is not None:
                    targets.append((path, oid, shape._shape))
    radius = float(str(search_radius).split()[0]) if search_radius not in (None, "", "#") else None
    fields = ["NEAR_FID", "NEAR_DIST", "NEAR_DIST3"]
    if str(location).upper() == "LOCATION":
        fields += ["NEAR_FROMX", "NEAR_FROMY", "NEAR_FROMZ", "NEAR_X", "NEAR_Y", "NEAR_Z"]
    if str(angle).upper() == "ANGLE":
        fields += ["NEAR_ANG_H", "NEAR_ANG_V"]
    if str(delta).upper() == "DELTA":
        fields += ["NEAR_DELTX", "NEAR_DELTY", "NEAR_DELTZ"]
    if len(paths) > 1:
        fields.append("NEAR_FC")
    columns, _, _, _, _ = _z_class(in_features, "Near3D")
    existing = {c.name.lower() for c in columns}
    for name in fields:
        if name.lower() not in existing:
            _gpkg.add_column(in_features, _gpkg.Column(name, "String" if name == "NEAR_FC" else
                                                       "Integer" if name == "NEAR_FID" else "Double"))
    with da.UpdateCursor(in_features, [*fields, "SHAPE@"]) as rows:
        for row in rows:
            p = _zrows(row[-1]._shape)[0]
            best = None
            for path, oid, shape in targets:
                coordinates = _zrows(shape)
                if shape.geom_type in ("Point", "MultiPoint"):
                    candidates = list(coordinates)
                else:
                    candidates = [_segment_point(p, a, b) for part in _lines_of(shape) for a, b in zip(part[:-1], part[1:])]
                for q in candidates:
                    d3 = float(np.linalg.norm(q - p))
                    if (radius is None or d3 <= radius) and (best is None or d3 < best[0]):
                        best = (d3, path, oid, q)
            values = {n: -1.0 for n in fields}
            values["NEAR_FID"] = -1
            values["NEAR_FC"] = ""
            if best is not None:
                d3, path, oid, q = best
                dx, dy, dz = (q - p)
                values.update(NEAR_FID=oid, NEAR_DIST=_math.hypot(dx, dy), NEAR_DIST3=d3, NEAR_FROMX=p[0],
                              NEAR_FROMY=p[1], NEAR_FROMZ=p[2], NEAR_X=q[0], NEAR_Y=q[1], NEAR_Z=q[2],
                              NEAR_ANG_H=_math.degrees(_math.atan2(dy, dx)) if (dx or dy) else 0.0,
                              NEAR_ANG_V=_math.degrees(_math.atan2(dz, _math.hypot(dx, dy))) if (dx or dy or dz) else 0.0,
                              NEAR_DELTX=dx, NEAR_DELTY=dy, NEAR_DELTZ=dz, NEAR_FC=str(path))
            rows.updateRow([*[float(values[n]) if n not in ("NEAR_FID", "NEAR_FC") else values[n] for n in fields],
                            row[-1]])
    return Result(in_features)


def _densified(shape, step):
    """Each segment divided into the fewest equal pieces no longer than ``step``, as grapy's Densify divides them."""
    if step is None or shape.geom_type in ("Point", "MultiPoint"):
        return shape
    return _shapely.segmentize(_shapely.force_2d(shape), step)


def InterpolateShape(in_surface, in_feature_class, out_feature_class, sample_distance=None, z_factor=1, method="BILINEAR",
                     vertices_only="DENSIFY", pyramid_level_resolution=0, preserve_features="EXCLUDE",
                     interpolate_m_value="NO_INTERPOLATE_M_VALUE"):
    """3D features with each vertex's z interpolated from a raster surface — ``BILINEAR`` by default, or ``NEAREST`` —
    times ``z_factor``. With ``DENSIFY`` (the default) vertices are first added at ``sample_distance`` (by default the
    cell size), as documented; with ``VERTICES_ONLY`` only the existing vertices are interpolated. A feature with a vertex
    off the surface's data is left out, unless the method is NEAREST and ``preserve_features`` is ``PRESERVE``, as
    documented. Fields are copied.

    On a TIN or LAS dataset (ADR-229) the method is ``LINEAR`` — "a weighted average of the three nodes of the triangle
    containing the interpolation point" — and lines are sampled at their vertices and where they cross a triangle edge,
    plus every ``sample_distance`` when one is given, as documented; a feature with a vertex off the data area is left
    out (INFERRED, as on a raster).

    On a terrain (ADR-234) as on a TIN, at the pyramid level given.

    Refused: the methods other than BILINEAR and NEAREST on a raster and LINEAR on a TIN
    (natural neighbours and conflation, ADR-229 §5.4); ``interpolate_m_value``.

    INFERRED: densification divides each segment into the fewest equal pieces no longer than the sample distance; under
    PRESERVE a vertex off the data takes z 0; m-values are not carried."""
    from . import _gpkg, _tintools, da
    from .analysis import _out
    if _tintools.is_surface(in_surface):
        return _tintools.interpolate_shape(in_surface, in_feature_class, out_feature_class, sample_distance, z_factor,
                                           method, vertices_only, preserve_features, interpolate_m_value,
                                           pyramid_level_resolution)
    if isinstance(in_surface, str) and not in_surface.lower().endswith((".tif", ".tiff")):
        _refuse("InterpolateShape", "in_surface", in_surface, "it is not a raster, TIN, terrain or LAS dataset grapy "
                "holds")
    kind_of = str(method or "BILINEAR").upper()
    if kind_of not in ("BILINEAR", "NEAREST"):
        _refuse("InterpolateShape", "method", method)
    if str(interpolate_m_value or "NO_INTERPOLATE_M_VALUE").upper() != "NO_INTERPOLATE_M_VALUE":
        _refuse("InterpolateShape", "interpolate_m_value", interpolate_m_value)
    preserve = str(preserve_features or "EXCLUDE").upper() == "PRESERVE"
    if preserve and kind_of != "NEAREST":
        raise ExecuteError("InterpolateShape: PRESERVE is available only with NEAREST, as documented.")
    raster = as_raster(in_surface, "InterpolateShape")
    step = float(sample_distance) if sample_distance not in (None, "", "#") else raster.meanCellWidth
    every = str(vertices_only or "DENSIFY").upper() != "VERTICES_ONLY"
    zf = float(z_factor if z_factor is not None else 1)
    columns, kind, srid, _, _ = _z_class(in_feature_class, "InterpolateShape")
    names = [c.name for c in columns]
    out = _out(out_feature_class)
    _gpkg.create(out, kind, srid, columns, True, False)
    with da.SearchCursor(in_feature_class, [*names, "SHAPE@"]) as rows, da.InsertCursor(out, [*names, "SHAPE@"]) as written:
        for row in rows:
            if row[-1] is None:
                continue
            shape = _densified(row[-1]._shape, step) if every else _shapely.force_2d(row[-1]._shape)
            missing = [False]

            def heights(z, xy):
                out_z = []
                for x, y in xy:
                    value = sa._sample(raster, x, y, kind_of == "BILINEAR")
                    if value is None:
                        missing[0] = True
                        value = 0.0
                    out_z.append(float(value) * zf)
                return np.array(out_z).reshape(-1, 1)
            built = _gpkg.rebuild(shape, True, False, None, heights)
            if missing[0] and not preserve:
                continue
            written.insertRow((*row[:-1], built))
    return Result(out)


def UpdateFeatureZ(in_features, in_surface, method="BILINEAR", status_field=None):
    """Each vertex's z replaced by the raster surface's, bilinearly interpolated, for the features entirely on the
    surface; the others are left, as documented. ``status_field`` receives 1 for a feature updated and 0 for one not.

    On a TIN or LAS dataset (ADR-229) the method is LINEAR, each vertex on its triangle's plane.

    Refused: terrain surfaces (ADR-229 §5.6); methods other than BILINEAR on a raster (the page names BILINEAR as
    the raster's) and LINEAR on a TIN; a 2D feature class.

    INFERRED: "entirely on the surface" means every vertex has a value under bilinear interpolation."""
    from . import _gpkg, _tintools, da
    tin = _tintools.surface_of(in_surface, "UpdateFeatureZ") if _tintools.is_surface(in_surface) else None
    if tin is None and isinstance(in_surface, str) and not in_surface.lower().endswith((".tif", ".tiff")):
        _refuse("UpdateFeatureZ", "in_surface", in_surface, "terrains are not held (ADR-229 §5.6)")
    if tin is not None:
        if str(method or "LINEAR").upper() not in ("LINEAR", "BILINEAR"):
            _refuse("UpdateFeatureZ", "method", method, "a TIN is interpolated linearly (ADR-229 §5.4)")
        return _update_on_tin(in_features, tin, status_field)
    if str(method or "BILINEAR").upper() != "BILINEAR":
        _refuse("UpdateFeatureZ", "method", method, "a raster surface is interpolated bilinearly")
    _, _, _, has_z, has_m = _z_class(in_features, "UpdateFeatureZ")
    if not has_z:
        _refuse("UpdateFeatureZ", "in_features", in_features, "it is a 2D feature class")
    raster = as_raster(in_surface, "UpdateFeatureZ")
    fields = ["SHAPE@"] + ([status_field] if status_field not in (None, "", "#") else [])
    with da.UpdateCursor(in_features, fields) as rows:
        for row in rows:
            if row[0] is None:
                continue
            shape = row[0]._shape
            samples = [sa._sample(raster, x, y, True) for x, y in _shapely.get_coordinates(shape)]
            if any(s is None for s in samples):
                rows.updateRow([row[0], *([0] if len(fields) > 1 else [])])
                continue
            built = _gpkg.rebuild(shape, True, has_m, None,
                                  lambda z, xy: np.array([sa._sample(raster, x, y, True) for x, y in xy], dtype=np.float64))
            rows.updateRow([built, *([1] if len(fields) > 1 else [])])
    return Result(in_features)


def _update_on_tin(in_features, tin, status_field):
    """UpdateFeatureZ on a TIN: each vertex given its triangle plane's height, for features wholly on the data area."""
    from . import _gpkg, da
    _, _, _, has_z, has_m = _z_class(in_features, "UpdateFeatureZ")
    if not has_z:
        _refuse("UpdateFeatureZ", "in_features", in_features, "it is a 2D feature class")
    fields = ["SHAPE@"] + ([status_field] if status_field not in (None, "", "#") else [])
    with da.UpdateCursor(in_features, fields) as rows:
        for row in rows:
            if row[0] is None:
                continue
            shape = row[0]._shape
            xy = _shapely.get_coordinates(shape)
            if np.isnan(tin.z_at(xy[:, 0], xy[:, 1])).any():
                rows.updateRow([row[0], *([0] if len(fields) > 1 else [])])
                continue
            built = _gpkg.rebuild(shape, True, has_m, None,
                                  lambda z, part: tin.z_at(np.asarray(part)[:, 0], np.asarray(part)[:, 1]))
            rows.updateRow([built, *([1] if len(fields) > 1 else [])])
    return Result(in_features)


for _built in ("FeatureTo3DByAttribute", "AddZInformation", "Near3D", "InterpolateShape", "UpdateFeatureZ"):
    _REFUSED.pop(_built, None)
_REFUSED.update({
    "Buffer3D": "it writes multipatch features, a data model this server does not hold yet (ADR-224 group 2)",
    "MinimumBoundingVolume": "it writes multipatch features, a data model this server does not hold yet (ADR-224 group 2)",
    "Layer3DToFeatureClass": "it reads a 3D layer from a map, which a user tool on the server does not have",
    "RasterToMultipoint": "how the cells' points are grouped into multipoint features is not documented",
    "RasterDomain": "how cells joined only at a corner are traced into its perimeter through cell centres is not "
                    "documented",
    "LineOfSight": "how the sight line is sampled against the surface is not documented",
    "Intersect3DLineWithSurface": "how the line is sampled against the surface is not documented",
    "Intervisibility": "how each sight line is sampled against the obstructions is not documented",
    "Skyline": "its horizon search and output vertices are not documented",
    "SkylineGraph": "it reads Skyline's output, which is refused",
    "EnforceRiverMonotonicity": "its boundary sampling and simplification are described, not specified",
    "GenerateClearanceSurface": "its flare geometry and overlap rules are described, not specified",
})


# ---- more tools on 3D features


def _dp3(points, tolerance):
    """Douglas-Peucker in three dimensions: a vertex stays when it lies farther than the tolerance, in 3D, from the
    segment between the vertices kept either side of it."""
    keep = {0, len(points) - 1}
    stack = [(0, len(points) - 1)]
    while stack:
        first, last = stack.pop()
        if last - first < 2:
            continue
        a, b = points[first], points[last]
        far, at = -1.0, None
        for i in range(first + 1, last):
            gap = float(np.linalg.norm(points[i] - _segment_point(points[i], a, b)))
            if gap > far:
                far, at = gap, i
        if far > tolerance:
            keep.add(at)
            stack += [(first, at), (at, last)]
    return sorted(keep)


def Simplify3DLine(in_features, out_feature_class, tolerance):
    """3D lines simplified by the Douglas-Peucker algorithm the page names, the tolerance being the 3D distance the
    simplified line must stay within, as documented. Fields are copied.

    INFERRED: the distance is to the segment between kept vertices (not its infinite line), and a vertex exactly at the
    tolerance goes; each part is simplified on its own; m-values are dropped."""
    from . import _gpkg, da
    from .analysis import _out
    columns, kind, srid, has_z, _ = _z_class(in_features, "Simplify3DLine")
    if kind != "Polyline" or not has_z:
        _refuse("Simplify3DLine", "in_features", in_features, "it takes 3D lines")
    step = float(str(tolerance).split()[0])
    names = [c.name for c in columns]
    out = _out(out_feature_class)
    _gpkg.create(out, "Polyline", srid, columns, True, False)
    with da.SearchCursor(in_features, [*names, "SHAPE@"]) as rows, da.InsertCursor(out, [*names, "SHAPE@"]) as written:
        for row in rows:
            if row[-1] is None:
                continue
            parts = []
            for part in _lines_of(row[-1]._shape):
                kept = _dp3(part, step)
                parts.append([tuple(part[i]) for i in kept])
            written.insertRow((*row[:-1], _shapely.MultiLineString(parts)))
    return Result(out)


def _along_3d(part, distances):
    """Points at given 3D distances along a line part, (x, y, z)."""
    steps = np.linalg.norm(np.diff(part, axis=0), axis=1)
    cumulative = np.concatenate([[0.0], np.cumsum(steps)])
    out = []
    for d in distances:
        k = int(np.searchsorted(cumulative, d, side="right") - 1)
        k = min(max(k, 0), len(steps) - 1)
        t = (d - cumulative[k]) / steps[k] if steps[k] else 0.0
        out.append(part[k] + t * (part[k + 1] - part[k]))
    return out, cumulative[-1]


def GeneratePointsAlong3DLines(in_features, out_features, point_placement="DISTANCE", distance=None, percentage=None,
                               include_end_points="NO_END_POINTS", add_chainage_fields="NO_CHAINAGE",
                               distance_field=None):
    """3D points along each 3D line, measured in three dimensions as documented: every ``distance`` from the start
    (``DISTANCE``, the Python default), or at multiples of ``percentage`` of the length (``PERCENTAGE``), with the start
    and end too under ``END_POINTS``. Each point keeps its line's fields and ``ORIG_FID``; ``ADD_CHAINAGE`` adds
    ``ORIG_LEN`` (the 3D distance from the start) and ``ORIG_SEQ`` (the point's order along its line).

    Refused: ``DISTANCE_FIELD``.

    INFERRED: a point falling exactly at the end is the end (written only with END_POINTS); a multipart line is measured
    part by part from each part's start; ORIG_SEQ counts from 1."""
    from . import _gpkg, da
    from .analysis import _out
    columns, kind, srid, has_z, _ = _z_class(in_features, "GeneratePointsAlong3DLines")
    if kind != "Polyline" or not has_z:
        _refuse("GeneratePointsAlong3DLines", "in_features", in_features, "it takes 3D lines")
    mode = str(point_placement or "DISTANCE").upper()
    if mode not in ("DISTANCE", "PERCENTAGE"):
        _refuse("GeneratePointsAlong3DLines", "point_placement", point_placement)
    ends = str(include_end_points or "NO_END_POINTS").upper() == "END_POINTS"
    chainage = str(add_chainage_fields or "NO_CHAINAGE").upper() == "ADD_CHAINAGE"
    names = [c.name for c in columns]
    extra = [_gpkg.Column("ORIG_FID", "Integer")] + ([_gpkg.Column("ORIG_LEN", "Double"), _gpkg.Column("ORIG_SEQ", "Integer")]
                                                     if chainage else [])
    out = _out(out_features)
    _gpkg.create(out, "Point", srid, [*columns, *extra], True, False)
    with da.SearchCursor(in_features, ["OID@", *names, "SHAPE@"]) as rows, \
            da.InsertCursor(out, [*names, *[c.name for c in extra], "SHAPE@"]) as written:
        for row in rows:
            if row[-1] is None:
                continue
            sequence = 0
            for part in _lines_of(row[-1]._shape):
                _, length = _along_3d(part, [])
                if mode == "DISTANCE":
                    step = float(str(distance).split()[0])
                    marks = list(np.arange(step, length, step)) if step > 0 else []
                else:
                    share = float(percentage) / 100 * length
                    marks = list(np.arange(share, length, share)) if share > 0 else []
                marks = [m for m in marks if m < length]
                if ends:
                    marks = [0.0, *marks, length]
                points, _ = _along_3d(part, marks)
                for d, p in zip(marks, points):
                    sequence += 1
                    values = [row[0]] + ([float(d), sequence] if chainage else [])
                    written.insertRow((*row[1:-1], *values, _shapely.Point(*p)))
    return Result(out)


def _ascii_number(value, decimal_format, digits, comma):
    text = f"{value:.{int(digits)}f}" if decimal_format == "FIXED" else repr(float(value))
    if text.endswith(".0") and decimal_format != "FIXED":
        text = text[:-2]
    return text.replace(".", ",") if comma else text


def FeatureClassZToASCII(in_feature_class, output_location, out_file, format="GENERATE", delimiter="SPACE",
                         decimal_format="AUTOMATIC", digits_after_decimal=None, decimal_separator="DECIMAL_POINT"):
    """3D features written to one GENERATE file, as documented: no header; a point is ``id x y z``; a line or polygon
    an id line, its ``x y z`` rows and END; a final END closes the file, so it ends with two. Delimiter, decimal
    notation and separator as documented (AUTOMATIC keeps the precision needed and drops trailing zeros).

    Refused: XYZ and PROFILE, which write one file per feature under names the page does not give.

    INFERRED: the id is the ObjectID; AUTOMATIC writes Python's shortest exact form of each number; a polygon's rings
    are written as consecutive blocks, each closing on its first vertex."""
    import os
    from . import da
    if str(format or "GENERATE").upper() != "GENERATE":
        _refuse("FeatureClassZToASCII", "format", format, "it writes files named in a way the page does not give")
    comma = str(decimal_separator or "DECIMAL_POINT").upper() == "DECIMAL_COMMA"
    separator = "," if str(delimiter or "SPACE").upper() == "COMMA" else " "
    if comma and separator == ",":
        raise ExecuteError("ERROR 000800: a comma delimiter cannot go with a decimal comma.")
    fmt = str(decimal_format or "AUTOMATIC").upper()
    if fmt == "FIXED" and digits_after_decimal in (None, "", "#"):
        _refuse("FeatureClassZToASCII", "digits_after_decimal", "(empty)", "FIXED needs it; no default is documented")
    _, kind, _, has_z, _ = _z_class(in_feature_class, "FeatureClassZToASCII")
    if not has_z:
        _refuse("FeatureClassZToASCII", "in_feature_class", in_feature_class, "it is a 2D feature class")

    def line(values):
        return separator.join(_ascii_number(v, fmt, digits_after_decimal or 0, comma) if not isinstance(v, int)
                              else str(v) for v in values)

    lines = []
    with da.SearchCursor(in_feature_class, ["OID@", "SHAPE@"]) as rows:
        for oid, shape in rows:
            if shape is None:
                continue
            geometry = shape._shape
            if geometry.geom_type in ("Point", "MultiPoint"):
                for p in _zrows(geometry):
                    lines.append(line([oid, *p]))
                continue
            for part in _lines_of(geometry):
                lines.append(str(oid))
                lines += [line(list(p)) for p in part]
                lines.append("END")
    lines.append("END")
    path = os.path.join(output_location, out_file)
    with open(path, "w", encoding="utf-8") as written:
        written.write("\n".join(lines) + "\n")
    return Result(path)


def ASCII3DToFeatureClass(input, in_file_type, out_feature_class, out_geometry_type, z_factor=1,
                          input_coordinate_system=None, average_point_spacing=None, file_suffix=None,
                          decimal_separator="DECIMAL_POINT"):
    """3D features read from ASCII files, as documented: XYZ — every row beginning with three numbers is a point, any
    header before and trailing entries ignored; GENERATE — ``id x y z`` points, or blocks of an id line, ``x y z`` rows and
    END for lines and polygons, two ENDs ending the file. With POINT output each coordinate is a point; with POLYLINE or
    POLYGON an XYZ file is one feature and a GENERATE block one. z is multiplied by ``z_factor``. A folder is read for
    the files ending in ``file_suffix``.

    Refused: XYZI (its column order is not given) and MULTIPOINT output (its tiling by point spacing is not specified).

    INFERRED: numbers may be separated by spaces, tabs or commas (commas only with DECIMAL_POINT); the output has no
    fields; files in a folder are read in name order."""
    import glob
    import os
    import re
    from . import _gpkg, da
    from .analysis import _out
    from ._reference import reference_of
    kind = str(in_file_type).upper()
    if kind not in ("XYZ", "GENERATE"):
        _refuse("ASCII3DToFeatureClass", "in_file_type", in_file_type, "its column order is not given")
    geometry = str(out_geometry_type).upper()
    if geometry not in ("POINT", "POLYLINE", "POLYGON"):
        _refuse("ASCII3DToFeatureClass", "out_geometry_type", out_geometry_type, "its tiling is not specified")
    comma = str(decimal_separator or "DECIMAL_POINT").upper() == "DECIMAL_COMMA"
    zf = float(z_factor if z_factor is not None else 1)
    files = []
    for item in (input if isinstance(input, (list, tuple)) else [input]):
        if os.path.isdir(item):
            if not file_suffix:
                raise ExecuteError("ERROR 000735: a folder input needs file_suffix.")
            files += sorted(glob.glob(os.path.join(item, f"*.{str(file_suffix).lstrip('.')}")))
        else:
            files.append(item)

    def numbers(text):
        if comma:
            parts = re.split(r"[\s;]+", text.strip())
            parts = [p.replace(",", ".") for p in parts]
        else:
            parts = re.split(r"[\s,]+", text.strip())
        out = []
        for p in parts:
            try:
                out.append(float(p))
            except ValueError:
                break
        return out

    features = []
    for path in files:
        with open(path, encoding="utf-8") as read:
            rows = [r for r in read.read().splitlines() if r.strip()]
        if kind == "XYZ":
            coordinates = [n[:3] for n in (numbers(r) for r in rows) if len(n) >= 3]
            if geometry == "POINT":
                features += [[c] for c in coordinates]
            else:
                features.append(coordinates)
        else:
            block = None
            for r in rows:
                if r.strip().upper() == "END":
                    if block:
                        features.append(block)
                    block = None
                    continue
                n = numbers(r)
                if geometry == "POINT" and len(n) >= 4:
                    features.append([n[1:4]])
                elif block is None:
                    block = []
                elif len(n) >= 3:
                    block.append(n[:3])
    reference = reference_of(input_coordinate_system)
    out = _out(out_feature_class)
    shape = {"POINT": "Point", "POLYLINE": "Polyline", "POLYGON": "Polygon"}[geometry]
    _gpkg.create(out, shape, reference.factoryCode if reference else 0, [], True, False)
    with da.InsertCursor(out, ["SHAPE@"]) as written:
        for coordinates in features:
            scaled = [(x, y, z * zf) for x, y, z in coordinates]
            if geometry == "POINT":
                written.insertRow((_shapely.Point(*scaled[0]),))
            elif geometry == "POLYLINE":
                written.insertRow((_shapely.MultiLineString([scaled]),))
            else:
                written.insertRow((_shapely.MultiPolygon([_shapely.Polygon(scaled)]),))
    return Result(out)


_HEIGHT_FIELDS = ("Shape.Z", "Spot", "Z", "Z_Value", "Height", "Elev", "Elevation", "Contour")


def ConstructSightLines(in_observer_points, in_target_features, out_line_feature_class, observer_height_field=None,
                        target_height_field=None, join_field=None, sample_distance=None,
                        output_the_direction="NOT_OUTPUT_THE_DIRECTION", sampling_method="2D_DISTANCE"):
    """A sight line from each observer point to each target point — only to targets sharing its ``join_field`` value
    when one is given — with ``OID_OBSERV``, ``OID_TARGET`` and ``DIST_ALONG``, and under ``OUTPUT_THE_DIRECTION``
    ``AZIMUTH`` and ``VERT_ANGLE``, as documented. Heights come from the field named, by default the first of
    Shape.Z, Spot, Z, Z_Value, Height, Elev, Elevation and Contour present (Shape.Z being the shapes' own z); ``<None>``
    gives none. With heights on both sides the lines are 3D.

    Refused: line and polygon targets, whose sampling along the perimeter the page does not fix (where it starts, and
    whether the end is sampled).

    INFERRED: DIST_ALONG is 0 for a point target; AZIMUTH is in degrees clockwise from north and VERT_ANGLE in degrees
    above the horizontal from observer to target; a null height leaves the line out."""
    from . import _gpkg, da
    from .analysis import _out
    o_columns, o_kind, srid, o_z, _ = _z_class(in_observer_points, "ConstructSightLines")
    t_columns, t_kind, _, t_z, _ = _z_class(in_target_features, "ConstructSightLines")
    if t_kind not in ("Point", "Multipoint"):
        _refuse("ConstructSightLines", "in_target_features", t_kind, "the perimeter sampling of line and polygon targets "
                "is not fixed by the documentation")

    def source(given, columns, has_z):
        names = {c.name.lower(): c.name for c in columns}
        if given in (None, "", "#"):
            for candidate in _HEIGHT_FIELDS:
                if candidate == "Shape.Z" and has_z:
                    return "SHAPE"
                if candidate.lower() in names:
                    return names[candidate.lower()]
            return None
        if str(given).upper() in ("<NONE>", "NONE"):
            return None
        if str(given).upper() in ("SHAPE.Z", "SHAPE"):
            return "SHAPE" if has_z else None
        return names.get(str(given).lower())

    o_height, t_height = source(observer_height_field, o_columns, o_z), source(target_height_field, t_columns, t_z)
    three_d = o_height is not None and t_height is not None
    join = str(join_field) if join_field not in (None, "", "#") else None

    def read(path, height, columns):
        names = [c.name for c in columns]
        fields = ["OID@", "SHAPE@"] + ([height] if height not in (None, "SHAPE") else []) + \
                 ([join] if join and join in names else [])
        out = []
        with da.SearchCursor(path, fields) as rows:
            for row in rows:
                if row[1] is None:
                    continue
                values = dict(zip(fields, row))
                for p in _shapely.get_coordinates(row[1]._shape, include_z=bool(_shapely.has_z(row[1]._shape))):
                    z = (p[2] if len(p) > 2 else None) if height == "SHAPE" else values.get(height)
                    out.append((row[0], float(p[0]), float(p[1]), z, values.get(join)))
        return out

    observers = read(in_observer_points, o_height, o_columns)
    targets = read(in_target_features, t_height, t_columns)
    direction = str(output_the_direction or "").upper() == "OUTPUT_THE_DIRECTION"
    fields = [_gpkg.Column("OID_OBSERV", "Integer"), _gpkg.Column("OID_TARGET", "Integer"), _gpkg.Column("DIST_ALONG", "Double")]
    if direction:
        fields += [_gpkg.Column("AZIMUTH", "Double"), _gpkg.Column("VERT_ANGLE", "Double")]
    out = _out(out_line_feature_class)
    _gpkg.create(out, "Polyline", srid, fields, three_d, False)
    with da.InsertCursor(out, [*[c.name for c in fields], "SHAPE@"]) as written:
        for o_oid, ox, oy, oz, o_key in observers:
            for t_oid, tx, ty, tz, t_key in targets:
                if join and o_key != t_key:
                    continue
                if three_d and (oz is None or tz is None):
                    continue
                values = [o_oid, t_oid, 0.0]
                if direction:
                    azimuth = _math.degrees(_math.atan2(tx - ox, ty - oy)) % 360
                    flat = _math.hypot(tx - ox, ty - oy)
                    vertical = _math.degrees(_math.atan2(float(tz) - float(oz), flat)) if three_d else None
                    values += [azimuth, vertical]
                line = _shapely.LineString([(ox, oy, float(oz)), (tx, ty, float(tz))] if three_d else [(ox, oy), (tx, ty)])
                written.insertRow((*values, _shapely.MultiLineString([line])))
    return Result(out)


for _built in ("Simplify3DLine", "GeneratePointsAlong3DLines", "FeatureClassZToASCII", "ASCII3DToFeatureClass",
               "ConstructSightLines"):
    _REFUSED.pop(_built, None)
_REFUSED.update({
    "CalculateMissingZValues": "the page does not give its interpolation or extrapolation, nor how a placeholder z is "
                               "matched",
    "Intersect3DLines": "its horizontal tolerance, and how the XY and Z tolerance environments apply, are not given",
})


# ---- raster attribute tables


def Lookup(in_raster, lookup_field, out_raster):
    """arcpy.sa.Lookup, saved with its attribute table; see its docstring."""
    return _saved(sa.Lookup(in_raster, lookup_field), out_raster)


def CutFill(in_before_surface, in_after_surface, out_raster, z_factor=1):
    """arcpy.sa.CutFill, saved with its attribute table (Value, Count, Volume, Area); see its docstring."""
    return _saved(sa.CutFill(in_before_surface, in_after_surface, z_factor), out_raster)


for _built in ("Lookup", "CutFill"):
    _REFUSED.pop(_built, None)


# ---- TINs and LAS datasets (ADR-229): built in _tintools and _las, answered here under arcpy.ddd's names


from . import _las, _tintools  # noqa: E402

CreateTin = _tintools.CreateTin
EditTin = _tintools.EditTin
CopyTin = _tintools.CopyTin
DelineateTinDataArea = _tintools.DelineateTinDataArea
TinDomain = _tintools.TinDomain
TinEdge = _tintools.TinEdge
TinNode = _tintools.TinNode
TinTriangle = _tintools.TinTriangle
TinLine = _tintools.TinLine
TinPolygonTag = _tintools.TinPolygonTag
TinRaster = _tintools.TinRaster
TinContour = _tintools.TinContour
SurfaceContour = _tintools.SurfaceContour
SurfaceSlope = _tintools.SurfaceSlope
SurfaceAspect = _tintools.SurfaceAspect
SurfaceVolume = _tintools.SurfaceVolume
PolygonVolume = _tintools.PolygonVolume
TinPolygonVolume = _tintools.TinPolygonVolume
LasDatasetToTin = _las.LasDatasetToTin
LASToMultipoint = _las.LASToMultipoint
ExtractLas = _las.ExtractLas
ChangeLasClassCodes = _las.ChangeLasClassCodes
ClassifyLasByHeight = _las.ClassifyLasByHeight
SetLasClassCodesUsingFeatures = _las.SetLasClassCodesUsingFeatures
LasPointStatsByArea = _las.LasPointStatsByArea
PointFileInformation = _las.PointFileInformation
ClassifyLasGround = _equivalent("the progressive morphological filter", "Zhang et al. 2003")(_las.ClassifyLasGround)

from . import _lastools  # noqa: E402 — ADR-229's second batch

ColorizeLas = _lastools.ColorizeLas
ThinLas = _lastools.ThinLas
TileLas = _lastools.TileLas
ClassifyLasOverlap = _lastools.ClassifyLasOverlap
LocateLasPointsByProximity = _lastools.LocateLasPointsByProximity
LocateOutliers = _lastools.LocateOutliers
ExtractObjectsFromPointCloud = _lastools.ExtractObjectsFromPointCloud
ClassifyLasBuilding = _equivalent("plane detection by region growing with a smoothness constraint",
                                  "Rabbani, van den Heuvel and Vosselman 2006")(_lastools.ClassifyLasBuilding)

for _built in ("CreateTin", "EditTin", "CopyTin", "DelineateTinDataArea", "TinDomain", "TinEdge", "TinNode", "TinTriangle",
               "TinLine", "TinPolygonTag", "TinRaster", "TinContour", "SurfaceContour", "SurfaceSlope", "SurfaceAspect",
               "SurfaceVolume", "PolygonVolume", "TinPolygonVolume", "LasDatasetToTin", "LASToMultipoint", "ExtractLas",
               "ChangeLasClassCodes", "ClassifyLasByHeight", "SetLasClassCodesUsingFeatures", "LasPointStatsByArea",
               "PointFileInformation", "ClassifyLasGround", "ColorizeLas", "ThinLas", "TileLas", "ClassifyLasOverlap",
               "LocateLasPointsByProximity", "LocateOutliers", "ExtractObjectsFromPointCloud", "ClassifyLasBuilding"):
    _REFUSED.pop(_built, None)

# ---- terrain datasets (ADR-234): built in _terrain, answered here under arcpy.ddd's names

from . import _terrain  # noqa: E402

CreateTerrain = _terrain.CreateTerrain
AddTerrainPyramidLevel = _terrain.AddTerrainPyramidLevel
RemoveTerrainPyramidLevel = _terrain.RemoveTerrainPyramidLevel
ChangeTerrainReferenceScale = _terrain.ChangeTerrainReferenceScale
AddFeatureClassToTerrain = _terrain.AddFeatureClassToTerrain
RemoveFeatureClassFromTerrain = _terrain.RemoveFeatureClassFromTerrain
ChangeTerrainResolutionBounds = _terrain.ChangeTerrainResolutionBounds
BuildTerrain = _terrain.BuildTerrain
AppendTerrainPoints = _terrain.AppendTerrainPoints
ReplaceTerrainPoints = _terrain.ReplaceTerrainPoints
DeleteTerrainPoints = _terrain.DeleteTerrainPoints
RemoveTerrainPoints = _terrain.RemoveTerrainPoints
TerrainToTin = _terrain.TerrainToTin
TerrainToPoints = _terrain.TerrainToPoints
TerrainToRaster = _terrain.TerrainToRaster

for _built in ("CreateTerrain", "AddTerrainPyramidLevel", "RemoveTerrainPyramidLevel", "ChangeTerrainReferenceScale",
               "AddFeatureClassToTerrain", "RemoveFeatureClassFromTerrain", "ChangeTerrainResolutionBounds",
               "BuildTerrain", "AppendTerrainPoints", "ReplaceTerrainPoints", "DeleteTerrainPoints",
               "RemoveTerrainPoints", "TerrainToTin", "TerrainToPoints", "TerrainToRaster"):
    _REFUSED.pop(_built, None)
_REFUSED.update({
    "AddTerrainPoints": "its reference page could not be read: doc.esri.com/en/arcgis-pro/latest/tool-reference/"
                        "3d-analyst/add-terrain-points.html answered 404, and pro.arcgis.com's add-terrain-points.htm "
                        "redirects to it (checked 2026-10-30); AppendTerrainPoints, which grapy builds, adds points to a "
                        "terrain (ADR-234)",
    "BIMToTerrain": "it reads a TIN surface from an AutoCAD Civil 3D drawing or an IFC file through ArcGIS's BIM file "
                    "workspace, which grapy does not hold — Civil 3D's drawing format is Autodesk's and unpublished, and "
                    "conversion.BIMFileToGeodatabase is refused for the same model (ADR-234 §5.4)",
})
_REFUSED.update({name: "it trains or runs a deep learning model on point clouds, which grapy does not carry" for name in (
    "ClassifyPointCloudUsingTrainedModel", "DetectObjectsFromPointCloudUsingTrainedModel",
    "EvaluatePointCloudClassificationModel", "PreparePointCloudObjectDetectionTrainingData",
    "PreparePointCloudTrainingData", "TrainPointCloudClassificationModel", "TrainPointCloudObjectDetectionModel")})
_REFUSED.update({
    "DecimateTinNodes": "how the nodes kept are chosen is not documented, for either method (ADR-229 §5.7)",
    "LandXMLToTin": "it makes a constrained Delaunay TIN, as the page says, and grapy's TINs are conforming Delaunay — "
                    "scipy has no constrained triangulation (ADR-229 §5.3)",
    "RasterTin": "how it selects the raster's points within the z tolerance is not documented (ADR-229 §5.7)",
    "TINToMultipatch": "it writes multipatch features, a data model this server does not hold yet (ADR-224 group 2)",
    "LasBuildingMultipatch": "it writes multipatch features, a data model this server does not hold yet (ADR-224 group 2)",
    "TinSlope": "it is the legacy name of SurfaceSlope, which grapy builds; its own page was not read",
    "TinAspect": "it is the legacy name of SurfaceAspect, which grapy builds; its own page was not read",
    "SurfaceDifference": "its difference surface is built by constrained Delaunay triangulation, as documented, which "
                         "grapy does not have (ADR-229 §5.3)",
    "TinDifference": "it is the legacy name of SurfaceDifference, whose difference surface is built by constrained "
                     "Delaunay triangulation, which grapy does not have (ADR-229 §5.3)",
    "ClassifyLasNoise": "its page contradicts itself on the isolation limit (fewer than, or no more than, the point "
                        "limit), and its other methods' defaults are not given",
    "LasHeightMetrics": "how the ground surface the heights are measured from is built is not stated",
    "ExtractPowerLinesFromPointCloud": "its page names a catenary fit but not how points are grouped into spans and "
                                       "wires, how the curve is fitted, or how the wind correction is computed, and "
                                       "its SAG and WIND_ANGLE fields depend on all three (ADR-229 condition 4)",
    "ExtractRailsFromPointCloud": "it needs ArcGIS's deep learning frameworks installed, as its page says, and the page "
                                  "does not say what they detect or how the rails are traced from the crown points; "
                                  "grapy carries no deep learning model (ADR-229 condition 4)",
    "ExtractMeshFeaturesUsingPointCloud": "its input is an integrated mesh scene layer (I3S or a scene layer package) "
                                          "and its output multipatch features, data models this server does not hold "
                                          "yet (ADR-224 group 2)",
})


# ---- multipatch features (ADR-238): built in _mptools and _import3d, answered here under arcpy.ddd's names

from . import _import3d, _mptools  # noqa: E402

IsClosed3D = _mptools.IsClosed3D
MultiPatchFootprint = _mptools.MultiPatchFootprint
MinimumBoundingVolume = _mptools.MinimumBoundingVolume
TINToMultipatch = _mptools.TINToMultipatch
InterpolatePolyToPatch = _mptools.InterpolatePolyToPatch
ExtrudeBetween = _mptools.ExtrudeBetween
Intersect3DLineWithMultiPatch = _mptools.Intersect3DLineWithMultiPatch
Inside3D = _mptools.Inside3D
Intersect3D = _mptools.Intersect3D
Union3D = _mptools.Union3D
Difference3D = _mptools.Difference3D
Buffer3D = _mptools.Buffer3D
FenceDiagram = _mptools.FenceDiagram
LasBuildingMultipatch = _mptools.LasBuildingMultipatch
SetLasClassCodesUsingRaster = _mptools.SetLasClassCodesUsingRaster
SunShadowVolume = _mptools.SunShadowVolume
SunShadowFrequency = _mptools.SunShadowFrequency
Import3DFiles = _import3d.Import3DFiles

for _built in ("IsClosed3D", "MultiPatchFootprint", "MinimumBoundingVolume", "TINToMultipatch", "InterpolatePolyToPatch",
               "ExtrudeBetween", "Intersect3DLineWithMultiPatch", "Inside3D", "Intersect3D", "Union3D", "Difference3D",
               "Buffer3D", "FenceDiagram", "LasBuildingMultipatch", "SetLasClassCodesUsingRaster", "SunShadowVolume",
               "SunShadowFrequency", "Import3DFiles"):
    _REFUSED.pop(_built, None)
_REFUSED.update({
    "EncloseMultiPatch": "its page says only that the closed result drapes along the input at a grid resolution and "
                         "hides overhangs, not how the grid samples the input nor how the enclosing surface is built "
                         "from it (ADR-238)",
    "ExtractLOD2Buildings": "its page names three levels of detail, a smoothness level and an accuracy, but not how roof "
                            "planes are found in the height source, how they are generalised, or how the walls meet "
                            "them (ADR-238)",
    "ExtractMultipatchFromMesh": "its input is an integrated mesh scene layer or package (I3S, published as OGC "
                                 "17-014r9), and the page does not say which of the mesh's levels of detail is clipped "
                                 "-- a scene layer holds the mesh at every level — nor how a face crossing the "
                                 "footprint is cut (ADR-238)",
    "ExtractMeshFeaturesUsingPointCloud": "its input is an integrated mesh scene layer or package (I3S, OGC 17-014r9), "
                                          "and the page does not say how the mesh's triangles are subdivided down to "
                                          "the maximum area, which decides the output's triangles, nor which level of "
                                          "detail is read (ADR-238)",
})


def __getattr__(name):
    if name.startswith("_"):
        raise AttributeError(name)
    why = _REFUSED.get(name)

    def missing(*_, **__):
        raise NotBuiltError(f"arcpy.ddd.{name} is not built in grapy" + (f": {why}." if why else " yet."))

    return missing


# ---- SkylineBarrier on ADR-238's multipatch features (D-329), built in _mpmore
from . import _mpmore  # noqa: E402

SkylineBarrier = _mpmore.SkylineBarrier


# ---- ADR-244: the visibility, surface, barrier, footprint and profile tools built as equivalents
from . import _surfaces3d  # noqa: E402


@_equivalent(*_surfaces3d.ObserverPoints.__grapy_equivalent__[:-1].rsplit(" (", 1))
def ObserverPoints(in_raster, in_observer_point_features, out_raster, z_factor=1, curvature_correction="FLAT_EARTH",
                   refractivity_coefficient=0.13, out_agl_raster=None):
    """EQUIVALENT: arcpy.sa.ObserverPoints, saved; see its docstring."""
    return _saved(sa.ObserverPoints(in_raster, in_observer_point_features, z_factor, curvature_correction,
                                    refractivity_coefficient, out_agl_raster), out_raster)


@_equivalent(*_surfaces3d.Viewshed2.__grapy_equivalent__[:-1].rsplit(" (", 1))
def Viewshed2(in_raster, in_observer_features, out_raster, out_agl_raster=None, analysis_type="FREQUENCY",
              vertical_error=0, out_observer_region_relationship_table=None, refractivity_coefficient=0.13,
              surface_offset=0, observer_elevation=None, observer_offset=1, inner_radius=0, inner_radius_is_3d="GROUND",
              outer_radius=None, outer_radius_is_3d="GROUND", horizontal_start_angle=0, horizontal_end_angle=360,
              vertical_upper_angle=90, vertical_lower_angle=-90, analysis_method="ALL_SIGHTLINES",
              analysis_target_device="GPU_THEN_CPU"):
    """EQUIVALENT: arcpy.sa.Viewshed2, saved; see its docstring."""
    return _saved(sa.Viewshed2(in_raster, in_observer_features, out_agl_raster, analysis_type, vertical_error,
                               out_observer_region_relationship_table, refractivity_coefficient, surface_offset,
                               observer_elevation, observer_offset, inner_radius, inner_radius_is_3d, outer_radius,
                               outer_radius_is_3d, horizontal_start_angle, horizontal_end_angle, vertical_upper_angle,
                               vertical_lower_angle, analysis_method, analysis_target_device), out_raster)


@_equivalent(*_surfaces3d.SurfaceParameters.__grapy_equivalent__[:-1].rsplit(" (", 1))
def SurfaceParameters(in_raster, out_raster, parameter_type="SLOPE", local_surface_type="QUADRATIC",
                      neighborhood_distance=None, use_adaptive_neighborhood="FIXED_NEIGHBORHOOD", z_unit="METER",
                      output_slope_measurement="DEGREE", project_geodesic_azimuths="GEODESIC_AZIMUTHS",
                      use_equatorial_aspect="NORTH_POLE_ASPECT", in_analysis_mask=None):
    """EQUIVALENT: arcpy.sa.SurfaceParameters, saved; see its docstring."""
    return _saved(sa.SurfaceParameters(in_raster, parameter_type, local_surface_type, neighborhood_distance,
                                       use_adaptive_neighborhood, z_unit, output_slope_measurement,
                                       project_geodesic_azimuths, use_equatorial_aspect, in_analysis_mask), out_raster)


@_equivalent(*_surfaces3d.SplineWithBarriers.__grapy_equivalent__[:-1].rsplit(" (", 1))
def SplineWithBarriers(Input_point_features, Z_value_field, Input_barrier_features=None, Output_cell_size=None,
                       Output_raster=None, Smoothing_Factor=0.0):
    """EQUIVALENT: arcpy.sa.SplineWithBarriers, saved; see its docstring."""
    return _saved(sa.SplineWithBarriers(Input_point_features, Z_value_field, Input_barrier_features, Output_cell_size,
                                        Smoothing_Factor), Output_raster)


ContourWithBarriers = _surfaces3d.ContourWithBarriers
RegularizeBuildingFootprint = _surfaces3d.RegularizeBuildingFootprint
RegularizeAdjacentBuildingFootprint = _surfaces3d.RegularizeAdjacentBuildingFootprint
StackProfile = _surfaces3d.StackProfile
for _built in ("ObserverPoints", "Viewshed2", "SurfaceParameters", "SplineWithBarriers", "ContourWithBarriers",
               "RegularizeBuildingFootprint", "RegularizeAdjacentBuildingFootprint", "StackProfile"):
    _REFUSED.pop(_built, None)
_REFUSED["TopoToRaster"] = _surfaces3d.TOPO_TO_RASTER
_REFUSED["TopoToRasterByFile"] = _surfaces3d.TOPO_TO_RASTER_BY_FILE
