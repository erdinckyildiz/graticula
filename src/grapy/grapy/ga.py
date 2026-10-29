"""arcpy.ga — the Geostatistical Analyst names grapy builds, by their documented names and parameters (ADR-224).

Most of Geostatistical Analyst answers a *geostatistical layer*: a model object — method, parameters, the data it
was fitted to — that a map draws and other ga tools read. grapy has no such object. So:

- A tool that can also write a raster (``out_raster``) is built for that path, and ``out_ga_layer`` is refused when
  given: a parameter grapy does not honour is refused, never ignored (ADR-224 §5.4). Pass ``""`` for it.
- A tool whose only output, or whose input, is a geostatistical layer is refused with that reason.
- Kriging in every form, and everything that fits by an unpublished optimisation or draws random numbers, is refused
  with its reason.

Where the computation is the same as a 3D Analyst tool's, the ``ddd`` tool is called: ``IDW`` is ``ddd.Idw`` with a
variable radius, ``GlobalPolynomialInterpolation`` is ``ddd.Trend``. Their grid and cell size are the same documented
rule — the shorter side of the points' extent divided by 250 — and their own INFERRED choices carry over.

INFERRED: a tool's Result lists its outputs in the order of its parameters, ``out_ga_layer`` first (empty, since
grapy writes none), then ``out_raster``.
"""
from __future__ import annotations

import numpy as np

from ._result import Result
from ._runtime import NotBuiltError


def _refuse(tool, parameter, value, why):
    raise NotBuiltError(f"arcpy.ga.{tool}: {parameter} '{value}' is not built in grapy — {why}.")


def _given(value):
    return value not in (None, "", "#")


def _keyword(value):
    return str(value).strip().upper().replace(" ", "_")


# ---- search neighbourhoods: the documented constructors, holding what they are given


class SearchNeighborhoodStandard:
    """``SearchNeighborhoodStandard({majorSemiaxis}, {minorSemiaxis}, {angle}, {nbrMax}, {nbrMin}, {sectorType})``: an
    ellipse, the most and fewest neighbours to use within it, and its sectors."""

    def __init__(self, majorSemiaxis=None, minorSemiaxis=None, angle=None, nbrMax=None, nbrMin=None, sectorType=None):
        self.majorSemiaxis, self.minorSemiaxis, self.angle = majorSemiaxis, minorSemiaxis, angle
        self.nbrMax, self.nbrMin, self.sectorType = nbrMax, nbrMin, sectorType


class SearchNeighborhoodSmooth:
    """``SearchNeighborhoodSmooth({majorSemiaxis}, {minorSemiaxis}, {angle}, {smoothFactor})``."""

    def __init__(self, majorSemiaxis=None, minorSemiaxis=None, angle=None, smoothFactor=None):
        self.majorSemiaxis, self.minorSemiaxis, self.angle, self.smoothFactor = majorSemiaxis, minorSemiaxis, angle, smoothFactor


class SearchNeighborhoodStandardCircular:
    """``SearchNeighborhoodStandardCircular({radius}, {angle}, {nbrMax}, {nbrMin}, {sectorType})``; the angle "will
    only affect the angle of the sectors"."""

    def __init__(self, radius=None, angle=None, nbrMax=None, nbrMin=None, sectorType=None):
        self.radius, self.angle, self.nbrMax, self.nbrMin, self.sectorType = radius, angle, nbrMax, nbrMin, sectorType


class SearchNeighborhoodSmoothCircular:
    """``SearchNeighborhoodSmoothCircular({radius}, {smoothFactor})``."""

    def __init__(self, radius=None, smoothFactor=None):
        self.radius, self.smoothFactor = radius, smoothFactor


class SearchNeighborhoodStandard3D:
    """``SearchNeighborhoodStandard3D({radius}, {nbrMax}, {nbrMin}, {sectorType})``."""

    def __init__(self, radius=None, nbrMax=None, nbrMin=None, sectorType=None):
        self.radius, self.nbrMax, self.nbrMin, self.sectorType = radius, nbrMax, nbrMin, sectorType


