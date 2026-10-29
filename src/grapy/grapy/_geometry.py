"""arcpy's geometry objects, by their documented names, held as shapely geometries — ADR-223 §5.2."""
from __future__ import annotations

import shapely
from shapely.geometry import LineString, MultiLineString, MultiPoint, MultiPolygon, Polygon as ShapelyPolygon
from shapely.geometry import Point as ShapelyPoint

from ._reference import SpatialReference, reference_of

# Linear units in metres, by the names LinearUnitConversionFactor and getLength document. The international units are
# exact by definition (1 ft = 0.3048 m); the US survey foot is 1200/3937 m. In getLength's list the bare names (Feet,
# Miles, Yards, Inches) are the US survey units, as that page says. INFERRED: a point is 1/72 of an international inch.
_US_FOOT = 1200 / 3937
_LINEAR = {
    "KILOMETERS": 1000.0, "METERS": 1.0, "DECIMETERS": 0.1, "CENTIMETERS": 0.01, "MILLIMETERS": 0.001,
    "NAUTICALMILESINT": 1852.0, "MILESINT": 1609.344, "YARDSINT": 0.9144, "FEETINT": 0.3048, "INCHESINT": 0.0254,
    "MILESUS": 5280 * _US_FOOT, "YARDSUS": 3 * _US_FOOT, "FEETUS": _US_FOOT, "INCHESUS": _US_FOOT / 12,
    "MILES": 5280 * _US_FOOT, "YARDS": 3 * _US_FOOT, "FEET": _US_FOOT, "INCHES": _US_FOOT / 12,
    "POINTS": 0.0254 / 72,
}
# Areal units in square metres, by the names ArealUnitConversionFactor and getArea document.
_AREAL = {
    "SQUAREKILOMETERS": 1e6, "HECTARES": 1e4, "ARES": 100.0, "SQUAREMETERS": 1.0, "SQUAREDECIMETERS": 0.01,
    "SQUARECENTIMETERS": 1e-4, "SQUAREMILLIMETERS": 1e-6,
    "SQUAREMILESINT": 1609.344 ** 2, "ACRESINT": 4046.8564224, "SQUAREYARDSINT": 0.9144 ** 2,
    "SQUAREFEETINT": 0.3048 ** 2, "SQUAREINCHESINT": 0.0254 ** 2,
    "SQUAREMILESUS": (5280 * _US_FOOT) ** 2, "ACRESUS": 43560 * _US_FOOT ** 2, "SQUAREYARDSUS": (3 * _US_FOOT) ** 2,
    "SQUAREFEETUS": _US_FOOT ** 2, "SQUAREINCHESUS": (_US_FOOT / 12) ** 2,
    "SQUAREMILES": (5280 * _US_FOOT) ** 2, "ACRES": 43560 * _US_FOOT ** 2, "SQUAREYARDS": (3 * _US_FOOT) ** 2,
    "SQUAREFEET": _US_FOOT ** 2, "SQUAREINCHES": (_US_FOOT / 12) ** 2,
}
_REFUSED_UNITS = {"NAUTICALMILESUS": "the US survey nautical mile's length is not given in the documentation",
                  "NAUTICALMILES": "the US survey nautical mile's length is not given in the documentation",
                  "DECIMALDEGREES": "a degree has no constant length", "UNKNOWN": "an unknown unit has no length"}


def _unit(table, name, function):
    key = str(name).replace(" ", "").replace("_", "").upper()
    if key in _REFUSED_UNITS:
        from ._runtime import NotBuiltError
        raise NotBuiltError(f"arcpy.{function}: the unit '{name}' is not built in grapy — {_REFUSED_UNITS[key]}.")
    if key not in table:
        raise ValueError(f"arcpy.{function}: '{name}' is not a unit it takes.")
    return table[key]


def LinearUnitConversionFactor(from_unit, to_unit):
    """The factor taking a distance in ``from_unit`` to ``to_unit`` — 620.5 MilesInt times the factor to Kilometers is
    998.597952, the page's own example."""
    return _unit(_LINEAR, from_unit, "LinearUnitConversionFactor") / _unit(_LINEAR, to_unit, "LinearUnitConversionFactor")


def ArealUnitConversionFactor(from_unit, to_unit):
    """The factor taking an area in ``from_unit`` to ``to_unit`` — 10,000 Hectares are 100 SquareKilometers."""
    return _unit(_AREAL, from_unit, "ArealUnitConversionFactor") / _unit(_AREAL, to_unit, "ArealUnitConversionFactor")


