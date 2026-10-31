"""A synthetic radar scene for grapy's SAR tests (ADR-237): no Sentinel scene is downloaded.

The sensor flies a circular polar orbit of radius 7071 km in a frozen Earth-fixed frame, crossing the equator at 30° E
going north at T0; the image starts 5 s later. It looks right (east), 808 km in slant range. Ground points are found
here with scipy's ``fsolve`` on the range-Doppler equations — the same equations grapy solves, but solved by another
method from another start, so a fault in grapy's Newton steps, interpolations or tie-point handling shows.
"""
import math
import os

import numpy as np
from scipy.optimize import fsolve

from grapy import _sar

GM = 3.986004418e14
RS = 7071e3
W = math.sqrt(GM / RS ** 3)
LON0 = math.radians(30.0)
T0 = _sar.seconds("2020-06-01T10:00:00")
T1 = T0 + 5.0
DT = 0.002
FS = 64.345e6
R0 = 808e3
TAU0 = 2 * R0 / _sar.C
FREQ = 5.405e9
ROWS, COLS = 60, 80


def state(t, offset=(0.0, 0.0, 0.0)):
    a = W * (t - T0)
    s = RS * np.array([math.cos(a) * math.cos(LON0), math.cos(a) * math.sin(LON0), math.sin(a)]) + np.asarray(offset)
    v = RS * W * np.array([-math.sin(a) * math.cos(LON0), -math.sin(a) * math.sin(LON0), math.cos(a)])
    return s, v


def vectors(offset=(0.0, 0.0, 0.0)):
    out = []
    for t in T0 + np.arange(-60, 71, 10.0):
        s, v = state(t, offset)
        out.append([float(t), *s.tolist(), *v.tolist()])
    return out


def ground(t, r, h=0.0, offset=(0.0, 0.0, 0.0)):
    """The point at height h on the right of the track at zero Doppler time t and slant range r."""
    s, v = state(t, offset)
    a2, b2 = (_sar.WGS84_A + h) ** 2, (_sar.WGS84_B + h) ** 2
    lat = math.degrees(math.asin(s[2] / np.linalg.norm(s)))
    guess = _sar.ecef(math.degrees(LON0) + 4.0, lat, h)

    def f(p):
        d = p - s
        return [np.dot(d, v) / 1e3, (np.dot(d, d) - r * r) / 1e9, (p[0] ** 2 + p[1] ** 2) / a2 + p[2] ** 2 / b2 - 1]

    p = fsolve(f, guess, xtol=1e-13)
    return p


def incidence_at(p, t):
    s, _ = state(t)
    look = (p - s) / np.linalg.norm(p - s)
    lon, lat, _ = _sar.geodetic(p)
    _, _, up = _enu(lon, lat)
    return math.degrees(math.acos(-np.dot(look, up)))


def _enu(lon, lat):
    lo, la = math.radians(float(lon)), math.radians(float(lat))
    east = np.array([-math.sin(lo), math.cos(lo), 0.0])
    north = np.array([-math.sin(la) * math.cos(lo), -math.sin(la) * math.sin(lo), math.cos(la)])
    up = np.array([math.cos(la) * math.cos(lo), math.cos(la) * math.sin(lo), math.sin(la)])
    return east, north, up


