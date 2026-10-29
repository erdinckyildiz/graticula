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


def __getattr__(name):
    if name.startswith("_"):
        raise AttributeError(name)

    def missing(*_, **__):
        raise NotBuiltError(f"arcpy.conversion.{name} is not built in grapy yet.")

    return missing
