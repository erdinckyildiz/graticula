"""arcpy.un — utility networks, by their documented names and parameters (ADR-231).

The model is ``grapy._utility``: grapy's tables in the GeoPackage, beside the network's classes in its feature dataset.
The traces are ``grapy._untrace``. Upstream and Downstream are *equivalents* (ADR-224 §5.6): the pages define their
direction only as "away from" or "toward" the subnetwork controllers.
"""
from __future__ import annotations

import csv
import json
import math
import os

import shapely

from . import _gpkg, _untrace, _utility
from ._result import Result
from ._runtime import ExecuteError, NotBuiltError, equivalent

_VERSIONS = {"COMPATIBLE": "8", "CURRENT": "8", "VERSION_8": "8", "VERSION_7": "7", "VERSION_6": "6", "VERSION_5": "5"}


def _given(value):
    return value is not None and str(value).strip() not in ("", "#")


def _word(value, default):
    return str(value).strip().upper() if _given(value) else default


def _flag(value, yes, no, default):
    """A Boolean parameter as a keyword or as True/False."""
    if value is True:
        return True
    if value is False:
        return False
    word = _word(value, default)
    if word in (yes, "TRUE"):
        return True
    if word in (no, "FALSE"):
        return False
    raise ExecuteError(f"ERROR 000800: '{value}' is not {yes} or {no}.")


def _path(value):
    return os.fspath(value) if isinstance(value, os.PathLike) else str(value)


def _rows(value):
    """A multivalue or value-table parameter split into rows by ``;`` outside quotes."""
    if value is None:
        return []
    if isinstance(value, (list, tuple)):
        return list(value)
    rows, current, quote = [], "", None
    for c in str(value):
        if quote:
            current += c
            if c == quote:
                quote = None
        elif c in "'\"":
            quote = c
            current += c
        elif c == ";":
            rows.append(current)
            current = ""
        else:
            current += c
    rows.append(current)
    return [r.strip() for r in rows if r.strip()]


def _cells(row):
    import shlex
    return list(row) if isinstance(row, (list, tuple)) else shlex.split(str(row))


# ---- creating a utility network ------------------------------------------------------------------------------------

def CreateUtilityNetwork(in_feature_dataset, in_utility_network_name, service_territory_feature_class,
                         utility_network_version="COMPATIBLE"):
    """A utility network in a feature dataset, with its structure network's five classes, as documented. The service
    territory must be a polygon class in the dataset, z- and m-enabled. INFERRED (ADR-231 §5.9 (a)): COMPATIBLE and
    CURRENT make a version 8 network, the version Pro 3.7 makes; the version is recorded and changes nothing else
    except that big integer network attributes need 7 or later. The network's associations and dirty areas are the
    classes ``<name>_Associations`` and ``<name>_DirtyAreas`` beside it."""
    from .management import _feature_dataset_of
    file, dataset = _gpkg.split(_path(in_feature_dataset))
    if not dataset or "/" in dataset or _feature_dataset_of(f"{file}/{dataset}/x") is None:
        raise ExecuteError("ERROR 000732: a utility network is created in a feature dataset (CreateFeatureDataset).")
    version = _VERSIONS.get(_word(utility_network_version, "COMPATIBLE"))
    if version is None:
        raise ExecuteError(f"ERROR 000800: '{utility_network_version}' is not a utility network version.")
    territory = _path(service_territory_feature_class)
    if ".gpkg" not in territory.lower():
        territory = f"{file}/{dataset}/{territory}"
    t_file, t_table = _gpkg.split(territory)
    if os.path.normcase(os.path.abspath(t_file)) != os.path.normcase(os.path.abspath(file)) or \
            not t_table or t_table.rsplit("/", 1)[0] != dataset:
        raise ExecuteError("ERROR 000732: the service territory feature class must exist in the input feature dataset.")
    connection = _gpkg.connect(file)
    try:
        row = connection.execute("select geometry_type_name, srs_id, z, m from gpkg_geometry_columns where "
                                 "table_name = ?", (t_table,)).fetchone()
        srid = connection.execute("select srs_id from grapy_feature_datasets where name = ?", (dataset,)).fetchone()[0]
    finally:
        connection.close()
    if row is None or "POLYGON" not in row[0].upper():
        raise ExecuteError("ERROR 000732: the service territory is an existing polygon feature class.")
    if not (row[2] and row[3]):
        raise ExecuteError("ERROR 002074: the service territory feature class must be z- and m-enabled.")
    if int(row[1]) != int(srid):
        raise ExecuteError("ERROR 000622: the feature dataset and the service territory must have the same spatial "
                           "reference.")
    _utility.create(file, dataset, str(in_utility_network_name), f"{file}/{t_table}", int(srid), version)
    return Result(f"{file}/{dataset}/{in_utility_network_name}")


def AddDomainNetwork(in_utility_network, domain_network_name, tier_definition, subnetwork_controller_type,
                     domain_network_alias_name=None):
    """A traditional domain network and its seven classes, ``<name>Device`` … ``<name>EdgeObject``, as documented."""
    definition = _word(tier_definition, "")
    controller = _word(subnetwork_controller_type, "")
    if definition not in ("HIERARCHICAL", "PARTITIONED"):
        raise ExecuteError("ERROR 000800: tier_definition is HIERARCHICAL or PARTITIONED.")
    if controller not in ("SOURCE", "SINK"):
        raise ExecuteError("ERROR 000800: subnetwork_controller_type is SOURCE or SINK.")
    _utility.add_domain(_path(in_utility_network), str(domain_network_name), definition, controller,
                        str(domain_network_alias_name) if _given(domain_network_alias_name) else None)
    return Result(_path(in_utility_network))


def AddTierGroup(in_utility_network, domain_network, name):
    """A tier group in a hierarchical domain network."""
    _utility.add_tier_group(_path(in_utility_network), str(domain_network), str(name))
    return Result(_path(in_utility_network))


def AddTier(in_utility_network, domain_network, name, rank, topology_type=None, tier_group_name=None,
            subnetwork_field_name=None):
    """A tier: in a hierarchical domain network inside a tier group, MESH by default, its subnetwork field added to the
    domain's classes the first time the group gets a tier; in a partitioned one the topology type is required and the
    subnetwork name field is ``SUBNETWORKNAME``. The topology type changes nothing in a trace, as the page says."""
    kind = _word(topology_type, "") or None
    if kind not in (None, "RADIAL", "MESH"):
        raise ExecuteError("ERROR 000800: topology_type is RADIAL or MESH.")
    _utility.add_tier(_path(in_utility_network), str(domain_network), str(name), int(rank), kind,
                      str(tier_group_name) if _given(tier_group_name) else None,
                      str(subnetwork_field_name) if _given(subnetwork_field_name) else None)
    return Result(_path(in_utility_network))


# ---- network attributes and categories -----------------------------------------------------------------------------

_ATTRIBUTE_FIELDS = {"SHORT": ("SmallInteger",), "LONG": ("Integer",), "DOUBLE": ("Double", "Single"),
                     "DATE": ("Date",), "BIGINTEGER": ("Integer",)}


def AddNetworkAttribute(in_utility_network, attribute_name, attribute_type, is_inline="NOT_INLINE",
                        is_apportionable="NOT_APPORTIONABLE", domain=None, is_overridable="NOT_OVERRIDABLE",
                        is_nullable="NULLABLE", is_substitution="NOT_SUBSTITUTION", network_attribute_to_substitute=None):
    """A network attribute, with the page's constraints: in line only for SHORT and LONG and then with a coded value
    domain of codes from 0, its bits counted against the 25 a network has; apportionable only DOUBLE; substitution only
    a LONG not in line; BIGINTEGER on version 7 or later. ``is_overridable`` is ignored, as the page says it is."""
    kind = _word(attribute_type, "")
    if kind not in _ATTRIBUTE_FIELDS:
        raise ExecuteError("ERROR 000800: attribute_type is SHORT, LONG, DOUBLE, DATE or BIGINTEGER.")
    inline = _flag(is_inline, "INLINE", "NOT_INLINE", "NOT_INLINE")
    apportionable = _flag(is_apportionable, "APPORTIONABLE", "NOT_APPORTIONABLE", "NOT_APPORTIONABLE")
    nullable = _flag(is_nullable, "NULLABLE", "NOT_NULLABLE", "NULLABLE")
    substitution = _flag(is_substitution, "SUBSTITUTION", "NOT_SUBSTITUTION", "NOT_SUBSTITUTION")
    with _utility.Network(_path(in_utility_network)) as network:
        network.require_disabled("AddNetworkAttribute")
        if network.q("select 1 from grapy_un_attribute where network = ? and lower(name) = lower(?)", network.name,
                     str(attribute_name)).fetchone():
            raise ExecuteError(f"ERROR 000258: the network attribute '{attribute_name}' exists already.")
        if kind == "BIGINTEGER" and int(network.version) < 7:
            raise ExecuteError("ERROR 002090: big integer network attributes need utility network version 7 or later.")
        if inline and kind not in ("SHORT", "LONG"):
            raise ExecuteError("ERROR 002090: only SHORT and LONG network attributes can be in line.")
        if apportionable and kind != "DOUBLE":
            raise ExecuteError("ERROR 002090: only DOUBLE network attributes can be apportionable.")
        if substitution and (kind != "LONG" or inline):
            raise ExecuteError("ERROR 002090: substitution is only for a LONG network attribute that is not in line.")
        if inline:
            if not _given(domain):
                raise ExecuteError("ERROR 000735: an in-line network attribute needs a coded value domain.")
            codes = sorted(int(float(v)) for (v,) in network.q(
                "select value from gpkg_data_column_constraints where constraint_name = ? and constraint_type = 'enum'",
                str(domain)))
            if not codes or codes != list(range(len(codes))):
                raise ExecuteError(f"ERROR 002090: '{domain}' must be a coded value domain with sequential codes from 0.")
            used = sum(_bits(network, d) for (d,) in network.q(
                "select domain_name from grapy_un_attribute where network = ? and inline = 1 and system = 0",
                network.name))
            if used + _bits(network, str(domain)) > 25:
                raise ExecuteError("ERROR 002090: the 25 bits for user-defined in-line network attributes are used up.")
        substitute = str(network_attribute_to_substitute) if _given(network_attribute_to_substitute) else None
        if substitution and substitute is None:
            raise ExecuteError("ERROR 000735: network_attribute_to_substitute is required for a substitution.")
        network.q("insert into grapy_un_attribute values (?, ?, ?, ?, ?, ?, ?, ?, ?, 0)", network.name,
                  str(attribute_name), kind, int(inline), int(apportionable), str(domain) if _given(domain) else None,
                  int(nullable), int(substitution), substitute)
    return Result(_path(in_utility_network))


