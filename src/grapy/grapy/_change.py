"""Change between rasters and through time — arcpy.ia's change tools on grapy's multidimensional raster (ADR-236).

ComputeChange and ComputeChangeRaster compare two rasters, as their pages document. AnalyzeChangesUsingCCDC and
AnalyzeChangesUsingLandTrendr are built as **equivalents** of the published algorithms their pages name (CCDC: Zhu and
Woodcock 2014; LandTrendr: Kennedy, Yang and Cohen 2010): the pages give the parameters and not the algorithm.

**The change analysis raster is grapy's own** (ADR-236 §5.6, INFERRED): Esri's is a CRF whose bands the pages do not
lay out. grapy's is a multidimensional raster with one variable, ``CCDC`` or ``LandTrendr``, a slice per input slice
(CCDC) or per year (LandTrendr), and these bands in each slice:

- **CCDC**, for each detection band ``b``: ``Intercept_b``, ``Slope_b``, ``Cos1_b``, ``Sin1_b``, ``Cos2_b``,
  ``Sin2_b``, ``Cos3_b``, ``Sin3_b`` and ``RMSE_b`` of the model in force at that slice (t in days since 1970-01-01,
  coefficients the model does not use 0); then ``Change`` (1 at the first observation of a confirmed change) and
  ``Magnitude`` (the norm, over the detection bands, of each band's median residual over the confirming observations).
- **LandTrendr**: ``Fitted_Value``, ``Slope`` (per year), ``Vertex`` (1 at a vertex) and ``RMSE``, then
  ``Fitted_<band>`` for each other band when they are included.

DetectChangeUsingChangeAnalysis reads that raster; a change analysis raster from elsewhere is refused by name.
"""
from __future__ import annotations

import math

import numpy as np

from . import _mdim
from ._mdim import TIME, Cube, Variable
from ._mdimtools import _flag, _given, _refuse, _result, _upper
from ._runtime import NotBuiltError, equivalent


# ---- Compute Change


def _class_names(raster, field, tool):
    """Each class value's name from the raster's attribute table: ``field``, or CLASS_NAME, CLASSNAME or ClassName;
    the value itself when there is none."""
    rat = raster._rat
    if rat is None:
        if _given(field):
            raise ValueError(f"{tool}: the raster has no attribute table to read '{field}' from.")
        return {}, {}
    names = [n.lower() for n in rat.names()]
    value_field = "value" if "value" in names else ("classvalue" if "classvalue" in names else None)
    if value_field is None:
        raise ValueError(f"{tool}: the attribute table needs a Value or ClassValue field.")
    candidates = [str(field).lower()] if _given(field) else ["class_name", "classname"]
    name_field = next((c for c in candidates if c in names), None)
    values = rat.column(value_field)
    labels = {int(v): str(n) for v, n in zip(values, rat.column(name_field))} if name_field else {}
    colours = {}
    if all(c in names for c in ("red", "green", "blue")):
        colours = {int(v): (float(r), float(g), float(b)) for v, r, g, b in
                   zip(values, rat.column("red"), rat.column("green"), rat.column("blue"))}
    return labels, colours


def _categorical(first, second, from_classes, to_classes, filter_method, colours_by, from_field, to_field, other,
                 by_name, tool):
    from ._raster import Raster, _align, _Rat
    if not (first.isInteger and second.isInteger):
        raise ValueError(f"{tool}: CATEGORICAL_DIFFERENCE needs two integer (categorical) rasters.")
    arrays, masks, grid = _align([first, second], tool)
    a, b = arrays[0].astype(np.int64), arrays[1].astype(np.int64)
    mask = masks[0] | masks[1]
    names1, colours1 = _class_names(first, from_field, tool)
    names2, colours2 = _class_names(second, to_field, tool)

    def wanted(listed, names):
        items = listed if isinstance(listed, (list, tuple)) else [p for p in str(listed or "").split(";") if p.strip()]
        if not items:
            return None
        if by_name:
            back = {n.lower(): v for v, n in names.items()}
            missing = [i for i in items if str(i).lower() not in back and not str(i).lstrip("-").isdigit()]
            if missing:
                raise ValueError(f"{tool}: no class is named {', '.join(map(str, missing))}.")
            return {back.get(str(i).lower(), int(i) if str(i).lstrip("-").isdigit() else None) for i in items}
        return {int(i) for i in items}

    keep1, keep2 = wanted(from_classes, names1), wanted(to_classes, names2)
    if keep1 is not None:
        mask = mask | ~np.isin(a, list(keep1))
    if keep2 is not None:
        mask = mask | ~np.isin(b, list(keep2))
    kind = _upper(filter_method, "ALL")
    if kind not in ("ALL", "CHANGED_PIXELS_ONLY", "UNCHANGED_PIXELS_ONLY", "CHANGE_PIXELS_ONLY"):
        raise ValueError(f"{tool}: filter_method must be ALL, CHANGED_PIXELS_ONLY or UNCHANGED_PIXELS_ONLY.")
    colour_rule = _upper(colours_by, "AVERAGE")
    if colour_rule not in ("AVERAGE", "FROM_COLOR", "TO_COLOR"):
        raise ValueError(f"{tool}: define_transition_colors must be AVERAGE, FROM_COLOR or TO_COLOR.")
    changed = a != b
    classified = ~mask & (changed if kind.startswith("CHANGE") else (~changed if kind == "UNCHANGED_PIXELS_ONLY"
                                                                     else np.ones_like(changed)))
    pairs = sorted({(int(x), int(y)) for x, y in zip(a[classified], b[classified])})
    out = np.zeros(a.shape, np.int32)
    area = abs(grid.width * grid.height)
    has_colour = bool(colours1) and bool(colours2)
    fields = [("Value", "Integer", 5), ("ClassName", "String", 0), ("Class_From", "String", 0),
              ("Class_To", "String", 0), ("Count", "Integer", 1), ("Area", "Double", 0)]
    if has_colour:
        fields += [("Red", "Integer", 0), ("Green", "Integer", 0), ("Blue", "Integer", 0)]
    rows = []
    for number, (x, y) in enumerate(pairs, 1):
        here = classified & (a == x) & (b == y)
        out[here] = number
        n1, n2 = names1.get(x, str(x)), names2.get(y, str(y))
        row = [number, n1 if x == y and n1 == n2 else f"{n1}>{n2}", n1, n2, int(here.sum()), float(here.sum() * area)]
        if has_colour:
            c1, c2 = colours1.get(x, (0, 0, 0)), colours2.get(y, (0, 0, 0))
            c = {"AVERAGE": tuple((p + q) / 2 for p, q in zip(c1, c2)), "FROM_COLOR": c1, "TO_COLOR": c2}[colour_rule]
            row += [int(round(v)) for v in c]
        rows.append(row)
    rest = ~mask & ~classified
    if rest.any():
        row = [0, other, "", "", int(rest.sum()), float(rest.sum() * area)] + ([0, 0, 0] if has_colour else [])
        rows.insert(0, row)
    result = Raster._of(out, mask, grid)
    result._rat = _Rat(fields, rows)
    return result


