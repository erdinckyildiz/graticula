"""Solar radiation: the hemispherical viewshed, sun map and sky map model of Rich et al. (1994) and Fu and Rich (2000,
2002), as Esri's *How solar radiation is calculated* and *Modeling solar radiation* write it — ADR-243.

The pages give the model's equations as text (1–9 of *How solar radiation is calculated*): direct insolation per sun
map sector, ``S_const · β^m(θ) · SunDur · SunGap · cos(AngIn)``; the relative optical path ``m(θ)`` with its elevation
term; the angle of incidence; diffuse insolation per sky map sector, ``R_glb · P_dif · Dur · SkyGap · Weight ·
cos(AngIn)``, with the uniform and standard overcast weights. *Modeling solar radiation* writes the discretisation in
words: horizon angles traced in ``calculation_directions`` azimuths and interpolated between them, then converted to an
upward-looking hemispherical raster of ``sky_size`` cells a side whose radius is proportional to the zenith angle; a
sun map of sectors, one per day interval and hour interval of the sun's track; a sky map of ``zenith_divisions`` ×
``azimuth_divisions`` sectors; and each sector's gap fraction, its unobstructed cells over all its cells. That is
what this module does, with the details the pages leave open recorded below and in ADR-243 §5 as INFERRED:

* the sun's position for the Area, Points and Graphics tools is in local solar time, the latitude alone placing it
  (the pages: *"the program accounts for equation of time"* when converting local time to solar time), with the
  declination of Spencer (1971, *Search* 2(5), 172) and the standard spherical-astronomy hour angle;
* a sun map sector's centroid is the sun at the middle of the sunlit part of its hour slot on the middle day of its
  day interval, and its duration that sunlit part times the days (*"for partial sectors (near the horizon), the
  duration is calculated using spherical geometry"*: the sunrise hour angle ``cos h₀ = −tan φ tan δ``);
* a sector's cells are those its track crosses, sampled each day of its interval and nine times across its hours, and
  its gap fraction is taken over its own cells, so two sectors whose tracks share cells do not take each other's;
* ``m(θ)`` is equation 3 below 80°, and at and beyond 80°, where the page says only that *"refraction is
  important"*, the elevation term of equation 3 times Kasten and Young's (1989, *Applied Optics* 28(22)) air mass;
* equation 6's sum is weighted by each sector's duration, so that ``R_glb · Dur`` is the global normal energy of the
  period: the page writes the sum without durations and multiplies by ``Dur`` in equation 5, and the two readings agree
  only for sectors of equal duration;
* the sky map's zenith divisions are equal in zenith angle and its azimuth divisions equal in azimuth, from north;
* a negative cosine of incidence (the sun behind the surface) contributes nothing.
"""
from __future__ import annotations

import datetime as _dt
import math

import numpy as np
from ._runtime import _is_given  # noqa: E402 — a Raster is never compared with ==

S_CONST = 1367.0  # W/m², the page's World Radiation Center solar constant

FU_RICH = ("the hemispherical viewshed, sun map and sky map model of Fu and Rich, with the direct and diffuse equations "
           "of How solar radiation is calculated")
FU_RICH_SOURCE = ("Fu and Rich 2002, Computers and Electronics in Agriculture 37, 25–35; Rich et al. 1994, ASPRS "
                  "Technical Papers, 524–529; Fu 2000, Ph.D. thesis, University of Kansas; Spencer 1971, Search 2(5); "
                  "Kasten and Young 1989, Applied Optics 28(22)")
NOAA_SOURCE = ("Fu and Rich 2002, Computers and Electronics in Agriculture 37; Rich et al. 1994, ASPRS Technical Papers; "
               "the sun's position by NOAA's Solar Calculator (Meeus 1998, Astronomical Algorithms, ch. 25 and 28)")


# ---- the time configurations (arcpy.sa.TimeWithinDay … TimeWholeYear)


def _current_year():
    return _dt.date.today().year


