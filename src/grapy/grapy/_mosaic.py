"""Mosaic datasets held in a GeoPackage, read as one raster mosaicked by their method — ADR-230.

A mosaic dataset in ArcGIS is a catalog of rasters kept in a geodatabase, whose internal tables are Esri's and are not
published. What is published is what the catalog *holds* — its attribute table's fields (Name, MinPS, MaxPS, LowPS,
HighPS, Category, Tag, GroupName, ProductName, CenterX, CenterY, ZOrder), its footprints and boundary — and how it is
displayed: the mosaic methods and operators ("Mosaicking rules for a mosaic dataset"), the properties
SetMosaicDatasetProperties sets and Describe reads. grapy's mosaic dataset is its own tables (INFERRED, ADR-230 §5.2):

- ``<name>``: the catalog, a GeoPackage polygon feature table named after the mosaic dataset, so the cursors,
  AddField and CalculateField work on it as they do on a mosaic dataset's attribute table in ArcGIS Pro. Its object
  id column is ``OBJECTID``; each row is an item, its shape the footprint, ``Raster`` the item's file (an absolute
  path), and the documented fields beside it.
- ``grapy_mosaic``: one row per mosaic dataset: its reference, band count, pixel type, product definition, whether it
  is referenced, its boundary (a GeoPackage geometry blob) and its properties as JSON, under Describe's names.
- ``grapy_mosaic_item``: per item, the file's modification time when it was last read (for SynchronizeMosaicDataset's
  stale items) and the NoData definitions of DefineMosaicDatasetNoData, as JSON.

The three are registered in ``gpkg_extensions`` as the ``grapy_mosaic`` extension. Nothing of Esri's mosaic dataset
format — its AMD tables, raster type tables, overviews, item caches (CRF) — is reproduced.

**Reading.** ``read`` answers the cells of a mosaic dataset, or of a MakeMosaicLayer layer, as one raster: the items in
view at the read's cell size (MinPS ≤ cell size ≤ MaxPS), ordered by the mosaic method, combined cell by cell by the
mosaic operator, clipped to the boundary. Items must lie on the mosaic's grid: the same cell size, or a whole multiple
of it taken by NEAREST (each finer cell lies inside one coarser cell, so the answer is exact), with corners whole
cells apart. Any other alignment, a reference other than the mosaic's, and any other resampling are refused by name.

**One reading with the server's.** Where ArcGIS's pages leave a mosaic rule open, grapy reads it as this server's image
services already draw it (ADR-230 §5.4): North-West and Closest to Center measure from the extent read to the centre of
each raster's extent (ADR-152); Seamline draws each item where its centre is nearest, the halfway lines (ADR-161); BLEND
weighs a cell by its distance inside its item, plus one, up to 64 (ADR-158). One difference remains: grapy orders by
ZOrder and pixel size before the method, as the mosaicking rules page says, and the server's mosaic rule does not yet
(ADR-230 condition 5).
"""
from __future__ import annotations

import datetime
import json
import math
import os

import numpy as np
import shapely

from . import _gpkg
from ._runtime import AddWarning, ExecuteError, NotBuiltError, env
from ._runtime import _is_given  # noqa: E402 — a Raster is never compared with ==

EXTENSION = "grapy_mosaic"
DEFINITION = "Graticula ADR-230: grapy's mosaic datasets"
OID = "OBJECTID"

#: The catalog's fields after the object id and shape, as the mosaic dataset attribute table page lists them.
FIELDS = (("Raster", "String"), ("Name", "String"), ("MinPS", "Double"), ("MaxPS", "Double"), ("LowPS", "Double"),
          ("HighPS", "Double"), ("Category", "Integer"), ("Tag", "String"), ("GroupName", "String"),
          ("ProductName", "String"), ("CenterX", "Double"), ("CenterY", "Double"), ("ZOrder", "Integer"))

#: Pixel types by CreateMosaicDataset's keywords (1, 2 and 4 bit, which numpy has no cell type for, are refused).
PIXEL_TYPES = {"8_BIT_UNSIGNED": "uint8", "8_BIT_SIGNED": "int8", "16_BIT_UNSIGNED": "uint16",
               "16_BIT_SIGNED": "int16", "32_BIT_UNSIGNED": "uint32", "32_BIT_SIGNED": "int32",
               "32_BIT_FLOAT": "float32", "64_BIT": "float64"}

#: SetMosaicDatasetProperties' mosaic method words; MakeMosaicLayer's keywords map onto them.
METHODS = ("None", "Center", "NorthWest", "LockRaster", "ByAttribute", "Nadir", "Viewpoint", "Seamline")
LAYER_METHODS = {"CLOSEST_TO_CENTER": "Center", "NORTH_WEST": "NorthWest", "LOCK_RASTER": "LockRaster",
                 "BY_ATTRIBUTE": "ByAttribute", "CLOSEST_TO_NADIR": "Nadir", "CLOSEST_TO_VIEWPOINT": "Viewpoint",
                 "SEAMLINE": "Seamline", "NONE": "None"}
OPERATORS = ("FIRST", "LAST", "MIN", "MAX", "MEAN", "BLEND", "SUM")

