"""arcpy.da's cursors, by their documented names and tokens — ADR-223 §5.2.

The tokens a field list may hold: ``OID@``, ``SHAPE@`` (a geometry object), ``SHAPE@XY`` (the centroid's x and y),
``SHAPE@X``, ``SHAPE@Y``, ``SHAPE@Z``, ``SHAPE@M``, ``SHAPE@XYZ``, ``SHAPE@WKT``, ``SHAPE@WKB``, ``SHAPE@JSON``,
``SHAPE@AREA``, ``SHAPE@LENGTH``, and ``*`` for every field and the shape.

Z and M: a class whose geometry column carries z or m (gpkg_geometry_columns' flags) is read with them, and a shape
written to it is made to have exactly them — a missing z 0, a missing m NaN, and one the class does not carry dropped
(see ``_gpkg.encode``). ``SHAPE@Z``, ``SHAPE@M`` and ``SHAPE@XYZ`` read a point's own values; on other shapes they are
refused, since the documentation does not say which vertex's value they would be.
"""
from __future__ import annotations

import shapely
from shapely.geometry import Point as ShapelyPoint

from . import _gpkg
from ._geometry import Geometry, Point, wrap
from ._reference import SpatialReference
from ._runtime import NotBuiltError


def _columns(fields, described, oid):
    names = [c.name for c in described]
    if fields == "*" or fields == ["*"]:
        return ["OID@", *names, "SHAPE@"]
    if isinstance(fields, str):
        fields = [f.strip() for f in fields.split(";")]
    for f in fields:
        if not f.upper().startswith(("OID@", "SHAPE@")) and f not in names and f != oid:
            raise RuntimeError(f"Cannot find field '{f}'")
    return list(fields)


def _shape_token(token, shape, reference):
    token = token.upper()
    if shape is None:
        return None
    if token == "SHAPE@":
        return wrap(shape, reference)
    if token == "SHAPE@XY":
        c = shape if shape.geom_type == "Point" else shape.centroid
        return (c.x, c.y)
    if token == "SHAPE@X":
        return (shape if shape.geom_type == "Point" else shape.centroid).x
    if token == "SHAPE@Y":
        return (shape if shape.geom_type == "Point" else shape.centroid).y
    if token in ("SHAPE@Z", "SHAPE@M", "SHAPE@XYZ"):
        if shape.geom_type != "Point":
            raise NotBuiltError(f"arcpy.da: '{token}' on a {shape.geom_type} is not built in grapy — the documentation "
                                "gives a feature's value, not which vertex's.")
        z = float(shapely.get_z(shape)) if shapely.has_z(shape) else None
        if token == "SHAPE@Z":
            return z
        if token == "SHAPE@M":
            return float(shapely.get_m(shape)) if shapely.has_m(shape) else None
        return (shape.x, shape.y, z)
    if token == "SHAPE@WKT":
        return shapely.to_wkt(shape)
    if token == "SHAPE@WKB":
        return shapely.to_wkb(shape)
    if token == "SHAPE@JSON":
        return wrap(shape, reference).JSON
    if token == "SHAPE@AREA":
        return shape.area
    if token == "SHAPE@LENGTH":
        return shape.length
    raise RuntimeError(f"'{token}' is not a shape token grapy reads.")


def _shape_from(value):
    """A value written to a shape token, as a shapely geometry."""
    if value is None:
        return None
    if isinstance(value, Geometry):
        return value._shape
    if isinstance(value, Point):
        if value.Z is None and value.M is None:
            return ShapelyPoint(value.X, value.Y)
        from ._geometry import _from_points
        return _from_points("POINT", [[value]], value.Z is not None, value.M is not None)
    if isinstance(value, (tuple, list)) and len(value) in (2, 3) and all(isinstance(v, (int, float)) for v in value):
        return ShapelyPoint(*value)
    if isinstance(value, str):
        return shapely.from_wkt(value)
    if isinstance(value, (bytes, bytearray)):
        return shapely.from_wkb(bytes(value))
    return value


# The GeoPackages an Editor holds open, by normalised file path: a cursor on one of them works inside the Editor's
# transaction instead of opening its own connection.
_EDITING: dict = {}
# The version each of those Editors edits (ADR-227): cursors in its session read and write that version.
_EDITING_VERSION: dict = {}


def _file_key(path):
    import os
    return os.path.normcase(os.path.abspath(os.fspath(path)))