def _continuous(first, second, method, tool):
    from ._raster import _align, Raster
    if method in ("DIFFERENCE", "RELATIVE_DIFFERENCE"):
        if first.bandCount != second.bandCount:
            raise ValueError(f"{tool}: the two rasters must have the same number of bands.")
        arrays, masks = [], []
        grid = None
        for k in range(1, first.bandCount + 1):
            (x, y), (m1, m2), grid = _align([first._band(k), second._band(k)], tool)
            x, y = x.astype(np.float64), y.astype(np.float64)
            with np.errstate(all="ignore"):
                if method == "DIFFERENCE":
                    value = y - x
                else:
                    value = (y - x) / np.maximum(y, x)
            empty = m1 | m2 | ~np.isfinite(value)
            if method == "DIFFERENCE" and first.isInteger and second.isInteger:
                value = np.where(empty, 0, value).astype(np.int64)
            arrays.append(np.where(empty, 0, value))
            masks.append(empty)
        if len(arrays) == 1:
            return Raster._of(arrays[0], masks[0], grid)
        return Raster._of_bands(arrays, masks, grid)
    if first.bandCount != second.bandCount or first.bandCount < 2:
        raise ValueError(f"{tool}: {method} compares two multiband rasters with the same bands.")
    pairs = [_align([first._band(k), second._band(k)], tool) for k in range(1, first.bandCount + 1)]
    grid = pairs[0][2]
    x = np.stack([p[0][0].astype(np.float64) for p in pairs])
    y = np.stack([p[0][1].astype(np.float64) for p in pairs])
    mask = np.logical_or.reduce([p[1][0] | p[1][1] for p in pairs])
    with np.errstate(all="ignore"):
        if method == "SPECTRAL_EUCLIDEAN_DISTANCE":
            value = np.sqrt(((y - x) ** 2).sum(axis=0))
        elif method == "SPECTRAL_ANGLE_DIFFERENCE":
            cosine = (x * y).sum(axis=0) / (np.sqrt((x * x).sum(axis=0)) * np.sqrt((y * y).sum(axis=0)))
            value = np.arccos(np.clip(cosine, -1, 1))
        else:
            value = (np.argmax(np.abs(y - x), axis=0) + 1).astype(np.int32)
    mask = mask | ~np.isfinite(value)
    return Raster._of(np.where(mask, 0, value), mask, grid)


def _open(value, tool):
    from ._raster import _open_bands
    return _open_bands(value, tool)


def ComputeChange(raster1, raster2, method="DIFFERENCE", from_class_values=None, to_class_values=None,
                  filter_method="CHANGED_PIXELS_ONLY", define_transition_colors="AVERAGE", extent_type="IntersectionOf",
                  cellsize_type="MaxOf"):
    """The change from ``raster1`` (earlier) to ``raster2`` (later): DIFFERENCE, ``raster2 − raster1``;
    RELATIVE_DIFFERENCE, that divided by the larger of the two (the formulas of the Compute Change Raster page); or
    CATEGORICAL_DIFFERENCE, a class per transition (``Forest>Urban``) and, under ALL, the class name for an unchanged
    pixel, with the pixels not classified in ``Other``, as documented. Class names and colours come from the rasters'
    attribute tables (CLASS_NAME or CLASSNAME; Red, Green and Blue), and a transition's colour is their average or the
    from or to class's (``define_transition_colors``).

    Refused: an extent other than the inputs' intersection where their extents differ, and differing cell sizes —
    resampling is not built.

    INFERRED (ADR-236 §5.7): transitions are numbered from 1 in order of (from value, to value), and ``Other`` is 0;
    a pixel of a class not listed in ``from_class_values`` or ``to_class_values`` is NoData; the attribute table's
    fields are Value, ClassName, Class_From, Class_To, Count, Area and, when both inputs have colours, Red, Green and
    Blue; an integer difference stays integer."""
    tool = "ComputeChange"
    first, second = _open(raster1, tool), _open(raster2, tool)
    _extent(first, second, extent_type, cellsize_type, tool)
    kind = _upper(method, "DIFFERENCE")
    if kind == "CATEGORICAL_DIFFERENCE":
        return _categorical(first, second, from_class_values, to_class_values, filter_method,
                            define_transition_colors, None, None, "Other", False, tool)
    if kind not in ("DIFFERENCE", "RELATIVE_DIFFERENCE"):
        raise ValueError(f"{tool}: method must be DIFFERENCE, RELATIVE_DIFFERENCE or CATEGORICAL_DIFFERENCE.")
    return _continuous(first, second, kind, tool)


