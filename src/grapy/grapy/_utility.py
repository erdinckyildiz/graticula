"""Utility networks held in the GeoPackage a tool works on — ADR-231.

A utility network lives in a feature dataset (management.CreateFeatureDataset), as ArcGIS's does. Its classes are
ordinary grapy feature classes and tables in that dataset, named as ArcGIS names them — ``StructureJunction``,
``StructureLine``, ``StructureBoundary``, ``StructureJunctionObject``, ``StructureEdgeObject`` and, for each traditional
domain network, ``<Domain>Device``, ``<Domain>Line``, ``<Domain>Junction``, ``<Domain>Assembly``,
``<Domain>SubnetLine``, ``<Domain>JunctionObject``, ``<Domain>EdgeObject`` — with the system fields the public pages
list. ``ASSETGROUP`` is each class's subtype field (ADR-228), and an asset type is a code of the domain assigned to
``ASSETTYPE`` for that subtype. Two more classes are the ones a user reads: ``<network>_Associations`` (the
associations table's documented fields) and ``<network>_DirtyAreas``.

The definition is grapy's own tables, registered in ``gpkg_extensions`` as ``grapy_utility_network`` (INFERRED, ADR-231
§5.9 (a)): the network, its sources, domain networks, tier groups, tiers, network attributes and their fields, network
categories and their asset types, terminal configurations and their asset types, rules and subnetwork controllers; and,
since ADR-231's second batch, per-asset-type settings (edge connectivity, association role), subnetwork definitions, the
Subnetworks table and named trace configurations.

**The network topology is a record, not a live view** (ADR-231 §5.4). EnableNetworkTopology and ValidateNetworkTopology
copy each feature's shape, asset group and type, terminal fields and network attribute fields into
``grapy_un_topology_feature``, and each association into ``grapy_un_topology_association``; a trace reads that record,
never the features as they are now, as the pages say a trace reads the topology "the last time it was enabled or
validated". A dirty area is found by comparing the features with the record when a utility network tool runs
(INFERRED: grapy has no edit session to watch), and is written to ``<network>_DirtyAreas``. Esri's binary network
index is not reproduced (CLAUDE.md §5).

Flow direction for upstream and downstream traces is an *equivalent* (ADR-224 §5.6): the pages say flow is found
"from the subnetwork controller" but not how; grapy lets the resource pass from u to v when some path from a
controller reaches u without passing v, read off the dominator tree (Cooper, Harvey and Kennedy 2001).
"""
from __future__ import annotations

import json
import os
import shlex
from collections import defaultdict, deque

import shapely
from shapely.geometry import LineString, Point as ShapelyPoint, box
from shapely.ops import substring, unary_union

from . import _gpkg
from ._runtime import ExecuteError, NotBuiltError
from ._topology import register

EXTENSION = "grapy_utility_network"
DEFINITION = "Graticula ADR-231: grapy's utility networks"
TABLES = ("grapy_un", "grapy_un_source", "grapy_un_domain", "grapy_un_tier_group", "grapy_un_tier", "grapy_un_attribute",
          "grapy_un_attribute_field", "grapy_un_category", "grapy_un_category_asset", "grapy_un_terminal_config",
          "grapy_un_terminal_asset", "grapy_un_rule", "grapy_un_controller", "grapy_un_topology_feature",
          "grapy_un_topology_association", "grapy_un_error", "grapy_un_asset_setting", "grapy_un_subnetwork_definition",
          "grapy_un_subnetwork", "grapy_un_trace_config")
FLOW = ("flow direction away from the subnetwork controllers, read off the dominator tree: the resource passes from u to "
        "v when some path from a controller reaches u without passing v",
        "Cooper, Harvey and Kennedy 2001, A Simple, Fast Dominance Algorithm")

# INFERRED (ADR-231 §5.9 (c)): two coordinates within this distance are one location, ArcGIS's default x,y tolerance of
# a millimetre in the data's unit; a GeoPackage keeps no tolerance of its own.
TOLERANCE = 0.001

STRUCTURE = "Structure"
STRUCTURE_CLASSES = (("StructureJunction", "Point", "StructureJunction"), ("StructureLine", "Polyline", "StructureLine"),
                     ("StructureBoundary", "Polygon", "StructureBoundary"),
                     ("StructureJunctionObject", None, "JunctionObject"), ("StructureEdgeObject", None, "EdgeObject"))
DOMAIN_CLASSES = (("Device", "Point"), ("Line", "Polyline"), ("Junction", "Point"), ("Assembly", "Point"),
                  ("SubnetLine", "Polyline"), ("JunctionObject", None), ("EdgeObject", None))
JUNCTION_ROLES = ("Device", "Junction", "StructureJunction")
LINE_ROLES = ("Line", "StructureLine")
OBJECT_ROLES = ("JunctionObject", "EdgeObject")

# The associations table's ASSOCIATIONTYPE codes, the CSV's words and the tools' keywords, as the pages give them.
ASSOCIATION_TYPES = {1: ("Junction Junction Connectivity", "JUNCTION_JUNCTION_CONNECTIVITY"),
                     2: ("Containment", "CONTAINMENT"), 3: ("Structural Attachment", "STRUCTURAL_ATTACHMENT"),
                     4: ("Junction Edge From Connectivity", "JUNCTION_EDGE_FROM_CONNECTIVITY"),
                     5: ("Junction Edge Midspan Connectivity", "JUNCTION_EDGE_MIDSPAN_CONNECTIVITY"),
                     6: ("Junction Edge To Connectivity", "JUNCTION_EDGE_TO_CONNECTIVITY")}
RULE_TYPES = {"JUNCTION_JUNCTION_CONNECTIVITY": "Junction Junction Connectivity",
              "JUNCTION_EDGE_CONNECTIVITY": "Junction Edge Connectivity",
              "EDGE_JUNCTION_EDGE_CONNECTIVITY": "Edge Junction Edge Connectivity",
              "CONTAINMENT": "Containment", "STRUCTURAL_ATTACHMENT": "Structural Attachment"}
# Association status bits, from the Association status attribute page's domain.
CONTAINER, STRUCTURE_BIT, CONTENT, ATTACHMENT, VISIBLE_CONTENT, CONNECTIVITY = 1, 2, 4, 8, 16, 32
# Dirty area status bits, from the Dirty areas page.
MODIFIED, DELETED, ASSOCIATED, FEATURE_ERROR, OBJECT_ERROR = 1, 2, 4, 8, 16
SYSTEM_CATEGORIES = ("Subnetwork Controller", "Subnetwork Tap", "Attribute Substitution", "Propagator Resetter")
# System network attributes the Network attributes page lists that grapy computes (the telecom ones and Flow direction,
# Position from/to and Source ID are not; ADR-231 §5.5).
SYSTEM_ATTRIBUTES = ("Asset group", "Asset type", "Is subnetwork controller", "Shape length", "Terminal ID", "Tier name")


def ensure(connection):
    connection.execute("""create table if not exists grapy_un (name text primary key, dataset text not null,
        territory text not null, version text not null, enabled integer not null default 0,
        ever_enabled integer not null default 0)""")
    connection.execute("""create table if not exists grapy_un_source (network text not null, source_id integer not null,
        class_name text not null, domain text not null, role text not null, primary key (network, source_id))""")
    connection.execute("""create table if not exists grapy_un_domain (network text not null, name text not null,
        alias text, kind text not null, tier_definition text, controller_type text, primary key (network, name))""")
    connection.execute("""create table if not exists grapy_un_tier_group (network text not null, domain text not null,
        name text not null, primary key (network, domain, name))""")
    connection.execute("""create table if not exists grapy_un_tier (network text not null, domain text not null,
        name text not null, rank integer not null, topology_type text not null, tier_group text,
        subnetwork_field text not null, primary key (network, name))""")
    connection.execute("""create table if not exists grapy_un_attribute (network text not null, name text not null,
        type text not null, inline integer not null, apportionable integer not null, domain_name text,
        nullable integer not null, substitution integer not null, substitute text, system integer not null default 0,
        primary key (network, name))""")
    connection.execute("""create table if not exists grapy_un_attribute_field (network text not null,
        attribute text not null, class_name text not null, field text not null, primary key (network, attribute,
        class_name))""")
    connection.execute("""create table if not exists grapy_un_category (network text not null, name text not null,
        system integer not null default 0, primary key (network, name))""")
    connection.execute("""create table if not exists grapy_un_category_asset (network text not null,
        category text not null, class_name text not null, asset_group integer not null, asset_type integer not null)""")
    connection.execute("""create table if not exists grapy_un_terminal_config (network text not null, id integer not null,
        name text not null, model text not null, terminals text not null, paths text not null, default_path text not null,
        primary key (network, id))""")
    connection.execute("""create table if not exists grapy_un_terminal_asset (network text not null,
        class_name text not null, asset_group integer not null, asset_type integer not null, config_id integer not null,
        primary key (network, class_name, asset_group, asset_type))""")
    connection.execute("""create table if not exists grapy_un_rule (network text not null, id integer not null,
        rule_type text not null, from_class text not null, from_group integer not null, from_type integer not null,
        from_terminal integer, to_class text not null, to_group integer not null, to_type integer not null,
        to_terminal integer, via_class text, via_group integer, via_type integer, via_terminal integer,
        primary key (network, id))""")
    connection.execute("""create table if not exists grapy_un_controller (network text not null, class_name text not null,
        globalid text not null, terminal_id integer, controller_name text not null, tier text not null,
        subnetwork_name text not null, description text, notes text)""")
    connection.execute("""create table if not exists grapy_un_topology_feature (network text not null,
        source_id integer not null, oid integer not null, globalid text, signature text not null, data text not null,
        primary key (network, source_id, oid))""")
    connection.execute("""create table if not exists grapy_un_topology_association (network text not null,
        globalid text not null, signature text not null, data text not null, primary key (network, globalid))""")
    connection.execute("""create table if not exists grapy_un_error (network text not null, source_id integer not null,
        oid integer not null, globalid text, code integer not null, message text not null, envelope text)""")
    # ADR-231's second batch: per-asset-type settings (edge connectivity, association role), subnetwork definitions,
    # the subnetworks UpdateSubnetwork keeps, and named trace configurations.
    connection.execute("""create table if not exists grapy_un_asset_setting (network text not null,
        class_name text not null, asset_group integer not null, asset_type integer not null, setting text not null,
        value text not null, primary key (network, class_name, asset_group, asset_type, setting))""")
    connection.execute("""create table if not exists grapy_un_subnetwork_definition (network text not null,
        domain text not null, tier text not null, definition text not null, primary key (network, tier))""")
    connection.execute("""create table if not exists grapy_un_subnetwork (network text not null, domain text not null,
        tier text not null, name text not null, controllers text, isdirty integer not null, isdeleted integer not null,
        last_update text, last_ack_export text, features text, primary key (network, tier, name))""")
    connection.execute("""create table if not exists grapy_un_trace_config (network text not null, name text not null,
        creator text, description text, trace_type text not null, configuration text not null, result_types text,
        tags text, primary key (network, name))""")
    register(connection, TABLES, EXTENSION, DEFINITION)


