"""IDW3D and NearestNeighbor3D: interpolation on 3D points to a netCDF grid of voxels — ADR-238.

Both tools write a netCDF file of a 3D grid. grapy writes it with scipy's netCDF reader-writer (``scipy.io``, the
classic netCDF-3 format of Unidata's specification, 64-bit offsets), which GDAL, xarray and ArcGIS read. The pages do
not name the file's dimensions or variables; grapy's are INFERRED (ADR-238 §5.2): dimensions ``elevation``, ``y``
and ``x`` with coordinate variables of the same names holding the voxel centres, and one variable per interpolated
field named after it. The file is written by ``_mdim.netcdf3``, grapy's one netCDF writer, which the multidimensional
raster (ADR-236) writes through too; the two layouts differ (a voxel grid is ascending in y, and keeps integer
categories with −1 as fill), the writer does not. A voxel with no prediction holds the variable's ``_FillValue`` (NaN for IDW3D, −1 for a
category). The coordinate system is the global attribute ``crs_wkt``.

The voxel layer each page lists among its derived outputs is made only when the tool runs in a local scene of
ArcGIS Pro, as both pages say; a user tool on this server runs in none, so it is never made, and its output is "".

The grid, as both pages document: the first voxel at the minimum x, y and z of the extent (the study area's, else the
points'), then every spacing, one more where the spacing does not divide the extent (0–10 by 3 gives 0, 3, 6, 9, 12);
40 voxels along an axis by default.
"""
from __future__ import annotations

import math

import numpy as np
import shapely

from . import _multipatch as M
from ._result import Result
from ._runtime import AddMessage, ExecuteError, NotBuiltError


def _refuse(tool, parameter, value, why):
    raise NotBuiltError(f"arcpy.ga.{tool}: {parameter} '{value}' is not built in grapy — {why}.")


def _points(in_features, fields, tool):
    from .analysis import _schema
    columns, srid, rows = M.read(in_features)
    if _schema(in_features)[1] not in ("Point",):
        raise ExecuteError(f"ERROR: arcpy.ga.{tool}: the input must be points.")
    xyz, values = [], []
    for _, attrs, shape in rows:
        if shape is None or shape.is_empty or not shapely.has_z(shape):
            continue
        p = shapely.get_coordinates(shape, include_z=True)[0]
        xyz.append(p)
        values.append([attrs[f] for f in fields])
    if srid:
        from pyproj import CRS
        if CRS.from_epsg(srid).is_geographic:
            raise ExecuteError(f"ERROR: arcpy.ga.{tool}: the points must be in a projected coordinate system, as "
                               "documented.")
    return np.array(xyz, float).reshape(-1, 3), values, srid


def _axis(lo, hi, spacing, given):
    if given is not None:
        step = float(spacing)
        if step <= 0:
            raise ExecuteError("ERROR: a spacing must be positive.")
        n = int(math.ceil((hi - lo) / step - 1e-9)) + 1 if hi > lo else 1
    else:
        n = 40
        step = (hi - lo) / 39 if hi > lo else 1.0
    return lo + step * np.arange(n)


def _grid(xyz, x_spacing, y_spacing, elevation_spacing, in_study_area, srid):
    from .analysis import _distance
    area = None
    lo, hi = xyz.min(axis=0), xyz.max(axis=0)
    if M.given(in_study_area):
        shapes = [s for _, _, s in M.read(in_study_area)[2] if s is not None]
        area = shapely.union_all([shapely.force_2d(s) for s in shapes])
        x0, y0, x1, y1 = area.bounds
        lo, hi = np.array([x0, y0, lo[2]]), np.array([x1, y1, hi[2]])
    axes = []
    for k, spacing in enumerate((x_spacing, y_spacing, elevation_spacing)):
        given = M.given(spacing)
        axes.append(_axis(lo[k], hi[k], _distance(spacing, srid) if given else None, given or None))
    return axes, area