class _Time:
    _fields = ()

    def __repr__(self):
        return f"{type(self).__name__}({', '.join(repr(getattr(self, f)) for f in self._fields)})"

    __str__ = __repr__


class TimeWithinDay(_Time):
    """arcpy.sa.TimeWithinDay: a period within one day, in solar time (decimal hours, 0–24)."""

    _fields = ("day", "startTime", "endTime")

    def __init__(self, day=183, startTime=0, endTime=24):
        self.day, self.startTime, self.endTime = int(day), float(startTime), float(endTime)


class TimeMultipleDays(_Time):
    """arcpy.sa.TimeMultipleDays: the days from ``startDay`` 0:00 to ``endDay`` 0:00, into the next year when
    ``endDay`` is the smaller, as the class page's examples read ([1, 2] is January 1)."""

    _fields = ("year", "startDay", "endDay")

    def __init__(self, year=None, startDay=5, endDay=160):
        self.year = int(year) if _is_given(year) else _current_year()
        self.startDay, self.endDay = int(startDay), int(endDay)


class TimeSpecialDays(_Time):
    """arcpy.sa.TimeSpecialDays: the summer solstice, the equinox and the winter solstice."""


class TimeWholeYear(_Time):
    """arcpy.sa.TimeWholeYear: a year in calendar months."""

    _fields = ("year",)

    def __init__(self, year=None):
        self.year = int(year) if _is_given(year) else _current_year()


def _leap(year):
    return year % 4 == 0 and (year % 100 != 0 or year % 400 == 0)


# INFERRED (ADR-243 §5.2): the special days are the day numbers of 21 June, 21 March and 21 December in a common year;
# the page names the days, not their numbers.
SPECIAL_DAYS = (("summer solstice", 172), ("equinox", 80), ("winter solstice", 355))


def day_intervals(config, day_interval):
    """The period as [(label, [day numbers …])], one entry per output interval, and whether hour slots are the
    intervals (within a day). Day numbers run past the year's length into the next year."""
    if isinstance(config, TimeWithinDay):
        if not 1 <= config.day <= 366:
            raise ValueError("TimeWithinDay: day must be a Julian day from 1 to 366.")
        if not 0 <= config.startTime <= config.endTime <= 24:
            raise ValueError("TimeWithinDay: startTime must not be after endTime, both from 0 to 24.")
        return [(f"day {config.day}", [config.day])], True
    if isinstance(config, TimeSpecialDays):
        return [(label, [day]) for label, day in SPECIAL_DAYS], False
    if isinstance(config, TimeWholeYear):
        out, first = [], 1
        for month in range(1, 13):
            length = (_dt.date(config.year + (month == 12), month % 12 + 1, 1) - _dt.date(config.year, month, 1)).days
            out.append((_dt.date(config.year, month, 1).strftime("%B"), list(range(first, first + length))))
            first += length
        return out, False
    if isinstance(config, TimeMultipleDays):
        length = 366 if _leap(config.year) else 365
        if config.startDay == config.endDay:
            raise ValueError("TimeMultipleDays: startDay and endDay cannot be equal.")
        end = config.endDay if config.endDay > config.startDay else config.endDay + length
        days = list(range(config.startDay, end))
        step = max(1, int(day_interval))
        return [(f"days {days[i]}–{days[min(i + step, len(days)) - 1]}", days[i:i + step])
                for i in range(0, len(days), step)], False
    raise TypeError(f"time_configuration must be a TimeWithinDay, TimeMultipleDays, TimeSpecialDays or TimeWholeYear "
                    f"object, not {type(config).__name__}.")


# ---- the sun in solar time


def declination(day):
    """The sun's declination in degrees on a day of the year (Spencer 1971)."""
    g = 2 * math.pi * (((day - 1) % 365.0)) / 365.0
    return math.degrees(0.006918 - 0.399912 * math.cos(g) + 0.070257 * math.sin(g) - 0.006758 * math.cos(2 * g)
                        + 0.000907 * math.sin(2 * g) - 0.002697 * math.cos(3 * g) + 0.00148 * math.sin(3 * g))


