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


def _raster_of(value, tool):
    from ._raster import as_raster
    return as_raster(value, tool)


def RasterToPolygon(in_raster, out_polygon_features, simplify="SIMPLIFY", raster_field="Value",
                    create_multipart_features="SINGLE_OUTER_PART", max_vertices_per_feature=None):
    """A polygon for each connected region of equal cell values, with ``Id`` and ``gridcode`` as arcpy writes them.
    Regions follow cell edges exactly; ``SIMPLIFY``'s smoothing is not documented closely enough to reproduce, so a
    call that asks for it is refused (pass ``NO_SIMPLIFY``)."""
    from rasterio.features import shapes
    from shapely.geometry import shape as as_shape
    from ._runtime import NotBuiltError
    from ._raster import _refuse_environment
    _refuse_environment("RasterToPolygon")
    if str(simplify).upper() != "NO_SIMPLIFY":
        raise NotBuiltError("RasterToPolygon: simplify 'SIMPLIFY' is not built in grapy yet; pass 'NO_SIMPLIFY'.")
    if str(raster_field).upper() not in ("VALUE", "VALUES"):
        raise NotBuiltError(f"RasterToPolygon: raster_field '{raster_field}' needs an attribute table, which is not built in grapy yet.")
    if max_vertices_per_feature not in (None, "", "#"):
        raise NotBuiltError("RasterToPolygon: max_vertices_per_feature is not built in grapy yet.")
    raster = _raster_of(in_raster, "RasterToPolygon")
    if not raster.isInteger:
        from ._runtime import ExecuteError
        raise ExecuteError("ERROR 010247: RasterToPolygon needs an integer raster.")
    import numpy as np
    from shapely import union_all
    found = list(shapes(raster._array.astype(np.int32), mask=~raster._mask, connectivity=4, transform=raster._grid.transform()))
    out = _out(out_polygon_features)
    _gpkg.create(out, "Polygon", raster._grid.srid, [_gpkg.Column("Id", "Integer"), _gpkg.Column("gridcode", "Integer")])
    multipart = str(create_multipart_features).upper() == "MULTIPLE_OUTER_PART"
    with da.InsertCursor(out, ["Id", "gridcode", "SHAPE@"]) as written:
        if multipart:
            by_value: dict = {}
            for geometry, value in found:
                by_value.setdefault(int(value), []).append(as_shape(geometry))
            for i, (value, parts) in enumerate(sorted(by_value.items()), start=1):
                written.insertRow((i, value, union_all(parts)))
        else:
            for i, (geometry, value) in enumerate(found, start=1):
                written.insertRow((i, int(value), as_shape(geometry)))
    return Result(out)


def RasterToPoint(in_raster, out_point_features, raster_field="Value"):
    """A point at the centre of each cell that is not NoData, with ``pointid`` and ``grid_code``."""
    from shapely.geometry import Point
    if str(raster_field).upper() not in ("VALUE", "VALUES"):
        from ._runtime import NotBuiltError
        raise NotBuiltError(f"RasterToPoint: raster_field '{raster_field}' needs an attribute table, which is not built in grapy yet.")
    raster = _raster_of(in_raster, "RasterToPoint")
    xs, ys = raster._grid.centres()
    out = _out(out_point_features)
    _gpkg.create(out, "Point", raster._grid.srid, [_gpkg.Column("pointid", "Integer"),
                                                     _gpkg.Column("grid_code", "Integer" if raster.isInteger else "Double")])
    with da.InsertCursor(out, ["pointid", "grid_code", "SHAPE@"]) as written:
        n = 0
        for r in range(raster.height):
            for c in range(raster.width):
                if not raster._mask[r, c]:
                    n += 1
                    written.insertRow((n, raster._array[r, c].item(), Point(xs[c], ys[r])))
    return Result(out)


def _feature_grid(in_features, cellsize, tool):
    from ._runtime import NotBuiltError
    from .sa import _grid_over
    if cellsize in (None, "", "#"):
        raise NotBuiltError(f"{tool}: the default cell size (the extent's shorter side over 250) is not built in grapy yet; pass cellsize.")
    if hasattr(cellsize, "meanCellWidth"):
        cellsize = cellsize.meanCellWidth
    return _grid_over(in_features, float(cellsize))


