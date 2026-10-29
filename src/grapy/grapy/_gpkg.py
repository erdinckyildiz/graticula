"""GeoPackage feature classes, read and written with the standard library's sqlite3 — ADR-223 §5.5.

A feature class is a path: ``<file>.gpkg`` holding one feature table, or ``<file>.gpkg/<table>``. The geometry
blob is the GeoPackage binary — a short header, then WKB — which shapely reads and writes.
"""
from __future__ import annotations

import os
import sqlite3
import struct
from dataclasses import dataclass

import shapely

GEOMETRY = "geom"
OID = "fid"

# Both vocabularies: AddField's words (SHORT, LONG, TEXT …) and a Field's own type words (SmallInteger, Integer, String …),
# which is what Describe hands back and a template carries.
_TYPES = {
    "SHORT": "SMALLINT", "LONG": "INTEGER", "BIGINTEGER": "INTEGER", "FLOAT": "FLOAT", "DOUBLE": "DOUBLE",
    "TEXT": "TEXT", "DATE": "DATETIME", "GUID": "TEXT", "BLOB": "BLOB",
    "SMALLINTEGER": "SMALLINT", "INTEGER": "INTEGER", "SINGLE": "FLOAT", "STRING": "TEXT",
}
_BACK = {"SMALLINT": "SmallInteger", "MEDIUMINT": "Integer", "INTEGER": "Integer", "INT": "Integer",
         "FLOAT": "Single", "REAL": "Double", "DOUBLE": "Double", "TEXT": "String", "DATETIME": "Date",
         "DATE": "Date", "BLOB": "Blob", "BOOLEAN": "SmallInteger"}
_SHAPES = {"POINT": "Point", "MULTIPOINT": "Multipoint", "LINESTRING": "Polyline", "MULTILINESTRING": "Polyline",
           "POLYGON": "Polygon", "MULTIPOLYGON": "Polygon", "GEOMETRY": "Geometry"}
_STORED = {"Point": "POINT", "Multipoint": "MULTIPOINT", "Polyline": "MULTILINESTRING", "Polygon": "MULTIPOLYGON"}


@dataclass
class Column:
    name: str
    type: str   # arcpy's field type word: Integer, Double, String, Date, …
    nullable: bool = True


def split(path: str) -> tuple[str, str | None]:
    """The file and, when named, the table of a feature class path."""
    path = os.fspath(path).replace("\\", "/")
    at = path.lower().rfind(".gpkg")
    if at < 0:
        raise ValueError(f"'{path}' is not a GeoPackage feature class: write <file>.gpkg or <file>.gpkg/<table>.")
    file, rest = path[: at + 5], path[at + 5:].strip("/")
    return file, rest or None


def connect(file: str) -> sqlite3.Connection:
    connection = sqlite3.connect(file)
    connection.execute("pragma foreign_keys = on")
    return connection


def table_of(path: str) -> tuple[sqlite3.Connection, str]:
    """An open connection and the feature table a path names; the only one when it names none. A layer's name answers
    its dataset's table (its query and selection are the cursors' to apply)."""
    from . import _layers
    layer = _layers.find(path)
    if layer is not None:
        path = layer.source
    file, table = split(path)
    if not os.path.exists(file):
        raise FileNotFoundError(f"'{file}' does not exist.")
    connection = connect(file)
    if table is None:
        names = [r[0] for r in connection.execute("select table_name from gpkg_contents where data_type = 'features'")]
        if len(names) != 1:
            connection.close()
            raise ValueError(f"'{file}' holds {len(names)} feature classes; name one as {file}/<table>.")
        table = names[0]
    return connection, table


def describe(connection: sqlite3.Connection, table: str) -> tuple[list[Column], str, int, str]:
    """The columns (the id and shape excluded), the shape type, the reference and the geometry column."""
    row = connection.execute(
        "select column_name, geometry_type_name, srs_id from gpkg_geometry_columns where table_name = ?", (table,)).fetchone()
    geometry, shape, srid = (row if row else (GEOMETRY, "GEOMETRY", 0))
    columns = []
    for _, name, declared, notnull, _, pk in connection.execute(f'pragma table_info("{table}")'):
        if pk or name == geometry:
            continue
        columns.append(Column(name, _BACK.get((declared or "TEXT").split("(")[0].upper(), "String"), not notnull))
    return columns, _SHAPES.get((shape or "GEOMETRY").upper(), "Geometry"), int(srid or 0), geometry


