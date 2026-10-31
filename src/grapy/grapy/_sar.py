"""grapy's synthetic aperture radar data — ADR-237.

A **SAR dataset** in grapy is a GeoTIFF whose bands are named by polarization (``VV``, ``VH`` …), complex64 for
complex data and float32 for everything else, beside a JSON sidecar ``<path>.sar.json`` that carries what the
radar tools need and a raster does not: the mission and mode, what the values are (complex, amplitude, intensity,
phase, coherence …) and in which scale, the calibration and noise look-up tables, the burst layout, the orbit and the
geolocation tie points (INFERRED, ADR-237 §5.1 — Esri's CRF and its SAR metadata are not published). A dataset in
radar geometry has no reference; its rows are azimuth lines and its columns range samples. After geometric terrain
correction it is an ordinary WGS 84 grid, and the sidecar stays with it.

A **Sentinel-1 SAFE product** is read directly, from its folder, its ``manifest.safe`` or ``<SAFE>/<swath>`` (``IW2``),
by the layout ESA publishes: *Sentinel-1 Product Specification* (S1-RS-MDA-52-7441) for the annotation, calibration
and noise XML, and the measurement GeoTIFFs. Nothing of Esri's is read.

Geometry is range-Doppler on the WGS 84 ellipsoid (Small and Schubert 2008, *Guide to ASAR Geocoding*, ESA
RSL-ASAR-GC-AD): a pixel's azimuth time and slant range place it where the range sphere meets the zero-Doppler plane
of the orbit and the ellipsoid raised to the point's height.
"""
from __future__ import annotations

import datetime as _dt
import glob
import json
import math
import os
import xml.etree.ElementTree as ET

import numpy as np

from ._runtime import ExecuteError, NotBuiltError, env
from ._runtime import _is_given  # noqa: E402 — a Raster is never compared with ==

SIDE = ".sar.json"
FORMAT = "grapy-sar/1"
C = 299792458.0
WGS84_A = 6378137.0
WGS84_F = 1 / 298.257223563
WGS84_B = WGS84_A * (1 - WGS84_F)
_EPOCH = _dt.datetime(2000, 1, 1)


def seconds(text):
    """Seconds since 2000-01-01 of an ISO time as the annotation and orbit files write it (``UTC=`` prefix allowed)."""
    text = str(text).strip()
    if text.startswith("UTC="):
        text = text[4:]
    text = text.rstrip("Z")
    if "." in text:
        head, frac = text.split(".", 1)
        text = head + "." + (frac + "000000")[:6]
    return (_dt.datetime.fromisoformat(text) - _EPOCH).total_seconds()


def iso(value):
    return (_EPOCH + _dt.timedelta(seconds=float(value))).isoformat()


# ---- the dataset


class SarData:
    """Bands (name -> array, in order), the sidecar's metadata, and — in map geometry — the grid's transform and EPSG."""

    def __init__(self, bands, meta, transform=None, srid=0, path=None):
        self.bands = dict(bands)
        self.meta = meta
        self.transform = transform
        self.srid = int(srid or 0)
        self.path = path

    @property
    def names(self):
        return list(self.bands)

    @property
    def shape(self):
        return next(iter(self.bands.values())).shape

    @property
    def radar(self):
        return self.meta.get("geometry", "radar") == "radar"

    def copy_meta(self, **changes):
        meta = json.loads(json.dumps(self.meta))
        meta.update(changes)
        return meta


def _path_of(value):
    path = getattr(value, "catalogPath", None) or os.fspath(value)
    path = str(path)
    if not os.path.isabs(path) and not os.path.exists(path):
        path = os.path.join(env.workspace, path)
    return path


def out_path(value):
    """Where an output dataset goes: a GeoTIFF at the path given, ``.tif`` added to a name without an extension.
    INFERRED: a ``.crf`` name is written as a GeoTIFF under that name, because the Cloud Raster Format is not published."""
    path = _path_of(value)
    if not os.path.splitext(path)[1]:
        path += ".tif"
    return path


def is_safe(path):
    path = path.rstrip("/\\")
    if os.path.basename(path).lower() == "manifest.safe":
        return True
    if path.upper().endswith(".SAFE") and os.path.isdir(path):
        return True
    parent = os.path.dirname(path)
    return parent.upper().endswith(".SAFE") and os.path.isdir(parent) and not os.path.exists(path)


