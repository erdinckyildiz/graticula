"""arcpy.topographic's relief tools: MEF, the magnetic tools, contours, spot heights and elevation bands (ADR-251).

Rasters are read through ``grapy._raster`` (one band). Contours are made by ``sa._contour_lines``, the method *How
contouring works* describes, so a contour here is the same line arcpy.sa.Contour draws.
"""
from __future__ import annotations

import datetime
import math

import numpy as np
import shapely
from shapely.geometry import LineString, MultiLineString, Point, Polygon, box
from shapely.ops import polygonize, unary_union

from . import _topo_prod as tp
from . import _topo_prod_wmm as wmm
from ._result import Result
from ._runtime import AddMessage, AddWarning, ExecuteError, NotBuiltError

FOOT = 0.3048  # the international foot; aeronautical charts are in it


# ---- rasters ---------------------------------------------------------------------------------------------------------

def raster_of(tool, value):
    """One raster from a raster parameter; several (a multivalue) are mosaicked, the first holding a cell winning, as
    the pages ask several rasters to share cell size and type."""
    from ._raster import Raster, as_raster
    found = [v for v in (tp.listed(value) if not hasattr(value, "_grid") else [value])]
    if not found:
        raise ExecuteError(f"ERROR 000735: {tool}: a raster is required.")
    rasters = [as_raster(v, tool) for v in found]
    if len(rasters) == 1:
        return rasters[0]
    grid = rasters[0]._grid
    for r in rasters[1:]:
        if abs(r._grid.width - grid.width) > 1e-9 or abs(r._grid.height - grid.height) > 1e-9:
            raise ExecuteError(f"ERROR 000800: {tool}: the rasters must have the same cell size.")
    left = min(r._grid.left for r in rasters)
    top = max(r._grid.top for r in rasters)
    right = max(r._grid.right for r in rasters)
    bottom = min(r._grid.bottom for r in rasters)
    columns = int(round((right - left) / grid.width))
    rows = int(round((top - bottom) / grid.height))
    from ._raster import _Grid
    out_grid = _Grid(left, top, grid.width, grid.height, rows, columns, grid.srid)
    array = np.zeros((rows, columns), dtype=np.float64)
    mask = np.ones((rows, columns), bool)
    for r in rasters:
        r0 = int(round((top - r._grid.top) / grid.height))
        c0 = int(round((r._grid.left - left) / grid.width))
        window = (slice(r0, r0 + r._grid.rows), slice(c0, c0 + r._grid.columns))
        free = mask[window] & ~r._mask
        array[window][free] = r._array[free]
        mask[window] &= r._mask
    return Raster._of(array, mask, out_grid)


def cells_in(raster, shape):
    """The values of the cells whose centres lie inside an area (NoData left out)."""
    if shape is None or shape.is_empty:
        return np.array([])
    xs, ys = raster._grid.centres()
    x0, y0, x1, y1 = shape.bounds
    columns = np.where((xs >= x0) & (xs <= x1))[0]
    rows = np.where((ys >= y0) & (ys <= y1))[0]
    if not len(columns) or not len(rows):
        return np.array([])
    gx, gy = np.meshgrid(xs[columns], ys[rows])
    inside = shapely.contains_xy(shape, gx, gy)
    block = raster._array[np.ix_(rows, columns)].astype(np.float64)
    masked = raster._mask[np.ix_(rows, columns)]
    return block[inside & ~masked]


def extreme_cells(raster, shape, highest):
    """(value, [(x, y) of every cell centre holding it]) of the highest or lowest cell inside an area."""
    xs, ys = raster._grid.centres()
    gx, gy = np.meshgrid(xs, ys)
    inside = shapely.contains_xy(shape, gx, gy) & ~raster._mask
    if not inside.any():
        return None, []
    values = raster._array.astype(np.float64)
    target = values[inside].max() if highest else values[inside].min()
    where = inside & (values == target)
    return float(target), list(zip(gx[where].tolist(), gy[where].tolist()))


def sample(raster, x, y):
    cell = raster._grid.cell_of(x, y)
    if cell is None or raster._mask[cell]:
        return None
    return float(raster._array[cell])


# ---- CalculateMEF ----------------------------------------------------------------------------------------------------

_SPECIFICATIONS = ("JOG_MIL_J_89100", "ONC_MIL_O_89102", "TPC_MIL_T_89101", "STANAG_3591_ED6")


def _feet(tool, value, default):
    """A linear unit in international feet."""
    if not tp.given(value):
        value = default
    if isinstance(value, (int, float)):
        return float(value)
    parts = str(value).split()
    if len(parts) == 1:
        return float(parts[0])
    return float(parts[0]) * _unit_feet(tool, parts[1])


def _unit_feet(tool, unit):
    word = str(unit).upper().replace(" ", "").replace("_", "")
    if word in ("FEET", "FOOT", "FEETINT", "INTERNATIONALFEET"):
        return 1.0
    from . import _units
    metres = _units.linear(word)
    if metres is None:
        raise ExecuteError(f"ERROR 000800: {tool}: '{unit}' is not a linear unit.")
    return metres / FOOT


def _up100(value):
    return math.ceil(round(value, 6) / 100.0) * 100


def mef_figure(specification, terrain, obstruction, allowance, vo_accuracy, terrain_accuracy):
    """The maximum elevation figure in feet, by the specification's published rule (values in feet)."""
    terrain_branch = None if terrain is None else terrain + allowance + terrain_accuracy
    if specification == "STANAG_3591_ED6":
        human = None if obstruction is None else obstruction + vo_accuracy + terrain_accuracy
    else:
        human = None if obstruction is None else obstruction + vo_accuracy
    if specification == "JOG_MIL_J_89100":
        if human is not None and (terrain is None or obstruction > terrain + allowance):
            return _up100(human)
        return None if terrain_branch is None else _up100(terrain_branch)
    found = [v for v in (human, terrain_branch) if v is not None]
    return _up100(max(found)) if found else None


