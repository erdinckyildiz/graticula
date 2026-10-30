"""Arcade's geometry functions (ADR-235 §5.3), computed with shapely and pyproj — the Tier 2 libraries grapy already
uses (ADR-223) — from the Geometry functions page.

A geometry function takes a Geometry or a Feature (its geometry). INFERRED (ADR-235 §5.8): a function given null
answers null, or false for a relation; a planar measure with units converts from the spatial reference's own linear
unit, and is refused on a geographic reference, whose degrees are no length (AreaGeodetic and LengthGeodetic measure
those); an empty result of an overlay answers null.
"""
from __future__ import annotations

import json
import math

import shapely
from shapely import affinity
from shapely.geometry import LineString, MultiPoint, Point, Polygon, box
from shapely.ops import unary_union

from ._errors import ArcadeError
from ._fn_core import arity
from ._library import builtin
from ._values import Dictionary, Feature, Geometry, from_json, is_number, to_bool, to_json, to_number, to_text, type_of

_LINEAR = {"meters": 1.0, "meter": 1.0, "kilometers": 1000.0, "kilometer": 1000.0, "centimeters": 0.01,
           "millimeters": 0.001, "decimeters": 0.1, "feet": 0.3048, "foot": 0.3048, "international-feet": 0.3048,
           "us-feet": 1200 / 3937, "feet-us": 1200 / 3937, "us-survey-feet": 1200 / 3937, "inches": 0.0254,
           "yards": 0.9144, "miles": 1609.344, "mile": 1609.344, "us-miles": 6336000 / 3937,
           "nautical-miles": 1852.0}
_AREAL = {"square-meters": 1.0, "square-kilometers": 1e6, "square-feet": 0.09290304, "square-us-feet": (1200 / 3937) ** 2,
          "square-yards": 0.83612736, "square-miles": 2589988.110336, "acres": 4046.8564224, "hectares": 10000.0,
          "square-inches": 0.00064516, "square-centimeters": 1e-4, "square-millimeters": 1e-6}


def shape_of(value, name):
    """The shapely shape of a geometry or a feature, or None."""
    if value is None:
        return None
    if isinstance(value, Feature):
        return value.geometry.shape if value.geometry is not None else None
    if isinstance(value, Geometry):
        return value.shape
    raise ArcadeError(f"{name} takes a geometry or a feature; a {type_of(value)} was given.")


def geometry_of(value, name):
    if value is None:
        return None
    if isinstance(value, Feature):
        return value.geometry
    if isinstance(value, Geometry):
        return value
    raise ArcadeError(f"{name} takes a geometry or a feature; a {type_of(value)} was given.")


def _wrap(shape, like, kind=None):
    if shape is None or shape.is_empty:
        return None
    return Geometry(shape, like.wkid if like is not None else None, kind)


def _meters_per_unit(wkid, name):
    if not wkid:
        return 1.0
    from .._reference import SpatialReference
    reference = SpatialReference(int(wkid))
    if reference.type == "Geographic":
        raise ArcadeError(f"{name} with units needs a projected spatial reference; this one is geographic. Use "
                          f"{name}Geodetic.")
    return reference.metersPerUnit


def _factor(table, unit, name):
    key = to_text(unit).strip().lower().replace("_", "-").replace(" ", "-")
    if key not in table:
        raise ArcadeError(f"{name}: '{unit}' is not a unit grapy knows: {', '.join(sorted(table))}.")
    return table[key]


# ---- properties -------------------------------------------------------------------------------------------------------


def _point(x, y, wkid, z=None):
    return Geometry(Point(x, y) if z is None else Point(x, y, z), wkid, "Point")


def properties(geometry):
    """What a geometry answers by member: x, y, rings, paths, points, the extent's bounds, hasZ, hasM, type and
    spatialReference."""
    shape, wkid = geometry.shape, geometry.wkid
    out = {}
    if geometry.kind == "Point":
        out["x"], out["y"] = shape.x, shape.y
        out["z"] = shape.z if shape.has_z else None
        out["m"] = None
    elif geometry.kind == "Extent":
        x0, y0, x1, y1 = shape.bounds
        out.update({"xmin": x0, "ymin": y0, "xmax": x1, "ymax": y1, "width": x1 - x0, "height": y1 - y0,
                    "center": _point((x0 + x1) / 2, (y0 + y1) / 2, wkid)})
    else:
        from ..conversion import _to_esri
        document = _to_esri(shape) or {}
        for key in ("rings", "paths"):
            if key in document:
                out[key] = [[_point(c[0], c[1], wkid) for c in part] for part in document[key]]
        if "points" in document:
            out["points"] = [_point(c[0], c[1], wkid) for c in document["points"]]
    out["hasZ"] = bool(shape.has_z)
    out["hasM"] = False
    out["spatialReference"] = Dictionary({"wkid": float(wkid)} if wkid else {})
    out["type"] = geometry.kind.lower()
    return out