class Point:
    """A coordinate, not a geometry — arcpy's Point."""

    def __init__(self, X=0.0, Y=0.0, Z=None, M=None, ID=0):
        self.X, self.Y, self.Z, self.M, self.ID = X, Y, Z, M, ID

    def __repr__(self):
        return f"Point({self.X}, {self.Y})"

    def equals(self, other):
        return isinstance(other, Point) and (self.X, self.Y) == (other.X, other.Y)


class Array(list):
    """arcpy's Array: a list of Points, or of Arrays for parts."""

    def add(self, value):
        self.append(value)

    def getObject(self, index):
        return self[index]

    def removeAll(self):
        self.clear()

    @property
    def count(self):
        return len(self)


class Geometry:
    """A geometry with its reference. ``Geometry(shapely)`` is grapy's own; the arcpy constructors are the subclasses."""

    type = "geometry"

    def __init__(self, shape=None, spatial_reference=None):
        self._shape = shape
        self.spatialReference = reference_of(spatial_reference)

    # -- what arcpy reads off any geometry
    @property
    def WKT(self):
        return shapely.to_wkt(self._shape)

    @property
    def WKB(self):
        return shapely.to_wkb(self._shape)

    @property
    def JSON(self):
        """Esri JSON, as arcpy's geometry JSON is, with the reference's wkid."""
        import json
        from .conversion import _to_esri
        document = _to_esri(self._shape) or {}
        if self.spatialReference is not None:
            document["spatialReference"] = {"wkid": self.spatialReference.factoryCode}
        return json.dumps(document)

    @property
    def __geo_interface__(self):
        return shapely.geometry.mapping(self._shape)

    @property
    def area(self):
        return self._shape.area

    @property
    def length(self):
        return self._shape.length

    @property
    def centroid(self):
        c = self._shape.centroid
        return Point(c.x, c.y)

    @property
    def trueCentroid(self):
        return self.centroid

    @property
    def labelPoint(self):
        p = self._shape.representative_point()
        return Point(p.x, p.y)

    @property
    def firstPoint(self):
        coordinates = list(_coordinates(self._shape))
        return Point(*coordinates[0][:2]) if coordinates else None

    @property
    def lastPoint(self):
        coordinates = list(_coordinates(self._shape))
        return Point(*coordinates[-1][:2]) if coordinates else None

    @property
    def extent(self):
        from ._reference import Extent
        x0, y0, x1, y1 = self._shape.bounds
        return Extent(x0, y0, x1, y1, self.spatialReference)

    @property
    def isMultipart(self):
        return isinstance(self._shape, (MultiPoint, MultiLineString, MultiPolygon)) and len(self._shape.geoms) > 1

    @property
    def partCount(self):
        return len(self._shape.geoms) if hasattr(self._shape, "geoms") else 1

    @property
    def pointCount(self):
        return len(list(_coordinates(self._shape)))

    def _measure(self, method, tool):
        method = str(method or "GEODESIC").upper()
        if method in ("GREAT_ELLIPTIC", "LOXODROME", "PRESERVE_SHAPE"):
            from ._runtime import NotBuiltError
            raise NotBuiltError(f"Geometry.{tool}: method '{method}' is not built in grapy; GEODESIC and PLANAR are.")
        if method not in ("GEODESIC", "PLANAR"):
            raise ValueError(f"Geometry.{tool}: '{method}' is not a measurement method.")
        geographic = self.spatialReference is not None and self.spatialReference.type == "Geographic"
        if method == "PLANAR" and geographic:
            raise ValueError(f"Geometry.{tool}: PLANAR is only available in a projected coordinate system.")
        if method == "GEODESIC" and self.spatialReference is None:
            raise ValueError(f"Geometry.{tool}: GEODESIC needs a spatial reference.")
        return method

    def _on_spheroid(self):
        """The shape in longitude and latitude on the reference's own geographic system, and that ellipsoid's Geod."""
        from pyproj import CRS
        crs = CRS.from_epsg(self.spatialReference.factoryCode)
        geographic = crs.geodetic_crs
        moved = self.projectAs(SpatialReference(geographic.to_epsg())) if crs.is_projected else self
        return moved._shape, geographic.get_geod()

    def getArea(self, method="GEODESIC", units=None):
        """The area by GEODESIC (on the reference's ellipsoid) or PLANAR measurement. With no units, a PLANAR area is in
        the reference's units squared and a GEODESIC one in square metres, as documented. GREAT_ELLIPTIC, LOXODROME and
        PRESERVE_SHAPE are refused."""
        method = self._measure(method, "getArea")
        if method == "GEODESIC":
            shape, geod = self._on_spheroid()
            area = abs(geod.geometry_area_perimeter(shape)[0])
            per_metre = 1.0
        else:
            area = self._shape.area
            per_metre = (self.spatialReference.metersPerUnit if self.spatialReference else 1.0) ** 2
        if units in (None, ""):
            return area
        return area * per_metre / _unit(_AREAL, units, "Geometry.getArea")

    def getLength(self, method="GEODESIC", units=None):
        """The length by GEODESIC or PLANAR measurement; units as getArea's."""
        method = self._measure(method, "getLength")
        if method == "GEODESIC":
            shape, geod = self._on_spheroid()
            length = geod.geometry_length(shape)
            per_metre = 1.0
        else:
            length = self._shape.length
            per_metre = self.spatialReference.metersPerUnit if self.spatialReference else 1.0
        if units in (None, ""):
            return length
        return length * per_metre / _unit(_LINEAR, units, "Geometry.getLength")

    # -- relations
    def contains(self, other, relation=None):
        """CLEMENTINI (the default): the interiors meet. PROPER: the boundaries do not meet either. INFERRED: BOUNDARY,
        "no restrictions for interiors or boundaries", is covers — every point of the other is in this one."""
        relation = str(relation or "CLEMENTINI").upper()
        a, b = self._shape, _shape_of(other)
        if relation == "PROPER":
            return a.contains_properly(b)
        if relation == "BOUNDARY":
            return a.covers(b)
        return a.contains(b)

    def within(self, other, relation=None):
        """The opposite of contains, with the same relations."""
        return _wrap(_shape_of(other), self.spatialReference).contains(self, relation)

    def crosses(self, other):
        return self._shape.crosses(_shape_of(other))

    def overlaps(self, other):
        return self._shape.overlaps(_shape_of(other))

    def touches(self, other):
        return self._shape.touches(_shape_of(other))

    def disjoint(self, other):
        return self._shape.disjoint(_shape_of(other))

    def equals(self, other):
        return self._shape.equals(_shape_of(other))

    def distanceTo(self, other):
        return self._shape.distance(_shape_of(other))

    # -- operations, each answering a geometry of the same reference
    def buffer(self, distance):
        return _wrap(self._shape.buffer(distance), self.spatialReference)

    def clip(self, envelope):
        return _wrap(shapely.clip_by_rect(self._shape, envelope.XMin, envelope.YMin, envelope.XMax, envelope.YMax),
                     self.spatialReference)

    def convexHull(self):
        return _wrap(self._shape.convex_hull, self.spatialReference)

    def difference(self, other):
        return _wrap(self._shape.difference(_shape_of(other)), self.spatialReference)

    def intersect(self, other, dimension=None):
        """The intersection, of the dimension asked: 1 points, 2 lines, 4 polygons. Built where the dimension is the
        one two such inputs meet in — the lower of the two; a lower one (where two polygons' boundaries cross, say) is
        refused, since the documentation does not say which points or lines it means."""
        b = _shape_of(other)
        natural = min(_dimension(self._shape), _dimension(b))
        wanted = {1: 0, 2: 1, 4: 2}.get(int(dimension)) if dimension not in (None, "") else natural
        if wanted is None:
            raise ValueError("Geometry.intersect: dimension is 1 (points), 2 (lines) or 4 (polygons).")
        if wanted > natural:
            raise ValueError("Geometry.intersect: two inputs cannot meet in a higher dimension than the lower of theirs.")
        if wanted < natural:
            from ._runtime import NotBuiltError
            raise NotBuiltError("Geometry.intersect: a dimension lower than the inputs' own is not built in grapy.")
        result = self._shape.intersection(b)
        parts = [g for g in getattr(result, "geoms", [result]) if not g.is_empty and _dimension(g) == wanted]
        empty = {0: MultiPoint(), 1: MultiLineString(), 2: MultiPolygon()}[wanted]
        return _wrap(shapely.union_all(parts) if parts else empty, self.spatialReference)

    def symmetricDifference(self, other):
        return _wrap(self._shape.symmetric_difference(_shape_of(other)), self.spatialReference)

    def union(self, other):
        return _wrap(self._shape.union(_shape_of(other)), self.spatialReference)

    def generalize(self, max_offset):
        return _wrap(self._shape.simplify(max_offset, preserve_topology=True), self.spatialReference)

    def densify(self, method, distance, deviation=None):
        """DISTANCE: vertices added so that no segment is longer than ``distance``, "evenly distributed along the
        original segment". GEODESIC on a geographic reference: each segment replaced by points on its geodesic, evenly
        spaced at most ``distance`` metres apart. ANGLE is refused: grapy's segments are straight, and the documentation
        does not say what ANGLE does to a straight one."""
        import math
        kind = str(method).upper()
        if kind == "DISTANCE":
            return _wrap(shapely.segmentize(self._shape, float(distance)), self.spatialReference)
        from ._runtime import NotBuiltError
        if kind != "GEODESIC":
            raise NotBuiltError(f"Geometry.densify: method '{method}' is not built in grapy; DISTANCE and GEODESIC are.")
        if self.spatialReference is None or self.spatialReference.type != "Geographic":
            raise NotBuiltError("Geometry.densify: GEODESIC is built for a geographic reference only in grapy.")
        _, geod = self._on_spheroid()

        def along(coordinates):
            out = [tuple(coordinates[0][:2])]
            for first, second in zip(coordinates, coordinates[1:]):
                (x0, y0), (x1, y1) = first[:2], second[:2]
                count = max(int(math.ceil(geod.inv(x0, y0, x1, y1)[2] / float(distance))) - 1, 0)
                if count:
                    out += [tuple(p) for p in geod.npts(x0, y0, x1, y1, count)]
                out.append((x1, y1))
            return out
        return _wrap(_map_lines(self._shape, along), self.spatialReference)

    def projectAs(self, spatial_reference, transformation_name=None):
        """The geometry in another reference. A named transformation is refused: grapy's references are EPSG codes, and
        an Esri transformation name does not name a pyproj pipeline. INFERRED: with none named, pyproj's best available
        transformation is used, where ArcGIS Pro applies none."""
        if transformation_name not in (None, ""):
            from ._runtime import NotBuiltError
            raise NotBuiltError(f"Geometry.projectAs: transformation_name '{transformation_name}' is not built in grapy.")
        target = reference_of(spatial_reference)
        if self.spatialReference is None or target is None or self.spatialReference.factoryCode == target.factoryCode:
            return _wrap(self._shape, target or self.spatialReference)
        import numpy
        from pyproj import Transformer
        move = Transformer.from_crs(self.spatialReference.factoryCode, target.factoryCode, always_xy=True).transform
        return _wrap(shapely.transform(self._shape, lambda xy: numpy.column_stack(move(xy[:, 0], xy[:, 1]))), target)

    # -- parts, as arcpy reads them

    def getPart(self, index=None):
        """An Array of Points for one part, or an Array of such Arrays for every part. A polygon part lists its outer
        ring and then each hole, a None between rings. INFERRED: the None separator, arcpy's convention for rings in a
        part, which its page does not spell out."""
        def points(part):
            if isinstance(part, ShapelyPolygon):
                out = Array(Point(*c[:2]) for c in part.exterior.coords)
                for hole in part.interiors:
                    out.append(None)
                    out.extend(Point(*c[:2]) for c in hole.coords)
                return out
            return Array(Point(*c[:2]) for c in part.coords)
        parts = list(self._shape.geoms) if hasattr(self._shape, "geoms") else [self._shape]
        if index is None:
            return Array(points(p) for p in parts)
        return points(parts[int(index)])

    def __getitem__(self, index):
        return self.getPart(index)

    def __iter__(self):
        return iter(self.getPart())

    def boundary(self):
        """A polygon's boundary is a polyline; a polyline's, the multipoint of its endpoints; a point's, empty."""
        return _wrap(self._shape.boundary, self.spatialReference)

    def _line(self, tool):
        """The geometry as one LineString, for the measures along a line."""
        shape = self._shape
        if shape.geom_type == "MultiLineString":
            if len(shape.geoms) != 1:
                from ._runtime import NotBuiltError
                raise NotBuiltError(f"Polyline.{tool}: a multipart line is not built in grapy; how parts are measured "
                                    "in turn is not documented.")
            shape = shape.geoms[0]
        if shape.geom_type != "LineString":
            raise ValueError(f"Polyline.{tool} takes a polyline.")
        return shape

    def measureOnLine(self, in_point, use_percentage=False):
        """The distance from the line's start to the point on it nearest ``in_point``, or that as a ratio of its length."""
        line = self._line("measureOnLine")
        return line.project(_shape_of(_point_of(in_point)), normalized=bool(use_percentage))

    def positionAlongLine(self, value, use_percentage=False, geodesic=False):
        """The point ``value`` along the line — past the end, the end; before the start, the start, as documented.
        GEODESIC is refused. INFERRED: a planar distance is in the reference's units (the page says metres, but a
        planar measure on a projected line is in its units)."""
        if geodesic:
            from ._runtime import NotBuiltError
            raise NotBuiltError("Polyline.positionAlongLine: geodesic=True is not built in grapy.")
        line = self._line("positionAlongLine")
        return _wrap(line.interpolate(float(value), normalized=bool(use_percentage)), self.spatialReference)

    def segmentAlongLine(self, start_measure, end_measure, use_percentage=False):
        """The polyline between two measures along the line."""
        from shapely.ops import substring
        line = self._line("segmentAlongLine")
        piece = substring(line, float(start_measure), float(end_measure), normalized=bool(use_percentage))
        return _wrap(piece if piece.geom_type != "Point" else LineString([piece, piece]), self.spatialReference)

    def queryPointAndDistance(self, in_point, as_percentage=False):
        """(nearest point on the line, distance along the line to it, distance from in_point to it, right side): the
        side is right of the line's direction at the nearest segment. INFERRED: a point on the line is not right of it."""
        line = self._line("queryPointAndDistance")
        point = _shape_of(_point_of(in_point))
        along = line.project(point)
        nearest = line.interpolate(along)
        coordinates = list(line.coords)
        best, right = None, False
        for a, b in zip(coordinates, coordinates[1:]):
            gap = LineString([a, b]).distance(point)
            if best is None or gap < best:
                best = gap
                right = ((b[0] - a[0]) * (point.y - a[1]) - (b[1] - a[1]) * (point.x - a[0])) < 0
        measure = along / line.length if as_percentage and line.length else along
        return (_wrap(nearest, self.spatialReference), measure, point.distance(nearest), bool(right))

    def snapToLine(self, in_point):
        """The point on this geometry nearest ``in_point`` — on a polygon, on its boundary (INFERRED)."""
        from shapely.ops import nearest_points
        target = self._shape.boundary if _dimension(self._shape) == 2 else self._shape
        return _wrap(nearest_points(target, _shape_of(_point_of(in_point)))[0], self.spatialReference)

    def reverseOrientation(self):
        """The polyline with its vertices in reverse order."""
        return _wrap(shapely.reverse(self._shape), self.spatialReference)

    def cut(self, cutter):
        """[left, right]: the pieces left and right of the cutting polyline, by its direction. A part the cutter does
        not reach is on the right, and a geometry that is not cut answers [None, itself], as documented.
        INFERRED: a piece's side is the side of a point inside it (a polygon's representative point, a line piece's
        midpoint) relative to the nearest segment of the cutter."""
        from shapely.ops import split
        blade = _shape_of(cutter)
        pieces = list(split(self._shape, blade).geoms)
        if len(pieces) <= 1:
            return [None, _wrap(self._shape, self.spatialReference)]
        segments = [LineString([a, b]) for line in getattr(blade, "geoms", [blade]) for a, b in zip(line.coords, line.coords[1:])]
        left, right = [], []
        for piece in pieces:
            if piece.distance(blade) > 0:
                right.append(piece)
                continue
            inside = piece.representative_point() if _dimension(piece) == 2 else piece.interpolate(0.5, normalized=True)
            segment = min(segments, key=lambda s: s.distance(inside))
            (ax, ay), (bx, by) = segment.coords
            side = (bx - ax) * (inside.y - ay) - (by - ay) * (inside.x - ax)
            (left if side > 0 else right).append(piece)
        merge = (lambda ps: shapely.union_all(ps) if ps else None)
        return [_wrap(merge(left), self.spatialReference) if left else None,
                _wrap(merge(right), self.spatialReference) if right else None]

    # -- affine changes

    def move(self, dx=0.0, dy=0.0, dz=0.0):
        """The geometry moved by dx and dy. dz needs z-values, which grapy's geometries do not hold, and is refused."""
        if dz:
            from ._runtime import NotBuiltError
            raise NotBuiltError("Geometry.move: dz is not built in grapy; its geometries have no z-values.")
        from shapely import affinity
        return _wrap(affinity.translate(self._shape, float(dx), float(dy)), self.spatialReference)

    def rotate(self, origin=None, rotation_angle=0.0):
        """The geometry turned ``rotation_angle`` radians around ``origin`` (default 0, 0). INFERRED: a positive angle
        turns counterclockwise, the mathematical sense; the page does not say."""
        from shapely import affinity
        o = _point_of(origin) if origin is not None else Point(0.0, 0.0)
        o = _shape_of(o)
        return _wrap(affinity.rotate(self._shape, float(rotation_angle), origin=(o.x, o.y), use_radians=True),
                     self.spatialReference)

    def scale(self, origin=None, sx=1.0, sy=1.0, sz=1.0):
        """Each vertex moved to ``(x − origin_x)·sx + origin_x`` and likewise in y, as the page's formula. sz needs
        z-values and is refused unless 1."""
        if float(sz) != 1.0:
            from ._runtime import NotBuiltError
            raise NotBuiltError("Geometry.scale: sz is not built in grapy; its geometries have no z-values.")
        from shapely import affinity
        o = _shape_of(_point_of(origin) if origin is not None else Point(0.0, 0.0))
        return _wrap(affinity.scale(self._shape, float(sx), float(sy), origin=(o.x, o.y)), self.spatialReference)

    # -- angle and distance

    def _method(self, method, tool):
        method = str(method or "GEODESIC").upper()
        if method in ("GREAT_ELLIPTIC", "LOXODROME"):
            from ._runtime import NotBuiltError
            raise NotBuiltError(f"PointGeometry.{tool}: method '{method}' is not built in grapy; GEODESIC and PLANAR are.")
        return self._measure(method, tool)

    def angleAndDistanceTo(self, other, method="GEODESIC"):
        """(angle in degrees, distance in metres) to another point, or to a polygon's centroid. GEODESIC: the forward
        azimuth and length of the geodesic on the reference's ellipsoid. INFERRED: the angle is measured clockwise from
        north, the geodesic azimuth's convention, and PLANAR uses the same convention on the grid, its distance turned
        into metres by the reference's unit."""
        import math
        method = self._method(method, "angleAndDistanceTo")
        target = _wrap(_shape_of(other), self.spatialReference) if not isinstance(other, Geometry) else other
        if target.spatialReference is not None and self.spatialReference is not None and \
                target.spatialReference.factoryCode != self.spatialReference.factoryCode:
            target = target.projectAs(self.spatialReference)
        there = target._shape.centroid if _dimension(target._shape) == 2 else target._shape
        here = self._shape
        if method == "PLANAR":
            dx, dy = there.x - here.x, there.y - here.y
            unit = self.spatialReference.metersPerUnit if self.spatialReference else 1.0
            return (math.degrees(math.atan2(dx, dy)), math.hypot(dx, dy) * unit)
        start, geod = self._on_spheroid()
        end = _wrap(there, self.spatialReference)._on_spheroid()[0]
        azimuth, _, distance = geod.inv(start.x, start.y, end.x, end.y)
        return (azimuth, distance)

    def pointFromAngleAndDistance(self, angle, distance, method="GEODESIC"):
        """The point ``distance`` away at ``angle`` degrees. GEODESIC: along the geodesic of that forward azimuth,
        ``distance`` in metres, as the page's return value says. PLANAR: on the grid, in the reference's units.
        INFERRED: the angle clockwise from north, as angleAndDistanceTo's."""
        import math
        method = self._method(method, "pointFromAngleAndDistance")
        here = self._shape
        if method == "PLANAR":
            a = math.radians(float(angle))
            return _wrap(ShapelyPoint(here.x + float(distance) * math.sin(a), here.y + float(distance) * math.cos(a)),
                         self.spatialReference)
        start, geod = self._on_spheroid()
        lon, lat, _ = geod.fwd(start.x, start.y, float(angle), float(distance))
        from pyproj import CRS
        geographic = SpatialReference(CRS.from_epsg(self.spatialReference.factoryCode).geodetic_crs.to_epsg())
        return _wrap(ShapelyPoint(lon, lat), geographic).projectAs(self.spatialReference)

    def __repr__(self):
        return f"<{type(self).__name__} {self.WKT[:60]}>"