def calculate_mef(target_mef_features, in_terrain, obstruction_features, mef_field, max_vo_field, max_terrain_field,
                  specification, terrain_elev_field, vo_allowance, vo_accuracy, terrain_accuracy):
    tool = "CalculateMEF"
    spec = tp.word(specification, "")
    if spec not in _SPECIFICATIONS:
        raise ExecuteError(f"ERROR 000800: {tool}: specification is one of {', '.join(_SPECIFICATIONS)}.")
    path = tp.dataset(target_mef_features)
    fields = [tp.require_field(tool, path, f) for f in (mef_field, max_vo_field, max_terrain_field)]
    allowance = _feet(tool, vo_allowance, "150 Feet")
    vo_acc = _feet(tool, vo_accuracy, "20 Meters")
    t_acc = _feet(tool, terrain_accuracy, "20 Meters")
    raster, terrain_points = None, None
    try:
        kind = tp.schema(tp.dataset(in_terrain))[1]
    except Exception:  # noqa: BLE001 — not a feature class: a raster
        kind = None
    if kind in ("Point", "Multipoint"):
        if not tp.given(terrain_elev_field):
            raise ExecuteError(f"ERROR 000735: {tool}: terrain_elev_field is required with point terrain.")
        field = tp.require_field(tool, tp.dataset(in_terrain), terrain_elev_field)
        terrain_points = [(r.shape, r[field]) for r in tp.read(in_terrain, fields=[field]) if r[field] is not None]
    else:
        raster = raster_of(tool, in_terrain)
    obstructions = []
    for row in tp.table_rows(obstruction_features):
        if len(row) < 2:
            raise ExecuteError(f"ERROR 000800: {tool}: each obstruction row names features and an elevation field.")
        layer, field = row[0], row[1]
        factor = _unit_feet(tool, row[2]) if len(row) > 2 and tp.given(row[2]) else 1.0
        found = tp.require_field(tool, tp.dataset(layer), field)
        obstructions += [(r.shape, r[found] * factor) for r in tp.read(layer, fields=[found]) if r[found] is not None]
    changes = {}
    for row in tp.read(target_mef_features, fields=[]):
        cell = row.shape
        if cell is None:
            continue
        if raster is not None:
            values = cells_in(raster, cell)
            terrain = float(values.max()) / FOOT if values.size else None
        else:
            inside = [v for s, v in terrain_points if s is not None and cell.covers(s)]
            terrain = max(inside) / FOOT if inside else None
        tops = [v for s, v in obstructions if s is not None and cell.intersects(s)]
        top = max(tops) if tops else None
        figure = mef_figure(spec, terrain, top, allowance, vo_acc, t_acc)
        if figure is None:
            AddWarning(f"{tool}: feature {row.oid} has neither terrain nor obstructions inside it.")
        changes[row.oid] = {fields[0]: figure, fields[1]: top, fields[2]: terrain}
    tp.update(path, changes)
    return Result(tp.text(target_mef_features))


# ---- the World Magnetic Model tools ----------------------------------------------------------------------------------

_COMPONENTS = {"DECLINATION": "D", "ANNUAL_DRIFT": "Ddot", "INCLINATION": "I", "HORIZONTAL": "H",
               "EAST_COMPONENT": "Y", "NORTH_COMPONENT": "X", "VERTICAL_INTENSITY": "Z", "TOTAL_INTENSITY": "F",
               "GRID_VARIATION": None}


def _date(tool, value):
    if not tp.given(value):
        return datetime.date.today()
    if isinstance(value, (datetime.date, datetime.datetime)):
        return value
    text = str(value).strip()
    for pattern in ("%m/%d/%Y", "%m/%d/%Y %H:%M:%S", "%Y-%m-%d", "%Y-%m-%d %H:%M:%S"):
        try:
            return datetime.datetime.strptime(text, pattern)
        except ValueError:
            pass
    raise ExecuteError(f"ERROR 000800: {tool}: '{value}' is not a date (MM/DD/YYYY).")


def _model(tool, value, year):
    if tp.given(value):
        path = tp.text(value)
        if not path.upper().endswith(".COF"):
            raise NotBuiltError(f"arcpy.topographic.{tool}: wmm_file '{path}' is not built in grapy — Esri's WMM.xml "
                                "layout is not published; give NOAA's World Magnetic Model .COF coefficient file instead.")
        try:
            return wmm.model_for(year, path)
        except ValueError as said:
            raise ExecuteError(f"ERROR 000800: {tool}: {said}") from None
    try:
        return wmm.model_for(year)
    except ValueError as said:
        raise ExecuteError(f"ERROR 000800: {tool}: {said}") from None


def _altitude_km(tool, value, srid):
    if not tp.given(value):
        return 0.0
    if isinstance(value, (int, float)):
        metres = float(value) * (tp.per_unit(srid) or 1.0)
    else:
        parts = str(value).split()
        from . import _units
        unit = _units.linear(parts[1]) if len(parts) > 1 else (tp.per_unit(srid) or 1.0)
        if unit is None:
            raise ExecuteError(f"ERROR 000800: {tool}: '{value}' is not a linear unit.")
        metres = float(parts[0]) * unit
    return metres / 1000.0


def _is_lambert(srid):
    from pyproj import CRS
    try:
        method = CRS.from_epsg(srid).to_json_dict().get("conversion", {}).get("method", {}).get("name", "")
    except Exception:  # noqa: BLE001
        return False
    return "Lambert Conic Conformal" in method


def convergence(srid, x, y):
    """The grid convergence (degrees, positive where grid north lies east of true north) at a point."""
    from pyproj import CRS, Proj, Transformer
    lon, lat = Transformer.from_crs(srid, 4326, always_xy=True).transform(x, y)
    return Proj(CRS.from_epsg(srid)).get_factors(lon, lat).meridian_convergence


def calculate_magnetic_components(in_features, altitude, date, magnetic_component, wmm_file):
    from pyproj import Transformer
    tool = "CalculateMagneticComponents"
    path = tp.dataset(in_features)
    _, kind, srid, _ = tp.schema(path)
    if kind != "Point":
        raise ExecuteError(f"ERROR 000800: {tool}: the input must be points.")
    if srid <= 0:
        raise ExecuteError(f"ERROR 000622: {tool}: the input has no coordinate system.")
    moment = _date(tool, date)
    year = wmm.decimal_year(moment)
    model = _model(tool, wmm_file, year)
    height = _altitude_km(tool, altitude, srid)
    wanted = []
    for row in tp.table_rows(magnetic_component):
        component = str(row[0]).upper()
        if component not in _COMPONENTS:
            raise ExecuteError(f"ERROR 000800: {tool}: '{row[0]}' is not a magnetic component.")
        wanted.append((component, tp.require_field(tool, path, row[1])))
    if any(c == "GRID_VARIATION" for c, _ in wanted) and not _is_lambert(srid):
        raise ExecuteError(f"ERROR 000800: {tool}: GRID_VARIATION needs a Lambert Conformal Conic coordinate system.")
    to_lonlat = Transformer.from_crs(srid, 4326, always_xy=True)
    changes = {}
    for row in tp.read(in_features, fields=[]):
        if row.shape is None:
            continue
        lon, lat = to_lonlat.transform(row.shape.x, row.shape.y)
        found = wmm.field(model, lat, lon, height, year)
        change = {}
        for component, field in wanted:
            if component == "GRID_VARIATION":
                change[field] = found["D"] - convergence(srid, row.shape.x, row.shape.y)
            else:
                change[field] = found[_COMPONENTS[component]]
        changes[row.oid] = change
    tp.update(path, changes)
    AddMessage(f"{tool}: {len(changes)} points computed with {model.name}.")
    return Result(tp.text(in_features))


