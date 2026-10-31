"""Measures along routes — the dynamic segmentation under arcpy.locref (ADR-252).

A route is held here as a list of *parts*, each an (n, 4) numpy array of x, y, z, m; a part ends at a physical gap.
The pages say only that measures between two calibration points are "derived by linear interpolation"
(*Essential Roads and Highways vocabulary*). Everything else here is the published method of arcpy.lr.CalibrateRoutes
(DISTANCE: interpolate between, extrapolate before and after) and arcpy.lr.LocateFeaturesAlongRoutes / MakeRouteEventLayer
(a location's measure is the m interpolated on the nearest segment; an event's shape is the route between its
measures), applied to the parts in route order. Distances count z, as *Create a new route* says the geometric length
does.
"""
from __future__ import annotations

import math

import numpy as np
import shapely
from shapely.ops import linemerge

from ._gpkg import _number

CALIBRATE = ("linear interpolation between calibration points, extrapolated before the first and after the last by the "
             "nearest pair's ratio (the DISTANCE method of Calibrate Routes)",
             "ArcGIS Pro, Calibrate Routes and Essential Roads and Highways vocabulary")
LOCATE = ("a location's measure is the m interpolated on the nearest route segment within the tolerance (Locate "
          "Features Along Routes); an event's shape is the route between its measures (Make Route Event Layer)",
          "ArcGIS Pro, Locate Features Along Routes and Make Route Event Layer")


def parts(shape):
    """The parts of a line (or a point's one vertex) as (n, 4) arrays; a missing z is 0 and a missing m NaN."""
    if shape is None or shape.is_empty:
        return []
    geoms = list(shape.geoms) if hasattr(shape, "geoms") else [shape]
    out = []
    for g in geoms:
        if g.is_empty:
            continue
        hz, hm = bool(shapely.has_z(g)), bool(shapely.has_m(g))
        raw = shapely.get_coordinates(g, include_z=hz, include_m=hm)
        z = raw[:, 2] if hz else np.zeros(len(raw))
        m = raw[:, -1] if hm else np.full(len(raw), np.nan)
        out.append(np.column_stack([raw[:, 0], raw[:, 1], z, m]).astype(float))
    return out


def line(pieces):
    """A MultiLineString ZM of parts; None when there are none."""
    pieces = [p for p in pieces if len(p) >= 2]
    if not pieces:
        return None
    body = ", ".join("(" + ", ".join(" ".join(_number(v) for v in row) for row in p) + ")" for p in pieces)
    return shapely.from_wkt(f"MULTILINESTRING ZM ({body})")


def point(x, y, z, m):
    return shapely.from_wkt(f"POINT ZM ({_number(x)} {_number(y)} {_number(z)} {_number(m)})")


def cumulative(part):
    """The distance of each vertex from the part's start, z counted."""
    if len(part) < 2:
        return np.zeros(len(part))
    steps = np.sqrt(((part[1:, :3] - part[:-1, :3]) ** 2).sum(axis=1))
    return np.concatenate([[0.0], np.cumsum(steps)])


def length(pieces):
    return float(sum(cumulative(p)[-1] for p in pieces if len(p)))


# ---- assembling a route from centerlines -----------------------------------------------------------------------------

def merge(shapes):
    """Centerline shapes merged into continuous parts ((n, 4) arrays, m NaN); a part ends at a gap or a branch."""
    lines = []
    for s in shapes:
        for p in parts(s):
            if len(p) >= 2:
                lines.append(shapely.from_wkt("LINESTRING Z (" + ", ".join(
                    f"{_number(r[0])} {_number(r[1])} {_number(r[2])}" for r in p) + ")"))
    if not lines:
        return []
    merged = linemerge(lines) if len(lines) > 1 else lines[0]
    return parts(merged)