# ---- locating ------------------------------------------------------------------------------------------------------

def locate(path):
    """(file, dataset, name) of a utility network path ``<file>.gpkg/<dataset>/<network>``."""
    path = os.fspath(path) if isinstance(path, os.PathLike) else str(path)
    file, table = _gpkg.split(path)
    if not table or "/" not in table:
        raise ExecuteError(f"ERROR 000732: '{path}' is no utility network: write <file>.gpkg/<feature dataset>/<network>.")
    dataset, name = table.rsplit("/", 1)
    return file, dataset, name


class Network:
    """An open utility network: its connection and names. Closed by ``close``; used as a context manager."""

    def __init__(self, path):
        self.path = str(path)
        self.file, self.dataset, self.name = locate(path)
        if not os.path.exists(self.file):
            raise ExecuteError(f"ERROR 000732: '{self.file}' does not exist.")
        self.connection = _gpkg.connect(self.file)
        ensure(self.connection)
        row = self.connection.execute("select territory, version, enabled, ever_enabled from grapy_un where name = ? "
                                      "and dataset = ?", (self.name, self.dataset)).fetchone()
        if row is None:
            self.connection.close()
            raise ExecuteError(f"ERROR 000732: there is no utility network '{self.name}' in '{self.dataset}'.")
        self.territory, self.version, self.enabled, self.ever_enabled = row[0], row[1], bool(row[2]), bool(row[3])

    def __enter__(self):
        return self

    def __exit__(self, kind, *_):
        if kind is None:
            self.connection.commit()
        self.connection.close()

    def q(self, sql, *args):
        return self.connection.execute(sql, args)

    # -- names --

    def table(self, name):
        """A class's table from its name as a tool gives it: ``WaterDevice``, ``<dataset>/WaterDevice`` or a path."""
        text = str(name).replace("\\", "/")
        if ".gpkg" in text.lower():
            text = _gpkg.split(text)[1] or ""
        short = text.rsplit("/", 1)[-1]
        for (class_name,) in self.q("select class_name from grapy_un_source where network = ?", self.name):
            if class_name.rsplit("/", 1)[-1].lower() == short.lower():
                return class_name
        raise ExecuteError(f"ERROR 000732: '{name}' is not a class of the utility network '{self.name}'.")

    def sources(self):
        """{source_id: (class_name, domain, role)}."""
        return {s: (c, d, r) for s, c, d, r in self.q("select source_id, class_name, domain, role from grapy_un_source "
                                                       "where network = ? order by source_id", self.name)}

    def source_of(self, class_name):
        row = self.q("select source_id from grapy_un_source where network = ? and class_name = ?", self.name,
                     class_name).fetchone()
        return int(row[0])

    def role(self, class_name):
        return self.q("select role from grapy_un_source where network = ? and class_name = ?", self.name,
                      class_name).fetchone()[0]

    def domain_of(self, class_name):
        return self.q("select domain from grapy_un_source where network = ? and class_name = ?", self.name,
                      class_name).fetchone()[0]

    def domain(self, name):
        row = self.q("select name, alias, kind, tier_definition, controller_type from grapy_un_domain where network = ? "
                     "and lower(name) = lower(?)", self.name, str(name)).fetchone()
        if row is None:
            raise ExecuteError(f"ERROR 000732: '{name}' is not a domain network of '{self.name}'.")
        return row

    def tier(self, name):
        row = self.q("select domain, name, rank, topology_type, tier_group, subnetwork_field from grapy_un_tier where "
                     "network = ? and lower(name) = lower(?)", self.name, str(name)).fetchone()
        if row is None:
            raise ExecuteError(f"ERROR 000732: '{name}' is not a tier of '{self.name}'.")
        return row

    def require_disabled(self, tool):
        if self.enabled:
            raise ExecuteError(f"ERROR 001934: {tool}: the network topology must be disabled (DisableNetworkTopology).")

    def require_enabled(self, tool):
        if not self.enabled:
            raise ExecuteError(f"ERROR 001934: {tool}: the network topology must be enabled (EnableNetworkTopology).")

    # -- asset groups and types --

    def asset_group(self, class_name, value):
        """The asset group code a name or code gives, of a class's subtypes."""
        from . import _subtypes
        groups = _subtypes.codes(self.connection, class_name)
        return _code_of(value, groups, f"asset group of {class_name.rsplit('/', 1)[-1]}")

    def asset_types(self, class_name, group):
        """{code: name} of an asset group's asset types: the coded values of the domain on ASSETTYPE for that subtype,
        else of the field's own domain."""
        from . import _subtypes
        domain = _subtypes.domains(self.connection, class_name).get(("assettype", int(group)))
        if domain is None:
            row = self.q("select constraint_name from gpkg_data_columns where table_name = ? and "
                         "lower(column_name) = 'assettype'", class_name).fetchone() if _has_table(
                self.connection, "gpkg_data_columns") else None
            domain = row[0] if row else None
        if domain is None:
            return {0: "Unknown"}
        return {int(float(v)): d for v, d in self.q("select value, description from gpkg_data_column_constraints where "
                                                    "constraint_name = ? and constraint_type = 'enum'", domain)}

    def asset_type(self, class_name, group, value):
        return _code_of(value, self.asset_types(class_name, group),
                        f"asset type of {class_name.rsplit('/', 1)[-1]} asset group {group}")

    def asset_names(self, class_name, group, kind):
        from . import _subtypes
        groups = _subtypes.codes(self.connection, class_name)
        return groups.get(int(group), str(group)), self.asset_types(class_name, group).get(int(kind), str(kind))

    # -- terminals --

    def terminal_config(self, class_name, group, kind):
        """(id, name, model, [(terminal id, name, upstream)], {path name: [(a, b)] | 'ALL' | 'NONE'}, default) of an
        asset type, or None for an asset type with no terminal configuration (a single terminal)."""
        row = self.q("select c.id, c.name, c.model, c.terminals, c.paths, c.default_path from grapy_un_terminal_asset a "
                     "join grapy_un_terminal_config c on c.network = a.network and c.id = a.config_id where a.network = ? "
                     "and a.class_name = ? and a.asset_group = ? and a.asset_type = ?", self.name, class_name, int(group),
                     int(kind)).fetchone()
        if row is None:
            return None
        return row[0], row[1], row[2], [tuple(t) for t in json.loads(row[3])], json.loads(row[4]), row[5]

    def terminal_id(self, class_name, group, kind, value):
        """A terminal's id from its name or id; None for no terminal (an asset type with none, or '*' / empty)."""
        if value is None or str(value).strip() in ("", "#", "*") or str(value).strip().lower() == "single terminal":
            return None
        config = self.terminal_config(class_name, group, kind)
        if config is None:
            return None
        for terminal_id, name, _ in config[3]:
            if str(value).strip().lower() == name.lower() or str(value).strip() == str(terminal_id):
                return terminal_id
        raise ExecuteError(f"ERROR 000732: '{value}' is not a terminal of the configuration '{config[1]}'.")

    def terminal_name(self, class_name, group, kind, terminal_id):
        if terminal_id is None:
            return "Single Terminal"
        config = self.terminal_config(class_name, group, kind)
        return next((n for t, n, _ in config[3] if t == terminal_id), str(terminal_id)) if config else "Single Terminal"

    # -- features --

    def oid(self, class_name):
        return _gpkg.oid_column(self.connection, class_name)

    def feature_by_globalid(self, class_name, globalid):
        row = self.q(f'select "{self.oid(class_name)}", ASSETGROUP, ASSETTYPE from "{class_name}" where '
                     'upper(GLOBALID) = upper(?)', str(globalid)).fetchone()
        if row is None:
            raise ExecuteError(f"ERROR 000732: there is no feature {globalid} in {class_name.rsplit('/', 1)[-1]}.")
        return int(row[0]), int(row[1] or 0), int(row[2] or 0)

    def setting(self, class_name, group, kind, name, default=None):
        """A per-asset-type setting (SetEdgeConnectivity, SetAssociationRole), or ``default``."""
        row = self.q("select value from grapy_un_asset_setting where network = ? and class_name = ? and asset_group = ? "
                     "and asset_type = ? and setting = ?", self.name, class_name, int(group), int(kind), name).fetchone()
        return json.loads(row[0]) if row else default

    def set_setting(self, class_name, group, kind, name, value):
        self.q("insert or replace into grapy_un_asset_setting values (?, ?, ?, ?, ?, ?)", self.name, class_name,
               int(group), int(kind), name, json.dumps(value))

    def network_fields(self, class_name):
        """{network attribute: field} of a class."""
        return {a: f for a, f in self.q("select attribute, field from grapy_un_attribute_field where network = ? and "
                                        "class_name = ?", self.name, class_name)}