def _subtype_code(tool, path, value):
    """A subtype given by code or by name, as (subtype field, code); None when none is given."""
    if not tp.given(value):
        return None
    from . import _gpkg, _subtypes
    connection, table = _gpkg.table_of(path)
    try:
        field = _subtypes.field(connection, table)
        codes = _subtypes.codes(connection, table)
    finally:
        connection.close()
    if field is None:
        raise ExecuteError(f"ERROR 000800: {tool}: '{path}' has no subtypes.")
    for code, name in codes.items():
        if str(value) == name or str(value) == str(code):
            return field[0], code
    raise ExecuteError(f"ERROR 000800: {tool}: '{value}' is not a subtype of '{path}'.")


def generate_isogonic_lines(extent_features, altitude, date, interval, base, target_isogonic_features, magnetic_field,
                            subtype, annual_drift, annual_drift_field):
    from pyproj import Transformer
    from ._raster import Raster, _Grid
    from .sa import _contour_lines
    tool = "GenerateIsogonicLines"
    area = unary_union([r.shape for r in tp.read(extent_features, fields=[]) if r.shape is not None])
    srid = tp.srid_of(extent_features)
    if area.is_empty or srid <= 0:
        raise ExecuteError(f"ERROR 000800: {tool}: the extent features must hold a shape with a coordinate system.")
    target = tp.dataset(target_isogonic_features)
    if tp.srid_of(target) != srid:
        raise ExecuteError(f"ERROR 000622: {tool}: the extent and target features must share a coordinate system.")
    field = tp.require_field(tool, target, magnetic_field)
    drift = tp.flag(tool, annual_drift, "ANNUAL_DRIFT", "NO_ANNUAL_DRIFT", "NO_ANNUAL_DRIFT")
    if drift and not tp.given(annual_drift_field):
        raise ExecuteError(f"ERROR 000735: {tool}: annual_drift_field is required with ANNUAL_DRIFT.")
    drift_field = tp.require_field(tool, target, annual_drift_field) if drift else None
    step = float(interval) if tp.given(interval) else 1.0
    origin = float(base) if tp.given(base) else 0.0
    if step <= 0:
        raise ExecuteError(f"ERROR 000800: {tool}: the interval must be positive.")
    moment = _date(tool, date)
    year = wmm.decimal_year(moment)
    model = _model(tool, None, year)
    height = _altitude_km(tool, altitude, srid)
    subtype_pair = _subtype_code(tool, target, subtype)
    x0, y0, x1, y1 = area.bounds
    n = 64
    dx, dy = (x1 - x0) / (n - 1) or 1.0, (y1 - y0) / (n - 1) or 1.0
    grid = _Grid(x0 - dx / 2, y1 + dy / 2, dx, dy, n, n, srid)
    xs, ys = grid.centres()
    to_lonlat = Transformer.from_crs(srid, 4326, always_xy=True)
    gx, gy = np.meshgrid(xs, ys)
    lon, lat = to_lonlat.transform(gx, gy)
    values = np.array([[wmm.field(model, lat[r, c], lon[r, c], height, year)["D"] for c in range(n)]
                       for r in range(n)])
    raster = Raster._of(values, np.zeros(values.shape, bool), grid)
    low = math.ceil((values.min() - origin) / step)
    high = math.floor((values.max() - origin) / step)
    levels = [origin + k * step for k in range(low, high + 1)]
    rows = []
    for level, line in _contour_lines(raster, levels):
        clipped = line.intersection(area)
        for part in tp.parts(clipped):
            if part.geom_type != "LineString" or part.length == 0:
                continue
            values_row = {field: level, "SHAPE@": MultiLineString([part])}
            if subtype_pair:
                values_row[subtype_pair[0]] = subtype_pair[1]
            if drift:
                middle = part.interpolate(0.5, normalized=True)
                plon, plat = to_lonlat.transform(middle.x, middle.y)
                rate = wmm.field(model, plat, plon, height, year)["Ddot"]
                kind = {c.name: c.type for c in tp.schema(target)[0]}[drift_field]
                values_row[drift_field] = f"{rate:.2f}" if kind == "String" else rate
            rows.append(values_row)
    tp.insert(target, rows)
    AddMessage(f"{tool}: {len(rows)} isogonic lines appended, {model.name}.")
    return Result(tp.text(target_isogonic_features))


# ---- contours --------------------------------------------------------------------------------------------------------

def closed(line):
    return line is not None and len(line.coords) >= 4 and \
        math.dist(line.coords[0][:2], line.coords[-1][:2]) < 1e-9


def is_depression(raster, ring_polygon, level, inner=None):
    """Whether a closed contour encloses lower ground: the mean of the cells inside it, inner closed contours' areas
    left out, is below its value."""
    area = ring_polygon if inner is None or inner.is_empty else ring_polygon.difference(inner)
    values = cells_in(raster, area)
    if not values.size:
        values = cells_in(raster, ring_polygon)
    return bool(values.size) and float(values.mean()) < level


def classify(level, base, interval, index_interval, supplemental, codes, depression):
    """The contour code of a level: auxiliary when it falls between main contours, index when it is a multiple of the
    index interval, else intermediate; each with its depression form."""
    offset = level - base
    on_main = abs(offset / interval - round(offset / interval)) < 1e-9
    if not on_main and supplemental != "NONE":
        if depression:
            return codes["depression_auxiliary"]
        half = abs(offset / (interval / 2) - round(offset / (interval / 2))) < 1e-9
        return codes["half_auxiliary"] if half else codes["quarter_auxiliary"]
    index = index_interval and abs(level / index_interval - round(level / index_interval)) < 1e-9
    if depression:
        return codes["depression_index"] if index else codes["depression_intermediate"]
    return codes["index"] if index else codes["intermediate"]