def _circle(tool, neighborhood):
    """The radius and the most and fewest neighbours of a one-sector circle, or a refusal naming what is not built."""
    if not _given(neighborhood):
        _refuse(tool, "search_neighborhood", "(default)", "its default is derived from the data by a rule the "
                "documentation does not give; pass a SearchNeighborhoodStandardCircular")
    if isinstance(neighborhood, (SearchNeighborhoodSmooth, SearchNeighborhoodSmoothCircular)):
        _refuse(tool, "search_neighborhood", type(neighborhood).__name__, "the sigmoidal weight between the inner and "
                "outer ellipses is not given as a formula")
    if isinstance(neighborhood, SearchNeighborhoodStandardCircular):
        radius = neighborhood.radius
    elif isinstance(neighborhood, SearchNeighborhoodStandard):
        if neighborhood.majorSemiaxis != neighborhood.minorSemiaxis:
            _refuse(tool, "search_neighborhood", "an ellipse", "the direction its angle is measured in is not documented; "
                    "an ellipse whose semiaxes are equal is a circle and is built")
        radius = neighborhood.majorSemiaxis
    else:
        _refuse(tool, "search_neighborhood", type(neighborhood).__name__, "not a two-dimensional standard neighbourhood")
    sector = _keyword(neighborhood.sectorType) if _given(neighborhood.sectorType) else "ONE_SECTOR"
    if sector != "ONE_SECTOR":
        _refuse(tool, "sectorType", sector, "which sector a point on a sector's edge belongs to is not documented")
    for name in ("radius", "nbrMax", "nbrMin"):
        value = radius if name == "radius" else getattr(neighborhood, name)
        if not _given(value):
            _refuse(tool, name, "(empty)", "its default is derived from the data by a rule the documentation does not give")
    return float(radius), int(neighborhood.nbrMax), int(neighborhood.nbrMin)


def _no_layer(tool, out_ga_layer, out_raster):
    if _given(out_ga_layer):
        _refuse(tool, "out_ga_layer", out_ga_layer, "grapy has no geostatistical layer; pass '' and an out_raster")
    if not _given(out_raster):
        _refuse(tool, "out_raster", "(empty)", "without it the only output is a geostatistical layer, which grapy does "
                "not have")


def IDW(in_features, z_field, out_ga_layer=None, out_raster=None, cell_size=None, power=None, search_neighborhood=None,
        weight_field=None):
    """Inverse distance weighted interpolation to a raster. From "How inverse distance weighted interpolation works":
    "weights are proportional to the inverse of the distance … raised to the power value p", the prediction is their
    weighted average of the neighbours' values, and IDW "is an exact interpolator". The neighbours are the nearest
    ``nbrMax`` points within the search circle.

    Built for a one-sector circle (SearchNeighborhoodStandardCircular, or a SearchNeighborhoodStandard whose semiaxes
    are equal) with its radius, nbrMax and nbrMin given, and an explicit power. Refused: an omitted power (it "will be
    estimated" by minimising cross-validation error, a search the documentation does not specify), an omitted
    neighbourhood (its defaults come from the data), smooth neighbourhoods, ellipses, sectors, ``weight_field`` (how a
    weight enters is not given), and any cell that finds fewer than ``max(nbrMin, 1)`` points within the circle —
    what the tool does then is not documented.

    The computation is ``ddd.Idw`` with a variable radius of nbrMax points and the circle's radius as its maximum
    distance; its INFERRED choices (the grid's corner, a point at a cell centre, coincident points refused) apply.
    """
    _no_layer("IDW", out_ga_layer, out_raster)
    if not _given(power):
        _refuse("IDW", "power", "(empty)", "the power is then estimated by a cross-validation search the documentation "
                "does not specify")
    if float(power) < 1:
        raise ValueError("arcpy.ga.IDW: the power must be 1 or more.")
    if _given(weight_field):
        _refuse("IDW", "weight_field", weight_field, "how a weight enters the prediction is not documented")
    radius, most, fewest = _circle("IDW", search_neighborhood)
    from . import ddd, sa
    xs, ys, _ = sa._points(in_features, z_field, "IDW")
    grid = sa._point_grid(in_features, cell_size, xs, ys, "IDW")
    cx, cy = grid.centres()
    gx, gy = np.meshgrid(cx, cy)
    found = (np.hypot(gx[..., None] - xs, gy[..., None] - ys) <= radius).sum(axis=-1)
    if (found < max(fewest, 1)).any():
        _refuse("IDW", "search_neighborhood", f"radius {radius:g}, nbrMin {fewest}", f"{int((found < max(fewest, 1)).sum())} "
                "cells find fewer neighbours than that within the circle, and what the tool does then is not documented")
    result = ddd.Idw(in_features, z_field, out_raster, grid.width, power, f"VARIABLE {most} {radius!r}")
    return Result("", result.getOutput(0))