class _Cursor:
    def __init__(self, in_table, field_names):
        self._connection, self._table = _gpkg.table_of(in_table)
        self._shared = False
        if _EDITING:
            file = self._connection.execute("pragma database_list").fetchone()[2]
            shared = _EDITING.get(_file_key(file)) if file else None
            if shared is not None:
                self._connection.close()
                self._connection, self._shared = shared, True
        self._described, _, self._srid, self._geometry = _gpkg.describe(self._connection, self._table)
        self._has_z, self._has_m = _gpkg.dimensions(self._connection, self._table)
        self._oid = _gpkg.oid_column(self._connection, self._table)
        self._reference = SpatialReference(self._srid) if self._srid > 0 else None
        # A layer with joins (management.AddJoin) is read through them: its fields are qualified, <table>.<field>, for
        # the layer's own table and each join table alike, and an unqualified name is not one of them.
        from . import _layers
        layer = _layers.find(in_table)
        self._joined = None
        if layer is not None and layer.joins:
            if self._shared:
                raise NotBuiltError("arcpy.da: a cursor on a joined layer inside an edit session is not built in grapy.")
            self._joined = _layers.joined(layer, self._connection, self._table, self._oid, self._geometry)
            self._described = [_gpkg.Column(name, kind) for name, _, kind, _ in self._joined.fields]
            try:
                self.fields = tuple(_columns(field_names, self._described, None))
            except RuntimeError as error:
                raise RuntimeError(f"{error}: on a joined layer a field is named <table>.<field>, such as "
                                   f"{self._joined.names[0] if self._joined.names else 'table.field'}.") from None
        else:
            self.fields = tuple(_columns(field_names, self._described, self._oid))
        self._versioned = self._version_of(layer)
        if self._versioned is not None and self._joined is not None:
            raise NotBuiltError("arcpy.da: a cursor on a joined layer of a versioned class is not built in grapy.")

    def _version_of(self, layer):
        """(branch, base moment, until moment) when this cursor reads a named version or a historical moment of a
        registered class (ADR-227), else None: a layer's version or moment (management.ChangeVersion), or the version
        of the Editor whose session this cursor joins."""
        from . import _versions
        kind = _versions.kind(self._connection, self._table)
        if kind is None:
            return None
        name = getattr(layer, "version", None) if layer is not None else None
        until = getattr(layer, "moment", None) if layer is not None else None
        if name is None and self._shared:
            file = self._connection.execute("pragma database_list").fetchone()[2]
            name = _EDITING_VERSION.get(_file_key(file))
        branch, base = 0, 0
        if name is not None:
            _, branch, base = _versions.version(self._connection, name)
        if branch == 0 and until is None:
            return None
        if branch and kind != "branch":
            raise NotBuiltError(f"arcpy.da: '{self._table}' is archived, not branch versioned; it has no named versions.")
        return branch, base, until

    def _writable_version(self):
        if self._versioned[2] is not None:
            raise RuntimeError(f"arcpy.da: '{self._table}' is read at a historical moment, which cannot be edited.")
        return self._versioned[0], self._versioned[1]

    def _row_values(self, row):
        """{column: value} of the fields a write names, the geometry encoded under its column name."""
        out = {}
        for field, value in zip(self.fields, row):
            upper = field.upper()
            if upper == "OID@":
                continue
            if upper.startswith("SHAPE@"):
                out[self._geometry] = _gpkg.encode(_shape_from(value), self._srid, self._has_z, self._has_m)
            else:
                out[field] = value
        return out

    def _select(self):
        out = []
        for f in self.fields:
            upper = f.upper()
            if self._joined is not None:
                out.append(self._joined.oid_sql if upper == "OID@" else self._joined.geometry_sql
                           if upper.startswith("SHAPE@") else self._joined.sql(f))
            else:
                out.append(f'"{self._oid}"' if upper == "OID@" else f'"{self._geometry}"' if upper.startswith("SHAPE@") else f'"{f}"')
        return ", ".join(out)

    def _from(self):
        if getattr(self, "_versioned", None) is not None:
            from . import _versions
            branch, base, until = self._versioned
            return _versions.state_sql(self._connection, self._table, branch, base, until)
        return self._joined.from_sql if self._joined is not None else f'"{self._table}"'

    def _where(self, in_table, where_clause):
        from . import _layers
        if self._joined is not None:
            return _layers.combined(_layers.find(in_table), self._joined.oid_sql, where_clause, quoted=True)
        return _layers.combined(_layers.find(in_table), self._oid, where_clause)

    def _value(self, field, raw):
        return _shape_token(field, _gpkg.decode(raw), self._reference) if field.upper().startswith("SHAPE@") else raw

    def __enter__(self):
        return self

    def __exit__(self, *_):
        self._close()
        return False

    def _close(self):
        if self._connection is not None:
            if getattr(self, "_joined", None) is not None:
                self._connection.commit()
                for schema in self._joined.attached:
                    self._connection.execute(f"detach database {schema}")
            if not self._shared:
                self._connection.commit()
                self._connection.close()
            self._connection = None

    def __del__(self):
        try:
            self._close()
        except Exception:
            pass


class _RatCursor:
    """A cursor on a raster's attribute table, read as a table: ``OID@`` (the row's position from 1) and its fields,
    by name without regard to case. Rows are held in an in-memory SQLite table so a where clause and sql_clause work
    as on a feature class; an update cursor writes the table back to the raster's .aux.xml when it closes.

    INFERRED: ``OID@`` is the row's position from 1, as ArcGIS shows an ObjectID column on an attribute table."""

    def __init__(self, in_table, field_names, where_clause=None, update=False, sql_clause=(None, None)):
        import sqlite3
        from ._raster import _read_rat
        self._path, self._update = in_table, update
        self._rat = _read_rat(in_table)
        if self._rat is None:
            raise RuntimeError(f"'{in_table}' has no attribute table; build one (management.BuildRasterAttributeTable).")
        names = self._rat.names()
        lower = {n.lower(): n for n in names}
        if field_names in ("*", ["*"]):
            field_names = ["OID@", *names]
        if isinstance(field_names, str):
            field_names = [f.strip() for f in field_names.split(";")]
        fields = []
        for f in field_names:
            if f.upper() == "OID@":
                fields.append("OID@")
            elif f.upper().startswith("SHAPE@"):
                raise RuntimeError(f"'{f}': a raster attribute table has no shapes.")
            elif f.lower() in lower:
                fields.append(lower[f.lower()])
            else:
                raise RuntimeError(f"Cannot find field '{f}'")
        self.fields = tuple(fields)
        self._connection = sqlite3.connect(":memory:")
        kinds = {"Integer": "INTEGER", "Double": "REAL", "String": "TEXT"}
        defined = ", ".join(f'"{n}" {kinds[k]}' for n, k, _ in self._rat.fields)
        self._connection.execute(f'create table rat (oid integer primary key{", " + defined if defined else ""})')
        for i, row in enumerate(self._rat.rows, 1):
            marks = ", ".join("?" for _ in range(len(row) + 1))
            self._connection.execute(f"insert into rat values ({marks})", [i, *row])
        columns = ", ".join("oid" if f == "OID@" else f'"{f}"' for f in self.fields)
        prefix, postfix = sql_clause or (None, None)
        sql = f'select oid, {columns} from rat' + (f" where {where_clause}" if where_clause else "")
        sql += f" {postfix}" if postfix else " order by oid"
        self._rows = self._connection.execute(sql).fetchall()
        self._at = -1

    def __enter__(self):
        return self

    def __exit__(self, *_):
        self._close()
        return False

    def __iter__(self):
        return self

    def __next__(self):
        self._at += 1
        if self._at >= len(self._rows):
            raise StopIteration
        values = self._rows[self._at][1:]
        return list(values) if self._update else tuple(values)

    def next(self):
        return self.__next__()

    def reset(self):
        self._at = -1

    def updateRow(self, row):
        if not self._update:
            raise RuntimeError("A search cursor does not update rows.")
        oid = self._rows[self._at][0]
        pairs = [(f, v) for f, v in zip(self.fields, row) if f != "OID@"]
        if pairs:
            assignments = ", ".join(f'"{f}" = ?' for f, _ in pairs)
            self._connection.execute(f"update rat set {assignments} where oid = ?", [*[v for _, v in pairs], oid])

    def deleteRow(self):
        if not self._update:
            raise RuntimeError("A search cursor does not delete rows.")
        self._connection.execute("delete from rat where oid = ?", (self._rows[self._at][0],))

    def _close(self):
        if self._connection is None:
            return
        if self._update:
            from ._raster import _write_rat
            names = [n for n, _, _ in self._rat.fields]
            columns = ", ".join(f'"{n}"' for n in names)
            rows = self._connection.execute(f"select {columns} from rat order by oid").fetchall() if names else []
            self._rat.rows = [list(r) for r in rows]
            _write_rat(self._path, self._rat)
        self._connection.close()
        self._connection = None