def _has_table(connection, name):
    return connection.execute("select 1 from sqlite_master where name = ?", (name,)).fetchone() is not None


def _code_of(value, names, what):
    if value is None or str(value).strip() in ("", "#"):
        raise ExecuteError(f"ERROR 000735: an {what} is required.")
    for code, name in names.items():
        if str(value).strip().lower() == str(name).lower():
            return code
    try:
        code = int(float(str(value)))
    except ValueError:
        raise ExecuteError(f"ERROR 000732: '{value}' is not an {what}.") from None
    if code not in names:
        raise ExecuteError(f"ERROR 000732: '{value}' is not an {what}.")
    return code


# ---- creating -------------------------------------------------------------------------------------------------------

def _create_class(file, dataset, name, shape, srid, columns, z, m):
    path = f"{file}/{dataset}/{name}"
    if shape is None:
        _gpkg.create(path, "Geometry", 0, columns)
    else:
        _gpkg.create(path, shape, srid, columns, z, m)
    return f"{dataset}/{name}"


def _prepare_class(connection, table, alias_group=True):
    """ASSETGROUP as the subtype field with its 0 Unknown subtype, and global IDs (ADR-228)."""
    from . import _attachments, _subtypes
    _subtypes.ensure(connection)
    if alias_group:
        connection.execute("insert into grapy_subtype_field values (?, 'ASSETGROUP', 0)", (table,))
        connection.execute("insert into grapy_subtype values (?, 0, 'Unknown')", (table,))
    _attachments.add_global_ids(connection, table)


def _base_columns(role):
    columns = [_gpkg.Column("ASSETGROUP", "Integer"), _gpkg.Column("ASSETTYPE", "SmallInteger"),
               _gpkg.Column("ASSOCIATIONSTATUS", "SmallInteger")]
    if role in ("Device", "JunctionObject"):
        columns += [_gpkg.Column(n, t) for n, t in (("ISSUBNETWORKCONTROLLER", "SmallInteger"),
                                                    ("SUBNETWORKCONTROLLERNAME", "String"), ("TIERNAME", "String"),
                                                    ("TIERRANK", "Integer"), ("TERMINALCONFIGURATION", "String"))]
    if role in ("Line",):
        columns += [_gpkg.Column("FROMDEVICETERMINAL", "Integer"), _gpkg.Column("TODEVICETERMINAL", "Integer")]
    if role in ("Device", "Line", "Junction", "JunctionObject", "EdgeObject"):
        columns += [_gpkg.Column("ISCONNECTED", "SmallInteger")]
    return columns


def create(file, dataset, name, territory, srid, version):
    """CreateUtilityNetwork: the network's rows, the structure network's classes, its associations and dirty areas."""
    connection = _gpkg.connect(file)
    try:
        ensure(connection)
        if connection.execute("select 1 from grapy_un where name = ?", (name,)).fetchone():
            raise ExecuteError(f"ERROR 000258: a utility network named '{name}' already exists.")
        connection.execute("insert into grapy_un values (?, ?, ?, ?, 0, 0)", (name, dataset, territory, version))
        connection.execute("insert into grapy_un_domain values (?, ?, ?, 'Structure', null, null)",
                           (name, STRUCTURE, "Structure Network"))
        connection.commit()
    finally:
        connection.close()
    source = 1
    for class_name, shape, role in STRUCTURE_CLASSES:
        columns = _base_columns(role) + [_gpkg.Column("SUBNETWORKNAME", "String"),
                                         _gpkg.Column("SUPPORTEDSUBNETWORKNAME", "String")]
        table = _create_class(file, dataset, class_name, shape, srid, columns, True, True)
        _register_source(file, name, source, table, STRUCTURE, role)
        source += 1
    for suffix, shape, columns in (
            ("Associations", None, [_gpkg.Column(n, t) for n, t in (
                ("FROMNETWORKSOURCEID", "Integer"), ("FROMGLOBALID", "String"), ("FROMTERMINALID", "Integer"),
                ("TONETWORKSOURCEID", "Integer"), ("TOGLOBALID", "String"), ("TOTERMINALID", "Integer"),
                ("ASSOCIATIONTYPE", "SmallInteger"), ("ISCONTENTVISIBLE", "SmallInteger"), ("PERCENTALONG", "Double"),
                ("STATUS", "Integer"), ("ERRORCODE", "Integer"), ("ERRORMESSAGE", "String"))]),
            ("DirtyAreas", "Polygon", [_gpkg.Column(n, t) for n, t in (
                ("STATUS", "Integer"), ("ERRORCODE", "Integer"), ("ERRORMESSAGE", "String"),
                ("NETWORKSOURCEID", "Integer"), ("FEATUREGLOBALID", "String"))])):
        table = _create_class(file, dataset, f"{name}_{suffix}", shape, srid, columns, False, False)
        connection = _gpkg.connect(file)
        try:
            from . import _attachments
            _attachments.add_global_ids(connection, table)
            connection.commit()
        finally:
            connection.close()
    with Network(f"{file}/{dataset}/{name}") as network:
        for category in SYSTEM_CATEGORIES:
            network.q("insert into grapy_un_category values (?, ?, 1)", name, category)
        for attribute, kind in zip(SYSTEM_ATTRIBUTES, ("SHORT", "SHORT", "SHORT", "DOUBLE", "SHORT", "LONG")):
            network.q("insert into grapy_un_attribute values (?, ?, ?, 0, ?, null, 1, 0, null, 1)", name, attribute, kind,
                      1 if attribute == "Shape length" else 0)
        write_dirty_areas(network)


def _register_source(file, network, source_id, table, domain, role):
    connection = _gpkg.connect(file)
    try:
        connection.execute("insert into grapy_un_source values (?, ?, ?, ?, ?)", (network, source_id, table, domain, role))
        _prepare_class(connection, table, role != "SubnetLine")
        connection.commit()
    finally:
        connection.close()


def add_domain(path, name, tier_definition, controller_type, alias):
    with Network(path) as network:
        network.require_disabled("AddDomainNetwork")
        if network.q("select 1 from grapy_un_domain where network = ? and lower(name) = lower(?)", network.name,
                     name).fetchone():
            raise ExecuteError(f"ERROR 000258: the domain network '{name}' already exists.")
        network.q("insert into grapy_un_domain values (?, ?, ?, 'Traditional', ?, ?)", network.name, name, alias,
                  tier_definition, controller_type)
        start = network.q("select coalesce(max(source_id), 0) + 1 from grapy_un_source where network = ?",
                          network.name).fetchone()[0]
        srid = network.q("select srs_id from grapy_feature_datasets where name = ?", network.dataset).fetchone()[0]
        file, dataset, un = network.file, network.dataset, network.name
    for i, (suffix, shape) in enumerate(DOMAIN_CLASSES):
        if suffix == "SubnetLine":
            columns = [_gpkg.Column(n, t) for n, t in (("DOMAINNETWORKNAME", "String"), ("TIERNAME", "String"),
                                                       ("SUBNETWORKNAME", "String"),
                                                       ("SUBNETWORKCONTROLLERNAMES", "String"), ("ISDIRTY", "SmallInteger"),
                                                       ("LASTUPDATESUBNETWORK", "Date"),
                                                       ("LASTACKEXPORTSUBNETWORK", "Date"))]
        else:
            columns = _base_columns(suffix)
            if suffix != "Assembly":
                columns += [_gpkg.Column("SUPPORTINGSUBNETWORKNAME", "String")]
            columns += [_gpkg.Column("SUPPORTEDSUBNETWORKNAME", "String")]
        table = _create_class(file, dataset, f"{name}{suffix}", shape, srid, columns, True, True)
        _register_source(file, un, start + i, table, name, suffix)


def add_tier_group(path, domain, name):
    with Network(path) as network:
        network.require_disabled("AddTierGroup")
        row = network.domain(domain)
        if row[3] != "HIERARCHICAL":
            raise ExecuteError("ERROR 002094: tier groups can only be added to domain networks with a hierarchical tier "
                               "definition.")
        if len(str(name)) > 64:
            raise ExecuteError("ERROR 000800: a tier group's name can be a maximum of 64 characters.")
        if network.q("select 1 from grapy_un_tier_group where network = ? and domain = ? and lower(name) = lower(?)",
                     network.name, row[0], name).fetchone():
            raise ExecuteError(f"ERROR 000258: the tier group '{name}' already exists.")
        network.q("insert into grapy_un_tier_group values (?, ?, ?)", network.name, row[0], str(name))