_SCALE_INTERVAL = {"1:5,000": 2, "1:10,000": 5, "1:12,500": 5, "1:25,000": 10, "1:50,000": 20, "1:100,000": 40,
                   "1:250,000": 100, "1:500,000": 200, "1:1,000,000": 300}


def _scale(tool, value, default="1:50,000"):
    chosen = str(value).replace(" ", "") if tp.given(value) else default
    for name in _SCALE_INTERVAL:
        if name.replace(",", "") == chosen.replace(",", ""):
            return name
    raise ExecuteError(f"ERROR 000800: {tool}: '{value}' is not a map scale.")


def _one_area(tool, value):
    """The one area of interest a layer shows: its single selected polygon, or the one feature of a class."""
    rows = [r for r in tp.read(value) if r.shape is not None]
    if len(rows) != 1:
        raise ExecuteError(f"ERROR 000800: {tool}: the area of interest must hold exactly one selected feature.")
    return rows[0]


def generate_topographic_contours(in_rasters, area_of_interest, contour_features, elevation_field, contour_subtype,
                                  scale, resample_raster, contour_interval, base_contour, z_factor, zero_contour,
                                  code_field, index_interval, index_code, intermediate_code, depression_code,
                                  depression_intermediate_code, raster_smooth_tolerance, minimum_length,
                                  contour_smooth_tolerance, supplemental_contours, half_auxiliary_code,
                                  quarter_auxiliary_code, depression_auxiliary_code):
    from .sa import _contour_lines
    tool = "GenerateTopographicContours"
    if tp.flag(tool, resample_raster, "RESAMPLE_RASTER", "NO_RESAMPLE_RASTER", "NO_RESAMPLE_RASTER"):
        raise NotBuiltError("arcpy.topographic.GenerateTopographicContours: resample_raster 'RESAMPLE_RASTER' is not "
                            "built in grapy — the page gives neither the cell size each scale resamples to nor the "
                            "resampling method.")
    raster = raster_of(tool, in_rasters)
    target = tp.dataset(contour_features)
    srid = tp.srid_of(target)
    if raster._grid.srid and srid and raster._grid.srid != srid:
        raise ExecuteError(f"ERROR 000622: {tool}: the rasters and the contours must share a coordinate system.")
    aoi = _one_area(tool, area_of_interest).shape
    chosen_scale = _scale(tool, scale)
    interval = float(contour_interval) if tp.given(contour_interval) else float(_SCALE_INTERVAL[chosen_scale])
    base = float(base_contour) if tp.given(base_contour) else 0.0
    zf = float(z_factor) if tp.given(z_factor) else 1.0
    supplemental = tp.word(supplemental_contours, "NONE")
    if supplemental not in ("NONE", "HALF_AUXILIARY", "QUARTER_AUXILIARY"):
        raise ExecuteError(f"ERROR 000800: {tool}: supplemental_contours is NONE, HALF_AUXILIARY or QUARTER_AUXILIARY.")
    elevation = tp.require_field(tool, target, elevation_field)
    code = tp.find_field(target, code_field) if tp.given(code_field) else tp.find_field(target, "HQC")
    if tp.given(code_field) and code is None:
        raise ExecuteError(f"ERROR 000728: {tool}: the field '{code_field}' does not exist.")
    codes = {"index": int(index_code) if tp.given(index_code) else 1,
             "intermediate": int(intermediate_code) if tp.given(intermediate_code) else 2,
             "depression_index": int(depression_code) if tp.given(depression_code) else 5,
             "depression_intermediate": int(depression_intermediate_code) if tp.given(depression_intermediate_code) else 6,
             "half_auxiliary": int(half_auxiliary_code) if tp.given(half_auxiliary_code) else 3,
             "quarter_auxiliary": int(quarter_auxiliary_code) if tp.given(quarter_auxiliary_code) else 14,
             "depression_auxiliary": int(depression_auxiliary_code) if tp.given(depression_auxiliary_code) else 22}
    every = float(index_interval) if tp.given(index_interval) else 5 * interval
    smoothing = tp.linear(tool, raster_smooth_tolerance, srid, 0.0)
    if smoothing:
        from scipy.ndimage import gaussian_filter
        from ._raster import Raster
        sigma = smoothing / raster._grid.width
        filled = np.where(raster._mask, np.nanmean(np.where(raster._mask, np.nan, raster._array)), raster._array)
        raster = Raster._of(gaussian_filter(filled.astype(np.float64), sigma), raster._mask, raster._grid)
    values = cells_in(raster, aoi.buffer(2 * raster._grid.width)) * zf
    if not values.size:
        raise ExecuteError(f"ERROR 000800: {tool}: the raster holds no value inside the area of interest.")
    step = interval / {"NONE": 1, "HALF_AUXILIARY": 2, "QUARTER_AUXILIARY": 4}[supplemental]
    low, high = math.ceil((values.min() - base) / step), math.floor((values.max() - base) / step)
    levels = [base + k * step for k in range(low, high + 1)]
    with_zero = tp.flag(tool, zero_contour, "ZERO_CONTOUR", "NO_ZERO_CONTOUR", "NO_ZERO_CONTOUR")
    if not with_zero and values.min() >= 0:
        levels = [v for v in levels if abs(v) > 1e-12]
    elif with_zero and 0.0 not in levels:
        levels.append(0.0)
    shortest = tp.linear(tool, minimum_length, srid, 0.0)
    line_smoothing = tp.linear(tool, contour_smooth_tolerance, srid, 0.0)
    subtype_pair = _subtype_code(tool, target, contour_subtype)
    made = []
    for level, line in _contour_lines(raster, levels, zf):
        if line_smoothing:
            line = tp.smooth(line, line_smoothing)
        for part in tp.parts(line.intersection(aoi)):
            if part.geom_type == "LineString" and part.length > 0 and part.length >= shortest:
                made.append((level, part))
    rings = [(level, Polygon(line.coords)) for level, line in made if closed(line)]
    rows = []
    for level, line in made:
        depression = False
        if closed(line):
            ring = Polygon(line.coords)
            inner = unary_union([p for v, p in rings if not p.equals(ring) and ring.contains(p)])
            depression = is_depression(raster, ring, level / zf, inner)
        row = {elevation: level, "SHAPE@": MultiLineString([line])}
        if code:
            row[code] = classify(level, base, interval, every, supplemental, codes, depression)
        if subtype_pair:
            row[subtype_pair[0]] = subtype_pair[1]
        rows.append(row)
    tp.insert(target, rows)
    AddMessage(f"{tool}: {len(rows)} contours appended.")
    return Result(tp.text(contour_features))