def _bits(network, domain):
    count = network.q("select count(*) from gpkg_data_column_constraints where constraint_name = ? and "
                      "constraint_type = 'enum'", domain).fetchone()[0]
    return max(1, math.ceil(math.log2(count))) if count > 1 else 1


def SetNetworkAttribute(in_utility_network, network_attribute, domain_network, featureclass, field):
    """A network attribute assigned to a field of a class of the domain network: the field's type must match, a
    network attribute without nulls needs a field without nulls, and an attribute takes one field per class."""
    with _utility.Network(_path(in_utility_network)) as network:
        network.require_disabled("SetNetworkAttribute")
        row = network.q("select name, type, nullable from grapy_un_attribute where network = ? and lower(name) = "
                        "lower(?) and system = 0", network.name, str(network_attribute)).fetchone()
        if row is None:
            raise ExecuteError(f"ERROR 000732: '{network_attribute}' is not a user network attribute.")
        domain = network.domain(domain_network)[0]
        class_name = network.table(featureclass)
        if network.domain_of(class_name) != domain:
            raise ExecuteError(f"ERROR 000732: '{featureclass}' is not in the domain network '{domain}'.")
        columns = {c.name.lower(): c for c in _gpkg.describe(network.connection, class_name)[0]}
        column = columns.get(str(field).lower())
        if column is None:
            raise ExecuteError(f"ERROR 000728: the field '{field}' does not exist.")
        if column.type not in _ATTRIBUTE_FIELDS[row[1]]:
            raise ExecuteError(f"ERROR 002091: the field '{field}' is {column.type}; the network attribute is {row[1]}.")
        if not row[2] and column.nullable:
            raise ExecuteError("ERROR 002091: a network attribute that does not allow nulls needs a field that does not.")
        if network.q("select 1 from grapy_un_attribute_field where network = ? and attribute = ? and class_name = ?",
                     network.name, row[0], class_name).fetchone():
            raise ExecuteError(f"ERROR 002091: '{row[0]}' is assigned to {class_name.rsplit('/', 1)[-1]} already.")
        network.q("insert into grapy_un_attribute_field values (?, ?, ?, ?)", network.name, row[0], class_name,
                  column.name)
    return Result(_path(in_utility_network))


def AddNetworkCategory(in_utility_network, category_name):
    """A network category, to be assigned to asset types with SetNetworkCategory."""
    with _utility.Network(_path(in_utility_network)) as network:
        network.require_disabled("AddNetworkCategory")
        if network.q("select 1 from grapy_un_category where network = ? and lower(name) = lower(?)", network.name,
                     str(category_name)).fetchone():
            raise ExecuteError(f"ERROR 000258: the network category '{category_name}' exists already.")
        network.q("insert into grapy_un_category values (?, ?, 0)", network.name, str(category_name))
    return Result(_path(in_utility_network))


def SetNetworkCategory(in_utility_network, domain_network, featureclass, assetgroup, assettype, category=None):
    """The categories of an asset type replaced by those named; none named unassigns them all. Subnetwork Controller
    only on a device or junction object of a traditional domain network, and in a partitioned one only on an asset type
    whose terminal configuration is directional with an upstream and a downstream terminal, as the page says."""
    with _utility.Network(_path(in_utility_network)) as network:
        network.require_disabled("SetNetworkCategory")
        domain = network.domain(domain_network)
        class_name = network.table(featureclass)
        group = network.asset_group(class_name, assetgroup)
        kind = network.asset_type(class_name, group, assettype)
        names = []
        for each in _rows(category):
            row = network.q("select name from grapy_un_category where network = ? and lower(name) = lower(?)",
                            network.name, str(each).strip("'\"")).fetchone()
            if row is None:
                raise ExecuteError(f"ERROR 000732: '{each}' is not a network category.")
            names.append(row[0])
        if "Subnetwork Controller" in names:
            if network.role(class_name) not in ("Device", "JunctionObject") or domain[2] != "Traditional":
                raise ExecuteError("ERROR 002092: Subnetwork Controller is only for the device class and junction "
                                   "object table of a traditional domain network.")
            if domain[3] == "PARTITIONED":
                config = network.terminal_config(class_name, group, kind)
                if config is None or config[2] != "DIRECTIONAL" or not any(t[2] for t in config[3]) or \
                        all(t[2] for t in config[3]):
                    raise ExecuteError("ERROR 002092: in a partitioned domain network a subnetwork controller's asset "
                                       "type needs a directional terminal configuration with an upstream and a "
                                       "downstream terminal.")
        network.q("delete from grapy_un_category_asset where network = ? and class_name = ? and asset_group = ? and "
                  "asset_type = ?", network.name, class_name, group, kind)
        for name in names:
            network.q("insert into grapy_un_category_asset values (?, ?, ?, ?, ?)", network.name, name, class_name,
                      group, kind)
    return Result(_path(in_utility_network))


def DeleteNetworkCategory(in_utility_network, category_name):
    """A user network category deleted; not one assigned to an asset type, nor a system category."""
    with _utility.Network(_path(in_utility_network)) as network:
        network.require_disabled("DeleteNetworkCategory")
        row = network.q("select name, system from grapy_un_category where network = ? and lower(name) = lower(?)",
                        network.name, str(category_name)).fetchone()
        if row is None:
            raise ExecuteError(f"ERROR 000732: '{category_name}' is not a network category.")
        if row[1]:
            raise ExecuteError(f"ERROR 002093: '{row[0]}' is a system-provided network category; it cannot be deleted.")
        if network.q("select 1 from grapy_un_category_asset where network = ? and category = ?", network.name,
                     row[0]).fetchone():
            raise ExecuteError(f"ERROR 002093: '{row[0]}' is assigned to an asset type; unassign it with "
                               "SetNetworkCategory first.")
        network.q("delete from grapy_un_category where network = ? and name = ?", network.name, row[0])
    return Result(_path(in_utility_network))


# ---- terminal configurations ---------------------------------------------------------------------------------------