def read(value, tool):
    """The SAR dataset a parameter names: a grapy SAR dataset or a Sentinel-1 SAFE product."""
    path = _path_of(value)
    if is_safe(path):
        return read_safe(path, tool)
    side = path + SIDE
    if not os.path.exists(path):
        raise ExecuteError(f"{tool}: '{path}' does not exist.")
    if not os.path.exists(side):
        raise ExecuteError(f"{tool}: '{path}' is not SAR data: it has no {SIDE} beside it, and is not a Sentinel-1 "
                           "SAFE product.")
    import rasterio
    meta = json.load(open(side, encoding="utf-8"))
    with _quiet(), rasterio.open(path) as source:
        names = [d or f"Band_{k}" for k, d in enumerate(source.descriptions, 1)]
        bands = {}
        for k, name in enumerate(names, 1):
            band = source.read(k)
            bands[name] = band.astype(np.complex64 if np.iscomplexobj(band) else np.float32)
        transform = source.transform if meta.get("geometry") == "map" else None
        srid = source.crs.to_epsg() if source.crs else 0
    return SarData(bands, meta, transform, srid, path)


def write(path, bands, meta, transform=None, srid=0):
    """A SAR dataset: the GeoTIFF of its bands and its sidecar."""
    import rasterio
    from rasterio.transform import from_origin
    path = out_path(path)
    arrays = list(bands.values())
    complex_ = any(np.iscomplexobj(a) for a in arrays)
    dtype = "complex64" if complex_ else "float32"
    rows, columns = arrays[0].shape
    profile = {"driver": "GTiff", "height": rows, "width": columns, "count": len(arrays), "dtype": dtype,
               "transform": transform if transform is not None else from_origin(0, 0, 1, 1)}
    if not complex_:
        profile["nodata"] = float("nan")
    if srid:
        profile["crs"] = f"EPSG:{srid}"
    os.makedirs(os.path.dirname(os.path.abspath(path)), exist_ok=True)
    for stale in (path, path + SIDE, path + ".aux.xml"):
        if os.path.exists(stale):
            os.remove(stale)
    with _quiet(), rasterio.open(path, "w", **profile) as out:
        for k, (name, array) in enumerate(bands.items(), 1):
            out.write(array.astype(dtype), k)
            out.set_band_description(k, name)
    meta = dict(meta)
    meta["format"] = FORMAT
    meta["bands"] = list(bands)
    with open(path + SIDE, "w", encoding="utf-8") as f:
        json.dump(meta, f)
    return path


def _quiet():
    """Radar geometry has no georeference by design; rasterio's warning that it has none is silenced."""
    import contextlib
    import warnings

    @contextlib.contextmanager
    def quiet():
        from rasterio.errors import NotGeoreferencedWarning
        with warnings.catch_warnings():
            warnings.simplefilter("ignore", NotGeoreferencedWarning)
            yield
    return quiet()


def polarizations(sar, value, tool):
    """The bands a ``polarization_bands`` parameter names; the first band when none is named, as documented."""
    if not _is_given(value) or (isinstance(value, list) and not value):
        return [sar.names[0]]
    wanted = value if isinstance(value, (list, tuple)) else str(value).replace(",", ";").split(";")
    out = []
    for w in wanted:
        w = str(w).strip().strip("'\"")
        if not w:
            continue
        match = [n for n in sar.names if n.upper() == w.upper()]
        if not match:
            raise ExecuteError(f"{tool}: the radar data has no polarization band '{w}'; it has {', '.join(sar.names)}.")
        out.append(match[0])
    return out


def word(value, default):
    return str(value).upper() if _is_given(value) else default


# ---- Sentinel-1 SAFE


def _text(node, path, default=None):
    found = node.find(path)
    return found.text if found is not None and found.text is not None else default


def _numbers(text, kind=float):
    return [kind(x) for x in str(text).split()] if text else []


def _ipf_version(safe):
    manifest = os.path.join(safe, "manifest.safe")
    if not os.path.exists(manifest):
        return None
    for node in ET.parse(manifest).iter():
        if node.tag.endswith("software") and "IPF" in (node.get("name") or ""):
            try:
                return float(node.get("version"))
            except (TypeError, ValueError):
                return None
    return None


