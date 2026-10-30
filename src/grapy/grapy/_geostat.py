"""Geostatistical layers: grapy's own form of a fitted interpolation model and its data — ADR-233.

A *geostatistical layer* is what most of arcpy.ga reads or writes: an interpolation method, its parameters as fitted
or given, the search neighbourhood, and the data it was fitted to. Esri keeps it in a layer file or a model XML whose
form is not published and is not reproduced here. grapy's layer is a JSON document (INFERRED, ADR-233 §5.2, Q-201):

    {"format": "grapy-geostatistical-layer", "version": 1,
     "method": "IDW" | "GLOBAL_POLYNOMIAL" | "LOCAL_POLYNOMIAL" | "RADIAL_BASIS" | "KRIGING" | "COKRIGING",
     "datasets": [{"dataset": path, "field": name, "x": [...], "y": [...], "z": [...]}, ...],
     "spatialReference": srid, "extent": [left, bottom, right, top],
     "parameters": {...the method's...},
     "neighborhood": {"majorSemiaxis", "minorSemiaxis", "angle", "nbrMax", "nbrMin"} or null for every point}

A layer *holds a copy of its points* (INFERRED): the model was fitted to them, so it answers the same when the source
changes, and GACreateGeostatisticalLayer is how it is pointed at other data. A layer with no ``datasets`` is a *model*
— what ArcGIS keeps as model XML — and GACreateGeostatisticalLayer makes a layer from it.

Kriging's parameters:

    {"kind": "ORDINARY" | "SIMPLE" | "UNIVERSAL", "mean": m (SIMPLE), "drift": "LINEAR" | "QUADRATIC" (UNIVERSAL),
     "lagSize": L, "numberOfLags": 12,
     "semivariogram": {"model": "SPHERICAL" …, "range": a, "partialSill": c, "nugget": c0, "shape": α or ν,
                       "minorRange": b, "direction": θ, "auto": {"range": bool, "partialSill": bool, "nugget": bool}}}

Cokriging's: {"semivariograms": {"primary": {…}, "secondary": {…}, "cross": {…}}}, every value given.

A layer *name* (no folder, no ``.json``) lives as long as this process, as grapy's other layers do (``_layers``); a path
is a file. A file that is not grapy's JSON — an ArcGIS .lyrx or model XML — is refused by name.
"""
from __future__ import annotations

import copy
import json
import math
import os
import re

import numpy as np

from ._runtime import ExecuteError, NotBuiltError, env

FORMAT = "grapy-geostatistical-layer"
METHODS = ("IDW", "GLOBAL_POLYNOMIAL", "LOCAL_POLYNOMIAL", "RADIAL_BASIS", "KRIGING", "COKRIGING")
LAYERS: dict = {}


# ---- semivariogram models


MODELS = ("SPHERICAL", "CIRCULAR", "TETRASPHERICAL", "PENTASPHERICAL", "EXPONENTIAL", "GAUSSIAN",
          "RATIONAL_QUADRATIC", "K_BESSEL", "STABLE")
REFUSED_MODELS = {
    "J_BESSEL": "the scaling between its range and the Bessel function's argument is not given, and the J-Bessel "
                "model has no practical range to fix it by",
    "HOLE_EFFECT": "the scaling between its range and the sine's period is not given, and the hole effect has no "
                   "practical range to fix it by",
}


def _bessel_scale(nu):
    """K-Bessel (Matérn) argument at the range: Ω with 1 − m(Ω) = 0.95, m(u) = 2^{1−ν}/Γ(ν) u^ν K_ν(u), so the range is
    the practical range at 95 % of the sill, the convention of the exponential and Gaussian models' factor 3
    (1 − e⁻³ ≈ 0.95). INFERRED."""
    from scipy.optimize import brentq
    from scipy.special import gamma, kv
    return brentq(lambda u: 2 ** (1 - nu) / gamma(nu) * u ** nu * kv(nu, u) - 0.05, 1e-9, 200.0)