def _value_raster(in_features, value_field, grid, assignment, tool):
    """Each cell's value from the features it holds, by the assignment rule; NoData where it holds none."""
    import numpy as np
    from rasterio.features import rasterize
    from ._raster import Raster
    from ._runtime import NotBuiltError
    field = "OID@" if str(value_field).upper() in ("OBJECTID", "OID", "FID") else value_field
    with da.SearchCursor(in_features, [field, "SHAPE@"]) as rows:
        pairs = [(s._shape, v) for v, s in rows if s is not None and v is not None]
    shape = (grid.rows, grid.columns)
    rule = str(assignment).upper()
    numeric = all(isinstance(v, (int, float)) and not isinstance(v, bool) for _, v in pairs)
    if not numeric:
        raise NotBuiltError(f"{tool}: a text value field needs an attribute table, which is not built in grapy yet.")
    integer = all(isinstance(v, int) for _, v in pairs)
    if rule == "CELL_CENTER":
        # Later features are drawn over earlier ones (INFERRED: arcpy documents no order without a priority field).
        burnt = rasterize([(s, float(v)) for s, v in pairs], out_shape=shape, transform=grid.transform(), fill=0, dtype="float64")
        covered = rasterize([(s, 1) for s, _ in pairs], out_shape=shape, transform=grid.transform(), fill=0, dtype="uint8") > 0
        array, mask = burnt, ~covered
    else:
        values: dict = {}
        for s, v in pairs:
            for point in getattr(s, "geoms", [s]):
                if point.geom_type != "Point":
                    raise NotBuiltError(f"{tool}: cell_assignment '{assignment}' is built for points only in grapy yet.")
                # INFERRED: a point on the grid's eastern or southern edge belongs to the last cell, not outside it.
                cell = grid.cell_of(min(point.x, grid.right - grid.width * 1e-9), max(point.y, grid.bottom + grid.height * 1e-9))
                if cell is not None:
                    values.setdefault(cell, []).append(v)
        array = np.zeros(shape)
        mask = np.ones(shape, bool)
        for cell, vs in values.items():
            if rule == "MOST_FREQUENT":
                kinds, counts = np.unique(vs, return_counts=True)
                # INFERRED: a tie goes to the lowest value.
                value = kinds[counts == counts.max()].min()
            elif rule in ("SUM", "MEAN", "MAXIMUM", "MINIMUM", "RANGE", "COUNT", "STANDARD_DEVIATION"):
                a = np.asarray(vs, float)
                value = {"SUM": a.sum(), "MEAN": a.mean(), "MAXIMUM": a.max(), "MINIMUM": a.min(),
                         "RANGE": a.max() - a.min(), "COUNT": len(a), "STANDARD_DEVIATION": a.std()}[rule]
            else:
                raise NotBuiltError(f"{tool}: cell_assignment '{assignment}' is not built in grapy yet.")
            array[cell] = value
            mask[cell] = False
        integer = integer and rule in ("MOST_FREQUENT", "SUM", "MAXIMUM", "MINIMUM", "RANGE", "COUNT")
    return Raster._of(np.where(mask, 0, array).astype(np.int32 if integer else np.float64), mask, grid)


def PolygonToRaster(in_features, value_field, out_rasterdataset, cell_assignment="CELL_CENTER", priority_field="NONE",
                    cellsize=None, build_rat=None):
    """Polygons burnt onto a grid over their extent: a cell takes the value of the polygon at its centre."""
    from ._runtime import NotBuiltError
    if str(cell_assignment).upper() != "CELL_CENTER":
        raise NotBuiltError(f"PolygonToRaster: cell_assignment '{cell_assignment}' is not built in grapy yet; CELL_CENTER is.")
    if str(priority_field).upper() not in ("NONE", "", "#"):
        raise NotBuiltError("PolygonToRaster: priority_field is not built in grapy yet.")
    grid = _feature_grid(in_features, cellsize, "PolygonToRaster")
    raster = _value_raster(in_features, value_field, grid, "CELL_CENTER", "PolygonToRaster")
    return Result(raster.save(out_rasterdataset))


def FeatureToRaster(in_features, field, out_raster, cell_size=None):
    grid = _feature_grid(in_features, cell_size, "FeatureToRaster")
    raster = _value_raster(in_features, field, grid, "CELL_CENTER", "FeatureToRaster")
    return Result(raster.save(out_raster))


def PointToRaster(in_features, value_field, out_rasterdataset, cell_assignment="MOST_FREQUENT", priority_field="NONE",
                  cellsize=None, build_rat=None):
    """Points gathered into cells, each cell's value by the assignment rule over the points it holds."""
    from ._runtime import NotBuiltError
    if str(priority_field).upper() not in ("NONE", "", "#"):
        raise NotBuiltError("PointToRaster: priority_field is not built in grapy yet.")
    grid = _feature_grid(in_features, cellsize, "PointToRaster")
    raster = _value_raster(in_features, value_field, grid, cell_assignment, "PointToRaster")
    return Result(raster.save(out_rasterdataset))


def __getattr__(name):
    if name.startswith("_"):
        raise AttributeError(name)

    def missing(*_, **__):
        raise NotBuiltError(f"arcpy.conversion.{name} is not built in grapy yet.")

    return missing
