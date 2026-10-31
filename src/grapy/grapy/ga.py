"""arcpy.ga — Geostatistical Analyst in grapy, by its documented names and parameters (ADR-224, ADR-233).

Most of Geostatistical Analyst reads or writes a *geostatistical layer*: an interpolation method, its parameters as
fitted or given, a search neighbourhood, and its data. grapy holds one as its own documented JSON (``_geostat``,
ADR-233 §5.2): Esri's layer files and model XML are not a published format and are not reproduced, so a model a
script brings from ArcGIS Pro is refused by name and is written once in grapy's form.

Built:

- the interpolators that make a layer and/or a raster: ``IDW``, ``GlobalPolynomialInterpolation``, and as equivalents
  (ADR-224 §5.6) ``LocalPolynomialInterpolation`` and ``RadialBasisFunctions``;
- kriging — ordinary, simple, universal — and ordinary cokriging, made by ``GACreateGeostatisticalLayer`` from a model
  (a layer, or grapy's JSON with no data), with the semivariogram fitted to the empirical one where a parameter is
  not given;
- what reads a layer: ``GALayerToGrid``, ``GALayerToRasters``, ``GALayerToPoints``, ``GALayerToContour``,
  ``GACalculateZValue``, ``CrossValidation`` and its ``CrossValidationResult``, ``GAGetModelParameter``,
  ``GASetModelParameter``, ``GeostatisticalDatasets``, ``CompareGeostatisticalLayers``;
- ``GANeighborhoodSelection``, and ``GAMovingWindowKriging`` as an equivalent.

Refused, each with its own reason in ``_REFUSED``: empirical Bayesian kriging and what is built on it, the tools whose
answer is drawn at random, the barrier interpolators, and the rest named there.

INFERRED: a tool's Result lists its outputs in the order of its parameters, ``out_ga_layer`` first, then
``out_raster``; an output not asked for is ``""``.
"""
from __future__ import annotations

import math
import re

import numpy as np

from . import _geostat
from ._geostat import GALayer
from ._result import Result
from ._runtime import NotBuiltError, equivalent as _equivalent


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


def _required_neighbourhood(tool, value):
    if not _given(value):
        _refuse(tool, "search_neighborhood", "(default)", "its default is derived from the data by a rule the "
                "documentation does not give; pass a SearchNeighborhoodStandard or SearchNeighborhoodStandardCircular")
    return _geostat.neighbourhood(value, tool)


# ---- grids and outputs


def _grid(layer, cell_size, tool):
    """The output grid over the layer's points, its top-left corner at their extent's, at the cell size given or the
    documented default: the shorter side of the extent divided by 250."""
    from . import sa
    from ._raster import _Grid, _refuse_environment
    _refuse_environment(f"arcpy.ga.{tool}")
    left, bottom, right, top = layer.extent()
    cell = sa._cell_size(cell_size, tool)
    if cell is None:
        shorter = min(right - left, top - bottom)
        if shorter <= 0:
            _refuse(tool, "cell_size", "(empty)", "the points' extent has no width or height to divide by 250")
        cell = shorter / 250.0
    columns = max(1, int(math.ceil((right - left) / cell)))
    rows = max(1, int(math.ceil((top - bottom) / cell)))
    return _Grid(left, top, cell, cell, rows, columns, layer.srid)


def _raster(values, grid):
    from ._raster import Raster
    mask = np.isnan(values)
    return Raster._of(np.where(mask, 0, values).astype(np.float32), mask, grid)


def _surface(layer, grid, block=(1, 1)):
    xs, ys = grid.centres()
    return layer.grid_values(xs, ys, block, (grid.width, grid.height))


def _make(tool, method, in_features, z_field, parameters, neighbourhood, out_ga_layer, out_raster, cell_size):
    if not _given(out_ga_layer) and not _given(out_raster):
        raise ValueError(f"arcpy.ga.{tool}: give out_ga_layer, out_raster or both.")
    data, srid = _geostat.read_dataset(in_features, z_field, tool)
    layer = GALayer(method, parameters, neighbourhood, [data], srid)
    made = _geostat.save_layer(out_ga_layer, layer) if _given(out_ga_layer) else ""
    written = ""
    if _given(out_raster):
        grid = _grid(layer, cell_size, tool)
        written = _raster(_surface(layer, grid)[0], grid).save(out_raster)
    return Result(made, written)


# ---- the interpolators


def IDW(in_features, z_field, out_ga_layer=None, out_raster=None, cell_size=None, power=None, search_neighborhood=None,
        weight_field=None):
    """Inverse distance weighted interpolation to a layer, a raster or both. From "How inverse distance weighted
    interpolation works": "weights are proportional to the inverse of the distance … raised to the power value p", the
    prediction is their weighted average of the neighbours' values, and IDW "is an exact interpolator". The
    neighbours are the nearest ``nbrMax`` points within the ellipse, and when fewer than ``nbrMin`` are, the nearest
    outside it up to ``nbrMin``, as GA Neighborhood Selection documents.

    Refused: an omitted power (it "will be estimated" by minimising cross-validation error, a search the documentation
    does not specify), an omitted neighbourhood (its defaults come from the data), smooth neighbourhoods, sectors, and
    ``weight_field`` (how a weight enters is not given). INFERRED: coincident points are refused; the grid's top-left
    corner is the points' extent's."""
    if not _given(power):
        _refuse("IDW", "power", "(empty)", "the power is then estimated by a cross-validation search the documentation "
                "does not specify")
    if float(power) < 1:
        raise ValueError("arcpy.ga.IDW: the power must be 1 or more.")
    if _given(weight_field):
        _refuse("IDW", "weight_field", weight_field, "how a weight enters the prediction is not documented")
    nbr = _required_neighbourhood("IDW", search_neighborhood)
    return _make("IDW", "IDW", in_features, z_field, {"power": float(power)}, nbr, out_ga_layer, out_raster, cell_size)


def GlobalPolynomialInterpolation(in_features, z_field, out_ga_layer=None, out_raster=None, cell_size=None, power=1,
                                  weight_field=None):
    """A polynomial of order ``power`` fitted to every point by least squares and evaluated at each cell centre, from
    "How global polynomial interpolation works": "the surface … is obtained by using a least-squares regression fit",
    first order a plane, up to order 10. Every term xⁱyʲ with i + j ≤ power, as How Trend works writes it.

    Refused: ``weight_field`` (how a weight enters the fit is not documented). INFERRED: power defaults to 1; the terms
    are Trend's, which the global polynomial page does not list."""
    if _given(weight_field):
        _refuse("GlobalPolynomialInterpolation", "weight_field", weight_field, "how a weight enters the fit is not "
                "documented")
    order = int(power) if _given(power) else 1
    if not 1 <= order <= 10:
        raise ValueError("arcpy.ga.GlobalPolynomialInterpolation: the order is from 1 to 10.")
    return _make("GlobalPolynomialInterpolation", "GLOBAL_POLYNOMIAL", in_features, z_field, {"power": order}, None,
                 out_ga_layer, out_raster, cell_size)


@_equivalent("local polynomial regression by kernel-weighted least squares",
             "Cleveland and Devlin 1988, JASA 83(403); Fan and Gijbels 1996, Local Polynomial Modelling, ch. 3; "
             "kernels of Silverman 1986, Table 3.1")
