"""arcpy.conversion's tools grapy builds, by their documented names and parameters — ADR-223 §5.2.

The JSON tools read and write both forms arcpy's do: Esri JSON (a feature set, ``geometryType`` and ``features`` with
``attributes`` and ``geometry``) and, asked with ``GEOJSON``, RFC 7946 GeoJSON.
"""
from __future__ import annotations

import json
import os

from . import _gpkg, da
from ._result import Result
from ._runtime import NotBuiltError


def _out(path):
    return path if ".gpkg" in str(path).lower() else str(path) + ".gpkg"


def ExportFeatures(in_features, out_features, where_clause=None, use_field_alias_as_name="NOT_USE_ALIAS",
                   field_mapping=None, sort_field=None):
    """The features a where clause chooses — all of them when none is given — copied, in the order of ``sort_field``
    (``[[field, ASCENDING|DESCENDING]]``) when one is named."""
    from .management import _pairs
    connection, table = _gpkg.table_of(in_features)
    columns, shape, srid, _ = _gpkg.describe(connection, table)
    connection.close()
    names = [c.name for c in columns]
    order = ", ".join(f'"{f}" {"DESC" if (d[0] if d else "").upper().startswith("DESC") else "ASC"}'
                      for f, *d in _pairs(sort_field))
    out = _out(out_features)
    _gpkg.create(out, shape, srid, columns)
    with da.SearchCursor(in_features, [*names, "SHAPE@"], where_clause, sql_clause=(None, f"order by {order}" if order else None)) as rows, \
            da.InsertCursor(out, [*names, "SHAPE@"]) as written:
        for row in rows:
            written.insertRow(row)
    return Result(out)


def FeatureClassToFeatureClass(in_features, out_path, out_name, where_clause=None, *_, **__):
    return ExportFeatures(in_features, os.path.join(out_path, out_name), where_clause)


ExportTable = ExportFeatures
TableToTable = FeatureClassToFeatureClass


_ESRI_TYPES = {"esriGeometryPoint": "Point", "esriGeometryMultipoint": "Multipoint", "esriGeometryPolyline": "Polyline",
               "esriGeometryPolygon": "Polygon"}
_ESRI_FIELDS = {"esriFieldTypeSmallInteger": "SmallInteger", "esriFieldTypeInteger": "Integer",
                "esriFieldTypeBigInteger": "BigInteger", "esriFieldTypeSingle": "Single", "esriFieldTypeDouble": "Double",
                "esriFieldTypeString": "String", "esriFieldTypeDate": "Date", "esriFieldTypeGUID": "Guid"}
_RESERVED = {"esriFieldTypeOID", "esriFieldTypeGeometry", "esriFieldTypeGlobalID"}


def _from_esri(geometry):
    """An Esri JSON geometry as a shapely one."""
    from shapely.geometry import LineString, MultiLineString, MultiPoint, Point, Polygon
    from shapely.ops import unary_union
    if geometry is None:
        return None
    if "x" in geometry:
        return Point(geometry["x"], geometry["y"]) if geometry["x"] is not None else None
    if "points" in geometry:
        return MultiPoint([p[:2] for p in geometry["points"]])
    if "paths" in geometry:
        return MultiLineString([[p[:2] for p in path] for path in geometry["paths"]])
    if "rings" in geometry:
        # Rings in one feature: each is an area, a hole being one that lies inside another — read by the rule rather
        # than by winding, which writers do not all keep.
        rings = [Polygon([p[:2] for p in ring]) for ring in geometry["rings"] if len(ring) >= 4]
        shells = [r for r in rings if not any(o is not r and o.contains(r) for o in rings)]
        result = unary_union(shells)
        for hole in (r for r in rings if r not in shells):
            result = result.difference(hole)
        return result
    raise ValueError(f"Not an Esri JSON geometry: {sorted(geometry)}")


