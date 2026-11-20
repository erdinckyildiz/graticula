"""Geodesic buffers — D-397.

A geodesic buffer of distance *d* is the set of points whose distance *on the ellipsoid* from the shape is at most
*d*. It is built here from pieces, each buffered in its own azimuthal equidistant projection (AEQD) — PROJ's
ellipsoidal AEQD, which keeps every distance from its centre exact:

- a point is a piece centred on itself, so its circle is exact (only the polygon's vertices approximate it);
- a line, or a polygon's rings, is first densified in its own coordinates — ArcGIS Pro's GEODESIC is *shape
  preserving*: a segment is the straight line of the data's own coordinate system, densified, not a geodesic — and
  then cut into runs short enough that the AEQD's error, which grows with a run's length, stays small; a feature
  small enough is one piece;
- a polygon's buffer is the polygon with its rings' buffer added; a negative distance takes the rings' buffer away.

Each piece's buffer is projected back to the data's own reference and the pieces are joined there. Every vertex of
the result is checked against the ellipsoid by the tests (``test_geodesic_buffer.py``), and the result against
ArcGIS Pro 3.7's own (measured 2026-11-20; the tolerances are in that test's header).

Refused, by name, rather than answered wrong: a buffer that would cross the antimeridian or reach a pole, where a
polygon in longitude and latitude needs a cut this module does not make.
"""
from __future__ import annotations

import math

import numpy
import shapely

_SEGMENTS_PER_QUARTER = 64      # a circle of 256 vertices; Pro 3.7 writes about 315 for a 100 km circle


class GeodesicRefusal(ValueError):
    """A geodesic buffer this module will not build — said with the reason."""


def _crs(srid):
    from pyproj import CRS
    return CRS.from_epsg(int(srid))


class _Frame:
    """The data's reference, its geographic reference, the transformations between them, and the ellipsoid."""

    def __init__(self, srid):
        from pyproj import Geod, Transformer
        self.source = _crs(srid)
        self.geographic = self.source.geodetic_crs if self.source.is_projected else self.source
        self.projected = self.source.is_projected
        self.forward = Transformer.from_crs(self.source, self.geographic, always_xy=True)
        self.back = Transformer.from_crs(self.geographic, self.source, always_xy=True) if self.projected else None
        ellipsoid = self.geographic.ellipsoid
        self.a = ellipsoid.semi_major_metre
        self.rf = ellipsoid.inverse_flattening or 0.0
        self.geod = Geod(a=self.a, rf=self.rf) if self.rf else Geod(a=self.a, b=self.a)

    def aeqd(self, lon, lat):
        from pyproj import CRS, Transformer
        shape = f"+a={self.a} +rf={self.rf}" if self.rf else f"+R={self.a}"
        centred = CRS.from_proj4(f"+proj=aeqd +lat_0={lat} +lon_0={lon} {shape} +units=m +no_defs +type=crs")
        return (Transformer.from_crs(self.geographic, centred, always_xy=True),
                Transformer.from_crs(centred, self.geographic, always_xy=True))

    def lonlat(self, xy):
        lon, lat = self.forward.transform(xy[:, 0], xy[:, 1])
        return numpy.column_stack([lon, lat])


def _run_length(distance):
    """How long a run of a line may be, in metres. Off its centre an AEQD stretches distances across the radial
    direction by about ρ²/6R²; a run's buffer is measured from points within half a run of the centre, so the error
    grows with the run and with the distance. 20 km below a 20 km buffer, shorter for a longer one — sized so the
    tests' measured error stays below 0.01 % of the distance, and checked by them rather than trusted."""
    if distance <= 20000:
        return 20000.0
    return max(20000.0 * math.sqrt(20000.0 / distance), 2000.0)


def _paths(shape, frame, step):
    """The shape's points and its lines and rings densified in its own coordinates to at most ``step`` metres, each as
    an array of longitude and latitude."""
    points, lines = [], []

    def walk(geometry):
        if geometry.is_empty:
            return
        kind = geometry.geom_type
        if kind in ("MultiPolygon", "MultiLineString", "MultiPoint", "GeometryCollection"):
            for part in geometry.geoms:
                walk(part)
        elif kind == "Polygon":
            walk(geometry.exterior)
            for ring in geometry.interiors:
                walk(ring)
        elif kind == "Point":
            points.append(frame.lonlat(numpy.asarray([[geometry.x, geometry.y]])))
        else:
            xy = numpy.asarray(geometry.coords)[:, :2]
            ll = frame.lonlat(xy)
            metres = numpy.asarray(frame.geod.inv(ll[:-1, 0], ll[:-1, 1], ll[1:, 0], ll[1:, 1])[2], dtype=float)
            counts = numpy.maximum(1, numpy.ceil(metres / step)).astype(int)
            if counts.max() > 1:
                out = [xy[:1]]
                for a, b, n in zip(xy[:-1], xy[1:], counts):
                    t = (numpy.arange(1, n + 1) / n)[:, None]
                    out.append(a + (b - a) * t)
                ll = frame.lonlat(numpy.vstack(out))
            lines.append(ll)

    walk(shape)
    return points, lines


