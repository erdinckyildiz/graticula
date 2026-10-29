"""Relationship classes held in the GeoPackage a tool works on — ADR-225.

A relationship class is a row of ``grapy_relationship_class``: its origin and destination tables, type (SIMPLE or
COMPOSITE), labels, message direction, cardinality, whether it is attributed, and its keys. A many-to-many or
attributed class keeps its pairs in a relationship table named after the class, holding the origin and destination
foreign keys and any attributes (INFERRED: the page names neither the table nor its fields). Rules added by
AddRuleToRelationshipClass are rows of ``grapy_relationship_rule``. Both tables are registered in ``gpkg_extensions``
as the ``grapy_relationships`` extension.

The pairs are also written to the OGC GeoPackage Related Tables Extension (OGC 18-000) — a row of
``gpkgext_relations`` and a mapping table ``<class>_rte`` of (``base_id``, ``related_id``) object ids — so another
reader sees which rows are related. INFERRED: grapy writes the mapping when it makes the class and whenever it deletes
through it; a key edited by any other path is mirrored at grapy's next relationship tool.
"""
from __future__ import annotations

from . import _gpkg
from ._topology import register

EXTENSION = "grapy_relationships"
DEFINITION = "Graticula ADR-225: grapy's relationship classes"
RTE = "http://www.geopackage.org/18-000.html"


def ensure(connection):
    connection.execute("""create table if not exists grapy_relationship_class (name text primary key,
        origin text not null, destination text not null, relationship_type text not null, forward_label text,
        backward_label text, message_direction text not null, cardinality text not null, attributed integer not null,
        origin_primary_key text not null, origin_foreign_key text not null, destination_primary_key text,
        destination_foreign_key text, relationship_table text)""")
    connection.execute("""create table if not exists grapy_relationship_rule (rule_id integer primary key autoincrement,
        relationship_class text not null, origin_subtype text, origin_minimum integer, origin_maximum integer,
        destination_subtype text, destination_minimum integer, destination_maximum integer)""")
    register(connection, ("grapy_relationship_class", "grapy_relationship_rule"), EXTENSION, DEFINITION)


def classes(connection, **where):
    """The relationship classes of a GeoPackage as dictionaries, filtered by column values."""
    exists = connection.execute("select 1 from sqlite_master where name = 'grapy_relationship_class'").fetchone()
    if not exists:
        return []
    cursor = connection.execute("select * from grapy_relationship_class")
    names = [d[0] for d in cursor.description]
    rows = [dict(zip(names, r)) for r in cursor]
    return [r for r in rows if all(r[k] == v for k, v in where.items())]


def _oid(connection, table):
    return _gpkg.oid_column(connection, table)


def pairs(connection, rc):
    """(origin object id, destination object id) of every related pair of a relationship class."""
    origin, destination = rc["origin"], rc["destination"]
    o_oid, d_oid = _oid(connection, origin), _oid(connection, destination)
    if rc["relationship_table"]:
        sql = (f'select o."{o_oid}", d."{d_oid}" from "{rc["relationship_table"]}" as r '
               f'join "{origin}" as o on o."{rc["origin_primary_key"]}" = r."{rc["origin_foreign_key"]}" '
               f'join "{destination}" as d on d."{rc["destination_primary_key"]}" = r."{rc["destination_foreign_key"]}"')
    else:
        sql = (f'select o."{o_oid}", d."{d_oid}" from "{origin}" as o join "{destination}" as d '
               f'on d."{rc["origin_foreign_key"]}" = o."{rc["origin_primary_key"]}"')
    return sorted(set(connection.execute(sql).fetchall()))


def write_related_tables(connection, rc):
    """The class's pairs written to the OGC Related Tables Extension, replacing what was there."""
    mapping = f'{rc["name"]}_rte'
    connection.execute("""create table if not exists gpkgext_relations (id integer primary key autoincrement,
        base_table_name text not null, base_primary_column text not null default 'id', related_table_name text not null,
        related_primary_column text not null default 'id', relation_name text not null,
        mapping_table_name text not null unique)""")
    connection.execute(f'create table if not exists "{mapping}" (base_id integer not null, related_id integer not null)')
    connection.execute(f'delete from "{mapping}"')
    connection.executemany(f'insert into "{mapping}" values (?, ?)', pairs(connection, rc))
    kind = connection.execute("select data_type from gpkg_contents where table_name = ?", (rc["destination"],)).fetchone()
    relation = "features" if kind and kind[0] == "features" and _has_shapes(connection, rc["destination"]) else "attributes"
    connection.execute("delete from gpkgext_relations where mapping_table_name = ?", (mapping,))
    connection.execute("insert into gpkgext_relations (base_table_name, base_primary_column, related_table_name, "
                       "related_primary_column, relation_name, mapping_table_name) values (?, ?, ?, ?, ?, ?)",
                       (rc["origin"], _oid(connection, rc["origin"]), rc["destination"],
                        _oid(connection, rc["destination"]), relation, mapping))
    connection.execute("insert or ignore into gpkg_contents (table_name, data_type, identifier) values (?, 'attributes', ?)",
                       (mapping, mapping))
    register(connection, ("gpkgext_relations", mapping), "related_tables", RTE)


def _has_shapes(connection, table):
    row = connection.execute("select geometry_type_name, srs_id from gpkg_geometry_columns where table_name = ?",
                             (table,)).fetchone()
    return bool(row) and not (row[0].upper() == "GEOMETRY" and int(row[1] or 0) == 0)


