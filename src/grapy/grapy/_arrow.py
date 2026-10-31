"""Apache Arrow tables of a grapy table, and Parquet files written from them — arcpy.da.TableToArrowTable and
arcpy.conversion.ExportToParquet.

TableToArrowTable is built to its reference page (doc.esri.com …/arcpy/data-access/tabletoarrowtable) and to *Apache
Arrow in ArcGIS* (…/arcpy/get-started/working-with-arrow-in-arcgis), which gives the field types and the geometry
column's metadata: ``esri.encoding`` (EsriShape, WKB, EsriJSON, GeoJSON, WKT), ``esri.sr_wkt``, ``esri.oid`` and
``esri.interop.type``. EsriShape is the shape record of the ESRI Shapefile Technical Description (July 1998), without
the record header, which the page calls Esri's native binary encoding.

ExportToParquet's page gives its two parameters and not how geometry or fields are written, so it is an equivalent:
GeoParquet 1.1 (OGC GeoParquet, WKB in a ``geometry`` column described by the ``geo`` key of the file's metadata).
"""
from __future__ import annotations

import json
import struct

import shapely

from . import _gpkg
from ._runtime import ExecuteError, NotBuiltError

ENCODINGS = {"ESRISHAPE": "EsriShape", "ESRIJSON": "EsriJSON", "GEOJSON": "GeoJSON", "WKT": "WKT", "WKB": "WKB"}
_TEXT_LIMIT = 5000  # "Text fields are trimmed at 5,000 characters when converted to an Arrow table."
_NO_DATA = -1e39    # the Technical Description: a measure below -10^38 is "no data"


def _shape_base(geom_type):
    return {"Point": 1, "MultiPoint": 8, "LineString": 3, "MultiLineString": 3, "Polygon": 5,
            "MultiPolygon": 5}.get(geom_type)


def esri_shape(shape, has_z, has_m):
    """A shape's record content as the Technical Description lays it out — Point 1, MultiPoint 8, PolyLine 3, Polygon
    5; with measures 21, 28, 23, 25; with z 11, 18, 13, 15, whose measures follow the z values when the class has them.
    A null or empty shape is the Null Shape (type 0). Polygon rings: outer rings clockwise, holes counter-clockwise.
    INFERRED (Q-223 (f)): a PointZ of a class without measures writes the Description's "no data" measure, -1e39."""
    from shapely.geometry.polygon import orient
    if shape is None or shape.is_empty:
        return struct.pack("<i", 0)
    base = _shape_base(shape.geom_type)
    if base is None:
        raise NotBuiltError(f"arcpy.da.TableToArrowTable: a {shape.geom_type} has no shape type in the ESRI Shapefile "
                            "Technical Description.")
    kind = base + (10 if has_z else 20 if has_m else 0)
    coords = shapely.get_coordinates(shape, include_z=has_z, include_m=has_m)  # x, y[, z][, m]

    def column(i):
        return [float(c[i]) for c in points]

    if base == 1:
        c = coords[0]
        out = struct.pack("<idd", kind, c[0], c[1])
        if has_z:
            return out + struct.pack("<dd", c[2], c[3] if has_m else _NO_DATA)
        return out + (struct.pack("<d", c[2]) if has_m else b"")
    x0, y0, x1, y1 = shape.bounds
    if base == 8:
        points = list(coords)
        out = struct.pack("<i4di", kind, x0, y0, x1, y1, len(points)) + b"".join(struct.pack("<dd", p[0], p[1])
                                                                                for p in points)
        starts = None
    else:
        if base == 3:
            parts = [part for part in getattr(shape, "geoms", [shape])]
            rings = [shapely.get_coordinates(p, include_z=has_z, include_m=has_m) for p in parts]
        else:
            rings = []
            for polygon in getattr(shape, "geoms", [shape]):
                oriented = orient(polygon, -1.0)
                rings.append(shapely.get_coordinates(oriented.exterior, include_z=has_z, include_m=has_m))
                rings.extend(shapely.get_coordinates(r, include_z=has_z, include_m=has_m) for r in oriented.interiors)
        points = [c for ring in rings for c in ring]
        starts, total = [], 0
        for ring in rings:
            starts.append(total)
            total += len(ring)
        out = struct.pack("<i4dii", kind, x0, y0, x1, y1, len(rings), len(points))
        out += b"".join(struct.pack("<i", s) for s in starts)
        out += b"".join(struct.pack("<dd", p[0], p[1]) for p in points)
    if has_z:
        zs = column(2)
        out += struct.pack("<dd", min(zs), max(zs)) + b"".join(struct.pack("<d", z) for z in zs)
    if has_m:
        ms = column(3 if has_z else 2)
        out += struct.pack("<dd", min(ms), max(ms)) + b"".join(struct.pack("<d", m) for m in ms)
    return out


