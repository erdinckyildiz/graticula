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
    from .management import _copy_joined, _joined_layer, _pairs
    if _joined_layer(in_features) is not None:
        if where_clause or sort_field or field_mapping:
            raise NotBuiltError("arcpy.conversion.ExportFeatures of a joined layer with a where clause, sort or field "
                                "mapping is not built in grapy.")
        return _copy_joined(in_features, _out(out_features), True)
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
    target = None
    if str(outputToWGS84 or "KEEP_INPUT_SR").upper() == "WGS84":
        from ._reference import SpatialReference
        target, srid = SpatialReference(4326), 4326
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
    with da.SearchCursor(in_features, ["OID@", *names, "SHAPE@"], spatial_reference=target) as rows:
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
    if max_vertices_per_feature not in (None, "", "#"):
        raise NotBuiltError("RasterToPolygon: max_vertices_per_feature is not built in grapy yet.")
    raster = _raster_of(in_raster, "RasterToPolygon")
    if not raster.isInteger:
        from ._runtime import ExecuteError
        raise ExecuteError("ERROR 010247: RasterToPolygon needs an integer raster.")
    import numpy as np
    from shapely import union_all
    found = list(shapes(raster._array.astype(np.int32), mask=~raster._mask, connectivity=4, transform=raster._grid.transform()))
    field, extra = _rat_field(raster, raster_field, "RasterToPolygon")
    out = _out(out_polygon_features)
    _gpkg.create(out, "Polygon", raster._grid.srid,
                 [_gpkg.Column("Id", "Integer"), _gpkg.Column("gridcode", "Integer"), *([extra] if extra else [])])
    multipart = str(create_multipart_features).upper() == "MULTIPLE_OUTER_PART"
    names = ["Id", "gridcode", *([extra.name] if extra else []), "SHAPE@"]
    with da.InsertCursor(out, names) as written:
        if multipart:
            by_value: dict = {}
            for geometry, value in found:
                by_value.setdefault(int(value), []).append(as_shape(geometry))
            for i, (value, parts) in enumerate(sorted(by_value.items()), start=1):
                written.insertRow((i, value, *([field(value)] if extra else []), union_all(parts)))
        else:
            for i, (geometry, value) in enumerate(found, start=1):
                written.insertRow((i, int(value), *([field(int(value))] if extra else []), as_shape(geometry)))
    return Result(out)


def _rat_field(raster, raster_field, tool):
    """For a raster_field other than Value: a lookup from a cell value to that field of the raster's attribute table,
    and the output column carrying it, named and typed as the field. INFERRED: the page says the field "will become
    an attribute in the output feature class" without naming the column; a value with no row is null."""
    if str(raster_field or "Value").upper() in ("VALUE", "VALUES"):
        return None, None
    from ._raster import _read_rat
    rat = raster._rat if getattr(raster, "_rat", None) is not None else _read_rat(raster.catalogPath or "")
    if rat is None:
        raise ValueError(f"{tool}: '{raster_field}' is read from the raster's attribute table, and the raster has none.")
    try:
        at, value_at = rat.index(raster_field), rat.index("Value")
    except KeyError:
        raise ValueError(f"{tool}: the raster's attribute table has no field '{raster_field}' (or no Value field).") from None
    lookup = {row[value_at]: row[at] for row in rat.rows}
    name, kind, _ = rat.fields[at]
    return (lambda value: lookup.get(int(value))), _gpkg.Column(name, kind)


def RasterToPoint(in_raster, out_point_features, raster_field="Value"):
    """A point at the centre of each cell that is not NoData, with ``pointid`` and ``grid_code`` — or, with a
    ``raster_field`` of the attribute table, that field in place of ``grid_code``, as the page writes grid_code only
    when no field is chosen (INFERRED: the column takes the field's name and type)."""
    from shapely.geometry import Point
    raster = _raster_of(in_raster, "RasterToPoint")
    field, extra = _rat_field(raster, raster_field, "RasterToPoint")
    xs, ys = raster._grid.centres()
    out = _out(out_point_features)
    value_column = extra or _gpkg.Column("grid_code", "Integer" if raster.isInteger else "Double")
    _gpkg.create(out, "Point", raster._grid.srid, [_gpkg.Column("pointid", "Integer"), value_column])
    with da.InsertCursor(out, ["pointid", value_column.name, "SHAPE@"]) as written:
        n = 0
        for r in range(raster.height):
            for c in range(raster.width):
                if not raster._mask[r, c]:
                    n += 1
                    value = raster._array[r, c].item()
                    written.insertRow((n, field(value) if extra else value, Point(xs[c], ys[r])))
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
    texts = None
    if not numeric:
        # A text field: "the output raster will contain an integer value field and a string field", as documented.
        # INFERRED, the page not saying how the integers are made: the distinct texts in ascending order take 1, 2 ….
        if str(assignment).upper() not in ("CELL_CENTER", "MOST_FREQUENT", "MAXIMUM_AREA", "MAXIMUM_LENGTH"):
            raise NotBuiltError(f"{tool}: cell_assignment '{assignment}' on a text field is not built in grapy.")
        texts = sorted({str(v) for _, v in pairs})
        code = {t: i for i, t in enumerate(texts, start=1)}
        pairs = [(s, code[str(v)]) for s, v in pairs]
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
    made = Raster._of(np.where(mask, 0, array).astype(np.int32 if integer else np.float64), mask, grid)
    if texts is not None:
        from ._raster import _Rat
        values, counts = np.unique(made._array[~made._mask], return_counts=True)
        made._rat = _Rat([("Value", "Integer", 5), ("Count", "Integer", 1), (str(value_field), "String", 0)],
                         [[int(v), int(c), texts[int(v) - 1]] for v, c in zip(values, counts)])
    return made


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


def _rows_of(in_table, where_clause=None, sort_field=None):
    """The OID name, the columns and every row (OID first, shape last) a table or layer shows."""
    from .management import _pairs, _schema, _source
    path, where = _source(in_table)
    if where_clause:
        where = f"({where}) and ({where_clause})" if where else where_clause
    columns = _schema(path)[0]
    connection, table = _gpkg.table_of(path)
    oid = _gpkg.oid_column(connection, table)
    connection.close()
    order = ", ".join(f'"{f}" {"DESC" if (d[0] if d else "").upper().startswith("DESC") else "ASC"}'
                      for f, *d in _pairs(sort_field))
    names = [c.name for c in columns]
    with da.SearchCursor(path, ["OID@", *names, "SHAPE@"], where, sql_clause=(None, f"order by {order}" if order else None)) as rows:
        return oid, columns, [list(r) for r in rows]


_DELIMITED = {".csv": ",", ".txt": ",", ".asc": ",", ".psv": "|", ".tsv": "\t", ".tab": "\t"}


def ExportTable(in_table, out_table, where_clause=None, use_field_alias_as_name="NOT_USE_ALIAS", field_mapping=None,
                sort_field=None):
    """A table's rows — a view's selected rows, those of ``where_clause``, in ``sort_field`` order — written to a new
    table without their shapes, or to a delimited text file by its extension as documented: comma for ``.csv``,
    ``.txt`` and ``.asc``, pipe for ``.psv``, tab for ``.tsv`` and ``.tab``. INFERRED: a text file starts with a row of
    field names, the object ID first under its own name; a null is an empty value. A field mapping is a FieldMappings
    object; its string form is not built."""
    import csv
    from .management import _mappings
    mappings = _mappings("ExportTable", field_mapping)
    oid, columns, rows = _rows_of(in_table, where_clause, sort_field)
    names = [c.name for c in columns]
    if mappings is not None:
        source = str(in_table)
        columns = [_gpkg.Column(m._output.name, m._output.type) for m in mappings.fieldMappings]
        rows = [[r[0], *[m._value(source, dict(zip((n.lower() for n in names), r[1:-1]))) for m in mappings.fieldMappings],
                 r[-1]] for r in rows]
        names = [c.name for c in columns]
    extension = os.path.splitext(str(out_table))[1].lower()
    if extension in _DELIMITED:
        with open(out_table, "w", encoding="utf-8", newline="") as written:
            writer = csv.writer(written, delimiter=_DELIMITED[extension])
            writer.writerow([oid, *names])
            for row in rows:
                writer.writerow(["" if v is None else v for v in row[:-1]])
        return Result(str(out_table))
    if extension not in ("", ".gpkg") and ".gpkg" not in str(out_table).lower():
        raise NotBuiltError(f"arcpy.conversion.ExportTable: writing '{extension}' is not built in grapy.")
    out = _out(out_table)
    _gpkg.create(out, "Geometry", 0, columns)
    with da.InsertCursor(out, [*names, "SHAPE@"]) as written:
        for row in rows:
            written.insertRow((*row[1:-1], None))
    return Result(out)