class PointGeometry(Geometry):
    type = "point"

    def __init__(self, inputs=None, spatial_reference=None, has_z=False, has_m=False):
        if isinstance(inputs, Point):
            inputs = ShapelyPoint(inputs.X, inputs.Y)
        super().__init__(inputs, spatial_reference)


class Multipoint(Geometry):
    type = "multipoint"

    def __init__(self, inputs=None, spatial_reference=None, has_z=False, has_m=False):
        if isinstance(inputs, (Array, list)):
            inputs = MultiPoint([(p.X, p.Y) for p in inputs])
        super().__init__(inputs, spatial_reference)


class Polyline(Geometry):
    type = "polyline"

    def __init__(self, inputs=None, spatial_reference=None, has_z=False, has_m=False):
        if isinstance(inputs, (Array, list)):
            parts = inputs if inputs and isinstance(inputs[0], (Array, list)) else [inputs]
            inputs = MultiLineString([[(p.X, p.Y) for p in part] for part in parts])
        super().__init__(inputs, spatial_reference)


class Polygon(Geometry):
    type = "polygon"

    def __init__(self, inputs=None, spatial_reference=None, has_z=False, has_m=False):
        if isinstance(inputs, (Array, list)):
            parts = inputs if inputs and isinstance(inputs[0], (Array, list)) else [inputs]
            inputs = shapely.make_valid(MultiPolygon([ShapelyPolygon([(p.X, p.Y) for p in part]) for part in parts]))
        super().__init__(inputs, spatial_reference)