def _extent(first, second, extent_type, cellsize_type, tool):
    kind = str(extent_type or "IntersectionOf")
    if kind.lower() not in ("firstof", "intersectionof", "unionof", "lastof"):
        raise ValueError(f"{tool}: extent_type must be FirstOf, IntersectionOf, UnionOf or LastOf.")
    if str(cellsize_type or "MaxOf").lower() not in ("firstof", "minof", "maxof", "meanof", "lastof"):
        raise ValueError(f"{tool}: cellsize_type must be FirstOf, MinOf, MaxOf, MeanOf or LastOf.")
    if not first._grid.same(second._grid) and kind.lower() != "intersectionof":
        _refuse(tool, "extent_type", extent_type, "rasters of different extents are combined over their intersection "
                "only; another extent needs NoData filling or resampling")


def ComputeChangeRaster(from_raster, to_raster, compute_change_method="DIFFERENCE", from_classes=None, to_classes=None,
                        filter_method="ALL", define_transition_colors="AVERAGE", from_classname_field=None,
                        to_classname_field=None):
    """The tool's form of ComputeChange, with the multiband methods its page adds: SPECTRAL_EUCLIDEAN_DISTANCE, the
    Euclidean distance between the two pixel vectors; SPECTRAL_ANGLE_DIFFERENCE, the angle between them in radians,
    ``arccos(a·b / (|a||b|))`` (Kruse et al. 1993, Remote Sensing of Environment 44); and BAND_WITH_MOST_CHANGE.
    ``from_classes`` and ``to_classes`` are class names (or values); unclassified pixels go to ``No Change``, as the
    tool's page names it.

    INFERRED (ADR-236 §5.7): as ComputeChange; BAND_WITH_MOST_CHANGE is the one-based band whose absolute difference
    is largest, the first on a tie."""
    tool = "ComputeChangeRaster"
    first, second = _open(from_raster, tool), _open(to_raster, tool)
    kind = _upper(compute_change_method, "DIFFERENCE")
    if kind == "CATEGORICAL_DIFFERENCE":
        return _categorical(first, second, from_classes, to_classes, filter_method, define_transition_colors,
                            from_classname_field, to_classname_field, "No Change", True, tool)
    if kind not in ("DIFFERENCE", "RELATIVE_DIFFERENCE", "SPECTRAL_EUCLIDEAN_DISTANCE", "SPECTRAL_ANGLE_DIFFERENCE",
                    "BAND_WITH_MOST_CHANGE"):
        raise ValueError(f"{tool}: '{compute_change_method}' is not one of its methods.")
    return _continuous(first, second, kind, tool)


# ---- CCDC (Zhu and Woodcock 2014)


_T = 365.25


def _ccdc_design(t, coefficients):
    w = 2 * np.pi / _T
    columns = [np.ones_like(t), t]
    for k in range(1, 4):
        columns += [np.cos(k * w * t), np.sin(k * w * t)]
    return np.column_stack(columns)[:, :coefficients]


def _ccdc_fit(t, y, coefficients):
    a = _ccdc_design(t, coefficients)
    coef, *_ = np.linalg.lstsq(a, y, rcond=None)
    residual = y - a @ coef
    rmse = np.sqrt((residual ** 2).sum(axis=0) / max(1, len(t) - coefficients))
    full = np.zeros((8, y.shape[1]))
    full[:coefficients] = coef
    return full, rmse


def _ccdc_predict(coef, t):
    return _ccdc_design(np.atleast_1d(t), 8) @ coef


def _complexity(n):
    return 4 if n < 18 else (6 if n < 24 else 8)