def identify_contours(in_contour_features, in_rasters, contour_height_field, contour_code_field,
                      contour_index_interval, index_code, intermediate_code, depression_code,
                      depression_intermediate_code, depression_contours_count, area_of_interest, z_factor,
                      supplemental_contours, half_auxiliary_code, quarter_auxiliary_code, depression_auxiliary_code,
                      contour_interval, base_contour):
    tool = "IdentifyContours"
    path = tp.dataset(in_contour_features)
    height = tp.require_field(tool, path, contour_height_field)
    code = tp.require_field(tool, path, contour_code_field)
    raster = raster_of(tool, in_rasters)
    zf = float(z_factor) if tp.given(z_factor) else 1.0
    interval = float(contour_interval) if tp.given(contour_interval) else 20.0
    base = float(base_contour) if tp.given(base_contour) else 0.0
    every = float(contour_index_interval) if tp.given(contour_index_interval) else 100.0
    supplemental = tp.word(supplemental_contours, "NONE")
    count = int(depression_contours_count) if tp.given(depression_contours_count) else None
    if count is not None and count <= 0:
        raise ExecuteError(f"ERROR 000800: {tool}: depression_contours_count must be greater than 0.")
    codes = {"index": int(index_code) if tp.given(index_code) else 1,
             "intermediate": int(intermediate_code) if tp.given(intermediate_code) else 2,
             "depression_index": int(depression_code) if tp.given(depression_code) else 5,
             "depression_intermediate": int(depression_intermediate_code) if tp.given(depression_intermediate_code) else 6,
             "half_auxiliary": int(half_auxiliary_code) if tp.given(half_auxiliary_code) else 3,
             "quarter_auxiliary": int(quarter_auxiliary_code) if tp.given(quarter_auxiliary_code) else 14,
             "depression_auxiliary": int(depression_auxiliary_code) if tp.given(depression_auxiliary_code) else 22}
    aoi = unary_union([r.shape for r in tp.read(area_of_interest, fields=[]) if r.shape is not None]) \
        if tp.given(area_of_interest) else None
    rows = [r for r in tp.read(in_contour_features, fields=[height]) if r.shape is not None and r[height] is not None]
    if aoi is not None:
        rows = [r for r in rows if r.shape.intersects(aoi)]
    rings = {}
    for r in rows:
        line = tp.single_line(r.shape)
        if line is not None and closed(line):
            rings[r.oid] = Polygon(line.coords)
    depressions = {}
    for oid, ring in rings.items():
        r = next(x for x in rows if x.oid == oid)
        inner = unary_union([p for o, p in rings.items() if o != oid and ring.contains(p)])
        if is_depression(raster, ring, float(r[height]) / zf, inner):
            depressions[oid] = ring
    coded_depressions = set(depressions)
    if count is not None:
        coded_depressions = {o for o, ring in depressions.items()
                             if 1 + sum(1 for p, other in depressions.items() if p != o and ring.contains(other)) <= count}
    changes = {}
    for r in rows:
        level = float(r[height])
        changes[r.oid] = {code: classify(level, base, interval, every, supplemental, codes, r.oid in coded_depressions)}
    tp.update(path, changes)
    AddMessage(f"{tool}: {len(changes)} contours coded, {len(coded_depressions)} as depressions.")
    return Result(tp.text(in_contour_features))


# ---- spot heights ----------------------------------------------------------------------------------------------------

def _closed_rings(rows, field):
    out = []
    for r in rows:
        line = tp.single_line(r.shape)
        if line is not None and closed(line) and r[field] is not None:
            out.append((r.oid, float(r[field]), Polygon(line.coords)))
    return out


def _innermost(rings):
    """The closed contours that contain no other contour."""
    return [(o, v, p) for o, v, p in rings if not any(q is not p and p.contains(q) for _, _, q in rings)]


def _enclosing(rings, ring):
    found = [(p.area, v) for _, v, p in rings if p is not ring and p.contains(ring)]
    return min(found)[1] if found else None


def top_or_depression(raster, rings, value, ring):
    """'top' or 'depression' for a closed contour: by the contour enclosing it when there is one, else by the raster
    inside it."""
    outer = _enclosing(rings, ring)
    if outer is not None and abs(outer - value) > 1e-9:
        return "top" if outer < value else "depression"
    if raster is None:
        return "top"
    return "depression" if is_depression(raster, ring, value) else "top"


def generate_spot_heights(in_contour_features, in_raster, target_spot_features, contour_height_field, spot_height_field,
                          spot_height_subtype, area_of_interest, scale, z_factor):
    from . import _layers
    tool = "GenerateSpotHeights"
    raster = raster_of(tool, in_raster)
    height = tp.require_field(tool, tp.dataset(in_contour_features), contour_height_field)
    target = tp.dataset(target_spot_features)
    spot = tp.require_field(tool, target, spot_height_field)
    chosen = _scale(tool, scale)
    small_scale = chosen in ("1:250,000", "1:500,000", "1:1,000,000")
    zf = float(z_factor) if tp.given(z_factor) else (3.28084 if small_scale else 1.0)
    in_feet = abs(zf - 3.28084) < 1e-9
    rows = [r for r in tp.read(in_contour_features, fields=[height]) if r.shape is not None]
    if tp.given(area_of_interest):
        extent = box(*unary_union([r.shape for r in tp.read(area_of_interest, fields=[])]).bounds)
    else:
        layer = _layers.find(in_contour_features)
        extent = box(*unary_union([r.shape for r in rows]).bounds) if layer is not None and layer.selection and rows \
            else box(raster._grid.left, raster._grid.bottom, raster._grid.right, raster._grid.top)
    rows = [r for r in rows if r.shape.intersects(extent)]
    rings = _closed_rings(rows, height)
    subtype_pair = _subtype_code(tool, target, spot_height_subtype)
    made = []
    for oid, value, ring in _innermost(rings):
        kind = top_or_depression(raster, rings, value, ring)
        extreme, where = extreme_cells(raster, ring, kind == "top")
        if extreme is None:
            continue  # the page: no spot height where the contours do not overlap the DEM
        elevation = extreme * zf
        if in_feet:
            elevation = math.ceil(round(elevation, 6) / 5.0) * 5
        for x, y in where:
            row = {spot: elevation, "SHAPE@": Point(x, y)}
            if subtype_pair:
                row[subtype_pair[0]] = subtype_pair[1]
            made.append(row)
    tp.insert(target, made)
    AddMessage(f"{tool}: {len(made)} spot heights created.")
    return Result(tp.text(target_spot_features))