def LocalPolynomialInterpolation(in_features, z_field, out_ga_layer=None, out_raster=None, cell_size=None, power=None,
                                 search_neighborhood=None, kernel_function=None, bandwidth=None,
                                 use_condition_number=None, condition_number=None, weight_field=None,
                                 output_type=None):
    """EQUIVALENT: at each place, the polynomial of order ``power`` fitted to the neighbours within ``bandwidth`` by
    least squares weighted by the kernel K(d / bandwidth), and its value there (Fan and Gijbels 1996). The page names
    the kernels and calls the bandwidth "the maximum distance at which data points are used"; it gives no formula.

    Built kernels, compact on the bandwidth (Silverman 1986): ``CONSTANT`` 1, ``EPANECHNIKOV`` 1 − t², ``QUARTIC``
    (1 − t²)². Refused: ``EXPONENTIAL``, ``GAUSSIAN`` and ``POLYNOMIAL5``, whose shapes against the bandwidth are not
    given; an omitted power, kernel or bandwidth (no default is stated, and the bandwidth's is optimised by a search
    not given); ``USE_CONDITION_NUMBER`` (the spatial condition number is not defined); ``weight_field``; and
    ``output_type`` other than ``PREDICTION`` (the standard errors and condition number are not defined). A place
    with fewer neighbours than the polynomial has terms, or whose fit is singular, is NoData."""
    tool = "LocalPolynomialInterpolation"
    for name, value in (("power", power), ("kernel_function", kernel_function), ("bandwidth", bandwidth)):
        if not _given(value):
            _refuse(tool, name, "(empty)", "the page states no default, and the bandwidth's is optimised by a search "
                    "the documentation does not specify")
    kernel = _keyword(kernel_function)
    if kernel not in _geostat._KERNELS:
        _refuse(tool, "kernel_function", kernel, "its shape against the bandwidth is not given; CONSTANT, EPANECHNIKOV "
                "and QUARTIC are built")
    if _given(use_condition_number) and _keyword(use_condition_number) not in ("NO_USE_CONDITION_NUMBER", "FALSE"):
        _refuse(tool, "use_condition_number", use_condition_number, "the spatial condition number is not defined")
    if _given(weight_field):
        _refuse(tool, "weight_field", weight_field, "how a weight enters the fit is not documented")
    if _given(output_type) and _keyword(output_type) != "PREDICTION":
        _refuse(tool, "output_type", output_type, "its standard errors and condition number are not defined")
    if int(power) < 0 or float(bandwidth) <= 0:
        raise ValueError(f"arcpy.ga.{tool}: the power is 0 or more and the bandwidth positive.")
    nbr = _required_neighbourhood(tool, search_neighborhood)
    return _make(tool, "LOCAL_POLYNOMIAL", in_features, z_field,
                 {"power": int(power), "kernel": kernel, "bandwidth": float(bandwidth)}, nbr, out_ga_layer, out_raster,
                 cell_size)


_BASES = ("THIN_PLATE_SPLINE", "SPLINE_WITH_TENSION", "COMPLETELY_REGULARIZED_SPLINE", "MULTIQUADRIC_FUNCTION",
          "INVERSE_MULTIQUADRIC_FUNCTION")


@_equivalent("exact radial basis function interpolation in the search neighbourhood",
             "Franke 1982, Mathematics of Computation 38(157); Hardy 1971, JGR 76(8); Mitášová and Mitáš 1993, "
             "Mathematical Geology 25(6)")
def RadialBasisFunctions(in_features, z_field, out_ga_layer=None, out_raster=None, cell_size=None,
                         search_neighborhood=None, radial_basis_functions=None, small_scale_parameter=None):
    """EQUIVALENT: s(q) = Σ λᵢ φ(|q − xᵢ|) + trend through the neighbours exactly — the page says the surface "passes
    through the input points exactly" and names the five functions. φ with σ the small-scale parameter: thin-plate
    spline r² ln r (Duchon; σ only rescales it, which a linear trend absorbs), multiquadric √(r² + σ²) and inverse
    multiquadric 1/√(r² + σ²) (Hardy 1971), the completely regularized spline −[ln(σr/2)² + E₁((σr/2)²) + C_E] and
    the spline with tension ln(σr/2) + K₀(σr) + C_E (Mitášová and Mitáš 1993).

    Refused: an omitted function (no default is stated) or parameter (its optimum minimises a cross-validation error
    by a search not given), an omitted neighbourhood. INFERRED: the thin-plate spline carries a linear trend, the
    others a constant."""
    tool = "RadialBasisFunctions"
    if not _given(radial_basis_functions):
        _refuse(tool, "radial_basis_functions", "(empty)", "the page states no default")
    kind = _keyword(radial_basis_functions)
    if kind not in _BASES:
        raise ValueError(f"arcpy.ga.{tool}: '{radial_basis_functions}' is not a radial basis function.")
    if not _given(small_scale_parameter):
        _refuse(tool, "small_scale_parameter", "(empty)", "its optimum minimises the root mean square prediction "
                "error by a search the documentation does not specify")
    if float(small_scale_parameter) <= 0 and kind != "THIN_PLATE_SPLINE":
        raise ValueError(f"arcpy.ga.{tool}: the small-scale parameter is positive.")
    nbr = _required_neighbourhood(tool, search_neighborhood)
    return _make(tool, "RADIAL_BASIS", in_features, z_field, {"kernel": kind, "parameter": float(small_scale_parameter)},
                 nbr, out_ga_layer, out_raster, cell_size)


# ---- datasets


class GeostatisticalDatasets:
    """``GeostatisticalDatasets(ga_model_source)``: the datasets and fields of a geostatistical layer, read and
    written as ``dataset1``, ``dataset1Field`` and, for cokriging, ``dataset2`` and ``dataset2Field``. Passed to
    GACreateGeostatisticalLayer as ``in_datasets``. A model with no data answers empty strings, as the class page
    says a model XML does."""

    def __init__(self, ga_model_source):
        layer = _geostat.open_layer(ga_model_source, "GeostatisticalDatasets", need_data=False)
        count = 2 if layer.method == "COKRIGING" else 1
        for k in range(count):
            d = layer.datasets[k] if k < len(layer.datasets) else {}
            setattr(self, f"dataset{k + 1}", d.get("dataset", ""))
            setattr(self, f"dataset{k + 1}Field", d.get("field", ""))
        if layer.method in ("IDW", "GLOBAL_POLYNOMIAL"):
            self.dataset1WeightField = ""

    def _pairs(self):
        out = []
        for k in (1, 2):
            dataset = getattr(self, f"dataset{k}", "")
            if dataset:
                out.append((dataset, getattr(self, f"dataset{k}Field", "")))
        if getattr(self, "dataset1WeightField", ""):
            _refuse("GeostatisticalDatasets", "dataset1WeightField", self.dataset1WeightField,
                    "how a weight enters the prediction is not documented")
        return out

    def __str__(self):
        return ";".join(f"{d} X=Shape Y=Shape F1={f}" for d, f in self._pairs())


# ---- making a layer from a model


