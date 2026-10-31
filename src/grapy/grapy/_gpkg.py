"""GeoPackage feature classes, read and written with the standard library's sqlite3 — ADR-223 §5.5.

A feature class is a path: ``<file>.gpkg`` holding one feature table, or ``<file>.gpkg/<table>``. The geometry
blob is the GeoPackage binary — a short header, then WKB — which shapely reads and writes.
"""
from __future__ import annotations

import datetime
import os
import sqlite3
import struct
from dataclasses import dataclass

import shapely

# Dates are written as the sqlite3 module's own default adapter wrote them (ISO 8601, a space before the time),
# registered explicitly because Python 3.12 deprecated that default: the text grapy stores does not change.
sqlite3.register_adapter(datetime.datetime, lambda value: value.isoformat(" "))
sqlite3.register_adapter(datetime.date, lambda value: value.isoformat())

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
    functions(connection)
    return connection


def functions(connection: sqlite3.Connection) -> None:
    """The SQL functions a GeoPackage's RTree spatial index triggers call (GeoPackage 1.4 Annex F.3, *Extension
    Functions*: ``ST_IsEmpty``, ``ST_MinX``, ``ST_MaxX``, ``ST_MinY``, ``ST_MaxY``), registered so that grapy can write
    to a file whose writer — GDAL, ArcGIS Pro — added the index. Without them every insert or update of a shape fails
    with *no such function: ST_IsEmpty*. grapy adds no index of its own; the extension is optional."""
    def bounds(blob):
        shape = decode(blob) if isinstance(blob, bytes) else None
        return None if shape is None or shape.is_empty else shape.bounds

    def empty(blob):
        if not isinstance(blob, bytes):
            return None
        shape = decode(blob)
        return 1 if shape is None or shape.is_empty else 0

    connection.create_function("ST_IsEmpty", 1, empty, deterministic=True)
    for name, at in (("ST_MinX", 0), ("ST_MinY", 1), ("ST_MaxX", 2), ("ST_MaxY", 3)):
        connection.create_function(name, 1, lambda blob, at=at: (bounds(blob) or (None,) * 4)[at], deterministic=True)


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
        if not names:  # a file holding one table, an attributes table since 2026-10-31
            names = [r[0] for r in connection.execute(
                "select table_name from gpkg_contents where data_type = 'attributes'")]
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
    oid = oid_column(connection, table)
    for _, name, declared, notnull, _, pk in connection.execute(f'pragma table_info("{table}")'):
        if pk or name == geometry or name == oid:
            continue
        columns.append(Column(name, _BACK.get((declared or "TEXT").split("(")[0].upper(), "String"), not notnull))
    kind = _SHAPES.get((shape or "GEOMETRY").upper(), "Geometry")
    if kind == "Polygon":  # a multipatch class is a MultiPolygon Z table its extension row marks (ADR-238)
        from ._multipatch import is_registered
        kind = "MultiPatch" if is_registered(connection, table) else kind
    return columns, kind, int(srid or 0), geometry


def oid_column(connection: sqlite3.Connection, table: str) -> str:
    """A table's integer primary key; a view's first integer column (it has no key, and no rowid to read — ADR-240);
    else ``rowid``."""
    listed = list(connection.execute(f'pragma table_info("{table}")'))
    for _, name, _, _, _, pk in listed:
        if pk:
            return name
    if connection.execute("select 1 from sqlite_master where name = ? and type = 'view'", (table,)).fetchone():
        for _, name, declared, _, _, _ in listed:
            if (declared or "").upper().startswith("INT"):
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
        forget(connection, table)
        stored = _STORED.get(shape, "GEOMETRY")
        defined = "".join(f', "{c.name}" {_TYPES.get(c.type.upper(), "TEXT")}' for c in columns)
        if stored == "GEOMETRY" and not srid and not has_z and not has_m:
            # A table — grapy's "Geometry with no reference", as _contents reads it — is an attributes table
            # (GeoPackage 1.4 §2.4.2): no geometry column and no gpkg_geometry_columns row. Written as a GEOMETRY
            # feature table, ArcGIS Pro opens it as a feature class of unknown type and cannot read its rows.
            connection.execute(f'create table "{table}" ({OID} integer primary key autoincrement not null{defined})')
            connection.execute(
                "insert into gpkg_contents (table_name, data_type, identifier) values (?, 'attributes', ?)", (table, table))
            connection.commit()
            return
        # The geometry column is declared with its geometry type name, not BLOB (GeoPackage 1.4 §2.1.6.1.1 and the
        # sample feature table of Annex C.5): ArcGIS Pro reads a column declared BLOB as a blob field, and the class as
        # a table with no geometry.
        connection.execute(f'create table "{table}" ({OID} integer primary key autoincrement not null, '
                           f'{GEOMETRY} {stored}{defined})')
        connection.execute(
            "insert into gpkg_contents (table_name, data_type, identifier, srs_id) values (?, 'features', ?, ?)",
            (table, table, srid))
        connection.execute(
            "insert into gpkg_geometry_columns (table_name, column_name, geometry_type_name, srs_id, z, m) values (?, ?, ?, ?, ?, ?)",
            (table, GEOMETRY, stored, srid, 1 if has_z else 0, 1 if has_m else 0))
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