def validate_spot_heights(in_contour_features, contour_height_field, contour_interval, in_rasters, in_spot_features,
                          spot_height_field, area_of_interest, z_factor, use_rasters):
    from .management import AddField
    tool = "ValidateSpotHeights"
    height = tp.require_field(tool, tp.dataset(in_contour_features), contour_height_field)
    interval = float(contour_interval) if tp.given(contour_interval) else 20.0
    zf = float(z_factor) if tp.given(z_factor) else 1.0
    with_rasters = tp.flag(tool, use_rasters, "USE_RASTERS", "NO_USE_RASTERS", "USE_RASTERS")
    raster = raster_of(tool, in_rasters) if with_rasters else None
    path = tp.dataset(in_spot_features)
    spot = tp.require_field(tool, path, spot_height_field)
    if tp.find_field(path, "IS_VALID") is None:
        AddField(path, "IS_VALID", "SHORT")
    flag_field = tp.find_field(path, "IS_VALID")
    aoi = unary_union([r.shape for r in tp.read(area_of_interest, fields=[])]) if tp.given(area_of_interest) else None
    rings = _closed_rings(tp.read(in_contour_features, fields=[height]), height)
    changes = {}
    for r in tp.read(in_spot_features, fields=[spot]):
        if r.shape is None or r[spot] is None or (aoi is not None and not aoi.covers(r.shape)):
            continue
        holding = [(p.area, v, p) for _, v, p in rings if p.contains(r.shape)]
        if not holding:
            changes[r.oid] = {flag_field: 4}
            continue
        _, value, ring = min(holding, key=lambda t: t[0])
        kind = top_or_depression(raster, rings, value, ring) if raster is not None else "top"
        elevation = float(r[spot])
        if kind == "top":
            wrong_side, missing = elevation <= value, elevation >= value + interval
        else:
            wrong_side, missing = elevation >= value, elevation <= value - interval
        if wrong_side:
            result = 3
        elif missing:
            result = 2
        elif raster is not None and kind == "top":
            values = cells_in(raster, ring)
            result = 1 if values.size and float(values.max()) * zf > elevation + 1e-9 else 0
        else:
            result = 0
        changes[r.oid] = {flag_field: result}
    tp.update(path, changes)
    AddMessage(f"{tool}: {sum(1 for c in changes.values() if c[flag_field])} of {len(changes)} spot heights flagged.")
    return Result(tp.text(in_spot_features))


def thin_spot_heights(in_features, area_of_interest, elevation_field, invisibility_field, high_low_spots,
                      search_distance, max_spots, input_contours, contour_code_field, depression_code_value):
    tool = "ThinSpotHeights"
    path = tp.dataset(in_features)
    srid = tp.srid_of(path)
    elevation = tp.require_field(tool, path, elevation_field)
    hidden = tp.require_field(tool, path, invisibility_field)
    marked = tp.require_field(tool, path, high_low_spots) if tp.given(high_low_spots) else None
    distance = tp.linear(tool, search_distance, srid, tp.linear(tool, "1300 Meters", srid))
    cap = int(max_spots) if tp.given(max_spots) else None
    aoi = _one_area(tool, area_of_interest).shape
    fields = [elevation] + ([marked] if marked else [])
    spots = [r for r in tp.read(in_features, fields=fields) if r.shape is not None and aoi.covers(r.shape)
             and r[elevation] is not None]
    priority = {}
    if marked:
        for r in spots:
            if r[marked] not in (None, "", 0, "0"):
                priority[r.oid] = 0
    elif spots:
        priority[max(spots, key=lambda r: (r[elevation], -r.oid)).oid] = 0
        priority[min(spots, key=lambda r: (r[elevation], r.oid)).oid] = 0
    if tp.given(input_contours):
        contour_path = tp.dataset(input_contours)
        code = tp.require_field(tool, contour_path, contour_code_field) if tp.given(contour_code_field) else None
        depressed = {str(v).strip() for v in tp.listed(depression_code_value)}
        rows = tp.read(input_contours, fields=[code] if code else [])
        rings = []
        for r in rows:
            line = tp.single_line(r.shape)
            if line is not None and closed(line):
                rings.append((Polygon(line.coords), code is not None and str(r[code]) in depressed))
        innermost = [(p, d) for p, d in rings if not any(q is not p and p.contains(q) for q, _ in rings)]
        for ring, depression in innermost:
            inside = [r for r in spots if ring.contains(r.shape)]
            if inside:
                chosen = (min if depression else max)(inside, key=lambda r: r[elevation])
                priority.setdefault(chosen.oid, 1)
    order = sorted(spots, key=lambda r: (priority.get(r.oid, 2), -float(r[elevation]), r.oid))
    kept = []
    for r in order:
        if cap is not None and len(kept) >= cap:
            break
        if priority.get(r.oid) == 0 or all(r.shape.distance(k.shape) >= distance for k in kept):
            kept.append(r)
    keep = {k.oid for k in kept}
    tp.update(path, {r.oid: {hidden: 0 if r.oid in keep else 1} for r in spots})
    AddMessage(f"{tool}: {len(keep)} of {len(spots)} spot heights kept visible.")
    return Result(tp.text(in_features))


# ---- SetLineDirection ------------------------------------------------------------------------------------------------