def _ccdc_pixel(t, y, threshold, consecutive, update_days):
    """One pixel's CCDC: [(start index, end index, coefficients, rmse)] and [(index of change, magnitude)]."""
    from scipy import stats
    n, k = y.shape
    critical = stats.chi2.ppf(threshold, k)
    segments, changes = [], []
    i = 0
    while i + 12 <= n:
        # initialisation: twelve clear observations spanning a year, stable (Zhu and Woodcock 2014, §3.3.1)
        j = i + 12
        while j <= n and t[j - 1] - t[i] < 365:
            j += 1
        if j > n:
            break
        coef, rmse = _ccdc_fit(t[i:j], y[i:j], 4)
        rmse = np.maximum(rmse, 1e-12)
        fitted = _ccdc_predict(coef, t[[i, j - 1]])
        test = (np.abs(coef[1] * (t[j - 1] - t[i])) + np.abs(y[i] - fitted[0]) + np.abs(y[j - 1] - fitted[1])) / (3 * rmse)
        if test.mean() > 1:
            i += 1
            continue
        start, members = i, list(range(i, j))
        last_fit = t[j - 1]
        coef, rmse = _ccdc_fit(t[members], y[members], _complexity(len(members)))
        rmse = np.maximum(rmse, 1e-12)
        m = j
        broke = False
        while m < n:
            ahead = list(range(m, min(n, m + consecutive)))
            if len(ahead) < consecutive:
                break
            residual = y[ahead] - _ccdc_predict(coef, t[ahead])
            score = ((residual / rmse) ** 2).sum(axis=1)
            if (score > critical).all():
                segments.append((start, m - 1, coef, rmse))
                changes.append((m, float(np.linalg.norm(np.median(residual, axis=0)))))
                i, broke = m, True
                break
            if score[0] > critical:
                m += 1  # a single outlier, left out of the model
                continue
            members.append(m)
            if t[m] - last_fit >= update_days:
                coef, rmse = _ccdc_fit(t[members], y[members], _complexity(len(members)))
                rmse = np.maximum(rmse, 1e-12)
                last_fit = t[m]
            m += 1
        if not broke:
            coef, rmse = _ccdc_fit(t[members], y[members], _complexity(len(members)))
            segments.append((start, n - 1, coef, rmse))
            break
    return segments, changes


@equivalent("Continuous Change Detection and Classification: a harmonic model of up to three harmonics fitted by "
            "least squares and monitored by a chi-squared test on consecutive normalised residuals",
            "Zhu and Woodcock 2014, Remote Sensing of Environment 144, 152–171")
def AnalyzeChangesUsingCCDC(in_multidimensional_raster, bands=None, tmask_bands=None, chi_squared_threshold=0.99,
                            min_anomaly_observations=6, update_frequency=1):
    """EQUIVALENT: Zhu and Woodcock's (2014) CCDC on each pixel's time series. A model
    ``y = a0 + c1·t + Σₖ (aₖ·cos(2πk·t/T) + bₖ·sin(2πk·t/T))`` (T = 365.25 days; four, six or eight coefficients as the
    segment holds fewer than 18, fewer than 24 or more observations) is initialised on twelve observations spanning a
    year, moved on until stable (slope over the window and first and last residuals within three RMSEs, averaged over
    the bands); each next observation is tested by the sum over the detection bands of its squared residual over RMSE
    against the chi-squared quantile at ``chi_squared_threshold``; ``min_anomaly_observations`` consecutive anomalies
    confirm a change at the first of them, and a lone one is left out as an outlier; the model is refitted when
    ``update_frequency`` years of new observations have come in. The model is fitted by ordinary least squares, as in
    the 2014 paper. The output is grapy's change analysis raster (see the module).

    Refused: ``tmask_bands``, the paper's multitemporal cloud mask (Zhu and Woodcock 2014b), which grapy does not
    build; give masked data, as the page recommends.

    INFERRED (ADR-236 §5.6): ``bands`` are one-based band numbers (the page says band IDs); NoData slices are left out
    of a pixel's series; the input is one variable with time slices."""
    tool = "AnalyzeChangesUsingCCDC"
    if _given(tmask_bands) and list(tmask_bands if isinstance(tmask_bands, (list, tuple)) else [tmask_bands]):
        _refuse(tool, "tmask_bands", tmask_bands, "the multitemporal cloud mask (Tmask) is not built; give masked data")
    threshold = float(chi_squared_threshold if _given(chi_squared_threshold) else 0.99)
    consecutive = int(min_anomaly_observations if _given(min_anomaly_observations) else 6)
    update_days = float(update_frequency if _given(update_frequency) else 1) * _T
    if not 0 < threshold < 1 or consecutive < 1:
        raise ValueError(f"{tool}: the threshold is a probability and the anomaly count at least 1.")
    cube = _mdim.cube_of(in_multidimensional_raster, tool)
    v = cube.chosen(None, TIME)[0]
    other, indexes = v.along(TIME)[0]
    times = [v.coords[i][TIME] for i in indexes]
    if len(indexes) < 12 or _mdim.days(times[-1]) - _mdim.days(times[0]) < 365:
        raise ValueError(f"{tool}: the input needs at least 12 slices spanning at least a year, as documented.")
    chosen = list(range(v.bands)) if not _given(bands) else [int(b) - 1 for b in (
        bands if isinstance(bands, (list, tuple)) else [bands])]
    if not chosen:
        chosen = list(range(v.bands))
    if any(b < 0 or b >= v.bands for b in chosen):
        raise ValueError(f"{tool}: the slices have {v.bands} bands; bands are numbered from 1.")
    t_all = np.array([_mdim.days(x) for x in times])
    data, mask = v.data[indexes], v.mask[indexes]
    nb = len(chosen)
    rows, cols = cube.grid.rows, cube.grid.columns
    names = [f"{p}_{b + 1}" for b in chosen for p in ("Intercept", "Slope", "Cos1", "Sin1", "Cos2", "Sin2", "Cos3",
                                                      "Sin3", "RMSE")] + ["Change", "Magnitude"]
    out = np.zeros((len(indexes), len(names), rows, cols))
    empty = np.ones((len(indexes), len(names), rows, cols), bool)
    for r in range(rows):
        for c in range(cols):
            ok = ~mask[:, chosen, r, c].any(axis=1)
            where = np.flatnonzero(ok)
            if where.size < 12:
                continue
            t = t_all[where]
            y = data[where][:, chosen, r, c].astype(np.float64)
            segments, changes = _ccdc_pixel(t, y, threshold, consecutive, update_days)
            if not segments:
                continue
            for s, (a, b, coef, rmse) in enumerate(segments):
                lo = 0 if s == 0 else where[a]
                hi = len(indexes) - 1 if s == len(segments) - 1 else where[segments[s + 1][0]] - 1
                for slot in range(lo, hi + 1):
                    for q in range(nb):
                        out[slot, q * 9:q * 9 + 8, r, c] = coef[:, q]
                        out[slot, q * 9 + 8, r, c] = rmse[q]
                    empty[slot, :, r, c] = False
            for at, magnitude in changes:
                out[where[at], -2, r, c] = 1
                out[where[at], -1, r, c] = magnitude
    variable = Variable("CCDC", [TIME], [{TIME: x} for x in times], np.where(empty, 0, out), empty,
                        {"grapy_change": "CCDC"}, names)
    return _result([variable], cube.grid)