_KINDS = ("ORDINARY", "SIMPLE", "UNIVERSAL")


def _fit_kriging(tool, parameters, datasets):
    """A kriging model's parameters with what is not given, or is marked auto, fitted to the data (ADR-233 §5.4)."""
    p = dict(parameters)
    kind = _keyword(p.get("kind", "ORDINARY"))
    if kind not in _KINDS:
        _refuse(tool, "kind", kind, "indicator, probability and disjunctive kriging need a threshold transformation "
                "and declustering the documentation does not specify; ORDINARY, SIMPLE and UNIVERSAL are built")
    for name in ("transformation", "declustering", "measurementError"):
        if _given(p.get(name)):
            _refuse(tool, name, p[name], "the normal score and Box-Cox back-transformations, declustering weights and "
                    "measurement error split are not given closely enough to reproduce")
    p["kind"] = kind
    model = dict(p.get("semivariogram") or {})
    model["model"] = _keyword(model.get("model", "SPHERICAL"))
    _geostat.shape(model["model"], 0.5, model.get("shape"))   # refuses J-Bessel and hole effect by name
    xs, ys, zs = (np.array(datasets[0][k], dtype=np.float64) for k in ("x", "y", "z"))
    residual = zs
    if kind == "SIMPLE":
        p["mean"] = float(p["mean"]) if _given(p.get("mean")) else float(zs.mean())
    if kind == "UNIVERSAL":
        drift = _keyword(p.get("drift", "LINEAR"))
        if drift not in ("LINEAR", "QUADRATIC"):
            raise ValueError(f"arcpy.ga.{tool}: universal kriging's drift is LINEAR or QUADRATIC.")
        p["drift"] = drift
        s = max(float(np.ptp(xs)), float(np.ptp(ys)), 1.0)
        u, v = (xs - xs.mean()) / s, (ys - ys.mean()) / s
        design = np.column_stack([np.ones_like(u), u, v] + ([u * u, u * v, v * v] if drift == "QUADRATIC" else []))
        residual = zs - design @ np.linalg.lstsq(design, zs, rcond=None)[0]
    lags = int(p.get("numberOfLags") or 12)
    lag = float(p["lagSize"]) if _given(p.get("lagSize")) else _geostat.default_lag(xs, ys, lags)
    p["numberOfLags"], p["lagSize"] = lags, lag
    auto = dict(model.get("auto") or {})
    if any(model.get(k) in (None, "") or auto.get(k) for k in ("range", "partialSill", "nugget")) or (
            model["model"] in ("STABLE", "K_BESSEL") and model.get("shape") in (None, "")):
        model = _geostat.fit({**model, "auto": auto}, xs, ys, residual, lag, lags)
    else:
        model["auto"] = {k: bool(auto.get(k)) for k in ("range", "partialSill", "nugget")}
        for k in ("range", "partialSill", "nugget"):
            model[k] = float(model[k])
    p["semivariogram"] = model
    return p


def _cokriging(tool, parameters, datasets):
    if len(datasets) != 2:
        _refuse(tool, "in_datasets", f"{len(datasets)} datasets", "cokriging is built for two, the primary and one "
                "secondary")
    models = (parameters or {}).get("semivariograms") or {}
    for role in ("primary", "secondary", "cross"):
        m = dict(models.get(role) or {})
        if any(m.get(k) in (None, "") for k in ("range", "partialSill")) or any(
                (m.get("auto") or {}).values()):
            _refuse(tool, f"semivariograms.{role}", "(fitted)", "how the cross-semivariogram is fitted together with the "
                    "two direct ones, and kept positive definite, is not given; give all three models' values")
        m["model"] = _keyword(m.get("model", "SPHERICAL"))
        _geostat.shape(m["model"], 0.5, m.get("shape"))
        models[role] = m
    return {"semivariograms": models}


@_equivalent("kriging and cokriging with the semivariogram fitted by weighted least squares",
             "Cressie 1993, Statistics for Spatial Data, §2.4, §3.2–3.4; Cressie 1985, Mathematical Geology 17(5); "
             "Isaaks and Srivastava 1989, An Introduction to Applied Geostatistics, ch. 12–17; models of Johnston et "
             "al. 2001, Appendix A, and Chilès and Delfiner 2012, §2.5")
def GACreateGeostatisticalLayer(in_ga_model_source, in_datasets, out_layer):
    """EQUIVALENT where it fits: a new layer from a model source and data. The model's method and parameters are kept;
    a kriging parameter not given, or marked auto (GASetModelParameter's ``@auto``), is fitted to the new data, as the
    page says the normal score transformation's are recalculated. The fit is Cressie's (1985) weighted least squares
    on the empirical semivariogram — Geostatistical Analyst's pages say only "weighted least squares".

    The model source is a geostatistical layer or a file of grapy's JSON with or without data (ADR-233 §5.2); an
    ArcGIS .lyrx or model XML is refused by name. ``in_datasets`` is a GeostatisticalDatasets object or the documented
    string. A GLOBAL_POLYNOMIAL, IDW, LOCAL_POLYNOMIAL or RADIAL_BASIS model keeps its parameters."""
    tool = "GACreateGeostatisticalLayer"
    source = _geostat.open_layer(in_ga_model_source, tool, need_data=False)
    pairs = _geostat.parse_datasets(in_datasets)
    if not pairs:
        raise ValueError(f"arcpy.ga.{tool}: in_datasets names no dataset.")
    datasets, srid = [], 0
    for dataset, field in pairs:
        data, s = _geostat.read_dataset(dataset, field, tool)
        datasets.append(data)
        srid = srid or s
    parameters = source.parameters
    if source.method == "KRIGING":
        if len(datasets) != 1:
            raise ValueError(f"arcpy.ga.{tool}: kriging takes one dataset.")
        parameters = _fit_kriging(tool, parameters, datasets)
    elif source.method == "COKRIGING":
        parameters = _cokriging(tool, parameters, datasets)
        if source.neighborhood is not None:
            _refuse(tool, "neighborhood", "a local neighbourhood", "how a cokriging neighbourhood divides its points "
                    "between the datasets is not given; cokriging is built with every point")
    elif len(datasets) != 1:
        raise ValueError(f"arcpy.ga.{tool}: a {source.method} model takes one dataset.")
    layer = GALayer(source.method, parameters, source.neighborhood, datasets, srid)
    return Result(_geostat.save_layer(out_layer, layer))


# ---- model parameters


_PATHS = {  # (method, leaf) → (where in the model, the key); the paths the GA Set Model Parameter page prints
    ("KRIGING", "NUGGET"): ("semivariogram", "nugget"),
    ("KRIGING", "RANGE"): ("semivariogram", "range"),
    ("KRIGING", "SILL"): ("semivariogram", "partialSill"),
    ("KRIGING", "NUMBEROFLAGS"): (None, "numberOfLags"),
}