def TableToTable(in_rows, out_path, out_name, where_clause=None, *_, **__):
    """ExportTable into a folder or GeoPackage by name, as the older tool was called."""
    return ExportTable(in_rows, os.path.join(str(out_path), str(out_name)), where_clause)


def _geodatabase(tool, value):
    path = str(value)
    if not path.lower().endswith(".gpkg"):
        raise NotBuiltError(f"arcpy.conversion.{tool}: grapy's workspace is a GeoPackage (management.CreateFileGDB "
                            f"answers one); '{path}' is not.")
    return path


def FeatureClassToGeodatabase(Input_Features, Output_Geodatabase):
    """Each feature class copied into a GeoPackage under its own name."""
    from .management import _stem_of
    target = _geodatabase("FeatureClassToGeodatabase", Output_Geodatabase)
    from .management import _list
    for each in _list(Input_Features):
        ExportFeatures(each, f"{target}/{_stem_of(each)}")
    return Result(target)


def TableToGeodatabase(Input_Table, Output_Geodatabase, config_keyword=None):
    """Each table copied into a GeoPackage under its own name, without shapes."""
    from .management import _list, _stem_of
    target = _geodatabase("TableToGeodatabase", Output_Geodatabase)
    for each in _list(Input_Table):
        ExportTable(each, f"{target}/{_stem_of(each)}")
    return Result(target)


# ---- Excel: Office Open XML (ECMA-376), written and read with the standard library ---------------------------------------

_XLSX_MAIN = "http://schemas.openxmlformats.org/spreadsheetml/2006/main"
_XLSX_REL = "http://schemas.openxmlformats.org/officeDocument/2006/relationships"
_PACKAGE_REL = "http://schemas.openxmlformats.org/package/2006/relationships"


def _column_letters(index):
    text = ""
    index += 1
    while index:
        index, rest = divmod(index - 1, 26)
        text = chr(65 + rest) + text
    return text


def _cell(reference, value):
    from xml.sax.saxutils import escape
    if value is None:
        return ""
    if isinstance(value, bool):
        return f'<c r="{reference}"><v>{int(value)}</v></c>'
    if isinstance(value, (int, float)):
        return f'<c r="{reference}"><v>{value!r}</v></c>'
    return f'<c r="{reference}" t="inlineStr"><is><t xml:space="preserve">{escape(str(value))}</t></is></c>'


def TableToExcel(Input_Table, Output_Excel_File, Use_field_alias_as_column_header="NAME",
                 Use_domain_and_subtype_description="CODE"):
    """Tables written as sheets of an ``.xlsx`` workbook, a sheet per table named after it (a number added to a name
    taken), as documented. INFERRED: a sheet's first row holds the field names, the object ID first; a null is an
    empty cell; text is written as text and numbers as numbers. ``.xls`` and domain descriptions are not built."""
    import zipfile
    from xml.sax.saxutils import escape
    from .management import _list, _stem_of
    if str(Output_Excel_File).lower().endswith(".xls"):
        raise NotBuiltError("arcpy.conversion.TableToExcel: .xls (Excel's binary format) is not built in grapy; .xlsx is.")
    if str(Use_domain_and_subtype_description or "CODE").upper() == "DESCRIPTION":
        raise NotBuiltError("arcpy.conversion.TableToExcel: domain descriptions are not built in grapy.")
    sheets, taken = [], set()
    for each in _list(Input_Table):
        oid, columns, rows = _rows_of(each)
        name, n = _stem_of(each)[:31], 0
        while name.lower() in taken:
            n += 1
            name = f"{_stem_of(each)[:28]}_{n}"
        taken.add(name.lower())
        lines = ['<row r="1">' + "".join(_cell(f"{_column_letters(i)}1", h) for i, h in enumerate([oid, *[c.name for c in columns]])) + "</row>"]
        for r, row in enumerate(rows, start=2):
            lines.append(f'<row r="{r}">' + "".join(_cell(f"{_column_letters(i)}{r}", v) for i, v in enumerate(row[:-1])) + "</row>")
        sheets.append((name, f'<?xml version="1.0" encoding="UTF-8" standalone="yes"?><worksheet xmlns="{_XLSX_MAIN}">'
                             f'<sheetData>{"".join(lines)}</sheetData></worksheet>'))
    types = "".join(f'<Override PartName="/xl/worksheets/sheet{i}.xml" ContentType="application/vnd.openxmlformats-'
                    f'officedocument.spreadsheetml.worksheet+xml"/>' for i in range(1, len(sheets) + 1))
    with zipfile.ZipFile(Output_Excel_File, "w", zipfile.ZIP_DEFLATED) as package:
        package.writestr("[Content_Types].xml",
                         '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/'
                         'package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-'
                         'package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override '
                         'PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.'
                         f'sheet.main+xml"/>{types}</Types>')
        package.writestr("_rels/.rels", f'<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships '
                         f'xmlns="{_PACKAGE_REL}"><Relationship Id="rId1" Type="{_XLSX_REL}/officeDocument" '
                         'Target="xl/workbook.xml"/></Relationships>')
        package.writestr("xl/workbook.xml", f'<?xml version="1.0" encoding="UTF-8" standalone="yes"?><workbook '
                         f'xmlns="{_XLSX_MAIN}" xmlns:r="{_XLSX_REL}"><sheets>' + "".join(
                             f'<sheet name="{escape(name)}" sheetId="{i}" r:id="rId{i}"/>' for i, (name, _) in enumerate(sheets, 1))
                         + "</sheets></workbook>")
        package.writestr("xl/_rels/workbook.xml.rels", f'<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships '
                         f'xmlns="{_PACKAGE_REL}">' + "".join(
                             f'<Relationship Id="rId{i}" Type="{_XLSX_REL}/worksheet" Target="worksheets/sheet{i}.xml"/>'
                             for i in range(1, len(sheets) + 1)) + "</Relationships>")
        for i, (_, xml) in enumerate(sheets, 1):
            package.writestr(f"xl/worksheets/sheet{i}.xml", xml)
    return Result(str(Output_Excel_File))


_DATE_FORMATS = set(range(14, 23)) | {45, 46, 47}


def _split_reference(reference):
    import re
    match = re.fullmatch(r"([A-Za-z]+)(\d+)", reference)
    letters, row = match.group(1).upper(), int(match.group(2))
    column = 0
    for ch in letters:
        column = column * 26 + (ord(ch) - 64)
    return column - 1, row