def set_line_direction(in_line_features, digital_elevation_model, line_direction, reverse_direction, vertical_tolerance):
    tool = "SetLineDirection"
    mode = tp.word(line_direction, "DOWNHILL_FLOW")
    if mode not in ("DOWNHILL", "DOWNHILL_FLOW", "DOWNHILL_SPLIT_FLOW"):
        raise ExecuteError(f"ERROR 000800: {tool}: line_direction is DOWNHILL, DOWNHILL_FLOW or DOWNHILL_SPLIT_FLOW.")
    flip = tp.flag(tool, reverse_direction, "REVERSE_DIRECTION", "KEEP_DIRECTION", "KEEP_DIRECTION")
    raster = raster_of(tool, digital_elevation_model)
    path = tp.dataset(in_line_features)
    names = tp.field_names(path)
    tolerance = float(str(vertical_tolerance).split()[0]) if tp.given(vertical_tolerance) else 0.0
    rows = [r for r in tp.read(in_line_features) if tp.single_line(r.shape) is not None]

    def z(point):
        found = sample(raster, point[0], point[1])
        return float("nan") if found is None else found

    lines = {r.oid: tp.single_line(r.shape) for r in rows}
    new_rows = []
    if mode == "DOWNHILL_SPLIT_FLOW":
        for r in rows:
            coords = list(lines[r.oid].coords)
            heights = [z(c) for c in coords]
            cuts = [i for i in range(1, len(coords) - 1)
                    if heights[i] > heights[i - 1] + tolerance and heights[i] > heights[i + 1] + tolerance]
            if not cuts:
                continue
            pieces, start = [], 0
            for i in cuts + [len(coords) - 1]:
                pieces.append(LineString(coords[start:i + 1]))
                start = i
            lines[r.oid] = pieces[0]
            for piece in pieces[1:]:
                values = {n: r[n] for n in names}
                values["SHAPE@"] = piece
                new_rows.append(values)
    oriented = {}
    if mode == "DOWNHILL_FLOW":
        oriented = _flow_orientation(lines, z, tolerance)
    changes = {}
    for oid, line in lines.items():
        coords = list(line.coords)
        if mode == "DOWNHILL_FLOW":
            reverse = oriented.get(oid, False)
        else:
            reverse = z(coords[0]) < z(coords[-1]) - tolerance
        if reverse != flip:
            coords = coords[::-1]
        changes[oid] = {"SHAPE@": MultiLineString([LineString(coords)])}
    for values in new_rows:
        coords = list(values["SHAPE@"].coords)
        reverse = z(coords[0]) < z(coords[-1]) - tolerance
        if reverse != flip:
            coords = coords[::-1]
        values["SHAPE@"] = MultiLineString([LineString(coords)])
    tp.update(path, changes)
    tp.insert(path, new_rows)
    return Result(tp.text(in_line_features))


def _flow_orientation(lines, z, tolerance):
    """{oid: whether to reverse} so every line runs toward its network's outlets: the end nodes (touching one line)
    within the tolerance of the lowest end node of the network; each line flows from its node farther (in lines) from an
    outlet to its nearer node, and downhill where both are as far."""
    from collections import deque
    nodes = {}
    for oid, line in lines.items():
        for at in (line.coords[0], line.coords[-1]):
            nodes.setdefault(tp.key(at), []).append(oid)
    seen, result = set(), {}
    for start in list(nodes):
        if start in seen:
            continue
        component, queue = [], deque([start])
        seen.add(start)
        while queue:
            node = queue.popleft()
            component.append(node)
            for oid in nodes[node]:
                line = lines[oid]
                for at in (tp.key(line.coords[0]), tp.key(line.coords[-1])):
                    if at not in seen:
                        seen.add(at)
                        queue.append(at)
        ends = [n for n in component if len(nodes[n]) == 1] or component
        heights = {n: z(n) for n in component}
        lowest = min(heights[n] for n in ends)
        outlets = [n for n in ends if heights[n] <= lowest + tolerance]
        distance = {n: 0 for n in outlets}
        queue = deque(outlets)
        while queue:
            node = queue.popleft()
            for oid in nodes[node]:
                line = lines[oid]
                for at in (tp.key(line.coords[0]), tp.key(line.coords[-1])):
                    if at not in distance:
                        distance[at] = distance[node] + 1
                        queue.append(at)
        for oid in {o for n in component for o in nodes[n]}:
            line = lines[oid]
            a, b = tp.key(line.coords[0]), tp.key(line.coords[-1])
            if distance[a] == distance[b]:
                result[oid] = heights[a] < heights[b] - tolerance
            else:
                result[oid] = distance[a] < distance[b]
    return result


# ---- elevation bands -------------------------------------------------------------------------------------------------

_BAND_SHARES = {1: [1.0], 2: [0.6, 0.4], 3: [0.3, 0.4, 0.3], 4: [0.2, 0.3, 0.3, 0.2]}
_BAND_FIELDS = (("Low_Value", "Double"), ("High_Value", "Double"), ("TintBand", "Integer"))


def band_count(low, high):
    """The bands the TDS Topographic Map specification §3.3.6.3 gives an elevation range, as the page cites it."""
    span = high - low
    if span <= 100:
        return 0
    return 3 if span <= 600 else 4


def band_limits(values, bands, interval):
    """The band limits: the lowest and highest values, and between them the values at the cumulative area shares
    snapped to the nearest contour of ``interval``."""
    values = np.sort(values)
    low, high = float(values[0]), float(values[-1])
    limits, share = [low], 0.0
    for part in _BAND_SHARES[bands][:-1]:
        share += part
        at = float(np.quantile(values, share))
        snapped = round(at / interval) * interval
        limits.append(min(max(snapped, low), high))
    limits.append(high)
    return limits


def _clean(shape, min_area):
    """Parts and holes smaller than ``min_area`` removed."""
    out = []
    for p in tp.polygons(shape):
        if p.area < min_area:
            continue
        holes = [r for r in p.interiors if Polygon(r).area >= min_area]
        out.append(Polygon(p.exterior, holes))
    return unary_union(out)


def bands_from_raster(tool, raster, aoi, interval, min_area, smoothing, hydro, bands, srid, min_island=None):
    """[(low, high, band number, shape)] of a raster's elevation bands inside an area."""
    from rasterio.features import shapes as polygonise
    xs, ys = raster._grid.centres()
    gx, gy = np.meshgrid(xs, ys)
    inside = shapely.contains_xy(aoi, gx, gy) & ~raster._mask
    counted = inside.copy()
    if hydro is not None and not hydro.is_empty:
        counted &= ~shapely.contains_xy(hydro, gx, gy)
    values = raster._array.astype(np.float64)[counted]
    if not values.size:
        raise ExecuteError(f"ERROR 000800: {tool}: the raster holds no value inside the area of interest.")
    count = bands if bands is not None else band_count(float(values.min()), float(values.max()))
    if count == 0:
        AddWarning(f"{tool}: the elevation range is 100 or less, for which the specification gives no bands.")
        return []
    limits = band_limits(values, count, interval)
    classes = np.zeros(raster._array.shape, dtype=np.int32)
    for k in range(count):
        upper = limits[k + 1]
        chosen = (raster._array >= limits[k]) & ((raster._array < upper) if k < count - 1 else (raster._array <= upper))
        classes[chosen & inside] = k + 1
    out = []
    transform = raster._grid.transform()
    for k in range(count):
        pieces = [shapely.geometry.shape(g) for g, v in polygonise(classes, mask=classes == k + 1, transform=transform)
                  if int(v) == k + 1]
        shape = unary_union(pieces).intersection(aoi) if pieces else Polygon()
        shape = tp.smooth(shape, smoothing).intersection(aoi) if smoothing else shape
        shape = _clean(shape, min_area if min_island is None else min_island)
        shape = unary_union([p for p in tp.polygons(shape) if p.area >= min_area])
        if not shape.is_empty:
            out.append((limits[k], limits[k + 1], k + 1, shape))
    return out