def AddTerminalConfiguration(in_utility_network, terminal_configuration_name, traversability_model,
                             terminals_directional=None, terminals_bidirectional=None, valid_paths=None,
                             default_path=None):
    """A terminal configuration: two to eight terminals of at most 32 characters, numbered 1, 2 … in the order given
    (INFERRED, ADR-231 §5.9 (b)); valid paths named, each All, None or terminal pairs ``A-B, A-C``; required for a
    bidirectional configuration of three or four terminals. A directional configuration needs an upstream terminal.
    The default path is a path's name, ALL or NONE, ALL when none is given."""
    model = _word(traversability_model, "")
    if model not in ("DIRECTIONAL", "BIDIRECTIONAL"):
        raise ExecuteError("ERROR 000800: traversability_model is DIRECTIONAL or BIDIRECTIONAL.")
    if model == "DIRECTIONAL":
        if not _given(terminals_directional):
            raise ExecuteError("ERROR 000735: terminals_directional is required for a DIRECTIONAL configuration.")
        terminals = []
        for row in _rows(terminals_directional):
            cells = _cells(row)
            up = cells[1] if len(cells) > 1 else True
            terminals.append((str(cells[0]), up is True or str(up).strip().lower() in ("true", "1", "upstream")))
        if not any(up for _, up in terminals):
            raise ExecuteError("ERROR 002096: a directional configuration needs an upstream terminal.")
    else:
        if not _given(terminals_bidirectional):
            raise ExecuteError("ERROR 000735: terminals_bidirectional is required for a BIDIRECTIONAL configuration.")
        terminals = [(str(_cells(row)[0]) if isinstance(row, (list, tuple)) else str(row).strip("'\" "), False)
                     for row in _rows(terminals_bidirectional)]
    if not 2 <= len(terminals) <= 8:
        raise ExecuteError("ERROR 002096: a terminal configuration has from two to eight terminals.")
    names = [n for n, _ in terminals]
    if any(len(n) > 32 for n in names) or len({n.lower() for n in names}) != len(names):
        raise ExecuteError("ERROR 002096: terminal names are unique and at most 32 characters.")
    ids = {n.lower(): i + 1 for i, n in enumerate(names)}
    paths = {}
    for row in _rows(valid_paths):
        cells = _cells(row)
        if len(cells) < 2:
            raise ExecuteError(f"ERROR 000800: a valid path is a name and a value: '{row}'.")
        name, value = str(cells[0]), " ".join(str(c) for c in cells[1:])
        if value.strip().upper() in ("ALL", "NONE"):
            paths[name] = value.strip().upper()
            continue
        pairs = []
        for pair in value.replace(";", ",").split(","):
            ends = [p.strip() for p in pair.split("-")]
            if len(ends) != 2 or ends[0].lower() not in ids or ends[1].lower() not in ids:
                raise ExecuteError(f"ERROR 002096: '{pair.strip()}' is not a pair of this configuration's terminals.")
            pairs.append((ids[ends[0].lower()], ids[ends[1].lower()]))
        paths[name] = pairs
    if paths and len(terminals) > 4:
        raise ExecuteError("ERROR 002096: valid paths are defined for configurations of three or four terminals.")
    if model == "BIDIRECTIONAL" and len(terminals) in (3, 4) and not paths:
        raise ExecuteError("ERROR 000735: valid_paths is required for a bidirectional configuration of three or four "
                           "terminals.")
    default = str(default_path).strip() if _given(default_path) else "ALL"
    if default.upper() in ("ALL", "NONE"):
        default = default.upper()
    elif default not in paths:
        raise ExecuteError(f"ERROR 002096: the default path '{default}' is not a valid path of the configuration.")
    with _utility.Network(_path(in_utility_network)) as network:
        network.require_disabled("AddTerminalConfiguration")
        if network.q("select 1 from grapy_un_terminal_config where network = ? and lower(name) = lower(?)",
                     network.name, str(terminal_configuration_name)).fetchone():
            raise ExecuteError(f"ERROR 000258: the terminal configuration '{terminal_configuration_name}' exists.")
        new_id = network.q("select coalesce(max(id), 0) + 1 from grapy_un_terminal_config where network = ?",
                           network.name).fetchone()[0]
        network.q("insert into grapy_un_terminal_config values (?, ?, ?, ?, ?, ?, ?)", network.name, new_id,
                  str(terminal_configuration_name), model,
                  json.dumps([[i + 1, n, bool(up)] for i, (n, up) in enumerate(terminals)]), json.dumps(paths), default)
    return Result(_path(in_utility_network))


def SetTerminalConfiguration(in_utility_network, domain_network, device_featureclass, assetgroup, assettype,
                             terminal_configuration):
    """A terminal configuration assigned to an asset type of a device class or junction object table — permanent once
    the network topology has been enabled, as the page cautions."""
    with _utility.Network(_path(in_utility_network)) as network:
        network.require_disabled("SetTerminalConfiguration")
        if network.ever_enabled:
            raise ExecuteError("ERROR 002097: a terminal configuration cannot be assigned or changed after the network "
                               "topology has been enabled for the first time.")
        network.domain(domain_network)
        class_name = network.table(device_featureclass)
        if network.role(class_name) not in ("Device", "JunctionObject"):
            raise ExecuteError("ERROR 002097: terminal configurations are assigned to asset types of the device class "
                               "or junction object table.")
        group = network.asset_group(class_name, assetgroup)
        kind = network.asset_type(class_name, group, assettype)
        row = network.q("select id from grapy_un_terminal_config where network = ? and lower(name) = lower(?)",
                        network.name, str(terminal_configuration)).fetchone()
        if row is None:
            raise ExecuteError(f"ERROR 000732: '{terminal_configuration}' is not a terminal configuration.")
        network.q("insert or replace into grapy_un_terminal_asset values (?, ?, ?, ?, ?)", network.name, class_name, group,
                  kind, row[0])
    return Result(_path(in_utility_network))


def DeleteTerminalConfiguration(in_utility_network, terminal_configuration_name):
    """A terminal configuration deleted — only one assigned to no asset type, and only before the network topology is
    first enabled."""
    with _utility.Network(_path(in_utility_network)) as network:
        network.require_disabled("DeleteTerminalConfiguration")
        if network.ever_enabled:
            raise ExecuteError("ERROR 002097: terminal configurations cannot be deleted once the network topology has "
                               "been enabled.")
        row = network.q("select id from grapy_un_terminal_config where network = ? and lower(name) = lower(?)",
                        network.name, str(terminal_configuration_name)).fetchone()
        if row is None:
            raise ExecuteError(f"ERROR 000732: '{terminal_configuration_name}' is not a terminal configuration.")
        if network.q("select 1 from grapy_un_terminal_asset where network = ? and config_id = ?", network.name,
                     row[0]).fetchone():
            raise ExecuteError("ERROR 002097: the terminal configuration is assigned to an asset type.")
        network.q("delete from grapy_un_terminal_config where network = ? and id = ?", network.name, row[0])
    return Result(_path(in_utility_network))


# ---- rules ---------------------------------------------------------------------------------------------------------

def _end(network, cls, group, kind, terminal, rule_type, needs_terminal=True):
    class_name = network.table(cls)
    g = network.asset_group(class_name, group)
    t = network.asset_type(class_name, g, kind)
    term = None
    if rule_type not in ("CONTAINMENT", "STRUCTURAL_ATTACHMENT"):
        config = network.terminal_config(class_name, g, t)
        if config is not None and needs_terminal and network.role(class_name) in ("Device", "JunctionObject"):
            if not _given(terminal):
                raise ExecuteError(f"ERROR 000735: a terminal is required for {class_name.rsplit('/', 1)[-1]} {group} "
                                   f"{kind}, whose asset type has terminals.")
        term = network.terminal_id(class_name, g, t, terminal) if config else None
    return class_name, g, t, term


def AddRule(in_utility_network, rule_type, from_class, from_assetgroup, from_assettype, to_class, to_assetgroup,
            to_assettype, from_terminal=None, to_terminal=None, via_class=None, via_assetgroup=None, via_assettype=None,
            via_terminal=None):
    """A network rule between two asset types (and, for edge-junction-edge, a junction between them), respecting the
    feature restrictions the page and the network rules page state. A terminal is required on an asset type with
    terminals, and ignored for containment and structural attachment."""
    kind = _word(rule_type, "")
    if kind not in _utility.RULE_TYPES:
        raise ExecuteError(f"ERROR 000800: '{rule_type}' is not a rule type.")
    with _utility.Network(_path(in_utility_network)) as network:
        a = _end(network, from_class, from_assetgroup, from_assettype, from_terminal, kind)
        b = _end(network, to_class, to_assetgroup, to_assettype, to_terminal, kind)
        via = None
        if kind == "EDGE_JUNCTION_EDGE_CONNECTIVITY":
            if not _given(via_class):
                raise ExecuteError("ERROR 000735: via_class is required for an edge-junction-edge rule.")
            via = _end(network, via_class, via_assetgroup, via_assettype, via_terminal, kind)
        elif _given(via_class):
            raise ExecuteError("ERROR 000800: via_class is only for an EDGE_JUNCTION_EDGE_CONNECTIVITY rule.")
        _utility.add_rule(network, kind, (a, b), via)
    return Result(_path(in_utility_network))


def _describe_rule(network, rule):
    """A rule as DeleteRule's page shows one: ``<id>: From[Class.Group.Type(.Terminal)] To[…]``."""
    def side(prefix, word):
        cls = rule[f"{prefix}_class"]
        group, kind = network.asset_names(cls, rule[f"{prefix}_group"], rule[f"{prefix}_type"])
        text = f"{cls.rsplit('/', 1)[-1]}.{group}.{kind}"
        if rule[f"{prefix}_terminal"] is not None:
            text += "." + network.terminal_name(cls, rule[f"{prefix}_group"], rule[f"{prefix}_type"],
                                                rule[f"{prefix}_terminal"])
        return f"{word}[{text}]"
    text = f"{rule['id']}: {side('from', 'From')} {side('to', 'To')}"
    if rule["via_class"]:
        text += " " + side("via", "Via")
    return text