def shape(model, t, extra=None):
    """The model's shape at t = h / range, rising from 0 to 1: the forms of Johnston et al. (2001), Using ArcGIS
    Geostatistical Analyst, Appendix A, and Chilès and Delfiner (2012), Geostatistics, §2.5 — the circular,
    tetraspherical and pentaspherical models being the volume of intersection of two balls in 2, 4 and 5 dimensions."""
    t = np.asarray(t, dtype=np.float64)
    tc = np.clip(t, 0.0, 1.0)
    if model == "SPHERICAL":
        return np.where(t < 1, 1.5 * tc - 0.5 * tc ** 3, 1.0)
    if model == "CIRCULAR":
        return np.where(t < 1, 1 - (2 / math.pi) * np.arccos(tc) + (2 / math.pi) * tc * np.sqrt(1 - tc * tc), 1.0)
    if model == "TETRASPHERICAL":
        return np.where(t < 1, (2 / math.pi) * (np.arcsin(tc) + tc * np.sqrt(1 - tc * tc)
                                                 + (2 / 3) * tc * (1 - tc * tc) ** 1.5), 1.0)
    if model == "PENTASPHERICAL":
        return np.where(t < 1, 15 / 8 * tc - 5 / 4 * tc ** 3 + 3 / 8 * tc ** 5, 1.0)
    if model == "EXPONENTIAL":
        return 1 - np.exp(-3 * t)
    if model == "GAUSSIAN":
        return 1 - np.exp(-3 * t * t)
    if model == "RATIONAL_QUADRATIC":
        return 19 * t * t / (1 + 19 * t * t)
    if model == "STABLE":
        alpha = 1.0 if extra is None else float(extra)
        if not 0 < alpha <= 2:
            raise ValueError("A stable model's shape parameter is from 0 (exclusive) to 2.")
        return 1 - np.exp(-3 * t ** alpha)
    if model == "K_BESSEL":
        from scipy.special import gamma, kv
        nu = 1.0 if extra is None else float(extra)
        if not 0 < nu <= 10:
            raise ValueError("A K-Bessel model's shape parameter is from 0 (exclusive) to 10.")
        u = _bessel_scale(nu) * np.maximum(t, 1e-300)
        m = 2 ** (1 - nu) / gamma(nu) * u ** nu * kv(nu, u)
        return np.where(t > 0, 1 - np.nan_to_num(m, nan=0.0), 0.0)
    if model in REFUSED_MODELS:
        raise NotBuiltError(f"semivariogram model '{model}' is not built in grapy — {REFUSED_MODELS[model]}.")
    raise ValueError(f"'{model}' is not a semivariogram model.")


def _scaled(model, dx, dy):
    """Separation over the range, with geometric anisotropy when a minor range is given: the separation is turned
    onto the major axis — its direction θ degrees clockwise from north (INFERRED) — and each component divided by its
    range (Isaaks and Srivastava 1989, ch. 16)."""
    a = float(model["range"])
    b = model.get("minorRange")
    if b in (None, "") or float(b) == a:
        return np.hypot(dx, dy) / a
    theta = math.radians(float(model.get("direction") or 0.0))
    u = dx * math.sin(theta) + dy * math.cos(theta)
    v = dx * math.cos(theta) - dy * math.sin(theta)
    return np.sqrt((u / a) ** 2 + (v / float(b)) ** 2)


def gamma(model, dx, dy):
    """γ: 0 at no separation, the nugget plus the partial sill times the shape beyond."""
    dx, dy = np.asarray(dx, dtype=np.float64), np.asarray(dy, dtype=np.float64)
    value = float(model.get("nugget") or 0.0) + float(model["partialSill"]) * shape(
        model["model"], _scaled(model, dx, dy), model.get("shape"))
    return np.where((dx == 0) & (dy == 0), 0.0, value)


def covariance(model, dx, dy):
    """C(h) = sill − γ(h), the sill being nugget plus partial sill (every model here has one)."""
    return float(model.get("nugget") or 0.0) + float(model["partialSill"]) - gamma(model, dx, dy)