def write_bands(path, srid, bands):
    tp.create(path, "Polygon", srid, _BAND_FIELDS)
    tp.insert(path, [{"Low_Value": low, "High_Value": high, "TintBand": k, "SHAPE@": tp.as_multi(shape, "Polygon")}
                     for low, high, k, shape in bands])


def generate_elevation_bands(in_raster, in_aoi, out_feature_class, contour_interval, min_area, smooth_tolerance,
                             in_hydro_features, number_of_bands):
    tool = "GenerateElevationBands"
    raster = raster_of(tool, in_raster)
    interval = float(contour_interval) if tp.given(contour_interval) else 20.0
    if interval not in (10.0, 20.0, 40.0, 80.0):
        raise ExecuteError(f"ERROR 000800: {tool}: contour_interval is 10, 20, 40 or 80.")
    aoi = _one_area(tool, in_aoi).shape
    srid = tp.srid_of(in_aoi)
    area = tp.areal(tool, min_area, srid, tp.areal(tool, "0.00016 SquareDecimalDegrees", srid))
    smoothing = tp.linear(tool, smooth_tolerance, srid, tp.linear(tool, "0.002 DecimalDegrees", srid))
    hydro = unary_union([r.shape for each in tp.listed(in_hydro_features) for r in tp.read(each, fields=[])
                         if r.shape is not None]) if tp.given(in_hydro_features) else None
    bands = int(number_of_bands) if tp.given(number_of_bands) else None
    if bands is not None and not 1 <= bands <= 4:
        raise ExecuteError(f"ERROR 000800: {tool}: number_of_bands is 1 to 4.")
    out = tp.text(out_feature_class)
    from .analysis import _out
    out = _out(out)
    write_bands(out, srid, bands_from_raster(tool, raster, aoi, interval, area, smoothing, hydro, bands, srid))
    return Result(out)


_JOG_A = [(0, 1315), (1315, 1970), (1970, 5250), (5250, 9190), (9190, 12470), (12470, 29200)]


def generate_elevation_bands_from_features(contour_features, elevation_field, area_of_interest, out_feature_class,
                                           exclusion_features, product, band_interval, band_values):
    tool = "GenerateElevationBandsFromFeatures"
    field = tp.require_field(tool, tp.dataset(contour_features), elevation_field)
    aoi = _one_area(tool, area_of_interest).shape
    if abs(aoi.area - aoi.minimum_rotated_rectangle.area) > 1e-6 * max(aoi.area, 1):
        AddWarning(f"{tool}: the area of interest is not rectangular.")
    chosen = tp.word(product, "CUSTOM").replace("_", "-")
    if chosen in ("ONC", "TPC"):
        raise NotBuiltError(f"arcpy.topographic.{tool}: product '{chosen}' is not built in grapy — the band values the "
                            "tool fills in for it are not on the page; give them as Custom band_values.")
    rows = [r for r in tp.read(contour_features, fields=[field]) if r.shape is not None and r[field] is not None]
    contours = [(float(r[field]), part) for r in rows for part in tp.parts(r.shape.intersection(aoi))
                if part.geom_type == "LineString"]
    if not contours:
        raise ExecuteError(f"ERROR 000800: {tool}: no contour lies inside the area of interest.")
    levels = sorted({v for v, _ in contours})
    steps = [b - a for a, b in zip(levels, levels[1:]) if b - a > 1e-9]
    interval = min(steps) if steps else 1.0
    if chosen == "JOG-A":
        table = list(_JOG_A)
    elif tp.given(band_values):
        table = [(float(r[0]), float(r[1])) for r in tp.table_rows(band_values)]
    elif tp.given(band_interval):
        step = float(band_interval)
        top = max(levels) + interval
        table = [(k * step, (k + 1) * step) for k in range(int(math.ceil(top / step)))]
    else:
        raise ExecuteError(f"ERROR 000735: {tool}: a Custom product needs band_interval or band_values.")
    faces = list(polygonize(unary_union([line for _, line in contours] + [aoi.boundary])))
    faces = [f for f in faces if aoi.buffer(1e-9).covers(f.representative_point())]
    adjacent = []
    for face in faces:
        touching = {v for v, line in contours if face.boundary.intersection(line).length > 1e-9}
        adjacent.append(touching)
    ranges = [(min(t), max(t)) if len(t) >= 2 else None for t in adjacent]
    changed = True
    while changed:
        changed = False
        for i, face in enumerate(faces):
            if ranges[i] is not None or len(adjacent[i]) != 1:
                continue
            v = next(iter(adjacent[i]))
            for j, other in enumerate(faces):
                if j == i or ranges[j] is None or v not in adjacent[j]:
                    continue
                if face.boundary.intersection(other.boundary).length <= 1e-9:
                    continue
                low, high = ranges[j]
                ranges[i] = (v, v + interval) if high <= v + 1e-9 else (v - interval, v)
                changed = True
                break
    for i, t in enumerate(adjacent):
        if ranges[i] is None and len(t) == 1:
            v = next(iter(t))
            ranges[i] = (v - interval, v) if v == levels[0] else (v, v + interval)
    exclusion = unary_union([r.shape for each in tp.listed(exclusion_features) for r in tp.read(each, fields=[])
                             if r.shape is not None]) if tp.given(exclusion_features) else None
    grouped = {}
    for face, span in zip(faces, ranges):
        if span is None:
            continue   # the page: an elevation range no contour covers gets no band
        middle = (max(span[0], 0.0) + span[1]) / 2
        for k, (low, high) in enumerate(table):
            if low <= middle < high:
                grouped.setdefault(k, []).append(face)
                break
    srid = tp.srid_of(area_of_interest)
    from .analysis import _out
    out = _out(tp.text(out_feature_class))
    bands = []
    for k in sorted(grouped):
        shape = unary_union(grouped[k])
        if exclusion is not None:
            shape = shape.difference(exclusion)
        if not shape.is_empty:
            bands.append((table[k][0], table[k][1], k + 1, shape))
    if tp.exists(out):
        for name, _ in _BAND_FIELDS:
            tp.require_field(tool, out, name)
        tp.insert(out, [{"Low_Value": low, "High_Value": high, "TintBand": k, "SHAPE@": tp.as_multi(s, "Polygon")}
                        for low, high, k, s in bands])
    else:
        write_bands(out, srid, bands)
    return Result(out)