def _runs(path, limit, geod):
    """A densified path cut into runs of at most ``limit`` metres, each sharing its ends with the next."""
    metres = numpy.asarray(geod.inv(path[:-1, 0], path[:-1, 1], path[1:, 0], path[1:, 1])[2], dtype=float)
    runs, start, length = [], 0, 0.0
    for i, step in enumerate(metres):
        if length + step > limit and i > start:
            runs.append(path[start:i + 1])
            start, length = i, 0.0
        length += step
    runs.append(path[start:])
    return runs


def _piece(parts, distance, frame):
    """The buffer of some points and runs (arrays of longitude and latitude), built in one AEQD at their middle, in
    the data's own reference."""
    every = numpy.vstack(parts)
    lo, hi = every.min(axis=0), every.max(axis=0)
    middle = (lo + hi) / 2
    there, home = frame.aeqd(middle[0], middle[1])
    local = []
    for part in parts:
        x, y = there.transform(part[:, 0], part[:, 1])
        local.append(shapely.Point(x[0], y[0]) if len(part) == 1 else shapely.LineString(numpy.column_stack([x, y])))
    disc = shapely.union_all([g.buffer(distance, quad_segs=_SEGMENTS_PER_QUARTER) for g in local])
    poles = numpy.column_stack(there.transform([middle[0], middle[0]], [90.0, -90.0]))
    if any(disc.contains(shapely.Point(p)) for p in poles):
        raise GeodesicRefusal("the buffer would reach a pole")

    def homeward(xy):
        lon, lat = home.transform(xy[:, 0], xy[:, 1])
        if numpy.any(numpy.abs(numpy.diff(lon)) > 180):
            raise GeodesicRefusal("the buffer would cross the antimeridian")
        if frame.back is None:
            return numpy.column_stack([lon, lat])
        x, y = frame.back.transform(lon, lat)
        return numpy.column_stack([x, y])

    return shapely.transform(disc, homeward)


def buffer(shape, distance, srid):
    """``shape``, in EPSG ``srid``, buffered by ``distance`` metres on the ellipsoid, in the same reference."""
    if shape is None or shape.is_empty or distance == 0:
        return shape
    areal = shape.geom_type in ("Polygon", "MultiPolygon")
    if distance < 0 and not areal:
        return shapely.Polygon()
    frame = _Frame(srid)
    size = abs(distance)
    limit = _run_length(size)
    points, lines = _paths(shape, frame, limit / 16)

    # A feature whose whole extent is under a run is one piece, so a city's buildings cost one projection each.
    every = numpy.vstack(points + lines)
    lo, hi = every.min(axis=0), every.max(axis=0)
    extent = frame.geod.inv(lo[0], lo[1], hi[0], hi[1])[2]
    if extent <= limit and not points:
        pieces = [_piece(lines, size, frame)]
    else:
        pieces = [_piece([p], size, frame) for p in points]
        for line in lines:
            pieces += [_piece([run], size, frame) for run in _runs(line, limit, frame.geod)]
    around = shapely.union_all(pieces)
    if not areal:
        return around
    return shapely.union(shape, around) if distance > 0 else shapely.difference(shape, around)


def distances(lonlat, shape, srid, step):
    """The geodesic distance in metres from each (longitude, latitude) to ``shape`` — to a polygon's rings, a line or
    points — over the shape densified to ``step`` of its own units, each measured in an AEQD centred on the point,
    which is exact from its centre. Independent of ``buffer``'s construction; the tests check a buffer's vertices
    with it."""
    frame = _Frame(srid)
    dense = shapely.segmentize(shape, step)
    target = dense.boundary if dense.geom_type in ("Polygon", "MultiPolygon") else dense
    target = shapely.transform(target, frame.lonlat)
    out = []
    for lon, lat in lonlat:
        there, _ = frame.aeqd(lon, lat)

        def local(xy, there=there):
            x, y = there.transform(xy[:, 0], xy[:, 1])
            return numpy.column_stack([x, y])

        out.append(shapely.Point(0, 0).distance(shapely.transform(target, local)))
    return numpy.asarray(out)