# ---- LandTrendr (Kennedy, Yang and Cohen 2010)


def _despike(y, threshold):
    """Kennedy et al.'s spike dampening: an interior value whose departure from its neighbours is mostly a spike —
    1 − |y₊ − y₋| / (|y − y₋| + |y − y₊|) above the threshold — is replaced by its neighbours' mean, the largest spike
    first, until none is left. A threshold of 1 dampens nothing."""
    y = y.copy()
    if threshold >= 1:
        return y
    for _ in range(len(y)):
        best, at = threshold, None
        for i in range(1, len(y) - 1):
            total = abs(y[i] - y[i - 1]) + abs(y[i] - y[i + 1])
            if total <= 0:
                continue
            spike = 1 - abs(y[i + 1] - y[i - 1]) / total
            if spike > best:
                best, at = spike, i
        if at is None:
            break
        y[at] = (y[at - 1] + y[at + 1]) / 2
    return y


def _hinge_fit(x, y, vertices):
    """The continuous piecewise-linear least-squares fit through the given vertex positions (a linear spline)."""
    xv = x[vertices]
    columns = [np.ones_like(x), x - xv[0]] + [np.maximum(0, x - k) for k in xv[1:-1]]
    a = np.column_stack(columns)
    coef, *_ = np.linalg.lstsq(a, y, rcond=None)
    fitted = a @ coef
    return fitted, ((y - fitted) ** 2).sum()


def _angles(x, fitted, vertices, scale):
    """The change in direction at each interior vertex, on the trajectory scaled by its range."""
    out = []
    for k in range(1, len(vertices) - 1):
        a, b, c = vertices[k - 1], vertices[k], vertices[k + 1]
        s1 = math.atan2((fitted[b] - fitted[a]) / scale, x[b] - x[a])
        s2 = math.atan2((fitted[c] - fitted[b]) / scale, x[c] - x[b])
        out.append(abs(s2 - s1))
    return out


def _landtrendr_pixel(x, y, max_segments, overshoot, spike, recovery, prevent_one_year, increasing, p_threshold,
                      proportion):
    from scipy import stats
    n = len(x)
    y = _despike(y, spike)
    scale = (y.max() - y.min()) or 1.0
    vertices = [0, n - 1]
    # vertices found where the current fit departs most (Kennedy et al. 2010, §2.2.2)
    while len(vertices) < min(n, max_segments + 1 + overshoot):
        fitted, _ = _hinge_fit(x, y, vertices)
        departure = np.abs(y - fitted)
        departure[vertices] = -1
        departure[[i for i in range(n) if any(abs(i - v) < 1 for v in vertices)]] = -1
        best = int(np.argmax(departure))
        if departure[best] <= 0:
            break
        vertices = sorted(vertices + [best])
    # culled by the smallest change in angle down to max_segments + 1
    while len(vertices) > max_segments + 1:
        fitted, _ = _hinge_fit(x, y, vertices)
        angles = _angles(x, fitted, vertices, scale)
        vertices.pop(1 + int(np.argmin(angles)))
    models = []
    current = list(vertices)
    sst = ((y - y.mean()) ** 2).sum()
    while len(current) >= 2:
        fitted, sse = _hinge_fit(x, y, current)
        p_count = len(current)
        if n > p_count and sst > 0:
            f = ((sst - sse) / (p_count - 1)) / (sse / (n - p_count)) if sse > 0 else np.inf
            p = float(stats.f.sf(f, p_count - 1, n - p_count)) if np.isfinite(f) else 0.0
        else:
            p = 1.0
        allowed = True
        for a, b in zip(current[:-1], current[1:]):
            slope = (fitted[b] - fitted[a]) / (x[b] - x[a])
            is_recovery = slope > 0 if increasing else slope < 0
            if is_recovery and abs(slope) / scale > 1.0 / max(recovery, 1e-12):
                allowed = False
            if is_recovery and prevent_one_year and x[b] - x[a] <= 1:
                allowed = False
        models.append((p, list(current), fitted, sse, allowed))
        if len(current) == 2:
            break
        angles = _angles(x, fitted, current, scale)
        current.pop(1 + int(np.argmin(angles)))
    good = [m for m in models if m[4]]
    if not good:
        flat = np.full(n, y.mean())
        return [0, n - 1], flat, float(np.sqrt(((y - flat) ** 2).mean()))
    passing = [m for m in good if m[0] <= p_threshold] or good
    lowest = min(m[0] for m in passing)
    chosen = max((m for m in passing if m[0] <= lowest * proportion), key=lambda m: len(m[1]))
    return chosen[1], chosen[2], float(np.sqrt(chosen[3] / n))