def _mask(axes, area, min_elev_raster, max_elev_raster, tool):
    """True where a voxel takes a prediction: inside the study area in x and y, above the minimum and below the
    maximum elevation rasters."""
    from ._mptools import _bilinear
    from ._raster import as_raster
    xs, ys, zs = axes
    gx, gy = np.meshgrid(xs, ys)
    keep2 = np.ones(gx.shape, bool)
    if area is not None:
        keep2 &= shapely.covers(area, shapely.points(gx, gy))
    keep = np.broadcast_to(keep2, (len(zs), *gx.shape)).copy()
    for raster, above in ((min_elev_raster, True), (max_elev_raster, False)):
        if not M.given(raster):
            continue
        surface = _bilinear(as_raster(raster, f"arcpy.ga.{tool}"), gx.ravel(), gy.ravel()).reshape(gx.shape)
        for k, z in enumerate(zs):
            keep[k] &= (z > surface) if above else (z < surface)
            keep[k] &= np.isfinite(surface)
    return keep


def _write(path, axes, variables, srid, fills):
    """The voxels through grapy's one netCDF writer, ``_mdim.netcdf3`` (ADR-236): the layout of this module's
    docstring, with CF's ``axis`` on the three coordinates and ``positive = "up"`` on ``elevation`` so that a CF reader
    — and ``Raster(path, True)``, which calls it ``StdZ`` — knows it is the vertical one."""
    from . import _mdim
    xs, ys, zs = axes
    dimensions = [("elevation", zs, {"axis": "Z", "positive": "up"}), ("y", ys, {"axis": "Y"}), ("x", xs, {"axis": "X"})]
    out = []
    for name, (data, extra) in variables.items():
        kind = "d" if data.dtype.kind == "f" else "i"
        out.append((name, ("elevation", "y", "x"), data.astype(np.float64 if kind == "d" else np.int32),
                    {"_FillValue": fills[kind], **extra}))
    attributes = {}
    if srid:
        from pyproj import CRS
        # netCDF-3 text is ASCII: WKT's degree signs and the like are replaced by '?'.
        attributes["crs_wkt"] = CRS.from_epsg(srid).to_wkt().encode("ascii", "replace").decode("ascii")
    return _mdim.netcdf3(path, dimensions, out, attributes)


def _neighbourhood(value, tool):
    from . import ga
    if not M.given(value):
        _refuse(tool, "search_neighborhood", "(default)", "its radius and counts are derived from the data by a rule "
                "the documentation does not give; pass a SearchNeighborhoodStandard3D")
    if not isinstance(value, ga.SearchNeighborhoodStandard3D):
        _refuse(tool, "search_neighborhood", type(value).__name__, "a 3D tool takes a SearchNeighborhoodStandard3D")
    sector = ga._keyword(value.sectorType) if ga._given(value.sectorType) else "ONE_SECTOR"
    if sector != "ONE_SECTOR":
        _refuse(tool, "sectorType", sector, "which sector a point on a sector's edge belongs to is not documented "
                "(ADR-233)")
    for name in ("radius", "nbrMax", "nbrMin"):
        if not ga._given(getattr(value, name)):
            _refuse(tool, name, "(empty)", "its default is derived from the data by a rule the documentation does not "
                    "give")
    return float(value.radius), int(value.nbrMax), int(value.nbrMin)


def _idw_at(tree, xyz, values, queries, factor, power, radius, nbr_max, nbr_min, exclude=None):
    """IDW predictions at query points: in the space whose z is multiplied by ``factor``, the nearest ``nbr_max``
    points within ``radius`` and, when fewer than ``nbr_min`` are, the nearest outside it up to ``nbr_min`` (as GA
    Neighborhood Selection documents); weights the inverse distance to the power, normalised."""
    k = min(len(xyz), max(nbr_max, nbr_min) + (1 if exclude is not None else 0))
    q = queries * [1.0, 1.0, factor]
    d, i = tree.query(q, k=k)
    d, i = np.atleast_2d(d).reshape(len(q), -1), np.atleast_2d(i).reshape(len(q), -1)
    out = np.full(len(q), np.nan)
    for row in range(len(q)):
        dist, idx = d[row], i[row]
        if exclude is not None:
            keep = idx != exclude[row]
            dist, idx = dist[keep], idx[keep]
        inside = dist <= radius
        chosen = np.nonzero(inside)[0][:nbr_max]
        if len(chosen) < nbr_min:
            chosen = np.concatenate([chosen, np.nonzero(~inside)[0][:nbr_min - len(chosen)]])
        if not len(chosen):
            continue
        dd, vv = dist[chosen], values[idx[chosen]]
        if np.any(dd == 0):
            out[row] = float(vv[dd == 0][0])
            continue
        w = dd ** (-power)
        out[row] = float(np.sum(w * vv) / np.sum(w))
    return out