def _shape_of(other):
    return other._shape if isinstance(other, Geometry) else other


def _coordinates(shape):
    if shape is None or shape.is_empty:
        return
    if hasattr(shape, "geoms"):
        for part in shape.geoms:
            yield from _coordinates(part)
    elif isinstance(shape, ShapelyPolygon):
        yield from shape.exterior.coords
        for ring in shape.interiors:
            yield from ring.coords
    else:
        yield from shape.coords


def _wrap(shape, spatial_reference):
    """A shapely geometry as the arcpy class of its kind."""
    if shape is None:
        return None
    kind = shape.geom_type
    if kind == "Point":
        return PointGeometry(shape, spatial_reference)
    if kind == "MultiPoint":
        return Multipoint(shape, spatial_reference)
    if kind in ("LineString", "MultiLineString", "LinearRing"):
        return Polyline(shape, spatial_reference)
    if kind in ("Polygon", "MultiPolygon"):
        return Polygon(shape, spatial_reference)
    return Geometry(shape, spatial_reference)


def wrap(shape, spatial_reference):
    return _wrap(shape, spatial_reference)


def _point_of(value):
    """A shapely point from a Point, a PointGeometry, a shapely point or an (x, y) pair."""
    if isinstance(value, Geometry):
        return value._shape
    if isinstance(value, Point):
        return ShapelyPoint(value.X, value.Y)
    if isinstance(value, (tuple, list)):
        return ShapelyPoint(*value[:2])
    return value


