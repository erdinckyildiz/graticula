"""The solar radiation tools of arcpy.sa (and the time classes arcpy.ia shares) on the model in ``_insolation`` —
ADR-243. Each is an equivalent (ADR-224 §5.6): the equations are Esri's, the discretisation the pages describe in words
is built as they describe it, and what they leave open is recorded as INFERRED there and in ADR-243 §5."""
from __future__ import annotations

import datetime as _dt
import math
import os

import numpy as np

from . import _insolation as _ins
from ._runtime import NotBuiltError, equivalent

LEGACY = (_ins.FU_RICH, _ins.FU_RICH_SOURCE)
MODERN = ("the Fu and Rich direct and diffuse equations evaluated each half hour with the sun placed by NOAA's "
          "Solar Calculator for each block of cells", _ins.NOAA_SOURCE)

TimeWithinDay = _ins.TimeWithinDay
TimeMultipleDays = _ins.TimeMultipleDays
TimeSpecialDays = _ins.TimeSpecialDays
TimeWholeYear = _ins.TimeWholeYear


def _refuse(tool, parameter, value, why):
    raise NotBuiltError(f"arcpy.sa.{tool}: {parameter} '{value}' is not built in grapy: {why}.")


def _given(value):
    return value not in (None, "", "#")


def _flag(value, yes, default=False):
    if isinstance(value, bool):
        return value
    if not _given(value):
        return default
    return str(value).upper() == yes


def _latitude(raster, latitude):
    """The page: the given latitude; else the mean latitude of a raster with a coordinate system; else 45."""
    if _given(latitude):
        value = float(latitude)
        if not -90 <= value <= 90:
            raise ValueError("latitude must be from -90 to 90 degrees.")
        return value
    g = raster._grid
    if g.srid:
        from . import _solar
        return _solar.place((g.left + g.right) / 2, (g.top + g.bottom) / 2, g.srid)[1]
    return 45.0


def _check(tool, sky_size, calculation_directions, zenith_divisions, azimuth_divisions):
    sky_size, directions = int(sky_size), int(calculation_directions)
    zd, ad = int(zenith_divisions), int(azimuth_divisions)
    if not 1 < sky_size <= 10000:
        raise ValueError(f"{tool}: sky_size must be from 2 to 10,000 cells.")
    if directions <= 0 or directions % 8:
        raise ValueError(f"{tool}: calculation_directions must be a multiple of 8.")
    if not 0 < zd < sky_size / 2:
        raise ValueError(f"{tool}: zenith_divisions must be greater than 0 and less than half the sky size.")
    if not 0 < ad < 160 or ad % 8:
        raise ValueError(f"{tool}: azimuth_divisions must be a multiple of 8, greater than 0 and less than 160.")
    return sky_size, directions, zd, ad


def _surface(raster, z_factor):
    z = raster._array.astype(np.float64) * float(z_factor)
    return np.where(raster._mask, np.nan, z)


def _slope_aspect_horn(raster, z_factor):
    from .sa import _aspect_degrees, _gradients
    dx, dy = _gradients(raster, float(z_factor))
    return np.degrees(np.arctan(np.hypot(dx, dy))), _aspect_degrees(dx, dy)


def _config(time_configuration):
    return time_configuration if _given(time_configuration) else TimeMultipleDays()


# ---- Area Solar Radiation