def _read_sheet(path, sheet):
    """{(column, row): (kind, text)} of one sheet; kind is 'n' (number), 's' (text), 'e' (error) or 'd' (a date)."""
    import posixpath
    import zipfile
    from xml.etree import ElementTree
    ns = {"m": _XLSX_MAIN}
    with zipfile.ZipFile(path) as package:
        workbook = ElementTree.fromstring(package.read("xl/workbook.xml"))
        relations = ElementTree.fromstring(package.read("xl/_rels/workbook.xml.rels"))
        targets = {r.get("Id"): r.get("Target") for r in relations}
        listed = workbook.findall("m:sheets/m:sheet", ns)
        if _given(sheet):
            chosen = [s for s in listed if s.get("name") == str(sheet)]
            if not chosen:
                raise NotBuiltError(f"arcpy.conversion.ExcelToTable: '{sheet}' is no sheet of the workbook; named ranges "
                                    "are not built in grapy.")
        else:
            chosen = listed[:1]
        target = targets[chosen[0].get(f"{{{_XLSX_REL}}}id")]
        part = target.lstrip("/") if target.startswith("/") else posixpath.normpath(posixpath.join("xl", target))
        shared = []
        if "xl/sharedStrings.xml" in package.namelist():
            for item in ElementTree.fromstring(package.read("xl/sharedStrings.xml")).findall("m:si", ns):
                shared.append("".join(t.text or "" for t in item.iter(f"{{{_XLSX_MAIN}}}t")))
        dates = set()
        if "xl/styles.xml" in package.namelist():
            styles = ElementTree.fromstring(package.read("xl/styles.xml"))
            custom = {int(f.get("numFmtId")): f.get("formatCode", "") for f in styles.findall("m:numFmts/m:numFmt", ns)}
            for i, xf in enumerate(styles.findall("m:cellXfs/m:xf", ns)):
                number = int(xf.get("numFmtId", "0"))
                code = custom.get(number, "").lower()
                plain = code.split(";")[0]
                import re
                plain = re.sub(r'"[^"]*"|\[[^\]]*\]', "", plain)
                if number in _DATE_FORMATS or any(ch in plain for ch in "dyh") or ("m" in plain and "s" in plain):
                    dates.add(i)
        root = ElementTree.fromstring(package.read(part))
    cells = {}
    for c in root.iter(f"{{{_XLSX_MAIN}}}c"):
        kind = c.get("t", "n")
        value = c.find("m:v", ns)
        if kind == "s":
            entry = ("s", shared[int(value.text)])
        elif kind == "inlineStr":
            entry = ("s", "".join(t.text or "" for t in c.iter(f"{{{_XLSX_MAIN}}}t")))
        elif kind == "str":
            entry = ("s", value.text if value is not None else "")
        elif kind == "e":
            entry = ("e", value.text if value is not None else "")
        elif value is None or value.text is None:
            continue
        else:
            entry = ("d" if int(c.get("s", "0")) in dates else "n", value.text)
        cells[_split_reference(c.get("r"))] = entry
    return cells


def ExcelToTable(Input_Excel_File, Output_Table, Sheet=None, field_names_row=1, cell_range=None):
    """A sheet of an ``.xlsx`` workbook as a table: ``field_names_row`` names the fields (0: ``COL_A``, ``COL_B`` …, and
    an empty header cell is named so too), and each column's type comes from all its values as documented — whole
    numbers an integer type by size, numbers with decimals double, a column of mixed kinds text — and ``#N/A`` is null.
    INFERRED: whole numbers are short within ±32767, long within 32 bits, and big integer beyond; an empty cell is
    null; an empty column is text; names are made valid with ValidateFieldName. Date cells and ``.xls`` are not built."""
    from . import ValidateFieldName
    if str(Input_Excel_File).lower().endswith(".xls"):
        raise NotBuiltError("arcpy.conversion.ExcelToTable: .xls (Excel's binary format) is not built in grapy; .xlsx is.")
    cells = _read_sheet(Input_Excel_File, Sheet)
    if not cells:
        raise NotBuiltError("arcpy.conversion.ExcelToTable of an empty sheet is not built in grapy.")
    if _given(cell_range):
        first, last = str(cell_range).split(":")
        (c0, r0), (c1, r1) = _split_reference(first), _split_reference(last)
    else:
        c0, c1 = min(c for c, _ in cells), max(c for c, _ in cells)
        r0, r1 = min(r for _, r in cells), max(r for _, r in cells)
    header = int(field_names_row or 0)
    data_rows = [r for r in range(r0, r1 + 1) if r != header and (header == 0 or r > header)]
    names, taken = [], set()
    for c in range(c0, c1 + 1):
        text = cells.get((c, header), ("s", ""))[1] if header else ""
        name = ValidateFieldName(str(text)) if str(text).strip() else f"COL_{_column_letters(c)}"
        base, n = name, 0
        while name.lower() in taken:
            n += 1
            name = f"{base}_{n}"
        taken.add(name.lower())
        names.append(name)
    columns, values = [], []
    for i, c in enumerate(range(c0, c1 + 1)):
        entries = [cells.get((c, r)) for r in data_rows]
        if any(e and e[0] == "d" for e in entries):
            raise NotBuiltError("arcpy.conversion.ExcelToTable: date cells are not built in grapy.")
        kinds = {e[0] for e in entries if e and not (e[0] == "e" and e[1] == "#N/A")}
        if kinds == {"n"}:
            numbers = [float(e[1]) if e and e[0] == "n" else None for e in entries]
            whole = all(v is None or v.is_integer() for v in numbers)
            present = [v for v in numbers if v is not None]
            if whole:
                biggest = max(abs(v) for v in present)
                kind = "SmallInteger" if biggest <= 32767 else "Integer"
                column = [None if v is None else int(v) for v in numbers]
            else:
                kind, column = "Double", numbers
        else:
            kind = "String"
            column = [None if not e or (e[0] == "e" and e[1] == "#N/A") else e[1] for e in entries]
        columns.append(_gpkg.Column(names[i], kind))
        values.append(column)
    out = _out(Output_Table)
    _gpkg.create(out, "Geometry", 0, columns)
    with da.InsertCursor(out, [*names, "SHAPE@"]) as written:
        for row in zip(*values):
            written.insertRow((*row, None))
    return Result(out)


def _given(value):
    return value is not None and str(value).strip() not in ("", "#")


# ---- GPX 1.1 (https://www.topografix.com/GPX/1/1/) ------------------------------------------------------------------------

_GPX = "http://www.topografix.com/GPX/1/1"


def GPXtoFeatures(Input_GPX_File, Output_Feature_class, Output_Type="POINTS"):
    """A GPX 1.0 or 1.1 file's waypoints and track points as points with ``Name``, ``Descript``, ``Type`` (``WPT`` or
    ``TRKPT``), ``Comment``, ``Symbol``, ``DateTimeS``, ``Elevation`` and ``DateTime``, a track point carrying its
    track's name and description; or, with ``TRACKS_AS_LINES``, a line per track name through its points, with
    ``Name``, ``Descript`` and ``Type`` — as documented, in WGS 1984. INFERRED: ``DateTime`` holds the file's time as
    written; a line's ``Type`` is ``TRKPT``, the points it is made of; routes are not read. grapy's shapes are 2D, so
    elevation is kept in ``Elevation`` and not as z."""
    from xml.etree import ElementTree
    from shapely.geometry import LineString, Point as ShapelyPoint
    root = ElementTree.parse(Input_GPX_File).getroot()
    namespace = root.tag[1:root.tag.index("}")] if root.tag.startswith("{") else ""
    if namespace not in ("http://www.topografix.com/GPX/1/1", "http://www.topografix.com/GPX/1/0"):
        from ._runtime import ExecuteError
        raise ExecuteError("ERROR 000814: the file is not GPX 1.0 or 1.1.")

    def child(element, name):
        found = element.find(f"{{{namespace}}}{name}")
        return found.text if found is not None else None

    points = []
    for wpt in root.findall(f"{{{namespace}}}wpt"):
        points.append((child(wpt, "name"), child(wpt, "desc"), "WPT", child(wpt, "cmt"), child(wpt, "sym"),
                       child(wpt, "time"), child(wpt, "ele"), float(wpt.get("lon")), float(wpt.get("lat"))))
    for trk in root.findall(f"{{{namespace}}}trk"):
        name, description = child(trk, "name"), child(trk, "desc")
        for pt in trk.iter(f"{{{namespace}}}trkpt"):
            points.append((name, description, "TRKPT", child(pt, "cmt"), child(pt, "sym"), child(pt, "time"),
                           child(pt, "ele"), float(pt.get("lon")), float(pt.get("lat"))))
    out = _out(Output_Feature_class)
    if str(Output_Type or "POINTS").upper() == "TRACKS_AS_LINES":
        columns = [_gpkg.Column("Name", "String"), _gpkg.Column("Descript", "String"), _gpkg.Column("Type", "String")]
        _gpkg.create(out, "Polyline", 4326, columns)
        lines: dict = {}
        for p in points:
            if p[2] == "TRKPT":
                lines.setdefault(p[0], [p[1], []])[1].append((p[7], p[8]))
        with da.InsertCursor(out, ["Name", "Descript", "Type", "SHAPE@"]) as written:
            for name, (description, coordinates) in lines.items():
                if len(coordinates) >= 2:
                    written.insertRow((name, description, "TRKPT", LineString(coordinates)))
        return Result(out)
    columns = [_gpkg.Column(n, t) for n, t in (("Name", "String"), ("Descript", "String"), ("Type", "String"),
                                                 ("Comment", "String"), ("Symbol", "String"), ("DateTimeS", "String"),
                                                 ("Elevation", "Double"), ("DateTime", "Date"))]
    _gpkg.create(out, "Point", 4326, columns)
    with da.InsertCursor(out, [*[c.name for c in columns], "SHAPE@"]) as written:
        for name, description, kind, comment, symbol, time, elevation, lon, lat in points:
            written.insertRow((name, description, kind, comment, symbol, time,
                               float(elevation) if elevation is not None else None, time, ShapelyPoint(lon, lat)))
    return Result(out)