APPLICATION_ID = 0x47504B47  # "GPKG" (GeoPackage 1.4 §1.1.1.1.1, Requirement 2)
USER_VERSION = 10400  # GeoPackage 1.4.0, the version these files are written to (same requirement)

# The core tables as GeoPackage 1.4 Annex C (C.1–C.3) defines them, which Requirements 10, 13 and 21 require. Until
# 2026-10-31 grapy wrote its own shorter definitions, without the foreign keys, the unique table name of
# gpkg_geometry_columns and last_change's NOT NULL; a file made then keeps them, as `create table if not exists` leaves
# an existing table alone.
_CORE_TABLES = (
    """create table if not exists gpkg_spatial_ref_sys (srs_name TEXT NOT NULL, srs_id INTEGER PRIMARY KEY,
        organization TEXT NOT NULL, organization_coordsys_id INTEGER NOT NULL, definition TEXT NOT NULL,
        description TEXT)""",
    """create table if not exists gpkg_contents (table_name TEXT NOT NULL PRIMARY KEY, data_type TEXT NOT NULL,
        identifier TEXT UNIQUE, description TEXT DEFAULT '',
        last_change DATETIME NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
        min_x DOUBLE, min_y DOUBLE, max_x DOUBLE, max_y DOUBLE, srs_id INTEGER,
        CONSTRAINT fk_gc_r_srs_id FOREIGN KEY (srs_id) REFERENCES gpkg_spatial_ref_sys(srs_id))""",
    """create table if not exists gpkg_geometry_columns (table_name TEXT NOT NULL, column_name TEXT NOT NULL,
        geometry_type_name TEXT NOT NULL, srs_id INTEGER NOT NULL, z TINYINT NOT NULL, m TINYINT NOT NULL,
        CONSTRAINT pk_geom_cols PRIMARY KEY (table_name, column_name),
        CONSTRAINT uk_gc_table_name UNIQUE (table_name),
        CONSTRAINT fk_gc_tn FOREIGN KEY (table_name) REFERENCES gpkg_contents(table_name),
        CONSTRAINT fk_gc_srs FOREIGN KEY (srs_id) REFERENCES gpkg_spatial_ref_sys (srs_id))""",
)

# Requirement 11 (§1.1.2.1.2, Table 3): every GeoPackage holds these three rows, whatever it uses.
_UNDEFINED = (
    ("Undefined Cartesian SRS", -1, "NONE", -1, "undefined", "undefined Cartesian coordinate reference system"),
    ("Undefined geographic SRS", 0, "NONE", 0, "undefined", "undefined geographic coordinate reference system"),
)

# The WKT 2 keywords a definition begins with when it is not the OGC 01-009 WKT the column holds (§1.1.2.1.1).
_WKT2 = ("GEOGCRS", "GEODCRS", "PROJCRS", "VERTCRS", "COMPOUNDCRS", "ENGCRS", "BOUNDCRS", "DERIVEDPROJCRS", "GEOGRAPHICCRS",
         "PROJECTEDCRS", "GEODETICCRS", "VERTICALCRS", "ENGINEERINGCRS", "TIMECRS", "PARAMETRICCRS")