def sun_at(latitude, dec, hour):
    """(zenith, azimuth clockwise from north) in degrees of the sun at a solar hour."""
    lat, d, h = math.radians(latitude), math.radians(dec), math.radians(15.0 * (hour - 12.0))
    cos_z = math.sin(lat) * math.sin(d) + math.cos(lat) * math.cos(d) * math.cos(h)
    zenith = math.degrees(math.acos(max(-1.0, min(1.0, cos_z))))
    azimuth = (math.degrees(math.atan2(math.sin(h), math.cos(h) * math.sin(lat) - math.tan(d) * math.cos(lat)))
               + 180.0) % 360.0
    return zenith, azimuth


def daylight(latitude, dec):
    """(sunrise, sunset) in solar hours: the sunrise hour angle cos h₀ = −tan φ tan δ."""
    x = -math.tan(math.radians(latitude)) * math.tan(math.radians(dec))
    if x >= 1:
        return 12.0, 12.0  # polar night
    if x <= -1:
        return 0.0, 24.0  # midnight sun
    h0 = math.degrees(math.acos(x)) / 15.0
    return 12.0 - h0, 12.0 + h0


class Sector:
    """One sun map sector: its centroid, its duration in hours, the output interval it belongs to and its track's
    samples (zenith, azimuth) for rasterising."""

    __slots__ = ("zenith", "azimuth", "duration", "band", "samples", "day")

    def __init__(self, zenith, azimuth, duration, band, samples, day):
        self.zenith, self.azimuth, self.duration, self.band = zenith, azimuth, duration, band
        self.samples, self.day = samples, day


def sun_sectors(config, latitude, day_interval=14, hour_interval=0.5):
    """The sun map's sectors for a time configuration, and the labels of its output intervals."""
    hour_interval = float(hour_interval)
    if hour_interval <= 0:
        raise ValueError("hour_interval must be greater than 0.")
    intervals, by_hour = day_intervals(config, day_interval)
    sectors, labels = [], []
    if by_hour:
        day = config.day
        dec = declination(day)
        rise, set_ = daylight(latitude, dec)
        if config.startTime == config.endTime:
            # INFERRED: an instantaneous configuration answers irradiance (W/m²), a sector of one hour's weight at that
            # moment, when the sun is up.
            zen, az = sun_at(latitude, dec, config.startTime)
            if zen < 90:
                sectors.append(Sector(zen, az, 1.0, 0, [(zen, az)], day))
            return sectors, [f"{config.startTime:g} h"]
        # The page: "If start time = 0, then t0 will be sun rise and t1 at the next time increment after sun rise."
        begin, end = max(config.startTime, rise), min(config.endTime, set_)
        band, t = 0, begin
        while t < end - 1e-9:
            a, b = t, min(t + hour_interval, end)
            sectors.append(_sector(latitude, [day], a, b, band))
            labels.append(f"{a:g}–{b:g} h")
            band, t = band + 1, t + hour_interval
        return [s for s in sectors if s is not None], labels or [f"day {day}"]
    slots = []
    t = 0.0
    while t < 24 - 1e-9:
        slots.append((t, min(t + hour_interval, 24.0)))
        t += hour_interval
    for band, (label, days) in enumerate(intervals):
        labels.append(label)
        for a, b in slots:
            found = _sector(latitude, days, a, b, band)
            if found is not None:
                sectors.append(found)
    return sectors, labels