def FeaturesToGPX(in_features, out_gpx_file, name_field=None, description_field=None, z_field=None, date_field=None):
    """Points and multipoints as GPX waypoints, or lines as tracks of track points — never both — in WGS 1984, with
    ``name``, ``desc``, ``ele`` and ``time`` from the fields named, as documented. INFERRED: GPX 1.1; a line's part is a
    track segment and its ``ele`` and ``time`` go on each of its points; a time is written as the field holds it, a
    date-time object in ISO 8601."""
    from xml.sax.saxutils import escape
    from ._reference import SpatialReference
    from .management import _schema, _source
    path, where = _source(in_features)
    columns, kind, _, _ = _schema(path)
    by_name = {c.name.lower(): c.name for c in columns}
    fields = [by_name[str(f).lower()] if _given(f) else None for f in (name_field, description_field, z_field, date_field)]
    wanted = [f for f in fields if f]

    def text(value):
        if value is None:
            return None
        return value.isoformat() if hasattr(value, "isoformat") else str(value)

    lines = ['<?xml version="1.0" encoding="UTF-8"?>', f'<gpx version="1.1" creator="grapy" xmlns="{_GPX}">']
    with da.SearchCursor(path, [*wanted, "SHAPE@"], where, spatial_reference=SpatialReference(4326)) as rows:
        for row in rows:
            values = dict(zip(wanted, row[:-1]))
            name, description, elevation, time = [text(values.get(f)) if f else None for f in fields]
            if row[-1] is None:
                continue
            shape = row[-1]._shape
            extra_point = "".join(f"<{tag}>{escape(v)}</{tag}>" for tag, v in (("ele", elevation), ("time", time)) if v is not None)
            labels = "".join(f"<{tag}>{escape(v)}</{tag}>" for tag, v in (("name", name), ("desc", description)) if v is not None)
            if kind in ("Point", "Multipoint"):
                for part in getattr(shape, "geoms", [shape]):
                    lines.append(f'<wpt lat="{part.y!r}" lon="{part.x!r}">{extra_point}{labels}</wpt>')
            elif kind == "Polyline":
                segments = "".join("<trkseg>" + "".join(f'<trkpt lat="{y!r}" lon="{x!r}">{extra_point}</trkpt>'
                                                        for x, y in [c[:2] for c in part.coords]) + "</trkseg>"
                                   for part in getattr(shape, "geoms", [shape]))
                lines.append(f"<trk>{labels}{segments}</trk>")
            else:
                raise NotBuiltError("arcpy.conversion.FeaturesToGPX takes points, multipoints or lines.")
    lines.append("</gpx>")
    with open(out_gpx_file, "w", encoding="utf-8") as written:
        written.write("\n".join(lines))
    return Result(str(out_gpx_file))


# ---- Esri ASCII grid and binary float grid --------------------------------------------------------------------------------

def _square_cells(raster, tool):
    import numpy
    g = raster._grid
    if not numpy.isclose(g.width, g.height):
        raise NotBuiltError(f"arcpy.conversion.{tool}: cells that are not square have no single CELLSIZE; not built in grapy.")
    return g


def _number(value):
    return str(int(value)) if float(value).is_integer() else repr(float(value))


def RasterToASCII(in_raster, out_ascii_file):
    """A raster as an Esri ASCII grid: ``NCOLS``, ``NROWS``, ``XLLCORNER``, ``YLLCORNER``, ``CELLSIZE`` and
    ``NODATA_VALUE`` in that order, then the rows from the top, as documented. INFERRED: NoData is written as -9999,
    as RasterToFloat's page says it does; a whole value is written without a decimal point; rows end with a line feed."""
    import numpy
    from ._runtime import ExecuteError
    raster = _raster_of(in_raster, "RasterToASCII")
    g = _square_cells(raster, "RasterToASCII")
    if numpy.any(raster._array[~raster._mask] == -9999):
        raise ExecuteError("ERROR 999999: a valid cell holds -9999, the NoData value written.")
    lines = [f"NCOLS {g.columns}", f"NROWS {g.rows}", f"XLLCORNER {_number(g.left)}", f"YLLCORNER {_number(g.bottom)}",
             f"CELLSIZE {_number(g.width)}", "NODATA_VALUE -9999"]
    for values, mask in zip(raster._array, raster._mask):
        lines.append(" ".join("-9999" if m else _number(v) for v, m in zip(values.tolist(), mask)))
    with open(out_ascii_file, "w", encoding="ascii", newline="\n") as written:
        written.write("\n".join(lines) + "\n")
    return Result(str(out_ascii_file))


def _header(lines):
    header = {}
    while lines and lines[0].split() and lines[0].split()[0].replace("_", "").isalpha():
        key, value = lines.pop(0).split()[:2]
        header[key.upper()] = value
    return header


def _grid_from_header(header):
    from ._raster import _Grid
    from ._runtime import env
    from ._reference import reference_of
    columns, rows, size = int(header["NCOLS"]), int(header["NROWS"]), float(header["CELLSIZE"])
    if "XLLCENTER" in header:
        left, bottom = float(header["XLLCENTER"]) - size / 2, float(header["YLLCENTER"]) - size / 2
    else:
        left, bottom = float(header["XLLCORNER"]), float(header["YLLCORNER"])
    reference = reference_of(env.outputCoordinateSystem) if env.outputCoordinateSystem is not None else None
    return _Grid(left, bottom + rows * size, size, size, rows, columns, reference.factoryCode if reference else 0)


def ASCIIToRaster(in_ascii_file, out_raster, data_type="INTEGER"):
    """An Esri ASCII grid as a raster, its origin a corner (``XLLCORNER``) or a centre (``XLLCENTER``), its
    ``NODATA_VALUE`` cells NoData, as documented. INFERRED: ``INTEGER`` is the default; an integer raster of a file
    holding fractions is not built, the page not saying how they are cut; with no ``NODATA_VALUE`` no cell is NoData;
    the reference is ``env.outputCoordinateSystem``, the file having none."""
    import numpy
    from ._raster import Raster
    with open(in_ascii_file, encoding="ascii") as read:
        lines = read.read().splitlines()
    header = _header(lines)
    grid = _grid_from_header(header)
    values = numpy.array([float(v) for line in lines for v in line.split()]).reshape(grid.rows, grid.columns)
    mask = values == float(header["NODATA_VALUE"]) if "NODATA_VALUE" in header else numpy.zeros(values.shape, bool)
    if str(data_type or "INTEGER").upper() == "INTEGER":
        if not numpy.all(numpy.mod(values[~mask], 1) == 0):
            raise NotBuiltError("arcpy.conversion.ASCIIToRaster: fractions in an INTEGER raster are not built in grapy.")
        values = values.astype(numpy.int32)
    return Result(Raster._of(values, mask, grid).save(out_raster))


