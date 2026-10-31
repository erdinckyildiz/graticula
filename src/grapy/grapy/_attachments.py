"""Global IDs and attachments held in the GeoPackage a tool works on — ADR-228.

**Global IDs.** A class given global IDs (management.AddGlobalIDs, or EnableAttachments on a class without them) gains
a text column ``GlobalID`` holding each row's GUID written as ``{XXXXXXXX-XXXX-XXXX-XXXX-XXXXXXXXXXXX}``, upper case in
braces, the form arcpy's cursors answer for a GlobalID; ``grapy_global_id`` names the column, and every grapy insert
path fills it for a new row (``_subtypes.InsertPlan``). INFERRED: the name, the text form and a fresh version-4 UUID per
row; a column already called GlobalID that grapy did not make leaves the new one named ``GlobalID_1``.

**Attachments.** EnableAttachments makes, beside a class ``<name>``, the table ``<name>__ATTACH`` with the fields
ArcGIS's attachment tables carry and GDAL's OpenFileGDB driver reads from them — ``ATTACHMENTID`` (its object id),
``REL_GLOBALID``, ``CONTENT_TYPE``, ``ATT_NAME``, ``DATA_SIZE`` and ``DATA`` — and the relationship class
``<name>__ATTACHREL``: COMPOSITE, one to many, origin key ``GlobalID``, foreign key ``REL_GLOBALID``, kept by
``grapy._relationships`` (ADR-225), so deleting a row deletes its attachments through every grapy delete path. Its pairs
are written to the OGC Related Tables Extension as a ``media`` relation, the requirement class OGC 18-000 defines for
attached files, so another GeoPackage reader sees which rows hold which files. INFERRED (ADR-228 §5.2): the table and
class names, which are ArcGIS's and GDAL's, kept as they are; ``ATTACHMENTID`` rather than the extension's ``id`` as the
media table's primary column, ``gpkgext_relations.related_primary_column`` naming it.
"""
from __future__ import annotations

import mimetypes
import uuid

from . import _relationships
from ._topology import register

EXTENSION = "grapy_attachments"
DEFINITION = "Graticula ADR-228: grapy's global IDs and attachment tables"
UPGRADE_EXIF = ("KEYWORDS", "EXIFINFO")
UPGRADE_ALT = ("ATT_TITLE", "ATT_ALT_TEXT", "ATT_CAPTION", "ATT_CREDIT")


def new_guid():
    return "{" + str(uuid.uuid4()).upper() + "}"


def _has(connection, name):
    return connection.execute("select 1 from sqlite_master where name = ?", (name,)).fetchone() is not None


def global_id_field(connection, table):
    """The GlobalID column of a table, or None when it has no global IDs."""
    if not _has(connection, "grapy_global_id"):
        return None
    row = connection.execute("select field_name from grapy_global_id where table_name = ?", (table,)).fetchone()
    return row[0] if row else None


def add_global_ids(connection, table):
    """The table's GlobalID column, made and filled for every row when it has none."""
    found = global_id_field(connection, table)
    if found:
        return found
    connection.execute("create table if not exists grapy_global_id (table_name text primary key, field_name text not null)")
    register(connection, ("grapy_global_id",), EXTENSION, DEFINITION)
    taken = {r[1].lower() for r in connection.execute(f'pragma table_info("{table}")')}
    name, n = "GlobalID", 0
    while name.lower() in taken:
        n += 1
        name = f"GlobalID_{n}"
    connection.execute(f'alter table "{table}" add column "{name}" TEXT')
    oid = _relationships._oid(connection, table)
    for (each,) in connection.execute(f'select "{oid}" from "{table}"').fetchall():
        connection.execute(f'update "{table}" set "{name}" = ? where "{oid}" = ?', (new_guid(), each))
    connection.execute("insert into grapy_global_id values (?, ?)", (table, name))
    return name


def attach_table(table):
    return f"{table}__ATTACH"


def relationship_class(table):
    return f"{table}__ATTACHREL"


def enabled(connection, table):
    return bool(_relationships.classes(connection, name=relationship_class(table)))


def is_companion(name, names):
    """Whether a GeoPackage table is one grapy keeps for another — an attachment table or its Related Tables mapping —
    so that a file holding one class and its attachments is still that one class."""
    lower = name.lower()
    if lower.endswith("__attach"):
        return name[: -len("__ATTACH")].lower() in {n.lower() for n in names}
    return lower.endswith("__attachrel_rte")