def _paths(tool, layer, model_param_xpath):
    found = []
    for path in [p.strip() for p in str(model_param_xpath).split(";") if p.strip()]:
        names = re.findall(r"@name\s*=\s*['\"]([^'\"]+)['\"]", path)
        auto = path.rstrip().endswith("/@auto")
        if not names:
            raise ValueError(f"arcpy.ga.{tool}: '{path}' is not a model parameter path.")
        method = {"KRIGING": "KRIGING"}.get(names[0].upper())
        key = _PATHS.get((method, names[-1].upper())) if method == layer.method else None
        if key is None or (auto and key[0] is None):
            _refuse(tool, "model_param_xpath", path, "grapy's model is its own JSON (ADR-233 §5.2), and of Esri's XML "
                    "paths it answers the ones the page prints: a kriging layer's Nugget, Range, Sill (the partial "
                    "sill), NumberOfLags, and their @auto")
        found.append((key, auto))
    return found


def GAGetModelParameter(in_ga_model_source, model_param_xpath):
    """The value of each parameter a path names, joined by ``;`` as the page's example is (``0.345;1127.14``). The
    paths answered are those the GA Set Model Parameter page prints, against grapy's own model (INFERRED: Sill is the
    model's partial sill, the component's sill)."""
    tool = "GAGetModelParameter"
    layer = _geostat.open_layer(in_ga_model_source, tool, need_data=False)
    values = []
    for (where, key), auto in _paths(tool, layer, model_param_xpath):
        holder = layer.parameters.get(where) or {} if where else layer.parameters
        value = (holder.get("auto") or {}).get(key, False) if auto else holder.get(key)
        values.append("" if value is None else (str(value).lower() if isinstance(value, bool) else repr(value)
                                                if isinstance(value, float) else str(value)))
    return Result(";".join(values))


def GASetModelParameter(in_ga_model_source, model_param_xpath, in_param_value, out_ga_model):
    """A copy of the model with each named parameter set, written to ``out_ga_model`` as grapy's JSON. A value set is
    held as given — its auto flag cleared — and ``@auto`` true marks it to be fitted again when GACreateGeostatisticalLayer
    uses the model (INFERRED)."""
    tool = "GASetModelParameter"
    layer = _geostat.open_layer(in_ga_model_source, tool, need_data=False).copy()
    keys = _paths(tool, layer, model_param_xpath)
    values = [v.strip() for v in str(in_param_value).split(";")]
    if len(values) != len(keys):
        raise ValueError(f"arcpy.ga.{tool}: {len(keys)} paths and {len(values)} values.")
    for ((where, key), auto), value in zip(keys, values):
        holder = layer.parameters.setdefault(where, {}) if where else layer.parameters
        if auto:
            holder.setdefault("auto", {})[key] = value.lower() in ("true", "1", "yes")
        else:
            holder[key] = int(float(value)) if key == "numberOfLags" else float(value)
            if where:
                holder.setdefault("auto", {})[key] = False
    path = out_ga_model if _geostat._is_path(str(out_ga_model)) else str(out_ga_model) + ".json"
    return Result(_geostat.save_layer(path, layer))


# ---- reading a layer


def _point(value):
    if hasattr(value, "X") and hasattr(value, "Y"):
        return float(value.X), float(value.Y)
    if isinstance(value, (list, tuple)):
        return float(value[0]), float(value[1])
    parts = str(value).replace(",", " ").split()
    return float(parts[0]), float(parts[1])


def GACalculateZValue(in_geostat_layer, point_coord):
    """The layer's prediction at one x,y. A place where no prediction can be made raises ExecuteError."""
    from ._runtime import ExecuteError
    layer = _geostat.open_layer(in_geostat_layer, "GACalculateZValue")
    got = layer.predict(*_point(point_coord))
    if got is None:
        raise ExecuteError("arcpy.ga.GACalculateZValue: no prediction can be made at that point.")
    return Result(got[0])


def GALayerToGrid(in_geostat_layer, out_surface_grid, cell_size=None, points_per_block_horz=1, points_per_block_vert=1):
    """The layer's predictions as a raster; each cell is "the mean of all predictions made within that cell", with
    ``points_per_block_horz`` × ``points_per_block_vert`` predictions per cell (INFERRED: at the centres of an even
    subdivision of the cell). A cell where a prediction cannot be made is NoData."""
    tool = "GALayerToGrid"
    layer = _geostat.open_layer(in_geostat_layer, tool)
    grid = _grid(layer, cell_size, tool)
    block = (int(points_per_block_horz or 1), int(points_per_block_vert or 1))
    if min(block) < 1:
        raise ValueError(f"arcpy.ga.{tool}: at least one prediction per cell each way.")
    return Result(_raster(_surface(layer, grid, block)[0], grid).save(out_surface_grid))


def _typed(tool, layer, prediction, error, kind, value):
    from scipy.stats import norm
    kind = _keyword(kind or "PREDICTION")
    if kind == "PREDICTION":
        return prediction
    if kind in ("STANDARD_ERROR_INDICATORS", "CONDITION_NUMBER"):
        _refuse(tool, "output_type", kind, "indicator kriging and local polynomial's spatial condition number are not "
                "built — neither is defined on the pages")
    if not layer.has_errors():
        raise ValueError(f"arcpy.ga.{tool}: a {layer.method} layer has no standard errors, so no {kind} surface.")
    if kind == "PREDICTION_STANDARD_ERROR":
        return error
    if not _given(value):
        raise ValueError(f"arcpy.ga.{tool}: {kind} needs quantile_probability_value.")
    value = float(value)
    with np.errstate(invalid="ignore", divide="ignore"):
        if kind == "QUANTILE":
            if not 0 < value < 1:
                raise ValueError(f"arcpy.ga.{tool}: a quantile is between 0 and 1.")
            return prediction + error * norm.ppf(value)
        if kind == "PROBABILITY":
            return np.where(error > 0, norm.sf((value - prediction) / np.where(error > 0, error, 1)),
                            (prediction > value).astype(np.float64))
    raise ValueError(f"arcpy.ga.{tool}: '{kind}' is not an output type.")


def GALayerToRasters(in_geostat_layer, out_raster, output_type=None, quantile_probability_value=None, cell_size=None,
                     points_per_block_horz=1, points_per_block_vert=1, additional_rasters=None, out_elevation=None):
    """The layer as one raster or several: ``PREDICTION``, and for a kriging layer ``PREDICTION_STANDARD_ERROR``,
    ``QUANTILE`` (prediction + standard error × Φ⁻¹(p)) and ``PROBABILITY`` that the threshold is exceeded,
    1 − Φ((t − prediction) / standard error) — the predictive distribution taken as normal, kriging's own assumption
    (Cressie 1993, §3.2). ``additional_rasters`` rows are ``[name, type, value]``.

    Refused: STANDARD_ERROR_INDICATORS and CONDITION_NUMBER; standard errors with more than one prediction per cell
    (block kriging's variance is not the mean of the points'); ``out_elevation`` (3D layers are EBK3D's).
    INFERRED: the default output type is PREDICTION."""
    tool = "GALayerToRasters"
    from .management import _pairs
    if _given(out_elevation):
        _refuse(tool, "out_elevation", out_elevation, "it is for 3D layers, which only EmpiricalBayesianKriging3D makes")
    layer = _geostat.open_layer(in_geostat_layer, tool)
    grid = _grid(layer, cell_size, tool)
    block = (int(points_per_block_horz or 1), int(points_per_block_vert or 1))
    rows = [[out_raster, output_type or "PREDICTION", quantile_probability_value]]
    for row in (_pairs(additional_rasters) if _given(additional_rasters) else []):
        rows.append(list(row) + [None] * (3 - len(row)))
    if block != (1, 1) and any(_keyword(r[1]) != "PREDICTION" for r in rows):
        _refuse(tool, "points_per_block", f"{block[0]} × {block[1]}", "block kriging's standard error is not the mean "
                "of its points' standard errors, and how it is computed is not given")
    prediction, error = _surface(layer, grid, block)
    written = [_raster(_typed(tool, layer, prediction, error, kind, value), grid).save(name)
               for name, kind, value in rows]
    return Result(written[0], ";".join(written[1:]))