def _sr_wkt(srid):
    """The reference as Esri's WKT 1, which ``esri.sr_wkt`` asks for as "a WKT coordinate system string"; empty when it
    is unknown, as the page says. INFERRED (Q-223 (f)): the ESRI WKT 1 form PROJ writes."""
    if not srid:
        return ""
    import pyproj
    try:
        return pyproj.CRS.from_user_input(srid).to_wkt("WKT1_ESRI") or ""
    except pyproj.exceptions.CRSError:
        return ""


def _arrow_type(pa, kind):
    """(pyarrow type, field metadata, nullable) for an ArcGIS field type, from the page's table; None for a type the
    page does not list, which "will be dropped"."""
    interop = lambda name: {b"esri.interop.type": name.encode()}  # noqa: E731
    return {
        "SmallInteger": (pa.int16(), None, True), "Integer": (pa.int32(), None, True),
        "BigInteger": (pa.int64(), None, True), "Single": (pa.float32(), None, True),
        "Double": (pa.float64(), None, True), "String": (pa.string(), None, True),
        "Date": (pa.timestamp("ms"), None, True), "DateOnly": (pa.date64(), None, True),
        "TimeOnly": (pa.time32("ms"), None, True),
        "TimestampOffset": (pa.string(), interop("esri.timestamp_offset"), True),
        "Guid": (pa.string(), interop("esri.guid"), True),
        "GlobalID": (pa.string(), interop("esri.global_id"), False),
        "Blob": (pa.binary(), interop("esri.blob"), True),
        "OID": (pa.int64(), {b"esri.oid": b"esri.int64"}, True),
    }.get(kind)


def _value(kind, value):
    import datetime
    if value is None:
        return None
    if kind == "String" and isinstance(value, str):
        return value[:_TEXT_LIMIT]
    if kind == "Date" and isinstance(value, str):
        return datetime.datetime.fromisoformat(value)
    if kind == "DateOnly" and isinstance(value, str):
        return datetime.date.fromisoformat(value[:10])
    if kind == "TimeOnly" and isinstance(value, str):
        return datetime.time.fromisoformat(value)
    if kind == "TimeOnly" and isinstance(value, datetime.datetime):
        return value.time()
    if kind in ("TimestampOffset", "Guid", "GlobalID") and not isinstance(value, str):
        return str(value)
    return value


def table_to_arrow(in_table, field_names=None, where_clause=None, geometry_encoding="ESRISHAPE",
                   tool="arcpy.da.TableToArrowTable"):
    import pyarrow as pa
    from . import ListFields, da
    word = str(geometry_encoding or "ESRISHAPE").upper()
    if word not in ENCODINGS:
        raise ExecuteError(f"{tool}: geometry_encoding '{geometry_encoding}' is not one of {', '.join(ENCODINGS)}.")
    fields = [f for f in ListFields(in_table)]
    if field_names in (None, "", "*", ["*"], ("*",)):
        chosen = fields
    else:
        wanted = [field_names] if isinstance(field_names, str) else list(field_names)
        by_name = {f.name.lower(): f for f in fields}
        missing = [n for n in wanted if n.lower() not in by_name]
        if missing:
            raise ExecuteError(f"{tool}: no field {', '.join(missing)} in {in_table}.")
        chosen = [by_name[n.lower()] for n in wanted]
    from .management import _source
    connection, table = _gpkg.table_of(_source(in_table)[0])
    try:
        _, _, srid, _ = _gpkg.describe(connection, table)
        has_z, has_m = _gpkg.dimensions(connection, table)
    finally:
        connection.close()
    columns, reading = [], []
    for f in chosen:
        if f.type == "Geometry":
            binary = ENCODINGS[word] in ("EsriShape", "WKB")
            meta = {b"esri.encoding": ENCODINGS[word].encode(), b"esri.sr_wkt": _sr_wkt(srid).encode()}
            columns.append(pa.field(f.name, pa.binary() if binary else pa.string(), True, meta))
            reading.append(("SHAPE@", "Geometry"))
            continue
        typed = _arrow_type(pa, f.type)
        if typed is None:
            continue  # "Other field types not listed above are not converted and will be dropped."
        arrow, meta, nullable = typed
        columns.append(pa.field(f.name, arrow, nullable, meta))
        reading.append(("OID@" if f.type == "OID" else f.name, f.type))
    values = [[] for _ in reading]
    if reading:
        with da.SearchCursor(in_table, [r[0] for r in reading], where_clause) as rows:
            for row in rows:
                for i, ((_, kind), value) in enumerate(zip(reading, row)):
                    values[i].append(_geometry(word, value, has_z, has_m, srid) if kind == "Geometry"
                                     else _value(kind, value))
    arrays = [pa.array(v, type=c.type) for v, c in zip(values, columns)]
    return pa.Table.from_arrays(arrays, schema=pa.schema(columns))