#: Properties a new mosaic dataset starts with, under Describe's names. Documented on SetMosaicDatasetProperties: not
#: clipped to footprints, clipped to the boundary, footprints may contain NoData, ascending, no colour correction, time
#: disabled. INFERRED where the pages give no default (ADR-230 §5.9 (c)): NorthWest is the default method, as the
#: mosaicking rules page calls it; FIRST, MakeMosaicLayer's documented default operator; NEAREST resampling; no limit
#: on the rasters per mosaic; a contribution of one cell; every method allowed. The image service transmission
#: properties (sizes, compression, mensuration, downloads, metadata) have no documented default and stay unset (None)
#: until SetMosaicDatasetProperties sets them.
DEFAULTS = {
    "allowedMosaicMethods": "NorthWest;Center;LockRaster;ByAttribute;Nadir;Viewpoint;Seamline;None",
    "defaultMosaicMethod": "NorthWest", "mosaicOperator": "FIRST", "orderField": "", "orderBaseValue": 0.0,
    "sortAscending": True, "clipToFootprint": False, "clipToBoundary": True, "footprintMayContainNoData": True,
    "defaultResamplingMethod": "NEAREST", "maxRastersPerMosaic": 0, "minimumPixelContribution": 1,
    "cellSizeToleranceFactor": 0.0, "blendWidth": None, "blendWidthUnits": None, "viewpointSpacingX": None,
    "viewpointSpacingY": None, "cellSize": None, "allowedCompressionMethods": None, "defaultCompressionMethod": None,
    "JPEGQuality": None, "LERCTolerance": None, "maxRequestSizeX": None, "maxRequestSizeY": None,
    "allowedMensurationCapabilities": None, "defaultMensurationCapability": None, "maxDownloadImageCount": None,
    "maxRecordsReturned": None, "rasterMetadataLevel": None, "allowedFields": None, "useTime": False,
    "startTimeField": "", "endTimeField": "", "timeValueFormat": "", "applyColorCorrection": False,
    "dataSourceType": None, "processingTemplates": None, "defaultProcessingTemplate": None,
}

LAYERS: dict = {}   # MakeMosaicLayer: layer name -> View


def _refuse(tool, parameter, value, why=""):
    raise NotBuiltError(f"arcpy.management.{tool}: {parameter} '{value}' is not built in grapy{': ' + why if why else ''}.")


def given(value):
    return _is_given(value)


def text(value):
    if isinstance(value, os.PathLike):
        return os.fspath(value)
    return str(value)


# ---- where a mosaic dataset is


class View:
    """How a mosaic dataset is read: its own properties, or a MakeMosaicLayer layer's overrides of them."""

    def __init__(self, path, where=None, method=None, order_field=None, order_base=None, lock=None, ascending=None,
                 operator=None, cell_size=None, bands=None, extent=None):
        self.path, self.where, self.method, self.order_field = path, where, method, order_field
        self.order_base, self.lock, self.ascending, self.operator = order_base, lock, ascending, operator
        self.cell_size, self.bands, self.extent = cell_size, bands, extent


def locate(value):
    """(file, name, layer View or None) of a mosaic dataset path or MakeMosaicLayer layer name."""
    from ._result import Result
    if isinstance(value, Result):
        value = str(value)
    view = LAYERS.get(text(value)) if isinstance(value, (str, os.PathLike)) else None
    path = view.path if view is not None else text(value)
    if ".gpkg" not in path.lower():
        workspace = text(env.workspace or "")
        if workspace.lower().endswith(".gpkg") and not os.path.isabs(path):
            path = workspace + "/" + path
    file, name = _gpkg.split(path)
    if not name:
        raise ValueError(f"'{path}' names no mosaic dataset: write <file>.gpkg/<mosaic dataset>.")
    return file, name, view


def catalog_path(value):
    """The path of a mosaic dataset as it was given, or ``<file>.gpkg/<name>`` (a layer's dataset, or a name in a
    GeoPackage workspace)."""
    file, name, view = locate(value)
    if view is not None:
        return view.path
    given_path = text(value)
    return given_path if ".gpkg" in given_path.lower() else f"{file}/{name}"


def exists(value):
    """Whether a value names a mosaic dataset or a mosaic layer grapy holds."""
    try:
        file, name, _ = locate(value)
    except (ValueError, TypeError):
        return False
    if not os.path.isfile(file):
        return False
    connection = _gpkg.connect(file)
    try:
        return _registered(connection, name)
    finally:
        connection.close()


def _registered(connection, name):
    if not connection.execute("select 1 from sqlite_master where name = 'grapy_mosaic'").fetchone():
        return False
    return connection.execute("select 1 from grapy_mosaic where name = ?", (name,)).fetchone() is not None


def names(connection):
    """The mosaic datasets a GeoPackage holds."""
    if not connection.execute("select 1 from sqlite_master where name = 'grapy_mosaic'").fetchone():
        return []
    return [r[0] for r in connection.execute("select name from grapy_mosaic order by name")]


def open_mosaic(value, tool):
    """(connection, name, record, view) of a mosaic dataset, refused by name when it is not one."""
    try:
        file, name, view = locate(value)
    except (ValueError, TypeError):
        raise ExecuteError(f"ERROR 000732: Input Mosaic Dataset: Dataset {value} does not exist or is not supported.")
    if not os.path.isfile(file):
        raise ExecuteError(f"ERROR 000732: Input Mosaic Dataset: Dataset {value} does not exist or is not supported.")
    connection = _gpkg.connect(file)
    if not _registered(connection, name):
        connection.close()
        raise ExecuteError(f"ERROR 000732: Input Mosaic Dataset: Dataset {value} does not exist or is not supported.")
    return connection, name, record(connection, name), view


def ensure(connection):
    from ._topology import register
    connection.execute("""create table if not exists grapy_mosaic (name text primary key, srid integer not null,
        bands integer, pixel_type text, product_definition text not null default 'NONE',
        referenced integer not null default 0, boundary blob, properties text not null, created_at text)""")
    connection.execute("""create table if not exists grapy_mosaic_item (mosaic text not null, item integer not null,
        mtime double, nodata text, primary key (mosaic, item))""")
    register(connection, ("grapy_mosaic", "grapy_mosaic_item"), EXTENSION, DEFINITION)