def RasterToFloat(in_raster, out_float_file):
    """A raster as a ``.flt`` of 32-bit IEEE floats, row by row from the top, with its ``.hdr``: ``NCOLS``, ``NROWS``,
    ``XLLCORNER``, ``YLLCORNER``, ``CELLSIZE``, ``NODATA_VALUE`` -9999 and ``BYTEORDER``, as documented. INFERRED: the
    bytes are little-endian (``LSBFIRST``), as the page says an Intel system writes them."""
    import numpy
    from ._runtime import ExecuteError
    if not str(out_float_file).lower().endswith(".flt"):
        raise ExecuteError("ERROR 000728: the output must have the .flt extension.")
    raster = _raster_of(in_raster, "RasterToFloat")
    g = _square_cells(raster, "RasterToFloat")
    if numpy.any(raster._array[~raster._mask] == -9999):
        raise ExecuteError("ERROR 999999: a valid cell holds -9999, the NoData value written.")
    numpy.where(raster._mask, -9999.0, raster._array).astype("<f4").tofile(out_float_file)
    header = [f"NCOLS {g.columns}", f"NROWS {g.rows}", f"XLLCORNER {_number(g.left)}", f"YLLCORNER {_number(g.bottom)}",
              f"CELLSIZE {_number(g.width)}", "NODATA_VALUE -9999", "BYTEORDER LSBFIRST"]
    with open(str(out_float_file)[:-4] + ".hdr", "w", encoding="ascii", newline="\n") as written:
        written.write("\n".join(header) + "\n")
    return Result(str(out_float_file))


def FloatToRaster(in_float_file, out_raster):
    """A ``.flt`` binary float grid and its ``.hdr`` as a 32-bit float raster, ``NODATA_VALUE`` cells NoData. INFERRED:
    the reference is ``env.outputCoordinateSystem``, the files having none."""
    import numpy
    from ._raster import Raster
    with open(str(in_float_file)[:-4] + ".hdr", encoding="ascii") as read:
        header = _header(read.read().splitlines())
    grid = _grid_from_header(header)
    order = "<" if header.get("BYTEORDER", "LSBFIRST").upper() == "LSBFIRST" else ">"
    values = numpy.fromfile(in_float_file, dtype=f"{order}f4").reshape(grid.rows, grid.columns).astype(numpy.float32)
    mask = values == numpy.float32(float(header["NODATA_VALUE"])) if "NODATA_VALUE" in header else numpy.zeros(values.shape, bool)
    return Result(Raster._of(values, mask, grid).save(out_raster))


# ---- KML 2.2 (OGC 07-147r2) ----------------------------------------------------------------------------------------------

def _kml_document(path):
    import zipfile
    from xml.etree import ElementTree
    if zipfile.is_zipfile(path):
        with zipfile.ZipFile(path) as package:
            names = [n for n in package.namelist() if n.lower().endswith(".kml")]
            chosen = "doc.kml" if "doc.kml" in names else (names[0] if names else None)
            if chosen is None:
                raise NotBuiltError("arcpy.conversion.KMLToLayer: the KMZ holds no .kml document.")
            return ElementTree.fromstring(package.read(chosen))
    return ElementTree.parse(path).getroot()


def _kml_coordinates(element, namespace):
    found = element.find(f"{{{namespace}}}coordinates")
    text = found.text if found is not None and found.text else ""
    return [tuple(float(v) for v in item.split(",")[:2]) for item in text.split()]


def _kml_shapes(geometry, namespace):
    """(kind, shapely shape) of each simple geometry in a KML geometry, MultiGeometry opened."""
    from shapely.geometry import LineString, Point as ShapelyPoint, Polygon as ShapelyPolygon
    tag = geometry.tag.split("}")[-1]
    if tag == "Point":
        return [("Points", ShapelyPoint(_kml_coordinates(geometry, namespace)[0]))]
    if tag in ("LineString", "LinearRing"):
        return [("Lines", LineString(_kml_coordinates(geometry, namespace)))]
    if tag == "Polygon":
        outer = geometry.find(f"{{{namespace}}}outerBoundaryIs/{{{namespace}}}LinearRing")
        inner = geometry.findall(f"{{{namespace}}}innerBoundaryIs/{{{namespace}}}LinearRing")
        return [("Polygons", ShapelyPolygon(_kml_coordinates(outer, namespace),
                                            [_kml_coordinates(r, namespace) for r in inner]))]
    if tag == "MultiGeometry":
        return [s for child in geometry for s in _kml_shapes(child, namespace)]
    raise NotBuiltError(f"arcpy.conversion.KMLToLayer: the KML geometry '{tag}' is not built in grapy.")


def KMLToLayer(in_kml_file, output_folder, output_data=None, include_groundoverlay="NO_GROUNDOVERLAY", out_suffix=None):
    """A KML 2.2 or KMZ file's placemarks as feature classes in WGS 1984 — ``Points``, ``Lines`` and ``Polygons`` in a
    feature dataset ``Placemarks`` of a workspace named ``output_data`` (the file's name when none is given), as
    documented. INFERRED: the workspace is a GeoPackage (see CreateFileGDB) and the dataset a name prefix (see
    CreateFeatureDataset); the fields are ``Name`` (the placemark's name), ``FolderPath`` (the names of its document
    and folders, joined with ``/``) and ``PopupInfo`` (its description) — the page names only ``PopupInfo``, and the other
    fields arcpy writes are not documented, so they are not written; a MultiGeometry's parts of one kind make one
    multipart feature; altitude is dropped, grapy's shapes being 2D. The layer file, ground overlays, models and
    ``out_suffix`` are not built."""
    import shapely
    from . import da
    from .management import CreateFeatureDataset, CreateFileGDB
    if str(include_groundoverlay or "NO_GROUNDOVERLAY").upper() == "GROUNDOVERLAY":
        raise NotBuiltError("arcpy.conversion.KMLToLayer: ground overlays are not built in grapy.")
    if _given(out_suffix):
        raise NotBuiltError("arcpy.conversion.KMLToLayer: out_suffix is not built in grapy; the page does not say how it "
                            "joins the names.")
    root = _kml_document(str(in_kml_file))
    namespace = root.tag[1:root.tag.index("}")] if root.tag.startswith("{") else ""
    found = {"Points": [], "Lines": [], "Polygons": []}

    def walk(element, folders):
        for child in element:
            tag = child.tag.split("}")[-1]
            if tag in ("Document", "Folder"):
                name = child.find(f"{{{namespace}}}name")
                walk(child, folders + ([name.text] if name is not None and name.text else []))
            elif tag == "Placemark":
                name = child.find(f"{{{namespace}}}name")
                description = child.find(f"{{{namespace}}}description")
                by_kind: dict = {}
                for geometry in child:
                    if geometry.tag.split("}")[-1] in ("Point", "LineString", "LinearRing", "Polygon", "MultiGeometry", "Model"):
                        for kind, shape in _kml_shapes(geometry, namespace):
                            by_kind.setdefault(kind, []).append(shape)
                for kind, shapes in by_kind.items():
                    merged = shapes[0] if len(shapes) == 1 else shapely.union_all(shapes) if kind == "Polygons" else (
                        shapely.MultiPoint(shapes) if kind == "Points" else shapely.MultiLineString(shapes))
                    found[kind].append((name.text if name is not None else None, "/".join(folders),
                                        description.text if description is not None else None, merged))

    walk(root, [])
    stem = str(output_data) if _given(output_data) else os.path.splitext(os.path.basename(str(in_kml_file)))[0]
    target = os.path.join(str(output_folder), stem + ".gpkg")
    if not os.path.exists(target):
        CreateFileGDB(str(output_folder), stem)
    dataset = f"{target}/Placemarks"
    from .management import _feature_dataset_of
    if _feature_dataset_of(f"{dataset}/x") is None:
        CreateFeatureDataset(target, "Placemarks", 4326)
    columns = [_gpkg.Column("Name", "String"), _gpkg.Column("FolderPath", "String"), _gpkg.Column("PopupInfo", "String")]
    shapes = {"Points": "Multipoint", "Lines": "Polyline", "Polygons": "Polygon"}
    for kind, rows in found.items():
        if not rows:
            continue
        kinds = {r[3].geom_type for r in rows}
        shape = "Point" if kind == "Points" and kinds == {"Point"} else shapes[kind]
        path = f"{dataset}/{kind}"
        _gpkg.create(path, shape, 4326, columns)
        with da.InsertCursor(path, ["Name", "FolderPath", "PopupInfo", "SHAPE@"]) as written:
            for name, folder, popup, geometry in rows:
                if shape == "Multipoint" and geometry.geom_type == "Point":
                    geometry = shapely.MultiPoint([geometry])
                written.insertRow((name, folder, popup, geometry))
    return Result(target, target)