def _ensure_metadata(connection: sqlite3.Connection, srid: int) -> None:
    """The GeoPackage core: the header's application id and version, the three core tables, and the spatial reference
    rows — the three Requirement 11 asks of every file, and ``srid``'s."""
    if connection.execute("select 1 from sqlite_master where name = 'gpkg_geometry_columns'").fetchone():
        from . import _gpkgupgrade   # D-340: a file an earlier grapy wrote is said once to be upgradable
        _gpkgupgrade.offer(connection.execute("pragma database_list").fetchone()[2], connection)
    connection.execute(f"pragma application_id = {APPLICATION_ID}")
    if connection.execute("pragma user_version").fetchone()[0] == 0:  # a file another writer versioned keeps its own
        connection.execute(f"pragma user_version = {USER_VERSION}")
    for statement in _CORE_TABLES:
        connection.execute(statement)
    columns = "srs_name, srs_id, organization, organization_coordsys_id, definition, description"
    for row in _UNDEFINED:
        connection.execute(f"insert or ignore into gpkg_spatial_ref_sys ({columns}) values (?, ?, ?, ?, ?, ?)", row)
    # A file another writer made with the gpkg_crs_wkt extension (Annex F.10) has a definition_12_063 column, NOT NULL;
    # a row grapy adds there carries WKT 2 in it.
    extended = any(r[1] == "definition_12_063" for r in connection.execute("pragma table_info(gpkg_spatial_ref_sys)"))
    for code in {4326, srid} - {-1, 0}:
        if code > 0 and not connection.execute("select 1 from gpkg_spatial_ref_sys where srs_id = ?", (code,)).fetchone():
            name, definition, description = _reference(code)
            if extended:
                connection.execute(f"insert into gpkg_spatial_ref_sys ({columns}, definition_12_063) "
                                   "values (?, ?, 'EPSG', ?, ?, ?, ?)", (name, code, code, definition, description,
                                                                        _definition(code, wkt2=True)))
            else:
                connection.execute(f"insert into gpkg_spatial_ref_sys ({columns}) values (?, ?, 'EPSG', ?, ?, ?)",
                                   (name, code, code, definition, description))
    # A file grapy wrote before 2026-10-31 holds WKT 2 in `definition`, where the standard asks for OGC 01-009 WKT.
    for code, definition in connection.execute(
            "select srs_id, definition from gpkg_spatial_ref_sys where upper(organization) = 'EPSG'").fetchall():
        if str(definition or "").lstrip().upper().startswith(_WKT2):
            wkt1 = _definition(code)
            if wkt1 != "undefined":
                connection.execute("update gpkg_spatial_ref_sys set definition = ? where srs_id = ?", (wkt1, code))


def _reference(srid: int) -> tuple[str, str, str | None]:
    """(srs_name, definition, description) of an EPSG code."""
    try:
        from pyproj import CRS
        crs = CRS.from_epsg(srid)
        return crs.name, _definition(srid), None
    except Exception:  # A reference pyproj does not know is still named.
        return f"EPSG:{srid}", "undefined", None


def _definition(srid: int, wkt2: bool = False) -> str:
    """A reference's ``definition``: OGC 01-009 WKT (WKT 1), the format GeoPackage §1.1.2.1.1 gives the column ([I32]
    clause 7) — WKT 2 belongs in ``definition_12_063``, the optional gpkg_crs_wkt extension's column (Annex F.10),
    which grapy fills only in a file that already has it (``wkt2``). A reference the format cannot express is
    ``undefined``."""
    if srid <= 0:
        return "undefined"
    try:
        from pyproj import CRS
        return CRS.from_epsg(srid).to_wkt("WKT2_2019" if wkt2 else "WKT1_GDAL") or "undefined"
    except Exception:
        return "undefined"


def forget(connection: sqlite3.Connection, table: str) -> None:
    """A table's rows in gpkg_geometry_columns and gpkg_contents removed, the child row first, as the foreign key from
    the one to the other (Annex C.3, ``fk_gc_tn``) requires."""
    connection.execute("delete from gpkg_geometry_columns where table_name = ?", (table,))
    connection.execute("delete from gpkg_contents where table_name = ?", (table,))


def grow_extent(connection: sqlite3.Connection, table: str, bounds) -> None:
    """The class's gpkg_contents bounding box widened to take in ``bounds`` (minx, miny, maxx, maxy). The standard
    calls the box informative and not necessarily tight (§1.1.3.1.1, note K7); like GDAL, grapy grows it as shapes are
    written and never shrinks it — RecalculateFeatureClassExtent makes it tight."""
    if bounds is None:
        return
    connection.execute(
        "update gpkg_contents set min_x = min(coalesce(min_x, ?1), ?1), min_y = min(coalesce(min_y, ?2), ?2), "
        "max_x = max(coalesce(max_x, ?3), ?3), max_y = max(coalesce(max_y, ?4), ?4) "
        "where table_name = ?5 and data_type = 'features'", (*bounds, table))


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
