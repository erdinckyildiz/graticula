"""The World Magnetic Model, for arcpy.topographic's magnetic tools (ADR-251).

The coefficients are NOAA NCEI's and the British Geological Survey's World Magnetic Model files, WMM2015v2, WMM2020 and
WMM2025, bundled unchanged in ``_topo_prod_data`` as published at https://www.ncei.noaa.gov/products/world-magnetic-model
(downloaded 2026-10-31). The World Magnetic Model is a work of the United States Government and is in the public
domain. The computation follows *The US/UK World Magnetic Model for 2025-2030: Technical Report* (Chulliat et al.,
NOAA NCEI / BGS, 2025), section 1.2: degree and order 12 Schmidt semi-normalised spherical harmonics with secular
variation, the WGS 84 geodetic position turned into geocentric spherical coordinates, and the field rotated back to the
geodetic frame.

A model is valid for the five years from its epoch; a date outside every bundled model is an error, as Esri's page
says the tool takes the model of the date's own epoch.
"""
from __future__ import annotations

import datetime
import math
import os

_DATA = os.path.join(os.path.dirname(os.path.abspath(__file__)), "_topo_prod_data")
_BUNDLED = ("WMM2015v2.COF", "WMM2020.COF", "WMM2025.COF")
_A_WGS84, _F_WGS84 = 6378.137, 1 / 298.257223563
_REFERENCE_RADIUS = 6371.2


class Model:
    def __init__(self, epoch, name, g, h, gdot, hdot, degree):
        self.epoch, self.name, self.g, self.h, self.gdot, self.hdot, self.degree = epoch, name, g, h, gdot, hdot, degree

    def valid(self, year):
        return self.epoch <= year < self.epoch + 5


def read(path):
    """A NOAA-format .COF coefficient file: a header line (epoch, model name, date), then ``n m g h gdot hdot`` rows,
    ended by a row of 9s."""
    with open(path, encoding="ascii") as handle:
        lines = [line for line in handle.read().splitlines() if line.strip()]
    head = lines[0].split()
    epoch, name = float(head[0]), head[1]
    g, h, gdot, hdot, degree = {}, {}, {}, {}, 0
    for line in lines[1:]:
        if line.strip().startswith("9999"):
            break
        n, m, gv, hv, gd, hd = line.split()[:6]
        n, m = int(n), int(m)
        g[n, m], h[n, m], gdot[n, m], hdot[n, m] = float(gv), float(hv), float(gd), float(hd)
        degree = max(degree, n)
    if not g:
        raise ValueError(f"'{path}' holds no World Magnetic Model coefficients.")
    return Model(epoch, name, g, h, gdot, hdot, degree)


def bundled():
    return [read(os.path.join(_DATA, name)) for name in _BUNDLED]


def decimal_year(moment):
    """A date as a decimal year: 2025-07-02 12:00 is 2025.5 (the day's fraction counted from 1 January)."""
    if isinstance(moment, datetime.datetime):
        start = datetime.datetime(moment.year, 1, 1)
        end = datetime.datetime(moment.year + 1, 1, 1)
        return moment.year + (moment - start).total_seconds() / (end - start).total_seconds()
    start = datetime.date(moment.year, 1, 1)
    days = (datetime.date(moment.year + 1, 1, 1) - start).days
    return moment.year + (moment - start).days / days


def model_for(year, path=None):
    """The model valid at ``year``: the file given, or the bundled model of that epoch."""
    if path is not None:
        model = read(path)
        if not model.valid(year):
            raise ValueError(f"the model {model.name} is valid from {model.epoch:g} to {model.epoch + 5:g}; "
                             f"{year:.3f} is outside it.")
        return model
    for model in bundled():
        if model.valid(year):
            return model
    raise ValueError(f"no World Magnetic Model held covers {year:.3f}; the bundled models cover 2015.0 to 2030.0.")