def candidates(pieces, x, y, tolerance):
    """Every (part, distance along the route, offset) at which (x, y) lies within ``tolerance`` of the route, nearest
    first; each segment answers once. Distances along a route run on across its parts (a gap adds nothing)."""
    found, before = [], 0.0
    for k, p in enumerate(pieces):
        d = cumulative(p)
        for i in range(len(p) - 1):
            ax, ay, bx, by = p[i, 0], p[i, 1], p[i + 1, 0], p[i + 1, 1]
            dx, dy = bx - ax, by - ay
            span = dx * dx + dy * dy
            t = 0.0 if span == 0 else min(1.0, max(0.0, ((x - ax) * dx + (y - ay) * dy) / span))
            off = math.hypot(ax + t * dx - x, ay + t * dy - y)
            if off <= tolerance:
                found.append((k, before + d[i] + t * (d[i + 1] - d[i]), off, i, t))
        before += d[-1] if len(d) else 0.0
    # one answer per location: a vertex shared by two segments answers twice
    unique = []
    for c in sorted(found, key=lambda c: (c[0], c[1])):
        if not unique or unique[-1][0] != c[0] or abs(unique[-1][1] - c[1]) > 1e-9:
            unique.append(c)
        elif c[2] < unique[-1][2]:
            unique[-1] = c
    return unique


def orient(pieces, points, tolerance):
    """The parts each turned so that the calibration points on it increase along it, and ordered by their smallest
    measure; a part with fewer than two points keeps its digitized direction and goes after those that have them, in
    the order merged (INFERRED, ADR-252 §5.9 (d))."""
    keyed = []
    for k, p in enumerate(pieces):
        found = []
        for x, y, m in points:
            near = candidates([p], x, y, tolerance)
            if len(near) == 1:
                found.append((near[0][1], m))
        if len(found) >= 2:
            found.sort()
            ds, ms = np.array([f[0] for f in found]), np.array([f[1] for f in found])
            if np.corrcoef(ds, ms)[0, 1] < 0 if np.std(ds) > 0 and np.std(ms) > 0 else ms[-1] < ms[0]:
                p = p[::-1].copy()
            keyed.append((0, float(ms.min()), k, p))
        elif len(found) == 1:
            keyed.append((0, float(found[0][1]), k, p))
        else:
            keyed.append((1, 0.0, k, p))
    return [p for _, _, _, p in sorted(keyed, key=lambda e: (e[0], e[1], e[2]))]


def place(pieces, points, tolerance):
    """Each calibration point (x, y, m) placed at a (part, distance) of the route: points taken in measure order, each at
    the first of its candidate locations not before the previous point's, so that the start and end of a loop, which
    share a location, are told apart. Answers (placed [(part, distance, m)], unplaced [(x, y, m)])."""
    placed, unplaced, last = [], [], (-1, -math.inf)
    for x, y, m in sorted(points, key=lambda e: e[2]):
        near = candidates(pieces, x, y, tolerance)
        if not near:
            unplaced.append((x, y, m))
            continue
        later = [c for c in near if (c[0], c[1]) >= last]
        c = later[0] if later else min(near, key=lambda c: c[2])
        placed.append((c[0], c[1], m))
        last = (c[0], c[1])
    placed.sort(key=lambda e: (e[0], e[1]))
    return placed, unplaced


def calibrate(pieces, placed):
    """The parts with m set from placed calibration points: interpolated between the two that bracket a vertex in route
    order, extrapolated before the first and after the last by the nearest pair's measure-to-distance ratio (by 1 with a
    single point); NaN with none."""
    out = []
    pieces = _with_vertices(pieces, placed)
    keys = [(p, d) for p, d, _ in placed]
    before = 0.0
    for k, p in enumerate(pieces):
        q = p.copy()
        d = cumulative(p) + before
        for i, dist in enumerate(d):
            q[i, 3] = _measure_at(placed, keys, (k, dist))
        out.append(q)
        before = d[-1] if len(d) else before
    return out


def _with_vertices(pieces, placed):
    """The parts with a vertex added where a calibration point falls between two, so that the measure turns there."""
    out, before = [], 0.0
    for k, p in enumerate(pieces):
        d = cumulative(p)
        wanted = sorted(dist - before for part, dist, _ in placed if part == k)
        rows = [p[0]]
        for i in range(len(p) - 1):
            for w in wanted:
                if d[i] + 1e-9 < w < d[i + 1] - 1e-9:
                    t = (w - d[i]) / (d[i + 1] - d[i])
                    rows.append(p[i] + t * (p[i + 1] - p[i]))
            rows.append(p[i + 1])
        out.append(np.array(rows))
        before += d[-1] if len(d) else 0.0
    return out