def JSONToFeatures(in_json_file, out_features, geometry_type=None):
    """A feature class from an Esri JSON feature set or a GeoJSON feature collection."""
    with open(in_json_file, encoding="utf-8") as read:
        document = json.load(read)
    out = _out(out_features)
    if document.get("type") == "FeatureCollection":
        from ._io import geojson_to_gpkg
        geojson_to_gpkg(in_json_file, out)
        return Result(out)
    srid = (document.get("spatialReference") or {}).get("latestWkid") or (document.get("spatialReference") or {}).get("wkid") or 0
    if srid == 102100:
        srid = 3857
    kind = _ESRI_TYPES.get(document.get("geometryType"), "Geometry")
    columns = [_gpkg.Column(f["name"], _ESRI_FIELDS.get(f.get("type"), "String")) for f in document.get("fields", [])
               if f.get("type") not in _RESERVED]
    if not document.get("fields"):
        names = []
        for feature in document.get("features", []):
            names += [n for n in (feature.get("attributes") or {}) if n not in names]
        columns = [_gpkg.Column(n, "String") for n in names]
    _gpkg.create(out, kind, int(srid), columns)
    names = [c.name for c in columns]
    with da.InsertCursor(out, [*names, "SHAPE@"]) as written:
        for feature in document.get("features", []):
            attributes = feature.get("attributes") or {}
            written.insertRow((*[attributes.get(n) for n in names], _from_esri(feature.get("geometry"))))
    return Result(out)


def _to_esri(shape):
    if shape is None or shape.is_empty:
        return None
    kind = shape.geom_type
    if kind == "Point":
        return {"x": shape.x, "y": shape.y}
    if kind == "MultiPoint":
        return {"points": [list(p.coords[0][:2]) for p in shape.geoms]}
    if kind in ("LineString", "MultiLineString"):
        lines = [shape] if kind == "LineString" else list(shape.geoms)
        return {"paths": [[list(c[:2]) for c in line.coords] for line in lines]}
    from shapely.geometry.polygon import orient
    polygons = [shape] if kind == "Polygon" else list(shape.geoms)
    rings = []
    for polygon in polygons:
        # Esri's winding: an outer ring clockwise, a hole counter-clockwise — shapely's orient(-1) writes exactly that.
        oriented = orient(polygon, -1.0)
        rings.append([list(c[:2]) for c in oriented.exterior.coords])
        rings += [[list(c[:2]) for c in hole.coords] for hole in oriented.interiors]
    return {"rings": rings}


def FeaturesToJSON(in_features, out_json_file, format_json="NOT_FORMATTED", include_z_values="NO_Z_VALUES",
                   include_m_values="NO_M_VALUES", geoJSON="NO_GEOJSON", outputToWGS84="KEEP_INPUT_SR",
                   use_field_alias="USE_FIELD_NAME"):
    """A feature class as an Esri JSON feature set, or with ``GEOJSON`` as GeoJSON in WGS 84."""
    indent = 2 if str(format_json).upper() == "FORMATTED" else None
    if str(geoJSON).upper() == "GEOJSON":
        from ._io import gpkg_to_geojson
        gpkg_to_geojson(in_features, out_json_file)
        if indent:
            with open(out_json_file, encoding="utf-8") as read:
                document = json.load(read)
            with open(out_json_file, "w", encoding="utf-8") as write:
                json.dump(document, write, indent=indent, default=str)
        return Result(out_json_file)
    connection, table = _gpkg.table_of(in_features)
    columns, shape, srid, _ = _gpkg.describe(connection, table)
    oid = _gpkg.oid_column(connection, table)
    connection.close()
    reverse = {v: k for k, v in _ESRI_FIELDS.items()}
    names = [c.name for c in columns]
    document = {
        "displayFieldName": "",
        "fieldAliases": {n: n for n in [oid, *names]},
        "geometryType": {v: k for k, v in _ESRI_TYPES.items()}.get(shape, "esriGeometryPolygon"),
        "spatialReference": {"wkid": srid, "latestWkid": srid} if srid > 0 else {},
        "fields": [{"name": oid, "type": "esriFieldTypeOID", "alias": oid},
                   *[{"name": c.name, "type": reverse.get(c.type, "esriFieldTypeString"), "alias": c.name} for c in columns]],
        "features": [],
    }
    with da.SearchCursor(in_features, ["OID@", *names, "SHAPE@"]) as rows:
        for row in rows:
            document["features"].append({"attributes": dict(zip([oid, *names], row[:-1])),
                                         "geometry": _to_esri(row[-1]._shape if row[-1] is not None else None)})
    with open(out_json_file, "w", encoding="utf-8") as write:
        json.dump(document, write, indent=indent, default=str)
    return Result(out_json_file)


def __getattr__(name):
    if name.startswith("_"):
        raise AttributeError(name)

    def missing(*_, **__):
        raise NotBuiltError(f"arcpy.conversion.{name} is not built in grapy yet.")

    return missing