@equivalent("LandTrendr temporal segmentation: despiking, vertex search by largest departure, culling by angle, "
            "continuous piecewise-linear least squares and selection by the F-test's p-value",
            "Kennedy, Yang and Cohen 2010, Remote Sensing of Environment 114, 2897–2910")
def AnalyzeChangesUsingLandTrendr(in_multidimensional_raster, processing_band=None, snapping_date="06-30",
                                  max_num_segments=5, vertex_count_overshoot=2, spike_threshold=0.9,
                                  recovery_threshold=0.25, prevent_one_year_recovery="PREVENT_ONE_YEAR_RECOVERY",
                                  recovery_trend="INCREASING_TREND", min_num_observations=6, best_model_proportion=1.25,
                                  pvalue_threshold=0.01, output_other_bands="EXCLUDE_OTHER_BANDS"):
    """EQUIVALENT: Kennedy, Yang and Cohen's (2010) LandTrendr on each pixel's yearly trajectory. One slice per year,
    the one nearest ``snapping_date`` (as documented); spikes are dampened; vertices are added where the fit departs
    most, up to ``max_num_segments + 1 + vertex_count_overshoot``, and culled by the smallest change of angle to
    ``max_num_segments + 1``; simpler models are made by removing vertices the same way; each is fitted as a continuous
    piecewise-linear least-squares line and scored by the F-test's p-value against the mean. Models with a recovery
    faster than ``1/recovery_threshold`` (of the trajectory's range per year), or a one-year recovery when prevented,
    are disallowed. The model chosen is the one with the most vertices whose p-value is within
    ``best_model_proportion`` of the lowest among those under ``pvalue_threshold`` (or of all, when none is), as the
    page describes. ``INCLUDE_OTHER_BANDS`` fits the other bands on the processing band's vertices.

    INFERRED (ADR-236 §5.6): the despiking proportion and the angle measure (on the trajectory scaled by its range);
    ``processing_band`` is a one-based band number; a pixel with fewer than ``min_num_observations`` valid years is
    NoData; the output layout is grapy's (see the module)."""
    tool = "AnalyzeChangesUsingLandTrendr"
    cube = _mdim.cube_of(in_multidimensional_raster, tool)
    v = cube.chosen(None, TIME)[0]
    _, indexes = v.along(TIME)[0]
    month, day = (int(p) for p in str(snapping_date or "06-30").split("-")[:2])
    by_year = {}
    for i in indexes:
        t = v.coords[i][TIME]
        year = _mdim.parts(t)[0]
        target = _mdim.to_time(f"{year}-{month}-{day}")
        gap = abs(_mdim.days(t) - _mdim.days(target))
        if year not in by_year or gap < by_year[year][0]:
            by_year[year] = (gap, i)
    years = sorted(by_year)
    least = int(min_num_observations)
    if len(years) < least:
        raise ValueError(f"{tool}: the input has {len(years)} years; at least {least} are needed.")
    slots = [by_year[y][1] for y in years]
    band = (int(processing_band) - 1) if _given(processing_band) else 0
    if not 0 <= band < v.bands:
        raise ValueError(f"{tool}: the slices have {v.bands} bands; bands are numbered from 1.")
    others = [b for b in range(v.bands) if b != band] if _flag(output_other_bands, "INCLUDE_OTHER_BANDS") else []
    names = ["Fitted_Value", "Slope", "Vertex", "RMSE"] + [f"Fitted_{v.names_of_bands()[b]}" for b in others]
    rows, cols = cube.grid.rows, cube.grid.columns
    out = np.zeros((len(years), len(names), rows, cols))
    empty = np.ones(out.shape, bool)
    data, mask = v.data[slots], v.mask[slots]
    x_all = np.array(years, float)
    increasing = _upper(recovery_trend, "INCREASING_TREND") == "INCREASING_TREND"
    prevent = _flag(prevent_one_year_recovery, "PREVENT_ONE_YEAR_RECOVERY", True)
    for r in range(rows):
        for c in range(cols):
            ok = ~mask[:, band, r, c]
            where = np.flatnonzero(ok)
            if where.size < least:
                continue
            x, y = x_all[where], data[where, band, r, c].astype(np.float64)
            vertices, fitted, rmse = _landtrendr_pixel(
                x, y, int(max_num_segments), int(vertex_count_overshoot), float(spike_threshold),
                float(recovery_threshold), prevent, increasing, float(pvalue_threshold), float(best_model_proportion))
            full = np.interp(x_all, x, fitted)
            vx = x[vertices]
            slope = np.zeros(len(years))
            for a, b in zip(vertices[:-1], vertices[1:]):
                s = (fitted[b] - fitted[a]) / (x[b] - x[a])
                slope[(x_all > x[a]) & (x_all <= x[b])] = s
            out[:, 0, r, c], out[:, 1, r, c] = full, slope
            out[:, 2, r, c] = np.isin(x_all, vx).astype(float)
            out[:, 3, r, c] = rmse
            for q, b in enumerate(others):
                okb = ok & ~mask[:, b, r, c]
                if okb.sum() >= 2:
                    xb = x_all[okb]
                    keep = [k for k in vx if k in xb]
                    if len(keep) >= 2:
                        fb, _ = _hinge_fit(xb, data[okb, b, r, c].astype(np.float64),
                                           [int(np.flatnonzero(xb == k)[0]) for k in keep])
                        out[:, 4 + q, r, c] = np.interp(x_all, xb, fb)
            empty[:, :, r, c] = False
    coords = [{TIME: v.coords[i][TIME]} for i in slots]
    variable = Variable("LandTrendr", [TIME], coords, np.where(empty, 0, out), empty,
                        {"grapy_change": "LANDTRENDR", "grapy_snapping_date": f"{month:02d}-{day:02d}"}, names)
    return _result([variable], cube.grid)