@equivalent(*LEGACY)
def AreaSolarRadiation(in_surface_raster, latitude=None, sky_size=200, time_configuration=None, day_interval=14,
                       hour_interval=0.5, each_interval="NOINTERVAL", z_factor=1, slope_aspect_input_type="FROM_DEM",
                       calculation_directions=32, zenith_divisions=8, azimuth_divisions=8, diffuse_model_type="UNIFORM_SKY",
                       diffuse_proportion=0.3, transmittivity=0.5, out_direct_radiation_raster=None,
                       out_diffuse_radiation_raster=None, out_direct_duration_raster=None):
    """Global radiation (direct + diffuse) in watt hours per square metre for every cell; with ``INTERVAL`` one band per
    interval. The direct duration is integer hours, as the page says.

    INFERRED: the optical path's elevation is the cell's z times z_factor, in metres; a cell's horizon is traced from
    the cell's own height over the whole raster."""
    from ._raster import Raster, _refuse_environment, as_raster
    _refuse_environment("arcpy.sa.AreaSolarRadiation")
    raster = as_raster(in_surface_raster, "AreaSolarRadiation")
    size, directions, zd, ad = _check("AreaSolarRadiation", sky_size, calculation_directions, zenith_divisions,
                                      azimuth_divisions)
    kind = str(slope_aspect_input_type or "FROM_DEM").upper()
    if kind not in ("FROM_DEM", "FLAT_SURFACE"):
        raise ValueError(f"AreaSolarRadiation: slope_aspect_input_type '{slope_aspect_input_type}' is not FROM_DEM or "
                         "FLAT_SURFACE.")
    lat = _latitude(raster, latitude)
    sectors, labels = _ins.sun_sectors(_config(time_configuration), lat, day_interval, hour_interval)
    z = _surface(raster, z_factor)
    valid = ~raster._mask
    rows, cols = np.nonzero(valid)
    g = raster._grid
    horizon = _ins.horizons(z, g.width, g.height, rows, cols, z[rows, cols], directions)
    if kind == "FLAT_SURFACE":
        slope, aspect = np.zeros(rows.size), -np.ones(rows.size)
    else:
        s, a = _slope_aspect_horn(raster, z_factor)
        slope, aspect = s[rows, cols], a[rows, cols]
    bands = len(labels)
    direct, diffuse, duration = _ins.insolation(horizon, z[rows, cols], slope, aspect, _ins.Sky(size), sectors, bands,
                                                zd, ad, diffuse_model_type, diffuse_proportion, transmittivity)
    every = _flag(each_interval, "INTERVAL")

    def out(values, integer=False):
        if not every:
            values = values.sum(axis=1, keepdims=True)
        arrays, masks = [], []
        for b in range(values.shape[1]):
            cell = np.zeros(raster._mask.shape, np.int32 if integer else np.float32)
            cell[rows, cols] = np.rint(values[:, b]) if integer else values[:, b]
            arrays.append(cell)
            masks.append(raster._mask.copy())
        if len(arrays) == 1:
            return Raster._of(arrays[0], masks[0], g)
        return Raster._of_bands(arrays, masks, g)

    for path, values, integer in ((out_direct_radiation_raster, direct, False),
                                  (out_diffuse_radiation_raster, diffuse, False),
                                  (out_direct_duration_raster, duration, True)):
        if _given(path):
            out(values, integer).save(path)
    return out(direct + diffuse)


# ---- Points Solar Radiation


def _locations(source, srid, tool):
    """[(x, y, slope or None, aspect or None, row of attributes)], the columns, and the shape type: a point feature
    class, or a text table of x, y and optionally slope and aspect, as the page shows."""
    path = os.fspath(source)
    if path.lower().endswith((".txt", ".csv", ".asc")):
        with open(path, encoding="utf-8") as handle:
            lines = [ln.strip() for ln in handle if ln.strip()]
        header = [h.strip().lower() for h in lines[0].replace(",", " ").replace("\t", " ").split()]
        has_header = not _number(header[0])
        names = header if has_header else ["x", "y", "slope", "aspect"][:len(header)]
        out = []
        for line in lines[1 if has_header else 0:]:
            values = [float(v) for v in line.replace(",", " ").replace("\t", " ").split()]
            row = dict(zip(names, values))
            out.append((row["x"], row["y"], row.get("slope"), row.get("aspect"), []))
        return out, [], "POINT"
    from . import da
    from .analysis import _schema
    columns, shape, _, _ = _schema(path)
    if str(shape).upper() not in ("POINT",):
        raise ValueError(f"{tool}: '{source}' is not a point feature class.")
    names = [c.name for c in columns]
    lower = [n.lower() for n in names]
    out = []
    with da.SearchCursor(path, [*names, "SHAPE@"]) as rows:
        for row in rows:
            if row[-1] is None:
                continue
            p = row[-1]._shape
            slope = row[lower.index("slope")] if "slope" in lower else None
            aspect = row[lower.index("aspect")] if "aspect" in lower else None
            out.append((p.x, p.y, slope, aspect, list(row[:-1])))
    return out, columns, "POINT"