def _write_points(out, srid, fields, rows):
    """A point feature class of the fields given — (name, type) — and rows of (values, x, y)."""
    from . import _gpkg, da
    from ._geometry import Point, PointGeometry
    from .analysis import _out
    out = _out(out)
    columns = [_gpkg.Column(n, t) for n, t in fields]
    _gpkg.create(out, "Point", srid, columns)
    with da.InsertCursor(out, [c.name for c in columns] + ["SHAPE@"]) as written:
        for values, x, y in rows:
            written.insertRow((*values, PointGeometry(Point(x, y), srid)))
    return out


def _locations(in_locations, tool):
    from . import da
    from .analysis import _schema
    columns, kind, srid, _ = _schema(in_locations)
    if kind != "Point":
        raise ValueError(f"arcpy.ga.{tool}: the locations are points.")
    names = [c.name for c in columns]
    rows = []
    with da.SearchCursor(in_locations, ["OID@", *names, "SHAPE@XY"]) as found:
        for row in found:
            if row[-1] is not None:
                rows.append((row[0], list(row[1:-1]), row[-1]))
    return columns, srid, rows


_VALIDATION = ("Error", "StdError", "Stdd_Error", "NormValue")


def _norm_values(standardized):
    """NormValue: the standard normal quantile matching each standardized error's rank, at plotting position
    (i − 0.5)/n (Hazen; INFERRED — the page names the QQ plot, not its positions)."""
    from scipy.stats import norm
    known = [(v, i) for i, v in enumerate(standardized) if v is not None]
    out = [None] * len(standardized)
    for rank, (_, i) in enumerate(sorted(known, key=lambda p: (p[0], p[1])), 1):
        out[i] = float(norm.ppf((rank - 0.5) / len(known)))
    return out


def _validated(layer, measured, predictions):
    """(Error, StdError, Stdd_Error, NormValue) for each prediction: Error is predicted minus measured, as documented."""
    errors = [None if p is None or m is None else p[0] - m for p, m in zip(predictions, measured)]
    se = [None if p is None else p[1] for p in predictions]
    standardized = [None if e is None or s in (None, 0) else e / s for e, s in zip(errors, se)]
    return errors, se, standardized, _norm_values(standardized) if layer.has_errors() else [None] * len(errors)


def GALayerToPoints(in_geostat_layer, in_locations, z_field=None, out_feature_class=None, append_all_fields="ALL",
                    elevation_field=None, elevation_units=None):
    """Predictions at the locations: ``Source_ID``, ``Included`` (Yes, or Not enough neighbors), ``Predicted`` and,
    for a kriging layer, ``StdError``; with ``z_field``, validation against it — ``Error`` (predicted minus the value),
    ``Stdd_Error`` (Error / StdError) and ``NormValue`` — the field names the page gives. ``ALL`` copies the locations'
    fields, ``FID_ONLY`` keeps ``Source_ID`` alone.

    Refused: ``elevation_field`` (3D layers are EBK3D's). INFERRED: Source_ID is written in both modes, a copied field
    whose name is one of the tool's is left out, and fields a layer cannot answer (StdError for IDW) are not written."""
    tool = "GALayerToPoints"
    if _given(elevation_field):
        _refuse(tool, "elevation_field", elevation_field, "it is for 3D layers, which only EmpiricalBayesianKriging3D makes")
    if not _given(out_feature_class):
        raise ValueError(f"arcpy.ga.{tool}: out_feature_class is required.")
    layer = _geostat.open_layer(in_geostat_layer, tool)
    columns, srid, rows = _locations(in_locations, tool)
    predictions = [layer.predict(x, y) for _, _, (x, y) in rows]
    own = ["Source_ID", "Included", "Predicted"] + (["StdError"] if layer.has_errors() else [])
    fields = [("Source_ID", "Integer"), ("Included", "String"), ("Predicted", "Double")] + (
        [("StdError", "Double")] if layer.has_errors() else [])
    values = [[oid, "Yes" if p is not None else "Not enough neighbors", None if p is None else p[0]]
              + ([None if p is None else p[1]] if layer.has_errors() else []) for (oid, _, _), p in zip(rows, predictions)]
    if _given(z_field):
        names = {c.name.lower(): i for i, c in enumerate(columns)}
        if str(z_field).lower() not in names:
            raise ValueError(f"'{z_field}' is not a field of {in_locations}.")
        k = names[str(z_field).lower()]
        measured = [None if r[1][k] is None else float(r[1][k]) for r in rows]
        errors, _, standardized, norms = _validated(layer, measured, predictions)
        fields.append(("Error", "Double"))
        own.append("Error")
        if layer.has_errors():
            fields += [("Stdd_Error", "Double"), ("NormValue", "Double")]
            own += ["Stdd_Error", "NormValue"]
        for i, v in enumerate(values):
            v.append(errors[i])
            if layer.has_errors():
                v += [standardized[i], norms[i]]
    keep = []
    if _keyword(append_all_fields or "ALL") in ("ALL", "TRUE"):
        taken = {f.lower() for f in own}
        keep = [i for i, c in enumerate(columns) if c.name.lower() not in taken]
        fields += [(columns[i].name, columns[i].type) for i in keep]
    out = _write_points(out_feature_class, srid, fields,
                        [(v + [r[1][i] for i in keep], r[2][0], r[2][1]) for v, r in zip(values, rows)])
    return Result(out)