def _dimension(shape):
    kind = shape.geom_type
    if kind in ("Point", "MultiPoint"):
        return 0
    if kind in ("LineString", "MultiLineString", "LinearRing"):
        return 1
    if kind in ("Polygon", "MultiPolygon"):
        return 2
    return max((_dimension(g) for g in getattr(shape, "geoms", [])), default=0)


def _map_lines(shape, change):
    """The shape with each line's or ring's coordinates passed through ``change``."""
    kind = shape.geom_type
    if kind == "LineString":
        return LineString(change(list(shape.coords)))
    if kind == "MultiLineString":
        return MultiLineString([change(list(g.coords)) for g in shape.geoms])
    if kind == "Polygon":
        return ShapelyPolygon(change(list(shape.exterior.coords)), [change(list(r.coords)) for r in shape.interiors])
    if kind == "MultiPolygon":
        return MultiPolygon([_map_lines(g, change) for g in shape.geoms])
    return shape


# ---- geometry from text, binary, JSON, geohash and coordinate notation


def FromWKT(wkt_string, spatial_reference=None):
    """A geometry from well-known text, in the reference given."""
    return _wrap(shapely.from_wkt(wkt_string), reference_of(spatial_reference))


def FromWKB(byte_array):
    """A geometry from well-known binary; WKB carries no reference, so it has none."""
    return _wrap(shapely.from_wkb(bytes(byte_array)), None)