def _number(text):
    try:
        float(text)
        return True
    except ValueError:
        return False


def _bilinear(z, row, col):
    from scipy.ndimage import map_coordinates
    r = np.clip(row, 0, z.shape[0] - 1)
    c = np.clip(col, 0, z.shape[1] - 1)
    filled = np.where(np.isnan(z), np.nanmean(z), z)
    return map_coordinates(filled, [np.atleast_1d(r), np.atleast_1d(c)], order=1, mode="nearest")


@equivalent(*LEGACY)
def PointsSolarRadiation(in_surface_raster, in_points_feature_or_table, out_global_radiation_features, height_offset=0,
                         latitude=None, sky_size=200, time_configuration=None, day_interval=14, hour_interval=0.5,
                         each_interval="NOINTERVAL", z_factor=1, slope_aspect_input_type="FROM_DEM",
                         calculation_directions=32, zenith_divisions=8, azimuth_divisions=8,
                         diffuse_model_type="UNIFORM_SKY", diffuse_proportion=0.3, transmittivity=0.5,
                         out_direct_radiation_features=None, out_diffuse_radiation_features=None,
                         out_direct_duration_features=None):
    """Global radiation in watt hours per square metre at each point, written to a copy of the points; with
    ``INTERVAL`` one field per interval, t0, t1 … as the page names them.

    INFERRED: without intervals the one field is ``T0``, the first of the page's series; a point's elevation is the
    surface interpolated bilinearly at it; its slope and aspect are the cell's (Horn's method) unless the table gives
    them; a location off the surface is NoData (null)."""
    from . import _gpkg, da
    from ._raster import _refuse_environment, as_raster
    from ._result import Result
    from .analysis import _out
    _refuse_environment("arcpy.sa.PointsSolarRadiation")
    raster = as_raster(in_surface_raster, "PointsSolarRadiation")
    size, directions, zd, ad = _check("PointsSolarRadiation", sky_size, calculation_directions, zenith_divisions,
                                      azimuth_divisions)
    kind = str(slope_aspect_input_type or "FROM_DEM").upper()
    lat = _latitude(raster, latitude)
    sectors, labels = _ins.sun_sectors(_config(time_configuration), lat, day_interval, hour_interval)
    g = raster._grid
    z = _surface(raster, z_factor)
    points, columns, shape = _locations(in_points_feature_or_table, g.srid, "PointsSolarRadiation")
    xs = np.array([p[0] for p in points], float)
    ys = np.array([p[1] for p in points], float)
    rows = (g.top - ys) / g.height - 0.5
    cols = (xs - g.left) / g.width - 0.5
    on = (rows >= -0.5) & (rows <= g.rows - 0.5) & (cols >= -0.5) & (cols <= g.columns - 0.5)
    ground = _bilinear(z, rows, cols)
    horizon = _ins.horizons(z, g.width, g.height, rows, cols, ground + float(height_offset or 0), directions)
    s, a = _slope_aspect_horn(raster, z_factor)
    ri = np.clip(np.floor(rows + 0.5).astype(int), 0, g.rows - 1)
    ci = np.clip(np.floor(cols + 0.5).astype(int), 0, g.columns - 1)
    slope = np.array([p[2] if p[2] is not None else (0.0 if kind == "FLAT_SURFACE" else s[r, c])
                      for p, r, c in zip(points, ri, ci)], float)
    aspect = np.array([p[3] if p[3] is not None else (-1.0 if kind == "FLAT_SURFACE" else a[r, c])
                       for p, r, c in zip(points, ri, ci)], float)
    on &= ~raster._mask[ri, ci]
    direct, diffuse, duration = _ins.insolation(horizon, ground, slope, aspect, _ins.Sky(size), sectors, len(labels),
                                                zd, ad, diffuse_model_type, diffuse_proportion, transmittivity)
    every = _flag(each_interval, "INTERVAL")
    names = [c.name for c in columns]

    def write(path, values):
        if not every:
            values = values.sum(axis=1, keepdims=True)
        fields = [f"T{k}" for k in range(values.shape[1])]
        out = _out(path)
        _gpkg.create(out, shape, g.srid, [*columns, *[_gpkg.Column(f, "Double") for f in fields]])
        from ._geometry import PointGeometry, Point
        from ._reference import SpatialReference
        reference = SpatialReference(g.srid) if g.srid else None
        with da.InsertCursor(out, [*names, *fields, "SHAPE@"]) as cursor:
            for k, p in enumerate(points):
                cells = [float(v) if on[k] else None for v in values[k]]
                cursor.insertRow((*p[4], *cells, PointGeometry(Point(p[0], p[1]), reference)))
        return out

    for path, values in ((out_direct_radiation_features, direct), (out_diffuse_radiation_features, diffuse),
                         (out_direct_duration_features, duration)):
        if _given(path):
            write(path, values)
    return Result(write(out_global_radiation_features, direct + diffuse))