def GALayerToContour(in_geostat_layer, contour_type, out_feature_class, contour_quality="DRAFT",
                     classification_type=None, classes_count=None, classes_breaks=None, out_elevation=None):
    """Contour lines of the layer's predictions, made as arcpy.sa.Contour makes them (How contouring works) on the
    layer's default grid — the shorter side of the points' extent divided by 250 (INFERRED for DRAFT). ``MANUAL``
    breaks are the contour values; ``EQUAL_INTERVAL`` with N classes gives the N − 1 values that split the
    predictions' range into N equal classes (INFERRED: the range of the predictions on that grid). Each line carries
    its value in ``Contour`` (INFERRED, as arcpy.sa.Contour).

    Refused: FILLED_CONTOUR (polygon contours are not built in grapy's contouring), SAME_AS_LAYER (a renderer),
    PRESENTATION (how much more detail is not given), GEOMETRIC_INTERVAL (Esri's classification, not published as a
    formula), QUANTILE (which sample quantile is not given), ``out_elevation`` (3D layers)."""
    tool = "GALayerToContour"
    from . import sa
    from .management import _list
    kind = _keyword(contour_type)
    if kind != "CONTOUR":
        _refuse(tool, "contour_type", kind, "filled contours are not built in grapy's contouring, and SAME_AS_LAYER "
                "reads a renderer")
    if _given(contour_quality) and _keyword(contour_quality) != "DRAFT":
        _refuse(tool, "contour_quality", contour_quality, "how much more detail PRESENTATION draws is not given")
    if _given(out_elevation):
        _refuse(tool, "out_elevation", out_elevation, "it is for 3D layers, which only EmpiricalBayesianKriging3D makes")
    classification = _keyword(classification_type or "MANUAL" if _given(classes_breaks) else classification_type or "")
    if classification in ("GEOMETRIC_INTERVAL", "QUANTILE"):
        _refuse(tool, "classification_type", classification, "its breaks are not given as a formula")
    layer = _geostat.open_layer(in_geostat_layer, tool)
    grid = _grid(layer, None, tool)
    surface = _raster(_surface(layer, grid)[0], grid)
    if classification == "MANUAL":
        levels = sorted({float(v) for v in _list(classes_breaks)})
    elif classification == "EQUAL_INTERVAL":
        if not _given(classes_count) or int(classes_count) < 2:
            raise ValueError(f"arcpy.ga.{tool}: EQUAL_INTERVAL needs a classes_count of 2 or more.")
        values = surface._values().astype(np.float64)
        low, high, n = float(values.min()), float(values.max()), int(classes_count)
        levels = [low + k * (high - low) / n for k in range(1, n)]
    else:
        raise ValueError(f"arcpy.ga.{tool}: give classification_type MANUAL with classes_breaks, or EQUAL_INTERVAL.")
    return sa._write_contours(out_feature_class, surface, sa._contour_lines(surface, levels))


# ---- cross-validation


class CrossValidationResult(Result):
    """What CrossValidation answers: a Result with the summary statistics the class page lists. ``meanError`` is the
    mean of predicted minus measured; ``rootMeanSquare`` its root mean square; ``averageStandard``,
    ``meanStandardized`` and ``rootMeanSquareStandardized`` are kriging's, None for a layer without standard errors as
    the page says; ``percentIn90Interval``, ``percentIn95Interval`` and ``averageCRPS`` are EBK's, and None.
    ``count`` is the number of samples predicted. Made from a layer: ``CrossValidationResult(in_geostat_layer)``."""

    def __init__(self, in_geostat_layer, out_point_feature_class=None):
        layer = _geostat.open_layer(in_geostat_layer, "CrossValidation")
        xs, ys, zs = layer.points(0)
        predictions = [layer.predict(x, y, leave_out=i) for i, (x, y) in enumerate(zip(xs, ys))]
        errors, se, standardized, norms = _validated(layer, list(zs), predictions)
        made = [e for e in errors if e is not None]
        self.count = len(made)
        self.meanError = float(np.mean(made)) if made else None
        self.rootMeanSquare = float(np.sqrt(np.mean(np.square(made)))) if made else None
        self.maxAbsoluteError = float(np.max(np.abs(made))) if made else None
        if layer.has_errors() and made:
            ses = [s for s in se if s is not None]
            stds = [s for s in standardized if s is not None]
            self.averageStandard = float(np.mean(ses))
            self.meanStandardized = float(np.mean(stds)) if stds else None
            self.rootMeanSquareStandardized = float(np.sqrt(np.mean(np.square(stds)))) if stds else None
        else:
            self.averageStandard = self.meanStandardized = self.rootMeanSquareStandardized = None
        self.percentIn90Interval = self.percentIn95Interval = self.averageCRPS = None
        out = ""
        if _given(out_point_feature_class):
            fields = [("Source_ID", "Integer"), ("Included", "String"), ("Measured", "Double"), ("Predicted", "Double"),
                      ("Error", "Double")]
            if layer.has_errors():
                fields += [("StdError", "Double"), ("Stdd_Error", "Double"), ("NormValue", "Double")]
            rows = []
            for i, p in enumerate(predictions):
                row = [i + 1, "Yes" if p is not None else "Not enough neighbors", float(zs[i]),
                       None if p is None else p[0], errors[i]]
                if layer.has_errors():
                    row += [se[i], standardized[i], norms[i]]
                rows.append((row, float(xs[i]), float(ys[i])))
            out = _write_points(out_point_feature_class, layer.srid, fields, rows)
        super().__init__(out)
        self.inputCount, self.messageCount, self.maxSeverity, self.resultID = 1, 0, 0, ""


def CrossValidation(in_geostat_layer, out_point_feature_class=None):
    """Leave-one-out cross-validation: each point of the layer's data predicted from the others with the layer's
    model held as fitted (Cressie 1993, §2.6.4; INFERRED that the model is not refitted for each point), written to
    ``out_point_feature_class`` with GA Layer To Points' fields — and ``Measured``, the value left out (INFERRED) —
    and summarised in the CrossValidationResult it answers. ``Source_ID`` is the point's position in the layer's data,
    from 1 (INFERRED: the layer holds its points, not their object IDs)."""
    return CrossValidationResult(in_geostat_layer, out_point_feature_class)


# ---- comparing layers


_CRITERIA = {"ACCURACY": lambda s: s["rmse"], "BIAS": lambda s: abs(s["mean"]), "WORST_CASE": lambda s: s["worst"],
             "STANDARD_ERROR": lambda s: None if s["rmss"] is None else abs(s["rmss"] - 1),
             "PRECISION": lambda s: s["ase"]}


def _competition(scores):
    """Ranks, smaller first, tied results sharing the highest rank of their group (1, 2, 2, 4), as documented."""
    return [1 + sum(1 for t in scores if t < s) for s in scores]