def DeleteRule(in_utility_network, rule_type, rule_desc):
    """Rules deleted by the id that begins each description (``"2: From[…] To[…]"``), checked to be of the type named.
    INFERRED: ``ALL`` with no description deletes every rule, with descriptions those named of any type."""
    kind = _word(rule_type, "")
    if kind != "ALL" and kind not in _utility.RULE_TYPES:
        raise ExecuteError(f"ERROR 000800: '{rule_type}' is not a rule type.")
    with _utility.Network(_path(in_utility_network)) as network:
        network.require_disabled("DeleteRule")
        existing = {r["id"]: r for r in _utility.rules(network)}
        named = _rows(rule_desc)
        if not named and kind == "ALL":
            network.q("delete from grapy_un_rule where network = ?", network.name)
            return Result(_path(in_utility_network))
        for each in named:
            text = str(each).strip("'\" ")
            try:
                rule_id = int(text.split(":", 1)[0])
            except ValueError:
                raise ExecuteError(f"ERROR 000800: '{each}' does not begin with a rule ID.") from None
            if rule_id not in existing or (kind != "ALL" and existing[rule_id]["type"] != kind):
                raise ExecuteError(f"ERROR 000732: there is no {kind} rule {rule_id}.")
            network.q("delete from grapy_un_rule where network = ? and id = ?", network.name, rule_id)
    return Result(_path(in_utility_network))


_RULE_COLUMNS = ("RULETYPE", "FROMFEATURECLASS", "FROMASSETGROUP", "FROMASSETTYPE", "FROMTERMINAL", "TOFEATURECLASS",
                 "TOASSETGROUP", "TOASSETTYPE", "TOTERMINAL", "VIAFEATURECLASS", "VIAASSETGROUP", "VIAASSETTYPE",
                 "VIATERMINAL")


def _csv_rows(path):
    with open(_path(path), newline="", encoding="utf-8-sig") as read:
        return [{(k or "").strip().upper(): (v or "").strip() for k, v in row.items()} for row in csv.DictReader(read)]


def _rule_word(text):
    found = next((k for k, v in _utility.RULE_TYPES.items() if v.lower() == text.strip().lower() or
                  k == text.strip().upper()), None)
    if found is None:
        raise ExecuteError(f"ERROR 000800: '{text}' is not a rule type.")
    return found


def ImportRules(in_utility_network, rule_type, csv_file):
    """Rules from a .csv file of the documented columns; ``*`` as a terminal is every terminal. INFERRED: a rule the
    network has already is skipped."""
    kind = _word(rule_type, "ALL")
    with _utility.Network(_path(in_utility_network)) as network:
        network.require_disabled("ImportRules")
        for row in _csv_rows(csv_file):
            rule = _rule_word(row.get("RULETYPE", ""))
            if kind != "ALL" and rule != kind:
                continue
            a = _end(network, row["FROMFEATURECLASS"], row["FROMASSETGROUP"], row["FROMASSETTYPE"],
                     row.get("FROMTERMINAL"), rule, needs_terminal=False)
            b = _end(network, row["TOFEATURECLASS"], row["TOASSETGROUP"], row["TOASSETTYPE"], row.get("TOTERMINAL"),
                     rule, needs_terminal=False)
            via = None
            if rule == "EDGE_JUNCTION_EDGE_CONNECTIVITY":
                via = _end(network, row["VIAFEATURECLASS"], row["VIAASSETGROUP"], row["VIAASSETTYPE"],
                           row.get("VIATERMINAL"), rule, needs_terminal=False)
            try:
                _utility.add_rule(network, rule, (a, b), via)
            except ExecuteError as said:
                if "exists already" not in str(said):
                    raise
    return Result(_path(in_utility_network))


def ExportRules(in_utility_network, rule_type, out_csv_file):
    """The network's rules of a type written to a .csv file of the documented columns, names rather than codes; a
    rule on every terminal of an asset type with terminals writes ``*``."""
    kind = _word(rule_type, "ALL")
    with _utility.Network(_path(in_utility_network)) as network:
        lines = []
        for rule in _utility.rules(network):
            if kind != "ALL" and rule["type"] != kind:
                continue
            row = [_utility.RULE_TYPES[rule["type"]]]
            for prefix in ("from", "to", "via"):
                cls = rule[f"{prefix}_class"]
                if cls is None:
                    row += ["", "", "", ""]
                    continue
                group, asset = network.asset_names(cls, rule[f"{prefix}_group"], rule[f"{prefix}_type"])
                config = network.terminal_config(cls, rule[f"{prefix}_group"], rule[f"{prefix}_type"])
                if rule["type"] in ("CONTAINMENT", "STRUCTURAL_ATTACHMENT"):
                    terminal = ""
                elif rule[f"{prefix}_terminal"] is not None:
                    terminal = network.terminal_name(cls, rule[f"{prefix}_group"], rule[f"{prefix}_type"],
                                                     rule[f"{prefix}_terminal"])
                else:
                    terminal = "*" if config else "Single Terminal"
                row += [cls.rsplit("/", 1)[-1], group, asset, terminal]
            lines.append(row)
    with open(_path(out_csv_file), "w", newline="", encoding="utf-8") as written:
        out = csv.writer(written)
        out.writerow(_RULE_COLUMNS)
        out.writerows(lines)
    return Result(_path(out_csv_file))


# ---- associations --------------------------------------------------------------------------------------------------

_ASSOCIATION_COLUMNS = ("ASSOCIATIONTYPE", "FROMFEATURECLASS", "FROMASSETGROUP", "FROMASSETTYPE", "FROMGLOBALID",
                        "FROMTERMINAL", "TOFEATURECLASS", "TOASSETGROUP", "TOASSETTYPE", "TOGLOBALID", "TOTERMINAL",
                        "ISCONTENTVISIBLE", "PERCENTALONG")


def _association_code(text):
    for code, (words, keyword) in _utility.ASSOCIATION_TYPES.items():
        if text.strip().lower() in (words.lower(), keyword.lower()):
            return code
    raise ExecuteError(f"ERROR 000800: '{text}' is not an association type.")


def ImportAssociations(in_utility_network, association_type, csv_file):
    """Associations from a .csv file of the documented columns, each feature found by global ID and each association
    checked against the rules. The asset group and type columns are checked against the features' own."""
    kind = _word(association_type, "ALL")
    with _utility.Network(_path(in_utility_network)) as network:
        for row in _csv_rows(csv_file):
            code = _association_code(row.get("ASSOCIATIONTYPE", ""))
            if kind != "ALL" and _utility.ASSOCIATION_TYPES[code][1] != kind:
                continue
            ends = []
            for side in ("FROM", "TO"):
                class_name = network.table(row[f"{side}FEATURECLASS"])
                _, group, asset = network.feature_by_globalid(class_name, row[f"{side}GLOBALID"])
                if _given(row.get(f"{side}ASSETGROUP")) and network.asset_group(class_name, row[f"{side}ASSETGROUP"]) \
                        != group:
                    raise ExecuteError(f"ERROR 002095: {row[f'{side}GLOBALID']} is not in the asset group "
                                       f"'{row[f'{side}ASSETGROUP']}'.")
                terminal = network.terminal_id(class_name, group, asset, row.get(f"{side}TERMINAL")) \
                    if code in (1, 4, 5, 6) else None
                ends.append((class_name, row[f"{side}GLOBALID"], terminal))
            visible = str(row.get("ISCONTENTVISIBLE", "")).strip().lower() in ("true", "1")
            percent = row.get("PERCENTALONG") or None
            _utility.add_association(network, code, *ends[0], *ends[1], visible=visible,
                                     percent=float(percent) if percent else None)
    return Result(_path(in_utility_network))


def ExportAssociations(in_utility_network, association_type, out_csv_file):
    """The network's associations of a type written to a .csv file of the documented columns."""
    kind = _word(association_type, "ALL")
    with _utility.Network(_path(in_utility_network)) as network:
        sources = network.sources()
        lines = []
        for row in _utility.association_rows(network):
            code = int(row["type"])
            if kind != "ALL" and _utility.ASSOCIATION_TYPES[code][1] != kind:
                continue
            out = [_utility.ASSOCIATION_TYPES[code][0]]
            for side in ("from", "to"):
                class_name = sources[row[f"{side}_source"]][0]
                _, group, asset = network.feature_by_globalid(class_name, row[f"{side}_globalid"])
                names = network.asset_names(class_name, group, asset)
                terminal = network.terminal_name(class_name, group, asset, row[f"{side}_terminal"]) \
                    if code in (1, 4, 5, 6) else ""
                out += [class_name.rsplit("/", 1)[-1], names[0], names[1], row[f"{side}_globalid"], terminal]
            out += ["True" if row["visible"] else "False" if code == 2 else "",
                    "" if row["percent"] is None else repr(float(row["percent"]))]
            lines.append(out)
    with open(_path(out_csv_file), "w", newline="", encoding="utf-8") as written:
        out_csv = csv.writer(written)
        out_csv.writerow(_ASSOCIATION_COLUMNS)
        out_csv.writerows(lines)
    return Result(_path(out_csv_file))