def add_tier(path, domain, name, rank, topology_type, tier_group, subnetwork_field):
    with Network(path) as network:
        network.require_disabled("AddTier")
        row = network.domain(domain)
        if row[2] != "Traditional":
            raise ExecuteError(f"ERROR 000732: '{domain}' is not a traditional domain network.")
        if network.q("select 1 from grapy_un_tier where network = ? and lower(name) = lower(?)", network.name,
                     name).fetchone():
            raise ExecuteError(f"ERROR 000258: a tier named '{name}' exists already; a tier's name is unique in the whole "
                               "utility network.")
        hierarchical = row[3] == "HIERARCHICAL"
        if hierarchical:
            if not tier_group:
                raise ExecuteError("ERROR 000735: Tier Group Name is required for a hierarchical domain network.")
            if not network.q("select 1 from grapy_un_tier_group where network = ? and domain = ? and name = ?",
                             network.name, row[0], tier_group).fetchone():
                raise ExecuteError(f"ERROR 000732: '{tier_group}' is not a tier group of '{row[0]}'.")
            kind = topology_type or "MESH"
            existing = network.q("select subnetwork_field from grapy_un_tier where network = ? and domain = ? and "
                                 "tier_group = ?", network.name, row[0], tier_group).fetchone()
            field = existing[0] if existing else subnetwork_field
            if not field:
                raise ExecuteError("ERROR 000735: Subnetwork Field Name is required for a hierarchical domain network.")
        else:
            if not topology_type:
                raise ExecuteError("ERROR 000735: Topology Type is required for a partitioned domain network.")
            kind, field = topology_type, "SUBNETWORKNAME"
        network.q("insert into grapy_un_tier values (?, ?, ?, ?, ?, ?, ?)", network.name, row[0], str(name), int(rank),
                  kind, tier_group if hierarchical else None, field)
        if True:  # the subnetwork field on the domain's classes, the tier group's or SUBNETWORKNAME
            for class_name, in network.q("select class_name from grapy_un_source where network = ? and domain = ? and "
                                         "role in ('Device','Line','Junction','JunctionObject','EdgeObject')",
                                         network.name, row[0]).fetchall():
                taken = {r[1].lower() for r in network.q(f'pragma table_info("{class_name}")')}
                if field.lower() not in taken:
                    network.q(f'alter table "{class_name}" add column "{field}" TEXT')


# ---- definition rows -----------------------------------------------------------------------------------------------

def rules(network):
    return [dict(zip(("id", "type", "from_class", "from_group", "from_type", "from_terminal", "to_class", "to_group",
                      "to_type", "to_terminal", "via_class", "via_group", "via_type", "via_terminal"), r))
            for r in network.q("select id, rule_type, from_class, from_group, from_type, from_terminal, to_class, "
                               "to_group, to_type, to_terminal, via_class, via_group, via_type, via_terminal from "
                               "grapy_un_rule where network = ? order by id", network.name)]


def add_rule(network, rule_type, ends, via=None):
    """A rule; ``ends`` is [(class, group, type, terminal)] for from and to, ``via`` the edge-junction-edge junction."""
    network.require_disabled("AddRule")
    (fc, fg, ft, fterm), (tc, tg, tt, tterm) = ends
    if rule_type in ("CONTAINMENT", "STRUCTURAL_ATTACHMENT"):
        fterm = tterm = None
    for existing in rules(network):
        if existing["type"] == rule_type and (existing["from_class"], existing["from_group"], existing["from_type"],
                                              existing["from_terminal"], existing["to_class"], existing["to_group"],
                                              existing["to_type"], existing["to_terminal"]) == (
                fc, fg, ft, fterm, tc, tg, tt, tterm) and (existing["via_class"], existing["via_group"],
                                                           existing["via_type"], existing["via_terminal"]) == (
                tuple(via) if via else (None, None, None, None)):
            raise ExecuteError("ERROR 002099: the rule exists already.")
    _check_rule_roles(network, rule_type, fc, tc, via)
    new_id = network.q("select coalesce(max(id), 0) + 1 from grapy_un_rule where network = ?", network.name).fetchone()[0]
    network.q("insert into grapy_un_rule values (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)", network.name, new_id,
              rule_type, fc, fg, ft, fterm, tc, tg, tt, tterm, *(via or (None, None, None, None)))
    return new_id


def _check_rule_roles(network, rule_type, from_class, to_class, via):
    """The feature restrictions a rule must respect, as the AddRule page and the network rules page state them."""
    from_role, to_role = network.role(from_class), network.role(to_class)
    junctions, edges = ("Device", "Junction", "StructureJunction", "JunctionObject"), ("Line", "StructureLine", "EdgeObject")
    if "SubnetLine" in (from_role, to_role) or "Assembly" == to_role and rule_type != "CONTAINMENT":
        raise ExecuteError(f"ERROR 002098: {from_class} and {to_class} cannot take part in a {rule_type} rule.")
    if rule_type == "JUNCTION_JUNCTION_CONNECTIVITY" and not (from_role in junctions and to_role in junctions):
        raise ExecuteError("ERROR 002098: a junction-junction rule is between point features or junction objects.")
    if rule_type == "JUNCTION_EDGE_CONNECTIVITY" and not (from_role in junctions and to_role in edges):
        raise ExecuteError("ERROR 002098: a junction-edge rule's from class is a junction or junction object and its to "
                           "class a line or edge object.")
    if rule_type == "EDGE_JUNCTION_EDGE_CONNECTIVITY":
        if not via or from_role not in edges or to_role not in edges or network.role(via[0]) not in junctions:
            raise ExecuteError("ERROR 002098: an edge-junction-edge rule joins two lines or edge objects through a "
                               "junction (via_class).")
    if rule_type == "STRUCTURAL_ATTACHMENT" and network.domain_of(from_class) != STRUCTURE:
        raise ExecuteError("ERROR 002098: a structural attachment rule's from class is a structure network class.")
    if rule_type == "CONTAINMENT" and from_role not in ("Assembly", "StructureJunction", "StructureLine",
                                                        "StructureBoundary", "Device", "JunctionObject",
                                                        "EdgeObject", "Line"):
        raise ExecuteError("ERROR 002098: that class cannot be a container.")


def _rule_matches(rule, kind, a, b, via=None):
    """Whether a rule of ``kind`` allows ``a`` and ``b`` (each (class, group, type, terminal)); connectivity rules in
    either order, as the Import Rules page says their entry order does not pertain."""
    if rule["type"] != kind:
        return False

    def same(side, end):
        cls, group, kind_, term = end
        if (rule[f"{side}_class"], rule[f"{side}_group"], rule[f"{side}_type"]) != (cls, group, kind_):
            return False
        return rule[f"{side}_terminal"] is None or term is None or rule[f"{side}_terminal"] == term

    if via is not None:
        if not (rule["via_class"], rule["via_group"], rule["via_type"]) == via[:3]:
            return False
        if rule["via_terminal"] is not None and via[3] is not None and rule["via_terminal"] != via[3]:
            return False
    ordered = same("from", a) and same("to", b)
    if kind in ("CONTAINMENT", "STRUCTURAL_ATTACHMENT"):
        return ordered
    return ordered or (same("from", b) and same("to", a))


# ---- associations --------------------------------------------------------------------------------------------------

def associations_table(network):
    return f"{network.dataset}/{network.name}_Associations"


def add_association(network, kind, from_class, from_globalid, from_terminal, to_class, to_globalid, to_terminal,
                    visible=None, percent=None):
    """An association row, checked against the rules (Import Associations: "Features listed … must have supporting
    rules"). Answers its object id."""
    from ._attachments import new_guid
    fo, fg, ft = network.feature_by_globalid(from_class, from_globalid)
    to, tg, tt = network.feature_by_globalid(to_class, to_globalid)
    a = (from_class, fg, ft, from_terminal)
    b = (to_class, tg, tt, to_terminal)
    rule_kind = {1: "JUNCTION_JUNCTION_CONNECTIVITY", 2: "CONTAINMENT", 3: "STRUCTURAL_ATTACHMENT"}.get(
        kind, "JUNCTION_EDGE_CONNECTIVITY")
    if not any(_rule_matches(r, rule_kind, a, b) for r in rules(network)):
        raise ExecuteError(f"ERROR 002095: no {rule_kind} rule supports the association between {from_globalid} and "
                           f"{to_globalid}.")
    if kind in (2, 3):
        # SetAssociationRole: once an asset type has a role, only a container contains and only a structure has
        # attachments. An asset type given no role keeps both, as before the role existed (INFERRED, §5.9 (k)).
        role = network.setting(from_class, fg, ft, "association_role")
        wanted = "CONTAINER" if kind == 2 else "STRUCTURE"
        if role is not None and role["role"] != wanted:
            raise ExecuteError(f"ERROR 002095: the asset type of {from_globalid} has the association role "
                               f"{role['role']}, not {wanted}.")
    if kind == 5 and (percent is None or not 0 <= float(percent) <= 1):
        raise ExecuteError("ERROR 000800: a midspan association needs PERCENTALONG between 0 and 1.")
    table = associations_table(network)
    network.q(f'insert into "{table}" (FROMNETWORKSOURCEID, FROMGLOBALID, FROMTERMINALID, TONETWORKSOURCEID, TOGLOBALID, '
              'TOTERMINALID, ASSOCIATIONTYPE, ISCONTENTVISIBLE, PERCENTALONG, STATUS, ERRORCODE, ERRORMESSAGE, GlobalID) '
              'values (?, ?, ?, ?, ?, ?, ?, ?, ?, 0, 0, null, ?)', network.source_of(from_class), from_globalid.upper(),
              from_terminal, network.source_of(to_class), to_globalid.upper(), to_terminal, kind,
              None if kind != 2 else (1 if visible else 0), None if kind != 5 else float(percent), new_guid())
    oid = network.connection.execute("select last_insert_rowid()").fetchone()[0]
    _association_status(network)
    return int(oid)


def delete_association(network, globalid, kinds):
    table = associations_table(network)
    row = network.q(f'select ASSOCIATIONTYPE from "{table}" where upper(GlobalID) = upper(?)', str(globalid)).fetchone()
    if row is None or int(row[0]) not in kinds:
        raise ExecuteError(f"ERROR 000732: there is no such association {globalid}.")
    network.q(f'delete from "{table}" where upper(GlobalID) = upper(?)', str(globalid))
    _association_status(network)