def grid(rows=ROWS, cols=COLS, first=T1, dt=DT, h=0.0):
    """Tie points [time, tau, line, pixel, lat, lon, height, incidence] on a 3 × 3 lattice."""
    out = []
    for line in (0, (rows - 1) // 2, rows - 1):
        for pixel in (0, (cols - 1) // 2, cols - 1):
            t = first + line * dt
            tau = TAU0 + pixel / FS
            p = ground(t, tau * _sar.C / 2, h)
            lon, lat, height = _sar.geodetic(p)
            out.append([t, tau, line, pixel, float(lat), float(lon), float(height), incidence_at(p, t)])
    return out


def meta(rows=ROWS, cols=COLS, values="complex", offset=(0.0, 0.0, 0.0), mode="SM", product="SLC", first=T1):
    g = grid(rows, cols, first)
    speed = RS * W * _sar.WGS84_A / RS
    return {
        "geometry": "radar", "mission": "TEST", "mode": mode, "swath": "S1", "product": product, "values": values,
        "scale": "linear", "calibration": "none", "noise_removed": False, "pass": "ASCENDING",
        "radar_frequency": FREQ, "range_sampling_rate": FS, "range_pixel_spacing": _sar.C / (2 * FS),
        "azimuth_pixel_spacing": speed * DT, "slant_range_time": TAU0, "azimuth_time_interval": DT,
        "incidence_angle_mid": g[4][7], "first_line_time": first, "line_interval": DT,
        "lines": list(range(rows)), "pixels": list(range(cols)), "looks": [1, 1],
        "orbit": vectors(offset), "orbit_source": "annotation", "grid": g,
    }


def write(path, bands, m):
    return _sar.write(path, bands, m)


def dem(path, west, south, east, north, value=0.0, cell=0.0002):
    """A WGS 84 DEM GeoTIFF of one height (or a function of lon, lat)."""
    import rasterio
    from rasterio.transform import from_origin
    columns = int(math.ceil((east - west) / cell))
    rows = int(math.ceil((north - south) / cell))
    lon = west + (np.arange(columns) + 0.5) * cell
    lat = north - (np.arange(rows) + 0.5) * cell
    LON, LAT = np.meshgrid(lon, lat)
    heights = value(LON, LAT) if callable(value) else np.full(LON.shape, float(value))
    with rasterio.open(path, "w", driver="GTiff", width=columns, height=rows, count=1, dtype="float32",
                       transform=from_origin(west, north, cell, cell), crs="EPSG:4326") as out:
        out.write(heights.astype(np.float32), 1)
    return path


def footprint(m, rows=ROWS, cols=COLS, margin=0.01):
    pts = [g for g in m["grid"]]
    lons = [p[5] for p in pts]
    lats = [p[4] for p in pts]
    return min(lons) - margin, min(lats) - margin, max(lons) + margin, max(lats) + margin


# ---- a Sentinel-1 SAFE product, in the layout ESA's product specification gives


def _vec(name, items):
    return f"<{name}>" + "".join(items) + f"</{name}>"


def safe(folder, product="GRD", pol_values=None, rows=ROWS, cols=COLS, bursts=None, ipf="003.31",
         sigma=(400.0, 500.0), beta=(300.0, 300.0), gamma=(350.0, 450.0), noise_range=(20.0, 40.0), noise_az=None):
    """A minimal SAFE folder: manifest, annotation, calibration and noise XML and measurement GeoTIFFs for each
    polarization in ``pol_values`` (name -> array). Calibration vectors sit at lines 0 and rows − 1 with values
    ``(at line 0, at the last line)``, constant along pixels; the noise range LUT is linear in pixel from
    noise_range[0] to noise_range[1]."""
    import rasterio
    name = os.path.join(folder, "S1A_TEST.SAFE")
    for sub in ("annotation/calibration", "measurement"):
        os.makedirs(os.path.join(name, sub), exist_ok=True)
    with open(os.path.join(name, "manifest.safe"), "w") as f:
        f.write(f'<xfdu><metadataSection><software name="Sentinel-1 IPF" version="{ipf}"/></metadataSection></xfdu>')
    g = grid(rows, cols)
    first = _sar.iso(T1)
    for pol, values in pol_values.items():
        base = f"s1a-iw-{product.lower()}-{pol.lower()}-test-001"
        grid_xml = "".join(
            f"<geolocationGridPoint><azimuthTime>{_sar.iso(p[0])}</azimuthTime><slantRangeTime>{p[1]!r}</slantRangeTime>"
            f"<line>{int(p[2])}</line><pixel>{int(p[3])}</pixel><latitude>{p[4]!r}</latitude><longitude>{p[5]!r}"
            f"</longitude><height>{p[6]!r}</height><incidenceAngle>{p[7]!r}</incidenceAngle></geolocationGridPoint>"
            for p in g)
        burst_xml = ""
        if bursts:
            lpb, times, first_valid, last_valid = bursts
            burst_xml = ("<swathTiming>" f"<linesPerBurst>{lpb}</linesPerBurst><samplesPerBurst>{cols}</samplesPerBurst>"
                         "<burstList>" + "".join(
                             f"<burst><azimuthTime>{_sar.iso(t)}</azimuthTime><firstValidSample>"
                             f"{' '.join(map(str, first_valid))}</firstValidSample><lastValidSample>"
                             f"{' '.join(map(str, last_valid))}</lastValidSample></burst>" for t in times)
                         + "</burstList></swathTiming>")
        orbit_xml = "".join(
            f"<orbit><time>{_sar.iso(o[0])}</time><position><x>{o[1]!r}</x><y>{o[2]!r}</y><z>{o[3]!r}</z></position>"
            f"<velocity><x>{o[4]!r}</x><y>{o[5]!r}</y><z>{o[6]!r}</z></velocity></orbit>" for o in vectors())
        annotation = (
            "<product><adsHeader><missionId>S1A</missionId><productType>" + product + "</productType>"
            f"<polarisation>{pol}</polarisation><mode>IW</mode><swath>IW</swath></adsHeader>"
            "<generalAnnotation><productInformation><pass>Ascending</pass>"
            f"<radarFrequency>{FREQ!r}</radarFrequency><rangeSamplingRate>{FS!r}</rangeSamplingRate>"
            "</productInformation><orbitList>" + orbit_xml + "</orbitList></generalAnnotation>"
            "<imageAnnotation><imageInformation>"
            f"<productFirstLineUtcTime>{first}</productFirstLineUtcTime><azimuthTimeInterval>{DT!r}</azimuthTimeInterval>"
            f"<rangePixelSpacing>{_sar.C / (2 * FS)!r}</rangePixelSpacing><azimuthPixelSpacing>13.5</azimuthPixelSpacing>"
            f"<slantRangeTime>{TAU0!r}</slantRangeTime><incidenceAngleMidSwath>{g[4][7]!r}</incidenceAngleMidSwath>"
            "</imageInformation></imageAnnotation>" + burst_xml +
            "<geolocationGrid><geolocationGridPointList>" + grid_xml + "</geolocationGridPointList></geolocationGrid>"
            "</product>")
        with open(os.path.join(name, "annotation", base + ".xml"), "w") as f:
            f.write(annotation)
        pix = "0 " + str((cols - 1) // 2) + " " + str(cols - 1)

        def cal_vector(line, k):
            return (f"<calibrationVector><azimuthTime>{first}</azimuthTime><line>{line}</line>"
                    f"<pixel>{pix}</pixel>"
                    f"<sigmaNought>{sigma[k]} {sigma[k]} {sigma[k]}</sigmaNought>"
                    f"<betaNought>{beta[k]} {beta[k]} {beta[k]}</betaNought>"
                    f"<gamma>{gamma[k]} {gamma[k]} {gamma[k]}</gamma><dn>1 1 1</dn></calibrationVector>")
        with open(os.path.join(name, "annotation", "calibration", "calibration-" + base + ".xml"), "w") as f:
            f.write("<calibration><calibrationVectorList>" + cal_vector(0, 0) + cal_vector(rows - 1, 1)
                    + "</calibrationVectorList></calibration>")
        lo, hi = noise_range
        mid = (lo + hi) / 2
        nr = "".join(f"<noiseRangeVector><azimuthTime>{first}</azimuthTime><line>{line}</line><pixel>{pix}</pixel>"
                     f"<noiseRangeLut>{lo} {mid} {hi}</noiseRangeLut></noiseRangeVector>" for line in (0, rows - 1))
        na = ""
        if noise_az is not None:
            na = ("<noiseAzimuthVectorList><noiseAzimuthVector><swath>IW</swath>"
                  f"<firstAzimuthLine>0</firstAzimuthLine><firstRangeSample>0</firstRangeSample>"
                  f"<lastAzimuthLine>{rows - 1}</lastAzimuthLine><lastRangeSample>{cols - 1}</lastRangeSample>"
                  f"<line>0 {rows - 1}</line><noiseAzimuthLut>{noise_az[0]} {noise_az[1]}</noiseAzimuthLut>"
                  "</noiseAzimuthVector></noiseAzimuthVectorList>")
        with open(os.path.join(name, "annotation", "calibration", "noise-" + base + ".xml"), "w") as f:
            f.write("<noise><noiseRangeVectorList>" + nr + "</noiseRangeVectorList>" + na + "</noise>")
        dtype = "complex64" if np.iscomplexobj(values) else "uint16"
        with _sar._quiet(), rasterio.open(os.path.join(name, "measurement", base + ".tiff"), "w", driver="GTiff",
                           width=values.shape[1], height=values.shape[0], count=1, dtype=dtype) as out:
            out.write(values.astype(dtype), 1)
    return name