def read_safe(path, tool):
    """A Sentinel-1 Level-1 product: one swath's measurement image per polarization, with its annotation."""
    path = path.rstrip("/\\")
    swath = None
    if os.path.basename(path).lower() == "manifest.safe":
        safe = os.path.dirname(path)
    elif path.upper().endswith(".SAFE"):
        safe = path
    else:
        safe, swath = os.path.dirname(path), os.path.basename(path).upper()
    found = {}
    for xml in sorted(glob.glob(os.path.join(safe, "annotation", "*.xml"))):
        root = ET.parse(xml).getroot()
        header = root.find("adsHeader")
        found.setdefault(_text(header, "swath").upper(), []).append((xml, root))
    if not found:
        raise ExecuteError(f"{tool}: '{safe}' holds no annotation/*.xml; it is not a Sentinel-1 Level-1 product.")
    if swath is None:
        if len(found) > 1:
            raise ExecuteError(f"{tool}: '{safe}' holds {len(found)} swaths ({', '.join(sorted(found))}); name one as "
                               f"{os.path.basename(safe)}/<swath>.")
        swath = next(iter(found))
    if swath not in found:
        raise ExecuteError(f"{tool}: '{safe}' has no swath {swath}; it has {', '.join(sorted(found))}.")
    import rasterio
    bands, pols = {}, {}
    meta = None
    for xml, root in found[swath]:
        header = root.find("adsHeader")
        pol = _text(header, "polarisation").upper()
        name = os.path.splitext(os.path.basename(xml))[0]
        tiff = os.path.join(safe, "measurement", name + ".tiff")
        if not os.path.exists(tiff):
            raise ExecuteError(f"{tool}: '{tiff}' is missing from the product.")
        with _quiet(), rasterio.open(tiff) as source:
            band = source.read(1)
        product = _text(header, "productType").upper()
        bands[pol] = band.astype(np.complex64) if np.iscomplexobj(band) else band.astype(np.float32)
        if meta is None:
            meta = _annotation(root, product, band.shape)
            meta["ipf_version"] = _ipf_version(safe)
            meta["source"] = safe
        pols[pol] = {"calibration": _calibration(os.path.join(safe, "annotation", "calibration", "calibration-" + name + ".xml")),
                     "noise": _noise(os.path.join(safe, "annotation", "calibration", "noise-" + name + ".xml"))}
    meta["pols"] = pols
    override = os.path.join(safe, "grapy-orbit.json")
    if os.path.exists(override):
        meta["orbit"] = json.load(open(override, encoding="utf-8"))["orbit"]
        meta["orbit_source"] = json.load(open(override, encoding="utf-8")).get("source")
    order = [p for p in ("HH", "HV", "VH", "VV") if p in bands] + [p for p in bands if p not in ("HH", "HV", "VH", "VV")]
    return SarData({p: bands[p] for p in order}, meta, None, 0, safe)