# ---- the empirical semivariogram and its fit


def default_lag(xs, ys, lags):
    """INFERRED: the lags cover half the largest distance between points (Journel and Huijbregts 1978, Mining
    Geostatistics, §III.B: the semivariogram is not read beyond half the field)."""
    d = np.hypot(xs[:, None] - xs, ys[:, None] - ys).max()
    return d / 2.0 / lags


def empirical(model, xs, ys, zs, lag, lags):
    """Lags (k−1)L < h ≤ kL for k = 1 … lags: mean scaled distance (in major-range units times the range), mean half
    squared difference, and the number of pairs (Matheron 1962; Cressie 1993, eq. 2.4.2)."""
    i, j = np.triu_indices(len(xs), 1)
    dx, dy = xs[j] - xs[i], ys[j] - ys[i]
    if model.get("minorRange") not in (None, "") and model.get("range") not in (None, ""):
        h = _scaled(model, dx, dy) * float(model["range"])
    else:
        h = np.hypot(dx, dy)
    g = 0.5 * (zs[j] - zs[i]) ** 2
    out = []
    for k in range(int(lags)):
        hit = (h > k * lag) & (h <= (k + 1) * lag)
        if hit.any():
            out.append((h[hit].mean(), g[hit].mean(), int(hit.sum())))
    return out


def fit(model, xs, ys, zs, lag, lags):
    """The parameters not given (or marked auto) fitted by Cressie's (1985) weighted least squares to the empirical
    semivariogram, Σ N(h)·(γ̂(h)/γ(h) − 1)², Nelder–Mead from half the largest lag, the largest semivariance and no
    nugget (INFERRED, as arcpy.sa.Kriging fits). The stable and K-Bessel shape is fitted when not given, from 1."""
    from scipy.optimize import minimize
    model = dict(model)
    auto = dict(model.get("auto") or {})
    for name in ("range", "partialSill", "nugget"):
        if model.get(name) in (None, ""):
            auto[name] = True
    if model.get("minorRange") not in (None, "") and auto.get("range"):
        raise NotBuiltError("arcpy.ga: fitting the range of an anisotropic semivariogram is not built in grapy — how "
                            "the directional semivariograms are combined in the fit is not given; give the range, the "
                            "minor range and the direction, and the sill and nugget are fitted")
    points = empirical(model, xs, ys, zs, lag, lags)
    if len(points) < 2:
        raise ValueError("Fewer than two lags hold pairs of points: give a larger lag size or the model's values.")
    h, g, n = (np.array(v, dtype=np.float64) for v in zip(*points))
    free = [k for k in ("range", "partialSill", "nugget") if auto.get(k)]
    shaped = model["model"] in ("STABLE", "K_BESSEL") and model.get("shape") in (None, "")
    if shaped:
        free.append("shape")
    start = {"range": h.max() / 2, "partialSill": max(g.max(), 1e-12), "nugget": 0.0, "shape": 1.0}
    if free:
        def trial(values):
            return {**model, **dict(zip(free, values))}

        def cost(values):
            p = trial(values)
            if float(p["range"]) <= 0 or float(p["partialSill"]) < 0 or float(p.get("nugget") or 0) < 0:
                return 1e30
            if "shape" in free and not 0.01 < float(p["shape"]) <= (2 if model["model"] == "STABLE" else 10):
                return 1e30
            fitted = float(p.get("nugget") or 0) + float(p["partialSill"]) * shape(
                p["model"], h / float(p["range"]), p.get("shape"))
            if np.any(fitted <= 0):
                return 1e30
            return float((n * (g / fitted - 1) ** 2).sum())

        best = minimize(cost, [start[k] for k in free], method="Nelder-Mead",
                        options={"xatol": 1e-10, "fatol": 1e-12, "maxiter": 20000})
        model = trial([float(v) for v in best.x])
    for name in ("range", "partialSill", "nugget"):
        model[name] = float(model.get(name) or 0.0)
    model["auto"] = {k: bool(auto.get(k)) for k in ("range", "partialSill", "nugget")}
    return model