# ---- GTFS (https://gtfs.org/documentation/schedule/reference/) -----------------------------------------------------------
#
# GTFS coordinates are WGS 1984, as the specification says. INFERRED, the pages naming no fields: each column of the
# file becomes a field of the same name, as the pages' note on shortened shapefile names implies; columns the
# specification types as latitude, longitude, float or non-negative float are doubles, its enums and integers short
# or long integers, the rest text.

_GTFS_DOUBLES = {"stop_lat", "stop_lon", "shape_pt_lat", "shape_pt_lon", "shape_dist_traveled"}
_GTFS_INTEGERS = {"location_type": "SmallInteger", "wheelchair_boarding": "SmallInteger", "route_type": "SmallInteger",
                  "route_sort_order": "Integer", "continuous_pickup": "SmallInteger", "continuous_drop_off": "SmallInteger",
                  "shape_pt_sequence": "Integer"}


def _gtfs_rows(path):
    import csv
    with open(path, encoding="utf-8-sig", newline="") as read:
        reader = csv.DictReader(read)
        return reader.fieldnames or [], [dict(r) for r in reader]


def _gtfs_value(column, text):
    if text is None or str(text).strip() == "":
        return None
    if column in _GTFS_DOUBLES:
        return float(text)
    if column in _GTFS_INTEGERS:
        return int(float(text))
    return text


def _gtfs_column(column):
    return _gpkg.Column(column, "Double" if column in _GTFS_DOUBLES else _GTFS_INTEGERS.get(column, "String"))


def _gtfs_sibling(path, name):
    other = os.path.join(os.path.dirname(os.path.abspath(str(path))), name)
    return other if os.path.exists(other) else None


def GTFSStopsToFeatures(in_gtfs_stops_file, out_feature_class):
    """A point for each stop of a GTFS ``stops.txt``, in WGS 1984, with its columns as fields; a stop of location type
    3 or 4 with no coordinates is left out, as documented. With ``stop_times.txt``, ``trips.txt`` and ``routes.txt``
    beside it, ``route_info`` holds the JSON ``{"route_id": […], "route_type": […]}`` of the routes serving the stop,
    as documented. INFERRED: the fields as the module note says; route lists sorted, route types as text as in the
    page's example, the JSON written without spaces; a stop of another type with no coordinates keeps an empty point."""
    import json
    from shapely.geometry import Point as ShapelyPoint
    columns, rows = _gtfs_rows(str(in_gtfs_stops_file))
    serving: dict = {}
    times, trips, routes = (_gtfs_sibling(in_gtfs_stops_file, n) for n in ("stop_times.txt", "trips.txt", "routes.txt"))
    if times and trips and routes:
        route_type = {r["route_id"]: r.get("route_type") for r in _gtfs_rows(routes)[1]}
        trip_route = {t["trip_id"]: t["route_id"] for t in _gtfs_rows(trips)[1]}
        for stop_time in _gtfs_rows(times)[1]:
            route = trip_route.get(stop_time.get("trip_id"))
            if route is not None:
                serving.setdefault(stop_time.get("stop_id"), set()).add(route)
    fields = [_gtfs_column(c) for c in columns] + ([_gpkg.Column("route_info", "String")] if serving or (times and trips and routes) else [])
    out = _out(out_feature_class)
    _gpkg.create(out, "Point", 4326, fields)
    with da.InsertCursor(out, [*[f.name for f in fields], "SHAPE@"]) as written:
        for row in rows:
            lat, lon = _gtfs_value("stop_lat", row.get("stop_lat")), _gtfs_value("stop_lon", row.get("stop_lon"))
            if (lat is None or lon is None) and str(row.get("location_type", "")).strip() in ("3", "4"):
                continue
            values = [_gtfs_value(c, row.get(c)) for c in columns]
            if len(fields) > len(columns):
                ids = sorted(serving.get(row.get("stop_id"), ()))
                kinds = sorted({str(route_type.get(i)) for i in ids if route_type.get(i) is not None})
                values.append(json.dumps({"route_id": ids, "route_type": kinds}, separators=(",", ":")))
            written.insertRow((*values, ShapelyPoint(lon, lat) if lat is not None and lon is not None else ShapelyPoint()))
    return Result(out)


def GTFSShapesToFeatures(in_gtfs_shapes_file, out_feature_class):
    """A line for each ``shape_id`` of a GTFS ``shapes.txt``, its points joined by straight lines in
    ``shape_pt_sequence`` order, as documented and as the specification orders them, in WGS 1984. With ``trips.txt``
    and ``routes.txt`` beside it, each line carries its route's ``routes.txt`` columns — one line per shape and route
    when a shape serves several, as documented. INFERRED: the fields are ``shape_id`` and then the route's columns;
    lines in order of first appearance, routes by ``route_id``."""
    from shapely.geometry import LineString
    _, rows = _gtfs_rows(str(in_gtfs_shapes_file))
    points: dict = {}
    for row in rows:
        points.setdefault(row["shape_id"], []).append((int(float(row["shape_pt_sequence"])), float(row["shape_pt_lon"]),
                                                       float(row["shape_pt_lat"])))
    trips, routes = _gtfs_sibling(in_gtfs_shapes_file, "trips.txt"), _gtfs_sibling(in_gtfs_shapes_file, "routes.txt")
    route_columns, by_shape, route_rows = [], {}, {}
    if trips and routes:
        route_columns, listed = _gtfs_rows(routes)
        route_rows = {r["route_id"]: r for r in listed}
        for trip in _gtfs_rows(trips)[1]:
            if trip.get("shape_id"):
                by_shape.setdefault(trip["shape_id"], set()).add(trip["route_id"])
    fields = [_gpkg.Column("shape_id", "String")] + [_gtfs_column(c) for c in route_columns]
    out = _out(out_feature_class)
    _gpkg.create(out, "Polyline", 4326, fields)
    with da.InsertCursor(out, [*[f.name for f in fields], "SHAPE@"]) as written:
        for shape_id, members in points.items():
            line = LineString([(x, y) for _, x, y in sorted(members)])
            if not route_columns:
                written.insertRow((shape_id, line))
                continue
            for route in sorted(by_shape.get(shape_id, [None])):
                row = route_rows.get(route, {}) if route is not None else {}
                written.insertRow((shape_id, *[_gtfs_value(c, row.get(c)) for c in route_columns], line))
    return Result(out)


# ---- shapefiles and dBASE tables (ESRI Shapefile Technical Description, 1998; dBASE III) --------------------------------

from ._runtime import equivalent as _equivalent  # noqa: E402


def _dbf_names(names):
    """Field names cut to dBASE's ten characters; a name a cut makes twice ends ``_1``, ``_2`` … within the ten."""
    out, taken = [], set()
    for name in names:
        cut = name[:10]
        n = 0
        while cut.upper() in taken:
            n += 1
            suffix = f"_{n}"
            cut = name[:10 - len(suffix)] + suffix
        taken.add(cut.upper())
        out.append(cut)
    return out