# ---- construction ---------------------------------------------------------------------------------------------------


def _coordinates(value):
    """[x, y] of a point given as an array, a Point or a dictionary."""
    if isinstance(value, Geometry) and value.kind == "Point":
        return [value.shape.x, value.shape.y]
    if isinstance(value, list):
        return [to_number(v) for v in value[:2]]
    if isinstance(value, Dictionary):
        return [to_number(value.get("x")), to_number(value.get("y"))]
    raise ArcadeError(f"A coordinate is an array or a Point; a {type_of(value)} was given.")


def _from_definition(definition, wanted=None):
    """A Geometry from an ArcGIS REST JSON definition: a Dictionary or JSON text."""
    if isinstance(definition, str):
        try:
            definition = from_json(json.loads(definition))
        except ValueError:
            raise ArcadeError("The geometry's JSON text is not JSON.") from None
    if isinstance(definition, Geometry):
        return definition
    if not isinstance(definition, Dictionary):
        raise ArcadeError(f"A geometry is defined by a dictionary or JSON text; a {type_of(definition)} was given.")
    reference = definition.get("spatialReference")
    wkid = None
    if isinstance(reference, Dictionary):
        wkid = reference.get("latestWkid") or reference.get("wkid")
    wkid = int(wkid) if wkid else None
    if definition.has("curveRings") or definition.has("curvePaths"):
        raise ArcadeError("Curves (curveRings, curvePaths) are not built in grapy: its geometries are linear.")
    if definition.has("x"):
        z = definition.get("z")
        shape = Point(to_number(definition.get("x")), to_number(definition.get("y")),
                      *([to_number(z)] if z is not None else []))
        kind = "Point"
    elif definition.has("xmin"):
        shape = box(*(to_number(definition.get(k)) for k in ("xmin", "ymin", "xmax", "ymax")))
        kind = "Extent"
    elif definition.has("points"):
        shape = MultiPoint([_coordinates(p) for p in definition.get("points")])
        kind = "Multipoint"
    elif definition.has("paths"):
        from shapely.geometry import MultiLineString
        shape = MultiLineString([[_coordinates(p) for p in path] for path in definition.get("paths")])
        kind = "Polyline"
    elif definition.has("rings"):
        from ..conversion import _from_esri
        shape = _from_esri({"rings": [[_coordinates(p) for p in ring] for ring in definition.get("rings")]})
        kind = "Polygon"
    else:
        raise ArcadeError("The dictionary defines no geometry: it has no x, xmin, points, paths or rings.")
    if wanted and kind != wanted:
        raise ArcadeError(f"The definition is a {kind}, not a {wanted}.")
    return Geometry(shape, wkid, kind)


def _constructor(name):
    @builtin(name)
    def run(_, args):
        arity(name, args, 1)
        return _from_definition(args[0], name)
    return run


for _name in ("Point", "Polyline", "Polygon", "Multipoint"):
    _constructor(_name)


@builtin("Geometry")
def _geometry(_, args):
    arity("Geometry", args, 1)
    value = args[0]
    if value is None:
        return None
    if isinstance(value, Feature):
        return value.geometry
    return _from_definition(value)


@builtin("Extent")
def _extent(_, args):
    arity("Extent", args, 1)
    value = args[0]
    if isinstance(value, (Feature, Geometry)):
        geometry = geometry_of(value, "Extent")
        if geometry is None:
            return None
        return Geometry(box(*geometry.shape.bounds), geometry.wkid, "Extent")
    made = _from_definition(value)
    return Geometry(box(*made.shape.bounds), made.wkid, "Extent")


@builtin("SetGeometry")
def _set_geometry(_, args):
    arity("SetGeometry", args, 2)
    feature, geometry = args
    if not isinstance(feature, Feature):
        raise ArcadeError(f"SetGeometry takes a feature; a {type_of(feature)} was given.")
    if feature.immutable:
        raise ArcadeError("SetGeometry: a profile's feature is immutable.")
    feature.geometry = geometry_of(geometry, "SetGeometry")
    return None


# ---- measures --------------------------------------------------------------------------------------------------------