# ---- Solar Radiation Graphics


@equivalent(*LEGACY)
def SolarRadiationGraphics(in_surface_raster, in_points_feature_or_table=None, sky_size=200, height_offset=0,
                           calculation_directions=32, latitude=None, time_configuration=None, day_interval=14,
                           hour_interval=0.5, out_sunmap_raster=None, zenith_divisions=8, azimuth_divisions=8,
                           out_skymap_raster=None):
    """The hemispherical viewshed of each location (one band per location; the raster's centre without locations),
    and optionally the sun map and sky map, each ``sky_size`` cells a side with a cell size of 1 and the lower left at
    (0, 0), as the page says.

    INFERRED: the viewshed is 1 where the sky is visible and 0 where obstructed, NoData outside the hemisphere; the
    sun map numbers its sectors from 1 in time order, 0 where no sector is, with two bands when the period falls on
    both sides of the solstices; the sky map numbers its sectors from 1, ring by ring from the zenith and clockwise
    from north within a ring."""
    from ._raster import Raster, _Grid, _refuse_environment, as_raster
    _refuse_environment("arcpy.sa.SolarRadiationGraphics")
    raster = as_raster(in_surface_raster, "SolarRadiationGraphics")
    size, directions, zd, ad = _check("SolarRadiationGraphics", sky_size, calculation_directions, zenith_divisions,
                                      azimuth_divisions)
    g = raster._grid
    z = _surface(raster, 1)
    if _given(in_points_feature_or_table):
        points, _, _ = _locations(in_points_feature_or_table, g.srid, "SolarRadiationGraphics")
        xs = np.array([p[0] for p in points], float)
        ys = np.array([p[1] for p in points], float)
        rows, cols = (g.top - ys) / g.height - 0.5, (xs - g.left) / g.width - 0.5
    else:
        rows, cols = np.array([(g.rows - 1) / 2.0]), np.array([(g.columns - 1) / 2.0])
    ground = _bilinear(z, rows, cols) + float(height_offset or 0)
    horizon = _ins.horizons(z, g.width, g.height, rows, cols, ground, directions)
    sky = _ins.Sky(size)
    seen = sky.visible(horizon)
    grid = _Grid(0, size, 1, 1, size, size, 0)
    outside = ~sky.inside
    arrays = []
    for k in range(rows.size):
        cell = np.zeros((size, size), np.int32)
        cell[sky.rows, sky.cols] = seen[k].astype(np.int32)
        arrays.append(cell)
    if _given(out_sunmap_raster):
        lat = _latitude(raster, latitude)
        sectors, _ = _ins.sun_sectors(_config(time_configuration), lat, day_interval, hour_interval)
        maps = _ins.sun_map(sky, sectors)
        (Raster._of(maps[0], outside.copy(), grid) if len(maps) == 1 else
         Raster._of_bands(maps, [outside.copy() for _ in maps], grid)).save(out_sunmap_raster)
    if _given(out_skymap_raster):
        ids = np.zeros((size, size), np.int32)
        ids[sky.rows, sky.cols] = sky.sky_sectors(zd, ad) + 1
        Raster._of(ids, outside.copy(), grid).save(out_skymap_raster)
    if len(arrays) == 1:
        return Raster._of(arrays[0], outside.copy(), grid)
    return Raster._of_bands(arrays, [outside.copy() for _ in arrays], grid)


# ---- Raster Solar Radiation


# The page's mean hexagon areas of the H3 sun map grid levels on Earth, km².
_LEVEL_AREA = {5: 252.9, 6: 36.1, 7: 5.16}
_STEP = _dt.timedelta(minutes=30)