# ---- the search neighbourhood


def neighbourhood(value, tool):
    """A search neighbourhood object as the dictionary a layer holds, or None for every point. One-sector standard
    neighbourhoods are built — circles, and ellipses with their angle as the major axis' direction in degrees
    clockwise from north (INFERRED). Refused: smooth neighbourhoods (their sigmoidal weight is not given as a
    formula), sectors (which sector holds a point on a sector's edge is not documented), 3D."""
    from . import ga
    if value is None or value == "":
        return None
    if isinstance(value, dict):
        return value
    if isinstance(value, (ga.SearchNeighborhoodSmooth, ga.SearchNeighborhoodSmoothCircular)):
        ga._refuse(tool, "search_neighborhood", type(value).__name__, "the sigmoidal weight between the inner and "
                   "outer ellipses is not given as a formula")
    if isinstance(value, ga.SearchNeighborhoodStandard3D):
        ga._refuse(tool, "search_neighborhood", "SearchNeighborhoodStandard3D", "it is for 3D models, which only "
                   "EmpiricalBayesianKriging3D makes")
    if isinstance(value, ga.SearchNeighborhoodStandardCircular):
        major = minor = value.radius
        angle = value.angle
    elif isinstance(value, ga.SearchNeighborhoodStandard):
        major, minor, angle = value.majorSemiaxis, value.minorSemiaxis, value.angle
    else:
        ga._refuse(tool, "search_neighborhood", type(value).__name__, "not a search neighbourhood object")
    sector = ga._keyword(value.sectorType) if ga._given(value.sectorType) else "ONE_SECTOR"
    if sector != "ONE_SECTOR":
        ga._refuse(tool, "sectorType", sector, "which sector a point on a sector's edge belongs to is not documented")
    for name, v in (("radius" if major == minor else "majorSemiaxis", major), ("minorSemiaxis", minor),
                    ("nbrMax", value.nbrMax), ("nbrMin", value.nbrMin)):
        if not ga._given(v):
            ga._refuse(tool, name, "(empty)", "its default is derived from the data by a rule the documentation does "
                       "not give")
    if float(minor) > float(major):
        raise ValueError(f"arcpy.ga.{tool}: the minor semiaxis is longer than the major one.")
    return {"majorSemiaxis": float(major), "minorSemiaxis": float(minor), "angle": float(angle or 0.0),
            "nbrMax": int(value.nbrMax), "nbrMin": int(value.nbrMin)}


def select(nbr, xs, ys, qx, qy):
    """The points a neighbourhood takes for one place: the nearest ``nbrMax`` inside the ellipse and, when it holds
    fewer than ``nbrMin``, the nearest outside it up to ``nbrMin`` — as GA Neighborhood Selection documents (*"If a
    sector lacks enough points, the nearest available point outside it is selected"*). Nearness is the distance
    scaled to the ellipse (INFERRED), so a circle's is Euclidean; ties keep the data's order."""
    if nbr is None:
        return np.arange(len(xs))
    dx, dy = xs - qx, ys - qy
    theta = math.radians(nbr["angle"])
    u = dx * math.sin(theta) + dy * math.cos(theta)
    v = dx * math.cos(theta) - dy * math.sin(theta)
    r = np.sqrt((u / nbr["majorSemiaxis"]) ** 2 + (v / nbr["minorSemiaxis"]) ** 2)
    order = np.argsort(r, kind="stable")
    inside = [i for i in order if r[i] <= 1.0][:nbr["nbrMax"]]
    if len(inside) < nbr["nbrMin"]:
        outside = [i for i in order if r[i] > 1.0]
        inside += outside[:nbr["nbrMin"] - len(inside)]
    return np.array(inside, dtype=int)


# ---- the layer