def _annotation(root, product, shape):
    header = root.find("adsHeader")
    general = root.find("generalAnnotation")
    info = general.find("productInformation")
    image = root.find("imageAnnotation/imageInformation")
    rows, columns = shape
    meta = {
        "geometry": "radar", "mission": _text(header, "missionId"), "mode": _text(header, "mode"),
        "swath": _text(header, "swath"), "product": product,
        "values": "complex" if product == "SLC" else "amplitude", "scale": "linear", "calibration": "none",
        "noise_removed": False, "pass": _text(info, "pass"),
        "radar_frequency": float(_text(info, "radarFrequency")),
        "range_sampling_rate": float(_text(info, "rangeSamplingRate")),
        "range_pixel_spacing": float(_text(image, "rangePixelSpacing")),
        "azimuth_pixel_spacing": float(_text(image, "azimuthPixelSpacing")),
        "slant_range_time": float(_text(image, "slantRangeTime")),
        "azimuth_time_interval": float(_text(image, "azimuthTimeInterval")),
        "incidence_angle_mid": float(_text(image, "incidenceAngleMidSwath")),
        "first_line_time": seconds(_text(image, "productFirstLineUtcTime")),
        "line_interval": float(_text(image, "azimuthTimeInterval")),
        "lines": list(range(rows)), "pixels": list(range(columns)), "looks": [1, 1],
    }
    orbit = []
    for o in general.findall("orbitList/orbit"):
        orbit.append([seconds(_text(o, "time")), *(float(_text(o, f"position/{a}")) for a in "xyz"),
                      *(float(_text(o, f"velocity/{a}")) for a in "xyz")])
    meta["orbit"] = orbit
    meta["orbit_source"] = "annotation"
    grid = []
    for g in root.findall("geolocationGrid/geolocationGridPointList/geolocationGridPoint"):
        grid.append([seconds(_text(g, "azimuthTime")), float(_text(g, "slantRangeTime")), float(_text(g, "line")),
                     float(_text(g, "pixel")), float(_text(g, "latitude")), float(_text(g, "longitude")),
                     float(_text(g, "height")), float(_text(g, "incidenceAngle"))])
    meta["grid"] = grid
    timing = root.find("swathTiming")
    bursts = timing.findall("burstList/burst") if timing is not None else []
    if bursts:
        meta["bursts"] = {
            "lines_per_burst": int(_text(timing, "linesPerBurst")),
            "samples_per_burst": int(_text(timing, "samplesPerBurst")),
            "list": [{"time": seconds(_text(b, "azimuthTime")),
                      "first_valid": _numbers(_text(b, "firstValidSample"), int),
                      "last_valid": _numbers(_text(b, "lastValidSample"), int)} for b in bursts]}
        meta["first_line_time"] = None
    return meta


def _calibration(path):
    if not os.path.exists(path):
        return None
    vectors = []
    for v in ET.parse(path).getroot().findall("calibrationVectorList/calibrationVector"):
        vectors.append({"line": int(_text(v, "line")), "pixel": _numbers(_text(v, "pixel"), int),
                        "sigma0": _numbers(_text(v, "sigmaNought")), "beta0": _numbers(_text(v, "betaNought")),
                        "gamma0": _numbers(_text(v, "gamma"))})
    return vectors


def _noise(path):
    if not os.path.exists(path):
        return None
    root = ET.parse(path).getroot()
    out = {"range": [], "azimuth": []}
    ranges = root.findall("noiseRangeVectorList/noiseRangeVector") or root.findall("noiseVectorList/noiseVector")
    for v in ranges:
        lut = _text(v, "noiseRangeLut") or _text(v, "noiseLut")
        out["range"].append({"line": int(_text(v, "line")), "pixel": _numbers(_text(v, "pixel"), int),
                             "values": _numbers(lut)})
    for v in root.findall("noiseAzimuthVectorList/noiseAzimuthVector"):
        out["azimuth"].append({
            "first_line": int(_text(v, "firstAzimuthLine")), "last_line": int(_text(v, "lastAzimuthLine")),
            "first_sample": int(_text(v, "firstRangeSample")), "last_sample": int(_text(v, "lastRangeSample")),
            "line": _numbers(_text(v, "line"), int), "values": _numbers(_text(v, "noiseAzimuthLut"))})
    return out


# ---- look-up tables: bilinear over the original lines and pixels a dataset's rows and columns came from


def lut(vectors, key, lines, pixels):
    """A LUT given as vectors (each at one line, over pixels) interpolated bilinearly at every (line, pixel) — the
    interpolation ESA's calibration note (ESA-EOPG-CSCOP-TN-0002) prescribes; held constant beyond the ends."""
    lines = np.asarray(lines, float)
    pixels = np.asarray(pixels, float)
    at = np.array([v["line"] for v in vectors], float)
    rows = np.array([np.interp(pixels, v["pixel"], v[key]) for v in vectors])  # vectors × columns
    if len(at) == 1:
        return np.repeat(rows, len(lines), axis=0)
    k = np.clip(np.searchsorted(at, lines, side="right") - 1, 0, len(at) - 2)
    w = np.clip((lines - at[k]) / (at[k + 1] - at[k]), 0.0, 1.0)[:, None]
    return rows[k] * (1 - w) + rows[k + 1] * w