def AsShape(geojson_struct, esri_json=False):
    """A geometry from GeoJSON (in WGS 84, as documented) or, with ``esri_json``, from Esri JSON in its own reference;
    an Esri JSON feature set answers a FeatureSet."""
    import json
    document = json.loads(geojson_struct) if isinstance(geojson_struct, str) else geojson_struct
    if not esri_json:
        return _wrap(shapely.geometry.shape(document), SpatialReference(4326))
    if "features" in document:
        from . import FeatureSet
        return FeatureSet._from_esri_json(document)
    from .conversion import _from_esri
    reference = document.get("spatialReference") or {}
    code = reference.get("latestWkid") or reference.get("wkid")
    code = 3857 if code == 102100 else code
    return _wrap(_from_esri(document), SpatialReference(int(code)) if code else None)


_GEOHASH = "0123456789bcdefghjkmnpqrstuvwxyz"


def FromGeohash(geohash_string):
    """The geohash cell's extent in WGS 84: each character five bits, alternately halving longitude and latitude,
    longitude first — the public geohash definition."""
    from ._reference import Extent
    west, east, south, north = -180.0, 180.0, -90.0, 90.0
    longitude = True
    for character in str(geohash_string).lower():
        bits = _GEOHASH.index(character)
        for shift in range(4, -1, -1):
            bit = (bits >> shift) & 1
            if longitude:
                middle = (west + east) / 2
                west, east = (middle, east) if bit else (west, middle)
            else:
                middle = (south + north) / 2
                south, north = (middle, north) if bit else (south, middle)
            longitude = not longitude
    return Extent(west, south, east, north, SpatialReference(4326))


