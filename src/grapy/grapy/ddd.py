"""arcpy.ddd — 3D Analyst's tools grapy builds, by their documented names and parameters (ADR-224).

3D Analyst's raster tools are geoprocessing tools: each writes its output to a path and answers a Result, where
Spatial Analyst's answer a Raster. Where a ddd tool is the same computation as an ``arcpy.sa`` function, it calls that
function and saves what it answers; the parameter order and defaults are the ddd page's.

Not built, each refused by name with its reason in ``__getattr__`` below:

- TIN, terrain, LAS dataset, point cloud and multipatch tools: data models this server does not hold yet (ADR-224
  group 2).
- Tools whose answer is a Z value on a feature, or which read one: grapy's feature classes are two-dimensional today.
- Tools whose algorithm the documentation describes without specifying (Kriging, Spline, Natural Neighbor, Topo to
  Raster, Viewshed, Visibility …), and tools whose answer lives in a raster attribute table, which grapy's rasters do
  not carry.
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



# ---- refused, by name, with the reason

_GROUP_2 = "needs a data model this server does not hold yet (ADR-224 group 2)"
_NO_Z = "it reads or writes z values on features, and grapy's feature classes are two-dimensional today"
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


def __getattr__(name):
    if name.startswith("_"):
        raise AttributeError(name)
    why = _REFUSED.get(name)

    def missing(*_, **__):
        raise NotBuiltError(f"arcpy.ddd.{name} is not built in grapy" + (f": {why}." if why else " yet."))

    return missing
