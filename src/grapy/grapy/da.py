"""arcpy.da's cursors, by their documented names and tokens — ADR-223 §5.2.

The tokens a field list may hold: ``OID@``, ``SHAPE@`` (a geometry object), ``SHAPE@XY`` (the centroid's x and y),
``SHAPE@X``, ``SHAPE@Y``, ``SHAPE@WKT``, ``SHAPE@WKB``, ``SHAPE@JSON``, ``SHAPE@AREA``, ``SHAPE@LENGTH``, and ``*``
for every field and the shape.
"""
from __future__ import annotations

import shapely
from shapely.geometry import Point as ShapelyPoint

from . import _gpkg
from ._geometry import Geometry, Point, wrap
from ._reference import SpatialReference


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
        return ShapelyPoint(value.X, value.Y)
    if isinstance(value, (tuple, list)) and len(value) == 2 and all(isinstance(v, (int, float)) for v in value):
        return ShapelyPoint(*value)
    if isinstance(value, str):
        return shapely.from_wkt(value)
    if isinstance(value, (bytes, bytearray)):
        return shapely.from_wkb(bytes(value))
    return value


class _Cursor:
    def __init__(self, in_table, field_names):
        self._connection, self._table = _gpkg.table_of(in_table)
        self._described, _, self._srid, self._geometry = _gpkg.describe(self._connection, self._table)
        self._oid = _gpkg.oid_column(self._connection, self._table)
        self._reference = SpatialReference(self._srid) if self._srid > 0 else None
        self.fields = tuple(_columns(field_names, self._described, self._oid))

    def _select(self):
        out = []
        for f in self.fields:
            upper = f.upper()
            out.append(f'"{self._oid}"' if upper == "OID@" else f'"{self._geometry}"' if upper.startswith("SHAPE@") else f'"{f}"')
        return ", ".join(out)

    def _value(self, field, raw):
        return _shape_token(field, _gpkg.decode(raw), self._reference) if field.upper().startswith("SHAPE@") else raw

    def __enter__(self):
        return self

    def __exit__(self, *_):
        self._close()
        return False

    def _close(self):
        if self._connection is not None:
            self._connection.commit()
            self._connection.close()
            self._connection = None

    def __del__(self):
        try:
            self._close()
        except Exception:
            pass


class SearchCursor(_Cursor):
    """Reads rows: ``with SearchCursor(fc, ["name", "SHAPE@"]) as rows: for name, shape in rows: …``"""

    def __init__(self, in_table, field_names, where_clause=None, spatial_reference=None, explode_to_points=False,
                 sql_clause=(None, None), datum_transformation=None):
        super().__init__(in_table, field_names)
        from . import _layers
        where_clause = _layers.combined(_layers.find(in_table), self._oid, where_clause)
        prefix, postfix = sql_clause or (None, None)
        sql = f'select {"distinct " if prefix and "DISTINCT" in prefix.upper() else ""}{self._select()} from "{self._table}"'
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
        self._rows = self._connection.execute(f'select {self._select()} from "{self._table}"')


class InsertCursor(_Cursor):
    """Writes rows: ``with InsertCursor(fc, ["name", "SHAPE@"]) as rows: rows.insertRow(("a", geometry))``"""

    def insertRow(self, row):
        if len(row) != len(self.fields):
            raise RuntimeError(f"insertRow takes {len(self.fields)} values, one per field; {len(row)} were given.")
        names, values = [], []
        for field, value in zip(self.fields, row):
            upper = field.upper()
            if upper == "OID@":
                continue
            if upper.startswith("SHAPE@"):
                names.append(self._geometry)
                values.append(_gpkg.encode(_shape_from(value), self._srid))
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
        from . import _layers
        where_clause = _layers.combined(_layers.find(in_table), self._oid, where_clause)
        sql = f'select "{self._oid}", {self._select()} from "{self._table}"'
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
        names, values = [], []
        for field, value in zip(self.fields, row):
            upper = field.upper()
            if upper == "OID@":
                continue
            if upper.startswith("SHAPE@"):
                names.append(self._geometry)
                values.append(_gpkg.encode(_shape_from(value), self._srid))
            else:
                names.append(field)
                values.append(value)
        assignments = ", ".join(f'"{n}" = ?' for n in names)
        self._connection.execute(f'update "{self._table}" set {assignments} where "{self._oid}" = ?', [*values, oid])

    def deleteRow(self):
        self._connection.execute(f'delete from "{self._table}" where "{self._oid}" = ?', (self._rows[self._at][0],))


def Describe(in_table):
    from . import Describe as describe
    return describe(in_table)