def _planar(name, args, measure, table, power):
    arity(name, args, 1, 2)
    from ._data import FeatureSet
    if isinstance(args[0], FeatureSet):
        geometries = [f.geometry for f in args[0].features() if f.geometry is not None]
    else:
        geometry = geometry_of(args[0], name)
        if geometry is None:
            return None
        geometries = [geometry]
    total = sum(measure(g.shape) for g in geometries)
    if len(args) < 2 or args[1] in (None, "") or not geometries:
        return float(total)
    per_unit = _meters_per_unit(geometries[0].wkid, name) ** power
    return float(total * per_unit / _factor(table, args[1], name))


@builtin("Area")
def _area(_, args):
    return _planar("Area", args, lambda s: s.area, _AREAL, 2)


@builtin("Length")
def _length(_, args):
    return _planar("Length", args, lambda s: s.length, _LINEAR, 1)


def _geodetic(name, args, area):
    arity(name, args, 1, 3)
    if len(args) > 2 and args[2] not in (None, "") and to_text(args[2]).lower() != "geodesic":
        raise ArcadeError(f"{name}: only the geodesic curve type is built in grapy.")
    from ._data import FeatureSet
    if isinstance(args[0], FeatureSet):
        geometries = [f.geometry for f in args[0].features() if f.geometry is not None]
    else:
        geometry = geometry_of(args[0], name)
        if geometry is None:
            return None
        geometries = [geometry]
    from .._geometry import Geometry as GrapyGeometry
    from .._reference import SpatialReference
    total = 0.0
    for geometry in geometries:
        if not geometry.wkid:
            raise ArcadeError(f"{name} needs a spatial reference.")
        measured = GrapyGeometry(geometry.shape, SpatialReference(int(geometry.wkid)))
        total += measured.getArea("GEODESIC") if area else measured.getLength("GEODESIC")
    unit = args[1] if len(args) > 1 and args[1] not in (None, "") else ("square-meters" if area else "meters")
    return float(total / _factor(_AREAL if area else _LINEAR, unit, name))


@builtin("AreaGeodetic")
def _area_geodetic(_, args):
    return _geodetic("AreaGeodetic", args, True)


@builtin("LengthGeodetic")
def _length_geodetic(_, args):
    return _geodetic("LengthGeodetic", args, False)


@builtin("DistanceGeodetic")
def _distance_geodetic(_, args):
    arity("DistanceGeodetic", args, 2, 4)
    if len(args) > 3 and args[3] not in (None, "") and to_text(args[3]).lower() != "geodesic":
        raise ArcadeError("DistanceGeodetic: only the geodesic curve type is built in grapy.")
    a, b = geometry_of(args[0], "DistanceGeodetic"), geometry_of(args[1], "DistanceGeodetic")
    if a is None or b is None:
        return None
    if a.kind != "Point" or b.kind != "Point":
        raise ArcadeError("DistanceGeodetic takes two points.")
    if not a.wkid:
        raise ArcadeError("DistanceGeodetic needs a spatial reference.")
    from pyproj import CRS, Transformer
    crs = CRS.from_epsg(int(a.wkid))
    geographic = crs.geodetic_crs
    to_lonlat = Transformer.from_crs(crs, geographic, always_xy=True)
    (x0, y0), (x1, y1) = to_lonlat.transform(a.shape.x, a.shape.y), to_lonlat.transform(b.shape.x, b.shape.y)
    distance = geographic.get_geod().inv(x0, y0, x1, y1)[2]
    unit = args[2] if len(args) > 2 and args[2] not in (None, "") else "meters"
    return float(distance / _factor(_LINEAR, unit, "DistanceGeodetic"))


@builtin("Distance")
def _distance(_, args):
    arity("Distance", args, 2, 3)
    a, b = geometry_of(args[0], "Distance"), geometry_of(args[1], "Distance")
    if a is None or b is None:
        return None
    distance = a.shape.distance(b.shape)
    if len(args) < 3 or args[2] in (None, ""):
        return float(distance)
    return float(distance * _meters_per_unit(a.wkid, "Distance") / _factor(_LINEAR, args[2], "Distance"))


def _degrees(dx, dy):
    return math.degrees(math.atan2(dy, dx)) % 360.0


def _points(name, args):
    out = []
    for value in args:
        geometry = geometry_of(value, name)
        if geometry is None or geometry.kind != "Point":
            raise ArcadeError(f"{name} takes points.")
        out.append(geometry.shape)
    return out


@builtin("Angle")
def _angle(_, args):
    """Arithmetic degrees, 0 to 360, counter-clockwise from east; with three points, the angle at the second from the
    first to the third, counter-clockwise."""
    arity("Angle", args, 2, 3)
    p = _points("Angle", args)
    if len(p) == 2:
        return _degrees(p[1].x - p[0].x, p[1].y - p[0].y)
    return (_degrees(p[2].x - p[1].x, p[2].y - p[1].y) - _degrees(p[0].x - p[1].x, p[0].y - p[1].y)) % 360.0