def _raster_table(value):
    from ._raster import _is_raster_path
    return _is_raster_path(value)


class SearchCursor(_Cursor):
    """Reads rows: ``with SearchCursor(fc, ["name", "SHAPE@"]) as rows: for name, shape in rows: …``"""

    def __new__(cls, in_table, field_names=None, where_clause=None, *args, **kwargs):
        if _raster_table(in_table):
            sql = kwargs.get("sql_clause", args[2] if len(args) > 2 else (None, None))
            return _RatCursor(in_table, field_names, where_clause, update=issubclass(cls, UpdateCursor), sql_clause=sql)
        return super().__new__(cls)

    def __init__(self, in_table, field_names, where_clause=None, spatial_reference=None, explode_to_points=False,
                 sql_clause=(None, None), datum_transformation=None):
        if explode_to_points:
            raise NotBuiltError("arcpy.da.SearchCursor: explode_to_points=True is not built in grapy.")
        super().__init__(in_table, field_names)
        where_clause = self._where(in_table, where_clause)
        self._where_sql = where_clause
        prefix, postfix = sql_clause or (None, None)
        sql = f'select {"distinct " if prefix and "DISTINCT" in prefix.upper() else ""}{self._select()} from {self._from()}'
        if where_clause:
            sql += f" where {where_clause}"
        if postfix:
            sql += f" {postfix}"
        self._rows = self._connection.execute(sql)
        self._target = spatial_reference

    def __iter__(self):
        return self

    def __next__(self):
        row = self._rows.fetchone()
        if row is None:
            raise StopIteration
        values = [self._value(f, v) for f, v in zip(self.fields, row)]
        if self._target is not None:
            values = [v.projectAs(self._target) if isinstance(v, Geometry) else v for v in values]
        return tuple(values)

    def next(self):
        return self.__next__()

    def reset(self):
        where = getattr(self, "_where_sql", None)
        self._rows = self._connection.execute(f'select {self._select()} from {self._from()}' + (f" where {where}" if where else ""))


class InsertCursor(_Cursor):
    """Writes rows: ``with InsertCursor(fc, ["name", "SHAPE@"]) as rows: rows.insertRow(("a", geometry))``"""

    def insertRow(self, row):
        if self._joined is not None:
            raise NotBuiltError("arcpy.da.InsertCursor on a joined layer is not built in grapy; insert into its table.")
        if len(row) != len(self.fields):
            raise RuntimeError(f"insertRow takes {len(self.fields)} values, one per field; {len(row)} were given.")
        if self._versioned is not None:
            from . import _versions
            branch, _ = self._writable_version()
            object_id = _versions.reserve_id(self._connection, self._table)
            _versions.write(self._connection, self._table, branch, object_id, self._row_values(row))
            return object_id
        names, values = [], []
        for field, value in zip(self.fields, row):
            upper = field.upper()
            if upper == "OID@":
                continue
            if upper.startswith("SHAPE@"):
                names.append(self._geometry)
                values.append(_gpkg.encode(_shape_from(value), self._srid, self._has_z, self._has_m))
            else:
                names.append(field)
                values.append(value)
        quoted = ", ".join('"' + n + '"' for n in names)
        marks = ", ".join("?" for _ in names)
        cursor = self._connection.execute(f'insert into "{self._table}" ({quoted}) values ({marks})', values)
        return cursor.lastrowid


class UpdateCursor(SearchCursor):
    """Reads and changes rows: ``updateRow(row)`` writes the current one, ``deleteRow()`` removes it."""

    def __init__(self, in_table, field_names, where_clause=None, spatial_reference=None, explode_to_points=False,
                 sql_clause=(None, None), datum_transformation=None):
        _Cursor.__init__(self, in_table, field_names)
        where_clause = self._where(in_table, where_clause)
        own_oid = self._joined.oid_sql if self._joined is not None else f'"{self._oid}"'
        sql = f'select {own_oid}, {self._select()} from {self._from()}'
        if where_clause:
            sql += f" where {where_clause}"
        self._rows = self._connection.execute(sql).fetchall()
        self._at = -1
        self._target = None

    def __next__(self):
        self._at += 1
        if self._at >= len(self._rows):
            raise StopIteration
        row = self._rows[self._at]
        return [self._value(f, v) for f, v in zip(self.fields, row[1:])]

    def updateRow(self, row):
        oid = self._rows[self._at][0]
        if self._versioned is not None:
            from . import _versions
            branch, base = self._writable_version()
            current = _versions.current_row(self._connection, self._table, branch, base, oid)
            current.update(self._row_values(row))
            _versions.write(self._connection, self._table, branch, oid, current)
            return
        names, values = [], []
        for i, (field, value) in enumerate(zip(self.fields, row)):
            upper = field.upper()
            if upper == "OID@":
                continue
            if upper.startswith("SHAPE@"):
                names.append(self._geometry)
                values.append(_gpkg.encode(_shape_from(value), self._srid, self._has_z, self._has_m))
            elif self._joined is not None and self._joined.column(field) is None:
                # A join table's field: read as it was, it is passed over; changed, it is refused — the join is the
                # layer's, and its table is not written through it.
                if value != self._rows[self._at][1 + i]:
                    raise NotBuiltError(f"arcpy.da.UpdateCursor: '{field}' belongs to a joined table and is not written "
                                        "through the join in grapy.")
            else:
                names.append(self._joined.column(field) if self._joined is not None else field)
                values.append(value)
        if not names:
            return
        assignments = ", ".join(f'"{n}" = ?' for n in names)
        self._connection.execute(f'update "{self._table}" set {assignments} where "{self._oid}" = ?', [*values, oid])

    def deleteRow(self):
        if self._versioned is not None:
            from . import _versions
            branch, base = self._writable_version()
            oid = self._rows[self._at][0]
            current = _versions.current_row(self._connection, self._table, branch, base, oid)
            _versions.write(self._connection, self._table, branch, oid, current, deleted=True)
            return
        # A composite relationship class deletes the row's destination rows with it (ADR-225, grapy._relationships).
        from . import _relationships
        _relationships.cascade(self._connection, self._table, [self._rows[self._at][0]])
        self._connection.execute(f'delete from "{self._table}" where "{self._oid}" = ?', (self._rows[self._at][0],))