# ---- subnetwork controllers ----------------------------------------------------------------------------------------

_CONTROLLER_COLUMNS = ("SUBNETWORKCONTROLLERNAME", "FEATUREGLOBALID", "FEATURECLASSNAME", "FEATUREASSETGROUP",
                       "FEATUREASSETTYPE", "FEATURETERMINAL", "TIERNAME", "SUBNETWORKNAME", "DESCRIPTION", "NOTES")


def _enable_controller(network, class_name, globalid, terminal_name, controller_name, subnetwork_name, tier_name,
                       description=None, notes=None):
    """A terminal set as a subnetwork controller, with the prerequisites the pages give: the Subnetwork Controller
    category on its asset type, a tier of its domain network, a terminal on an asset type with terminals — in a
    partitioned network a downstream terminal for a source and an upstream one for a sink — and a controller name
    unique in the tier."""
    oid, group, kind = network.feature_by_globalid(class_name, globalid)
    if network.role(class_name) not in ("Device", "JunctionObject"):
        raise ExecuteError("ERROR 002083: only devices and junction objects can be subnetwork controllers.")
    if not network.q("select 1 from grapy_un_category_asset where network = ? and category = 'Subnetwork Controller' "
                     "and class_name = ? and asset_group = ? and asset_type = ?", network.name, class_name, group,
                     kind).fetchone():
        raise ExecuteError("ERROR 002083: the asset type is not assigned the Subnetwork Controller network category.")
    domain = network.domain(network.domain_of(class_name))
    tier = network.tier(tier_name)
    if tier[0] != domain[0]:
        raise ExecuteError(f"ERROR 002083: the tier '{tier_name}' is not in '{domain[0]}'.")
    config = network.terminal_config(class_name, group, kind)
    terminal = network.terminal_id(class_name, group, kind, terminal_name)
    if config is not None:
        if terminal is None:
            raise ExecuteError("ERROR 002083: name the terminal that is the subnetwork controller.")
        if domain[3] == "PARTITIONED":
            upstream = next(t[2] for t in config[3] if t[0] == terminal)
            if upstream == (domain[4] == "SOURCE"):
                raise ExecuteError("ERROR 002083: in a partitioned domain network a source is a downstream terminal and "
                                   "a sink an upstream one.")
    globalid = str(globalid).upper()
    for other_class, other_id, other_terminal in network.q(
            "select class_name, globalid, terminal_id from grapy_un_controller where network = ? and lower(tier) = "
            "lower(?) and lower(controller_name) = lower(?)", network.name, tier[1], str(controller_name)).fetchall():
        if (other_class, other_id, other_terminal) != (class_name, globalid, terminal):
            raise ExecuteError(f"ERROR 002083: the controller name '{controller_name}' is taken in the tier '{tier[1]}'.")
    network.q("delete from grapy_un_controller where network = ? and class_name = ? and globalid = ? and terminal_id is ?",
              network.name, class_name, globalid, terminal)
    network.q("insert into grapy_un_controller values (?, ?, ?, ?, ?, ?, ?, ?, ?)", network.name, class_name, globalid,
              terminal, str(controller_name), tier[1], str(subnetwork_name), description, notes)
    network.q(f'update "{class_name}" set ISSUBNETWORKCONTROLLER = 1, SUBNETWORKCONTROLLERNAME = ?, TIERNAME = ?, '
              f'TIERRANK = ? where upper(GLOBALID) = ?', str(controller_name), tier[1], tier[2], globalid)


def _disable_controller(network, class_name, globalid, terminal_name):
    oid, group, kind = network.feature_by_globalid(class_name, globalid)
    terminal = network.terminal_id(class_name, group, kind, terminal_name)
    globalid = str(globalid).upper()
    gone = network.q("delete from grapy_un_controller where network = ? and class_name = ? and globalid = ? and "
                     "terminal_id is ?", network.name, class_name, globalid, terminal).rowcount
    if not gone:
        raise ExecuteError("ERROR 002083: that terminal is not a subnetwork controller.")
    if not network.q("select 1 from grapy_un_controller where network = ? and class_name = ? and globalid = ?",
                     network.name, class_name, globalid).fetchone():
        network.q(f'update "{class_name}" set ISSUBNETWORKCONTROLLER = 0, SUBNETWORKCONTROLLERNAME = null, '
                  'TIERNAME = null, TIERRANK = null where upper(GLOBALID) = ?', globalid)


def ImportSubnetworkControllers(in_utility_network, csv_file):
    """Subnetwork controllers set from a .csv file of the documented columns. INFERRED (ADR-231 §5.9 (e)): a
    controller takes effect in traces at once, without a validate."""
    with _utility.Network(_path(in_utility_network)) as network:
        for row in _csv_rows(csv_file):
            class_name = network.table(row["FEATURECLASSNAME"])
            _enable_controller(network, class_name, row["FEATUREGLOBALID"], row.get("FEATURETERMINAL"),
                               row["SUBNETWORKCONTROLLERNAME"], row["SUBNETWORKNAME"], row["TIERNAME"],
                               row.get("DESCRIPTION") or None, row.get("NOTES") or None)
    return Result(_path(in_utility_network))


def ExportSubnetworkControllers(in_utility_network, out_csv_file):
    """The network's subnetwork controllers written to a .csv file of the documented columns; one or more must exist."""
    with _utility.Network(_path(in_utility_network)) as network:
        rows = network.q("select class_name, globalid, terminal_id, controller_name, tier, subnetwork_name, description, "
                         "notes from grapy_un_controller where network = ? order by tier, controller_name",
                         network.name).fetchall()
        if not rows:
            raise ExecuteError("ERROR 002084: the utility network has no subnetwork controllers.")
        lines = []
        for class_name, globalid, terminal, name, tier, subnetwork, description, notes in rows:
            _, group, kind = network.feature_by_globalid(class_name, globalid)
            names = network.asset_names(class_name, group, kind)
            lines.append([name, globalid, class_name.rsplit("/", 1)[-1], names[0], names[1],
                          network.terminal_name(class_name, group, kind, terminal), tier, subnetwork, description or "",
                          notes or ""])
    with open(_path(out_csv_file), "w", newline="", encoding="utf-8") as written:
        out = csv.writer(written)
        out.writerow(_CONTROLLER_COLUMNS)
        out.writerows(lines)
    return Result(_path(out_csv_file))


class UtilityNetwork:
    """arcpy.un.UtilityNetwork: associations and subnetwork controllers of one utility network, by its documented
    methods. Each change is saved when the method returns (grapy has no edit session to hold it, INFERRED)."""

    def __init__(self, utilityNetworkPath):
        _utility.Network(_path(utilityNetworkPath)).connection.close()
        self._path = _path(utilityNetworkPath)

    def AddConnectivityAssociation(self, association_type, from_table, from_global_id, from_terminal_name, to_table,
                                   to_global_id, to_terminal_name, percent_along=None):
        """A connectivity association (1 junction-junction, 4 junction-edge from, 5 midspan, 6 to); its object ID."""
        if int(association_type) not in (1, 4, 5, 6):
            raise ExecuteError("ERROR 000800: association_type is 1, 4, 5 or 6.")
        with _utility.Network(self._path) as network:
            a, b = network.table(from_table), network.table(to_table)
            _, fg, ft = network.feature_by_globalid(a, from_global_id)
            _, tg, tt = network.feature_by_globalid(b, to_global_id)
            return _utility.add_association(
                network, int(association_type), a, from_global_id, network.terminal_id(a, fg, ft, from_terminal_name),
                b, to_global_id, network.terminal_id(b, tg, tt, to_terminal_name),
                percent=percent_along if int(association_type) == 5 else None)

    def AddContainmentAssociation(self, container_table, container_global_id, content_table, content_global_id,
                                  is_content_visible=False):
        with _utility.Network(self._path) as network:
            return _utility.add_association(network, 2, network.table(container_table), container_global_id, None,
                                            network.table(content_table), content_global_id, None,
                                            visible=bool(is_content_visible))

    def AddStructuralAttachmentAssociation(self, structure_table, structure_global_id, attachment_table,
                                           attachment_global_id):
        with _utility.Network(self._path) as network:
            return _utility.add_association(network, 3, network.table(structure_table), structure_global_id, None,
                                            network.table(attachment_table), attachment_global_id, None)

    def DeleteConnectivityAssociation(self, association_type, association_global_id):
        with _utility.Network(self._path) as network:
            _utility.delete_association(network, association_global_id, {int(association_type)})

    def DeleteContainmentAssociation(self, association_global_id):
        with _utility.Network(self._path) as network:
            _utility.delete_association(network, association_global_id, {2})

    def DeleteStructualAttachmentAssociation(self, association_global_id):
        """Spelled as arcpy spells it."""
        with _utility.Network(self._path) as network:
            _utility.delete_association(network, association_global_id, {3})

    def EnableSubnetworkController(self, table, global_id, terminal_name, subnetwork_controller_name, subnetwork_name,
                                   tier_name, description=None, notes=None):
        with _utility.Network(self._path) as network:
            _enable_controller(network, network.table(table), global_id, terminal_name, subnetwork_controller_name,
                               subnetwork_name, tier_name, description, notes)

    def DisableSubnetworkController(self, table, global_id, terminal_name):
        with _utility.Network(self._path) as network:
            _disable_controller(network, network.table(table), global_id, terminal_name)

    def HasValidNetworkTopology(self):
        """Whether the network topology is enabled."""
        with _utility.Network(self._path) as network:
            return network.enabled