@builtin("Bearing")
def _bearing(_, args):
    """Geographic degrees, 0 to 360, clockwise from north; with three points, at the second from the first to the
    third, clockwise."""
    arity("Bearing", args, 2, 3)
    p = _points("Bearing", args)

    def bearing(a, b):
        return (90.0 - _degrees(b.x - a.x, b.y - a.y)) % 360.0
    if len(p) == 2:
        return bearing(p[0], p[1])
    return (bearing(p[1], p[2]) - bearing(p[1], p[0])) % 360.0


# ---- relations --------------------------------------------------------------------------------------------------------


def _relation(name, test, featureset_first=True):
    @builtin(name)
    def run(_, args):
        arity(name, args, 2)
        from ._data import FeatureSet
        if isinstance(args[0], FeatureSet) or isinstance(args[1], FeatureSet):
            return _data_relation(name, args)
        a, b = shape_of(args[0], name), shape_of(args[1], name)
        if a is None or b is None:
            return False
        return bool(test(a, b))
    return run


def _data_relation(name, args):
    from ._data import FeatureSet
    # Contains(geometry, features) and Within(geometry, features) take the FeatureSet second; the others first.
    if name in ("Contains", "Within"):
        geometry, features = args[0], args[1]
        if not isinstance(features, FeatureSet):
            raise ArcadeError(f"{name} with a FeatureSet takes the geometry first and the FeatureSet second.")
        shape = shape_of(geometry, name)
        test = (lambda f: shape.contains(f)) if name == "Contains" else (lambda f: shape.within(f))
    else:
        features, geometry = args[0], args[1]
        if not isinstance(features, FeatureSet):
            raise ArcadeError(f"{name} with a FeatureSet takes the FeatureSet first and the geometry second.")
        shape = shape_of(geometry, name)
        relate = {"Intersects": "intersects", "Crosses": "crosses", "Overlaps": "overlaps", "Touches": "touches"}
        if name == "EnvelopeIntersects":
            envelope = box(*shape.bounds) if shape is not None else None
            test = lambda f: box(*f.bounds).intersects(envelope)  # noqa: E731
        else:
            test = lambda f: getattr(f, relate[name])(shape)  # noqa: E731
    if shape is None:
        return features.only([])
    return features.only([f.oid for f in features.features(geometry=True)
                          if f.geometry is not None and test(f.geometry.shape)])


_relation("Intersects", lambda a, b: a.intersects(b))
_relation("Contains", lambda a, b: a.contains(b))
_relation("Within", lambda a, b: a.within(b))
_relation("Crosses", lambda a, b: a.crosses(b))
_relation("Overlaps", lambda a, b: a.overlaps(b))
_relation("Touches", lambda a, b: a.touches(b))
_relation("Disjoint", lambda a, b: a.disjoint(b))
_relation("EnvelopeIntersects", lambda a, b: box(*a.bounds).intersects(box(*b.bounds)))


@builtin("Relate")
def _relate(_, args):
    arity("Relate", args, 3)
    a, b = shape_of(args[0], "Relate"), shape_of(args[1], "Relate")
    if a is None or b is None:
        return False
    pattern = to_text(args[2])
    if len(pattern) != 9 or any(c not in "TF*012" for c in pattern.upper()):
        raise ArcadeError("Relate's relation is a nine-character DE-9IM pattern of T, F, *, 0, 1 and 2.")
    return bool(a.relate_pattern(b, pattern.upper()))


# ---- overlay and construction -------------------------------------------------------------------------------------------


def _overlay(name, operation):
    @builtin(name)
    def run(_, args):
        arity(name, args, 2)
        a, b = geometry_of(args[0], name), geometry_of(args[1], name)
        if a is None or b is None:
            return None
        return _wrap(operation(a.shape, b.shape), a)
    return run


_overlay("Intersection", lambda a, b: a.intersection(b))
_overlay("Difference", lambda a, b: a.difference(b))
_overlay("SymmetricDifference", lambda a, b: a.symmetric_difference(b))


@builtin("Union")
def _union(_, args):
    values = args[0] if len(args) == 1 and isinstance(args[0], list) else args
    geometries = [g for g in (geometry_of(v, "Union") for v in values) if g is not None]
    if not geometries:
        return None
    return _wrap(unary_union([g.shape for g in geometries]), geometries[0])