def _sector(latitude, days, a, b, band):
    """The sector of an hour slot [a, b) over some days, or None when the sun is down throughout."""
    middle = days[len(days) // 2] if len(days) % 2 else (days[len(days) // 2 - 1] + days[len(days) // 2]) / 2
    dec = declination(middle)
    rise, set_ = daylight(latitude, dec)
    lo, hi = max(a, rise), min(b, set_)
    if hi <= lo:
        return None
    zen, az = sun_at(latitude, dec, (lo + hi) / 2)
    if zen >= 90:
        return None
    samples = []
    for day in days:
        d = declination(day)
        r, s = daylight(latitude, d)
        x, y = max(a, r), min(b, s)
        if y <= x:
            continue
        for k in range(9):
            z, w = sun_at(latitude, d, x + (y - x) * k / 8)
            if z < 90:
                samples.append((z, w))
    return Sector(zen, az, (hi - lo) * len(days), band, samples or [(zen, az)], middle)


# ---- the atmosphere


def optical_path(zenith_deg, elevation_m):
    """m(θ): equation 3 below 80°; at and beyond, the elevation term times Kasten and Young's (1989) air mass."""
    zenith_deg = np.asarray(zenith_deg, float)
    elevation_m = np.asarray(elevation_m, float)
    term = np.exp(-0.000118 * elevation_m - 1.638e-9 * elevation_m ** 2)
    low = 1.0 / np.cos(np.radians(np.minimum(zenith_deg, 80.0)))
    ky = 1.0 / (np.cos(np.radians(zenith_deg)) + 0.50572 * np.power(np.maximum(96.07995 - zenith_deg, 1e-6), -1.6364))
    return term * np.where(zenith_deg < 80.0, low, ky)


def cos_incidence(zenith, azimuth, slope, aspect):
    """cos(AngIn) by equation 4, at least 0: zenith and azimuth of a direction against a surface's slope (its zenith
    angle) and aspect (its azimuth), degrees; a flat surface's aspect of −1 is harmless since its slope is 0."""
    z, a = np.radians(zenith), np.radians(azimuth)
    gz, ga = np.radians(slope), np.radians(aspect)
    return np.maximum(np.cos(z) * np.cos(gz) + np.sin(z) * np.sin(gz) * np.cos(a - ga), 0.0)


def weights(zenith_divisions, azimuth_divisions, model):
    """Each sky sector's share of the diffuse radiation, equations 7 (uniform) and 8 (standard overcast), by ring."""
    t1 = np.radians(np.arange(zenith_divisions) * 90.0 / zenith_divisions)
    t2 = np.radians((np.arange(zenith_divisions) + 1) * 90.0 / zenith_divisions)
    if str(model or "UNIFORM_SKY").upper() == "STANDARD_OVERCAST_SKY":
        w = np.abs(2 * np.cos(t2) + np.cos(2 * t2) - 2 * np.cos(t1) - np.cos(2 * t1)) / (4 * azimuth_divisions)
    else:
        w = np.abs(np.cos(t2) - np.cos(t1)) / azimuth_divisions
    return w


# ---- the hemispherical rasters


class Sky:
    """The upward-looking hemispherical raster of ``size`` cells a side: zenith at the centre, the horizon at the edge,
    the radius proportional to the zenith angle, north up and east to the right (INFERRED: the page does not say whether
    the projection is mirrored, as a photograph looking up would be)."""

    def __init__(self, size):
        size = int(size)
        if not 1 < size <= 10000:
            raise ValueError("sky_size must be from 2 to 10,000 cells.")
        self.size = size
        c = size / 2.0
        j, i = np.meshgrid(np.arange(size), np.arange(size))
        x, y = j + 0.5 - c, c - (i + 0.5)
        r = np.hypot(x, y)
        self.inside = r <= c
        self.rows, self.cols = i[self.inside], j[self.inside]
        self.zenith = r[self.inside] / c * 90.0
        self.azimuth = np.degrees(np.arctan2(x[self.inside], y[self.inside])) % 360.0
        self.index = -np.ones((size, size), np.int64)
        self.index[self.inside] = np.arange(self.inside.sum())

    def cells_of(self, samples):
        """The sky cells a list of (zenith, azimuth) directions fall in."""
        z = np.array([s[0] for s in samples], float)
        a = np.radians([s[1] for s in samples])
        c = self.size / 2.0
        r = z / 90.0 * c
        j = np.clip(np.floor(c + r * np.sin(a)).astype(int), 0, self.size - 1)
        i = np.clip(np.floor(c - r * np.cos(a)).astype(int), 0, self.size - 1)
        found = self.index[i, j]
        return np.unique(found[found >= 0])

    def sky_sectors(self, zenith_divisions, azimuth_divisions):
        """Each sky cell's sector: ring from the zenith times azimuth divisions plus the slice from north."""
        ring = np.minimum((self.zenith / (90.0 / zenith_divisions)).astype(int), zenith_divisions - 1)
        piece = (self.azimuth / (360.0 / azimuth_divisions)).astype(int) % azimuth_divisions
        return ring * azimuth_divisions + piece

    def visible(self, horizon):
        """Which sky cells each location sees: (locations, cells), from horizon angles (locations, directions)."""
        n = horizon.shape[1]
        d = self.azimuth / (360.0 / n)
        i0 = np.floor(d).astype(int) % n
        i1 = (i0 + 1) % n
        w = d - np.floor(d)
        interpolated = horizon[:, i0] * (1 - w) + horizon[:, i1] * w
        return (90.0 - self.zenith)[None, :] > interpolated


def horizon_at(horizon, azimuth):
    """Horizon angles (locations, directions) linearly interpolated at one azimuth, degrees."""
    n = horizon.shape[1]
    d = (azimuth % 360.0) / (360.0 / n)
    i0 = int(math.floor(d)) % n
    w = d - math.floor(d)
    return horizon[:, i0] * (1 - w) + horizon[:, (i0 + 1) % n] * w


def horizons(z, cell_w, cell_h, rows, cols, base, directions):
    """Horizon angles in degrees (never below 0) for locations at fractional positions in cell-centre index units (row
    0, column 0 is the top-left cell's centre) seen from heights ``base``, traced over
    the surface ``z`` (NaN where NoData) in ``directions`` azimuths from north, a step of one cell at a time, to the
    raster's edge, sampling bilinearly."""
    from scipy.ndimage import map_coordinates
    rows, cols = np.asarray(rows, float), np.asarray(cols, float)
    base = np.asarray(base, float)
    step = min(cell_w, cell_h)
    reach = int(math.ceil(math.hypot(z.shape[0] * cell_h, z.shape[1] * cell_w) / step)) + 1
    filled = np.where(np.isnan(z), np.nanmin(z) if np.isfinite(z).any() else 0.0, z)
    bad = np.isnan(z).astype(float)
    out = np.zeros((rows.size, int(directions)))
    for k in range(int(directions)):
        a = math.radians(k * 360.0 / directions)
        dr, dc = -math.cos(a) * step / cell_h, math.sin(a) * step / cell_w
        best = np.zeros(rows.size)
        for s in range(1, reach + 1):
            r, c = rows + dr * s, cols + dc * s
            inside = (r > -0.5) & (r < z.shape[0] - 0.5) & (c > -0.5) & (c < z.shape[1] - 0.5)
            if not inside.any():
                break
            coords = [np.clip(r, 0, z.shape[0] - 1), np.clip(c, 0, z.shape[1] - 1)]
            height = map_coordinates(filled, coords, order=1, mode="nearest")
            nodata = map_coordinates(bad, coords, order=1, mode="nearest") > 0
            angle = np.degrees(np.arctan2(height - base, s * step))
            angle = np.where(inside & ~nodata, angle, 0.0)
            best = np.maximum(best, angle)
        out[:, k] = best
    return out


# ---- the model at many locations


def insolation(horizon, elevation, slope, aspect, sky, sectors, bands, zenith_divisions, azimuth_divisions,
               diffuse_model, diffuse_proportion, transmittivity):
    """(direct, diffuse, duration), each (locations, bands), in watt hours per square metre and hours, of locations
    with horizon angles (locations, directions), elevations in metres, and slopes and aspects in degrees."""
    from scipy import sparse
    pdif, beta = float(diffuse_proportion), float(transmittivity)
    if not 0 <= pdif < 1:
        raise ValueError("diffuse_proportion must be from 0 to less than 1: equation 6 divides by 1 − P_dif.")
    if not 0 <= beta <= 1:
        raise ValueError("transmittivity must be from 0 to 1.")
    n = horizon.shape[0]
    direct, diffuse, duration = np.zeros((n, bands)), np.zeros((n, bands)), np.zeros((n, bands))
    if not sectors:
        return direct, diffuse, duration
    cells = sky.zenith.size
    # sun sectors: each one's own cells, as a (cells × sectors) averaging matrix
    rows_, cols_, vals = [], [], []
    for k, s in enumerate(sectors):
        hit = sky.cells_of(s.samples)
        rows_.extend(hit.tolist())
        cols_.extend([k] * hit.size)
        vals.extend([1.0 / hit.size] * hit.size)
    sun_matrix = sparse.csr_matrix((vals, (rows_, cols_)), shape=(cells, len(sectors)))
    ids = sky.sky_sectors(zenith_divisions, azimuth_divisions)
    total = zenith_divisions * azimuth_divisions
    counts = np.bincount(ids, minlength=total).astype(float)
    sky_matrix = sparse.csr_matrix((1.0 / np.maximum(counts[ids], 1), (np.arange(cells), ids)), shape=(cells, total))
    ring = np.arange(total) // azimuth_divisions
    piece = np.arange(total) % azimuth_divisions
    sky_zenith = (ring + 0.5) * 90.0 / zenith_divisions
    sky_azimuth = (piece + 0.5) * 360.0 / azimuth_divisions
    sky_weight = weights(zenith_divisions, azimuth_divisions, diffuse_model)[ring]
    zen = np.array([s.zenith for s in sectors])
    az = np.array([s.azimuth for s in sectors])
    dur = np.array([s.duration for s in sectors])
    band = np.array([s.band for s in sectors])
    for lo in range(0, n, 64):
        hi = min(lo + 64, n)
        seen = sky.visible(horizon[lo:hi]).astype(float)
        sun_gap = (sparse.csr_matrix(seen) @ sun_matrix).toarray()
        sky_gap = (sparse.csr_matrix(seen) @ sky_matrix).toarray()
        m = optical_path(zen[None, :], elevation[lo:hi, None])
        normal = S_CONST * beta ** m * dur[None, :]  # Wh/m² at normal incidence, unobstructed
        cos_in = cos_incidence(zen[None, :], az[None, :], slope[lo:hi, None], aspect[lo:hi, None])
        cos_sky = cos_incidence(sky_zenith[None, :], sky_azimuth[None, :], slope[lo:hi, None], aspect[lo:hi, None])
        diffuse_share = (sky_gap * sky_weight[None, :] * cos_sky).sum(axis=1)
        for b in range(bands):
            pick = band == b
            if not pick.any():
                continue
            direct[lo:hi, b] = (normal[:, pick] * sun_gap[:, pick] * cos_in[:, pick]).sum(axis=1)
            duration[lo:hi, b] = (dur[None, pick] * sun_gap[:, pick]).sum(axis=1)
            global_normal = normal[:, pick].sum(axis=1) / (1.0 - pdif)  # R_glb · Dur
            diffuse[lo:hi, b] = global_normal * pdif * diffuse_share
    return direct, diffuse, duration


def sun_map(sky, sectors):
    """The sun map rasters: each sector's cells numbered from 1 in time order, one raster for the days from the winter
    to the summer solstice and one for the rest, as the Graphics page describes (INFERRED: a cell two sectors cross
    keeps the later one; 0 where no sector is)."""
    halves = {0: np.zeros((sky.size, sky.size), np.int32), 1: np.zeros((sky.size, sky.size), np.int32)}
    used = set()
    for k, s in enumerate(sectors, 1):
        half = 0 if (s.day >= 355 or s.day < 172) else 1
        hit = sky.cells_of(s.samples)
        halves[half][sky.rows[hit], sky.cols[hit]] = k
        used.add(half)
    return [halves[h] for h in sorted(used)] or [halves[0]]