def _angle(text, notation):
    """A latitude or longitude from DD, DDM or DMS text with its hemisphere letter or sign."""
    import re
    text = text.strip().upper().replace("°", " ").replace("'", " ").replace('"', " ")
    hemisphere = text[-1] if text and text[-1] in "NSEW" else (text[0] if text and text[0] in "NSEW" else "")
    numbers = [float(n) for n in re.findall(r"-?\d+(?:\.\d+)?", text)]
    wanted = {"DD": 1, "DDM": 2, "DMS": 3}[notation]
    if len(numbers) != wanted:
        raise ValueError(f"FromCoordString: '{text}' is not {notation}.")
    sign = -1 if numbers[0] < 0 or hemisphere in "SW" and hemisphere else 1
    value = abs(numbers[0]) + sum(abs(n) / 60 ** i for i, n in enumerate(numbers[1:], start=1))
    return sign * value, hemisphere


def FromCoordString(string, notation):
    """A PointGeometry in WGS 84 from coordinate notation. Built: DD, DDM and DMS (latitude then longitude, each with a
    hemisphere letter or a sign, as the page's examples) and UTM (zone, MGRS latitude band, easting, northing). MGRS,
    USNG, GARS, GEOREF and UTMNS are refused: their grids are not written out in the documentation."""
    import re
    kind = str(notation).upper()
    if kind in ("DD", "DDM", "DMS"):
        size = {"DD": 1, "DDM": 2, "DMS": 3}[kind]
        tokens = str(string).replace(",", " ").split()
        if len(tokens) == 2 * size:
            first, second = " ".join(tokens[:size]), " ".join(tokens[size:])
        else:
            split = re.split(r"(?<=[NSns])\s+", str(string).strip(), maxsplit=1)
            if len(split) != 2:
                raise ValueError(f"FromCoordString: '{string}' is not two {kind} coordinates.")
            first, second = split
        a, ha = _angle(first, kind)
        b, hb = _angle(second, kind)
        latitude, longitude = (b, a) if ha in "EW" and ha else (a, b)
        return PointGeometry(ShapelyPoint(longitude, latitude), SpatialReference(4326))
    if kind == "UTM":
        match = re.match(r"^\s*(\d{1,2})\s*([C-HJ-NP-X])\s+(\d+(?:\.\d+)?)\s+(\d+(?:\.\d+)?)\s*$", str(string).upper())
        if not match:
            raise ValueError(f"FromCoordString: '{string}' is not UTM (zone, band, easting, northing).")
        zone, band, easting, northing = int(match[1]), match[2], float(match[3]), float(match[4])
        code = (32600 if band >= "N" else 32700) + zone
        return PointGeometry(ShapelyPoint(easting, northing), SpatialReference(code)).projectAs(SpatialReference(4326))
    from ._runtime import NotBuiltError
    raise NotBuiltError(f"arcpy.FromCoordString: notation '{notation}' is not built in grapy; DD, DDM, DMS and UTM are.")