def read_related_tables(connection, name):
    """The (base_id, related_id) pairs the Related Tables Extension holds for a class."""
    return sorted(connection.execute(f'select base_id, related_id from "{name}_rte"').fetchall())


def cascade(connection, table, oids):
    """Before rows of ``table`` are deleted: the destination rows of every COMPOSITE class whose origin it is are
    deleted too, as documented ("deleting an origin object … deletes its related destination objects"), down any
    chain of composite classes; and the relationship-table rows naming a deleted row are removed (INFERRED). The Related
    Tables mappings of the classes touched are rewritten."""
    oids = [int(o) for o in oids]
    if not oids or not connection.execute("select 1 from sqlite_master where name = 'grapy_relationship_class'").fetchone():
        return
    marks = ", ".join("?" for _ in oids)
    oid = _oid(connection, table)
    touched = []
    for rc in classes(connection, origin=table) + classes(connection, destination=table):
        side = "origin" if rc["origin"] == table else "destination"
        key = rc[f"{side}_primary_key"]
        values = [r[0] for r in connection.execute(f'select "{key}" from "{table}" where "{oid}" in ({marks})', oids)]
        if not values:
            continue
        value_marks = ", ".join("?" for _ in values)
        if rc["relationship_table"]:
            connection.execute(f'delete from "{rc["relationship_table"]}" where "{rc[f"{side}_foreign_key"]}" in '
                               f'({value_marks})', values)
        if side == "origin" and rc["relationship_type"] == "COMPOSITE" and not rc["relationship_table"]:
            destination = rc["destination"]
            d_oid = _oid(connection, destination)
            doomed = [r[0] for r in connection.execute(
                f'select "{d_oid}" from "{destination}" where "{rc["origin_foreign_key"]}" in ({value_marks})', values)]
            cascade(connection, destination, doomed)
            if doomed:
                connection.execute(f'delete from "{destination}" where "{d_oid}" in ({", ".join("?" for _ in doomed)})',
                                   doomed)
        touched.append(rc)
    for rc in touched:
        if connection.execute("select 1 from sqlite_master where name = ?", (f'{rc["name"]}_rte',)).fetchone():
            mapping = f'{rc["name"]}_rte'
            column = "base_id" if rc["origin"] == table else "related_id"
            connection.execute(f'delete from "{mapping}" where {column} in ({marks})', oids)


# ---- Describe ----------------------------------------------------------------------------------------------------------

class Described:
    """What Describe answers for a relationship class. INFERRED throughout — the *Relationship class properties* page
    answered 404 when checked (2026-10-30) — from arcpy's published property names: ``cardinality`` is ``OneToOne``,
    ``OneToMany`` or ``ManyToMany``; ``notification`` ``None``, ``Forward``, ``Backward`` or ``Both``; the class keys are
    (field, role) pairs, roles ``OriginPrimary``, ``OriginForeign``, ``DestinationPrimary``, ``DestinationForeign``."""

    def __init__(self, path, file, rc, rules):
        self.catalogPath = str(path)
        self.name = self.baseName = rc["name"]
        self.path = file
        self.dataType = self.datasetType = "RelationshipClass"
        self.cardinality = {"ONE_TO_ONE": "OneToOne", "ONE_TO_MANY": "OneToMany", "MANY_TO_MANY": "ManyToMany"}[rc["cardinality"]]
        self.originClassNames = [rc["origin"].rsplit("/", 1)[-1]]
        self.destinationClassNames = [rc["destination"].rsplit("/", 1)[-1]]
        self.forwardPathLabel = rc["forward_label"] or ""
        self.backwardPathLabel = rc["backward_label"] or ""
        self.isComposite = rc["relationship_type"] == "COMPOSITE"
        self.isAttributed = bool(rc["attributed"])
        self.isReflexive = rc["origin"] == rc["destination"]
        self.isAttachmentRelationship = False
        self.keyType = "Single"
        self.classKey = "Undefined"
        self.notification = {"NONE": "None", "FORWARD": "Forward", "BACKWARD": "Backward", "BOTH": "Both"}.get(
            rc["message_direction"], "None")
        self.originClassKeys = [(rc["origin_primary_key"], "OriginPrimary"), (rc["origin_foreign_key"], "OriginForeign")]
        self.destinationClassKeys = [(rc["destination_primary_key"], "DestinationPrimary"),
                                     (rc["destination_foreign_key"], "DestinationForeign")] if rc["relationship_table"] else []
        self.relationshipRules = rules


def describe(path):
    """A Described for a relationship class path, or None when the path names none."""
    import os
    try:
        file, name = _gpkg.split(str(path))
    except ValueError:
        return None
    if not name or not os.path.exists(file):
        return None
    connection = _gpkg.connect(file)
    try:
        found = classes(connection, name=name)
        if not found:
            return None
        rules = [dict(zip(("originMinimumCardinality", "originMaximumCardinality", "destinationMinimumCardinality",
                           "destinationMaximumCardinality"), r)) for r in connection.execute(
            "select origin_minimum, origin_maximum, destination_minimum, destination_maximum from grapy_relationship_rule "
            "where relationship_class = ?", (name,))] if connection.execute(
            "select 1 from sqlite_master where name = 'grapy_relationship_rule'").fetchone() else []
    finally:
        connection.close()
    return Described(path, file, found[0], rules)