def oid_column(connection: sqlite3.Connection, table: str) -> str:
    for _, name, _, _, _, pk in connection.execute(f'pragma table_info("{table}")'):
        if pk:
            return name
    return "rowid"


def create(path: str, shape: str, srid: int, columns: list[Column], has_z: bool = False, has_m: bool = False) -> None:
    """A new, empty feature class — the file made if it is not there, the table replaced if it is. ``has_z`` and
    ``has_m`` set the geometry column's z and m flags to 1, *mandatory*, as the GeoPackage specification (§2.1.5)
    defines them; a 2D class keeps 0, *prohibited*."""
    file, table = split(path)
    table = table or os.path.splitext(os.path.basename(file))[0]
    os.makedirs(os.path.dirname(os.path.abspath(file)), exist_ok=True)
    connection = connect(file)
    try:
        _ensure_metadata(connection, srid)
        connection.execute(f'drop table if exists "{table}"')
        connection.execute("delete from gpkg_contents where table_name = ?", (table,))
        connection.execute("delete from gpkg_geometry_columns where table_name = ?", (table,))
        defined = "".join(f', "{c.name}" {_TYPES.get(c.type.upper(), "TEXT")}' for c in columns)
        connection.execute(f'create table "{table}" ({OID} integer primary key autoincrement, {GEOMETRY} BLOB{defined})')
        connection.execute(
            "insert into gpkg_contents (table_name, data_type, identifier, srs_id) values (?, 'features', ?, ?)",
            (table, table, srid))
        connection.execute(
            "insert into gpkg_geometry_columns (table_name, column_name, geometry_type_name, srs_id, z, m) values (?, ?, ?, ?, ?, ?)",
            (table, GEOMETRY, _STORED.get(shape, "GEOMETRY"), srid, 1 if has_z else 0, 1 if has_m else 0))
        connection.commit()
    finally:
        connection.close()


def add_column(path: str, column: Column) -> None:
    connection, table = table_of(path)
    try:
        connection.execute(f'alter table "{table}" add column "{column.name}" {_TYPES.get(column.type.upper(), "TEXT")}')
        connection.commit()
    finally:
        connection.close()


def _ensure_metadata(connection: sqlite3.Connection, srid: int) -> None:
    connection.execute("pragma application_id = 1196444487")
    connection.execute("""create table if not exists gpkg_spatial_ref_sys (srs_name text not null, srs_id integer primary key,
        organization text not null, organization_coordsys_id integer not null, definition text not null, description text)""")
    connection.execute("""create table if not exists gpkg_contents (table_name text primary key, data_type text not null,
        identifier text unique, description text default '', last_change datetime default (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
        min_x double, min_y double, max_x double, max_y double, srs_id integer)""")
    connection.execute("""create table if not exists gpkg_geometry_columns (table_name text not null, column_name text not null,
        geometry_type_name text not null, srs_id integer not null, z tinyint not null, m tinyint not null,
        primary key (table_name, column_name))""")
    for srs in {-1, 0, srid}:
        connection.execute(
            "insert or ignore into gpkg_spatial_ref_sys values (?, ?, ?, ?, ?, '')",
            (f"EPSG:{srs}" if srs > 0 else "undefined", srs, "EPSG" if srs > 0 else "NONE", srs, _definition(srs)))


def _definition(srid: int) -> str:
    if srid <= 0:
        return "undefined"
    try:
        from pyproj import CRS
        return CRS.from_epsg(srid).to_wkt()
    except Exception:  # A reference pyproj does not know is still named.
        return "undefined"


def dimensions(connection: sqlite3.Connection, table: str) -> tuple[bool, bool]:
    """Whether a feature table's geometries carry z and m: its gpkg_geometry_columns flags, 1 (mandatory) or 2
    (optional) counting as carrying them."""
    row = connection.execute("select z, m from gpkg_geometry_columns where table_name = ?", (table,)).fetchone()
    return (bool(row[0]), bool(row[1])) if row else (False, False)