class GALayer:
    def __init__(self, method, parameters=None, neighborhood=None, datasets=None, srid=0, name=None):
        if method not in METHODS:
            raise ValueError(f"'{method}' is not a method of a grapy geostatistical layer.")
        self.method, self.parameters = method, dict(parameters or {})
        self.neighborhood, self.datasets = neighborhood, list(datasets or [])
        self.srid, self.name = int(srid or 0), name
        self._cache = {}

    # -- data

    def points(self, k=0):
        d = self.datasets[k]
        return np.array(d["x"], dtype=np.float64), np.array(d["y"], dtype=np.float64), np.array(d["z"], dtype=np.float64)

    def extent(self):
        xs = np.concatenate([np.array(d["x"], dtype=np.float64) for d in self.datasets])
        ys = np.concatenate([np.array(d["y"], dtype=np.float64) for d in self.datasets])
        return float(xs.min()), float(ys.min()), float(xs.max()), float(ys.max())

    def has_errors(self):
        return self.method in ("KRIGING", "COKRIGING")

    # -- storage

    def to_json(self):
        left, bottom, right, top = self.extent() if self.datasets else (None,) * 4
        return {"format": FORMAT, "version": 1, "method": self.method, "parameters": self.parameters,
                "neighborhood": self.neighborhood, "spatialReference": self.srid,
                "extent": [left, bottom, right, top] if self.datasets else None, "datasets": self.datasets}

    @classmethod
    def from_json(cls, data, name=None):
        return cls(data["method"], data.get("parameters"), data.get("neighborhood"), data.get("datasets"),
                   data.get("spatialReference", 0), name)

    def copy(self):
        return GALayer.from_json(copy.deepcopy(self.to_json()), self.name)

    def __str__(self):
        return str(self.name or "")

    # -- prediction

    def predict(self, qx, qy, leave_out=None):
        """(prediction, standard error or None) at one place, or None when it cannot be made (too few neighbours, a
        singular system). ``leave_out`` drops one point of the first dataset, for cross-validation."""
        xs, ys, zs = self.points(0)
        keep = np.ones(len(xs), bool)
        if leave_out is not None:
            keep[leave_out] = False
            xs, ys, zs = xs[keep], ys[keep], zs[keep]
        try:
            if self.method == "GLOBAL_POLYNOMIAL":
                return _polynomial(xs, ys, zs, qx, qy, int(self.parameters.get("power", 1)), None), None
            if self.method == "COKRIGING":
                sx, sy, sz = self.points(1)
                return _cokrige(self.parameters["semivariograms"], xs, ys, zs, sx, sy, sz, qx, qy)
            idx = select(self.neighborhood, xs, ys, qx, qy)
            if not len(idx):
                return None
            x, y, z = xs[idx], ys[idx], zs[idx]
            if self.method == "IDW":
                return _idw(x, y, z, qx, qy, float(self.parameters["power"])), None
            if self.method == "LOCAL_POLYNOMIAL":
                return _local_polynomial(self.parameters, x, y, z, qx, qy), None
            if self.method == "RADIAL_BASIS":
                return _radial(self.parameters, x, y, z, qx, qy), None
            return _krige(self.parameters, x, y, z, qx, qy)
        except (np.linalg.LinAlgError, _TooFew):
            return None

    def grid_values(self, xs, ys, block=(1, 1), cell=(0.0, 0.0)):
        """Predictions and standard errors at each (x, y) of a grid's centres, a block of predictions averaged in each
        cell (GA Layer To Grid's block averaging, at the centres of an even subdivision of the cell, INFERRED)."""
        bh, bv = block
        out = np.full((len(ys), len(xs)), np.nan)
        err = np.full((len(ys), len(xs)), np.nan)
        offsets = [((i + 0.5) / bh - 0.5) * cell[0] for i in range(bh)], [((j + 0.5) / bv - 0.5) * cell[1] for j in range(bv)]
        for r, y in enumerate(ys):
            for c, x in enumerate(xs):
                values, errors = [], []
                for ox in offsets[0]:
                    for oy in offsets[1]:
                        got = self.predict(x + ox, y + oy)
                        if got is None:
                            values = None
                            break
                        values.append(got[0])
                        errors.append(got[1])
                    if values is None:
                        break
                if values:
                    out[r, c] = float(np.mean(values))
                    if errors[0] is not None and len(errors) == 1:
                        err[r, c] = errors[0]
        return out, err