def GlobalPolynomialInterpolation(in_features, z_field, out_ga_layer=None, out_raster=None, cell_size=None, power=1,
                                  weight_field=None):
    """A polynomial of order ``power`` fitted to the points by least squares and evaluated at each cell centre, from
    "How global polynomial interpolation works": "the surface … is obtained by using a least-squares regression fit",
    first order a plane, up to order 10.

    The computation is ``ddd.Trend``: every term xⁱyʲ with i + j ≤ power, as How Trend works writes it.
    Refused: ``weight_field`` (how a weight enters the fit is not documented). INFERRED: power defaults to 1, the first
    order; the page gives no default. INFERRED: the terms are Trend's, which the global polynomial page does not list.
    """
    _no_layer("GlobalPolynomialInterpolation", out_ga_layer, out_raster)
    if _given(weight_field):
        _refuse("GlobalPolynomialInterpolation", "weight_field", weight_field, "how a weight enters the fit is not "
                "documented")
    order = int(power) if _given(power) else 1
    if not 1 <= order <= 10:
        raise ValueError("arcpy.ga.GlobalPolynomialInterpolation: the order is from 1 to 10.")
    from . import ddd
    result = ddd.Trend(in_features, z_field, out_raster, cell_size, order)
    return Result("", result.getOutput(0))


_LAYER = "it reads or writes a geostatistical layer, a model object grapy does not have"
_KRIGING = "kriging's semivariogram fitting is an optimisation the documentation does not specify"
_RANDOM = "its answer is drawn at random"
_UNSPECIFIED = "its kernel or fitting is named but not given as a formula"

_REFUSED = {name: _LAYER for name in (
    "ArealInterpolationLayerToPolygons", "CompareGeostatisticalLayers", "CrossValidation", "CrossValidationResult",
    "DensifySamplingNetwork", "GACalculateZValue", "GACreateGeostatisticalLayer", "GAGetModelParameter",
    "GALayer3DToMultidimensionalRaster", "GALayer3DToNetCDF", "GALayerToContour", "GALayerToGrid", "GALayerToPoints",
    "GALayerToRasters", "GANeighborhoodSelection", "GASemivariogramSensitivity", "GASetModelParameter",
    "GeostatisticalDatasets", "ExploratoryInterpolation")}
_REFUSED.update({name: _KRIGING for name in (
    "EmpiricalBayesianKriging", "EmpiricalBayesianKriging3D", "EBKRegressionPrediction", "GAMovingWindowKriging")})
_REFUSED.update({name: _RANDOM for name in (
    "CreateSpatiallyBalancedPoints", "SubsetFeatures", "GaussianGeostatisticalSimulations")})
_REFUSED.update({name: _UNSPECIFIED for name in (
    "LocalPolynomialInterpolation", "RadialBasisFunctions", "KernelInterpolationWithBarriers",
    "DiffusionInterpolationWithBarriers")})
_REFUSED.update({name: "it interpolates in three dimensions, and grapy's feature classes hold no z values yet"
                 for name in ("IDW3D", "NearestNeighbor3D")})
_REFUSED["GenerateSubsetPolygons"] = "its curve through the points and its segmentation are described, not specified"
_REFUSED["ExtractValuesToTable"] = "the output table's fields are not named in the documentation"
_REFUSED["DirectionalTrend"] = "its output is a chart on a layer in the active map, and the tool cannot run without one"
_REFUSED.update({name: "it is a conditioned simulation, and its answer is drawn at random"
                 for name in ("GaussianGeostatisticalSimulations",)})


def __getattr__(name):
    if name.startswith("_"):
        raise AttributeError(name)

    def missing(*_, **__):
        why = _REFUSED.get(name)
        raise NotBuiltError(f"arcpy.ga.{name} is not built in grapy" + (f" — {why}." if why else " yet."))

    return missing