def encode(geometry, srid: int, has_z: bool | None = None, has_m: bool | None = None) -> bytes | None:
    """A shapely geometry as a GeoPackage blob: magic, version, flags (little-endian, no envelope), srs, then WKB in
    ISO form, whose type codes carry Z (1000s), M (2000s) and ZM (3000s) as GeoPackage §2.1.3 requires. Given a
    class's ``has_z``/``has_m``, the geometry is first made to have exactly those: a missing z becomes 0 and a missing m
    NaN, and one the class does not carry is dropped."""
    if geometry is None:
        return None
    if has_z is not None or has_m is not None:
        geometry = conform(geometry, bool(has_z), bool(has_m))
    flags = 0b00000001 | (0b00010000 if geometry.is_empty else 0)
    return b"GP" + bytes([0, flags]) + struct.pack("<i", srid) + shapely.to_wkb(geometry, byte_order=1, flavor="iso")


# ---- z and m: shapely carries both (GEOS 3.12+), but its transform and coordinate setters drop m, so geometries are
# rebuilt through WKT, where every dimension survives.


def _number(value) -> str:
    value = float(value)
    return "NaN" if value != value else repr(value)


def rebuild(geometry, has_z: bool, has_m: bool, change=None, change_z=None):
    """The geometry with exactly the dimensions asked — a missing z 0, a missing m NaN — and ``change`` applied to its
    x and y columns (an (n, 2) array in, one out) and ``change_z(z, xy)`` to its z column (given the part's x and y),
    m kept."""
    import numpy as np
    if geometry is None or geometry.is_empty:
        return geometry
    own_z, own_m = bool(shapely.has_z(geometry)), bool(shapely.has_m(geometry))
    tag = ("ZM" if has_m else "Z") if has_z else ("M" if has_m else "")

    def coordinates(part):
        raw = shapely.get_coordinates(part, include_z=own_z, include_m=own_m)
        xy = raw[:, :2]
        if change is not None:
            xy = np.asarray(change(xy.copy()), dtype=np.float64)
        columns = [xy]
        if has_z:
            z = raw[:, 2:3] if own_z else np.zeros((len(raw), 1))
            columns.append(np.asarray(change_z(z, xy), dtype=np.float64).reshape(-1, 1) if change_z is not None else z)
        if has_m:
            columns.append(raw[:, -1:] if own_m else np.full((len(raw), 1), np.nan))
        rows = np.hstack(columns)
        return ", ".join(" ".join(_number(v) for v in row) for row in rows)

    def body(part):
        kind = part.geom_type
        if kind in ("Point", "LineString", "LinearRing"):
            return f"({coordinates(part)})"
        if kind == "Polygon":
            return "(" + ", ".join(f"({coordinates(r)})" for r in [part.exterior, *part.interiors]) + ")"
        return "(" + ", ".join(body(g) for g in part.geoms) + ")"

    kind = geometry.geom_type.upper()
    if kind == "GEOMETRYCOLLECTION":
        inner = ", ".join(shapely.to_wkt(rebuild(g, has_z, has_m, change, change_z)) for g in geometry.geoms)
        return shapely.from_wkt(f"GEOMETRYCOLLECTION {tag} ({inner})".replace("  ", " "))
    return shapely.from_wkt(f"{kind} {tag} {body(geometry)}".replace("  ", " "))


def conform(geometry, has_z: bool, has_m: bool):
    """The geometry with exactly a class's dimensions; untouched when it already has them."""
    if geometry is None or geometry.is_empty:
        return geometry
    if bool(shapely.has_z(geometry)) == has_z and bool(shapely.has_m(geometry)) == has_m:
        return geometry
    return rebuild(geometry, has_z, has_m)


def decode(blob: bytes | None):
    """A GeoPackage blob as a shapely geometry, its envelope skipped."""
    if not blob:
        return None
    flags = blob[3]
    envelope = (0, 32, 48, 48, 64)[(flags >> 1) & 0b111]
    return shapely.from_wkb(bytes(blob[8 + envelope:]))