# ---- the network topology ------------------------------------------------------------------------------------------

def EnableNetworkTopology(in_utility_network, max_number_of_errors=10000, only_generate_errors="ENABLE_TOPO"):
    """The network topology built from every feature and association and enabled; the errors it finds written as
    error dirty areas. Rules must exist. ``ONLY_ERRORS`` writes the errors and leaves the topology disabled. INFERRED
    (ADR-231 §5.9 (d)): when more errors are found than ``max_number_of_errors`` (-1 or none: unbounded) the first so
    many are written and the topology is not enabled."""
    only = _word(only_generate_errors, "ENABLE_TOPO")
    if only not in ("ENABLE_TOPO", "ONLY_ERRORS"):
        raise ExecuteError("ERROR 000800: only_generate_errors is ONLY_ERRORS or ENABLE_TOPO.")
    limit = int(max_number_of_errors) if _given(max_number_of_errors) else -1
    with _utility.Network(_path(in_utility_network)) as network:
        if not _utility.rules(network):
            raise ExecuteError("ERROR 002085: there must be rules to enable the network topology.")
        graph = _utility.refresh(network, max_errors=limit)
        over = 0 <= limit < len(graph.errors)
        enable = only == "ENABLE_TOPO" and not over
        network.q("update grapy_un set enabled = ?, ever_enabled = max(ever_enabled, ?) where name = ?", int(enable),
                  int(enable), network.name)
        network.enabled = enable
        _utility.write_dirty_areas(network)
    if over and only == "ENABLE_TOPO":
        raise ExecuteError(f"ERROR 002086: more than {limit} errors were found; the network topology was not enabled.")
    return Result(_path(in_utility_network))


def DisableNetworkTopology(in_utility_network):
    """The network topology disabled: one dirty area over the service territory, the error areas kept."""
    with _utility.Network(_path(in_utility_network)) as network:
        network.q("update grapy_un set enabled = 0 where name = ?", network.name)
        network.enabled = False
        _utility.write_dirty_areas(network)
    return Result(_path(in_utility_network))


def _extent(value):
    if not _given(value) or str(value).upper() in ("DEFAULT", "MAXOF"):
        return None
    if hasattr(value, "XMin"):
        return value.XMin, value.YMin, value.XMax, value.YMax
    if str(value).upper() in ("MINOF", "DISPLAY"):
        raise NotBuiltError(f"arcpy.un.ValidateNetworkTopology: the extent '{value}' is not built in grapy: there is no "
                            "display, and one input has no common minimum; give coordinates or an Extent.")
    parts = str(value).split()
    if len(parts) >= 4:
        return tuple(float(p) for p in parts[:4])
    try:
        connection, table = _gpkg.table_of(str(value))
    except Exception:
        raise ExecuteError(f"ERROR 000800: '{value}' is not an extent.") from None
    try:
        shapes = [_gpkg.decode(r[0]) for r in connection.execute(f'select geom from "{table}"')]
    finally:
        connection.close()
    return shapely.unary_union([s for s in shapes if s is not None]).bounds


def ValidateNetworkTopology(in_utility_network, extent=None):
    """The features and associations whose dirty areas intersect the extent (all of them with none) brought into the
    network topology, and the errors found again. INFERRED (ADR-231 §5.9 (d)): a feature is validated whole when its
    dirty area meets the extent — dirty areas are not clipped. Outputs the network and the discovered subnetworks as
    JSON, which stays empty until subnetworks are kept (UpdateSubnetwork, a later batch)."""
    with _utility.Network(_path(in_utility_network)) as network:
        network.require_enabled("ValidateNetworkTopology")
        _utility.refresh(network, extent=_extent(extent))
        _utility.write_dirty_areas(network)
    return Result(_path(in_utility_network), json.dumps({"discoveredSubnetworks": []}))


# ---- tracing -------------------------------------------------------------------------------------------------------

_TRACE_TYPES = ("CONNECTED", "SUBNETWORK", "SUBNETWORK_CONTROLLERS", "UPSTREAM", "DOWNSTREAM", "ISOLATION")
_LATER = "is in a later batch of ADR-231 (§5.8)"


def _locations(value):
    """(FEATUREGLOBALID, TERMINALID, PERCENTALONG) rows of a starting point, barrier or stopping point class or table."""
    if not _given(value):
        return []
    from . import da
    from .management import _source
    path, _ = _source(value)
    connection, table = _gpkg.table_of(path)
    try:
        names = {c.name.upper(): c.name for c in _gpkg.describe(connection, table)[0]}
    finally:
        connection.close()
    if "FEATUREGLOBALID" not in names:
        raise ExecuteError(f"ERROR 002077: '{value}' has no FEATUREGLOBALID field.")
    fields = [names["FEATUREGLOBALID"], names.get("TERMINALID"), names.get("PERCENTALONG")]
    wanted = [f for f in fields if f]
    out = []
    with da.SearchCursor(value, wanted) as read:
        for row in read:
            values = dict(zip(wanted, row))
            terminal = values.get(fields[1]) if fields[1] else None
            percent = values.get(fields[2]) if fields[2] else None
            if percent is not None and not 0 <= float(percent) <= 1:
                percent = None
            out.append((values[fields[0]], None if terminal in (None, "") else int(terminal), percent))
    return out