def create(file, name, srid, bands=None, pixel_type=None, product="NONE", referenced=False, properties=None,
           extra_columns=()):
    """A new, empty mosaic dataset: its catalog table and its row in grapy_mosaic; one of the name there is replaced."""
    from ._topology import register
    os.makedirs(os.path.dirname(os.path.abspath(file)), exist_ok=True)
    connection = _gpkg.connect(file)
    try:
        _gpkg._ensure_metadata(connection, srid)
        ensure(connection)
        drop(connection, name)
        connection.execute(f'drop table if exists "{name}"')
        connection.execute("delete from gpkg_contents where table_name = ?", (name,))
        connection.execute("delete from gpkg_geometry_columns where table_name = ?", (name,))
        columns = [*FIELDS, *[c for c in extra_columns if c[0].lower() not in {f[0].lower() for f in FIELDS}]]
        defined = "".join(f', "{n}" {_gpkg._TYPES.get(t.upper(), "TEXT")}' for n, t in columns)
        connection.execute(f'create table "{name}" ("{OID}" integer primary key autoincrement, '
                           f'"{_gpkg.GEOMETRY}" BLOB{defined})')
        connection.execute("insert into gpkg_contents (table_name, data_type, identifier, srs_id) "
                           "values (?, 'features', ?, ?)", (name, name, srid))
        connection.execute("insert into gpkg_geometry_columns (table_name, column_name, geometry_type_name, srs_id, z, m) "
                           "values (?, ?, 'MULTIPOLYGON', ?, 0, 0)", (name, _gpkg.GEOMETRY, srid))
        register(connection, (name,), EXTENSION, DEFINITION)
        props = dict(DEFAULTS)
        props.update(properties or {})
        connection.execute("insert into grapy_mosaic (name, srid, bands, pixel_type, product_definition, referenced, "
                           "properties, created_at) values (?, ?, ?, ?, ?, ?, ?, ?)",
                           (name, int(srid or 0), bands, pixel_type, product or "NONE", 1 if referenced else 0,
                            json.dumps(props), datetime.datetime.now().isoformat(timespec="seconds")))
        connection.commit()
    finally:
        connection.close()


def drop(connection, name):
    """A mosaic dataset's rows in grapy's own tables removed (its catalog table is the caller's to drop)."""
    if not connection.execute("select 1 from sqlite_master where name = 'grapy_mosaic'").fetchone():
        return False
    held = connection.execute("select 1 from grapy_mosaic where name = ?", (name,)).fetchone() is not None
    connection.execute("delete from grapy_mosaic where name = ?", (name,))
    connection.execute("delete from grapy_mosaic_item where mosaic = ?", (name,))
    connection.execute("delete from gpkg_extensions where table_name = ? and extension_name = ?", (name, EXTENSION))
    return held


class Record:
    """A mosaic dataset's row of grapy_mosaic."""

    def __init__(self, name, srid, bands, pixel_type, product, referenced, boundary, properties):
        self.name, self.srid, self.bands, self.pixel_type = name, int(srid or 0), bands, pixel_type
        self.product, self.referenced = product, bool(referenced)
        self.boundary = _gpkg.decode(boundary) if boundary else None
        self.properties = dict(DEFAULTS)
        self.properties.update(json.loads(properties or "{}"))


def record(connection, name):
    row = connection.execute("select name, srid, bands, pixel_type, product_definition, referenced, boundary, "
                             "properties from grapy_mosaic where name = ?", (name,)).fetchone()
    return Record(*row)


def save_properties(connection, name, properties):
    connection.execute("update grapy_mosaic set properties = ? where name = ?", (json.dumps(properties), name))


def set_boundary(connection, name, srid, shape):
    blob = None if shape is None or shape.is_empty else _gpkg.encode(_multi(shape), srid)
    connection.execute("update grapy_mosaic set boundary = ? where name = ?", (blob, name))


def _multi(shape):
    if shape.geom_type == "Polygon":
        return shapely.MultiPolygon([shape])
    if shape.geom_type == "GeometryCollection":
        return shapely.MultiPolygon([g for g in shape.geoms if g.geom_type == "Polygon"]
                                    + [p for g in shape.geoms if g.geom_type == "MultiPolygon" for p in g.geoms])
    return shape


# ---- the catalog's items


def where_sql(clause):
    """A tool's where clause as SQLite reads it on the catalog. INFERRED: arcpy's SQL on a mosaic dataset is SQLite's
    on grapy's catalog, its object id column OBJECTID."""
    return f" where ({clause})" if given(clause) else ""


def columns_of(connection, name):
    """The catalog's columns: [(name, arcpy type)] after the object id and shape."""
    return [(c.name, c.type) for c in _gpkg.describe(connection, name)[0]]


def items(connection, name, where=None, extra=()):
    """The catalog's rows as dicts: OBJECTID, the shape, the documented fields and ``extra`` columns."""
    wanted = [f for f, _ in FIELDS] + [e for e in extra if e and e.lower() not in {f.lower() for f, _ in FIELDS}]
    present = {c.lower(): c for c, _ in columns_of(connection, name)}
    selected = [present[w.lower()] for w in wanted if w.lower() in present]
    sql = (f'select "{OID}", "{_gpkg.GEOMETRY}"' + "".join(f', "{c}"' for c in selected) + f' from "{name}"'
           + where_sql(where) + f' order by "{OID}"')
    try:
        rows = connection.execute(sql).fetchall()
    except Exception as error:  # noqa: BLE001 — a where clause SQLite cannot read is the caller's error
        raise ExecuteError(f"ERROR 000358: Invalid expression {where}: {error}")
    out = []
    for row in rows:
        item = {"OBJECTID": row[0], "shape": _gpkg.decode(row[1])}
        for column, value in zip(selected, row[2:]):
            item[column] = value
        for w in wanted:   # the asked name answers whatever case the column has
            if w not in item and w.lower() in present:
                item[w] = item.get(present[w.lower()])
        out.append(item)
    return out