class _TooFew(Exception):
    pass


def _idw(x, y, z, qx, qy, power):
    d = np.hypot(x - qx, y - qy)
    if (d == 0).any():
        return float(z[d == 0][0])
    w = d ** -power
    return float((w * z).sum() / w.sum())


def _terms(order):
    return [(i, j) for total in range(order + 1) for i in range(total, -1, -1) for j in [total - i]]


def _polynomial(x, y, z, qx, qy, order, weights):
    """The polynomial of ``order`` in every term xⁱyʲ with i + j ≤ order, least squares (weighted when ``weights``),
    evaluated at (qx, qy). Coordinates are taken relative to the place and scaled, which leaves the fit unchanged."""
    terms = _terms(order)
    if len(x) < len(terms):
        raise _TooFew()
    s = max(float(np.ptp(x)), float(np.ptp(y)), 1.0)
    u, v = (x - qx) / s, (y - qy) / s
    design = np.column_stack([u ** i * v ** j for i, j in terms])
    w = np.ones(len(x)) if weights is None else weights
    root = np.sqrt(w)
    a = design * root[:, None]
    if np.linalg.matrix_rank(a) < len(terms):
        raise _TooFew()
    coefficients, *_ = np.linalg.lstsq(a, z * root, rcond=None)
    return float(coefficients[0])


_KERNELS = {
    "CONSTANT": lambda t: np.ones_like(t),
    "EPANECHNIKOV": lambda t: 1 - t * t,
    "QUARTIC": lambda t: (1 - t * t) ** 2,
}


def _local_polynomial(p, x, y, z, qx, qy):
    d = np.hypot(x - qx, y - qy)
    near = d <= float(p["bandwidth"])
    if not near.any():
        raise _TooFew()
    t = d[near] / float(p["bandwidth"])
    weights = _KERNELS[p["kernel"]](t)
    if (weights <= 0).all():
        raise _TooFew()
    return _polynomial(x[near], y[near], z[near], qx, qy, int(p["power"]), weights)


def _basis(kind, r, sigma):
    from scipy.special import exp1, k0
    euler = 0.5772156649015329
    r = np.asarray(r, dtype=np.float64)
    if kind == "THIN_PLATE_SPLINE":
        safe = np.where(r > 0, r, 1.0)
        return np.where(r > 0, (r * r) * np.log(safe), 0.0)
    if kind == "MULTIQUADRIC_FUNCTION":
        return np.sqrt(r * r + sigma * sigma)
    if kind == "INVERSE_MULTIQUADRIC_FUNCTION":
        return 1 / np.sqrt(r * r + sigma * sigma)
    if kind == "COMPLETELY_REGULARIZED_SPLINE":
        x = (sigma * r / 2) ** 2
        safe = np.where(x > 0, x, 1.0)
        return np.where(x > 0, -(np.log(safe) + exp1(safe) + euler), 0.0)
    if kind == "SPLINE_WITH_TENSION":
        x = sigma * r
        safe = np.where(x > 0, x, 1.0)
        return np.where(x > 0, np.log(safe / 2) + k0(safe) + euler, 0.0)
    raise ValueError(kind)


def _radial(p, x, y, z, qx, qy):
    """s(q) = Σ λᵢ φ(|q − xᵢ|) + trend, exact through the points (Franke 1982; Mitášová and Mitáš 1993 for the
    completely regularized spline and the spline with tension). The thin-plate spline carries a linear trend, the
    others a constant (INFERRED)."""
    kind, sigma = p["kernel"], float(p["parameter"])
    n = len(x)
    terms = [np.ones(n)] + ([x - qx, y - qy] if kind == "THIN_PLATE_SPLINE" else [])
    m = len(terms)
    if n < m + (1 if m > 1 else 0):
        raise _TooFew()
    big = np.zeros((n + m, n + m))
    big[:n, :n] = _basis(kind, np.hypot(x[:, None] - x, y[:, None] - y), sigma)
    for k, t in enumerate(terms):
        big[:n, n + k] = big[n + k, :n] = t
    right = np.concatenate([z, np.zeros(m)])
    solution = np.linalg.solve(big, right)
    at = _basis(kind, np.hypot(x - qx, y - qy), sigma)
    value = float(solution[:n] @ at + solution[n])   # the linear terms are 0 at the place itself
    return value