@equivalent(*_utility.FLOW)
def Trace(in_utility_network, trace_type="CONNECTED", starting_points=None, barriers=None, domain_network=None,
          tier=None, target_tier=None, subnetwork_name=None, shortest_path_network_attribute_name=None,
          include_containers="EXCLUDE_CONTAINERS", include_content="EXCLUDE_CONTENT",
          include_structures="EXCLUDE_STRUCTURES", include_barriers="INCLUDE_BARRIERS",
          validate_consistency="DO_NOT_VALIDATE_CONSISTENCY", condition_barriers=None, function_barriers=None,
          traversability_scope="BOTH_JUNCTIONS_AND_EDGES", filter_barriers=None, filter_function_barriers=None,
          filter_scope="BOTH_JUNCTIONS_AND_EDGES", filter_bitset_network_attribute_name=None,
          filter_nearest="DO_NOT_FILTER", nearest_count=None, nearest_cost_network_attribute=None,
          nearest_categories=None, nearest_assets=None, functions=None, propagators=None, output_assettypes=None,
          output_conditions=None, include_isolated_features="EXCLUDE_ISOLATED_FEATURES",
          ignore_barriers_at_starting_points="DO_NOT_IGNORE_BARRIERS_AT_STARTING_POINTS",
          include_up_to_first_spatial_container="DO_NOT_INCLUDE_UP_TO_FIRST_SPATIAL_CONTAINER", result_types=None,
          selection_type="NEW_SELECTION", clear_all_previous_trace_results="CLEAR_ALL_PREVIOUS_TRACE_RESULTS",
          trace_name=None, aggregated_points=None, aggregated_lines=None, aggregated_polygons=None,
          allow_indeterminate_flow="TRACE_INDETERMINATE_FLOW", validate_locatability="DO_NOT_VALIDATE_LOCATABILITY",
          use_trace_config="DO_NOT_USE_TRACE_CONFIGURATION", trace_config_name=None, out_json_file=None,
          run_async="RUN_SYNCHRONOUSLY", include_geometry="EXCLUDE_GEOMETRY",
          include_domain_descriptions="EXCLUDE_DOMAIN_DESCRIPTIONS", result_network_attributes=None, result_fields=None,
          use_digitized_direction="IGNORE_DIGITIZED_DIRECTION", synthesize_geometries="DO_NOT_SYNTHESIZE_GEOMETRIES",
          stopping_points=None, num_paths=None, max_hops=None, related_record_fields=None, circuit_name=None,
          infer_connectivity="DO_NOT_INFER_CONNECTIVITY", include_flow_directions="EXCLUDE_FLOW_DIRECTIONS",
          include_propagated_values="EXCLUDE_PROPAGATED_VALUES"):
    """EQUIVALENT for Upstream and Downstream (ADR-224 §5.6): their flow direction is found as ``_utility.FLOW`` says.

    A trace over the network topology as last enabled or validated: CONNECTED, SUBNETWORK, SUBNETWORK_CONTROLLERS,
    UPSTREAM, DOWNSTREAM and ISOLATION, with starting points and barriers from a class or table with FEATUREGLOBALID
    (and TERMINALID, PERCENTALONG), condition and filter barriers on network attributes and categories, the
    traversability and filter scopes, output asset types and conditions, containers, content and structures, and the
    SELECTION and AGGREGATED_GEOMETRY results. A selection is made on a layer named after each class (``WaterDevice``),
    made when there is none (INFERRED, ADR-231 §5.9 (g)). As the page says, a subnetwork-based trace run from Python
    applies no tier's trace configuration of its own. What is not built is refused by name."""
    kind = _word(trace_type, "CONNECTED")
    if kind in ("LOOPS", "SHORTEST_PATH", "PATH"):
        raise NotBuiltError(f"arcpy.un.Trace: the {kind} trace type {_LATER}.")
    if kind == "CIRCUIT":
        raise NotBuiltError("arcpy.un.Trace: the CIRCUIT trace type needs telecom domain networks, which are in a later "
                            "batch of ADR-231 (§5.8).")
    if kind not in _TRACE_TYPES:
        raise ExecuteError(f"ERROR 000800: '{trace_type}' is not a trace type.")
    refused = {"target_tier": target_tier, "shortest_path_network_attribute_name": shortest_path_network_attribute_name,
               "function_barriers": function_barriers, "filter_function_barriers": filter_function_barriers,
               "filter_bitset_network_attribute_name": filter_bitset_network_attribute_name,
               "nearest_count": nearest_count, "nearest_cost_network_attribute": nearest_cost_network_attribute,
               "nearest_categories": nearest_categories, "nearest_assets": nearest_assets, "functions": functions,
               "propagators": propagators, "trace_config_name": trace_config_name, "out_json_file": out_json_file,
               "result_network_attributes": result_network_attributes, "result_fields": result_fields,
               "stopping_points": stopping_points, "num_paths": num_paths, "max_hops": max_hops,
               "related_record_fields": related_record_fields, "circuit_name": circuit_name}
    for name, value in refused.items():
        if _given(value) and value != []:
            raise NotBuiltError(f"arcpy.un.Trace: {name} {_LATER}.")
    for name, value, default in (
            ("filter_nearest", filter_nearest, "DO_NOT_FILTER"),
            ("include_up_to_first_spatial_container", include_up_to_first_spatial_container,
             "DO_NOT_INCLUDE_UP_TO_FIRST_SPATIAL_CONTAINER"),
            ("validate_locatability", validate_locatability, "DO_NOT_VALIDATE_LOCATABILITY"),
            ("use_trace_config", use_trace_config, "DO_NOT_USE_TRACE_CONFIGURATION"),
            ("include_geometry", include_geometry, "EXCLUDE_GEOMETRY"),
            ("include_domain_descriptions", include_domain_descriptions, "EXCLUDE_DOMAIN_DESCRIPTIONS"),
            ("use_digitized_direction", use_digitized_direction, "IGNORE_DIGITIZED_DIRECTION"),
            ("synthesize_geometries", synthesize_geometries, "DO_NOT_SYNTHESIZE_GEOMETRIES"),
            ("infer_connectivity", infer_connectivity, "DO_NOT_INFER_CONNECTIVITY"),
            ("include_flow_directions", include_flow_directions, "EXCLUDE_FLOW_DIRECTIONS"),
            ("include_propagated_values", include_propagated_values, "EXCLUDE_PROPAGATED_VALUES")):
        if value is True or (_given(value) and value is not False and _word(value, default) != default):
            raise NotBuiltError(f"arcpy.un.Trace: {name} {_LATER}.")
    if _word(run_async, "RUN_SYNCHRONOUSLY") not in ("RUN_SYNCHRONOUSLY", "RUN_ASYNCHRONOUSLY"):
        raise ExecuteError("ERROR 000800: run_async is RUN_SYNCHRONOUSLY or RUN_ASYNCHRONOUSLY.")
    types = [_word(t, "") for t in _rows(result_types)] or ["SELECTION"]
    for t in types:
        if t not in ("SELECTION", "AGGREGATED_GEOMETRY"):
            raise NotBuiltError(f"arcpy.un.Trace: the {t} result type {_LATER}.")
    request = _untrace.Request(
        trace_type=kind, starts=_locations(starting_points), barriers=_locations(barriers),
        domain_network=str(domain_network) if _given(domain_network) else None,
        tier=str(tier) if _given(tier) else None,
        subnetwork_name=str(subnetwork_name) if _given(subnetwork_name) else None,
        condition_barriers=_utility.value_table(condition_barriers, 5),
        filter_barriers=_utility.value_table(filter_barriers, 5),
        traversability_scope=_word(traversability_scope, "BOTH_JUNCTIONS_AND_EDGES"),
        filter_scope=_word(filter_scope, "BOTH_JUNCTIONS_AND_EDGES"),
        include_barriers=_flag(include_barriers, "INCLUDE_BARRIERS", "EXCLUDE_BARRIERS", "INCLUDE_BARRIERS"),
        include_isolated_features=_flag(include_isolated_features, "INCLUDE_ISOLATED_FEATURES",
                                        "EXCLUDE_ISOLATED_FEATURES", "EXCLUDE_ISOLATED_FEATURES"),
        ignore_barriers_at_starting_points=_flag(ignore_barriers_at_starting_points,
                                                 "IGNORE_BARRIERS_AT_STARTING_POINTS",
                                                 "DO_NOT_IGNORE_BARRIERS_AT_STARTING_POINTS",
                                                 "DO_NOT_IGNORE_BARRIERS_AT_STARTING_POINTS"),
        allow_indeterminate_flow=_flag(allow_indeterminate_flow, "TRACE_INDETERMINATE_FLOW",
                                       "IGNORE_INDETERMINATE_FLOW", "TRACE_INDETERMINATE_FLOW"))
    with _utility.Network(_path(in_utility_network)) as network:
        network.require_enabled("Trace")
        _utility.write_dirty_areas(network)
        graph = _utility.build(network)
        ids = _untrace.run(network, graph, request)
        ids = _untrace.filter_output(network, graph, ids, [str(r).strip("'\"") for r in _rows(output_assettypes)],
                                     _utility.value_table(output_conditions, 5))
        keys = _untrace.features(graph, ids)
        keys = _untrace.related(graph, keys,
                                _flag(include_containers, "INCLUDE_CONTAINERS", "EXCLUDE_CONTAINERS",
                                      "EXCLUDE_CONTAINERS"),
                                _flag(include_content, "INCLUDE_CONTENT", "EXCLUDE_CONTENT", "EXCLUDE_CONTENT"),
                                _flag(include_structures, "INCLUDE_STRUCTURES", "EXCLUDE_STRUCTURES",
                                      "EXCLUDE_STRUCTURES"))
        if _flag(validate_consistency, "VALIDATE_CONSISTENCY", "DO_NOT_VALIDATE_CONSISTENCY",
                 "DO_NOT_VALIDATE_CONSISTENCY"):
            dirty = _utility.dirty_keys(network) & keys
            if dirty:
                raise ExecuteError(f"ERROR 002087: the trace met {len(dirty)} feature(s) with dirty areas; validate the "
                                   "network topology.")
        sources = network.sources()
        srid = int(network.q("select srs_id from grapy_feature_datasets where name = ?", network.dataset).fetchone()[0])
        file = network.file
    if "SELECTION" in types:
        _select(file, sources, keys, _word(selection_type, "NEW_SELECTION"))
    if "AGGREGATED_GEOMETRY" in types:
        _aggregate(file, srid, graph, ids, keys, sources, str(trace_name) if _given(trace_name) else "",
                   aggregated_points, aggregated_lines, aggregated_polygons,
                   _flag(clear_all_previous_trace_results, "CLEAR_ALL_PREVIOUS_TRACE_RESULTS",
                         "DO_NOT_CLEAR_ALL_PREVIOUS_TRACE_RESULTS", "CLEAR_ALL_PREVIOUS_TRACE_RESULTS"))
    return Result(_path(in_utility_network))


def _select(file, sources, keys, kind):
    """The result selected on each class's layer, combined with its current selection as ``selection_type`` says."""
    from ._layers import LAYERS, Layer
    from .management import _combine
    for source, (class_name, _, role) in sources.items():
        if role == "SubnetLine":
            continue
        path = f"{file}/{class_name}"
        short = class_name.rsplit("/", 1)[-1]
        name, n = short, 0
        while name in LAYERS and os.path.normcase(LAYERS[name].source) != os.path.normcase(path):
            n += 1
            name = f"{short}_{n}"
        matched = {oid for s, oid in keys if s == source}
        if not matched and name not in LAYERS:
            continue
        layer = LAYERS.get(name)
        if layer is None:
            layer = LAYERS[name] = Layer(name, path, None, "FeatureLayer" if role not in _utility.OBJECT_ROLES
                                         else "TableView")
        if kind == "SWITCH_SELECTION":
            layer.selection = layer.selection ^ matched
        else:
            layer.selection = _combine(kind, set(layer.selection), matched, "un.Trace")