def noise(vectors, lines, pixels):
    """The thermal noise power at every (line, pixel): the range LUT, times the azimuth LUT of the block that holds
    the pixel where the product has one (IPF 2.9 and later, ESA MPC-0392)."""
    eta = lut(vectors["range"], "values", lines, pixels)
    lines = np.asarray(lines, float)
    pixels = np.asarray(pixels, float)
    for block in vectors.get("azimuth") or []:
        rows = (lines >= block["first_line"]) & (lines <= block["last_line"])
        columns = (pixels >= block["first_sample"]) & (pixels <= block["last_sample"])
        if not rows.any() or not columns.any():
            continue
        values = np.interp(lines[rows], block["line"], block["values"]) if len(block["line"]) > 1 else np.full(
            rows.sum(), block["values"][0])
        eta[np.ix_(rows, columns)] *= values[:, None]
    return eta


# ---- orbit and range-Doppler geometry


def _orbit(meta):
    o = np.asarray(meta["orbit"], float)
    if len(o) < 2:
        raise ExecuteError("The radar data carries fewer than two orbit state vectors.")
    return o[np.argsort(o[:, 0])]


def state(meta, t):
    """Position and velocity at azimuth times ``t``, by cubic Hermite interpolation of the bracketing state vectors
    (positions and velocities both honoured)."""
    o = _orbit(meta)
    t = np.atleast_1d(np.asarray(t, float))
    k = np.clip(np.searchsorted(o[:, 0], t) - 1, 0, len(o) - 2)
    t0, t1 = o[k, 0], o[k + 1, 0]
    h = (t1 - t0)[:, None]
    s = ((t - t0) / (t1 - t0))[:, None]
    p0, p1, v0, v1 = o[k, 1:4], o[k + 1, 1:4], o[k, 4:7] * h, o[k + 1, 4:7] * h
    h00, h10, h01, h11 = 2 * s ** 3 - 3 * s ** 2 + 1, s ** 3 - 2 * s ** 2 + s, -2 * s ** 3 + 3 * s ** 2, s ** 3 - s ** 2
    pos = h00 * p0 + h10 * v0 + h01 * p1 + h11 * v1
    d00, d10, d01, d11 = 6 * s ** 2 - 6 * s, 3 * s ** 2 - 4 * s + 1, -6 * s ** 2 + 6 * s, 3 * s ** 2 - 2 * s
    vel = (d00 * p0 + d10 * v0 + d01 * p1 + d11 * v1) / h
    return pos, vel


def ecef(lon, lat, h):
    lon, lat, h = (np.radians(np.asarray(lon, float)), np.radians(np.asarray(lat, float)), np.asarray(h, float))
    e2 = 1 - (WGS84_B / WGS84_A) ** 2
    n = WGS84_A / np.sqrt(1 - e2 * np.sin(lat) ** 2)
    return np.stack([(n + h) * np.cos(lat) * np.cos(lon), (n + h) * np.cos(lat) * np.sin(lon),
                     (n * (1 - e2) + h) * np.sin(lat)], axis=-1)


def geodetic(p):
    """Longitude, latitude (degrees) and ellipsoidal height of ECEF points (Bowring's method, iterated)."""
    p = np.asarray(p, float)
    x, y, z = p[..., 0], p[..., 1], p[..., 2]
    e2 = 1 - (WGS84_B / WGS84_A) ** 2
    lon = np.arctan2(y, x)
    r = np.hypot(x, y)
    lat = np.arctan2(z, r * (1 - e2))
    for _ in range(5):
        n = WGS84_A / np.sqrt(1 - e2 * np.sin(lat) ** 2)
        h = r / np.cos(lat) - n
        lat = np.arctan2(z, r * (1 - e2 * n / (n + h)))
    n = WGS84_A / np.sqrt(1 - e2 * np.sin(lat) ** 2)
    h = r / np.cos(lat) - n
    return np.degrees(lon), np.degrees(lat), h


def row_time(meta, rows):
    """Azimuth time of rows of a dataset that is not burst-stacked."""
    if meta.get("bursts"):
        raise ExecuteError("The radar data is still in bursts; run Deburst first, as the documented workflow does.")
    return meta["first_line_time"] + np.asarray(rows, float) * meta["line_interval"]


def _grid(meta):
    g = np.asarray(meta["grid"], float)
    if len(g) < 4:
        raise ExecuteError("The radar data carries too few geolocation tie points for its geometry.")
    return g