def _dbf_layout(columns, rows):
    """Each field's dBASE type, width and decimals. Text is as wide as its longest value (1 to 254); whole numbers are
    N(10,0) for 32-bit, N(5,0) for 16-bit, N(19,0) for 64-bit; reals N(19,11); dates D(8)."""
    layout = []
    for i, column in enumerate(columns):
        kind = column.type
        if kind == "String":
            longest = max([len(str(r[i]).encode("utf-8")) for r in rows if r[i] is not None] or [1])
            layout.append(("C", min(254, max(1, longest)), 0))
        elif kind == "SmallInteger":
            layout.append(("N", 5, 0))
        elif kind in ("Integer",):
            layout.append(("N", 10, 0))
        elif kind == "BigInteger":
            layout.append(("N", 19, 0))
        elif kind == "Date":
            layout.append(("D", 8, 0))
        else:
            layout.append(("N", 19, 11))
    return layout


def _write_dbf(path, names, layout, rows):
    import datetime
    import struct
    today = datetime.date.today()
    record_length = 1 + sum(width for _, width, _ in layout)
    header_length = 32 + 32 * len(names) + 1
    with open(path, "wb") as out:
        out.write(struct.pack("<BBBBIHH20x", 3, today.year - 1900, today.month, today.day, len(rows), header_length,
                              record_length))
        for name, (kind, width, decimals) in zip(names, layout):
            out.write(struct.pack("<11sc4xBB14x", name.encode("ascii", "replace"), kind.encode(), width, decimals))
        out.write(b"\r")
        for row in rows:
            out.write(b" ")
            for value, (kind, width, decimals) in zip(row, layout):
                if kind == "C":
                    text = ("" if value is None else str(value)).encode("utf-8")[:width]
                    out.write(text.ljust(width, b" "))
                elif kind == "D":
                    text = "" if value is None else str(value).replace("-", "")[:8]
                    out.write(text.encode("ascii").ljust(width, b" "))
                else:
                    number = 0 if value is None else value
                    text = f"{float(number):.{decimals}f}" if decimals else f"{int(number)}"
                    out.write(text.encode("ascii")[:width].rjust(width, b" "))
        out.write(b"\x1a")


def _shape_record(shape, kind):
    """A shape's record content: its type and coordinates, as the Technical Description lays them out."""
    import struct
    import shapely
    from shapely.geometry.polygon import orient
    if shape is None or shape.is_empty:
        return struct.pack("<i", 0)
    if kind == 1:
        return struct.pack("<idd", 1, shape.x, shape.y)
    x0, y0, x1, y1 = shape.bounds
    if kind == 8:
        points = shapely.get_coordinates(shape).tolist()
        return struct.pack("<i4di", 8, x0, y0, x1, y1, len(points)) + b"".join(struct.pack("<dd", *p) for p in points)
    if kind == 3:
        parts = [list(p.coords) for p in getattr(shape, "geoms", [shape])]
    else:
        parts = []
        for polygon in getattr(shape, "geoms", [shape]):
            # Outer rings clockwise, holes counter-clockwise, as the Technical Description orders them.
            oriented = orient(polygon, -1.0)
            parts.append(list(oriented.exterior.coords))
            parts.extend(list(r.coords) for r in oriented.interiors)
    starts, total = [], 0
    for part in parts:
        starts.append(total)
        total += len(part)
    content = struct.pack("<i4dii", kind, x0, y0, x1, y1, len(parts), total)
    content += b"".join(struct.pack("<i", s) for s in starts)
    content += b"".join(struct.pack("<dd", c[0], c[1]) for part in parts for c in part)
    return content