def _cross_validate(xyz, values, factor, power, radius, nbr_max, nbr_min):
    from scipy.spatial import cKDTree
    tree = cKDTree(xyz * [1.0, 1.0, factor])
    return _idw_at(tree, xyz, values, xyz, factor, power, radius, nbr_max, nbr_min, exclude=np.arange(len(xyz)))


def _estimate_factor(xyz, values, power, radius, nbr_max, nbr_min):
    """The inflation factor of least root-mean-square cross-validation error on 1–1,000, as the page says it is
    estimated. INFERRED (the search is not given): 61 factors evenly spaced in log10, then a bounded Brent search on
    log10 between the best one's neighbours."""
    from scipy.optimize import minimize_scalar

    def rms(log_factor):
        predicted = _cross_validate(xyz, values, 10 ** log_factor, power, radius, nbr_max, nbr_min)
        ok = np.isfinite(predicted)
        return float(np.sqrt(np.mean((predicted[ok] - values[ok]) ** 2))) if ok.any() else np.inf

    grid = np.linspace(0, 3, 61)
    scores = [rms(v) for v in grid]
    best = int(np.argmin(scores))
    lo, hi = grid[max(best - 1, 0)], grid[min(best + 1, len(grid) - 1)]
    found = minimize_scalar(rms, bounds=(lo, hi), method="bounded", options={"xatol": 1e-4})
    choice = found.x if found.fun <= scores[best] else grid[best]
    return float(10 ** choice)


def IDW3D(in_features, value_field, out_netcdf_file, power=2, elev_inflation_factor=None, out_cv_features=None,
          x_spacing=None, y_spacing=None, elevation_spacing=None, in_study_area=None, min_elev_raster=None,
          max_elev_raster=None, search_neighborhood=None):
    """Inverse distance weighting in 3D, as documented: each voxel's prediction is the weighted average of its
    neighbours' values, the weights the inverse 3D distance to the power, normalised to sum to one; an exact
    interpolator. Elevations are multiplied by the inflation factor before neighbours and distances are found. With
    no factor, it is estimated by minimising the root-mean-square leave-one-out cross-validation error, and reported.
    ``out_cv_features`` writes the cross-validation; the Result's derived outputs are the count, mean error and
    root-mean-square error.

    Refused: an omitted ``search_neighborhood`` or radius, nbrMax or nbrMin (derived from the data by an unstated
    rule), sectors other than ONE_SECTOR (ADR-233). INFERRED: the netCDF layout (module docstring); the factor's
    search (``_estimate_factor``); the cross-validation fields ``Measured``, ``Predicted`` and ``Error``
    (predicted − measured), whose names the page does not give."""
    from scipy.spatial import cKDTree
    from ._gpkg import Column
    tool = "IDW3D"
    p = float(power if M.given(power) else 2)
    if not 1 <= p <= 100:
        raise ExecuteError(f"ERROR: arcpy.ga.{tool}: the power must be 1 to 100.")
    radius, nbr_max, nbr_min = _neighbourhood(search_neighborhood, tool)
    xyz, values, srid = _points(in_features, [value_field], tool)
    if len(xyz) < 2:
        raise ExecuteError(f"ERROR: arcpy.ga.{tool}: too few points.")
    values = np.array([float(v[0]) for v in values])
    if M.given(elev_inflation_factor):
        factor = float(elev_inflation_factor)
        if not 1 <= factor <= 1000:
            raise ExecuteError(f"ERROR: arcpy.ga.{tool}: the elevation inflation factor must be 1 to 1,000.")
    else:
        factor = _estimate_factor(xyz, values, p, radius, nbr_max, nbr_min)
        AddMessage(f"Estimated elevation inflation factor: {factor:.6g}")
    axes, area = _grid(xyz, x_spacing, y_spacing, elevation_spacing, in_study_area, srid)
    keep = _mask(axes, area, min_elev_raster, max_elev_raster, tool)
    gz, gy, gx = np.meshgrid(axes[2], axes[1], axes[0], indexing="ij")
    queries = np.column_stack([gx.ravel(), gy.ravel(), gz.ravel()])[keep.ravel()]
    tree = cKDTree(xyz * [1.0, 1.0, factor])
    data = np.full(keep.shape, np.nan)
    data[keep] = _idw_at(tree, xyz, values, queries, factor, p, radius, nbr_max, nbr_min)
    _write(out_netcdf_file, axes, {value_field: (data, {})}, srid, {"d": np.nan, "i": -1})
    predicted = _cross_validate(xyz, values, factor, p, radius, nbr_max, nbr_min)
    ok = np.isfinite(predicted)
    error = predicted[ok] - values[ok]
    count = int(ok.sum())
    mean_error = float(error.mean()) if count else float("nan")
    rms = float(np.sqrt(np.mean(error ** 2))) if count else float("nan")
    AddMessage(f"Count {count}, mean error {mean_error:.6g}, root mean square {rms:.6g}")
    cv = ""
    if M.given(out_cv_features):
        rows = [([float(values[i]), float(predicted[i]), float(predicted[i] - values[i])],
                 shapely.Point(*xyz[i])) for i in np.nonzero(ok)[0]]
        cv = M.write(out_cv_features, srid, [Column("Measured", "Double"), Column("Predicted", "Double"),
                                             Column("Error", "Double")], rows, kind="Point", has_z=True)
    return Result(out_netcdf_file, cv, count, mean_error, rms, "")