def association_rows(network):
    table = associations_table(network)
    return [dict(zip(("oid", "from_source", "from_globalid", "from_terminal", "to_source", "to_globalid", "to_terminal",
                      "type", "visible", "percent", "globalid"), r))
            for r in network.q(f'select fid, FROMNETWORKSOURCEID, FROMGLOBALID, FROMTERMINALID, TONETWORKSOURCEID, '
                               f'TOGLOBALID, TOTERMINALID, ASSOCIATIONTYPE, ISCONTENTVISIBLE, PERCENTALONG, GlobalID '
                               f'from "{table}" order by fid')]


def _association_status(network):
    """Every network feature's ASSOCIATIONSTATUS, recomputed from the associations (the attribute's domain bits)."""
    status = defaultdict(int)
    for row in association_rows(network):
        f, t = (row["from_source"], row["from_globalid"]), (row["to_source"], row["to_globalid"])
        if row["type"] == 2:
            status[f] |= CONTAINER
            status[t] |= VISIBLE_CONTENT if row["visible"] else CONTENT
        elif row["type"] == 3:
            status[f] |= STRUCTURE_BIT
            status[t] |= ATTACHMENT
        else:
            status[f] |= CONNECTIVITY
            status[t] |= CONNECTIVITY
    for source, (class_name, _, role) in network.sources().items():
        if role == "SubnetLine":
            continue
        network.q(f'update "{class_name}" set ASSOCIATIONSTATUS = 0')
        for (s, globalid), value in status.items():
            if s == source:
                network.q(f'update "{class_name}" set ASSOCIATIONSTATUS = ? where upper(GLOBALID) = ?', value, globalid)


# ---- the features and the topology record --------------------------------------------------------------------------

def _current(network):
    """{(source, oid): (globalid, data)} of every network feature now, and its signature."""
    out = {}
    for source, (class_name, _, role) in network.sources().items():
        if role == "SubnetLine":
            continue
        columns = {c.name.lower() for c in _gpkg.describe(network.connection, class_name)[0]}
        fields = network.network_fields(class_name)
        extra = [("FROMDEVICETERMINAL", "fromdeviceterminal"), ("TODEVICETERMINAL", "todeviceterminal"),
                 ("TERMINALCONFIGURATION", "terminalconfiguration")]
        wanted = ["ASSETGROUP", "ASSETTYPE"] + [e for e, low in extra if low in columns] + \
            sorted({f for f in fields.values() if f.lower() in columns})
        oid = network.oid(class_name)
        geometry = "geom" if role not in OBJECT_ROLES else None
        select = ", ".join(f'"{w}"' for w in wanted)
        sql = f'select "{oid}", GLOBALID, {"geom" if geometry else "null"}, {select} from "{class_name}"'
        for row in network.q(sql).fetchall():
            shape = _gpkg.decode(row[2]) if geometry else None
            shape = shapely.force_2d(shape) if shape is not None else None
            values = dict(zip(wanted, row[3:]))
            data = {"wkt": shape.wkt if shape is not None and not shape.is_empty else None,
                    "group": int(values["ASSETGROUP"] or 0), "type": int(values["ASSETTYPE"] or 0),
                    "from_terminal": values.get("FROMDEVICETERMINAL"), "to_terminal": values.get("TODEVICETERMINAL"),
                    "path": values.get("TERMINALCONFIGURATION"),
                    "attributes": {a: values.get(f) for a, f in fields.items() if f in values}}
            out[(source, int(row[0]))] = ((row[1] or "").upper(), data)
    return out


def _signature(data):
    return json.dumps(data, sort_keys=True, default=str)


def _record(network):
    return {(int(s), int(o)): (g, json.loads(d), sig) for s, o, g, d, sig in network.q(
        "select source_id, oid, globalid, data, signature from grapy_un_topology_feature where network = ?", network.name)}


def _record_associations(network):
    return {g: (json.loads(d), sig) for g, d, sig in network.q(
        "select globalid, data, signature from grapy_un_topology_association where network = ?", network.name)}


def _envelope(data):
    if not data or not data.get("wkt"):
        return None
    return shapely.from_wkt(data["wkt"]).bounds


def _changes(network):
    """[(key, old data or None, new data or None)] of features and [(globalid, old, new)] of associations whose
    record differs from what is there now."""
    now, then = _current(network), _record(network)
    features = []
    for key in set(now) | set(then):
        new = now.get(key)
        old = then.get(key)
        if new is None:
            features.append((key, old[1], None))
        elif old is None:
            features.append((key, None, new[1]))
        elif old[2] != _signature(new[1]):
            features.append((key, old[1], new[1]))
    now_a = {r["globalid"].upper(): r for r in association_rows(network)}
    then_a = _record_associations(network)
    associations = []
    for globalid in set(now_a) | set(then_a):
        new = now_a.get(globalid)
        old = then_a.get(globalid)
        clean = {k: v for k, v in new.items() if k != "oid"} if new else None
        if old is None or new is None or old[1] != _signature(clean):
            associations.append((globalid, old[0] if old else None, clean))
    return features, associations, now


def write_dirty_areas(network):
    """``<network>_DirtyAreas`` rewritten: with the topology disabled, one area over the service territory's extent
    (status 0) beside the error areas; enabled, the edits not yet validated and the errors (Dirty areas page)."""
    table = f"{network.dataset}/{network.name}_DirtyAreas"
    srid = int(network.q("select srs_id from gpkg_geometry_columns where table_name = ?", table).fetchone()[0])
    network.q(f'delete from "{table}"')
    rows = []
    errors = defaultdict(list)
    for source, oid, globalid, code, message, envelope in network.q(
            "select source_id, oid, globalid, code, message, envelope from grapy_un_error where network = ?",
            network.name).fetchall():
        errors[(int(source), int(oid))].append((code, message, envelope, globalid))
    if not network.enabled:
        territory = _territory_extent(network)
        if territory is not None:
            rows.append((box(*territory), 0, 0, None, None, None))
        edited, known = {}, {}
    else:
        features, associations, now = _changes(network)
        known = {k: g for k, (g, _, _) in _record(network).items()}
        known.update({k: g for k, (g, _) in now.items()})
        edited = defaultdict(lambda: [0, []])
        for key, old, new in features:
            bit = DELETED if new is None else MODIFIED
            edited[key][0] |= bit
            edited[key][1] += [e for e in (_envelope(old), _envelope(new)) if e]
        for globalid, old, new in associations:
            for row in (old, new):
                if row is None:
                    continue
                for side in ("from", "to"):
                    key = next((k for k, (g, _) in now.items() if g == str(row[f"{side}_globalid"]).upper()), None)
                    if key is not None:
                        edited[key][0] |= ASSOCIATED
                        edited[key][1] += [e for e in (_envelope(now[key][1]),) if e]
    sources = network.sources()
    for key in set(edited) | set(errors):
        bits, envelopes = edited.get(key, (0, []))
        codes = errors.get(key, [])
        role = sources.get(key[0], (None, None, None))[2]
        error_bit = (OBJECT_ERROR if role in OBJECT_ROLES else FEATURE_ERROR) if codes else 0
        shapes = [box(*e) if e[0] != e[2] or e[1] != e[3] else ShapelyPoint(e[0], e[1]).buffer(TOLERANCE)
                  for e in envelopes + [tuple(json.loads(c[2])) for c in codes if c[2]]]
        if not shapes:
            territory = _territory_extent(network)
            shapes = [box(territory[0], territory[1], territory[0] + TOLERANCE, territory[1] + TOLERANCE)] \
                if territory else []
        if not shapes:
            continue
        area = unary_union([s if s.area > 0 else s.buffer(TOLERANCE) for s in shapes]).envelope
        globalid = codes[0][3] if codes else known.get(key)
        rows.append((area, bits | error_bit, codes[0][0] if codes else 0, "; ".join(c[1] for c in codes) or None,
                     key[0], globalid))
    for shape, status, code, message, source, globalid in rows:
        network.q(f'insert into "{table}" (geom, STATUS, ERRORCODE, ERRORMESSAGE, NETWORKSOURCEID, FEATUREGLOBALID) '
                  'values (?, ?, ?, ?, ?, ?)', _gpkg.encode(shapely.MultiPolygon([shape]) if shape.geom_type == "Polygon"
                                                            else shape, srid), status, code, message, source, globalid)


def _territory_extent(network):
    file, table = _gpkg.split(network.territory)
    connection = _gpkg.connect(file)
    try:
        shapes = [_gpkg.decode(r[0]) for r in connection.execute(f'select geom from "{table}"')]
    finally:
        connection.close()
    shapes = [s for s in shapes if s is not None and not s.is_empty]
    return unary_union(shapes).bounds if shapes else None


def dirty_meets(network, keys, graph):
    """Whether a dirty area — an edit not yet validated, by the envelopes of its old and new shapes, or an association
    changed on one of ``keys`` — meets the features ``keys`` (Update Subnetwork: "no dirty areas intersect subnetwork
    features")."""
    features, associations, now = _changes(network)
    if not features and not associations:
        return False
    if dirty_keys(network) & set(keys):
        return True
    areas = [box(*e).buffer(TOLERANCE) for _, old, new in features for e in (_envelope(old), _envelope(new)) if e]
    shapes = [shapely.from_wkt(graph.features[k][1]["wkt"]) for k in keys
              if k in graph.features and graph.features[k][1].get("wkt")]
    return any(a.intersects(s) for a in areas for s in shapes)


def dirty_keys(network):
    """The features (source, oid) that carry an edit dirty area now."""
    features, associations, now = _changes(network)
    keys = {k for k, _, _ in features}
    for _, old, new in associations:
        for row in (old, new):
            if row:
                for side in ("from", "to"):
                    keys |= {k for k, (g, _) in now.items() if g == str(row[f"{side}_globalid"]).upper()}
    return keys