def Describe(value, datatype=None):
    """arcpy.da.Describe: the same description as arcpy.Describe, as a dictionary of its properties."""
    from . import Describe as describe
    described = describe(value)
    names = [n for n in dir(described) if not n.startswith("_")]
    return {n: getattr(described, n) for n in names if not callable(getattr(described, n))}


# ---- Walk ---------------------------------------------------------------------------------------------------------


def Walk(workspace, topdown=True, onerror=None, followlinks=False, datatype=None, type=None):
    """(dirpath, dirnames, filenames) for each folder and GeoPackage under the workspace, as os.walk does for files.
    A GeoPackage is a workspace, as a file geodatabase is in arcpy: it appears among its folder's dirnames, and its
    tables are its filenames. In a folder, the filenames are its raster files (RasterDataset) and its .csv and .dbf
    files (Table). ``datatype`` keeps FeatureClass, Table and RasterDataset; any other type finds nothing; ``type``
    keeps a feature class's shape type (Point, Multipoint, Polyline, Polygon) or a raster's format (TIF, PNG …).
    INFERRED: which folder files count as data, and that a GeoPackage is walked as a workspace."""
    import os
    from . import _RASTER_EXTENSIONS, _contents
    kinds = None if datatype in (None, "", "Any") else {d.upper() for d in ([datatype] if isinstance(datatype, str) else datatype)}
    types = None if type in (None, "", "All", "Any") else {t.upper() for t in ([type] if isinstance(type, str) else type)}

    def keep(kind, subtype):
        if kinds is not None and kind.upper() not in kinds:
            return False
        if types is not None and kind in ("FeatureClass", "RasterDataset") and (subtype or "").upper() not in types:
            return False
        return True

    def listing(path):
        if path.lower().endswith(".gpkg"):
            names = []
            for table, kind, shape in _contents(path):
                label = {"features": "FeatureClass", "table": "Table", "raster": "RasterDataset"}[kind]
                if keep(label, shape if kind == "features" else None):
                    names.append(table)
            return [], names
        try:
            entries = sorted(os.listdir(path))
        except OSError as error:
            if onerror is not None:
                onerror(error)
            return None, None
        folders, files = [], []
        for name in entries:
            full = os.path.join(path, name)
            if os.path.isdir(full) or name.lower().endswith(".gpkg"):
                folders.append(name)
                continue
            extension = os.path.splitext(name)[1].lower()
            if extension in _RASTER_EXTENSIONS and keep("RasterDataset", _RASTER_EXTENSIONS[extension]):
                files.append(name)
            elif extension in (".csv", ".dbf") and keep("Table", None):
                files.append(name)
        return folders, files

    def walk(path):
        folders, files = listing(path)
        if folders is None:
            return
        if topdown:
            yield path, folders, files
        for name in list(folders):
            yield from walk(os.path.join(path, name))
        if not topdown:
            yield path, folders, files

    yield from walk(os.fspath(workspace))


# ---- NumPy --------------------------------------------------------------------------------------------------------

_NUMPY_TYPES = {"SmallInteger": "<i2", "Integer": "<i4", "BigInteger": "<i8", "Single": "<f4", "Double": "<f8",
                "Date": "<M8[us]"}


def _array(tool, in_table, field_names, where_clause, skip_nulls, null_value, spatial_reference=None,
           explode_to_points=False):
    """A NumPy structured array of the fields and tokens asked, as TableToNumPyArray and FeatureClassToNumPyArray
    document: nulls replaced by ``null_value`` first, then records with a null skipped when ``skip_nulls`` is True or
    a function of the object id that answers True. A float null is nan. INFERRED: a text field holds its longest value
    (``<U`` of that length), or Python objects with None for a null; an integer or date null with no null_value is
    refused, since "integer types do not support the concept of null values"."""
    import numpy as np
    connection, table = _gpkg.table_of(in_table)
    try:
        described, _, _, _ = _gpkg.describe(connection, table)
        oid = _gpkg.oid_column(connection, table)
    finally:
        connection.close()
    types = {c.name.lower(): c.type for c in described}
    if field_names in (None, "*", ["*"]):
        names = ["OID@", *[c.name for c in described if c.type != "Blob"]]
    else:
        names = [field_names] if isinstance(field_names, str) else list(field_names)
    for name in names:
        upper = name.upper()
        if upper == "SHAPE@":
            raise NotBuiltError(f"arcpy.da.{tool}: '{name}' is not built in grapy — a geometry object does not go in an "
                                "array.")
    reading = [n if n.upper() != "SHAPE@TRUECENTROID" else "SHAPE@XY" for n in names]
    if "OID@" not in [n.upper() for n in reading]:
        reading.append("OID@")
    position = [n.upper() for n in reading].index("OID@")
    rows = []
    with SearchCursor(in_table, reading if explode_to_points is False else [*reading, "SHAPE@"], where_clause,
                      spatial_reference) as cursor:
        for row in cursor:
            values = list(row[:len(reading)])
            if explode_to_points:
                shape = row[-1]._shape if row[-1] is not None else None
                points = list(_vertices(shape)) if shape is not None else [None]
                for point in points:
                    tokens = {"SHAPE@XY": lambda p: (p[0], p[1]), "SHAPE@X": lambda p: p[0], "SHAPE@Y": lambda p: p[1],
                              "SHAPE@Z": lambda p: p[2], "SHAPE@M": lambda p: p[3],
                              "SHAPE@XYZ": lambda p: (p[0], p[1], p[2])}
                    rows.append([(tokens[n.upper()](point) if n.upper() in tokens else v) if point is not None else v
                                 for n, v in zip(reading, values)])
            else:
                rows.append(values)
    out = []
    for values in rows:
        for i, name in enumerate(reading[:len(names)]):
            if values[i] is None and null_value is not None:
                values[i] = null_value.get(names[i], None) if isinstance(null_value, dict) else null_value
        has_null = any(v is None for v in values[:len(names)])
        if has_null and skip_nulls:
            if callable(skip_nulls):
                if skip_nulls(values[position]):
                    continue
            else:
                continue
        out.append(values[:len(names)])
    dtype = []
    for i, name in enumerate(names):
        upper = name.upper()
        column = [r[i] for r in out]
        if upper == "OID@":
            kind = "<i4"
        elif upper in ("SHAPE@XY", "SHAPE@TRUECENTROID"):
            kind = ("<f8", 2)
        elif upper == "SHAPE@XYZ":
            kind = ("<f8", 3)
        elif upper in ("SHAPE@X", "SHAPE@Y", "SHAPE@Z", "SHAPE@M", "SHAPE@AREA", "SHAPE@LENGTH"):
            kind = "<f8"
        elif upper == "SHAPE@WKB":
            kind = "O"
        elif upper in ("SHAPE@WKT", "SHAPE@JSON") or types.get(name.lower()) in ("String", "Guid", None):
            kind = "O" if any(v is None for v in column) else f"<U{max([len(str(v)) for v in column] + [1])}"
        else:
            kind = _NUMPY_TYPES.get(types.get(name.lower()), "O")
        if kind not in ("O", ("<f8", 2), ("<f8", 3)) and not str(kind).startswith(("<U", "<f")) and any(v is None for v in column):
            raise ValueError(f"arcpy.da.{tool}: field '{name}' holds nulls, which an integer or date array cannot; pass "
                             "null_value or skip_nulls.")
        if kind in ("<f8", "<f4"):
            for r in out:
                if r[i] is None:
                    r[i] = np.nan
        if isinstance(kind, tuple):
            for r in out:
                if r[i] is None:
                    r[i] = (np.nan,) * kind[1]
                elif any(v is None for v in r[i]):
                    r[i] = tuple(np.nan if v is None else v for v in r[i])
        dtype.append((name, *kind) if isinstance(kind, tuple) else (name, kind))
    return np.array([tuple(r) for r in out], dtype=np.dtype(dtype))