def _legendre(degree, x):
    """Schmidt semi-normalised associated Legendre functions P[n][m](x) and their derivatives with respect to the
    geocentric latitude, from the unnormalised recurrences (no Condon-Shortley phase)."""
    c = math.sqrt(max(0.0, 1 - x * x))
    raw = [[0.0] * (degree + 2) for _ in range(degree + 2)]
    for m in range(degree + 1):
        double_factorial = 1.0
        for k in range(1, 2 * m, 2):
            double_factorial *= k
        raw[m][m] = double_factorial * c ** m
        if m + 1 <= degree:
            raw[m + 1][m] = x * (2 * m + 1) * raw[m][m]
        for n in range(m + 2, degree + 1):
            raw[n][m] = ((2 * n - 1) * x * raw[n - 1][m] - (n + m - 1) * raw[n - 2][m]) / (n - m)
    p = [[0.0] * (degree + 1) for _ in range(degree + 1)]
    dp = [[0.0] * (degree + 1) for _ in range(degree + 1)]
    for n in range(degree + 1):
        for m in range(n + 1):
            scale = 1.0 if m == 0 else math.sqrt(2 * math.factorial(n - m) / math.factorial(n + m))
            p[n][m] = scale * raw[n][m]
            previous = raw[n - 1][m] if n >= 1 and m <= n - 1 else 0.0
            # (1 - x²) dP/dx = (n + m) P(n-1, m) - n x P(n, m), and dP/dφ = cos φ · dP/dx.
            dp[n][m] = scale * ((n + m) * previous - n * x * raw[n][m]) / c
    return p, dp


def field(model, latitude, longitude, height_km, year):
    """The geomagnetic elements at a WGS 84 geodetic position: X, Y, Z, H, F (nT), I and D (degrees), and their
    yearly changes Xdot, Ydot, Zdot, Hdot, Fdot (nT/yr), Idot and Ddot (degrees/yr)."""
    lat = math.radians(max(-89.999999, min(89.999999, latitude)))
    lon = math.radians(longitude)
    e2 = _F_WGS84 * (2 - _F_WGS84)
    rc = _A_WGS84 / math.sqrt(1 - e2 * math.sin(lat) ** 2)
    p_ = (rc + height_km) * math.cos(lat)
    z_ = (rc * (1 - e2) + height_km) * math.sin(lat)
    r = math.hypot(p_, z_)
    lat_c = math.asin(z_ / r)
    dt = year - model.epoch
    p, dp = _legendre(model.degree, math.sin(lat_c))

    def sums(g_of, h_of):
        x = y = z = 0.0
        for n in range(1, model.degree + 1):
            ratio = (_REFERENCE_RADIUS / r) ** (n + 2)
            for m in range(n + 1):
                g, h = g_of(n, m), h_of(n, m)
                cos_m, sin_m = math.cos(m * lon), math.sin(m * lon)
                x -= ratio * (g * cos_m + h * sin_m) * dp[n][m]
                y += ratio * m * (g * sin_m - h * cos_m) * p[n][m]
                z -= (n + 1) * ratio * (g * cos_m + h * sin_m) * p[n][m]
        y /= math.cos(lat_c)
        turn = lat_c - lat
        return x * math.cos(turn) - z * math.sin(turn), y, x * math.sin(turn) + z * math.cos(turn)

    x, y, z = sums(lambda n, m: model.g[n, m] + dt * model.gdot[n, m],
                   lambda n, m: model.h[n, m] + dt * model.hdot[n, m])
    xd, yd, zd = sums(lambda n, m: model.gdot[n, m], lambda n, m: model.hdot[n, m])
    h = math.hypot(x, y)
    f = math.hypot(h, z)
    hd = (x * xd + y * yd) / h
    fd = (x * xd + y * yd + z * zd) / f
    return {"X": x, "Y": y, "Z": z, "H": h, "F": f,
            "I": math.degrees(math.atan2(z, h)), "D": math.degrees(math.atan2(y, x)),
            "Xdot": xd, "Ydot": yd, "Zdot": zd, "Hdot": hd, "Fdot": fd,
            "Idot": math.degrees((h * zd - z * hd) / (f * f)), "Ddot": math.degrees((x * yd - y * xd) / (h * h))}