def _aggregate(file, srid, graph, ids, keys, sources, trace_name, points, lines, polygons, clear):
    """Aggregated geometry: one multipart feature each of the traced points, lines (the traced edge elements — a
    starting point or barrier midspan keeps only its edge element, as the page describes) and polygons, with
    TRACENAME, in ``Trace_Results_Aggregated_*`` of the network's GeoPackage when none is named (INFERRED: the
    project's default geodatabase has no counterpart)."""
    from shapely.geometry import MultiPoint
    from shapely.ops import linemerge, unary_union
    found_points, found_lines, found_polygons = [], [], []
    seen = set()
    for i in ids:
        e = graph.elements[i]
        if e.feature is None or e.shape is None:
            continue
        if e.kind == "E":
            found_lines.append(e.shape)
        elif e.feature not in seen:
            seen.add(e.feature)
            found_points.append(e.shape)
    traced = {graph.elements[i].feature for i in ids}
    for key in keys - traced:
        data = graph.features.get(key, (None, {}))[1]
        if not data.get("wkt"):
            continue
        shape = shapely.from_wkt(data["wkt"])
        if shape.geom_type in ("Polygon", "MultiPolygon"):
            found_polygons.append(shape)
        elif shape.geom_type in ("LineString", "MultiLineString"):
            found_lines.append(shape)
        else:
            found_points.append(shape)
    for key in keys:
        role = sources[key[0]][2]
        data = graph.features.get(key, (None, {}))[1]
        if role == "StructureBoundary" and data.get("wkt") and key in traced:
            found_polygons.append(shapely.from_wkt(data["wkt"]))
    outputs = (("Point", points, "Trace_Results_Aggregated_Points", found_points,
                lambda g: MultiPoint([p for s in g for p in (s.geoms if hasattr(s, "geoms") else [s])])),
               ("Polyline", lines, "Trace_Results_Aggregated_Lines", found_lines,
                lambda g: _multiline(linemerge(unary_union(g)))),
               ("Polygon", polygons, "Trace_Results_Aggregated_Polygons", found_polygons,
                lambda g: _multipolygon(unary_union(g))))
    for shape_word, given, default, found, combine in outputs:
        path = _path(given) if _given(given) else f"{file}/{default}"
        _aggregate_class(path, shape_word, srid)
        connection, table = _gpkg.table_of(path)
        try:
            if clear:
                connection.execute(f'delete from "{table}"')
            if found:
                connection.execute(f'insert into "{table}" (geom, TRACENAME) values (?, ?)',
                                   (_gpkg.encode(combine(found), srid), trace_name))
            connection.commit()
        finally:
            connection.close()


def _multiline(shape):
    from shapely.geometry import MultiLineString
    return shape if shape.geom_type == "MultiLineString" else MultiLineString([shape])


def _multipolygon(shape):
    from shapely.geometry import MultiPolygon
    return shape if shape.geom_type == "MultiPolygon" else MultiPolygon([shape])


def _aggregate_class(path, shape_word, srid):
    """An aggregated geometry class made when missing; an existing one must be of the shape and have TRACENAME."""
    file, table = _gpkg.split(path)
    if os.path.exists(file):
        connection = _gpkg.connect(file)
        try:
            row = connection.execute("select geometry_type_name from gpkg_geometry_columns where table_name = ?",
                                     (table,)).fetchone()
            if row is not None:
                fields = {c.name.upper() for c in _gpkg.describe(connection, table)[0]}
                word = {"Point": "POINT", "Polyline": "LINESTRING", "Polygon": "POLYGON"}[shape_word]
                if word not in row[0].upper() or "TRACENAME" not in fields:
                    raise ExecuteError(f"ERROR 002088: '{path}' must be a {shape_word.lower()} feature class with a "
                                       "TRACENAME field.")
                return
        finally:
            connection.close()
    stored = {"Point": "Multipoint", "Polyline": "Polyline", "Polygon": "Polygon"}[shape_word]
    _gpkg.create(path, stored, srid, [_gpkg.Column("TRACENAME", "String")])


# ---- refused by name -----------------------------------------------------------------------------------------------

_DIAGRAMS = ("is a network diagram tool: network diagrams, the schematic views of a utility or trace network, are a "
             "data model this server does not hold yet (ADR-224 group 2), as arcpy.nd says")
_TELECOM = ("needs telecom domain networks — circuits, unit identifiers, colour and wavelength schemes — which are "
            "in a later batch of ADR-231 (§5.8)")
_REASONS = {
    _DIAGRAMS: (
        "AddAngleDirectedLayout", "AddCollapseContainerByAttributeRule", "AddCollapseContainerRule",
        "AddCompressionLayout", "AddConnectivityAssociationsRule", "AddDiagramFeatureCapabilityByAttributeRule",
        "AddDiagramTemplate", "AddExpandContainerByAttributeRule", "AddExpandContainerRule", "AddForceDirectedLayout",
        "AddGridLayout", "AddLinearDispatchLayout", "AddMainRingLayout", "AddMainlineTreeLayout",
        "AddPartialOverlappingEdgesLayout", "AddRadialTreeLayout", "AddReduceEdgeByAttributeRule",
        "AddReduceJunctionByAttributeRule", "AddReduceJunctionRule", "AddRelativeMainlineLayout",
        "AddRemoveFeatureByAttributeRule", "AddRemoveFeatureRule", "AddReshapeDiagramEdgesLayout",
        "AddSetRootJunctionByAttributeRule", "AddSetStartingPointByAttributeRule", "AddSmartTreeLayout",
        "AddSpatialDispatchLayout", "AddSpatialQueryRule", "AddStartIterationRule", "AddStopIterationRule",
        "AddStructuralAttachmentsRule",
        "AddTraceRule", "AlterDiagramProperties", "AlterDiagramTemplate", "AppendToDiagram",
        "ApplyAngleDirectedLayout", "ApplyCompressionLayout", "ApplyForceDirectedLayout", "ApplyGeoPositionsLayout",
        "ApplyGridLayout", "ApplyLinearDispatchLayout", "ApplyMainRingLayout", "ApplyMainlineTreeLayout",
        "ApplyPartialOverlappingEdgesLayout", "ApplyRadialTreeLayout", "ApplyRelativeMainlineLayout",
        "ApplyRotateTreeLayout", "ApplySmartTreeLayout", "ApplySpatialDispatchLayout", "ApplyTemplateLayouts",
        "ChangeDiagramsOwner", "CreateDiagram", "CreateDiagramLayerDefinition", "DeleteDiagram",
        "DeleteDiagramTemplate", "ExportDiagramContent", "ExportDiagramLayerDefinition",
        "ExportDiagramTemplateDefinitions", "ExtendDiagram", "GetDiagramTemplateNames",
        "ImportDiagramTemplateDefinitions", "MakeDiagramLayer", "OverwriteDiagram", "PurgeTemporaryDiagrams",
        "ReshapeDiagramEdgesLayout", "StoreDiagram", "UpdateDiagram"),
    _TELECOM: ("AddTelecomDomainNetwork", "AddColorScheme", "AddColorSet", "AddWavelengthScheme", "DeleteColorScheme",
               "ExportCircuitDefinitions", "ExportCircuits", "ImportCircuitDefinitions", "SetCircuitProperties",
               "SetTelecomObjectCombinePolicy", "SetTelecomObjectDividePolicy", "VerifyCircuits"),
    _LATER: ("UpdateSubnetwork", "ExportSubnetwork", "SetSubnetworkDefinition", "AddTraceConfiguration",
             "DeleteTraceConfiguration", "ImportTraceConfigurations", "ExportTraceConfigurations", "AddTraceLocations",
             "UpdateIsConnected", "SetEdgeConnectivity", "SetAssociationRole", "AnalyzeNetworkData",
             "RebuildNetworkTopology", "ApplyErrorResolutions", "EnableClusterKeys", "CalculateClusterKeys"),
    "checks or repairs the consistency of Esri's persisted network index, which grapy does not hold: grapy's "
    "topology is rebuilt from its record at every trace (ADR-231 §5.4)": ("VerifyNetworkTopology",
                                                                         "RepairNetworkTopology"),
    "loads an asset package, Esri's geodatabase layout of a whole utility network, which grapy does not read: its "
    "networks are kept in grapy's own tables (ADR-231 §5.2)": ("MigrateToUtilityNetwork",),
    "has no page grapy could read: doc.esri.com/en/arcgis-pro/latest/tool-reference/utility-networks/"
    "update-utility-network-schema.html answered 404 (checked 2026-10-30)": ("UpdateUtilityNetworkSchema",),
}
_REFUSED = {name: reason for reason, names in _REASONS.items() for name in names}


def __getattr__(name):
    if name.startswith("_"):
        raise AttributeError(name)
    reason = _REFUSED.get(name)
    message = f"arcpy.un.{name} {reason}." if reason else f"arcpy.un.{name} is not built in grapy yet."

    def missing(*_, **__):
        raise NotBuiltError(message)

    return missing