def _vertices(shape):
    """Each vertex as (x, y, z, m), z and m None when the shape has none."""
    has_z, has_m = bool(shapely.has_z(shape)), bool(shapely.has_m(shape))
    for c in shapely.get_coordinates(shape, include_z=has_z, include_m=has_m):
        yield (float(c[0]), float(c[1]), float(c[2]) if has_z else None, float(c[-1]) if has_m else None)


def TableToNumPyArray(in_table, field_names="*", where_clause=None, skip_nulls=False, null_value=None):
    """A table as a NumPy structured array. Tokens: OID@, SHAPE@XY, SHAPE@X, SHAPE@Y, SHAPE@AREA, SHAPE@LENGTH,
    SHAPE@WKT, SHAPE@WKB, SHAPE@JSON. See ``_array`` for nulls."""
    return _array("TableToNumPyArray", in_table, field_names, where_clause, skip_nulls, null_value)


def FeatureClassToNumPyArray(in_table, field_names="*", where_clause=None, spatial_reference=None,
                             explode_to_points=False, skip_nulls=False, null_value=None):
    """A feature class as a NumPy structured array, projected to ``spatial_reference`` when given; with
    ``explode_to_points`` one row per vertex, SHAPE@XY, SHAPE@X and SHAPE@Y that vertex's. INFERRED: an exploded
    row's other tokens (area, length) are its feature's."""
    return _array("FeatureClassToNumPyArray", in_table, field_names, where_clause, skip_nulls, null_value,
                  spatial_reference, bool(explode_to_points))


def _column_type(tool, name, kind):
    import numpy as np
    if kind.shape:
        raise NotBuiltError(f"arcpy.da.{tool}: field '{name}' is an array of {kind.shape}; only scalar fields are built.")
    if np.issubdtype(kind, np.bool_):
        return "SmallInteger"
    if np.issubdtype(kind, np.integer):
        return {1: "SmallInteger", 2: "SmallInteger", 4: "Integer"}.get(kind.itemsize, "BigInteger")
    if np.issubdtype(kind, np.floating):
        return "Single" if kind.itemsize == 4 else "Double"
    if np.issubdtype(kind, np.datetime64):
        return "Date"
    if kind.kind in ("U", "S", "O"):
        return "String"
    raise NotBuiltError(f"arcpy.da.{tool}: field '{name}' of type {kind} is not built in grapy.")


def _python(value):
    import numpy as np
    if isinstance(value, bytes):
        return value.decode("utf-8")
    if isinstance(value, np.datetime64):
        return str(value.astype("datetime64[us]")).replace("T", " ")
    if isinstance(value, np.generic):
        value = value.item()
    if isinstance(value, float) and value != value:
        return None
    return value


def _new(tool, out_table):
    from . import Exists
    from .analysis import _out
    out = _out(out_table)
    if Exists(out):
        raise RuntimeError(f"arcpy.da.{tool}: '{out}' already exists; the function does not overwrite, as documented.")
    return out


def NumPyArrayToTable(in_array, out_table):
    """A NumPy structured array written as a new table, its field names validated (an invalid character becomes an
    underscore). It does not overwrite an existing table, as documented."""
    from . import ValidateFieldName
    out = _new("NumPyArrayToTable", out_table)
    names = list(in_array.dtype.names or [])
    if not names:
        raise ValueError("arcpy.da.NumPyArrayToTable takes a structured array.")
    columns = [_gpkg.Column(ValidateFieldName(n), _column_type("NumPyArrayToTable", n, in_array.dtype[n])) for n in names]
    _gpkg.create(out, "Geometry", 0, columns)
    with InsertCursor(out, [*[c.name for c in columns], "SHAPE@"]) as written:
        for record in in_array:
            written.insertRow((*[_python(record[n]) for n in names], None))
    return out


_TYPE_WORDS = {"TEXT": "String", "STRING": "String", "SHORT": "SmallInteger", "SMALLINTEGER": "SmallInteger",
               "LONG": "Integer", "INTEGER": "Integer", "BIGINTEGER": "BigInteger", "FLOAT": "Single", "SINGLE": "Single",
               "DOUBLE": "Double", "DATE": "Date", "GUID": "Guid", "BLOB": "Blob"}