def _grid_rows(meta):
    """The tie points grouped by their image line: [(mean azimuth time, points sorted by pixel)]. A real GRD's points
    on one line differ in azimuth time by about half a millisecond across the swath (measured on
    S1D_IW_GRDH_1SDV_20261031T054608, ADR-237 condition 3), so the time of a row is read at each pixel (_row_time);
    grouping by time found no row of two points at all."""
    g = _grid(meta)
    out = []
    for line in np.unique(g[:, 2]):
        r = g[g[:, 2] == line]
        r = r[np.argsort(r[:, 3])]
        if len(r) >= 2:
            out.append((float(r[:, 0].mean()), r))
    out.sort(key=lambda row: row[0])
    return out


def _row_time(rows, pixel):
    """Each tie-point row's azimuth time at ``pixel`` (rows × points), interpolated along the row."""
    return np.array([_extrap(np.asarray(pixel, float), r[:, 3], r[:, 0]) for _, r in rows])


def _over_grid(meta, t, pixel, column):
    """A tie-point quantity (column of the grid) at azimuth times ``t`` and original pixels, interpolated linearly
    along pixels within each tie-point row and then between rows in time."""
    rows = _grid_rows(meta)
    times = np.array([r[0] for r in rows])
    t = np.atleast_1d(np.asarray(t, float))
    pixel = np.broadcast_to(np.asarray(pixel, float), t.shape)
    values = np.array([_extrap(pixel, r[:, 3], r[:, column]) for _, r in rows])
    if len(times) == 1:
        return values[0]
    k = np.clip(np.searchsorted(times, t, side="right") - 1, 0, len(times) - 2)
    i = np.arange(t.size)
    at = _row_time(rows, pixel)
    w = (t - at[k, i]) / (at[k + 1, i] - at[k, i])
    return values[k, i] * (1 - w) + values[k + 1, i] * w


def _extrap(x, xp, fp):
    """Linear interpolation that continues the end segments' slopes beyond them."""
    y = np.interp(x, xp, fp)
    lo, hi = x < xp[0], x > xp[-1]
    if lo.any():
        y[lo] = fp[0] + (x[lo] - xp[0]) * (fp[1] - fp[0]) / (xp[1] - xp[0])
    if hi.any():
        y[hi] = fp[-1] + (x[hi] - xp[-1]) * (fp[-1] - fp[-2]) / (xp[-1] - xp[-2])
    return y


def column_pixel(meta, columns):
    return _extrap(np.asarray(columns, float), np.arange(len(meta["pixels"]), dtype=float),
                   np.asarray(meta["pixels"], float))


def pixel_column(meta, pixels):
    return _extrap(np.asarray(pixels, float), np.asarray(meta["pixels"], float),
                   np.arange(len(meta["pixels"]), dtype=float))


def range_time(meta, t, columns):
    """Two-way slant range time at rows' azimuth times and columns, from the tie points."""
    return _over_grid(meta, t, column_pixel(meta, columns), 1)


def incidence(meta, t, columns):
    return _over_grid(meta, t, column_pixel(meta, columns), 7)


def tie_height(meta, t, columns):
    return _over_grid(meta, t, column_pixel(meta, columns), 6)


def _pixel_of_range(meta, t, tau):
    """The original pixel at which the slant range time is ``tau`` at azimuth time ``t`` (the inverse of the
    tie-point interpolation, which is monotonic along each row)."""
    rows = _grid_rows(meta)
    times = np.array([r[0] for r in rows])
    t = np.atleast_1d(np.asarray(t, float))
    tau = np.atleast_1d(np.asarray(tau, float))
    per = np.array([_extrap(tau, r[:, 1], r[:, 3]) for _, r in rows])
    if len(times) == 1:
        return per[0]
    k = np.clip(np.searchsorted(times, t, side="right") - 1, 0, len(times) - 2)
    i = np.arange(t.size)
    at = np.array([_extrap(per[j], r[:, 3], r[:, 0]) for j, (_, r) in enumerate(rows)])
    w = (t - at[k, i]) / (at[k + 1, i] - at[k, i])
    return per[k, i] * (1 - w) + per[k + 1, i] * w