# ---- Detect Change Using Change Analysis Raster


_TYPES_CCDC = ("TIME_OF_LATEST_CHANGE", "TIME_OF_EARLIEST_CHANGE", "TIME_OF_LARGEST_CHANGE", "NUM_OF_CHANGES")
_TYPES_LT = _TYPES_CCDC + ("TIME_OF_LONGEST_CHANGE", "TIME_OF_SHORTEST_CHANGE", "TIME_OF_FASTEST_CHANGE",
                           "TIME_OF_SLOWEST_CHANGE")


# A detected change's date as Pro 3.7 writes it: days since 1899-12-30 (measured 2026-10-31, ADR-236 condition 3).
_DATE_EPOCH_DAYS = -25569.0  # 1899-12-30 in days since 1970-01-01


def _events(v, r, c, segment_date):
    """The pixel's change events: dicts of date (a decimal year, for ordering and the year filter), when (the date as
    days since 1899-12-30, which the output holds), magnitude (signed), duration, start and end value."""
    events = []
    times = [_mdim.decimal_year(co[TIME]) for co in v.coords]
    names = v.names_of_bands()
    if v.attrs.get("grapy_change") == "CCDC":
        change, magnitude = names.index("Change"), names.index("Magnitude")
        for i, t in enumerate(times):
            if not v.mask[i, change, r, c] and v.data[i, change, r, c] == 1:
                events.append({"date": t, "when": _mdim.days(v.coords[i][TIME]) - _DATE_EPOCH_DAYS,
                               "magnitude": float(v.data[i, magnitude, r, c]), "duration": None,
                               "start": None, "end": None})
        return events
    vertex, fitted = names.index("Vertex"), names.index("Fitted_Value")
    if v.mask[:, vertex, r, c].all():
        return events
    at = [i for i in range(len(times)) if v.data[i, vertex, r, c] == 1 and not v.mask[i, vertex, r, c]]
    for a, b in zip(at[:-1], at[1:]):
        start, end = float(v.data[a, fitted, r, c]), float(v.data[b, fitted, r, c])
        year_a, year_b = math.floor(times[a]), math.floor(times[b])
        year = year_a if segment_date == "BEGINNING_OF_SEGMENT" else year_b
        # A segment is dated at its year's snapping date, as Pro 3.7 dates it (measured, ADR-236 condition 3).
        snap = _mdim.to_time(f"{year}-{v.attrs.get('grapy_snapping_date', '06-30')}")
        events.append({"date": year, "when": _mdim.days(snap) - _DATE_EPOCH_DAYS,
                       "magnitude": end - start, "duration": year_b - year_a + 1, "start": start, "end": end})
    return events


def _detect(source, change_type, count, segment_date, direction, by_year, years, by_duration, durations, by_magnitude,
            magnitudes, by_start, starts, by_end, ends, tool):
    from ._raster import Raster
    cube = _mdim.cube_of(source, tool)
    v = cube.variable()
    kind = v.attrs.get("grapy_change")
    if kind not in ("CCDC", "LANDTRENDR"):
        raise NotBuiltError(f"{tool}: '{source}' is not a change analysis raster grapy made; the layout of one made "
                            "elsewhere is not documented (ADR-236 §5.6).")
    change = _upper(change_type, "TIME_OF_LATEST_CHANGE")
    if change not in (_TYPES_CCDC if kind == "CCDC" else _TYPES_LT):
        raise ValueError(f"{tool}: '{change_type}' is not available for a {kind} change analysis raster.")
    landtrendr_only = {"segment_date": segment_date not in (None, "", "BEGINNING_OF_SEGMENT"),
                       "change_direction": _upper(direction, "ALL") != "ALL", "filter_by_duration": by_duration,
                       "filter_by_start_value": by_start, "filter_by_end_value": by_end}
    if kind == "CCDC":
        for name, used in landtrendr_only.items():
            if used:
                raise ValueError(f"{tool}: {name} applies to a LandTrendr change analysis raster only, as documented.")
    seg = _upper(segment_date, "BEGINNING_OF_SEGMENT")
    want = _upper(direction, "ALL")
    n = max(1, int(count if _given(count) else 1))
    rows, cols = cube.grid.rows, cube.grid.columns
    layers = 1 if change == "NUM_OF_CHANGES" else n
    out = np.zeros((layers, rows, cols))
    empty = np.ones((layers, rows, cols), bool)
    for r in range(rows):
        for c in range(cols):
            if v.mask[:, :, r, c].all():
                continue
            events = []
            for e in _events(v, r, c, seg):
                if want == "INCREASE" and e["magnitude"] <= 0 or want == "DECREASE" and e["magnitude"] >= 0:
                    continue
                if by_year and not (years[0] <= math.floor(e["date"]) <= years[1]):
                    continue
                if by_duration and not (durations[0] <= e["duration"] <= durations[1]):
                    continue
                if by_magnitude and not (magnitudes[0] <= abs(e["magnitude"]) <= magnitudes[1]):
                    continue
                if by_start and not (starts[0] <= e["start"] <= starts[1]):
                    continue
                if by_end and not (ends[0] <= e["end"] <= ends[1]):
                    continue
                events.append(e)
            if change == "NUM_OF_CHANGES":
                out[0, r, c], empty[0, r, c] = len(events), False
                continue
            key, reverse = {"TIME_OF_LATEST_CHANGE": ("date", True), "TIME_OF_EARLIEST_CHANGE": ("date", False),
                            "TIME_OF_LARGEST_CHANGE": ("size", True), "TIME_OF_LONGEST_CHANGE": ("duration", True),
                            "TIME_OF_SHORTEST_CHANGE": ("duration", False), "TIME_OF_FASTEST_CHANGE": ("rate", True),
                            "TIME_OF_SLOWEST_CHANGE": ("rate", False)}[change]
            for e in events:
                e["size"] = abs(e["magnitude"])
                e["rate"] = abs(e["magnitude"]) / e["duration"] if e["duration"] else abs(e["magnitude"])
            events.sort(key=lambda e: (e[key], e["date"]), reverse=reverse)
            for k, e in enumerate(events[:n]):
                out[k, r, c], empty[k, r, c] = e["when"], False
    if layers == 1:
        return Raster._of(np.where(empty[0], 0, out[0]), empty[0], cube.grid)
    return Raster._of_bands([np.where(empty[k], 0, out[k]) for k in range(layers)], list(empty), cube.grid)