def CreateTable(path, fields=None, shape=None, configuration=None):
    """A new table, or with ``shape`` a feature class, answering ``(path, [field names])``. The names are validated (an
    invalid character becomes an underscore) and may differ from those asked, as documented.

    ``fields`` is tuples ``(name, type, alias, length, nullable, default)``, Field objects, or a NumPy dtype. ``shape``
    is a tuple ``(geometry type, spatial reference, has z, has m)``. Alias and length change nothing in a GeoPackage
    (INFERRED, as for AddField); a default value, z, m, a multipatch and ``configuration`` (enterprise geodatabases only)
    are refused by name."""
    from . import ValidateFieldName
    from ._reference import reference_of
    if configuration not in (None, "", "#"):
        raise NotBuiltError("arcpy.da.CreateTable: configuration applies to enterprise geodatabases only, which a GeoPackage is not.")
    columns = []
    if fields is not None and hasattr(fields, "names") and fields.names:
        columns = [_gpkg.Column(ValidateFieldName(n), _column_type("CreateTable", n, fields[n])) for n in fields.names]
    else:
        for field in fields or []:
            if isinstance(field, (tuple, list)):
                name, kind = field[0], str(field[1])
                if len(field) > 5 and field[5] is not None:
                    raise NotBuiltError("arcpy.da.CreateTable: a field's default value is not built in grapy yet.")
                nullable = True if len(field) <= 4 or field[4] in (None, True) else bool(field[4])
            else:
                name, kind, nullable = field.name, str(field.type), getattr(field, "isNullable", True)
                if kind in ("OID", "Geometry", "GlobalID"):
                    continue
            stored = _TYPE_WORDS.get(kind.upper().replace(" ", ""))
            if stored is None:
                raise NotBuiltError(f"arcpy.da.CreateTable: field type '{kind}' is not built in grapy.")
            columns.append(_gpkg.Column(ValidateFieldName(name), stored, bool(nullable)))
    kind, srid = "Geometry", 0
    if shape is not None:
        shape = (shape,) if isinstance(shape, str) else tuple(shape)
        geometry = str(shape[0]).upper()
        if geometry == "MULTIPATCH" or any(bool(v) for v in shape[2:4]):
            raise NotBuiltError("arcpy.da.CreateTable: z, m and multipatch geometry are not built — grapy's feature classes are 2D.")
        kinds = {"POINT": "Point", "MULTIPOINT": "Multipoint", "POLYLINE": "Polyline", "POLYGON": "Polygon"}
        if geometry not in kinds:
            raise ValueError(f"arcpy.da.CreateTable: '{shape[0]}' is not a geometry type.")
        kind = kinds[geometry]
        reference = reference_of(shape[1]) if len(shape) > 1 and shape[1] not in (None, "") else None
        srid = reference.factoryCode if reference else 0
    out = _new("CreateTable", path)
    _gpkg.create(out, kind, srid, columns)
    return out, [c.name for c in columns]


def NumPyArrayToFeatureClass(in_array, out_table, shape_fields, spatial_reference=None):
    """A NumPy structured array written as a new point feature class: its x and y from two fields, or from one field
    of two values; the other fields as attributes. z and m are refused: grapy's shapes hold neither."""
    from . import ValidateFieldName
    from ._reference import reference_of
    out = _new("NumPyArrayToFeatureClass", out_table)
    shape = [f for f in ([shape_fields] if isinstance(shape_fields, str) else list(shape_fields))]
    if len(shape) > 2 and any(f not in (None, "") for f in shape[2:]):
        raise NotBuiltError("arcpy.da.NumPyArrayToFeatureClass: z and m shape fields are not built in grapy.")
    shape = [f for f in shape[:2] if f not in (None, "")]
    names = [n for n in in_array.dtype.names if n not in shape]
    columns = [_gpkg.Column(ValidateFieldName(n), _column_type("NumPyArrayToFeatureClass", n, in_array.dtype[n]))
               for n in names]
    reference = reference_of(spatial_reference)
    _gpkg.create(out, "Point", reference.factoryCode if reference else 0, columns)
    with InsertCursor(out, [*[c.name for c in columns], "SHAPE@"]) as written:
        for record in in_array:
            xy = tuple(float(v) for v in record[shape[0]]) if len(shape) == 1 else (float(record[shape[0]]),
                                                                                   float(record[shape[1]]))
            written.insertRow((*[_python(record[n]) for n in names], ShapelyPoint(*xy)))
    return out


def ExtendTable(in_table, table_match_field, in_array, array_match_field, append_only=True):
    """The array's fields joined to the table permanently where ``table_match_field`` equals ``array_match_field``,
    the array's match values unique. With ``append_only`` a field the table already has is an error; without it, that
    field is updated."""
    from . import ValidateFieldName
    names = [n for n in in_array.dtype.names if n != array_match_field]
    keys = [_python(v) for v in in_array[array_match_field]]
    if len(set(keys)) != len(keys):
        raise ValueError("arcpy.da.ExtendTable: the array's match values must be unique.")
    by_key = {k: record for k, record in zip(keys, in_array)}
    connection, table = _gpkg.table_of(in_table)
    try:
        existing = {c.name.lower(): c.name for c in _gpkg.describe(connection, table)[0]}
    finally:
        connection.close()
    targets = []
    for name in names:
        valid = ValidateFieldName(name)
        if valid.lower() in existing:
            if append_only:
                raise RuntimeError(f"arcpy.da.ExtendTable: the table already has a field '{valid}' and append_only is True.")
            targets.append(existing[valid.lower()])
        else:
            _gpkg.add_column(in_table, _gpkg.Column(valid, _column_type("ExtendTable", name, in_array.dtype[name])))
            targets.append(valid)
    with UpdateCursor(in_table, [table_match_field, *targets]) as rows:
        for row in rows:
            record = by_key.get(row[0])
            if record is None:
                continue
            rows.updateRow([row[0], *[_python(record[n]) for n in names]])
    return in_table


# ---- domains ------------------------------------------------------------------------------------------------------

_POLICIES = {"DEFAULT": "DefaultValue", "DUPLICATE": "Duplicate", "GEOMETRY_RATIO": "GeometryRatio",
             "SUM_VALUES": "SumValues", "AREA_WEIGHTED": "AreaWeighted"}