def geocode(meta, rows, columns, heights):
    """ECEF ground points of radar pixels at ellipsoidal heights: zero Doppler, the slant range, and the ellipsoid
    raised by the height, solved by Newton's method from the tie points' estimate."""
    rows = np.atleast_1d(np.asarray(rows, float))
    columns = np.broadcast_to(np.asarray(columns, float), rows.shape)
    h = np.broadcast_to(np.asarray(heights, float), rows.shape)
    t = row_time(meta, rows)
    r = range_time(meta, t, columns) * C / 2
    s, v = state(meta, t)
    lon = _over_grid(meta, t, column_pixel(meta, columns), 5)
    lat = _over_grid(meta, t, column_pixel(meta, columns), 4)
    p = ecef(lon, lat, h)
    a2, b2 = (WGS84_A + h) ** 2, (WGS84_B + h) ** 2
    for _ in range(12):
        d = p - s
        f = np.stack([np.sum(d * v, -1), np.sum(d * d, -1) - r ** 2,
                      (p[:, 0] ** 2 + p[:, 1] ** 2) / a2 + p[:, 2] ** 2 / b2 - 1], -1)
        jac = np.stack([v, 2 * d, np.stack([2 * p[:, 0] / a2, 2 * p[:, 1] / a2, 2 * p[:, 2] / b2], -1)], 1)
        step = np.linalg.solve(jac, f[..., None])[..., 0]
        p = p - step
        if np.max(np.abs(step)) < 1e-4:
            break
    return p


def zero_doppler(meta, p):
    """The azimuth time at which each ECEF point is at zero Doppler, and the slant range then."""
    p = np.atleast_2d(np.asarray(p, float))
    o = _orbit(meta)
    # start from the state vector nearest each point, then Newton on f(t) = (P - S(t)) . V(t)
    distance = np.linalg.norm(p[:, None, :] - o[None, :, 1:4], axis=-1)
    t = o[np.argmin(distance, axis=1), 0]
    for _ in range(30):
        s, v = state(meta, t)
        d = p - s
        f = np.sum(d * v, -1)
        step = f / np.sum(v * v, -1)
        t = t + step
        if np.max(np.abs(step)) < 1e-9:
            break
    s, v = state(meta, t)
    return t, np.linalg.norm(p - s, axis=-1), s


def backgeocode(meta, p):
    """Fractional (row, column) of ECEF points in a dataset's radar geometry, and the sensor positions."""
    t, r, s = zero_doppler(meta, p)
    rows = (t - meta["first_line_time"]) / meta["line_interval"]
    pixels = _pixel_of_range(meta, t, 2 * r / C)
    return rows, pixel_column(meta, pixels), s


def ground_spacing(meta):
    """(range, azimuth) ground spacing of one cell: slant range spacing over the sine of the mid-swath incidence for
    slant range products, as the metadata gives it for ground range ones."""
    rg = meta["range_pixel_spacing"]
    if meta.get("product") == "SLC":
        rg = rg / math.sin(math.radians(meta["incidence_angle_mid"]))
    looks = meta.get("looks", [1, 1])
    return rg * looks[0], meta["azimuth_pixel_spacing"] * looks[1]


# ---- DEMs


class Dem:
    """A DEM read once, sampled bilinearly at longitude and latitude (WGS 84) through pyproj when it is held in
    another reference."""

    def __init__(self, value, tool, geoid="GEOID"):
        from ._raster import Raster
        if word(geoid, "GEOID") not in ("NONE", "FALSE"):
            raise NotBuiltError(f"arcpy.ia.{tool}: geoid 'GEOID' is not built in grapy: grapy carries no EGM96 geoid "
                                "grid; give a DEM of ellipsoidal heights with geoid 'NONE'.")
        raster = Raster(value)
        self.values = np.where(raster._mask, np.nan, raster._array.astype(float))
        self.grid = raster._grid
        self.srid = raster._grid.srid or 4326
        self._to = None
        if self.srid != 4326:
            import pyproj
            self._to = pyproj.Transformer.from_crs(4326, self.srid, always_xy=True)

    def at(self, lon, lat):
        x, y = (lon, lat) if self._to is None else self._to.transform(lon, lat)
        g = self.grid
        c = (np.asarray(x, float) - g.left) / g.width - 0.5
        r = (g.top - np.asarray(y, float)) / g.height - 0.5
        return bilinear(self.values, r, c)