def item_nodata(connection, name, oid):
    row = connection.execute("select nodata from grapy_mosaic_item where mosaic = ? and item = ?", (name, oid)).fetchone()
    return json.loads(row[0]) if row and row[0] else None


class Source:
    """An item's raster file as read: grid, band count, cell type, NoData and reference."""

    def __init__(self, path):
        import rasterio
        with rasterio.open(path) as source:
            t = source.transform
            if t.b != 0 or t.d != 0 or t.e >= 0:
                raise NotBuiltError(f"'{path}' is rotated or south-up; only north-up items are built in grapy's mosaic "
                                    "datasets.")
            self.left, self.top, self.width, self.height = t.c, t.f, t.a, -t.e
            self.rows, self.columns, self.count = source.height, source.width, source.count
            self.dtype = np.dtype(source.dtypes[0])
            self.nodata = source.nodata
            self.srid = (source.crs.to_epsg() or 0) if source.crs else 0
            factors = source.overviews(1) if source.count else []
        self.path = path
        self.high = max([1, *factors])   # the coarsest overview's factor, 1 with none

    @property
    def right(self):
        return self.left + self.columns * self.width

    @property
    def bottom(self):
        return self.top - self.rows * self.height

    @property
    def cell(self):
        """LowPS: the item's cell size (INFERRED: the mean of a rectangular cell's width and height)."""
        return (self.width + self.height) / 2

    def box(self):
        return shapely.box(self.left, self.bottom, self.right, self.top)

    def read(self):
        """(arrays, NoData masks) of every band."""
        import rasterio
        with rasterio.open(self.path) as source:
            arrays = [source.read(i) for i in range(1, self.count + 1)]
        masks = []
        for band in arrays:
            mask = np.zeros(band.shape, bool) if self.nodata is None else (
                np.isnan(band) if np.issubdtype(band.dtype, np.floating) and np.isnan(self.nodata) else band == self.nodata)
            if np.issubdtype(band.dtype, np.floating):
                mask |= np.isnan(band)
            masks.append(mask)
        return arrays, masks


def source_of(path):
    """The item's Source, or None when its file is missing (a broken path)."""
    if not path or not os.path.isfile(path):
        return None
    return Source(path)


def apply_nodata(arrays, masks, definition):
    """DefineMosaicDatasetNoData's definition applied to an item's bands: values made NoData per band, values outside a
    valid range made NoData, and with ``composite`` a cell NoData only where every band is (else where any is)."""
    if not definition:
        return masks
    masks = [m.copy() for m in masks]
    for band, values in (definition.get("values") or {}).items():
        targets = range(len(arrays)) if band == "ALL" else [int(band) - 1]
        for b in targets:
            if 0 <= b < len(arrays):
                for v in values:
                    masks[b] |= arrays[b] == v
    for band, (low, high) in (definition.get("ranges") or {}).items():
        targets = range(len(arrays)) if band == "ALL" else [int(band) - 1]
        for b in targets:
            if 0 <= b < len(arrays):
                masks[b] |= (arrays[b] < low) | (arrays[b] > high)
    combined = np.logical_and.reduce(masks) if definition.get("composite") else np.logical_or.reduce(masks)
    return [combined.copy() for _ in masks]


# ---- reading a mosaic dataset as one raster


def visible(item, cell):
    """Whether an item is in view at a cell size: MinPS ≤ cell ≤ MaxPS (INFERRED, ADR-230 §5.9 (e): a null MinPS is
    0, as the attribute table page says, and a null or zero MaxPS sets no upper limit, since a range of zero would hide
    an item that no tool has given ranges to)."""
    low = float(item.get("MinPS") or 0)
    high = item.get("MaxPS")
    tolerance = 1e-9 * max(1.0, cell)
    if cell < low - tolerance:
        return False
    return high in (None, 0) or cell <= float(high) + tolerance


def _number(value):
    """A field value or order base as a number: dates as days since 0001-01-01. ByAttribute's base may be written in
    any of the documented formats, YYYY/MM/DD HH:mm:ss.s down to YYYY."""
    if value is None:
        return None
    if isinstance(value, (int, float)):
        return float(value)
    if isinstance(value, datetime.datetime):
        return value.toordinal() + (value - datetime.datetime.combine(value.date(), datetime.time())).total_seconds() / 86400
    if isinstance(value, datetime.date):
        return float(value.toordinal())
    textual = str(value).strip()
    try:
        return float(textual)
    except ValueError:
        pass
    for form in ("%Y/%m/%d %H:%M:%S.%f", "%Y/%m/%d %H:%M:%S", "%Y/%m/%d %H:%M", "%Y/%m/%d %H", "%Y/%m/%d", "%Y/%m",
                 "%Y", "%Y-%m-%d %H:%M:%S.%f", "%Y-%m-%d %H:%M:%S", "%Y-%m-%dT%H:%M:%S", "%Y-%m-%d"):
        try:
            return _number(datetime.datetime.strptime(textual, form))
        except ValueError:
            continue
    raise ExecuteError(f"ERROR 000800: '{value}' is not a number or a date in one of the documented formats.")