def _write_shapefile(stem, kind, records, srid):
    """The .shp, .shx and .prj of a shapefile; records are shapes (or None)."""
    import struct
    import shapely
    contents = [_shape_record(s, kind) for s in records]
    present = [s for s in records if s is not None and not s.is_empty]
    bounds = shapely.union_all(present).bounds if present else (0.0, 0.0, 0.0, 0.0)

    def header(length_words):
        return struct.pack(">7i", 9994, 0, 0, 0, 0, 0, length_words) + struct.pack("<2i4d4d", 1000, kind, *bounds, 0, 0, 0, 0)

    shp_length = 50 + sum(4 + len(c) // 2 for c in contents)
    with open(stem + ".shp", "wb") as shp, open(stem + ".shx", "wb") as shx:
        shp.write(header(shp_length))
        shx.write(header(50 + 4 * len(contents)))
        offset = 50
        for number, content in enumerate(contents, start=1):
            shp.write(struct.pack(">2i", number, len(content) // 2) + content)
            shx.write(struct.pack(">2i", offset, len(content) // 2))
            offset += 4 + len(content) // 2
    if srid > 0:
        from pyproj import CRS
        with open(stem + ".prj", "w", encoding="ascii") as prj:
            prj.write(CRS.from_epsg(srid).to_wkt("WKT1_ESRI"))


def _unique_stem(folder, name, extension):
    stem, n = os.path.join(folder, name), 0
    while os.path.exists(stem + extension):
        n += 1
        stem = os.path.join(folder, f"{name}_{n}")
    return stem


def _export_dbase(in_table, folder, with_shapes):
    from .management import _schema, _source, _stem_of
    from ._runtime import env
    path, where = _source(in_table)
    columns, kind, srid, _ = _schema(path)
    names = [c.name for c in columns]
    with da.SearchCursor(path, [*names, "SHAPE@"], where) as rows:
        read = [list(r) for r in rows]
    values = [r[:-1] for r in read]
    stem_name = _stem_of(path).rsplit("/", 1)[-1]
    extension = ".shp" if with_shapes else ".dbf"
    stem = os.path.join(str(folder), stem_name) if env.overwriteOutput else _unique_stem(str(folder), stem_name, extension)
    layout = _dbf_layout(columns, values)
    _write_dbf(stem + ".dbf", _dbf_names(names), layout, values)
    with open(stem + ".cpg", "w", encoding="ascii") as cpg:
        cpg.write("UTF-8")
    if with_shapes:
        code = {"Point": 1, "Multipoint": 8, "Polyline": 3, "Polygon": 5}.get(kind)
        if code is None:
            raise NotBuiltError(f"arcpy.conversion.FeatureClassToShapefile: '{kind}' shapes have no shapefile type.")
        _write_shapefile(stem, code, [r[-1]._shape if r[-1] is not None else None for r in read], srid)
    return stem + extension


@_equivalent("the ESRI Shapefile Technical Description (1998) and dBASE III, field names cut to ten characters",
             "ESRI Shapefile Technical Description, July 1998")
def FeatureClassToShapefile(Input_Features, Output_Folder):
    """EQUIVALENT: each feature class written as a shapefile named after it in the folder — .shp, .shx, .dbf, .prj and
    a .cpg saying UTF-8 — as the Technical Description lays the files out; with overwriteOutput off, a name taken gets
    ``_1``, ``_2`` …, as documented. INFERRED, the page giving no rule: a field name is cut to its first ten
    characters, and a cut that repeats one already made ends ``_1``, ``_2`` … within the ten; nulls, which dBASE cannot
    hold, are written as blanks for text and dates and 0 for numbers; widths as ``_dbf_layout`` gives them."""
    from .management import _list
    for each in _list(Input_Features):
        _export_dbase(each, Output_Folder, True)
    return Result(str(Output_Folder))


@_equivalent("dBASE III tables, field names cut to ten characters", "dBASE III file format")
def TableToDBASE(Input_Table, Output_Folder):
    """EQUIVALENT: each table written as a dBASE table named after it in the folder, as FeatureClassToShapefile writes
    a shapefile's .dbf (the same INFERRED name, width and null rules)."""
    from .management import _list
    for each in _list(Input_Table):
        _export_dbase(each, Output_Folder, False)
    return Result(str(Output_Folder))


def PolylineToRaster(in_features, value_field, out_rasterdataset, cell_assignment="MAXIMUM_LENGTH", priority_field=None,
                     cellsize=None, build_rat="BUILD"):
    """Lines onto a grid over their extent: each cell takes the value of the line with the greatest length inside it
    (``MAXIMUM_LENGTH``), or of the value whose lines' lengths inside it sum greatest (``MAXIMUM_COMBINED_LENGTH``); with
    a priority field, the line of largest positive priority, as documented. With no cell size, the extent's shorter side
    over 250, as documented. INFERRED: a line takes a cell when some length of it lies inside; ties go to the lower
    object id, or the lower value; a text value field is numbered as PolygonToRaster numbers one."""
    import numpy as np
    import shapely
    from ._raster import Raster, _Rat, _value_count
    from .sa import _grid_over
    from .management import _features, _schema, _source
    rule = str(cell_assignment or "MAXIMUM_LENGTH").upper()
    if rule not in ("MAXIMUM_LENGTH", "MAXIMUM_COMBINED_LENGTH"):
        raise NotBuiltError(f"PolylineToRaster: cell_assignment '{cell_assignment}' is not built in grapy.")
    path = _source(in_features)[0]
    columns = {c.name.lower(): c.name for c in _schema(path)[0]}
    lines = []
    for oid, values, shape in _features(in_features):
        if shape is None:
            continue
        row = dict(zip([c.name for c in _schema(path)[0]], values))
        field = str(value_field)
        value = oid if field.upper() in ("OBJECTID", "OID", "FID") else row[columns[field.lower()]]
        priority = row[columns[str(priority_field).lower()]] if priority_field not in (None, "", "#", "NONE") else None
        if value is not None:
            lines.append((oid, value, priority, shape))
    texts = None
    if any(isinstance(v, str) for _, v, _, _ in lines):
        texts = sorted({str(v) for _, v, _, _ in lines})
        lines = [(o, texts.index(str(v)) + 1, p, s) for o, v, p, s in lines]
    if cellsize in (None, "", "#"):
        x0, y0, x1, y1 = shapely.union_all([s for _, _, _, s in lines]).bounds
        cellsize = min(x1 - x0, y1 - y0) / 250 or max(x1 - x0, y1 - y0) / 250
    grid = _grid_over(path, float(cellsize))
    array, mask = np.zeros((grid.rows, grid.columns)), np.ones((grid.rows, grid.columns), bool)
    for r in range(grid.rows):
        for c in range(grid.columns):
            left, top = grid.left + c * grid.width, grid.top - r * grid.height
            cell = shapely.box(left, top - grid.height, left + grid.width, top)
            inside = [(oid, value, priority, shape.intersection(cell).length) for oid, value, priority, shape in lines
                      if shape.intersects(cell)]
            inside = [i for i in inside if i[3] > 0]
            if not inside:
                continue
            with_priority = [i for i in inside if i[2] is not None and i[2] > 0]
            if with_priority:
                chosen = max(with_priority, key=lambda i: (i[2], -i[0]))[1]
            elif rule == "MAXIMUM_LENGTH":
                chosen = max(inside, key=lambda i: (i[3], -i[0]))[1]
            else:
                sums: dict = {}
                for _, value, _, length in inside:
                    sums[value] = sums.get(value, 0.0) + length
                chosen = max(sums.items(), key=lambda kv: (kv[1], -kv[0]))[0]
            array[r, c], mask[r, c] = chosen, False
    integer = all(isinstance(v, int) for _, v, _, _ in lines)
    raster = Raster._of(array.astype(np.int32 if integer else np.float64), mask, grid)
    if integer and str(build_rat or "BUILD").upper() == "BUILD":
        raster._rat = _value_count(raster._array, raster._mask)
        if texts is not None:
            raster._rat = _Rat([*raster._rat.fields, (str(value_field), "String", 0)],
                               [[*row, texts[row[0] - 1]] for row in raster._rat.rows])
    return Result(raster.save(out_rasterdataset))


# ---- refused by name ----------------------------------------------------------------------------------------------------
#
# Every arcpy.conversion name grapy does not build is refused with its own reason (ADR-224).

_REASONS = {
    "builds public-transit sources into a network dataset; grapy's network datasets (ADR-226) have no transit "
    "evaluator, and the public transit evaluator's schedule rules are not published": (
        "ConnectNetworkDatasetTransitSourcesToStreets", "GTFSToNetworkDatasetTransitSources"),
    "needs a point cloud scene layer (I3S or a scene layer package) as input, which grapy does not hold; LAS datasets "
    "are held since ADR-229 and LasDatasetToRaster reads them (ADR-224 group 2)": ("PointCloudToRaster",),
    "reads an integrated mesh scene layer (I3S or a scene layer package), which grapy does not hold (ADR-224 group 2)": (
        "MeshToLAS",),
    "reads a BIM file workspace (Revit or IFC), which grapy does not hold (ADR-224 group 2)": (
        "BIMFileToGeodatabase", "ExtractBIMFileFloorplan"),

    "reads or writes CAD drawings into Esri's CAD feature schema, which is not publicly documented": (
        "AddCADFields", "CADToGeodatabase", "ExportCAD"),
    "reads or writes Esri's personal, mobile or file geodatabase, whose formats are not publicly documented; grapy's "
    "workspace is a GeoPackage": ("ConvertPersonalGeodatabase", "MobileGdbToFileGdb"),
    "needs a Parquet writer (pyarrow), which grapy's build of these tools does not carry": ("ExportToParquet",),
    "uses Esri's undocumented text and document parsing to find locations": (
        "ExtractLocationsDocument", "ExtractLocationsText"),
    "vectorises scanned maps by Esri's undocumented recognition": ("ExtractScannedLines", "ExtractScannedPolygons"),
    "thins and vectorises raster lines by an algorithm Esri does not document, and its output's vertices, dangles and "
    "weeding follow no published rule grapy could name": ("RasterToPolyline",),
    "needs an ArcGIS Pro map, graphics layer or symbology, which a tool running on the server does not hold": (
        "FeaturesToGraphics", "GraphicsToFeatures", "MapToKML"),
    "writes placemarks, pop-ups and styles whose content the page does not document": ("LayerToKML",),
    "needs a PDF renderer, which grapy does not carry": ("PDFToTIFF",),
    "writes a raster into a geodatabase or a format other than GeoTIFF; grapy's rasters are GeoTIFF files "
    "(ADR-224 §5.5)": ("RasterToGeodatabase", "RasterToOtherFormat"),
    "needs SAS, a commercial runtime grapy does not carry": ("SASToTable", "TableToSAS"),
    "reaches a WFS service, and a fenced tool has no network (ADR-223 §5.3)": ("WFSToFeatureClass",),
    "writes GTFS columns the page does not name, in an order it does not give": ("FeaturesToGTFSStops", "FeaturesToGTFSShapes"),
    "routes its default modes 3, 5 and 11 along a network dataset by a travel mode the page does not give, and does not "
    "document how it names the shapes it draws as straight lines": ("GenerateShapesFeaturesFromGTFS",),
    "writes a GeoPackage tile pyramid whose tile matrix set, tile size, image format and zoom levels the page does not "
    "define": ("AddRasterToGeoPackage",),
    "lays the profiles of a USGS DEM, which start at different rows, on an output grid whose extent and filling the page "
    "does not define": ("DEMToRaster",),
    "uses a KML parser of its own whose output fields and feature class names the page does not define; KMLToLayer "
    "reads KML": ("KMLToGeodatabase",),
}

_REFUSED = {name: reason for reason, names in _REASONS.items() for name in names}


def __getattr__(name):
    if name.startswith("_"):
        raise AttributeError(name)
    reason = _REFUSED.get(name)
    message = f"arcpy.conversion.{name} {reason}." if reason else f"arcpy.conversion.{name} is not built in grapy yet."

    def missing(*_, **__):
        raise NotBuiltError(message)

    return missing


# ---- LAS datasets (ADR-229): built in _las, answered here under arcpy.conversion's name


from . import _las as _las_tools  # noqa: E402

LasDatasetToRaster = _las_tools.LasDatasetToRaster
_REFUSED.pop("LasDatasetToRaster", None)
from . import _lastools as _las_second  # noqa: E402 — ADR-229's second batch

ConvertLas = _las_second.ConvertLas
_REFUSED.pop("ConvertLas", None)

# ---- multipatch features (ADR-238), built in _mpmore (D-329)
from . import _mpmore  # noqa: E402

MultipatchToCollada = _mpmore.MultipatchToCollada
MultipatchToRaster = _mpmore.MultipatchToRaster