def bilinear(array, rows, columns):
    """Values of an array at fractional (row, column) cell centres, bilinearly; NaN outside it."""
    rows = np.asarray(rows, float)
    columns = np.asarray(columns, float)
    n, m = array.shape
    inside = (rows >= -0.5) & (rows <= n - 0.5) & (columns >= -0.5) & (columns <= m - 0.5)
    r = np.clip(rows, 0, n - 1)
    c = np.clip(columns, 0, m - 1)
    r0 = np.clip(np.floor(r).astype(int), 0, max(n - 2, 0))
    c0 = np.clip(np.floor(c).astype(int), 0, max(m - 2, 0))
    r1, c1 = np.minimum(r0 + 1, n - 1), np.minimum(c0 + 1, m - 1)
    wr, wc = r - r0, c - c0
    out = (array[r0, c0] * (1 - wr) * (1 - wc) + array[r0, c1] * (1 - wr) * wc + array[r1, c0] * wr * (1 - wc)
           + array[r1, c1] * wr * wc)
    if np.iscomplexobj(out):
        out = np.where(inside, out, np.nan + 1j * np.nan)
    else:
        out = np.where(inside, out, np.nan)
    return out


def heights_for(meta, rows, columns, dem, iterations=3):
    """Ground heights of radar pixels: from the DEM where one is given (geocoding and reading it in turn), from the
    tie points' heights otherwise or where the DEM has none."""
    rows = np.atleast_1d(np.asarray(rows, float))
    columns = np.broadcast_to(np.asarray(columns, float), rows.shape)
    t = row_time(meta, rows)
    h = tie_height(meta, t, columns)
    if dem is None:
        return h
    for _ in range(iterations):
        lon, lat, _ = geodetic(geocode(meta, rows, columns, h))
        found = dem.at(lon, lat)
        h = np.where(np.isnan(found), h, found)
    return h


def lonlat_grid(meta, shape, dem=None, step=16):
    """Longitude and latitude of every cell of a radar-geometry dataset, geocoded on a coarse lattice of ``step``
    cells and interpolated bilinearly between."""
    n, m = shape
    rs = np.unique(np.r_[np.arange(0, n, step), n - 1]).astype(float)
    cs = np.unique(np.r_[np.arange(0, m, step), m - 1]).astype(float)
    rr, cc = np.meshgrid(rs, cs, indexing="ij")
    h = heights_for(meta, rr.ravel(), cc.ravel(), dem)
    lon, lat, _ = geodetic(geocode(meta, rr.ravel(), cc.ravel(), h))
    lon, lat = lon.reshape(rr.shape), lat.reshape(rr.shape)
    from scipy.interpolate import RegularGridInterpolator
    R, Cc = np.meshgrid(np.arange(n, dtype=float), np.arange(m, dtype=float), indexing="ij")
    pts = np.stack([R.ravel(), Cc.ravel()], -1)
    lon_i = RegularGridInterpolator((rs, cs), lon)(pts).reshape(n, m) if len(rs) > 1 and len(cs) > 1 else np.full((n, m), lon.mean())
    lat_i = RegularGridInterpolator((rs, cs), lat)(pts).reshape(n, m) if len(rs) > 1 and len(cs) > 1 else np.full((n, m), lat.mean())
    return lon_i, lat_i


def map_centres(sar):
    """x and y of every cell centre of a map-geometry dataset."""
    n, m = sar.shape
    t = sar.transform
    cols, rows = np.meshgrid(np.arange(m) + 0.5, np.arange(n) + 0.5)
    return t.c + cols * t.a + rows * t.b, t.f + cols * t.d + rows * t.e


def cell_lonlat(sar, dem=None):
    """Longitude and latitude of every cell of a dataset, in either geometry."""
    if sar.radar:
        return lonlat_grid(sar.meta, sar.shape, dem)
    x, y = map_centres(sar)
    if sar.srid in (0, 4326):
        return x, y
    import pyproj
    to = pyproj.Transformer.from_crs(sar.srid, 4326, always_xy=True)
    return to.transform(x, y)


def intensity(array):
    return (np.abs(array) ** 2).astype(np.float32) if np.iscomplexobj(array) else array


def needs_linear(sar, tool):
    if sar.meta.get("scale") == "db":
        raise ExecuteError(f"{tool}: the radar data is in decibels; convert it to linear units with ConvertSARUnits.")
