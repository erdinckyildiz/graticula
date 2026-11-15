"""Prototype (disposable): decode and encode the mobile geodatabase geometry blob, as observed 2026-11-10.

Layout, as read from blobs ArcGIS Pro wrote for known input:
  int32 LE  (srid << 8) | 0x64
  int32 LE  point count (including part separators)
  byte      type: point 0x04, multipoint 0x08, polyline 0x14, polygon 0x18; +2 if Z, +1 if M
  byte      flags: low nibble 1 point/multipoint, 4 polyline, 8 polygon; 0x40 = several parts (or several multipoint points)
  [if npoints > 10] varint xmin, ymin, xmax, ymax (absolute, integer grid)
  [if 0x40 and not multipoint] varint extra-part count, then part start indexes, delta-coded
  4 bytes   varint payload length, zero padded
  4 bytes   01, dims (Z=1, M=2) | 0x10 for polygons, 00, 04 for polygons else 00
  payload   x0 y0, then dx dy...; then z0 dz...; then m0 dm...   (signed varints, FileGDB style)
Parts (polyline paths, polygon outer rings) are separated by a point at raw (-1, 0); holes follow their ring with no separator.
"""
import struct

def sv_read(b, i):
    x = b[i]; i += 1; neg = bool(x & 0x40); v = x & 0x3F; sh = 6
    while x & 0x80:
        x = b[i]; i += 1; v |= (x & 0x7F) << sh; sh += 7
    return (-v if neg else v), i

def sv_write(v):
    neg = v < 0; v = -v if neg else v
    out = bytearray()
    first = (v & 0x3F) | (0x40 if neg else 0); v >>= 6
    if v: first |= 0x80
    out.append(first)
    while v:
        x = v & 0x7F; v >>= 7
        if v: x |= 0x80
        out.append(x)
    return bytes(out)

class Grid:
    def __init__(self, falsex, falsey, xyunits, falsez=-100000.0, zunits=10000.0, falsem=-100000.0, munits=10000.0):
        self.fx, self.fy, self.u, self.fz, self.zu, self.fm, self.mu = falsex, falsey, xyunits, falsez, zunits, falsem, munits
    def x(self, v): return int(round((v - self.fx) * self.u))
    def y(self, v): return int(round((v - self.fy) * self.u))
    def z(self, v): return int(round((v - self.fz) * self.zu))
    def m(self, v): return int(round((v - self.fm) * self.mu))
    def ux(self, r): return r / self.u + self.fx
    def uy(self, r): return r / self.u + self.fy
    def uz(self, r): return r / self.zu + self.fz
    def um(self, r): return r / self.mu + self.fm

BASE = {"point": 0x04, "multipoint": 0x08, "polyline": 0x14, "polygon": 0x18}
NIB = {"point": 0x01, "multipoint": 0x01, "polyline": 0x04, "polygon": 0x08}

def encode(kind, parts, srid, grid, hasz=False, hasm=False):
    """parts: list of parts; a part is a list of (x, y[, z][, m]) tuples. For polygons a part is one outer ring followed by
    its holes, as a list of rings: [[ring, hole, hole], [ring]]. For point/multipoint, parts = [[pt, pt, ...]]."""
    # flatten to a raw point list with separators
    raw = []  # (ix, iy, iz, im) or None for separator
    starts = []
    def add(p):
        z = p[2] if hasz else None
        m = p[3 if hasz else 2] if hasm else None
        raw.append((grid.x(p[0]), grid.y(p[1]), grid.z(z) if hasz else None, grid.m(m) if hasm else None))
    if kind in ("point", "multipoint"):
        for p in parts[0]: add(p)
    elif kind == "polyline":
        for k, path in enumerate(parts):
            if k: raw.append(None)
            starts.append(len(raw))
            for p in path: add(p)
    else:
        for k, rings in enumerate(parts):
            if k: raw.append(None)
            starts.append(len(raw))
            for ring in rings:
                for p in ring: add(p)
    n = len(raw)
    multi = (kind == "multipoint" and n > 1) or (kind in ("polyline", "polygon") and len(parts) > 1)
    t = BASE[kind] + (2 if hasz else 0) + (1 if hasm else 0)
    flags = NIB[kind] | (0x40 if multi else 0)
    out = bytearray(struct.pack("<iI", (srid << 8) | 0x64, n))
    out += bytes([t, flags])
    real = [r for r in raw if r is not None]
    if n > 10:
        out += sv_write(min(r[0] for r in real)) + sv_write(min(r[1] for r in real)) + sv_write(max(r[0] for r in real)) + sv_write(max(r[1] for r in real))
    if multi and kind != "multipoint":
        out += sv_write(len(starts) - 1)
        prev = 0
        for s in starts[1:]:
            out += sv_write(s - prev); prev = s
    payload = bytearray()
    px, py = 0, 0
    for r in raw:
        x, y = (-1, 0) if r is None else (r[0], r[1])
        payload += sv_write(x - px) + sv_write(y - py); px, py = x, y
    if hasz:
        pz = 0
        for r in raw:
            z = 0 if r is None else r[2]
            payload += sv_write(z - pz); pz = z
    if hasm:
        pm = 0
        for r in raw:
            m = 0 if r is None else r[3]
            payload += sv_write(m - pm); pm = m
    ln = sv_write(len(payload)); out += ln + bytes(4 - len(ln))
    dims = (1 if hasz else 0) | (2 if hasm else 0)
    if kind == "polygon": out += bytes([1, 0x10 | dims, 0, 4])
    else: out += bytes([1, dims, 0, 0])
    out += payload
    return bytes(out)

def decode(b):
    """Returns (srid, kind, hasz, hasm, raw points with None for separators)."""
    head, n = struct.unpack_from("<iI", b, 0)
    srid = head >> 8
    t, flags = b[8], b[9]
    kind = {0x04: "point", 0x08: "multipoint", 0x14: "polyline", 0x18: "polygon"}[t & ~0x03]
    hasz, hasm = bool(t & 2), bool(t & 1)
    i = 10
    if n > 10:
        for _ in range(4): _, i = sv_read(b, i)
    if flags & 0x40 and kind != "multipoint":
        k, i = sv_read(b, i)
        for _ in range(k): _, i = sv_read(b, i)
    i += 4 + 4
    xs, ys = [], []
    px = py = 0
    for _ in range(n):
        dx, i = sv_read(b, i); dy, i = sv_read(b, i); px += dx; py += dy; xs.append(px); ys.append(py)
    zs, ms = [None] * n, [None] * n
    if hasz:
        pz = 0
        for j in range(n): d, i = sv_read(b, i); pz += d; zs[j] = pz
    if hasm:
        pm = 0
        for j in range(n): d, i = sv_read(b, i); pm += d; ms[j] = pm
    assert i == len(b), (i, len(b))
    raw = [None if (xs[j], ys[j]) == (-1, 0) else (xs[j], ys[j], zs[j], ms[j]) for j in range(n)]
    return srid, kind, hasz, hasm, raw

def regroup(kind, raw):
    """raw grid points -> parts in the encode() shape (grid integers)."""
    if kind in ("point", "multipoint"): return [[r for r in raw]]
    parts, cur = [], []
    for r in raw:
        if r is None: parts.append(cur); cur = []
        else: cur.append(r)
    parts.append(cur)
    if kind == "polyline": return parts
    out = []
    for p in parts:
        rings, ring = [], []
        for r in p:
            ring.append(r)
            if len(ring) > 1 and r[:2] == ring[0][:2]: rings.append(ring); ring = []
        if ring: rings.append(ring)
        out.append(rings)
    return out