def settings(rec, view):
    """The read's method, operator, order field, base, order and lock list: a layer's overrides over the mosaic's."""
    p = rec.properties
    method = (view.method if view is not None and view.method else None) or p.get("defaultMosaicMethod") or "NorthWest"
    operator = (view.operator if view is not None and view.operator else None) or p.get("mosaicOperator") or "FIRST"
    field = (view.order_field if view is not None and given(view.order_field) else None) or p.get("orderField") or ""
    base = view.order_base if view is not None and given(view.order_base) else p.get("orderBaseValue")
    ascending = view.ascending if view is not None and view.ascending is not None else bool(p.get("sortAscending", True))
    lock = view.lock if view is not None else None
    return method, str(operator).upper(), field, base, ascending, lock


def read(value, tool="Raster"):
    """(arrays, masks, grid, band names) of a mosaic dataset or mosaic layer read as one raster."""
    from ._raster import _Grid
    connection, name, rec, view = open_mosaic(value, tool)
    try:
        method, operator, order_field, order_base, ascending, lock = settings(rec, view)
        where = view.where if view is not None else None
        extra = [order_field] if order_field else []
        candidates = [i for i in items(connection, name, where, extra) if (i.get("Category") in (None, 1))]
        nodata_of = {i["OBJECTID"]: item_nodata(connection, name, i["OBJECTID"]) for i in candidates}
    finally:
        connection.close()
    props = rec.properties
    if method not in METHODS:
        raise NotBuiltError(f"{tool}: mosaic method '{method}' is not one of {', '.join(METHODS)}.")
    if method == "Viewpoint":
        raise NotBuiltError(f"{tool}: the Closest to Viewpoint mosaic method is not built in grapy: its viewpoint is "
                            "the one a user moves in ArcGIS Pro's display, which a tool on this server has no counterpart "
                            "of (ADR-230 §5.4).")
    if method == "Seamline" and operator == "LAST":
        raise ExecuteError(f"{tool}: LAST is not valid with the Seamline mosaic method, as MakeMosaicLayer's page says.")
    if operator not in OPERATORS:
        raise NotBuiltError(f"{tool}: mosaic operator '{operator}' is not one of {', '.join(OPERATORS)}.")
    if method == "LockRaster":
        if not lock:
            raise ExecuteError(f"{tool}: the Lock Raster mosaic method needs the rasters to lock, which MakeMosaicLayer's "
                               "lock_rasterid gives.")
        wanted = []
        for key in lock:
            for item in candidates:
                if (str(item["OBJECTID"]) == str(key) or str(item.get("Name") or "") == str(key)) and item not in wanted:
                    wanted.append(item)
        candidates = wanted

    # Frame Camera items are images in their sensor's geometry (ADR-234): read through their model, not as a grid.
    connection = _gpkg.connect(locate(value)[0])
    try:
        from . import _ortho
        framed = _ortho.models(connection, name)
    finally:
        connection.close()
    if any(i["OBJECTID"] in framed for i in candidates):
        raise NotBuiltError(f"{tool}: {name} holds Frame Camera items, which grapy does not orthorectify as a mosaic "
                            "dataset is read: the page of the Geometric function that would do it on the fly does not "
                            "define its approximation (ADR-234 §5.5); CreateOrthoCorrectedRasterDataset writes the "
                            "orthorectified raster.")

    # The items' files; a broken path is left out, with a warning (INFERRED).
    sources = {}
    for item in candidates:
        source = source_of(item.get("Raster"))
        if source is None:
            AddWarning(f"{tool}: item {item['OBJECTID']} of {name} is left out: its raster {item.get('Raster')} cannot be "
                       "found (RepairMosaicDatasetPaths repairs it).")
            continue
        sources[item["OBJECTID"]] = source
    candidates = [i for i in candidates if i["OBJECTID"] in sources]
    if not candidates:
        raise ExecuteError(f"ERROR 001000: {name} has no raster items to read" + (" where the query selects." if where else "."))

    # The cell size of the read: the layer's, the mosaic's, or the finest item's (INFERRED, §5.9 (e)).
    finest = min(candidates, key=lambda i: (sources[i["OBJECTID"]].cell, i["OBJECTID"]))
    anchor = sources[finest["OBJECTID"]]
    size = view.cell_size if view is not None and view.cell_size else props.get("cellSize")
    if size:
        pair = size if isinstance(size, (list, tuple)) else (size, size)
        width, height = float(pair[0]), float(pair[-1])
    else:
        width, height = anchor.width, anchor.height
    cell = (width + height) / 2
    candidates = [i for i in candidates if visible(i, cell)] if method != "LockRaster" else candidates
    if not candidates:
        raise ExecuteError(f"ERROR 001000: no raster of {name} is in view at a cell size of {cell} (MinPS and MaxPS).")

    # Bands and cell type.
    counts = {sources[i["OBJECTID"]].count for i in candidates}
    bands = rec.bands or (counts.pop() if len(counts) == 1 else None)
    if bands is None or any(sources[i["OBJECTID"]].count != bands for i in candidates):
        raise NotBuiltError(f"{tool}: items of {name} with different band counts are not built in grapy; which band of "
                            "one is which of another is a raster type's band definition (ADR-230 §5.3).")
    for i in candidates:
        s = sources[i["OBJECTID"]]
        if s.srid and rec.srid and s.srid != rec.srid:
            raise NotBuiltError(f"{tool}: item {i['OBJECTID']} is in EPSG:{s.srid} and the mosaic dataset in "
                                f"EPSG:{rec.srid}; projecting items on the fly is not built in grapy.")

    # The extent: the layer's template, else the boundary's envelope, else the items' (INFERRED).
    if view is not None and view.extent is not None:
        if view.extent == "MINOF":
            boxes = [sources[i["OBJECTID"]].box() for i in candidates]
            bounds = shapely.intersection_all(boxes).bounds
            if not bounds or any(math.isnan(b) for b in bounds):
                raise ExecuteError(f"ERROR 001000: the rasters of {name} have no area in common (MINOF).")
        else:
            bounds = view.extent
    elif rec.boundary is not None and not rec.boundary.is_empty:
        bounds = rec.boundary.bounds
    else:
        bounds = shapely.union_all([sources[i["OBJECTID"]].box() for i in candidates]).bounds
    left = anchor.left + math.floor((bounds[0] - anchor.left) / width + 1e-9) * width
    right = anchor.left + math.ceil((bounds[2] - anchor.left) / width - 1e-9) * width
    top = anchor.top - math.floor((anchor.top - bounds[3]) / height + 1e-9) * height
    bottom = anchor.top - math.ceil((anchor.top - bounds[1]) / height - 1e-9) * height
    columns, rows = int(round((right - left) / width)), int(round((top - bottom) / height))
    if rows <= 0 or columns <= 0:
        raise ExecuteError(f"ERROR 001000: the extent read from {name} holds no cells.")
    grid = _Grid(left, top, width, height, rows, columns, rec.srid or anchor.srid)
    area = shapely.box(left, bottom, right, top)

    # The items over the extent, in the method's order.
    candidates = [i for i in candidates if (i["shape"] if i["shape"] is not None else sources[i["OBJECTID"]].box())
                  .intersection(area).area > 0]
    ordered = order(candidates, sources, method, order_field, order_base, ascending, area)

    clip_footprint = bool(props.get("clipToFootprint"))
    opaque = not bool(props.get("footprintMayContainNoData", True))
    minimum = int(props.get("minimumPixelContribution") or 0)
    limit = int(props.get("maxRastersPerMosaic") or 0)
    resampling = str(props.get("defaultResamplingMethod") or "NEAREST").upper()
    placed = []
    for item in ordered:
        s = sources[item["OBJECTID"]]
        values, valid, cover, weight = _place(s, item, grid, nodata_of.get(item["OBJECTID"]), clip_footprint,
                                              resampling, tool)
        if cover is None or int(cover.sum()) < max(minimum, 1):
            continue
        # A footprint that may hold NoData lets the items under it show through its NoData cells; one that may not
        # decides every cell it covers (INFERRED, §5.9 (g)).
        if method == "Seamline":
            # ADR-161's seamline, the server's: each cell from the covering item whose centre is nearest it.
            xs, ys = grid.centres()
            cx, cy = _centre(item, s)
            weight = -np.hypot(xs[np.newaxis, :] - cx, ys[:, np.newaxis] - cy)
        placed.append((values, valid if not opaque else [cover] * len(valid), valid, cover, weight))
        if limit and len(placed) >= limit:
            break
    if not placed:
        raise ExecuteError(f"ERROR 001000: no raster of {name} has cells in the extent read.")

    dtype = np.dtype(PIXEL_TYPES[rec.pixel_type]) if rec.pixel_type in PIXEL_TYPES else np.result_type(
        *[p[0][0].dtype for p in placed])
    arrays, masks = [], []
    if operator == "BLEND":
        AddWarning(f"{tool}: BLEND weighs each cell by its distance inside its own item, plus one, up to 64 cells — this "
                   "server's weighting (ADR-158), not ArcGIS's, which is not published.")
    combine = "SEAMLINE" if method == "Seamline" and operator == "FIRST" else operator
    for b in range(bands):
        array, mask = _operate(combine, [(p[0][b], p[1][b], p[2][b], p[4]) for p in placed], (rows, columns), dtype,
                               tool)
        arrays.append(array)
        masks.append(mask)

    if bool(props.get("clipToBoundary", True)) and rec.boundary is not None and not rec.boundary.is_empty:
        from rasterio.features import geometry_mask
        outside = geometry_mask([rec.boundary], out_shape=(rows, columns), transform=grid.transform())
        masks = [m | outside for m in masks]
    names = None
    if view is not None and view.bands:
        picked = [int(b) for b in view.bands]
        if any(not 1 <= b <= bands for b in picked):
            raise ExecuteError(f"ERROR 000800: band_index names a band {name}'s {bands} bands do not have.")
        arrays, masks = [arrays[b - 1] for b in picked], [masks[b - 1] for b in picked]
        names = [f"Band_{b}" for b in picked] if len(picked) > 1 else None
    return arrays, masks, grid, names