def _krige(p, x, y, z, qx, qy):
    """Ordinary, simple or universal kriging at one place (Cressie 1993, §3.2–3.4): the estimate and the standard
    error, the square root of the kriging variance."""
    model = p["semivariogram"]
    kind = p.get("kind", "ORDINARY")
    n = len(x)
    rx, ry = x - qx, y - qy
    if kind == "SIMPLE":
        mean = float(p["mean"])
        big = covariance(model, rx[:, None] - rx, ry[:, None] - ry)
        right = covariance(model, rx, ry)
        weights = np.linalg.solve(big, right)
        variance = float(covariance(model, 0.0, 0.0)) - float(weights @ right)
        return mean + float(weights @ (z - mean)), math.sqrt(max(variance, 0.0))
    drift = p.get("drift") if kind == "UNIVERSAL" else None
    terms = {None: [], "LINEAR": [rx, ry], "QUADRATIC": [rx, ry, rx * rx, rx * ry, ry * ry]}[drift]
    m = 1 + len(terms)
    if n < m:
        raise _TooFew()
    big = np.zeros((n + m, n + m))
    big[:n, :n] = gamma(model, rx[:, None] - rx, ry[:, None] - ry)
    big[:n, n] = big[n, :n] = 1.0
    for k, t in enumerate(terms, 1):
        big[:n, n + k] = big[n + k, :n] = t
    right = np.zeros(n + m)
    right[:n] = gamma(model, rx, ry)
    right[n] = 1.0   # the drift terms are 0 at the place itself
    solution = np.linalg.solve(big, right)
    variance = float(solution @ right)
    return float(solution[:n] @ z), math.sqrt(max(variance, 0.0))


def _cokrige(models, x, y, z, sx, sy, sz, qx, qy):
    """Ordinary cokriging of the first variable from both (Isaaks and Srivastava 1989, ch. 17): weights a on the
    primary data summing to 1 and b on the secondary summing to 0, in covariance form; the variance is
    C₁₁(0) − Σaᵢ C₁₁(xᵢ − x₀) − Σbⱼ C₁₂(yⱼ − x₀) − μ₁."""
    c11, c22, c12 = models["primary"], models["secondary"], models["cross"]
    n1, n2 = len(x), len(sx)
    size = n1 + n2 + 2
    big = np.zeros((size, size))
    big[:n1, :n1] = covariance(c11, x[:, None] - x, y[:, None] - y)
    big[:n1, n1:n1 + n2] = covariance(c12, x[:, None] - sx, y[:, None] - sy)
    big[n1:n1 + n2, :n1] = big[:n1, n1:n1 + n2].T
    big[n1:n1 + n2, n1:n1 + n2] = covariance(c22, sx[:, None] - sx, sy[:, None] - sy)
    big[:n1, n1 + n2] = big[n1 + n2, :n1] = 1.0
    big[n1:n1 + n2, n1 + n2 + 1] = big[n1 + n2 + 1, n1:n1 + n2] = 1.0
    right = np.zeros(size)
    right[:n1] = covariance(c11, x - qx, y - qy)
    right[n1:n1 + n2] = covariance(c12, sx - qx, sy - qy)
    right[n1 + n2] = 1.0
    solution = np.linalg.solve(big, right)
    a, b, mu = solution[:n1], solution[n1:n1 + n2], solution[n1 + n2]
    variance = float(covariance(c11, 0.0, 0.0)) - float(a @ right[:n1]) - float(b @ right[n1:n1 + n2]) - float(mu)
    return float(a @ z + b @ sz), math.sqrt(max(variance, 0.0))