def NearestNeighbor3D(in_features, category_field, out_netcdf_file, x_spacing=None, y_spacing=None,
                      elevation_spacing=None, elev_inflation_factor=1, in_study_area=None, min_elev_raster=None,
                      max_elev_raster=None):
    """Each voxel takes the category of its nearest point, searched with elevations multiplied by the inflation
    factor (1 by default), as documented; one netCDF variable per category field, named after it, its categories coded
    0, 1, 2 … in ascending order of their values; where points are equally near, the first category in that order,
    as the page says of coincident points. The counts and percentages of each category are reported.

    INFERRED: the netCDF layout (module docstring); each variable's ``categories`` attribute lists ``code=value``;
    a null category is left out of the points."""
    from scipy.spatial import cKDTree
    tool = "NearestNeighbor3D"
    fields = [f.strip() for f in (category_field if isinstance(category_field, (list, tuple))
                                   else str(category_field).split(";")) if str(f).strip()]
    factor = float(elev_inflation_factor if M.given(elev_inflation_factor) else 1)
    if not 1 <= factor <= 1000:
        raise ExecuteError(f"ERROR: arcpy.ga.{tool}: the elevation inflation factor must be 1 to 1,000.")
    xyz_all, values_all, srid = _points(in_features, fields, tool)
    if len(xyz_all) < 3:
        raise ExecuteError(f"ERROR: arcpy.ga.{tool}: at least three points are needed, as documented.")
    axes, area = _grid(xyz_all, x_spacing, y_spacing, elevation_spacing, in_study_area, srid)
    keep = _mask(axes, area, min_elev_raster, max_elev_raster, tool)
    gz, gy, gx = np.meshgrid(axes[2], axes[1], axes[0], indexing="ij")
    queries = np.column_stack([gx.ravel(), gy.ravel(), gz.ravel()])[keep.ravel()] * [1.0, 1.0, factor]
    variables = {}
    for k, field in enumerate(fields):
        rows = [(p, v[k]) for p, v in zip(xyz_all, values_all) if v[k] is not None]
        xyz = np.array([r[0] for r in rows], float)
        labels = sorted({r[1] for r in rows})
        code = {label: i for i, label in enumerate(labels)}
        codes = np.array([code[r[1]] for r in rows])
        tree = cKDTree(xyz * [1.0, 1.0, factor])
        n = min(len(xyz), 8)
        d, i = tree.query(queries, k=n)
        d, i = d.reshape(len(queries), -1), i.reshape(len(queries), -1)
        near = d <= d[:, :1] * (1 + 1e-12) + 1e-12
        candidates = np.where(near, codes[i], np.iinfo(np.int64).max)
        chosen = candidates.min(axis=1)
        data = np.full(keep.shape, -1, np.int32)
        data[keep] = chosen.astype(np.int32)
        variables[field] = (data, {"categories": "; ".join(f"{c}={label}" for label, c in code.items())})
        total = int(keep.sum())
        for label, c in code.items():
            n_c = int(np.sum(chosen == c))
            AddMessage(f"{field}: {label}: {n_c} voxels ({100.0 * n_c / total if total else 0:.2f}%)")
    _write(out_netcdf_file, axes, variables, srid, {"d": np.nan, "i": -1})
    return Result(out_netcdf_file, "")