def refresh(network, extent=None, max_errors=None, everything=False):
    """Enable or validate: the record brought up to date — for every feature, or only those whose dirty area
    intersects ``extent`` — the topology rebuilt from it, and its errors and dirty areas rewritten. ``everything``
    (RebuildNetworkTopology) takes every feature in the extent, dirty or not."""
    _apply_deletion_semantics(network)
    features, associations, now = _changes(network)
    if everything:
        then = _record(network)
        changed = {k for k, _, _ in features}
        features = features + [(k, then[k][1] if k in then else None, now[k][1]) for k in now if k not in changed]
    window = box(*extent) if extent else None

    def inside(*datas):
        if window is None:
            return True
        return any(e is not None and box(*e).intersects(window) for e in (_envelope(d) for d in datas if d))

    taken = set()
    for key, old, new in features:
        if not inside(old, new):
            continue
        taken.add(key)
        network.q("delete from grapy_un_topology_feature where network = ? and source_id = ? and oid = ?", network.name,
                  *key)
        if new is not None:
            network.q("insert into grapy_un_topology_feature values (?, ?, ?, ?, ?, ?)", network.name, key[0], key[1],
                      now[key][0], _signature(new), json.dumps(new))
    by_globalid = {g: k for k, (g, _) in now.items()}
    for globalid, old, new in associations:
        row = new or old
        keys = [by_globalid.get(str(row[f"{s}_globalid"]).upper()) for s in ("from", "to")]
        if window is not None and not any(k and inside(now[k][1]) for k in keys):
            continue
        network.q("delete from grapy_un_topology_association where network = ? and globalid = ?", network.name, globalid)
        if new is not None:
            network.q("insert into grapy_un_topology_association values (?, ?, ?, ?)", network.name, globalid,
                      _signature(new), json.dumps(new))
    graph = build(network)
    network.q("delete from grapy_un_error where network = ?", network.name)
    found = graph.errors if max_errors is None or max_errors < 0 else graph.errors[:max_errors]
    for (source, oid), globalid, code, message, envelope in found:
        network.q("insert into grapy_un_error values (?, ?, ?, ?, ?, ?, ?)", network.name, source, oid, globalid, code,
                  message, json.dumps(envelope) if envelope else None)
    table = associations_table(network)
    network.q(f'update "{table}" set ERRORCODE = 0, ERRORMESSAGE = null')
    for globalid, code, message in graph.association_errors:
        network.q(f'update "{table}" set ERRORCODE = ?, ERRORMESSAGE = ? where upper(GlobalID) = ?', code, message,
                  globalid)
    return graph


def _apply_deletion_semantics(network):
    """SetAssociationRole's deletion semantics, applied when a utility network tool finds a container or structure
    deleted since the record (grapy has no edit session to apply them at the delete, INFERRED §5.9 (k)): CASCADE deletes
    its content or attachments, SET_TO_NONE only the associations, RESTRICTED leaves both as they are."""
    now = {k for k in _current(network)}
    record = _record(network)
    sources = network.sources()
    table = associations_table(network)
    changed = False
    for key, (globalid, data, _) in record.items():
        if key in now or key[0] not in sources:
            continue
        role = network.setting(sources[key[0]][0], data["group"], data["type"], "association_role")
        if not role or role.get("deletion") not in ("CASCADE", "SET_TO_NONE"):
            continue
        kind = 2 if role["role"] == "CONTAINER" else 3
        for content_source, content in network.q(f'select TONETWORKSOURCEID, TOGLOBALID from "{table}" where '
                                                 'upper(FROMGLOBALID) = ? and ASSOCIATIONTYPE = ?', str(globalid).upper(),
                                                 kind).fetchall():
            if role["deletion"] == "CASCADE" and content_source in sources:
                network.q(f'delete from "{sources[content_source][0]}" where upper(GLOBALID) = upper(?)', content)
        network.q(f'delete from "{table}" where upper(FROMGLOBALID) = ? and ASSOCIATIONTYPE = ?', str(globalid).upper(),
                  kind)
        changed = True
    if changed:
        _association_status(network)


# ---- the graph -----------------------------------------------------------------------------------------------------

class Element:
    __slots__ = ("id", "kind", "source", "oid", "globalid", "terminal", "start", "end", "shape", "length")

    def __init__(self, id_, kind, source, oid, globalid, terminal=None, start=None, end=None, shape=None, length=None):
        self.id, self.kind, self.source, self.oid, self.globalid = id_, kind, source, oid, globalid
        self.terminal, self.start, self.end, self.shape, self.length = terminal, start, end, shape, length

    @property
    def feature(self):
        return None if self.source == 0 else (self.source, self.oid)


class Graph:
    """The network topology built from the record: junction elements (one per terminal of a device or junction
    object, one per point otherwise, and system junctions), edge elements (a line between consecutive connection
    points along it), connectivity links, and the internal paths of devices with terminals."""

    def __init__(self, network):
        self.network = network
        self.elements: list[Element] = []
        self.by_feature = defaultdict(list)
        self.links = set()            # undirected connectivity between elements
        self.internal = []            # (a, b, directed) paths inside a device or junction object
        self.errors = []              # ((source, oid), globalid, code, message, envelope)
        self.association_errors = []  # (association globalid, code, message)
        self.features = {}            # (source, oid) -> (globalid, data)
        self.associations = []        # the recorded association rows
        self.ends = {}                # edge element -> ([junctions at its start], [junctions at its end]), digitized
        self.sources = network.sources()

    def add(self, kind, feature, globalid, **more):
        element = Element(len(self.elements), kind, feature[0] if feature else 0, feature[1] if feature else 0,
                          globalid, **more)
        self.elements.append(element)
        if feature:
            self.by_feature[feature].append(element.id)
        return element.id

    def link(self, a, b):
        if a != b:
            self.links.add((min(a, b), max(a, b)))

    def error(self, key, code, message):
        globalid, data = self.features.get(key, (None, None))
        if any(e[0] == key and e[2] == code for e in self.errors):
            return
        self.errors.append((key, globalid, code, message, _envelope(data)))

    # -- adjacency --

    def undirected(self, domain=None):
        """Connectivity only: terminals of one feature are one, as a connected trace treats them."""
        out = defaultdict(set)
        for a, b in self.links:
            out[a].add(b)
            out[b].add(a)
        for ids in self.by_feature.values():
            junctions = [i for i in ids if self.elements[i].kind == "J"]
            for a in junctions:
                for b in junctions:
                    if a != b:
                        out[a].add(b)
        return self._within(out, domain)

    def arcs(self, domain=None):
        """Traversability: links both ways, a device's internal paths as its terminal configuration allows."""
        out = defaultdict(set)
        for a, b in self.links:
            out[a].add(b)
            out[b].add(a)
        for a, b, directed in self.internal:
            out[a].add(b)
            if not directed:
                out[b].add(a)
        return self._within(out, domain)

    def path_arcs(self, domain=None):
        """Terminal paths respected but not their direction, as the shortest path and path traces do (the "Utility
        network trace types" page)."""
        out = defaultdict(set)
        for a, b in self.links:
            out[a].add(b)
            out[b].add(a)
        for a, b, _ in self.internal:
            out[a].add(b)
            out[b].add(a)
        return self._within(out, domain)

    def digitized_arcs(self, domain=None):
        """Flow by digitized direction (Trace's USE_DIGITIZED_DIRECTION): an edge element carries the resource from the
        junctions at its start to those at its end — a line's vertex order, an edge object's from and to associations;
        a junction-junction or midspan link both ways; a device's internal paths as its configuration allows. grapy
        computes no Flow direction network attribute (ADR-231 §5.5), so every line is traced as digitized (INFERRED,
        §5.9 (j))."""
        out = defaultdict(set)
        ended = set()
        for edge, (starts, ends) in self.ends.items():
            for j in starts:
                out[j].add(edge)
                ended.add((min(j, edge), max(j, edge)))
            for j in ends:
                out[edge].add(j)
                ended.add((min(j, edge), max(j, edge)))
        for a, b in self.links:
            if (a, b) in ended:
                continue
            out[a].add(b)
            out[b].add(a)
        for a, b, directed in self.internal:
            out[a].add(b)
            if not directed:
                out[b].add(a)
        return self._within(out, domain)

    def _within(self, out, domain):
        if domain is None:
            return out
        keep = {e.id for e in self.elements if e.source == 0 or self.sources[e.source][1] == domain}
        return defaultdict(set, {a: {b for b in bs if b in keep} for a, bs in out.items() if a in keep})

    def elements_of(self, key):
        return [self.elements[i] for i in self.by_feature.get(key, [])]