def CompareGeostatisticalLayers(in_geostat_layers, out_cv_table, out_geostat_layer=None, comparison_method="SINGLE",
                                criterion="ACCURACY", criteria_hierarchy=None, weighted_criteria=None,
                                exclusion_criteria=None):
    """Cross-validates each layer and ranks them as the page specifies: ``SINGLE`` by one criterion's score; ``SORTING``
    by the first criterion, results within its tolerance of the best (PERCENT: best + tol/100 × best; ABSOLUTE:
    best + tol) tied and ordered by the next; ``AVERAGE_RANK`` by the weighted average of each criterion's ranks.
    Scores: ACCURACY RMSE, BIAS |mean error|, WORST_CASE the largest absolute error, STANDARD_ERROR |RMS standardized
    − 1|, PRECISION the average standard error. Ties share the highest rank. The top layer is copied to
    ``out_geostat_layer`` unless first place is tied or every result is excluded.

    Exclusions built: MAX_RMSE, MAX_WORST_CASE, MAX_STD_RMSE, MIN_STD_RMSE, MAX_ASE, MIN_PERC_ERROR (RMSE ≤ SD ×
    (1 − value/100)). Refused: MAX_MEAN_ERROR and MIN_MEAN_ERROR, which the page describes in the same words. A
    standard-error criterion with a layer that has none is an error, as the page makes the option unavailable.

    INFERRED: the table's fields beyond ``RANK`` and ``Included`` — ``Layer``, ``Description``, ``Count``,
    ``MeanError``, ``RMSE``, ``MaxAbsError``, ``MeanStdError``, ``RMSStdError``, ``AvgStdError``, and for AVERAGE_RANK
    ``Rank_<criterion>`` and ``WeightedAverageRank``; in SORTING the next tie block starts at the best of the rest; the
    weighted average is Σ wᵢ rᵢ / Σ wᵢ; SD is the sample standard deviation (n − 1) of the layer's data."""
    tool = "CompareGeostatisticalLayers"
    from . import _gpkg, da
    from .analysis import _table
    from .management import _list, _pairs
    names = _list(in_geostat_layers)
    layers = [_geostat.open_layer(n, tool) for n in names]
    stats = []
    for layer in layers:
        r = CrossValidationResult(layer)
        stats.append({"count": r.count, "mean": r.meanError, "rmse": r.rootMeanSquare, "worst": r.maxAbsoluteError,
                      "ms": r.meanStandardized, "rmss": r.rootMeanSquareStandardized, "ase": r.averageStandard,
                      "sd": float(np.std(layer.points(0)[2], ddof=1))})
    included = [True] * len(layers)
    for row in (_pairs(exclusion_criteria) if _given(exclusion_criteria) else []):
        name, value = _keyword(row[0]), float(row[1])
        if name in ("MAX_MEAN_ERROR", "MIN_MEAN_ERROR"):
            _refuse(tool, "exclusion_criteria", name, "the page describes MAX_MEAN_ERROR and MIN_MEAN_ERROR in the same "
                    "words, so which results each excludes is not given")
        tests = {"MAX_RMSE": lambda s: s["rmse"] > value, "MAX_WORST_CASE": lambda s: s["worst"] > value,
                 "MAX_STD_RMSE": lambda s: s["rmss"] is not None and s["rmss"] > value,
                 "MIN_STD_RMSE": lambda s: s["rmss"] is not None and s["rmss"] <= value,
                 "MAX_ASE": lambda s: s["ase"] is not None and s["ase"] > value,
                 "MIN_PERC_ERROR": lambda s: not s["rmse"] <= s["sd"] * (1 - value / 100)}
        if name not in tests:
            raise ValueError(f"arcpy.ga.{tool}: '{row[0]}' is not an exclusion criterion.")
        included = [inc and not tests[name](s) for inc, s in zip(included, stats)]
    kept = [i for i, inc in enumerate(included) if inc]

    def scores(name):
        name = _keyword(name)
        if name not in _CRITERIA:
            raise ValueError(f"arcpy.ga.{tool}: '{name}' is not a criterion.")
        got = [_CRITERIA[name](stats[i]) for i in kept]
        if any(g is None for g in got):
            raise ValueError(f"arcpy.ga.{tool}: {name} needs every layer to have standard errors.")
        return got

    method = _keyword(comparison_method or "SINGLE")
    extra = {}
    if method == "SINGLE":
        ranks = _competition(scores(criterion or "ACCURACY"))
    elif method == "SORTING":
        hierarchy = _pairs(criteria_hierarchy)
        if not hierarchy:
            raise ValueError(f"arcpy.ga.{tool}: SORTING needs criteria_hierarchy.")

        def order(members, level):
            if level == len(hierarchy) or len(members) < 2:
                return [members]
            row = list(hierarchy[level]) + [None, None]
            values = dict(zip(kept, scores(row[0])))
            tol_type, tol = _keyword(row[1] or "PERCENT"), row[2]
            rest = sorted(members, key=lambda i: (values[i], i))
            blocks = []
            while rest:
                best = values[rest[0]]
                if not _given(tol) or _keyword(tol) == "NONE":
                    block = [i for i in rest if values[i] == best]
                else:
                    limit = best + float(tol) / 100 * best if tol_type == "PERCENT" else best + float(tol)
                    block = [i for i in rest if values[i] <= limit]
                rest = [i for i in rest if i not in block]
                blocks += order(block, level + 1)
            return blocks

        blocks = order(list(kept), 0)
        position, rank_of = 1, {}
        for block in blocks:
            for i in block:
                rank_of[i] = position
            position += len(block)
        ranks = [rank_of[i] for i in kept]
    elif method == "AVERAGE_RANK":
        weighted = _pairs(weighted_criteria)
        if not weighted:
            raise ValueError(f"arcpy.ga.{tool}: AVERAGE_RANK needs weighted_criteria.")
        total = sum(float(w) for _, w in weighted)
        average = [0.0] * len(kept)
        for name, weight in weighted:
            each = _competition(scores(name))
            extra[f"Rank_{_keyword(name)}"] = each
            average = [a + float(weight) * r for a, r in zip(average, each)]
        average = [a / total for a in average]
        extra["WeightedAverageRank"] = average
        ranks = _competition(average)
    else:
        raise ValueError(f"arcpy.ga.{tool}: '{comparison_method}' is not a comparison method.")
    columns = [_gpkg.Column("Layer", "String"), _gpkg.Column("Description", "String"), _gpkg.Column("RANK", "Integer"),
               _gpkg.Column("Included", "String"), _gpkg.Column("Count", "Integer"), _gpkg.Column("MeanError", "Double"),
               _gpkg.Column("RMSE", "Double"), _gpkg.Column("MaxAbsError", "Double"),
               _gpkg.Column("MeanStdError", "Double"), _gpkg.Column("RMSStdError", "Double"),
               _gpkg.Column("AvgStdError", "Double")] + [_gpkg.Column(k, "Double") for k in extra]
    out = _table(out_cv_table, columns)
    with da.InsertCursor(out, [c.name for c in columns]) as written:
        for i, (name, layer, s) in enumerate(zip(names, layers, stats)):
            position = kept.index(i) if i in kept else None
            written.insertRow((str(name), layer.method, None if position is None else ranks[position],
                               "Yes" if position is not None else "No", s["count"], s["mean"], s["rmse"], s["worst"],
                               s["ms"], s["rmss"], s["ase"],
                               *[None if position is None else float(v[position]) for v in extra.values()]))
    top = ""
    if _given(out_geostat_layer) and kept and ranks.count(1) == 1:
        top = _geostat.save_layer(out_geostat_layer, layers[kept[ranks.index(1)]])
    return Result(out, top)


# ---- neighbourhoods and moving windows


def GANeighborhoodSelection(in_dataset, out_layer, point_coord, neighbors_max, neighbors_min, minor_semiaxis,
                            major_semiaxis, angle, shape_type="ONE_SECTOR"):
    """A feature layer of the points the neighbourhood takes around ``point_coord``: up to ``neighbors_max`` inside the
    ellipse and, when it holds fewer than ``neighbors_min``, the nearest outside it, as the page says. INFERRED: the
    angle is the major axis' direction in degrees clockwise from north, nearness is distance scaled to the ellipse.
    Refused: sectors other than ONE_SECTOR (which sector holds a point on its edge is not documented)."""
    from . import da
    from .management import MakeFeatureLayer
    sector = _keyword(shape_type or "ONE_SECTOR")
    nbr = _geostat.neighbourhood(SearchNeighborhoodStandard(float(major_semiaxis), float(minor_semiaxis),
                                                            float(angle or 0), int(neighbors_max), int(neighbors_min),
                                                            sector), "GANeighborhoodSelection")
    oids, xs, ys = [], [], []
    with da.SearchCursor(in_dataset, ["OID@", "SHAPE@XY"]) as rows:
        for oid, xy in rows:
            if xy is not None:
                oids.append(oid)
                xs.append(xy[0])
                ys.append(xy[1])
    chosen = _geostat.select(nbr, np.array(xs, dtype=np.float64), np.array(ys, dtype=np.float64), *_point(point_coord))
    picked = sorted(int(oids[i]) for i in chosen)
    from ._gpkg import OID
    where = f"{OID} IN ({','.join(str(o) for o in picked)})" if picked else "1 = 0"
    MakeFeatureLayer(in_dataset, out_layer, where)
    return Result(out_layer)