def _pair(flag, low, high, name, defaults=None):
    on = _flag(flag, name, False)
    if not on:
        return False, None
    if not _given(low) or not _given(high):
        if defaults is None:
            raise ValueError(f"{name} needs its minimum and maximum.")
        low, high = defaults
    return True, (float(low), float(high))


def DetectChangeUsingChangeAnalysisRaster(in_change_analysis_raster, change_type="TIME_OF_LATEST_CHANGE",
                                          max_number_changes=1, segment_date="BEGINNING_OF_SEGMENT",
                                          change_direction="ALL", filter_by_year="NO_FILTER_BY_YEAR", min_year=None,
                                          max_year=None, filter_by_duration="NO_FILTER_BY_DURATION",
                                          min_duration=None, max_duration=None,
                                          filter_by_magnitude="NO_FILTER_BY_MAGNITUDE", min_magnitude=None,
                                          max_magnitude=None, filter_by_start_value="NO_FILTER_BY_START_VALUE",
                                          min_start_value=None, max_start_value=None,
                                          filter_by_end_value="NO_FILTER_BY_END_VALUE", min_end_value=None,
                                          max_end_value=None):
    """The changes in a change analysis raster grapy made: the dates of the latest, earliest or largest changes (CCDC
    and LandTrendr) or of the longest, shortest, fastest or slowest change segments (LandTrendr), a band per rank up to
    ``max_number_changes``, or the number of changes, with the documented filters. A LandTrendr segment's date is its
    first or last year (``segment_date``) and its duration ``end − start + 1`` years, as documented; magnitude filters
    are absolute, as documented.

    A date is days since 1899-12-30, a CCDC change's at its slice and a LandTrendr segment's at its year's snapping
    date, as Pro 3.7 writes them (measured 2026-10-31, ADR-236 condition 3). INFERRED (ADR-236 §5.3, §5.6): a CCDC change's
    magnitude is the change analysis raster's Magnitude band; a LandTrendr segment's is its fitted end less its fitted
    start; every segment between two vertices is a change; a rank with no change is NoData."""
    tool = "DetectChangeUsingChangeAnalysisRaster"
    return _detect(in_change_analysis_raster, change_type, max_number_changes, segment_date, change_direction,
                   *_pair(filter_by_year, min_year, max_year, "FILTER_BY_YEAR"),
                   *_pair(filter_by_duration, min_duration, max_duration, "FILTER_BY_DURATION"),
                   *_pair(filter_by_magnitude, min_magnitude, max_magnitude, "FILTER_BY_MAGNITUDE"),
                   *_pair(filter_by_start_value, min_start_value, max_start_value, "FILTER_BY_START_VALUE"),
                   *_pair(filter_by_end_value, min_end_value, max_end_value, "FILTER_BY_END_VALUE"), tool)


def DetectChangeUsingChangeAnalysis(raster, change_type="TIME_OF_LATEST_CHANGE", max_number_of_changes=1,
                                    segment_date="BEGINNING_OF_SEGMENT", change_direction="ALL", filter_by_year=False,
                                    min_year=1970, max_year=2020, filter_by_duration=False, min_duration=0,
                                    max_duration=50, filter_by_magnitude=False, min_magnitude=-50, max_magnitude=50):
    """The function form of DetectChangeUsingChangeAnalysisRaster, with the function page's defaults.

    INFERRED: the page names its eleventh parameter ``min_duration`` twice; the second, described as the maximum, is
    ``max_duration`` here."""
    tool = "DetectChangeUsingChangeAnalysis"
    on = lambda f: bool(f) and str(f).upper() not in ("FALSE", "NO")  # noqa: E731
    return _detect(raster, change_type, max_number_of_changes, segment_date, change_direction,
                   on(filter_by_year), (float(min_year), float(max_year)),
                   on(filter_by_duration), (float(min_duration), float(max_duration)),
                   on(filter_by_magnitude), (float(min_magnitude), float(max_magnitude)),
                   False, None, False, None, tool)


_ = (Cube,)