def _centre(item, source):
    """An image's centre: the centre of its raster's extent, as the server's mosaic rule measures it (ADR-152)."""
    return (source.left + source.right) / 2, (source.top + source.bottom) / 2


def order(candidates, sources, method, order_field, order_base, ascending, area):
    """The items in the mosaic method's order, the one on top first: ZOrder (a null as 0, lower on top), then pixel size
    (INFERRED: finer on top), then the method's own criterion, then OBJECTID (INFERRED) — as the mosaicking rules page
    orders North-West, Closest to Center, By Attribute and Closest to Nadir. None keeps OBJECTID order; Lock Raster the
    order of the IDs given (INFERRED). Seamline is ordered as None: which item a cell takes is decided by distance
    (ADR-161)."""
    if method in ("None", "Seamline"):
        return sorted(candidates, key=lambda i: i["OBJECTID"])
    if method == "LockRaster":
        return list(candidates)

    def centre(item):
        return _centre(item, sources[item["OBJECTID"]])

    if method == "NorthWest":
        # INFERRED (§5.9 (f)), as the server's mosaic rule decided it (ADR-152): the north-west position is the
        # north-west corner of the extent read, and an image's centre is the centre of its raster's extent.
        x0, y0 = area.bounds[0], area.bounds[3]

        def criterion(item):
            x, y = centre(item)
            return math.hypot(x - x0, y - y0)
    elif method == "Center":
        # The centre of the screen is the centre of the extent read (INFERRED).
        c = area.centroid

        def criterion(item):
            x, y = centre(item)
            return math.hypot(x - c.x, y - c.y)
    elif method == "Nadir":
        # The nadir is the item's CenterX and CenterY, as the attribute table page says (its footprint's centroid where
        # they are null, INFERRED).
        c = area.centroid

        def criterion(item):
            x, y = item.get("CenterX"), item.get("CenterY")
            if x is None or y is None:
                x, y = centre(item)
            return math.hypot(float(x) - c.x, float(y) - c.y)
    elif method == "ByAttribute":
        if not order_field:
            raise ExecuteError("ERROR 000735: the By Attribute mosaic method needs an order field.")
        base = _number(order_base) if given(order_base) else 0.0

        def criterion(item):
            value = item.get(order_field)
            if value is None:
                return math.inf   # INFERRED: an item with no value in the order field goes under every other
            difference = abs(_number(value) - base)
            return difference if ascending else -difference
    else:
        raise NotBuiltError(f"mosaic method '{method}' is not built in grapy.")

    return sorted(candidates, key=lambda i: (int(i.get("ZOrder") or 0), round(sources[i["OBJECTID"]].cell, 9),
                                             criterion(i), i["OBJECTID"]))