def _geometry(word, geometry, has_z, has_m, srid):
    shape = getattr(geometry, "_shape", geometry)
    if shape is None:
        return None
    if word == "ESRISHAPE":
        return esri_shape(shape, has_z, has_m)
    if word == "WKB":
        return shapely.to_wkb(shape)
    if word == "WKT":
        return shapely.to_wkt(shape)
    if word == "GEOJSON":
        return shapely.to_geojson(shape)
    return geometry.JSON


def is_arrow_table(value):
    """Whether a tool's input is a pyarrow Table — arcpy's tools take one as a table or feature class input."""
    try:
        import pyarrow as pa
    except ImportError:  # pragma: no cover — the image and CI install pyarrow
        return False
    return isinstance(value, pa.Table)


def export_to_parquet(inputs, out_file):
    """GeoParquet 1.1 of one table or feature class: its fields as TableToArrowTable types them, the shape as WKB in
    a column named after the class's shape field, described in the file's ``geo`` metadata. INFERRED (Q-223 (f)): the
    column keeps the shape field's name; ``crs`` is the reference's PROJJSON; ``geometry_types`` the class's shape
    type."""
    import os
    import pyarrow.parquet as pq
    from .management import _source
    out_file = os.fspath(out_file)
    if not out_file.lower().endswith(".parquet"):
        raise ExecuteError("arcpy.conversion.ExportToParquet: the Parquet file name must have a .parquet extension.")
    if os.path.exists(out_file):
        from . import env
        if not env.overwriteOutput:
            raise ExecuteError(f"arcpy.conversion.ExportToParquet: {out_file} already exists.")
    if isinstance(inputs, (list, tuple)):
        if len(inputs) != 1:
            raise ExecuteError("arcpy.conversion.ExportToParquet: one input is written to one Parquet file.")
        inputs = inputs[0]
    table = table_to_arrow(inputs, None, None, "WKB", "arcpy.conversion.ExportToParquet")
    metadata = dict(table.schema.metadata or {})
    geometry = [f for f in table.schema if (f.metadata or {}).get(b"esri.encoding") == b"WKB"]
    if geometry:
        import pyproj
        connection, name = _gpkg.table_of(_source(inputs)[0])
        try:
            _, kind, srid, _ = _gpkg.describe(connection, name)
            has_z, _ = _gpkg.dimensions(connection, name)
        finally:
            connection.close()
        column = geometry[0].name
        types = {"Point": ["Point"], "Multipoint": ["MultiPoint"], "Polyline": ["LineString", "MultiLineString"],
                 "Polygon": ["Polygon", "MultiPolygon"]}.get(kind, [])
        shapes = [shapely.from_wkb(b) for b in table.column(column).to_pylist() if b is not None]
        bbox = list(shapely.total_bounds(shapes)) if shapes else None
        described = {"encoding": "WKB", "geometry_types": [t + (" Z" if has_z else "") for t in types]}
        if srid:
            described["crs"] = json.loads(pyproj.CRS.from_user_input(srid).to_json())
        if bbox and all(b == b for b in bbox):
            described["bbox"] = bbox
        metadata[b"geo"] = json.dumps({"version": "1.1.0", "primary_column": column,
                                       "columns": {column: described}}).encode()
    pq.write_table(table.replace_schema_metadata(metadata), out_file)