_DOMAIN_FIELD_TYPES = {"SHORT": "Short", "LONG": "Long", "BIGINTEGER": "BigInteger", "FLOAT": "Float",
                       "DOUBLE": "Double", "TEXT": "Text", "DATE": "Date"}


class Domain:
    """An attribute domain, as ListDomains answers it."""

    def __init__(self, name, description, domainType, type, codedValues=None, range=None, splitPolicy="DefaultValue",
                 mergePolicy="DefaultValue", owner=""):
        self.name, self.description, self.domainType, self.type = name, description, domainType, type
        self.codedValues = dict(codedValues or {})
        self.range = range
        self.splitPolicy, self.mergePolicy, self.owner = splitPolicy, mergePolicy, owner

    def __repr__(self):
        return f"<Domain {self.name}>"


def ListDomains(workspace):
    """The attribute domains of a GeoPackage, read where management.CreateDomain keeps them: the schema extension's
    codes and ranges, and grapy_domains for the rest. INFERRED: the field type words (Short, Long, Float, Double,
    Text, Date) and policy words (DefaultValue, Duplicate, GeometryRatio, SumValues, AreaWeighted) are the Domain
    page's, mapped from CreateDomain's keywords."""
    import os
    from .management import _code_value
    file, _ = _gpkg.split(workspace)
    if not os.path.exists(file):
        raise FileNotFoundError(f"'{file}' does not exist.")
    connection = _gpkg.connect(file)
    try:
        try:
            domains = connection.execute("select name, description, field_type, domain_type, split_policy, merge_policy "
                                         "from grapy_domains order by name").fetchall()
        except Exception:  # No domain was ever created here.
            return []
        out = []
        for name, description, field_type, domain_type, split, merge in domains:
            coded = {}
            span = None
            if domain_type.upper() == "CODED":
                for value, text in connection.execute("select value, description from gpkg_data_column_constraints "
                                                      "where constraint_name = ? and constraint_type = 'enum' "
                                                      "order by rowid", (name,)):
                    coded[_code_value(value, field_type)] = text
            else:
                row = connection.execute("select min, max from gpkg_data_column_constraints where constraint_name = ? "
                                         "and constraint_type = 'range'", (name,)).fetchone()
                span = (row[0], row[1]) if row else None
            out.append(Domain(name, description or "", "CodedValue" if domain_type.upper() == "CODED" else "Range",
                              _DOMAIN_FIELD_TYPES.get(field_type, field_type), coded, span,
                              _POLICIES.get((split or "DEFAULT").upper(), split), _POLICIES.get((merge or "DEFAULT").upper(), merge)))
        return out
    finally:
        connection.close()


# ---- edit sessions ------------------------------------------------------------------------------------------------


class Editor:
    """An edit session on a GeoPackage, as one SQLite transaction: startEditing begins it, each edit operation is a
    savepoint (stopOperation keeps it, abortOperation rolls it back), stopEditing commits or discards the session.
    The da cursors opened on that GeoPackage while it is being edited work inside the session.

    With ``version``, the session edits that branch version (ADR-227): the da cursors in it read and write the
    version's state of every registered class. undoOperation and redoOperation are refused — grapy keeps no undo
    stack, and "nonversioned edit sessions do not support undo and redo operations". A folder is refused: an edit
    session edits one
    database. While the session holds its write lock, a tool that opens the GeoPackage itself (to add a field, say)
    waits for it and then fails, as a schema change needs a lock arcpy would not grant either."""

    def __init__(self, workspace, multiuser_mode=True, version=None):
        import os
        path = getattr(workspace, "catalogPath", workspace)
        path = os.fspath(path)
        if ".gpkg" not in path.lower():
            raise NotBuiltError(f"arcpy.da.Editor: '{path}' is not a GeoPackage; only a GeoPackage is edited in grapy.")
        self.workspacePath = _gpkg.split(path)[0]
        self.version = None
        if version not in (None, ""):
            from . import _versions
            connection = _gpkg.connect(self.workspacePath)
            try:
                self.version = _versions.version(connection, version)[0]
            finally:
                connection.close()
        self._connection = None
        self._operation = False

    @property
    def isEditing(self):
        return self._connection is not None

    def startEditing(self, with_undo=True, multiuser_mode=True):
        import sqlite3
        if self._connection is not None:
            raise RuntimeError("arcpy.da.Editor: the edit session has already started.")
        key = _file_key(self.workspacePath)
        if key in _EDITING:
            raise RuntimeError(f"arcpy.da.Editor: '{self.workspacePath}' is already being edited.")
        self._connection = sqlite3.connect(self.workspacePath, isolation_level=None)
        self._connection.execute("pragma foreign_keys = on")
        self._connection.execute("begin")
        _EDITING[key] = self._connection
        if self.version is not None:
            _EDITING_VERSION[key] = self.version

    def startOperation(self):
        if self._connection is None:
            raise RuntimeError("arcpy.da.Editor: an edit operation needs an edit session.")
        if self._operation:
            raise RuntimeError("arcpy.da.Editor: edit operations cannot be nested.")
        self._connection.execute("savepoint grapy_operation")
        self._operation = True

    def stopOperation(self):
        if not self._operation:
            raise RuntimeError("arcpy.da.Editor: no edit operation is in progress.")
        self._connection.execute("release grapy_operation")
        self._operation = False

    def abortOperation(self):
        if not self._operation:
            raise RuntimeError("arcpy.da.Editor: no edit operation is in progress.")
        self._connection.execute("rollback to grapy_operation")
        self._connection.execute("release grapy_operation")
        self._operation = False

    def stopEditing(self, save_changes=True):
        """Commits the session's edits, or with ``save_changes=False`` discards them. INFERRED: stopping while an
        operation is in progress is an error, as the page warns against it."""
        if self._connection is None:
            raise RuntimeError("arcpy.da.Editor: no edit session is in progress.")
        if self._operation:
            raise RuntimeError("arcpy.da.Editor: stop or abort the edit operation before stopping the edit session.")
        try:
            self._connection.execute("commit" if save_changes else "rollback")
        finally:
            _EDITING.pop(_file_key(self.workspacePath), None)
            _EDITING_VERSION.pop(_file_key(self.workspacePath), None)
            self._connection.close()
            self._connection = None

    def undoOperation(self):
        raise NotBuiltError("arcpy.da.Editor.undoOperation is not built in grapy: it keeps no undo stack, and nonversioned "
                            "edit sessions do not support undo, as documented.")

    def redoOperation(self):
        raise NotBuiltError("arcpy.da.Editor.redoOperation is not built in grapy: it keeps no undo stack, and nonversioned "
                            "edit sessions do not support redo, as documented.")

    def __enter__(self):
        self.startEditing()
        self.startOperation()
        return self

    def __exit__(self, kind, value, traceback):
        if kind is None:
            self.stopOperation()
            self.stopEditing(True)
        else:
            self.abortOperation()
            self.stopEditing(False)
        return False