@builtin("Buffer")
def _buffer(_, args):
    arity("Buffer", args, 2, 3)
    geometry = geometry_of(args[0], "Buffer")
    if geometry is None:
        return None
    distance = to_number(args[1])
    if len(args) > 2 and args[2] not in (None, ""):
        distance = distance * _factor(_LINEAR, args[2], "Buffer") / _meters_per_unit(geometry.wkid, "Buffer")
    return _wrap(geometry.shape.buffer(distance), geometry, "Polygon")


@builtin("Centroid")
def _centroid(_, args):
    arity("Centroid", args, 1, 2)
    geometry = geometry_of(args[0], "Centroid")
    if geometry is None:
        return None
    algorithm = to_text(args[1]).lower() if len(args) > 1 and args[1] is not None else "geometric"
    if algorithm not in ("geometric", "labelpoint"):
        raise ArcadeError("Centroid's algorithm is 'geometric' or 'labelPoint'.")
    point = geometry.shape.representative_point() if algorithm == "labelpoint" else geometry.shape.centroid
    return _wrap(point, geometry, "Point")


@builtin("ConvexHull")
def _convex_hull(_, args):
    arity("ConvexHull", args, 1)
    geometry = geometry_of(args[0], "ConvexHull")
    return None if geometry is None else _wrap(geometry.shape.convex_hull, geometry)


@builtin("Clip")
def _clip(_, args):
    arity("Clip", args, 2)
    geometry, envelope = geometry_of(args[0], "Clip"), geometry_of(args[1], "Clip")
    if geometry is None or envelope is None:
        return None
    return _wrap(shapely.clip_by_rect(geometry.shape, *envelope.shape.bounds), geometry)


@builtin("Densify")
def _densify(_, args):
    arity("Densify", args, 2, 3)
    geometry = geometry_of(args[0], "Densify")
    if geometry is None:
        return None
    length = to_number(args[1])
    if len(args) > 2 and args[2] not in (None, ""):
        length = length * _factor(_LINEAR, args[2], "Densify") / _meters_per_unit(geometry.wkid, "Densify")
    if not length > 0:
        raise ArcadeError("Densify's maximum segment length is greater than 0.")
    return _wrap(shapely.segmentize(geometry.shape, length), geometry, geometry.kind)


@builtin("IsSimple")
def _is_simple(_, args):
    arity("IsSimple", args, 1)
    shape = shape_of(args[0], "IsSimple")
    if shape is None:
        return False
    return bool(shape.is_valid if shape.geom_type in ("Polygon", "MultiPolygon") else shape.is_simple)


@builtin("IsSelfIntersecting")
def _is_self_intersecting(_, args):
    arity("IsSelfIntersecting", args, 1)
    shape = shape_of(args[0], "IsSelfIntersecting")
    if shape is None:
        return False
    if shape.geom_type in ("Polygon", "MultiPolygon"):
        return not shape.is_valid
    return not shape.is_simple


@builtin("Simplify")
def _simplify(_, args):
    arity("Simplify", args, 1)
    geometry = geometry_of(args[0], "Simplify")
    if geometry is None:
        return None
    return _wrap(shapely.make_valid(geometry.shape), geometry, geometry.kind)


@builtin("MultiPartToSinglePart")
def _multipart(_, args):
    arity("MultiPartToSinglePart", args, 1)
    geometry = geometry_of(args[0], "MultiPartToSinglePart")
    if geometry is None:
        return []
    parts = list(geometry.shape.geoms) if hasattr(geometry.shape, "geoms") else [geometry.shape]
    kind = "Point" if geometry.kind == "Multipoint" else geometry.kind
    return [Geometry(p, geometry.wkid, kind) for p in parts]


@builtin("Rotate")
def _rotate(_, args):
    arity("Rotate", args, 2, 3)
    geometry = geometry_of(args[0], "Rotate")
    if geometry is None:
        return None
    origin = "centroid"
    if len(args) > 2 and args[2] is not None:
        pivot = geometry_of(args[2], "Rotate")
        origin = (pivot.shape.x, pivot.shape.y)
    return Geometry(affinity.rotate(geometry.shape, to_number(args[1]), origin=origin), geometry.wkid, geometry.kind)


@builtin("RingIsClockwise")
def _ring_is_clockwise(_, args):
    arity("RingIsClockwise", args, 1)
    if not isinstance(args[0], list):
        raise ArcadeError("RingIsClockwise takes an array of points.")
    coordinates = [_coordinates(p) for p in args[0]]
    if len(coordinates) < 3:
        return False
    signed = sum(x0 * y1 - x1 * y0 for (x0, y0), (x1, y1) in zip(coordinates, coordinates[1:] + coordinates[:1]))
    return signed < 0


_ = (LineString, Polygon, is_number, to_bool, to_json)