def enable(connection, table):
    """Attachments on a table: its global IDs, the attachment table and the relationship class."""
    key = add_global_ids(connection, table)
    attached = attach_table(table)
    connection.execute(f'''create table "{attached}" (ATTACHMENTID integer primary key autoincrement,
        REL_GLOBALID TEXT not null, CONTENT_TYPE TEXT not null, ATT_NAME TEXT not null, DATA_SIZE INTEGER not null,
        DATA BLOB not null)''')
    connection.execute("insert or ignore into gpkg_contents (table_name, data_type, identifier) values (?, 'attributes', ?)",
                       (attached, attached))
    register(connection, (attached,), EXTENSION, DEFINITION)
    _relationships.ensure(connection)
    connection.execute("insert into grapy_relationship_class (name, origin, destination, relationship_type, forward_label, "
                       "backward_label, message_direction, cardinality, attributed, origin_primary_key, "
                       "origin_foreign_key) values (?, ?, ?, 'COMPOSITE', ?, ?, 'FORWARD', 'ONE_TO_MANY', 0, ?, "
                       "'REL_GLOBALID')", (relationship_class(table), table, attached, attached, table, key))
    refresh(connection, table)


def refresh(connection, table):
    """The attachment class's pairs rewritten to the Related Tables Extension, as a media relation."""
    found = _relationships.classes(connection, name=relationship_class(table))
    if found:
        _relationships.write_related_tables(connection, found[0], "media")


def disable(connection, table):
    """The attachment table, its relationship class and its Related Tables mapping, gone — and every attachment."""
    name = relationship_class(table)
    attached = attach_table(table)
    for extra in (attached, f"{name}_rte"):
        connection.execute(f'drop table if exists "{extra}"')
        connection.execute("delete from gpkg_contents where table_name = ?", (extra,))
        connection.execute("delete from gpkg_extensions where table_name = ?", (extra,))
    if _has(connection, "gpkgext_relations"):
        connection.execute("delete from gpkgext_relations where mapping_table_name = ?", (f"{name}_rte",))
    if _has(connection, "grapy_relationship_class"):
        connection.execute("delete from grapy_relationship_class where name = ?", (name,))


def drop(connection, table):
    """A deleted table's attachments and global IDs."""
    if enabled(connection, table):
        disable(connection, table)
    if _has(connection, "grapy_global_id"):
        connection.execute("delete from grapy_global_id where table_name = ?", (table,))


def rename(connection, old, new):
    """A renamed table's attachment table, relationship class and global IDs, renamed with it."""
    if _has(connection, "grapy_global_id"):
        connection.execute("update grapy_global_id set table_name = ? where table_name = ?", (new, old))
    if not enabled(connection, old):
        return
    disable_mapping = f"{relationship_class(old)}_rte"
    connection.execute(f'drop table if exists "{disable_mapping}"')
    connection.execute("delete from gpkg_contents where table_name = ?", (disable_mapping,))
    connection.execute("delete from gpkg_extensions where table_name = ?", (disable_mapping,))
    connection.execute("delete from gpkgext_relations where mapping_table_name = ?", (disable_mapping,))
    connection.execute(f'alter table "{attach_table(old)}" rename to "{attach_table(new)}"')
    for meta in ("gpkg_contents", "gpkg_extensions"):
        connection.execute(f"update {meta} set table_name = ? where table_name = ?", (attach_table(new), attach_table(old)))
    connection.execute("update gpkg_contents set identifier = ? where table_name = ?", (attach_table(new), attach_table(new)))
    connection.execute("update grapy_relationship_class set name = ?, origin = ?, destination = ? where name = ?",
                       (relationship_class(new), new, attach_table(new), relationship_class(old)))
    refresh(connection, new)


def content_type(name):
    """A file's MIME type from its extension (Python's mimetypes); INFERRED: ``application/octet-stream`` otherwise."""
    return mimetypes.guess_type(str(name))[0] or "application/octet-stream"


def add(connection, table, global_id, name, data):
    added = connection.execute(f'insert into "{attach_table(table)}" (REL_GLOBALID, CONTENT_TYPE, ATT_NAME, DATA_SIZE, '
                               f'DATA) values (?, ?, ?, ?, ?)', (global_id, content_type(name), name, len(data), data))
    from . import _behaviours
    _behaviours.stamp_row(connection, attach_table(table), "ATTACHMENTID", added.lastrowid, True)  # ADR-240