def build(network):
    """The topology from the record (``grapy_un_topology_feature`` and ``…_association``), its errors found."""
    graph = Graph(network)
    record = _record(network)
    graph.features = {k: (g, d) for k, (g, d, _) in record.items()}
    graph.associations = [d for d, _ in _record_associations(network).values()]
    all_rules = rules(network)
    sources = graph.sources

    def asset(key):
        data = graph.features[key][1]
        return sources[key[0]][0], data["group"], data["type"]

    terminals_of = {}
    for key, (globalid, data) in sorted(graph.features.items()):
        class_name, _, role = sources[key[0]]
        if data["group"] == 0:
            graph.error(key, 2, "Subtype is unknown: the asset group is Unknown.")
            continue
        if role in ("Device", "Junction", "StructureJunction", "JunctionObject"):
            if role != "JunctionObject" and not data["wkt"]:
                graph.error(key, 1, "The geometry for the network feature is empty.")
                continue
            config = network.terminal_config(class_name, data["group"], data["type"])
            terminals = [t[0] for t in config[3]] if config else [None]
            terminals_of[key] = (config, {})
            shape = shapely.from_wkt(data["wkt"]) if data["wkt"] else None
            for terminal in terminals:
                terminals_of[key][1][terminal] = graph.add("J", key, globalid, terminal=terminal, shape=shape)
            if config:
                _internal_paths(graph, config, terminals_of[key][1], data.get("path"))

    def junction(key, terminal):
        found = terminals_of.get(key)
        if not found:
            return []
        if terminal is None:
            return list(found[1].values())
        return [found[1][terminal]] if terminal in found[1] else []

    def terminal_ids(key):
        found = terminals_of.get(key)
        return list(found[1]) if found else []

    # Lines: where they meet points and each other, by coincident vertices.
    index = defaultdict(lambda: {"points": [], "lines": []})
    lines = {}
    for key, (globalid, data) in sorted(graph.features.items()):
        role = sources[key[0]][2]
        if data["group"] == 0 or not data["wkt"]:
            if role in LINE_ROLES and data["group"] != 0:
                graph.error(key, 1, "The geometry for the network feature is empty.")
            continue
        shape = shapely.from_wkt(data["wkt"])
        if role in ("Device", "Junction", "StructureJunction"):
            index[_key(shape.x, shape.y)]["points"].append(key)
        elif role in LINE_ROLES:
            line = shapely.line_merge(shape) if shape.geom_type == "MultiLineString" else shape
            if line.geom_type != "LineString":
                line = LineString([c for part in shape.geoms for c in part.coords])
            if line.length <= TOLERANCE:
                graph.error(key, 5, "Line feature has length within tolerance.")
                continue
            lines[key] = line
            coords = list(line.coords)
            # SetEdgeConnectivity: an asset type set to END_VERTEX connects only at its end vertices.
            ends_only = network.setting(sources[key[0]][0], data["group"], data["type"], "edge_connectivity",
                                        "ANY_VERTEX") == "END_VERTEX"
            for i, c in enumerate(coords):
                if ends_only and i not in (0, len(coords) - 1):
                    continue
                index[_key(c[0], c[1])]["lines"].append((key, i, i in (0, len(coords) - 1)))

    def line_asset(key):
        return asset(key)

    connections = defaultdict(list)   # line key -> [(position along, junction element ids)]
    for location, here in index.items():
        if not here["lines"]:
            continue
        # INFERRED (ADR-231 §5.9 (c)): coincidence connects within one network — a domain network's own classes, or
        # the structure network's — as the feature restrictions page pairs them; a vault over a valve is no error.
        domains = {sources[k[0]][1] for k, _, _ in here["lines"]}
        points = [p for p in here["points"] if sources[p[0]][1] in domains]
        line_keys = []
        for line_key, vertex, is_end in here["lines"]:
            if line_key not in [k for k, _, _ in line_keys]:
                line_keys.append((line_key, vertex, is_end))
        made = {}
        for line_key, vertex, is_end in line_keys:
            linked = []
            for point in [p for p in points if sources[p[0]][1] == sources[line_key[0]][1]]:
                pc, pg, pt = asset(point)
                lc, lg, lt = line_asset(line_key)
                candidates = [t for t in terminal_ids(point)
                              if any(_rule_matches(r, "JUNCTION_EDGE_CONNECTIVITY", (pc, pg, pt, t), (lc, lg, lt, None))
                                     for r in all_rules)]
                end_field = None
                if is_end:
                    end_field = graph.features[line_key][1]["from_terminal" if vertex == 0 else "to_terminal"]
                if end_field is not None and candidates and candidates != [None]:
                    candidates = [t for t in candidates if t == int(end_field)]
                if len(candidates) == 1:
                    linked += junction(point, candidates[0])
                elif len(candidates) > 1:
                    graph.error(line_key, 9, "Invalid connectivity: more than one junction-edge rule is applicable.")
                    graph.error(point, 9, "Invalid connectivity: more than one junction-edge rule is applicable.")
                else:
                    others = [k for k, _, _ in line_keys if k != line_key]
                    via = [t for t in terminal_ids(point) for other in others for r in all_rules
                           if _rule_matches(r, "EDGE_JUNCTION_EDGE_CONNECTIVITY", (lc, lg, lt, None),
                                            line_asset(other) + (None,), (pc, pg, pt, t))]
                    if via:
                        linked += junction(point, via[0])
                    else:
                        graph.error(line_key, 8, "Invalid connectivity: no connectivity rule.")
                        graph.error(point, 8, "Invalid connectivity: no connectivity rule.")
            made[line_key] = linked
        # Lines meeting with no point between them: one system junction when they share an asset group and type.
        bare = [(k, v, e) for k, v, e in line_keys if not made[k]]
        groups = {line_asset(k) for k, _, _ in bare}
        if len(bare) > 1 and not points:
            if len(groups) == 1:
                system = graph.add("J", None, None, shape=ShapelyPoint(*location_xy(location)))
                for k, _, _ in bare:
                    made[k] = [system]
            else:
                for k, _, _ in bare:
                    graph.error(k, 10, "Invalid connectivity: edge features with different asset group and asset type "
                                       "attributes cannot connect.")
        for line_key, vertex, is_end in line_keys:
            if made[line_key] or is_end:
                position = lines[line_key].project(ShapelyPoint(lines[line_key].coords[vertex]), normalized=True)
                connections[line_key].append((position, made[line_key]))

    for line_key, line in lines.items():
        globalid = graph.features[line_key][0]
        stops = defaultdict(list)
        for position, ids in connections[line_key]:
            stops[round(position, 12)] += ids
        for end in (0.0, 1.0):
            if end not in stops or not stops[end]:
                stops[end] = [graph.add("J", None, None, shape=line.interpolate(end, normalized=True))]
        ordered = sorted(stops)
        for a, b in zip(ordered, ordered[1:]):
            piece = substring(line, a, b, normalized=True)
            edge = graph.add("E", line_key, globalid, start=a, end=b, shape=piece, length=piece.length)
            graph.ends[edge] = (list(stops[a]), list(stops[b]))
            for junction_id in stops[a] + stops[b]:
                graph.link(edge, junction_id)

    _associate(graph, network, all_rules, asset, junction, terminal_ids, lines)
    return graph


def location_xy(location):
    return location[0] * TOLERANCE, location[1] * TOLERANCE


def _key(x, y):
    return round(x / TOLERANCE), round(y / TOLERANCE)


def _internal_paths(graph, config, elements, current_path):
    """A device's internal paths: its current valid path (TERMINALCONFIGURATION, else the configuration's default),
    every pair for ALL, none for NONE; in a directional configuration a path carries the resource only from an
    upstream terminal to a downstream one (INFERRED, ADR-231 §5.9 (f))."""
    _, _, model, terminals, paths, default = config
    name = current_path or default
    chosen = paths.get(name, "ALL" if str(name).upper() == "ALL" else "NONE" if str(name).upper() == "NONE" else None)
    if chosen is None:
        chosen = paths.get(default, "ALL")
    ids = [t[0] for t in terminals]
    pairs = [(a, b) for i, a in enumerate(ids) for b in ids[i + 1:]] if chosen == "ALL" else \
        [] if chosen == "NONE" else [tuple(p) for p in chosen]
    upstream = {t[0]: bool(t[2]) for t in terminals}
    for a, b in pairs:
        if model == "DIRECTIONAL":
            if upstream[a] and not upstream[b]:
                graph.internal.append((elements[a], elements[b], True))
            elif upstream[b] and not upstream[a]:
                graph.internal.append((elements[b], elements[a], True))
        else:
            graph.internal.append((elements[a], elements[b], False))


def _associate(graph, network, all_rules, asset, junction, terminal_ids, lines):
    """Connectivity associations into links; edge objects into edge elements; every association checked against the
    rules (errors 8, 17, 18, 34 and 35 of the error list)."""
    by_globalid = {g: k for k, (g, _) in graph.features.items()}
    sources = graph.sources
    edge_stops = defaultdict(dict)
    for row in graph.associations:
        a = by_globalid.get(str(row["from_globalid"]).upper())
        b = by_globalid.get(str(row["to_globalid"]).upper())
        if a is None or b is None or a not in graph.features or b not in graph.features:
            continue
        if graph.features[a][1]["group"] == 0 or graph.features[b][1]["group"] == 0:
            continue
        kind = int(row["type"])
        rule_kind = {1: "JUNCTION_JUNCTION_CONNECTIVITY", 2: "CONTAINMENT", 3: "STRUCTURAL_ATTACHMENT"}.get(
            kind, "JUNCTION_EDGE_CONNECTIVITY")
        ends = (asset(a) + (row["from_terminal"],), asset(b) + (row["to_terminal"],))
        if not any(_rule_matches(r, rule_kind, *ends) for r in all_rules):
            objects = sources[b[0]][2] in OBJECT_ROLES
            code = {1: 8, 2: 17 if objects else 34, 3: 18 if objects else 35}.get(kind, 8)
            message = {8: "Invalid connectivity: no connectivity rule.",
                       17: "No containment rule.", 34: "Feature in unsupported containment relationship.",
                       18: "No structural attachment rule.",
                       35: "Feature in unsupported structural attachment relationship."}[code]
            graph.error(b, code, message)
            graph.association_errors.append((str(row["globalid"]).upper(), code, message))
            continue
        if kind == 1:
            for x in junction(a, row["from_terminal"]):
                for y in junction(b, row["to_terminal"]):
                    graph.link(x, y)
        elif kind in (4, 5, 6):
            position = 0.0 if kind == 4 else 1.0 if kind == 6 else float(row["percent"] or 0)
            if sources[b[0]][2] == "EdgeObject":
                edge_stops[b].setdefault(round(position, 12), []).extend(junction(a, row["from_terminal"]))
            elif b in lines:
                edges = [e for e in graph.elements_of(b) if e.kind == "E"]
                hit = next((e for e in edges if e.start <= position <= e.end), None)
                if hit is not None:
                    for x in junction(a, row["from_terminal"]):
                        graph.link(x, hit.id)
    for key, (globalid, data) in sorted(graph.features.items()):
        if sources[key[0]][2] != "EdgeObject" or data["group"] == 0:
            continue
        stops = edge_stops.get(key, {})
        for end in (0.0, 1.0):
            if not stops.get(end):
                stops[end] = [graph.add("J", None, None)]
        ordered = sorted(stops)
        for x, y in zip(ordered, ordered[1:]):
            edge = graph.add("E", key, globalid, start=x, end=y, length=None)
            graph.ends[edge] = (list(stops[x]), list(stops[y]))
            for junction_id in stops[x] + stops[y]:
                graph.link(edge, junction_id)