def _measure_at(placed, keys, key):
    if not placed:
        return math.nan
    for (p, d, m) in placed:
        if (p, d) == key or (p == key[0] and abs(d - key[1]) < 1e-9):
            return m
    after = next((i for i, k in enumerate(keys) if k > key), None)
    if after is not None and after > 0:
        (_, d0, m0), (_, d1, m1) = placed[after - 1], placed[after]
        return m0 if d1 == d0 else m0 + (key[1] - d0) * (m1 - m0) / (d1 - d0)
    if len(placed) == 1:
        d0, m0 = placed[0][1], placed[0][2]
        return m0 + (key[1] - d0)
    pair = placed[:2] if after == 0 else placed[-2:]
    (_, d0, m0), (_, d1, m1) = pair
    ratio = 1.0 if d1 == d0 else (m1 - m0) / (d1 - d0)
    return m0 + (key[1] - d0) * ratio


# ---- reading measures ------------------------------------------------------------------------------------------------

def locate(pieces, x, y, tolerance):
    """[(measure, offset)] of (x, y) on a calibrated route, nearest first; empty when it is farther than ``tolerance``."""
    out = []
    for k, _, off, i, t in candidates(pieces, x, y, tolerance):
        p = pieces[k]
        out.append((float(p[i, 3] + t * (p[i + 1, 3] - p[i, 3])), off))
    return sorted(out, key=lambda e: e[1])


def at(pieces, m, tolerance=1e-9):
    """(x, y, z) at measure ``m``: the first location in route order where it falls, or None."""
    for p in pieces:
        for i in range(len(p) - 1):
            m0, m1 = p[i, 3], p[i + 1, 3]
            lo, hi = min(m0, m1), max(m0, m1)
            if math.isnan(m0) or math.isnan(m1) or not (lo - tolerance <= m <= hi + tolerance):
                continue
            t = 0.0 if m1 == m0 else (m - m0) / (m1 - m0)
            t = min(1.0, max(0.0, t))
            v = p[i, :3] + t * (p[i + 1, :3] - p[i, :3])
            return float(v[0]), float(v[1]), float(v[2])
    return None


def extent(pieces):
    """(smallest, largest) measure of a route, or None when it is not calibrated."""
    ms = [v for p in pieces for v in p[:, 3] if not math.isnan(v)]
    return (min(ms), max(ms)) if ms else None


def between(pieces, a, b):
    """The route between measures ``a`` and ``b`` as parts (the route's own direction), each segment clipped to the
    range; empty when the range misses the route."""
    lo, hi = min(a, b), max(a, b)
    out = []
    for p in pieces:
        current = []
        for i in range(len(p) - 1):
            m0, m1 = p[i, 3], p[i + 1, 3]
            if math.isnan(m0) or math.isnan(m1):
                continue
            s_lo, s_hi = min(m0, m1), max(m0, m1)
            if s_hi < lo or s_lo > hi or (s_hi == s_lo and not lo <= s_lo <= hi):
                if current:
                    out.append(np.array(current))
                    current = []
                continue
            if m1 == m0:
                t0, t1 = 0.0, 1.0
            else:
                t0 = min(1.0, max(0.0, ((lo if m1 > m0 else hi) - m0) / (m1 - m0)))
                t1 = min(1.0, max(0.0, ((hi if m1 > m0 else lo) - m0) / (m1 - m0)))
            v0 = p[i] + t0 * (p[i + 1] - p[i])
            v1 = p[i] + t1 * (p[i + 1] - p[i])
            if not current or not np.allclose(current[-1][:2], v0[:2]):
                if current:
                    out.append(np.array(current))
                current = [v0]
            current.append(v1)
            if t1 < 1.0:
                out.append(np.array(current))
                current = []
        if current:
            out.append(np.array(current))
    return [q for q in out if len(q) >= 2 and cumulative(q)[-1] > 0]


def covered(pieces, a, b, tolerance=1e-9):
    """Whether the route's measures reach both ends of [a, b]."""
    span = extent(pieces)
    return span is not None and span[0] - tolerance <= min(a, b) and max(a, b) <= span[1] + tolerance