@_equivalent("kriging with the semivariogram refitted in each moving window by weighted least squares",
             "Haas 1990, Atmospheric Environment 24A(7); Cressie 1985, Mathematical Geology 17(5)")
def GAMovingWindowKriging(in_ga_model_source, in_datasets, in_locations, neighbors_max, out_featureclass,
                          cell_size=None, out_surface_grid=None):
    """EQUIVALENT: at each location, the ``neighbors_max`` nearest points; the model's range, nugget and partial sill
    fitted to them by Cressie's (1985) weighted least squares, and kriging from them with that model (Haas 1990's
    moving window kriging). The page says the tool "recalculates the Range, Nugget, and Partial Sill semivariogram
    parameters" in each neighbourhood and does not say how. The model source must be kriging; the data must hold
    more than 10 points, as documented.

    Refused: ``out_surface_grid``, interpolated by local polynomial interpolation with parameters the page does not
    give. INFERRED: the output's fields ``Source_ID``, ``Included``, ``Predicted``, ``StdError``, ``Range``,
    ``Nugget``, ``PartialSill``; the lag in each window is the default rule over that window's points; a window
    whose fit fails is Not enough neighbors."""
    tool = "GAMovingWindowKriging"
    if _given(out_surface_grid):
        _refuse(tool, "out_surface_grid", out_surface_grid, "it is interpolated by local polynomial interpolation with "
                "parameters the page does not give")
    source = _geostat.open_layer(in_ga_model_source, tool, need_data=False)
    if source.method != "KRIGING":
        raise ValueError(f"arcpy.ga.{tool}: the model source must be a kriging model.")
    pairs = _geostat.parse_datasets(in_datasets)
    if len(pairs) != 1:
        raise ValueError(f"arcpy.ga.{tool}: one dataset.")
    data, srid = _geostat.read_dataset(*pairs[0], tool)
    xs, ys, zs = (np.array(data[k], dtype=np.float64) for k in ("x", "y", "z"))
    if len(xs) <= 10:
        raise ValueError(f"arcpy.ga.{tool}: the input dataset must contain more than 10 points.")
    count = int(neighbors_max)
    template = dict(source.parameters)
    model = {k: v for k, v in (template.get("semivariogram") or {}).items() if k not in ("range", "partialSill",
                                                                                         "nugget", "auto")}
    template["semivariogram"] = model
    template.pop("lagSize", None)
    _, _, rows = _locations(in_locations, tool)
    out_rows = []
    for oid, _, (x, y) in rows:
        idx = np.argsort(np.hypot(xs - x, ys - y), kind="stable")[:count]
        window = [{"x": xs[idx].tolist(), "y": ys[idx].tolist(), "z": zs[idx].tolist()}]
        try:
            fitted = _fit_kriging(tool, template, window)
            got = _geostat._krige(fitted, xs[idx], ys[idx], zs[idx], x, y)
        except (ValueError, np.linalg.LinAlgError):
            fitted, got = None, None
        m = fitted["semivariogram"] if fitted else {}
        out_rows.append(([oid, "Yes" if got else "Not enough neighbors", got[0] if got else None,
                          got[1] if got else None, m.get("range"), m.get("nugget"), m.get("partialSill")], x, y))
    fields = [("Source_ID", "Integer"), ("Included", "String"), ("Predicted", "Double"), ("StdError", "Double"),
              ("Range", "Double"), ("Nugget", "Double"), ("PartialSill", "Double")]
    return Result(_write_points(out_featureclass, srid, fields, out_rows))


# ---- refused


_EBK = ("empirical Bayesian kriging (Krivoruchko 2012) simulates semivariograms at random from subsets, and the "
        "subset, simulation and transformation choices are not specified; ordinary, simple and universal kriging, "
        "which grapy builds through GACreateGeostatisticalLayer, are not EBK")
_RANDOM = "its answer is drawn at random"
_UNSPECIFIED = "its kernel or fitting is named but not given as a formula"
_THREE_D = ("its input is a 3D geostatistical layer, which only EmpiricalBayesianKriging3D makes, and that is refused "
            "for its own reason")

_REFUSED = {name: _EBK for name in ("EmpiricalBayesianKriging", "EmpiricalBayesianKriging3D", "EBKRegressionPrediction")}
_REFUSED.update({name: _RANDOM for name in ("CreateSpatiallyBalancedPoints", "SubsetFeatures")})
_REFUSED.update({name: _UNSPECIFIED for name in ("KernelInterpolationWithBarriers", "DiffusionInterpolationWithBarriers")})
_REFUSED.update({name: _THREE_D for name in ("GALayer3DToMultidimensionalRaster", "GALayer3DToNetCDF")})
_REFUSED["GaussianGeostatisticalSimulations"] = "it is a conditioned simulation, and its answer is drawn at random"
_REFUSED["GASemivariogramSensitivity"] = ("its nugget, sill and range values are drawn at random within the spans "
                                          "(the page: Mersenne Twister, seed 0 differing on every run)")
_REFUSED["DensifySamplingNetwork"] = ("whether the kriging network is updated after each location is chosen — the page "
                                      "says the same location can keep scoring highest — and where locations are "
                                      "sought without candidate points are not given")
_REFUSED["ExploratoryInterpolation"] = ("its candidates are each method's default and optimized parameter sets, which "
                                        "are not given, and its default set includes empirical Bayesian kriging")
_REFUSED["ArealInterpolationLayerToPolygons"] = ("its input is an areal interpolation layer, made only by the "
                                                 "Geostatistical Wizard by areal kriging whose area-to-point covariance "
                                                 "fitting the pages do not give")
_REFUSED["GenerateSubsetPolygons"] = "its curve through the points and its segmentation are described, not specified"
_REFUSED["ExtractValuesToTable"] = "the output table's fields are not named in the documentation"
_REFUSED["DirectionalTrend"] = "its output is a chart on a layer in the active map, and the tool cannot run without one"


# ---- 3D interpolation to a netCDF grid of voxels (ADR-238)

from . import _voxel  # noqa: E402

IDW3D = _voxel.IDW3D
NearestNeighbor3D = _voxel.NearestNeighbor3D


def __getattr__(name):
    if name.startswith("_"):
        raise AttributeError(name)

    def missing(*_, **__):
        why = _REFUSED.get(name)
        raise NotBuiltError(f"arcpy.ga.{name} is not built in grapy" + (f" — {why}." if why else " yet."))

    return missing
