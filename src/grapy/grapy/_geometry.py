"""arcpy's geometry objects, by their documented names, held as shapely geometries — ADR-223 §5.2."""
from __future__ import annotations

import shapely
from shapely.geometry import LineString, MultiLineString, MultiPoint, MultiPolygon, Polygon as ShapelyPolygon
from shapely.geometry import Point as ShapelyPoint

from ._reference import SpatialReference, reference_of

_SQUARE_METRES = {"SQUAREMETERS": 1, "SQUAREKILOMETERS": 1e6, "HECTARES": 1e4, "ACRES": 4046.8564224,
                  "SQUAREMILES": 2589988.110336, "SQUAREFEET": 0.09290304, "SQUAREYARDS": 0.83612736}
_METRES = {"METERS": 1, "KILOMETERS": 1000, "FEET": 0.3048, "MILES": 1609.344, "YARDS": 0.9144, "NAUTICALMILES": 1852}


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
        import json
        return json.dumps(self.__geo_interface__)

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

    def getArea(self, method="PLANAR", units=None):
        if method.upper() in ("GEODESIC", "PRESERVE_SHAPE"):
            from pyproj import Geod
            area, _ = Geod(ellps="WGS84").geometry_area_perimeter(self.projectAs(SpatialReference(4326))._shape)
            area = abs(area)
        else:
            area = self._shape.area
        return area / _SQUARE_METRES.get((units or "SQUAREMETERS").upper().replace(" ", ""), 1)

    def getLength(self, method="PLANAR", units=None):
        if method.upper() in ("GEODESIC", "PRESERVE_SHAPE"):
            from pyproj import Geod
            length = Geod(ellps="WGS84").geometry_length(self.projectAs(SpatialReference(4326))._shape)
        else:
            length = self._shape.length
        return length / _METRES.get((units or "METERS").upper().replace(" ", ""), 1)

    # -- relations
    def contains(self, other, relation=None):
        return self._shape.contains(_shape_of(other))

    def within(self, other, relation=None):
        return self._shape.within(_shape_of(other))

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
        return _wrap(self._shape.intersection(_shape_of(other)), self.spatialReference)

    def symmetricDifference(self, other):
        return _wrap(self._shape.symmetric_difference(_shape_of(other)), self.spatialReference)

    def union(self, other):
        return _wrap(self._shape.union(_shape_of(other)), self.spatialReference)

    def generalize(self, max_offset):
        return _wrap(self._shape.simplify(max_offset, preserve_topology=True), self.spatialReference)

    def densify(self, method, distance, deviation=None):
        return _wrap(shapely.segmentize(self._shape, distance), self.spatialReference)

    def projectAs(self, spatial_reference, transformation_name=None):
        target = reference_of(spatial_reference)
        if self.spatialReference is None or target is None or self.spatialReference.factoryCode == target.factoryCode:
            return _wrap(self._shape, target or self.spatialReference)
        import numpy
        from pyproj import Transformer
        move = Transformer.from_crs(self.spatialReference.factoryCode, target.factoryCode, always_xy=True).transform
        return _wrap(shapely.transform(self._shape, lambda xy: numpy.column_stack(move(xy[:, 0], xy[:, 1]))), target)

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