def _place(source, item, grid, nodata, clip_footprint, resampling, tool):
    """An item's bands on the read's grid: (values, valid masks, covered cells, BLEND weights). A coarser item whose
    cell is a whole multiple of the grid's is repeated, which is NEAREST; any other resampling or alignment is refused.
    A cell's BLEND weight is its distance inside its item, counted in the item's own cells, plus one, up to 64 — the
    server's weighting (ADR-158)."""
    kx, ky = source.width / grid.width, source.height / grid.height
    if not (np.isclose(kx, round(kx)) and np.isclose(ky, round(ky))) or round(kx) < 1 or round(ky) < 1:
        raise NotBuiltError(f"{tool}: item {item['OBJECTID']}'s cell size ({source.width} by {source.height}) is not a "
                            f"whole multiple of the cell size read ({grid.width} by {grid.height}); resampling it is not "
                            "built in grapy (ADR-230 §5.3).")
    kx, ky = int(round(kx)), int(round(ky))
    if (kx > 1 or ky > 1) and resampling != "NEAREST":
        raise NotBuiltError(f"{tool}: item {item['OBJECTID']} is coarser than the cell size read, and {resampling} "
                            "resampling is not built in grapy; NEAREST is, exactly (ADR-230 §5.3).")
    fx, fy = (source.left - grid.left) / grid.width, (grid.top - source.top) / grid.height
    if not (np.isclose(fx, round(fx)) and np.isclose(fy, round(fy))):
        raise NotBuiltError(f"{tool}: item {item['OBJECTID']}'s cells do not line up with the mosaic's grid; resampling "
                            "it is not built in grapy (ADR-230 §5.3).")
    column0, row0 = int(round(fx)), int(round(fy))
    arrays, masks = source.read()
    masks = apply_nodata(arrays, masks, nodata)
    r, c = np.indices((source.rows, source.columns))
    inside = np.minimum(np.minimum(r, source.rows - 1 - r), np.minimum(c, source.columns - 1 - c))
    weights = np.minimum(inside + 1, 64).astype(np.float64)
    if kx > 1 or ky > 1:
        arrays = [np.repeat(np.repeat(a, ky, axis=0), kx, axis=1) for a in arrays]
        masks = [np.repeat(np.repeat(m, ky, axis=0), kx, axis=1) for m in masks]
        weights = np.repeat(np.repeat(weights, ky, axis=0), kx, axis=1)
    height, width = arrays[0].shape
    r0, r1 = max(row0, 0), min(row0 + height, grid.rows)
    c0, c1 = max(column0, 0), min(column0 + width, grid.columns)
    if r1 <= r0 or c1 <= c0:
        return None, None, None, None
    window = (slice(r0 - row0, r1 - row0), slice(c0 - column0, c1 - column0))
    values, valid = [], []
    cover = np.zeros((grid.rows, grid.columns), bool)
    cover[r0:r1, c0:c1] = True
    if clip_footprint and item["shape"] is not None:
        from rasterio.features import geometry_mask
        cover &= ~geometry_mask([item["shape"]], out_shape=(grid.rows, grid.columns), transform=grid.transform())
    for a, m in zip(arrays, masks):
        full = np.zeros((grid.rows, grid.columns), a.dtype)
        full[r0:r1, c0:c1] = a[window]
        ok = np.zeros((grid.rows, grid.columns), bool)
        ok[r0:r1, c0:c1] = ~m[window]
        values.append(full)
        valid.append(ok & cover)
    weight = np.zeros((grid.rows, grid.columns), np.float64)
    weight[r0:r1, c0:c1] = weights[window]
    return values, valid, cover, weight


