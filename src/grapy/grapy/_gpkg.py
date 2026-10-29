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


def create(path: str, shape: str, srid: int, columns: list[Column]) -> None:
    """A new, empty feature class — the file made if it is not there, the table replaced if it is."""
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
            "insert into gpkg_geometry_columns (table_name, column_name, geometry_type_name, srs_id, z, m) values (?, ?, ?, ?, 0, 0)",
            (table, GEOMETRY, _STORED.get(shape, "GEOMETRY"), srid))
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


def encode(geometry, srid: int) -> bytes | None:
    """A shapely geometry as a GeoPackage blob: magic, version, flags (little-endian, no envelope), srs, then WKB."""
    if geometry is None:
        return None
    flags = 0b00000001 | (0b00010000 if geometry.is_empty else 0)
    return b"GP" + bytes([0, flags]) + struct.pack("<i", srid) + shapely.to_wkb(geometry, byte_order=1)


def decode(blob: bytes | None):
    """A GeoPackage blob as a shapely geometry, its envelope skipped."""
    if not blob:
        return None
    flags = blob[3]
    envelope = (0, 32, 48, 48, 64)[(flags >> 1) & 0b111]
    return shapely.from_wkb(bytes(blob[8 + envelope:]))