# ---- values and conditions -----------------------------------------------------------------------------------------

class Values:
    """What a condition reads of an element: its network attributes and its categories."""

    def __init__(self, graph):
        self.graph = graph
        network = graph.network
        self.categories = defaultdict(set)
        for category, class_name, group, kind in network.q(
                "select category, class_name, asset_group, asset_type from grapy_un_category_asset where network = ?",
                network.name):
            self.categories[(class_name, int(group), int(kind))].add(category.lower())
        self.controllers = {}
        for class_name, globalid, terminal, name, tier, subnetwork in network.q(
                "select class_name, globalid, terminal_id, controller_name, tier, subnetwork_name from "
                "grapy_un_controller where network = ?", network.name):
            self.controllers[(class_name, globalid.upper(), terminal)] = (name, tier, subnetwork)
        self.lengths = {}

    def asset(self, element):
        if element.feature is None:
            return None
        data = self.graph.features[element.feature][1]
        return self.graph.sources[element.source][0], data["group"], data["type"]

    def controller(self, element):
        if element.feature is None:
            return None
        return self.controllers.get((self.graph.sources[element.source][0], element.globalid, element.terminal))

    def attribute(self, element, name):
        name = str(name)
        if element.feature is None:
            return None
        data = self.graph.features[element.feature][1]
        low = name.lower()
        if low == "asset group":
            return data["group"]
        if low == "asset type":
            return data["type"]
        if low == "terminal id":
            return element.terminal
        if low == "is subnetwork controller":
            return 1 if self.controller(element) else 0
        if low == "tier name":
            found = self.controller(element)
            return found[1] if found else None
        if low == "shape length":
            return element.length if element.kind == "E" else None
        for attribute, value in data["attributes"].items():
            if attribute.lower() == low:
                return value
        return None

    def has_category(self, element, category):
        found = self.asset(element)
        return found is not None and str(category).lower() in self.categories.get(found, set())


_OPERATORS = ("IS_EQUAL_TO", "DOES_NOT_EQUAL", "IS_GREATER_THAN", "IS_GREATER_THAN_OR_EQUAL_TO", "IS_LESS_THAN",
              "IS_LESS_THAN_OR_EQUAL_TO", "INCLUDES_THE_VALUES", "DOES_NOT_INCLUDE_THE_VALUES", "INCLUDES_ANY",
              "DOES_NOT_INCLUDE_ANY")


def value_table(value, columns):
    """A value table given as a list of rows or as text: rows split by ``;``, values by spaces with quotes kept
    together, ``#`` an empty value."""
    if value is None or (isinstance(value, str) and not value.strip()) or value == "#":
        return []
    rows = value if isinstance(value, (list, tuple)) else [r for r in str(value).split(";") if r.strip()]
    out = []
    for row in rows:
        cells = list(row) if isinstance(row, (list, tuple)) else shlex.split(str(row))
        cells = [None if c is None or str(c).strip() in ("", "#") else c for c in cells]
        out.append(cells + [None] * (columns - len(cells)))
    return out


class Condition:
    """Condition rows (Name, Operator, Type, Value, Combine Using): AND binds before OR (INFERRED, ADR-231 §5.9 (f));
    a row named ``Category`` asks for a network category."""

    def __init__(self, rows, values, scope="BOTH_JUNCTIONS_AND_EDGES"):
        self.rows, self.values, self.scope = rows, values, str(scope or "BOTH_JUNCTIONS_AND_EDGES").upper()
        for row in rows:
            if str(row[1]).upper() not in _OPERATORS:
                raise ExecuteError(f"ERROR 000800: '{row[1]}' is not an operator of a condition.")

    def __bool__(self):
        return bool(self.rows)

    def applies(self, element):
        if self.scope == "JUNCTIONS_ONLY" and element.kind != "J":
            return False
        if self.scope == "EDGES_ONLY" and element.kind != "E":
            return False
        return True

    def one(self, element, row):
        name, operator, kind, value = row[0], str(row[1]).upper(), str(row[2] or "SPECIFIC_VALUE").upper(), row[3]
        if str(name).lower() == "category":
            has = self.values.has_category(element, value)
            return has if operator == "IS_EQUAL_TO" else (not has) if operator == "DOES_NOT_EQUAL" else False
        left = self.values.attribute(element, name)
        right = self.values.attribute(element, value) if kind == "NETWORK_ATTRIBUTE" else value
        return compare(left, operator, right)

    def __call__(self, element):
        if not self.rows or not self.applies(element):
            return False
        groups, current = [], []
        for row in self.rows:
            current.append(self.one(element, row))
            if str(row[4] or "OR").upper() == "OR":
                groups.append(all(current))
                current = []
        if current:
            groups.append(all(current))
        return any(groups)


def compare(left, operator, right):
    """One comparison of a condition, function barrier or propagator, by the operator keywords the pages list; the
    four bitwise ones on integers. A null on either side is false."""
    operator = str(operator).upper()
    if operator not in _OPERATORS:
        raise ExecuteError(f"ERROR 000800: '{operator}' is not an operator of a condition.")
    if left is None or right is None:
        return False
    try:
        left_n, right_n = float(left), float(right)
    except (TypeError, ValueError):
        left_n, right_n = str(left), str(right)
    if operator == "IS_EQUAL_TO":
        return left_n == right_n
    if operator == "DOES_NOT_EQUAL":
        return left_n != right_n
    if operator == "IS_GREATER_THAN":
        return left_n > right_n
    if operator == "IS_GREATER_THAN_OR_EQUAL_TO":
        return left_n >= right_n
    if operator == "IS_LESS_THAN":
        return left_n < right_n
    if operator == "IS_LESS_THAN_OR_EQUAL_TO":
        return left_n <= right_n
    a, b = int(left_n), int(right_n)
    if operator == "INCLUDES_THE_VALUES":
        return a & b == b
    if operator == "DOES_NOT_INCLUDE_THE_VALUES":
        return a & b != b
    if operator == "INCLUDES_ANY":
        return a & b != 0
    return a & b == 0


# ---- traversal -----------------------------------------------------------------------------------------------------

def reach(seeds, arcs, barrier, starts_free=True, gate=None):
    """Elements reached from ``seeds`` over ``arcs``: a barrier is reached but not passed; returns (passed, barriers).
    Breadth first, each element's neighbours in id order, so a ``gate`` — ``gate(parent, child)`` true when the child
    stops the trace, as a function barrier's running total or a bitset filter decides — meets them in a fixed order
    (``parent`` is None for a seed)."""
    seen, stopped = set(), set()
    queue = deque()
    for s in sorted(seeds):
        if (barrier(s) or (gate is not None and gate(None, s))) and not starts_free:
            stopped.add(s)
            continue
        if gate is not None and starts_free:
            gate(None, s)
        seen.add(s)
        queue.append(s)
    while queue:
        a = queue.popleft()
        for b in sorted(arcs.get(a, ())):
            if b in seen or b in stopped:
                continue
            if barrier(b) or (gate is not None and gate(a, b)):
                stopped.add(b)
                continue
            seen.add(b)
            queue.append(b)
    return seen, stopped


def reverse(arcs):
    out = defaultdict(set)
    for a, bs in arcs.items():
        for b in bs:
            out[b].add(a)
    return out


def dominators(root, arcs):
    """The immediate dominator of each node reachable from ``root`` (Cooper, Harvey and Kennedy 2001)."""
    order, seen, stack = [], {root}, [(root, iter(sorted(arcs.get(root, ()))))]
    while stack:
        node, children = stack[-1]
        for child in children:
            if child not in seen:
                seen.add(child)
                stack.append((child, iter(sorted(arcs.get(child, ())))))
                break
        else:
            stack.pop()
            order.append(node)
    order.reverse()
    number = {n: i for i, n in enumerate(order)}
    predecessors = reverse({a: {b for b in bs if b in number} for a, bs in arcs.items() if a in number})
    idom = {root: root}
    changed = True
    while changed:
        changed = False
        for node in order[1:]:
            done = [p for p in predecessors.get(node, ()) if p in idom]
            if not done:
                continue
            new = done[0]
            for p in done[1:]:
                a, b = p, new
                while a != b:
                    while number[a] > number[b]:
                        a = idom[a]
                    while number[b] > number[a]:
                        b = idom[b]
                new = a
            if idom.get(node) != new:
                idom[node] = new
                changed = True
    return idom


def dominates(idom, a, b):
    """Whether ``a`` dominates ``b``."""
    while True:
        if a == b:
            return True
        parent = idom.get(b)
        if parent is None or parent == b:
            return False
        b = parent


def flow_arcs(region, arcs, seeds, barrier):
    """Arcs u→v inside ``region`` along which the resource can pass away from the seeds: u reached, and v not on every
    path from the seeds to u (v does not dominate u). A barrier passes nothing on."""
    root = -1
    local = {a: {b for b in arcs.get(a, ()) if b in region} for a in region if not barrier(a) or a in seeds}
    local[root] = set(seeds)
    idom = dominators(root, local)
    out = defaultdict(set)
    for a, bs in local.items():
        if a == root or a not in idom:
            continue
        for b in bs:
            if not dominates(idom, b, a):
                out[a].add(b)
    return out