def _operate(operator, layers, shape, dtype, tool):
    """One band mosaicked: ``layers`` are (values, decides, valid, weight) per item in order, top first. FIRST takes
    the first item that decides a cell, LAST the last; SEAMLINE the deciding item of greatest weight (the nearest
    centre), the first on a tie; MIN, MAX, MEAN, SUM and BLEND combine the valid values (INFERRED, as the server's
    mosaic reader does, ADR-158: NoData takes no part), BLEND weighted by each item's weight."""
    out = np.zeros(shape, np.float64 if operator in ("MEAN", "SUM", "BLEND") else dtype)
    if operator == "SEAMLINE":
        best = np.full(shape, -np.inf)
        mask = np.ones(shape, bool)
        for values, decides, valid, weight in layers:
            take = decides & (weight > best)
            out[take] = values[take]
            mask[take] = ~valid[take]
            best = np.where(take, weight, best)
        return out.astype(dtype), mask
    if operator in ("FIRST", "LAST"):
        decided = np.zeros(shape, bool)
        mask = np.ones(shape, bool)
        sequence = layers if operator == "FIRST" else list(reversed(layers))
        for values, decides, valid, _ in sequence:
            take = decides & ~decided
            out[take] = values[take]
            mask[take] = ~valid[take]
            decided |= decides
        return out.astype(dtype), mask
    stack = np.stack([v.astype(np.float64) for v, _, _, _ in layers])
    valid = np.stack([ok for _, _, ok, _ in layers])
    count = valid.sum(axis=0)
    mask = count == 0
    with np.errstate(all="ignore"):
        if operator == "MIN":
            result = np.where(valid, stack, np.inf).min(axis=0)
        elif operator == "MAX":
            result = np.where(valid, stack, -np.inf).max(axis=0)
        elif operator == "SUM":
            result = np.where(valid, stack, 0).sum(axis=0)
        elif operator == "BLEND":
            weights = np.where(valid, np.stack([w for _, _, _, w in layers]), 0)
            total = weights.sum(axis=0)
            result = (np.where(valid, stack, 0) * weights).sum(axis=0) / np.where(total == 0, 1, total)
        else:
            result = np.where(valid, stack, 0).sum(axis=0) / np.where(count == 0, 1, count)
    result = np.where(mask, 0, result)
    if np.issubdtype(dtype, np.integer):
        # INFERRED (§5.9 (g)): MEAN and BLEND of integer cells are rounded to the nearest whole number, half to even, and the
        # mosaic keeps its pixel type; a value its type cannot hold is refused rather than wrapped.
        result = np.rint(result)
        values = result[~mask]
        info = np.iinfo(dtype)
        if values.size and (values.min() < info.min or values.max() > info.max):
            raise NotBuiltError(f"{tool}: the {operator} of the items reaches values outside the mosaic's "
                                f"{dtype.name} cells, which are not built in grapy.")
    return result.astype(dtype), mask


# ---- Describe


def describe(value):
    """What Describe answers for a mosaic dataset, or None for anything else."""
    if not isinstance(value, (str, os.PathLike)) or not exists(value):
        return None
    return Described(value)


class Described:
    """Describe's Mosaic Dataset properties, with the Dataset and Raster Dataset groups the page says it supports and
    the catalog's fields (INFERRED: as a table's; ArcGIS Pro's cursors read a mosaic dataset's attribute table)."""

    def __init__(self, value):
        from . import Field
        from ._reference import Extent, SpatialReference
        connection, name, rec, view = open_mosaic(value, "Describe")
        try:
            catalog = items(connection, name)
            columns, _, _, geometry = _gpkg.describe(connection, name)
        finally:
            connection.close()
        file = locate(value)[0]
        self.catalogPath = f"{file}/{name}"
        self.name = self.baseName = name
        self.path = file
        self.dataType = self.datasetType = "MosaicDataset"
        self.dataElementType = "DEMosaicDataset"
        self.referenced = rec.referenced
        self.childrenNames = ["Footprint", "Boundary"]   # INFERRED: the side tables a user meets as layers
        self.spatialReference = SpatialReference(rec.srid) if rec.srid > 0 else None
        shapes = [i["shape"] for i in catalog if i["shape"] is not None]
        whole = rec.boundary if rec.boundary is not None else (shapely.union_all(shapes) if shapes else None)
        self.extent = Extent(*whole.bounds, self.spatialReference) if whole is not None and not whole.is_empty else None
        sources = [s for s in (source_of(i.get("Raster")) for i in catalog) if s is not None]
        self.bandCount = rec.bands or (sources[0].count if sources else 0)
        finest = min(sources, key=lambda s: s.cell) if sources else None
        self.meanCellWidth = finest.width if finest else None
        self.meanCellHeight = finest.height if finest else None
        if finest and self.extent is not None:
            self.width = int(math.ceil(self.extent.width / finest.width - 1e-9))
            self.height = int(math.ceil(self.extent.height / finest.height - 1e-9))
        else:
            self.width = self.height = 0
        kind = np.dtype(PIXEL_TYPES[rec.pixel_type]) if rec.pixel_type in PIXEL_TYPES else (
            sources[0].dtype if sources else None)
        self.pixelType = {np.dtype(np.uint8): "U8", np.dtype(np.int8): "S8", np.dtype(np.uint16): "U16",
                          np.dtype(np.int16): "S16", np.dtype(np.uint32): "U32", np.dtype(np.int32): "S32",
                          np.dtype(np.float32): "F32", np.dtype(np.float64): "F64"}.get(kind, "") if kind is not None else ""
        self.isInteger = bool(kind is not None and np.issubdtype(kind, np.integer))
        self.format = ""   # INFERRED: grapy's catalog is no file format of Esri's
        self.productDefinition = rec.product
        for key, val in rec.properties.items():
            if key != "cellSize":
                setattr(self, key, val)
        self.sortAscending = "Ascending" if rec.properties.get("sortAscending", True) else "Descending"
        self.isMultidimensional = False
        self.OIDFieldName = OID
        self.shapeFieldName = geometry
        self.shapeType = "Polygon"
        self.hasOID = True
        self.fields = [Field(OID, "OID", False), *[Field(c.name, c.type, c.nullable) for c in columns],
                       Field(geometry, "Geometry")]
        self.itemCount = len(catalog)
        self.children = []