_NO_MODEL = "a GeoPackage holds no replicas, subtypes, contingent values, relationship classes or database sequences " \
            "(ADR-224 group 2)"
_REFUSED = {name: _NO_MODEL for name in (
    "ListReplicas", "Replica", "SyncReplica", "ListSubtypes", "ContingentFieldValue",
    "ContingentValue", "ListContingentValues", "DatabaseSequence", "ListDatabaseSequences",
    "SearchRelatedRecords")}
_REFUSED["TableToArrowTable"] = "pyarrow is not among the wheels a user tool runs with today"
_REFUSED["CreateParquetCache"] = "pyarrow is not among the wheels a user tool runs with today"


# ---- versions (ADR-227) -------------------------------------------------------------------------------------------


class Version:
    """A version, as ListVersions answers it. INFERRED: ``isOwner`` is True — a user tool runs as one user, the owner of
    what it creates — and dates are UTC datetimes."""

    def __init__(self, name, parentVersionName, description, access, created, lastModified, isOwner=True):
        self.name, self.parentVersionName, self.description, self.access = name, parentVersionName, description, access
        self.created, self.lastModified, self.isOwner = created, lastModified, isOwner
        self.ancestors, self.children = [], []

    def __repr__(self):
        return f"<Version {self.name}>"


def _when(text):
    import datetime
    return datetime.datetime.strptime(text, "%Y-%m-%dT%H:%M:%S.%f") if text else None


def ListVersions(sde_workspace=None):
    """The versions of a GeoPackage workspace (ADR-227): DEFAULT and the branch versions made off it. Each version's
    ancestors and children are Version objects too, as documented."""
    import os
    from . import _versions
    from ._runtime import env
    workspace = os.fspath(sde_workspace if sde_workspace not in (None, "") else env.workspace)
    file, _ = _gpkg.split(workspace)
    connection = _gpkg.connect(file)
    try:
        _versions.ensure(connection)
        connection.commit()
        rows = connection.execute(f"select name, parent, description, access, created, modified from "
                                  f"{_versions.VERSIONS} order by branch_id").fetchall()
    finally:
        connection.close()
    made = {r[0]: Version(r[0], r[1], r[2] or "", r[3] or "Public", _when(r[4]), _when(r[5])) for r in rows}
    for version in made.values():
        parent = made.get(version.parentVersionName)
        while parent is not None:
            version.ancestors.append(parent)
            parent = made.get(parent.parentVersionName)
        if version.parentVersionName in made:
            made[version.parentVersionName].children.append(version)
    return list(made.values())


def __getattr__(name):
    if name.startswith("_"):
        raise AttributeError(name)
    why = _REFUSED.get(name)
    raise NotBuiltError(f"arcpy.da.{name} is not built in grapy" + (f" — {why}." if why else " yet."))


# ---- relationship classes (ADR-225) -------------------------------------------------------------------------------


class SearchRelatedRecords:
    """Reads related rows through a relationship class (``<file>.gpkg/<class>``), as documented: for each related
    pair, a tuple of the origin row and the destination row, each holding ``origin_fields`` and ``destination_fields``
    (fields or tokens); ``oids_to_search`` (``"*"`` for all) are origin object ids, or with ``backward`` destination
    ones; an id with no related rows yields nothing. INFERRED: a side whose fields are not asked for is an empty
    tuple; pairs come in order of origin and then destination object id."""

    def __init__(self, relationship_class, oids_to_search="*", origin_fields=None, destination_fields=None, backward=False):
        import os
        from . import _relationships
        if origin_fields is None and destination_fields is None:
            raise ValueError("SearchRelatedRecords needs origin_fields or destination_fields.")
        file, name = _gpkg.split(os.fspath(relationship_class) if isinstance(relationship_class, os.PathLike)
                                 else str(relationship_class))
        connection = _gpkg.connect(file)
        try:
            found = _relationships.classes(connection, name=name)
            if not found:
                raise RuntimeError(f"'{relationship_class}' is not a relationship class.")
            rc = found[0]
            pairs = _relationships.pairs(connection, rc)
        finally:
            connection.close()
        if oids_to_search not in (None, "*", ["*"]):
            wanted = {int(o) for o in (oids_to_search if isinstance(oids_to_search, (list, tuple, set)) else [oids_to_search])}
            pairs = [p for p in pairs if (p[1] if backward else p[0]) in wanted]

        def rows(table, fields):
            if fields is None:
                return None, []
            listed = [fields] if isinstance(fields, str) else list(fields)
            with SearchCursor(f"{file}/{table}", ["OID@", *listed]) as read:
                return {r[0]: tuple(r[1:]) for r in read}, listed

        self._origin, _ = rows(rc["origin"], origin_fields)
        self._destination, _ = rows(rc["destination"], destination_fields)
        self._pairs = pairs
        self._at = 0

    def __iter__(self):
        return self

    def __next__(self):
        if self._at >= len(self._pairs):
            raise StopIteration
        origin, destination = self._pairs[self._at]
        self._at += 1
        return (self._origin[origin] if self._origin is not None else (),
                self._destination[destination] if self._destination is not None else ())

    def next(self):
        return self.__next__()

    def reset(self):
        self._at = 0

    def __enter__(self):
        return self

    def __exit__(self, *_):
        return False


def ListFieldConflictFilters(table):
    """The fields of a versioned class with field conflict filters (management.AddFieldConflictFilter), ADR-227."""
    from . import _versions
    connection, name = _gpkg.table_of(table)
    try:
        return _versions.filters(connection, name)
    finally:
        connection.close()