def _instant(value, tool):
    from . import _solar
    found = _solar.parse_time(value, tool)
    if isinstance(found, _dt.time):
        raise ValueError(f"arcpy.sa.{tool}: '{value}' has no date.")
    return found


def _interval_length(unit, interval, tool):
    unit = str(unit or "DAY").upper()
    defaults = {"MINUTE": 60, "HOUR": 4, "DAY": 14, "WEEK": 2}
    if unit not in defaults:
        raise ValueError(f"{tool}: interval_unit '{unit}' is not MINUTE, HOUR, DAY or WEEK.")
    count = int(interval) if _given(interval) else defaults[unit]
    minutes = count * {"MINUTE": 1, "HOUR": 60, "DAY": 1440, "WEEK": 10080}[unit]
    if minutes <= 0 or minutes % 30:
        raise ValueError(f"{tool}: the time interval must be a positive multiple of 30 minutes, as the page says.")
    return _dt.timedelta(minutes=minutes)


@equivalent(*MODERN)
def RasterSolarRadiation(in_surface_raster, start_date_time, end_date_time, in_analysis_mask=None, in_slope_raster=None,
                         in_aspect_raster=None, out_direct_radiation_raster=None, out_diffuse_radiation_raster=None,
                         out_duration_raster=None, time_zone="UTC", adjust_DST="NOT_ADJUSTED_FOR_DST",
                         use_time_interval="NO_INTERVAL", interval_unit="DAY", interval=None, neighborhood_distance=None,
                         use_adaptive_neighborhood="FIXED_NEIGHBORHOOD", diffuse_model_type="UNIFORM_SKY",
                         diffuse_proportion=0.3, transmittivity=0.5, analysis_target_device="GPU_THEN_CPU",
                         sunmap_grid_level=None):
    """Total insolation in kilowatt hours per square metre of each cell from ``start_date_time`` to ``end_date_time``;
    with ``INTERVAL`` a multidimensional raster of ``global_solar_radiation`` over ``StdTime``, each slice the interval
    ending at its time, as *How Raster Solar Radiation works* describes.

    The sun is placed every half hour (the page's minimum duration) at the middle of the half hour, by longitude,
    latitude and UTC, once for each block of cells; slope and aspect come from Surface Parameters' fit over
    ``neighborhood_distance``, as the page says. The calculation runs on the CPU whatever ``analysis_target_device``
    asks, which changes only speed. A surface on the Moon is refused: placing the sun there needs NASA's SPICE kernels,
    which Pro installs and grapy does not have.

    INFERRED (ADR-243 §5.3): the sun map grid's H3 hexagons are square blocks of the level's mean area from the page's
    table; horizons are traced in 32 directions; the sky map has 8 zenith and 8 azimuth divisions on a 200-cell sky,
    the defaults of the Fu and Rich tools; a cell where Surface Parameters cannot fit takes Horn's slope and aspect."""
    from . import _mdim, _solar, _surfaceparams
    from ._raster import Raster, _refuse_environment, as_raster
    tool = "RasterSolarRadiation"
    _refuse_environment(f"arcpy.sa.{tool}")
    raster = as_raster(in_surface_raster, tool)
    g = raster._grid
    if not g.srid:
        raise ValueError(f"{tool}: the input surface has no coordinate system, and the sun's position needs one.")
    from pyproj import CRS
    crs = CRS.from_epsg(g.srid)
    if crs.is_geographic:
        raise ValueError(f"{tool}: the surface must be in a projected coordinate system, as the tool's usage says.")
    if crs.ellipsoid is not None and abs(crs.ellipsoid.semi_major_metre - 1737400.0) < 1000:
        _refuse(tool, "in_surface_raster", in_surface_raster, "a surface on the Moon needs the lunar ephemeris of "
                "NASA's SPICE kernels, which grapy does not have")
    device = str(analysis_target_device or "GPU_THEN_CPU").upper()
    if device not in ("GPU_THEN_CPU", "CPU_ONLY", "GPU_ONLY"):
        raise ValueError(f"{tool}: analysis_target_device '{analysis_target_device}' is not GPU_THEN_CPU, CPU_ONLY "
                         "or GPU_ONLY.")
    if device == "GPU_ONLY":
        _refuse(tool, "analysis_target_device", analysis_target_device, "grapy computes on the CPU only")
    to_metres = float(crs.axis_info[0].unit_conversion_factor) if crs.axis_info else 1.0
    level = int(sunmap_grid_level) if _given(sunmap_grid_level) else (6 if g.width * to_metres <= 4 else 5)
    if level not in _LEVEL_AREA:
        raise ValueError(f"{tool}: sunmap_grid_level must be from 5 to 7 on Earth.")
    zone = _solar.offset(time_zone, 0.0)
    dst = _flag(adjust_DST, "ADJUSTED_FOR_DST")
    start = _solar.to_utc(_instant(start_date_time, tool), zone, dst)
    end = _solar.to_utc(_instant(end_date_time, tool), zone, dst)
    if end < start:
        raise ValueError(f"{tool}: the end date and time must not be before the start.")
    if end - start > _dt.timedelta(days=366):
        raise ValueError(f"{tool}: the period must not be longer than one year.")
    every = _flag(use_time_interval, "INTERVAL")
    if every:
        length = _interval_length(interval_unit, interval, tool)
        slices = max(1, math.ceil((end - start) / length))
        end = start + slices * length
    else:
        length, slices = None, 1
    steps = max(1, math.ceil((end - start) / _STEP))

    z = _surface(raster, 1)
    valid = ~raster._mask
    if _given(in_analysis_mask):
        valid &= _mask_of(in_analysis_mask, g, tool)
    if _given(in_slope_raster) or _given(in_aspect_raster):
        if not (_given(in_slope_raster) and _given(in_aspect_raster)):
            horn_s, horn_a = _slope_aspect_horn(raster, 1)
        slope = (as_raster(in_slope_raster, tool)._array.astype(float) if _given(in_slope_raster) else horn_s)
        aspect = (as_raster(in_aspect_raster, tool)._array.astype(float) if _given(in_aspect_raster) else horn_a)
    else:
        adaptive = _flag(use_adaptive_neighborhood, "ADAPTIVE_NEIGHBORHOOD")
        distance = _linear(neighborhood_distance, g.width, to_metres)
        slope, aspect = _surfaceparams.slope_aspect(np.where(raster._mask, 0.0, z), raster._mask, g.width, distance,
                                                    adaptive)
        horn_s, horn_a = _slope_aspect_horn(raster, 1)
        slope = np.where(np.isfinite(slope), slope, horn_s)
        aspect = np.where(np.isfinite(aspect), aspect, horn_a)
    rows, cols = np.nonzero(valid)
    horizon = _ins.horizons(z, g.width, g.height, rows, cols, z[rows, cols], 32)
    sky = _ins.Sky(200)
    seen = sky.visible(horizon).astype(float)
    ids = sky.sky_sectors(8, 8)
    counts = np.bincount(ids, minlength=64).astype(float)
    sky_gap = np.stack([seen[:, ids == k].sum(axis=1) / max(counts[k], 1) for k in range(64)], axis=1)
    ring, piece = np.arange(64) // 8, np.arange(64) % 8
    cos_sky = _ins.cos_incidence(((ring + 0.5) * 11.25)[None, :], ((piece + 0.5) * 45.0)[None, :],
                                 slope[rows, cols][:, None], aspect[rows, cols][:, None])
    diffuse_share = (sky_gap * _ins.weights(8, 8, diffuse_model_type)[ring][None, :] * cos_sky).sum(axis=1)
    pdif, beta = float(diffuse_proportion), float(transmittivity)
    if not 0 <= pdif < 1:
        raise ValueError(f"{tool}: diffuse_proportion must be from 0 to less than 1.")
    if not 0 <= beta <= 1:
        raise ValueError(f"{tool}: transmittivity must be from 0 to 1.")

    side = math.sqrt(_LEVEL_AREA[level]) * 1000.0 / to_metres
    xs = g.left + (cols + 0.5) * g.width
    ys = g.top - (rows + 0.5) * g.height
    block = np.floor((xs - g.left) / side).astype(np.int64) * 1_000_003 + np.floor((g.top - ys) / side).astype(np.int64)
    direct = np.zeros((rows.size, slices))
    normal_sum = np.zeros((rows.size, slices))
    duration = np.zeros((rows.size, slices))
    hours = _STEP.total_seconds() / 3600.0
    for key in np.unique(block):
        members = np.nonzero(block == key)[0]
        lon, lat = _solar.place(xs[members].mean(), ys[members].mean(), g.srid)
        for k in range(steps):
            at = start + k * _STEP
            band = min(int((at - start) / length), slices - 1) if every else 0
            azimuth, elevation = _solar.position(at + _STEP / 2, lon, lat)
            if elevation <= 0:
                continue
            zen = 90.0 - elevation
            m = _ins.optical_path(zen, z[rows[members], cols[members]])
            normal = _ins.S_CONST * beta ** m * hours
            visible = elevation > _ins.horizon_at(horizon[members], azimuth)
            cos_in = _ins.cos_incidence(zen, azimuth, slope[rows[members], cols[members]],
                                        aspect[rows[members], cols[members]])
            direct[members, band] += normal * visible * cos_in
            duration[members, band] += hours * visible
            normal_sum[members, band] += normal
    diffuse = normal_sum / (1.0 - pdif) * pdif * diffuse_share[:, None]

    def cells(values):
        out = np.zeros((slices,) + raster._mask.shape, np.float32)
        out[:, rows, cols] = values.T
        return out

    mask = ~valid
    if not every:
        def single(values, scale):
            return Raster._of(cells(values * scale)[0], mask.copy(), g)
        for path, values, scale in ((out_direct_radiation_raster, direct, 1e-3),
                                    (out_diffuse_radiation_raster, diffuse, 1e-3), (out_duration_raster, duration, 1.0)):
            if _given(path):
                single(values, scale).save(path)
        return single(direct + diffuse, 1e-3)
    times = [np.datetime64(start + (k + 1) * length, "s") for k in range(slices)]

    def cube(name, values, scale):
        data = cells(values * scale)[:, None]
        variable = _mdim.Variable(name, ["StdTime"], [{"StdTime": t} for t in times], data,
                                  np.broadcast_to(mask, data.shape).copy())
        return _mdim.raster_of(_mdim.Cube(g, [variable]))
    for path, name, values, scale in ((out_direct_radiation_raster, "direct_solar_radiation", direct, 1e-3),
                                      (out_diffuse_radiation_raster, "diffuse_solar_radiation", diffuse, 1e-3),
                                      (out_duration_raster, "direct_duration", duration, 1.0)):
        if _given(path):
            cube(name, values, scale).save(path)
    return cube("global_solar_radiation", direct + diffuse, 1e-3)