# ---- reading and writing layers


def _path(value):
    text = os.fspath(value)
    if os.path.isabs(text):
        return text
    workspace = env.workspace
    if workspace and os.path.isdir(str(workspace)):
        return os.path.join(str(workspace), text)
    return os.path.abspath(text)


def _is_path(text):
    return any(s in text for s in ("/", "\\")) or os.path.splitext(text)[1] != ""


def open_layer(value, tool, need_data=True):
    """The layer a parameter names: a GALayer, a name this process holds, or a file of grapy's JSON."""
    if isinstance(value, GALayer):
        layer = value
    elif str(value) in LAYERS:
        layer = LAYERS[str(value)]
    else:
        path = _path(value)
        if not os.path.exists(path):
            raise ExecuteError(f"ERROR 000732: arcpy.ga.{tool}: Input geostatistical layer {value} does not exist or "
                               "is not supported.")
        try:
            data = json.load(open(path, encoding="utf-8"))
        except (ValueError, UnicodeDecodeError):
            data = None
        if not isinstance(data, dict) or data.get("format") != FORMAT:
            raise NotBuiltError(f"arcpy.ga.{tool}: '{value}' is not a geostatistical layer grapy wrote — ArcGIS's layer "
                                "files and model XML are not a published format, so grapy reads only its own JSON "
                                "(ADR-233 §5.2).")
        layer = GALayer.from_json(data, str(value))
    if need_data and not layer.datasets:
        raise ValueError(f"arcpy.ga.{tool}: '{value}' is a model with no data; make a layer from it with "
                         "GACreateGeostatisticalLayer.")
    return layer


def save_layer(value, layer):
    """Writes a layer to the name or file a parameter gives, and answers that value."""
    layer = layer.copy()
    text = str(value)
    if _is_path(text):
        path = _path(text)
        os.makedirs(os.path.dirname(path) or ".", exist_ok=True)
        with open(path, "w", encoding="utf-8") as out:
            json.dump(layer.to_json(), out, indent=1)
        layer.name = text
        return path
    layer.name = text
    LAYERS[text] = layer
    return text


# ---- the datasets string


def parse_datasets(value):
    """``in_datasets`` as [(dataset, field)]: a GeostatisticalDatasets object, or the documented string — each dataset
    followed by ``X=Shape Y=Shape F1=<field>``, datasets separated by ``;`` — or ``<dataset> <field>``."""
    if hasattr(value, "_pairs"):
        return value._pairs()
    out = []
    for part in [p.strip() for p in str(value).split(";") if p.strip()]:
        found = re.match(r"^(.*?)\s+((?:\w+=\S+\s*)+)$", part)
        if found:
            keys = dict(k.split("=", 1) for k in found.group(2).split())
            field = next((v for k, v in sorted(keys.items()) if re.fullmatch(r"F\d+", k, re.I)), None)
            if field is None:
                raise ValueError(f"'{part}' names no field (F1=<field>).")
            out.append((found.group(1).strip().strip("'\""), field))
        else:
            dataset, _, field = part.rpartition(" ")
            if not dataset:
                raise ValueError(f"'{part}' names no field.")
            out.append((dataset.strip().strip("'\""), field))
    return out


def read_dataset(dataset, field, tool):
    """The points' coordinates and values, and their EPSG code; coincident points are refused, as the Coincident
    Points environment that decides them is not built."""
    from . import sa
    from .analysis import _schema
    xs, ys, zs = sa._points(dataset, field, tool)
    if len({(a, b) for a, b in zip(xs, ys)}) != len(xs):
        raise NotBuiltError(f"arcpy.ga.{tool}: coincident points are not built in grapy — the Coincident Points "
                            "environment that decides how they are merged is not built.")
    return {"dataset": str(dataset), "field": str(field), "x": xs.tolist(), "y": ys.tolist(), "z": zs.tolist()}, \
        _schema(dataset)[2]