def _linear(value, default, to_metres):
    """A linear unit ("30 Meters") or a number in the raster's units."""
    if not _given(value):
        return default
    if isinstance(value, (int, float)):
        return float(value)
    parts = str(value).split()
    number = float(parts[0])
    if len(parts) == 1:
        return number
    factors = {"meters": 1.0, "meter": 1.0, "kilometers": 1000.0, "feet": 0.3048, "foot": 0.3048,
               "feetint": 0.3048, "usfeet": 1200 / 3937, "centimeters": 0.01, "millimeters": 0.001,
               "yards": 0.9144, "miles": 1609.344}
    unit = parts[1].lower()
    if unit not in factors:
        raise ValueError(f"'{value}' is not a linear unit grapy reads.")
    return number * factors[unit] / to_metres


def _mask_of(value, grid, tool):
    """Where an analysis mask allows: a raster's cells with data, or the cell centres inside polygon features."""
    from ._raster import Raster, as_raster
    if isinstance(value, Raster) or str(value).lower().endswith((".tif", ".tiff")):
        mask = as_raster(value, tool)
        if not mask._grid.same(grid):
            raise ValueError(f"{tool}: the analysis mask raster must lie on the surface's grid (resampling is not "
                             "built).")
        return ~mask._mask
    import shapely
    from .analysis import _read
    shapes = [f[2] for f in _read(value, grid.srid)]
    xs, ys = grid.centres()
    gx, gy = np.meshgrid(xs, ys)
    inside = np.zeros(gx.shape, bool)
    for shape in shapes:
        inside |= shapely.contains_xy(shape, gx, gy)
    return inside
