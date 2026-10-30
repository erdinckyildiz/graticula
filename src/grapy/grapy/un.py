"""arcpy.un — utility networks, by their documented names and parameters (ADR-231).

The model is ``grapy._utility``: grapy's tables in the GeoPackage, beside the network's classes in its feature dataset.
The traces are ``grapy._untrace``, the one place a trace's flow is decided; subnetworks (definitions, UpdateSubnetwork,
ExportSubnetwork, Validate's discovered subnetworks, Is connected) are ``grapy._unsubnet``, and the JSON results
``grapy._unjson``. Trace is an *equivalent* (ADR-224 §5.6) for Upstream and Downstream, whose direction the pages define
only as "away from" or "toward" the subnetwork controllers, and for Loops, Path and nearest filtering, which they
describe by their result; CalculateClusterKeys is one too. The telecom domain network, the error resolution database and
synthesized geometry are refused by name, each for its own reason. The network diagram tools are ``grapy.nd``'s, under
the same names (ADR-232).
"""
from __future__ import annotations

import csv
import json
import math
import os

import shapely

from . import _gpkg, _unjson, _untrace, _utility
from ._result import Result
from ._runtime import AddWarning, ExecuteError, NotBuiltError, equivalent

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
    """A terminal configuration: two to eight terminals of at most 32 characters, numbered 2, 3 … in the order given,
    1 being the Single Terminal of an asset type without a configuration (measured in Pro 3.7, ADR-231 §5.9 (b)); valid paths named, each All, None or terminal pairs ``A-B, A-C``; required for a
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
                  json.dumps([[i + 2, n, bool(up)] for i, (n, up) in enumerate(terminals)]), json.dumps(paths), default)
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


def _extent(value, tool="ValidateNetworkTopology"):
    if not _given(value) or str(value).upper() in ("DEFAULT", "MAXOF"):
        return None
    if hasattr(value, "XMin"):
        return value.XMin, value.YMin, value.XMax, value.YMax
    if str(value).upper() in ("MINOF", "DISPLAY"):
        raise NotBuiltError(f"arcpy.un.{tool}: the extent '{value}' is not built in grapy: there is no "
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
    dirty area meets the extent — dirty areas are not clipped. Outputs the network and, as JSON, the discovered
    subnetworks: those of a tier managing Is dirty whose controllers or extent changed since UpdateSubnetwork, now marked
    dirty (INFERRED, ADR-231 §5.9 (m))."""
    from . import _unsubnet
    with _utility.Network(_path(in_utility_network)) as network:
        network.require_enabled("ValidateNetworkTopology")
        _utility.refresh(network, extent=_extent(extent))
        _utility.write_dirty_areas(network)
        discovered = _unsubnet.discover(network)
    return Result(_path(in_utility_network), json.dumps({"discoveredSubnetworks": discovered}))


# ---- tracing -------------------------------------------------------------------------------------------------------

_TRACE_TYPES = ("CONNECTED", "SUBNETWORK", "SUBNETWORK_CONTROLLERS", "UPSTREAM", "DOWNSTREAM", "ISOLATION", "LOOPS",
                "SHORTEST_PATH", "PATH")
_ALL_TRACE_TYPES = ("CONNECTED", "SUBNETWORK", "SUBNETWORK_CONTROLLERS", "UPSTREAM", "DOWNSTREAM", "LOOPS",
                    "SHORTEST_PATH", "ISOLATION", "PATH", "CIRCUIT")
_JSON_TYPES = ("ELEMENTS", "CONNECTIVITY", "FEATURES", "CONTAINMENT_AND_ATTACHMENT_ASSOCIATIONS", "RELATED_RECORDS")
_TELECOM = ("needs a telecom domain network: the Add Telecom Domain Network page names the tool's parameters but not "
            "the classes and tables a telecom domain network has — its circuits, sections, subcircuits, unit identifiers "
            "and colour and wavelength schemes — and every telecom tool and the circuit trace read or write those, so "
            "grapy cannot build them from the pages without inventing Esri's telecom schema")
_SYNTHESIZE = ("synthesize_geometries is not built in grapy: the Trace page says geometry is created for associations "
               "and edge objects but not how it is drawn, and grapy will not invent one")
_FLAGS = {"include_containers": ("INCLUDE_CONTAINERS", "EXCLUDE_CONTAINERS"),
          "include_content": ("INCLUDE_CONTENT", "EXCLUDE_CONTENT"),
          "include_structures": ("INCLUDE_STRUCTURES", "EXCLUDE_STRUCTURES"),
          "include_barriers": ("INCLUDE_BARRIERS", "EXCLUDE_BARRIERS"),
          "validate_consistency": ("VALIDATE_CONSISTENCY", "DO_NOT_VALIDATE_CONSISTENCY"),
          "filter_nearest": ("FILTER_BY_NEAREST", "DO_NOT_FILTER"),
          "include_isolated_features": ("INCLUDE_ISOLATED_FEATURES", "EXCLUDE_ISOLATED_FEATURES"),
          "ignore_barriers_at_starting_points": ("IGNORE_BARRIERS_AT_STARTING_POINTS",
                                                 "DO_NOT_IGNORE_BARRIERS_AT_STARTING_POINTS"),
          "include_up_to_first_spatial_container": ("INCLUDE_UP_TO_FIRST_SPATIAL_CONTAINER",
                                                    "DO_NOT_INCLUDE_UP_TO_FIRST_SPATIAL_CONTAINER"),
          "allow_indeterminate_flow": ("TRACE_INDETERMINATE_FLOW", "IGNORE_INDETERMINATE_FLOW"),
          "validate_locatability": ("VALIDATE_LOCATABILITY", "DO_NOT_VALIDATE_LOCATABILITY"),
          "use_digitized_direction": ("USE_DIGITIZED_DIRECTION", "IGNORE_DIGITIZED_DIRECTION"),
          "synthesize_geometries": ("SYNTHESIZE_GEOMETRIES", "DO_NOT_SYNTHESIZE_GEOMETRIES"),
          "infer_connectivity": ("INFER_CONNECTIVITY", "DO_NOT_INFER_CONNECTIVITY"),
          "use_trace_config": ("USE_TRACE_CONFIGURATION", "DO_NOT_USE_TRACE_CONFIGURATION"),
          "include_geometry": ("INCLUDE_GEOMETRY", "EXCLUDE_GEOMETRY"),
          "include_domain_descriptions": ("INCLUDE_DOMAIN_DESCRIPTIONS", "EXCLUDE_DOMAIN_DESCRIPTIONS"),
          "include_flow_directions": ("INCLUDE_FLOW_DIRECTIONS", "EXCLUDE_FLOW_DIRECTIONS"),
          "include_propagated_values": ("INCLUDE_PROPAGATED_VALUES", "EXCLUDE_PROPAGATED_VALUES"),
          "clear_all_previous_trace_results": ("CLEAR_ALL_PREVIOUS_TRACE_RESULTS",
                                               "DO_NOT_CLEAR_ALL_PREVIOUS_TRACE_RESULTS")}
_DEFAULT_ON = {"include_barriers", "allow_indeterminate_flow", "clear_all_previous_trace_results"}
_TABLES = {"condition_barriers": 5, "filter_barriers": 5, "output_conditions": 5, "function_barriers": 5,
           "filter_function_barriers": 5, "functions": 7, "propagators": 6, "result_fields": 2,
           "related_record_fields": 2}


def _on(params, name):
    yes, no = _FLAGS[name]
    return _flag(params.get(name), yes, no, yes if name in _DEFAULT_ON else no)


def _locations(value, split_filter=False):
    """(FEATUREGLOBALID, TERMINALID, PERCENTALONG, filter barrier) rows of a starting point, barrier or stopping point
    class or table; a class AddTraceLocations wrote marks its filter barriers in ISFILTERBARRIER."""
    if not _given(value):
        return ([], []) if split_filter else []
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
    fields = [names["FEATUREGLOBALID"], names.get("TERMINALID"), names.get("PERCENTALONG"), names.get("ISFILTERBARRIER")]
    wanted = [f for f in fields if f]
    out = []
    with da.SearchCursor(value, wanted) as read:
        for row in read:
            values = dict(zip(wanted, row))
            terminal = values.get(fields[1]) if fields[1] else None
            percent = values.get(fields[2]) if fields[2] else None
            if percent is not None and not 0 <= float(percent) <= 1:
                percent = None
            filtered = bool(values.get(fields[3])) if fields[3] else False
            out.append((values[fields[0]], None if terminal in (None, "") else int(terminal), percent, filtered))
    if split_filter:
        return [r for r in out if not r[3]], [r for r in out if r[3]]
    return out


@equivalent(_utility.FLOW[0] + "; loops as biconnected components; paths of fewest hops by Yen's k shortest paths; "
            "nearest by Dijkstra's shortest paths",
            _utility.FLOW[1] + "; Hopcroft and Tarjan 1973, Algorithm 447; Yen 1971, Finding the k Shortest Loopless "
            "Paths in a Network; Dijkstra 1959, A Note on Two Problems in Connexion with Graphs")
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
    """EQUIVALENT for Upstream and Downstream, Loops, Path and nearest filtering (ADR-224 §5.6): their flow, loops,
    paths and nearness are found as the methods named say, where the pages describe the result and not the rule.

    A trace over the network topology as last enabled or validated — every trace type of a traditional domain network —
    with starting points, barriers and stopping points from a class or table with FEATUREGLOBALID (and TERMINALID,
    PERCENTALONG; AddTraceLocations' ISFILTERBARRIER marks filter barriers), condition, function and filter barriers,
    propagators, functions, nearest and bitset filtering, a target tier, digitized direction, the traversability and
    filter scopes, output asset types and conditions, containers, content and structures, locatability, a named trace
    configuration, and the SELECTION, AGGREGATED_GEOMETRY and JSON results (``_unjson``). A selection is made on a layer
    named after each class (``WaterDevice``), made when there is none (INFERRED, ADR-231 §5.9 (g)). As the page says, a
    subnetwork-based trace run from Python applies no tier's trace configuration of its own. The telecom circuit trace
    and synthesized geometry are refused by name."""
    params = dict(locals())
    if _flag(use_trace_config, "USE_TRACE_CONFIGURATION", "DO_NOT_USE_TRACE_CONFIGURATION",
             "DO_NOT_USE_TRACE_CONFIGURATION"):
        if not _given(trace_config_name):
            raise ExecuteError("ERROR 000735: trace_config_name is required to use a trace configuration.")
        with _utility.Network(_path(in_utility_network)) as network:
            row = network.q("select trace_type, configuration, result_types from grapy_un_trace_config where network = ? "
                            "and lower(name) = lower(?)", network.name, str(trace_config_name)).fetchone()
        if row is None:
            raise ExecuteError(f"ERROR 000732: '{trace_config_name}' is not a trace configuration of the network.")
        # INFERRED (ADR-231 §5.9 (n)): the configuration's settings replace the call's; the locations and the output
        # paths stay the call's.
        params["trace_type"] = row[0]
        params.update(json.loads(row[1]))
        if row[2] and json.loads(row[2]):
            params["result_types"] = json.loads(row[2])
    return _trace(params)


def _trace(params):
    p = params
    kind = _word(p["trace_type"], "CONNECTED")
    if kind == "CIRCUIT" or _word(p.get("circuit_name"), "") or _on(p, "infer_connectivity"):
        raise NotBuiltError(f"arcpy.un.Trace: the circuit trace, circuit_name and infer_connectivity {_TELECOM}.")
    if kind not in _TRACE_TYPES:
        raise ExecuteError(f"ERROR 000800: '{p['trace_type']}' is not a trace type.")
    if _on(p, "synthesize_geometries"):
        raise NotBuiltError(f"arcpy.un.Trace: {_SYNTHESIZE}.")
    if _word(p.get("run_async"), "RUN_SYNCHRONOUSLY") not in ("RUN_SYNCHRONOUSLY", "RUN_ASYNCHRONOUSLY"):
        raise ExecuteError("ERROR 000800: run_async is RUN_SYNCHRONOUSLY or RUN_ASYNCHRONOUSLY.")
    types = [_word(t, "") for t in _rows(p.get("result_types"))] or ["SELECTION"]
    for t in types:
        if t == "CIRCUITS":
            raise NotBuiltError(f"arcpy.un.Trace: the CIRCUITS result type {_TELECOM}.")
        if t not in ("SELECTION", "AGGREGATED_GEOMETRY") + _JSON_TYPES:
            raise ExecuteError(f"ERROR 000800: '{t}' is not a result type.")
    json_types = [t for t in types if t in _JSON_TYPES]
    if json_types and not _given(p.get("out_json_file")):
        raise ExecuteError("ERROR 000735: out_json_file is required for the " + ", ".join(json_types) + " result types.")
    tables = {name: _utility.value_table(p.get(name), width) for name, width in _TABLES.items()}
    nearest = None
    if _on(p, "filter_nearest"):
        categories = [str(c).strip("'\"") for c in _rows(p.get("nearest_categories"))]
        assets = [str(a).strip("'\"") for a in _rows(p.get("nearest_assets"))]
        if not _given(p.get("nearest_count")) or not _given(p.get("nearest_cost_network_attribute")) or not (
                categories or assets):
            raise ExecuteError("ERROR 000735: filtering by nearest needs a count, a cost network attribute and "
                               "categories or asset types.")
        nearest = (int(p["nearest_count"]), str(p["nearest_cost_network_attribute"]), categories, assets)
    barriers, filter_locations = _locations(p.get("barriers"), split_filter=True)
    digitized = _on(p, "use_digitized_direction") and kind in ("UPSTREAM", "DOWNSTREAM")
    request = _untrace.Request(
        trace_type=kind, starts=_locations(p.get("starting_points")), barriers=barriers,
        filter_locations=filter_locations, stops=_locations(p.get("stopping_points")),
        domain_network=None if digitized or not _given(p.get("domain_network")) else str(p["domain_network"]),
        tier=None if digitized or not _given(p.get("tier")) else str(p["tier"]),
        target_tier=None if digitized or not _given(p.get("target_tier")) else str(p["target_tier"]),
        subnetwork_name=str(p["subnetwork_name"]) if _given(p.get("subnetwork_name")) else None,
        condition_barriers=tables["condition_barriers"], filter_barriers=tables["filter_barriers"],
        function_barriers=tables["function_barriers"], filter_function_barriers=tables["filter_function_barriers"],
        functions=tables["functions"], propagators=[] if digitized else tables["propagators"],
        traversability_scope=_word(p.get("traversability_scope"), "BOTH_JUNCTIONS_AND_EDGES"),
        filter_scope=_word(p.get("filter_scope"), "BOTH_JUNCTIONS_AND_EDGES"),
        include_barriers=_on(p, "include_barriers"), include_isolated_features=_on(p, "include_isolated_features"),
        ignore_barriers_at_starting_points=_on(p, "ignore_barriers_at_starting_points"),
        allow_indeterminate_flow=_on(p, "allow_indeterminate_flow"),
        shortest_path_attribute=str(p["shortest_path_network_attribute_name"])
        if _given(p.get("shortest_path_network_attribute_name")) else None,
        num_paths=int(p["num_paths"]) if _given(p.get("num_paths")) else None,
        max_hops=int(p["max_hops"]) if _given(p.get("max_hops")) else None,
        filter_bitset=str(p["filter_bitset_network_attribute_name"])
        if _given(p.get("filter_bitset_network_attribute_name")) and kind in ("UPSTREAM", "DOWNSTREAM", "LOOPS") else None,
        nearest=nearest, digitized=digitized)
    path = _path(p["in_utility_network"])
    with _utility.Network(path) as network:
        network.require_enabled("Trace")
        _utility.write_dirty_areas(network)
        graph = _utility.build(network)
        outcome = _untrace.run(network, graph, request)
        ids = _untrace.filter_output(network, graph, outcome.ids,
                                     [str(r).strip("'\"") for r in _rows(p.get("output_assettypes"))],
                                     tables["output_conditions"])
        keys = _untrace.features(graph, ids)
        keys = _untrace.related(graph, keys, _on(p, "include_containers"), _on(p, "include_content"),
                                _on(p, "include_structures"),
                                _on(p, "include_containers") and _on(p, "include_up_to_first_spatial_container"))
        if _on(p, "validate_consistency"):
            dirty = _utility.dirty_keys(network) & keys
            if dirty:
                raise ExecuteError(f"ERROR 002087: the trace met {len(dirty)} feature(s) with dirty areas; validate the "
                                   "network topology.")
        if _on(p, "validate_locatability"):
            lost = _untrace.unlocatable(graph, keys)
            if lost:
                raise ExecuteError(f"ERROR 002104: the trace met {len(lost)} junction or edge object(s) that cannot be "
                                   "located: they have no containment or attachment association.")
        if json_types:
            writer = _unjson.Writer(network, graph, outcome, ids, keys, _on(p, "include_geometry"),
                                    _on(p, "include_domain_descriptions"),
                                    [str(a).strip("'\"") for a in _rows(p.get("result_network_attributes"))],
                                    tables["result_fields"], tables["related_record_fields"],
                                    _on(p, "include_flow_directions"), _on(p, "include_propagated_values"))
            _unjson.write(_path(p["out_json_file"]), writer.document(json_types))
        sources = network.sources()
        srid = int(network.q("select srs_id from grapy_feature_datasets where name = ?", network.dataset).fetchone()[0])
        file = network.file
    if "SELECTION" in types:
        _select(file, sources, keys, _word(p.get("selection_type"), "NEW_SELECTION"))
    if "AGGREGATED_GEOMETRY" in types:
        _aggregate(file, srid, graph, ids, keys, sources, str(p["trace_name"]) if _given(p.get("trace_name")) else "",
                   p.get("aggregated_points"), p.get("aggregated_lines"), p.get("aggregated_polygons"),
                   _on(p, "clear_all_previous_trace_results"))
    outputs = [path]
    if json_types:
        outputs.append(_path(p["out_json_file"]))
    return Result(*outputs)


# ---- trace locations and trace configurations ----------------------------------------------------------------------

def AddTraceLocations(in_utility_network, out_feature_class, load_selected_features="DO_NOT_LOAD_SELECTED_FEATURES",
                      clear_trace_locations="KEEP_LOCATIONS", trace_locations=None,
                      filter_barrier="TRAVERSABILITY_BARRIER"):
    """Trace locations written to a point class in the schema Trace reads: FEATUREGLOBALID, TERMINALID, PERCENTALONG and
    ISFILTERBARRIER (INFERRED, ADR-231 §5.9 (n): the page names no fields), from the selection of the network's layers or
    from rows (Layer Name, Global ID, Terminal ID, Percent Along) — Percent Along 0.5 on a line or edge object when none
    is given, as the page says. A junction's point is its shape, a line's the point at its percent along; an object's
    location has no shape."""
    load = _flag(load_selected_features, "LOAD_SELECTED_FEATURES", "DO_NOT_LOAD_SELECTED_FEATURES",
                 "DO_NOT_LOAD_SELECTED_FEATURES")
    clear = _word(clear_trace_locations, "KEEP_LOCATIONS")
    if clear not in ("CLEAR_LOCATIONS", "KEEP_LOCATIONS"):
        raise ExecuteError("ERROR 000800: clear_trace_locations is CLEAR_LOCATIONS or KEEP_LOCATIONS.")
    is_filter = _flag(filter_barrier, "FILTER_BARRIER", "TRAVERSABILITY_BARRIER", "TRAVERSABILITY_BARRIER")
    rows = _rows(trace_locations)
    if load and rows:
        raise ExecuteError("ERROR 000800: Load Selected Features and Trace Locations cannot be used together.")
    from ._layers import LAYERS
    with _utility.Network(_path(in_utility_network)) as network:
        sources = network.sources()
        srid = int(network.q("select srs_id from grapy_feature_datasets where name = ?", network.dataset).fetchone()[0])
        wanted = []
        if load:
            for layer in LAYERS.values():
                try:
                    file, table = _gpkg.split(layer.source)
                except ValueError:
                    continue
                if os.path.normcase(os.path.abspath(file)) != os.path.normcase(os.path.abspath(network.file)):
                    continue
                class_name = next((c for c, _, _ in sources.values() if c == table), None)
                if class_name is None:
                    continue
                oid = network.oid(class_name)
                for fid in sorted(layer.selection):
                    found = network.q(f'select GLOBALID from "{class_name}" where "{oid}" = ?', fid).fetchone()
                    if found:
                        wanted.append((class_name, found[0], None, None))
        for row in rows:
            cells = _cells(row)
            cells += [None] * (4 - len(cells))
            name = str(cells[0])
            layer = LAYERS.get(name)
            class_name = network.table(_gpkg.split(layer.source)[1] if layer is not None else name)
            terminal = cells[2] if _given(cells[2]) else None
            percent = float(cells[3]) if _given(cells[3]) else None
            wanted.append((class_name, str(cells[1]), terminal, percent))
        out = []
        for class_name, globalid, terminal, percent in wanted:
            oid, group, kind = network.feature_by_globalid(class_name, globalid)
            role = network.role(class_name)
            terminal_id = network.terminal_id(class_name, group, kind, terminal) if terminal is not None else None
            shape = None
            if role in _utility.LINE_ROLES or role == "EdgeObject":
                percent = 0.5 if percent is None else percent
            else:
                percent = None
            if role not in _utility.OBJECT_ROLES:
                blob = network.q(f'select geom from "{class_name}" where "{network.oid(class_name)}" = ?', oid).fetchone()[0]
                geometry = _gpkg.decode(blob)
                if geometry is not None and not geometry.is_empty:
                    geometry = shapely.force_2d(geometry)
                    shape = geometry if geometry.geom_type == "Point" else (
                        shapely.line_merge(geometry) if geometry.geom_type == "MultiLineString" else geometry
                    ).interpolate(percent or 0.0, normalized=True)
            out.append((str(globalid).upper(), terminal_id, percent, int(is_filter), shape))
    path = _path(out_feature_class)
    _trace_location_class(path, srid)
    connection, table = _gpkg.table_of(path)
    try:
        if clear == "CLEAR_LOCATIONS":
            connection.execute(f'delete from "{table}"')
        for globalid, terminal, percent, flag, shape in out:
            connection.execute(f'insert into "{table}" (geom, FEATUREGLOBALID, TERMINALID, PERCENTALONG, ISFILTERBARRIER) '
                               'values (?, ?, ?, ?, ?)', (_gpkg.encode(shape, srid) if shape is not None else None,
                                                          globalid, terminal, percent, flag))
        connection.commit()
    finally:
        connection.close()
    return Result(path)


def _trace_location_class(path, srid):
    file, table = _gpkg.split(path)
    if os.path.exists(file):
        connection = _gpkg.connect(file)
        try:
            exists = connection.execute("select 1 from gpkg_contents where table_name = ?", (table,)).fetchone()
            if exists:
                fields = {c.name.upper() for c in _gpkg.describe(connection, table)[0]}
                if not {"FEATUREGLOBALID", "TERMINALID", "PERCENTALONG"} <= fields:
                    raise ExecuteError(f"ERROR 000800: '{path}' is not a trace locations class.")
                if "ISFILTERBARRIER" not in fields:
                    connection.execute(f'alter table "{table}" add column ISFILTERBARRIER SMALLINT')
                    connection.commit()
                return
        finally:
            connection.close()
    _gpkg.create(path, "Point", srid, [_gpkg.Column("FEATUREGLOBALID", "String"), _gpkg.Column("TERMINALID", "Integer"),
                                       _gpkg.Column("PERCENTALONG", "Double"),
                                       _gpkg.Column("ISFILTERBARRIER", "SmallInteger")])


# The AddTraceConfiguration parameters a configuration keeps, in the page's order after the network, name, type,
# description and tags; and how each is written in an exported file. The file is the page's array of {creator, name,
# description, traceType, configuration, resultTypes, tags}, the last three as JSON strings; the configuration's keys are
# the REST API's traceConfiguration keys. INFERRED (ADR-231 §5.9 (n)): traceType's codes 1 … 10 in the order the
# AddTraceConfiguration page lists the types, the keys of functions, propagators and output filters the REST page's
# examples do not show, and SELECTION and ELEMENTS both written "elements" (read back as SELECTION).
_CONFIG = ("domain_network", "tier", "target_tier", "subnetwork_name", "shortest_path_network_attribute_name",
           "include_containers", "include_content", "include_structures", "include_barriers", "validate_consistency",
           "condition_barriers", "function_barriers", "traversability_scope", "filter_barriers",
           "filter_function_barriers", "filter_scope", "filter_bitset_network_attribute_name", "filter_nearest",
           "nearest_count", "nearest_cost_network_attribute", "nearest_categories", "nearest_assets", "functions",
           "propagators", "output_assettypes", "output_conditions", "include_isolated_features",
           "ignore_barriers_at_starting_points", "include_up_to_first_spatial_container", "result_types",
           "allow_indeterminate_flow", "validate_locatability", "use_digitized_direction", "synthesize_geometries",
           "num_paths", "max_hops", "circuit_name", "infer_connectivity")
_REST_TEXT = {"domain_network": "domainNetworkName", "tier": "tierName", "target_tier": "targetTierName",
              "subnetwork_name": "subnetworkName", "shortest_path_network_attribute_name": "shortestPathNetworkAttributeName",
              "filter_bitset_network_attribute_name": "filterBitsetNetworkAttributeName", "circuit_name": "circuitName",
              "num_paths": "numPaths", "max_hops": "maxHops"}
_REST_FLAGS = {"include_containers": "includeContainers", "include_content": "includeContent",
               "include_structures": "includeStructures", "include_barriers": "includeBarriers",
               "validate_consistency": "validateConsistency", "include_isolated_features": "includeIsolated",
               "ignore_barriers_at_starting_points": "ignoreBarriersAtStartingPoints",
               "include_up_to_first_spatial_container": "includeUpToFirstSpatialContainer",
               "allow_indeterminate_flow": "allowIndeterminateFlow", "validate_locatability": "validateLocatability",
               "use_digitized_direction": "useDigitizedDirection", "synthesize_geometries": "synthesizeGeometries",
               "infer_connectivity": "inferConnectivity"}
_REST_SCOPES = {"BOTH_JUNCTIONS_AND_EDGES": "junctionsAndEdges", "JUNCTIONS_ONLY": "junctions", "EDGES_ONLY": "edges"}
_REST_OPERATORS = dict(zip(_utility._OPERATORS, ("equal", "notEqual", "greaterThan", "greaterThanEqual", "lessThan",
                                                 "lessThanEqual", "includesTheValues", "doesNotIncludeTheValues",
                                                 "includesAny", "doesNotIncludeAny")))
_REST_RESULTS = {"SELECTION": "elements", "ELEMENTS": "elements", "AGGREGATED_GEOMETRY": "aggregatedGeometry",
                 "CONNECTIVITY": "connectivity", "FEATURES": "features",
                 "CONTAINMENT_AND_ATTACHMENT_ASSOCIATIONS": "associations", "RELATED_RECORDS": "relatedRecords",
                 "CIRCUITS": "circuits"}
_TRACE_CODES = {name: i + 1 for i, name in enumerate(_ALL_TRACE_TYPES)}


def _back(mapping, value):
    return next((k for k, v in mapping.items() if v == value), None)


def _number(value):
    try:
        number = float(value)
    except (TypeError, ValueError):
        return value
    return int(number) if number.is_integer() else number


def _rest_condition(row):
    return {"name": row[0], "type": "category" if str(row[0]).lower() == "category" else "networkAttribute",
            "operator": _REST_OPERATORS.get(str(row[1]).upper(), row[1]), "value": _number(row[3]),
            "combineUsingOr": str(row[4] or "OR").upper() != "AND",
            "isSpecificValue": str(row[2] or "SPECIFIC_VALUE").upper() != "NETWORK_ATTRIBUTE"}


def _condition_row(item):
    return [item["name"], _back(_REST_OPERATORS, item["operator"]) or item["operator"],
            "SPECIFIC_VALUE" if item.get("isSpecificValue", True) else "NETWORK_ATTRIBUTE", item.get("value"),
            "OR" if item.get("combineUsingOr", True) else "AND"]


def _to_rest(config):
    """A stored configuration (arcpy's parameters) as the REST API's traceConfiguration object."""
    out = {}
    for name, key in _REST_TEXT.items():
        if config.get(name) not in (None, ""):
            out[key] = config[name]
    for name, key in _REST_FLAGS.items():
        out[key] = _on(config, name)
    out["traversabilityScope"] = _REST_SCOPES[_word(config.get("traversability_scope"), "BOTH_JUNCTIONS_AND_EDGES")]
    out["filterScope"] = _REST_SCOPES[_word(config.get("filter_scope"), "BOTH_JUNCTIONS_AND_EDGES")]
    for name, key in (("condition_barriers", "conditionBarriers"), ("filter_barriers", "filterBarriers"),
                      ("output_conditions", "outputConditions")):
        out[key] = [_rest_condition(r) for r in _utility.value_table(config.get(name), 5)]
    for name, key in (("function_barriers", "functionBarriers"), ("filter_function_barriers", "filterFunctionBarriers")):
        out[key] = [{"functionType": str(r[0]).lower(), "networkAttributeName": r[1],
                     "operator": _REST_OPERATORS.get(str(r[2]).upper(), r[2]), "value": _number(r[3]),
                     "useLocalValues": _untrace._truthy(r[4])} for r in _utility.value_table(config.get(name), 5)]
    out["functions"] = [{"functionType": str(r[0]).lower(), "networkAttributeName": r[1],
                         "conditions": [_rest_condition([r[2], r[3], r[4], r[5], None])] if r[2] else [],
                         "functionName": r[6] or ""} for r in _utility.value_table(config.get("functions"), 7)]
    out["propagators"] = [{"networkAttributeName": r[0], "substitutionAttributeName": r[1] or "",
                           "propagatorFunctionType": _untrace.PROPAGATORS.get(str(r[2]).upper(), r[2]),
                           "operator": _REST_OPERATORS.get(str(r[3]).upper(), r[3]), "value": _number(r[4]),
                           "propagatedAttributeName": r[5] or ""}
                          for r in _utility.value_table(config.get("propagators"), 6)]
    out["nearestNeighbor"] = {"count": int(config.get("nearest_count") or -1) if _on(config, "filter_nearest") else -1,
                              "costNetworkAttributeName": config.get("nearest_cost_network_attribute") or "",
                              "nearestCategories": [str(c) for c in _rows(config.get("nearest_categories"))],
                              "nearestAssets": [str(a) for a in _rows(config.get("nearest_assets"))]}
    out["outputFilters"] = [{"assetType": str(a)} for a in _rows(config.get("output_assettypes"))]
    return out


def _from_rest(rest):
    """The REST API's traceConfiguration object as arcpy's parameters."""
    config = {}
    for name, key in _REST_TEXT.items():
        if key in rest and rest[key] not in (None, ""):
            config[name] = rest[key]
    for name, key in _REST_FLAGS.items():
        if key in rest:
            config[name] = _FLAGS[name][0 if rest[key] else 1]
    for name, key in (("traversability_scope", "traversabilityScope"), ("filter_scope", "filterScope")):
        if key in rest:
            config[name] = _back(_REST_SCOPES, rest[key]) or "BOTH_JUNCTIONS_AND_EDGES"
    for name, key in (("condition_barriers", "conditionBarriers"), ("filter_barriers", "filterBarriers"),
                      ("output_conditions", "outputConditions")):
        if rest.get(key):
            config[name] = [_condition_row(i) for i in rest[key]]
    for name, key in (("function_barriers", "functionBarriers"), ("filter_function_barriers", "filterFunctionBarriers")):
        if rest.get(key):
            config[name] = [[i["functionType"].upper(), i["networkAttributeName"],
                             _back(_REST_OPERATORS, i["operator"]) or i["operator"], i.get("value"),
                             bool(i.get("useLocalValues"))] for i in rest[key]]
    if rest.get("functions"):
        rows = []
        for i in rest["functions"]:
            c = _condition_row(i["conditions"][0]) if i.get("conditions") else [None] * 5
            rows.append([i["functionType"].upper(), i["networkAttributeName"], c[0], c[1], c[2], c[3],
                         i.get("functionName") or None])
        config["functions"] = rows
    if rest.get("propagators"):
        config["propagators"] = [[i["networkAttributeName"], i.get("substitutionAttributeName") or None,
                                  _back(_untrace.PROPAGATORS, i["propagatorFunctionType"]) or i["propagatorFunctionType"],
                                  _back(_REST_OPERATORS, i["operator"]) or i["operator"], i.get("value"),
                                  i.get("propagatedAttributeName") or None] for i in rest["propagators"]]
    nearest = rest.get("nearestNeighbor") or {}
    if nearest.get("count", -1) not in (-1, None):
        config.update({"filter_nearest": "FILTER_BY_NEAREST", "nearest_count": nearest["count"],
                       "nearest_cost_network_attribute": nearest.get("costNetworkAttributeName"),
                       "nearest_categories": nearest.get("nearestCategories") or [],
                       "nearest_assets": nearest.get("nearestAssets") or []})
    if rest.get("outputFilters"):
        config["output_assettypes"] = [i["assetType"] for i in rest["outputFilters"] if i.get("assetType")]
    return config


def _store_config(network, name, trace_type, description, tags, config, result_types, creator=None):
    if network.q("select 1 from grapy_un_trace_config where network = ? and lower(name) = lower(?)", network.name,
                 name).fetchone():
        raise ExecuteError(f"ERROR 000258: the trace configuration '{name}' exists already.")
    network.q("insert into grapy_un_trace_config values (?, ?, ?, ?, ?, ?, ?, ?)", network.name, name,
              creator or os.environ.get("USERNAME") or os.environ.get("USER") or "", description or "", trace_type,
              json.dumps(config), json.dumps(result_types), json.dumps(tags))


def AddTraceConfiguration(in_utility_network, trace_config_name, trace_type="CONNECTED", description=None, tags=None,
                          domain_network=None, tier=None, target_tier=None, subnetwork_name=None,
                          shortest_path_network_attribute_name=None, include_containers="EXCLUDE_CONTAINERS",
                          include_content="EXCLUDE_CONTENT", include_structures="EXCLUDE_STRUCTURES",
                          include_barriers="INCLUDE_BARRIERS", validate_consistency="DO_NOT_VALIDATE_CONSISTENCY",
                          condition_barriers=None, function_barriers=None,
                          traversability_scope="BOTH_JUNCTIONS_AND_EDGES", filter_barriers=None,
                          filter_function_barriers=None, filter_scope="BOTH_JUNCTIONS_AND_EDGES",
                          filter_bitset_network_attribute_name=None, filter_nearest="DO_NOT_FILTER", nearest_count=None,
                          nearest_cost_network_attribute=None, nearest_categories=None, nearest_assets=None,
                          functions=None, propagators=None, output_assettypes=None, output_conditions=None,
                          include_isolated_features="EXCLUDE_ISOLATED_FEATURES",
                          ignore_barriers_at_starting_points="DO_NOT_IGNORE_BARRIERS_AT_STARTING_POINTS",
                          include_up_to_first_spatial_container="DO_NOT_INCLUDE_UP_TO_FIRST_SPATIAL_CONTAINER",
                          result_types=None, allow_indeterminate_flow="TRACE_INDETERMINATE_FLOW",
                          validate_locatability="DO_NOT_VALIDATE_LOCATABILITY",
                          use_digitized_direction="IGNORE_DIGITIZED_DIRECTION",
                          synthesize_geometries="DO_NOT_SYNTHESIZE_GEOMETRIES", num_paths=None, max_hops=None,
                          circuit_name=None, infer_connectivity="DO_NOT_INFER_CONNECTIVITY"):
    """A named trace configuration kept with the network (version 5 or later, as the page says), for Trace's
    ``use_trace_config`` and the export and import tools. Its creator is the operating system user, as in a
    single-user deployment. The page's rules are checked: the domain network for the subnetwork-based and circuit types
    and the tier for the first four, a shortest path attribute for a shortest path trace, no subtract function in a
    subnetwork controllers or loops trace; telecom settings are refused as Trace refuses them."""
    given = dict(locals())
    kind = _word(trace_type, "CONNECTED")
    if kind not in _ALL_TRACE_TYPES:
        raise ExecuteError(f"ERROR 000800: '{trace_type}' is not a trace type.")
    if kind == "CIRCUIT" or _given(circuit_name) or _flag(infer_connectivity, "INFER_CONNECTIVITY",
                                                          "DO_NOT_INFER_CONNECTIVITY", "DO_NOT_INFER_CONNECTIVITY"):
        raise NotBuiltError(f"arcpy.un.AddTraceConfiguration: the circuit trace {_TELECOM}.")
    if kind in ("SUBNETWORK", "SUBNETWORK_CONTROLLERS", "UPSTREAM", "DOWNSTREAM") and not _flag(
            use_digitized_direction, "USE_DIGITIZED_DIRECTION", "IGNORE_DIGITIZED_DIRECTION",
            "IGNORE_DIGITIZED_DIRECTION") and not (_given(domain_network) and _given(tier)):
        raise ExecuteError(f"ERROR 000735: a {kind.lower()} trace configuration needs a domain network and a tier.")
    if kind == "SHORTEST_PATH" and not _given(shortest_path_network_attribute_name):
        raise ExecuteError("ERROR 000735: a shortest path trace configuration needs shortest_path_network_attribute_name.")
    for name in ("functions", "function_barriers", "filter_function_barriers"):
        for row in _utility.value_table(given[name], 7 if name == "functions" else 5):
            _untrace._function_word(row[0], kind)
    config = {k: given[k] for k in _CONFIG if k != "result_types" and given[k] is not None}
    results = [_word(t, "") for t in _rows(result_types)] or ["SELECTION"]
    with _utility.Network(_path(in_utility_network)) as network:
        if int(network.version) < 5:
            raise ExecuteError("ERROR 002090: trace configurations need utility network version 5 or later.")
        if _given(domain_network):
            network.domain(domain_network)
        if _given(tier):
            network.tier(tier)
        _store_config(network, str(trace_config_name), kind, description, [str(t) for t in _rows(tags)], config, results)
    return Result(_path(in_utility_network))


def DeleteTraceConfiguration(in_utility_network, trace_config_name):
    """Named trace configurations deleted, permanently."""
    with _utility.Network(_path(in_utility_network)) as network:
        for name in _rows(trace_config_name):
            gone = network.q("delete from grapy_un_trace_config where network = ? and lower(name) = lower(?)",
                             network.name, str(name).strip("'\"")).rowcount
            if not gone:
                raise ExecuteError(f"ERROR 000732: '{name}' is not a trace configuration of the network.")
    return Result(_path(in_utility_network))


def ExportTraceConfigurations(in_utility_network, trace_config_name, out_json_file):
    """Named trace configurations written to a .json array of {creator, name, description, traceType, configuration,
    resultTypes, tags}, as the page describes the file."""
    out = []
    with _utility.Network(_path(in_utility_network)) as network:
        for name in _rows(trace_config_name):
            row = network.q("select name, creator, description, trace_type, configuration, result_types, tags from "
                            "grapy_un_trace_config where network = ? and lower(name) = lower(?)", network.name,
                            str(name).strip("'\"")).fetchone()
            if row is None:
                raise ExecuteError(f"ERROR 000732: '{name}' is not a trace configuration of the network.")
            config = json.loads(row[4])
            results = [{"type": _REST_RESULTS[t], "includeGeometry": _on(config, "include_geometry"),
                        "includePropagatedValues": False, "networkAttributeNames": [], "diagramTemplateName": "",
                        "resultTypeFields": []} for t in json.loads(row[5] or "[]")]
            out.append({"creator": row[1], "name": row[0], "description": row[2], "traceType": _TRACE_CODES[row[3]],
                        "configuration": json.dumps(_to_rest(config)), "resultTypes": json.dumps(results),
                        "tags": json.dumps(json.loads(row[6] or "[]"))})
    _unjson.write(_path(out_json_file), out)
    return Result(_path(out_json_file))


def ImportTraceConfigurations(in_utility_network, in_json_file):
    """Trace configurations read from a .json file of the exported layout; a name the network has is an error."""
    with open(_path(in_json_file), encoding="utf-8-sig") as read:
        items = json.load(read)
    if not isinstance(items, list):
        raise ExecuteError("ERROR 000800: a trace configuration file is a JSON array.")
    codes = {v: k for k, v in _TRACE_CODES.items()}
    with _utility.Network(_path(in_utility_network)) as network:
        if int(network.version) < 5:
            raise ExecuteError("ERROR 002090: trace configurations need utility network version 5 or later.")
        for item in items:
            def parsed(value):
                return json.loads(value) if isinstance(value, str) else value
            kind = codes.get(int(item["traceType"])) if str(item.get("traceType", "")).isdigit() else \
                _word(item.get("traceType"), "CONNECTED")
            if kind is None:
                raise ExecuteError(f"ERROR 000800: '{item.get('traceType')}' is not a trace type.")
            results = [_back(_REST_RESULTS, r["type"]) or "SELECTION" for r in parsed(item.get("resultTypes")) or []]
            _store_config(network, item["name"], kind, item.get("description"), parsed(item.get("tags")) or [],
                          _from_rest(parsed(item.get("configuration")) or {}), results, item.get("creator"))
    return Result(_path(in_utility_network))


# ---- subnetworks ---------------------------------------------------------------------------------------------------

def _boolean(value, yes, no, default):
    return _flag(value, yes, no, default)


def SetSubnetworkDefinition(in_utility_network, domain_network, tier_name,
                            support_disjoint_subnetwork=None, valid_devices=None, valid_subnetwork_controller=None,
                            valid_lines=None, aggregated_line=None, diagram_template=None, summaries=None,
                            condition_barriers=None, function_barriers=None, include_barriers="INCLUDE_BARRIERS",
                            traversability_scope="BOTH_JUNCTIONS_AND_EDGES", propagators=None,
                            update_structure_features="UPDATE", update_container_features="UPDATE",
                            edit_mode_for_default_version="WITHOUT_EVENTING",
                            edit_mode_for_named_version="WITHOUT_EVENTING", valid_junctions=None,
                            valid_junction_objects=None, valid_junction_object_subnetwork_controller=None,
                            valid_edge_objects=None, manage_subnetwork_isdirty=None,
                            include_containers="EXCLUDE_CONTAINERS", include_content="EXCLUDE_CONTENT",
                            include_structures="EXCLUDE_STRUCTURES",
                            validate_locatability="DO_NOT_VALIDATE_LOCATABILITY"):
    """A tier's subnetwork definition set or replaced, with the topology disabled, as the page says: the valid asset
    types (``AssetGroup/AssetType``), the aggregated lines (a subset of the valid lines), summaries written to SubnetLine
    fields, condition and function barriers, propagators and their propagated fields, and the update, container and
    Is dirty options. Disjoint subnetworks are supported by default in a hierarchical tier only, and Is dirty is managed
    by default unless the tier has no controllers. The edit modes are kept and change nothing: grapy writes subnetwork
    names in place, as WITHOUT_EVENTING does (INFERRED, ADR-231 §5.9 (m)). The diagram templates named are those
    UpdateSubnetwork keeps the subnetwork system diagrams of (ADR-232 §5.6)."""
    from . import _diagram, _unsubnet
    templates = [str(t).strip("'\"") for t in _rows(diagram_template)]
    with _utility.Network(_path(in_utility_network)) as network:
        network.require_disabled("SetSubnetworkDefinition")
        domain = network.domain(domain_network)
        if domain[2] != "Traditional":
            raise ExecuteError(f"ERROR 000732: '{domain_network}' is not a traditional domain network.")
        tier = network.tier(tier_name)
        if tier[0] != domain[0]:
            raise ExecuteError(f"ERROR 000732: the tier '{tier_name}' is not in '{domain[0]}'.")
        version = int(network.version)
        hierarchical = domain[3] == "HIERARCHICAL"
        found = _unsubnet.default_definition()
        found["support_disjoint_subnetwork"] = True if hierarchical else _boolean(
            support_disjoint_subnetwork, "SUPPORT_DISJOINT", "NO_DISJOINT", "NO_DISJOINT")
        given = {"valid_devices": valid_devices, "valid_subnetwork_controller": valid_subnetwork_controller,
                 "valid_lines": valid_lines, "aggregated_line": aggregated_line, "valid_junctions": valid_junctions,
                 "valid_junction_objects": valid_junction_objects,
                 "valid_junction_object_subnetwork_controller": valid_junction_object_subnetwork_controller,
                 "valid_edge_objects": valid_edge_objects}
        for name, value in given.items():
            if _rows(value) and version < 4 and name not in ("valid_devices", "valid_subnetwork_controller",
                                                             "valid_lines", "aggregated_line"):
                raise ExecuteError(f"ERROR 002090: {name} needs utility network version 4 or later.")
            found[name] = _unsubnet.asset_list(network, domain[0], _unsubnet.VALID_LISTS[name], _rows(value))
        if not {tuple(a) for a in found["aggregated_line"]} <= {tuple(a) for a in found["valid_lines"]}:
            raise ExecuteError("ERROR 002105: the aggregated lines must be a subset of the valid lines.")
        subnet_line = network.q("select class_name from grapy_un_source where network = ? and domain = ? and role = "
                                "'SubnetLine'", network.name, domain[0]).fetchone()[0]
        columns = {c.name.lower(): c for c in _gpkg.describe(network.connection, subnet_line)[0]}
        found["summaries"] = []
        for row in _utility.value_table(summaries, 7):
            function = _untrace._function_word(row[0])
            _untrace._attribute(network, row[1])
            if not row[6] or str(row[6]).lower() not in columns:
                raise ExecuteError(f"ERROR 000728: the summary attribute '{row[6]}' is not a field of "
                                   f"{subnet_line.rsplit('/', 1)[-1]}.")
            if function == "AVERAGE" and columns[str(row[6]).lower()].type not in ("Double", "Single"):
                raise ExecuteError("ERROR 002105: an average is summarised into a double field.")
            found["summaries"].append([function] + list(row[1:]))
        found["condition_barriers"] = _utility.value_table(condition_barriers, 5)
        for row in found["condition_barriers"]:
            if str(row[0]).lower() == "category" and str(row[2] or "SPECIFIC_VALUE").upper() != "SPECIFIC_VALUE":
                raise ExecuteError("ERROR 002105: a Category condition barrier's type is SPECIFIC_VALUE.")
        found["function_barriers"] = _utility.value_table(function_barriers, 5)
        for row in found["function_barriers"]:
            _untrace._function_word(row[0])
        found["include_barriers"] = _boolean(include_barriers, "INCLUDE_BARRIERS", "EXCLUDE_BARRIERS", "INCLUDE_BARRIERS")
        found["traversability_scope"] = _word(traversability_scope, "BOTH_JUNCTIONS_AND_EDGES")
        found["propagators"] = _utility.value_table(propagators, 6)
        for row in found["propagators"]:
            if str(row[2] or "").upper() not in _untrace.PROPAGATORS:
                raise ExecuteError(f"ERROR 000800: '{row[2]}' is not a propagator function.")
        options = (("update_structure_features", update_structure_features, "UPDATE", "NOT_UPDATE", "UPDATE", 4),
                   ("update_container_features", update_container_features, "UPDATE", "NOT_UPDATE", "UPDATE", 4),
                   ("include_containers", include_containers, "INCLUDE_CONTAINERS", "EXCLUDE_CONTAINERS",
                    "EXCLUDE_CONTAINERS", 5),
                   ("include_content", include_content, "INCLUDE_CONTENT", "EXCLUDE_CONTENT", "EXCLUDE_CONTENT", 5),
                   ("include_structures", include_structures, "INCLUDE_STRUCTURES", "EXCLUDE_STRUCTURES",
                    "EXCLUDE_STRUCTURES", 5),
                   ("validate_locatability", validate_locatability, "VALIDATE_LOCATABILITY",
                    "DO_NOT_VALIDATE_LOCATABILITY", "DO_NOT_VALIDATE_LOCATABILITY", 5))
        for name, value, yes, no, default, needs in options:
            found[name] = _boolean(value, yes, no, default)
            if found[name] != (default == yes) and version < needs:
                raise ExecuteError(f"ERROR 002090: {name} needs utility network version {needs} or later.")
        for name, value in (("edit_mode_for_default_version", edit_mode_for_default_version),
                            ("edit_mode_for_named_version", edit_mode_for_named_version)):
            word = _word(value, "WITHOUT_EVENTING")
            if word not in ("WITHOUT_EVENTING", "WITH_EVENTING"):
                raise ExecuteError(f"ERROR 000800: {name} is WITHOUT_EVENTING or WITH_EVENTING.")
            found[name] = word
        has_controllers = bool(network.q("select 1 from grapy_un_controller where network = ? and lower(tier) = "
                                         "lower(?)", network.name, tier[1]).fetchone())
        found["manage_subnetwork_isdirty"] = _boolean(manage_subnetwork_isdirty, "MANAGE", "NOT_MANAGE",
                                                      "MANAGE" if has_controllers else "NOT_MANAGE")
        _diagram.ensure(network.connection)
        found["diagram_template"] = [_diagram.template(network, t)["name"] for t in templates]
        network.q("insert or replace into grapy_un_subnetwork_definition values (?, ?, ?, ?)", network.name, domain[0],
                  tier[1], json.dumps(found))
    return Result(_path(in_utility_network))


def _extra(condition_barriers, function_barriers, include_barriers, traversability_scope, propagators):
    extra = {"condition_barriers": _utility.value_table(condition_barriers, 5),
             "function_barriers": _utility.value_table(function_barriers, 5),
             "propagators": _utility.value_table(propagators, 6)}
    if _given(include_barriers):
        extra["include_barriers"] = _flag(include_barriers, "INCLUDE_BARRIERS", "EXCLUDE_BARRIERS", "INCLUDE_BARRIERS")
    if _given(traversability_scope):
        extra["traversability_scope"] = _word(traversability_scope, "BOTH_JUNCTIONS_AND_EDGES")
    return extra


def UpdateSubnetwork(in_utility_network, domain_network, tier, all_subnetworks_in_tier="SPECIFIC_SUBNETWORK",
                     subnetwork_name=None, continue_on_failure="STOP_ON_FAILURE", condition_barriers=None,
                     function_barriers=None, include_barriers=None, traversability_scope=None, propagators=None):
    """Subnetworks of a tier updated, as the page says: each traced from its controllers with the tier's subnetwork
    definition (and this tool's own barriers added to it), its name written to the subnetwork field of the features it
    reaches and taken from those it no longer reaches, their Is connected set True (Unknown where the name is cleared),
    propagated values written to their fields, containers and structures given its supported subnetwork name, and its
    SubnetLine row made or replaced with the aggregated lines, the controllers' names and the summaries, ISDIRTY clean.
    A subnetwork with no controller left is deleted. The topology must be enabled and no dirty area may meet the
    subnetwork; a failing subnetwork is marked invalid, and stops the tool unless CONTINUE_ON_FAILURE."""
    from . import _unsubnet
    every = _word(all_subnetworks_in_tier, "SPECIFIC_SUBNETWORK")
    if every not in ("ALL_SUBNETWORKS_IN_TIER", "SPECIFIC_SUBNETWORK"):
        raise ExecuteError("ERROR 000800: all_subnetworks_in_tier is ALL_SUBNETWORKS_IN_TIER or SPECIFIC_SUBNETWORK.")
    keep_going = _flag(continue_on_failure, "CONTINUE_ON_FAILURE", "STOP_ON_FAILURE", "STOP_ON_FAILURE")
    extra = _extra(condition_barriers, function_barriers, include_barriers, traversability_scope, propagators)
    path = _path(in_utility_network)
    with _utility.Network(path) as network:
        network.require_enabled("UpdateSubnetwork")
        domain = network.domain(domain_network)
        if domain[2] != "Traditional":
            raise ExecuteError("ERROR 000732: Update Subnetwork is supported only for traditional domain networks.")
        found_tier = network.tier(tier)
        if found_tier[0] != domain[0]:
            raise ExecuteError(f"ERROR 000732: the tier '{tier}' is not in '{domain[0]}'.")
        if every == "ALL_SUBNETWORKS_IN_TIER":
            names = sorted(_unsubnet.controller_names(network, found_tier[1]) |
                           set(_unsubnet.rows(network, found_tier[1])), key=str.lower)
        else:
            if not _given(subnetwork_name):
                raise ExecuteError("ERROR 000735: subnetwork_name is required to update a specific subnetwork.")
            known = {n.lower(): n for n in _unsubnet.controller_names(network, found_tier[1]) |
                     set(_unsubnet.rows(network, found_tier[1]))}
            if str(subnetwork_name).lower() not in known:
                raise ExecuteError(f"ERROR 002080: the tier '{found_tier[1]}' has no subnetwork '{subnetwork_name}'.")
            names = [known[str(subnetwork_name).lower()]]
    for name in names:
        with _utility.Network(path) as network:
            reason = _unsubnet.update(network, domain[0], found_tier[1], name, extra)
        if reason:
            if not keep_going:
                raise ExecuteError(reason)
            AddWarning(reason)
    from .nd import _system_diagrams
    _system_diagrams(path, found_tier[1], names)
    return Result(path)


def ExportSubnetwork(in_utility_network, domain_network, tier, subnetwork_name, export_acknowledged="NO_ACKNOWLEDGE",
                     out_json_file=None, condition_barriers=None, function_barriers=None, include_barriers=None,
                     traversability_scope=None, propagators=None, include_geometry="EXCLUDE_GEOMETRY", result_types=None,
                     result_network_attributes=None, result_fields=None,
                     include_domain_descriptions="EXCLUDE_DOMAIN_DESCRIPTIONS", related_record_fields=None,
                     include_flow_directions="EXCLUDE_FLOW_DIRECTIONS",
                     include_propagated_values="EXCLUDE_PROPAGATED_VALUES"):
    """A clean subnetwork written to a .json file: the subnetwork's identity and its ``controllers``, beside the
    result types asked for in the layout of Trace's JSON (``_unjson``; INFERRED, ADR-231 §5.9 (l)). With no result type
    only the controllers are written. ACKNOWLEDGE, with the topology enabled, stamps LASTACKEXPORTSUBNETWORK on its
    Subnetworks row and SubnetLine, and removes the row of a deleted subnetwork, as the page says."""
    from . import _unsubnet
    acknowledge = _flag(export_acknowledged, "ACKNOWLEDGE", "NO_ACKNOWLEDGE", "NO_ACKNOWLEDGE")
    if not _given(out_json_file):
        raise ExecuteError("ERROR 000735: out_json_file is required.")
    types = [_word(t, "") for t in _rows(result_types)]
    for t in types:
        if t not in ("CONNECTIVITY", "FEATURES", "CONTAINMENT_AND_ATTACHMENT_ASSOCIATIONS", "RELATED_RECORDS"):
            raise ExecuteError(f"ERROR 000800: '{t}' is not a result type of Export Subnetwork.")
    extra = _extra(condition_barriers, function_barriers, include_barriers, traversability_scope, propagators)
    with _utility.Network(_path(in_utility_network)) as network:
        if acknowledge:
            network.require_enabled("ExportSubnetwork with ACKNOWLEDGE")
        domain = network.domain(domain_network)
        found_tier = network.tier(tier)
        if found_tier[0] != domain[0]:
            raise ExecuteError(f"ERROR 000732: the tier '{tier}' is not in '{domain[0]}'.")
        row = network.q("select name, isdirty, isdeleted from grapy_un_subnetwork where network = ? and lower(tier) = "
                        "lower(?) and lower(name) = lower(?)", network.name, found_tier[1], str(subnetwork_name)).fetchone()
        if row is None or row[1] != _unsubnet.CLEAN:
            raise ExecuteError(f"ERROR 002102: the subnetwork '{subnetwork_name}' is not clean; run UpdateSubnetwork "
                               "first.")
        name = row[0]
        document = {"domainNetworkName": domain[0], "tierName": found_tier[1], "subnetworkName": name,
                    "isDeleted": bool(row[2]), "controllers": []}
        if not row[2]:
            graph = _utility.build(network)
            settings = _unsubnet.definition(network, found_tier[1])
            outcome, keys = _unsubnet.trace(network, graph, domain[0], found_tier[1], name, settings, extra)
            values = _utility.Values(graph)
            controllers = []
            for i in sorted(outcome.ids):
                e = graph.elements[i]
                found = values.controller(e)
                if found and found[1].lower() == found_tier[1].lower() and found[2].lower() == name.lower():
                    data = graph.features[e.feature][1]
                    controllers.append({"networkSourceId": e.source, "globalId": e.globalid, "objectId": e.oid,
                                        "terminalId": _utility.terminal_of(e), "assetGroupCode": data["group"],
                                        "assetTypeCode": data["type"], "subnetworkControllerName": found[0]})
            writer = _unjson.Writer(network, graph, outcome, outcome.ids, keys,
                                    _flag(include_geometry, "INCLUDE_GEOMETRY", "EXCLUDE_GEOMETRY", "EXCLUDE_GEOMETRY"),
                                    _flag(include_domain_descriptions, "INCLUDE_DOMAIN_DESCRIPTIONS",
                                          "EXCLUDE_DOMAIN_DESCRIPTIONS", "EXCLUDE_DOMAIN_DESCRIPTIONS"),
                                    [str(a).strip("'\"") for a in _rows(result_network_attributes)],
                                    _utility.value_table(result_fields, 2), _utility.value_table(related_record_fields, 2),
                                    _flag(include_flow_directions, "INCLUDE_FLOW_DIRECTIONS", "EXCLUDE_FLOW_DIRECTIONS",
                                          "EXCLUDE_FLOW_DIRECTIONS"),
                                    _flag(include_propagated_values, "INCLUDE_PROPAGATED_VALUES",
                                          "EXCLUDE_PROPAGATED_VALUES", "EXCLUDE_PROPAGATED_VALUES"))
            document["controllers"] = controllers
            document = writer.document(types, document)
        _unjson.write(_path(out_json_file), document)
        if acknowledge:
            stamp = _unsubnet.now()
            table = network.q("select class_name from grapy_un_source where network = ? and domain = ? and role = "
                              "'SubnetLine'", network.name, domain[0]).fetchone()[0]
            network.q(f'update "{table}" set LASTACKEXPORTSUBNETWORK = ? where DOMAINNETWORKNAME = ? and TIERNAME = ? and '
                      'SUBNETWORKNAME = ?', stamp, domain[0], found_tier[1], name)
            if row[2]:
                network.q("delete from grapy_un_subnetwork where network = ? and tier = ? and name = ?", network.name,
                          found_tier[1], name)
            else:
                network.q("update grapy_un_subnetwork set last_ack_export = ? where network = ? and tier = ? and name = ?",
                          stamp, network.name, found_tier[1], name)
    return Result(_path(in_utility_network))


def UpdateIsConnected(in_utility_network):
    """ISCONNECTED written on every feature of each traditional domain network: 1 (True) where connectivity reaches a
    subnetwork controller, 2 (False) where it does not (INFERRED codes, ADR-231 §5.9 (m)). The topology must be
    enabled."""
    from . import _unsubnet
    with _utility.Network(_path(in_utility_network)) as network:
        network.require_enabled("UpdateIsConnected")
        _unsubnet.update_is_connected(network)
    return Result(_path(in_utility_network))


# ---- asset type settings and topology maintenance ------------------------------------------------------------------

def SetEdgeConnectivity(in_utility_network, domain_network, line_featureclass, assetgroup, assettype,
                        edge_connectivity):
    """An asset type's edge connectivity policy: ANY_VERTEX (the default) connects at any vertex, END_VERTEX only at
    the end vertices. A line or edge object asset type; the topology disabled."""
    word = str(edge_connectivity or "").strip().upper().replace(" ", "")
    word = {"ANYVERTEX": "ANY_VERTEX", "ENDVERTEX": "END_VERTEX"}.get(word.replace("_", ""), word)
    if word not in ("ANY_VERTEX", "END_VERTEX"):
        raise ExecuteError("ERROR 000800: edge_connectivity is ANY_VERTEX or END_VERTEX.")
    with _utility.Network(_path(in_utility_network)) as network:
        network.require_disabled("SetEdgeConnectivity")
        domain = network.domain(domain_network)
        class_name = network.table(line_featureclass)
        if network.domain_of(class_name) != domain[0] or network.role(class_name) not in ("Line", "StructureLine",
                                                                                          "EdgeObject"):
            raise ExecuteError(f"ERROR 000732: '{line_featureclass}' is not a line class or edge object table of "
                               f"'{domain[0]}'.")
        group = network.asset_group(class_name, assetgroup)
        network.set_setting(class_name, group, network.asset_type(class_name, group, assettype), "edge_connectivity",
                            word)
    return Result(_path(in_utility_network))


def SetAssociationRole(in_utility_network, domain_network, featureclass, assetgroup, assettype, association_role_type,
                       association_deletion_semantics=None, view_scale=None, split_content="DO_NOT_SPLIT"):
    """An asset type's association role — CONTAINER, STRUCTURE or NONE — and deletion semantics (CASCADE,
    SET_TO_NONE, RESTRICTED). The topology disabled; a role removed only before it is first enabled; a view scale not on
    junction or edge objects; split content only for a container line. Once a role is set, only a container contains and
    only a structure has attachments; the deletion semantics act when a utility network tool next finds the delete, and
    the view scale and split content are kept for a map and an editor grapy does not have (INFERRED, ADR-231 §5.9 (k))."""
    role = _word(association_role_type, "")
    if role not in ("CONTAINER", "STRUCTURE", "NONE"):
        raise ExecuteError("ERROR 000800: association_role_type is CONTAINER, STRUCTURE or NONE.")
    deletion = _word(association_deletion_semantics, "CASCADE" if role != "NONE" else "")
    if role != "NONE" and deletion not in ("CASCADE", "SET_TO_NONE", "RESTRICTED"):
        raise ExecuteError("ERROR 000800: association_deletion_semantics is CASCADE, SET_TO_NONE or RESTRICTED.")
    split = _flag(split_content, "SPLIT", "DO_NOT_SPLIT", "DO_NOT_SPLIT")
    with _utility.Network(_path(in_utility_network)) as network:
        network.require_disabled("SetAssociationRole")
        domain = network.domain(domain_network)
        class_name = network.table(featureclass)
        if network.domain_of(class_name) != domain[0]:
            raise ExecuteError(f"ERROR 000732: '{featureclass}' is not in '{domain[0]}'.")
        kind_of = network.role(class_name)
        group = network.asset_group(class_name, assetgroup)
        kind = network.asset_type(class_name, group, assettype)
        if role == "STRUCTURE" and domain[0] != _utility.STRUCTURE:
            raise ExecuteError("ERROR 002092: the structure role is for asset types of the structure network.")
        if _given(view_scale) and kind_of in _utility.OBJECT_ROLES:
            raise ExecuteError("ERROR 000800: a view scale does not apply to junction or edge objects.")
        if split and not (role == "CONTAINER" and kind_of in _utility.LINE_ROLES):
            raise ExecuteError("ERROR 000800: split content applies only to container line features.")
        before = network.setting(class_name, group, kind, "association_role")
        if network.ever_enabled and before is not None and before["role"] != "NONE" and role != before["role"]:
            raise ExecuteError("ERROR 002092: a container or structure role can be removed only before the network "
                               "topology is first enabled.")
        network.set_setting(class_name, group, kind, "association_role",
                            {"role": role, "deletion": deletion or None,
                             "view_scale": float(view_scale) if _given(view_scale) else None, "split_content": split})
    return Result(_path(in_utility_network))


def RebuildNetworkTopology(in_utility_network, extent=None):
    """The network topology rebuilt in an extent: every feature in it taken into the record again whether its area is
    dirty or not, and the errors found again. The topology must be enabled. Dirty areas are not clipped (INFERRED,
    ADR-231 §5.9 (d))."""
    with _utility.Network(_path(in_utility_network)) as network:
        network.require_enabled("RebuildNetworkTopology")
        _utility.refresh(network, extent=_extent(extent, "RebuildNetworkTopology"), everything=True)
        _utility.write_dirty_areas(network)
    return Result(_path(in_utility_network))


def EnableClusterKeys(in_utility_network):
    """A CLUSTERKEY field (long) added to every class and table of the network; its values are CalculateClusterKeys'."""
    with _utility.Network(_path(in_utility_network)) as network:
        for class_name, _, role in network.sources().values():
            if "clusterkey" not in {c.name.lower() for c in _gpkg.describe(network.connection, class_name)[0]}:
                network.q(f'alter table "{class_name}" add column CLUSTERKEY INTEGER')
    return Result(_path(in_utility_network))


_CLUSTERS = ("cluster keys as the connected components of the network topology, its connectivity and its containment "
             "and attachment associations together, numbered from 1 in the order of each component's first feature",
             "Hopcroft and Tarjan 1973, Algorithm 447: Efficient Algorithms for Graph Manipulation")


@equivalent(*_CLUSTERS)
def CalculateClusterKeys(in_utility_network, in_overwrite_values="DO_NOT_OVERWRITE_VALUES", in_extent=None):
    """EQUIVALENT (ADR-224 §5.6): the page says cluster keys group features and objects for the topology but not how
    they are computed; grapy groups what is connected — and an object with its container or structure, as the page's
    "especially with nonspatial junction and edge objects" asks. Over the topology as last enabled, the topology
    disabled, every class carrying CLUSTERKEY; features whose shape meets the extent, and only empty keys unless
    OVERWRITE_VALUES."""
    overwrite = _flag(in_overwrite_values, "OVERWRITE_VALUES", "DO_NOT_OVERWRITE_VALUES", "DO_NOT_OVERWRITE_VALUES")
    with _utility.Network(_path(in_utility_network)) as network:
        network.require_disabled("CalculateClusterKeys")
        window = _extent(in_extent, "CalculateClusterKeys")
        sources = network.sources()
        for class_name, _, _ in sources.values():
            if "clusterkey" not in {c.name.lower() for c in _gpkg.describe(network.connection, class_name)[0]}:
                raise ExecuteError(f"ERROR 002106: {class_name.rsplit('/', 1)[-1]} has no CLUSTERKEY field; run "
                                   "EnableClusterKeys first.")
        graph = _utility.build(network)
        links = graph.undirected()
        by_globalid = {g: k for k, (g, _) in graph.features.items()}
        joined = {}
        for row in graph.associations:
            a = by_globalid.get(str(row["from_globalid"]).upper())
            b = by_globalid.get(str(row["to_globalid"]).upper())
            if a and b:
                joined.setdefault(a, set()).add(b)
                joined.setdefault(b, set()).add(a)
        feature_links = {}
        for a, bs in links.items():
            fa = graph.elements[a].feature or (0, a)
            for b in bs:
                fb = graph.elements[b].feature or (0, b)
                if fa != fb:
                    feature_links.setdefault(fa, set()).add(fb)
        for a, bs in joined.items():
            feature_links.setdefault(a, set()).update(bs)
        key_of, number = {}, 0
        for key in sorted(graph.features):
            if key in key_of:
                continue
            number += 1
            reached, _ = _utility.reach([key], feature_links, lambda k: False)
            for k in reached:
                key_of[k] = number
        from shapely.geometry import box
        area = box(*window) if window else None
        for key, value in key_of.items():
            if not isinstance(key[0], int) or key[0] not in sources:
                continue
            class_name = sources[key[0]][0]
            data = graph.features[key][1]
            if area is not None and (not data.get("wkt") or not shapely.from_wkt(data["wkt"]).intersects(area)):
                continue
            where = "" if overwrite else " and CLUSTERKEY is null"
            network.q(f'update "{class_name}" set CLUSTERKEY = ? where "{network.oid(class_name)}" = ?{where}', value,
                      key[1])
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

_RESOLUTIONS = ("works on the error resolution database Analyze Network Data writes — a mobile geodatabase of error, "
                "feature and candidate-rule tables and a layer file, whose layout the pages do not give beyond an "
                "ErrorAction field — so grapy cannot write it nor read one without inventing Esri's schema; grapy's "
                "topology errors are in <network>_DirtyAreas and the associations' ERRORCODE (ADR-231 §5.4)")
_REASONS = {
    _TELECOM: ("AddTelecomDomainNetwork", "AddColorScheme", "AddColorSet", "AddWavelengthScheme", "DeleteColorScheme",
               "ExportCircuitDefinitions", "ExportCircuits", "ImportCircuitDefinitions", "SetCircuitProperties",
               "SetTelecomObjectCombinePolicy", "SetTelecomObjectDividePolicy", "VerifyCircuits"),
    _RESOLUTIONS: ("AnalyzeNetworkData", "ApplyErrorResolutions"),
    "checks or repairs the consistency of Esri's persisted network index, which grapy does not hold: grapy's "
    "topology is rebuilt from its record at every trace (ADR-231 §5.4)": ("VerifyNetworkTopology",
                                                                         "RepairNetworkTopology"),
    "loads an asset package, Esri's geodatabase layout of a whole utility network, which grapy does not read: its "
    "networks are kept in grapy's own tables (ADR-231 §5.2)": ("MigrateToUtilityNetwork",),
    "has no page grapy could read: doc.esri.com/en/arcgis-pro/latest/tool-reference/utility-networks/"
    "update-utility-network-schema.html answered 404 (checked 2026-10-30)": ("UpdateUtilityNetworkSchema",),
}
_REFUSED = {name: reason for reason, names in _REASONS.items() for name in names}

# Network diagrams (ADR-232): arcpy.un holds arcpy.nd's diagram tools under the same names, and refuses the same ones.
from .nd import REFUSED as _ND_REFUSED  # noqa: E402
from .nd import (  # noqa: E402,F401 — arcpy.un's own names for them
    AddAngleDirectedLayout, AddCollapseContainerByAttributeRule, AddCollapseContainerRule, AddCompressionLayout,
    AddConnectivityAssociationsRule, AddDiagramFeatureCapabilityByAttributeRule, AddDiagramTemplate,
    AddExpandContainerByAttributeRule, AddExpandContainerRule, AddForceDirectedLayout, AddGridLayout,
    AddLinearDispatchLayout, AddMainRingLayout, AddMainlineTreeLayout, AddPartialOverlappingEdgesLayout,
    AddRadialTreeLayout, AddReduceEdgeByAttributeRule, AddReduceJunctionByAttributeRule, AddReduceJunctionRule,
    AddRemoveFeatureByAttributeRule, AddRemoveFeatureRule, AddReshapeDiagramEdgesLayout,
    AddSetRootJunctionByAttributeRule, AddSetStartingPointByAttributeRule, AddSmartTreeLayout, AddSpatialDispatchLayout,
    AddSpatialQueryRule, AddStartIterationRule, AddStopIterationRule, AddStructuralAttachmentsRule, AddTraceRule,
    AlterDiagramProperties, AlterDiagramTemplate, AppendToDiagram, ApplyAngleDirectedLayout, ApplyCompressionLayout,
    ApplyForceDirectedLayout, ApplyGeoPositionsLayout, ApplyGridLayout, ApplyLinearDispatchLayout, ApplyMainRingLayout,
    ApplyMainlineTreeLayout, ApplyPartialOverlappingEdgesLayout, ApplyRadialTreeLayout, ApplySmartTreeLayout,
    ApplySpatialDispatchLayout, ApplyTemplateLayouts, ChangeDiagramsOwner, CreateDiagram, DeleteDiagram,
    DeleteDiagramTemplate, ExportDiagramContent, ExportDiagramTemplateDefinitions, ExtendDiagram,
    GetDiagramTemplateNames, ImportDiagramTemplateDefinitions, MakeDiagramLayer, OverwriteDiagram,
    PurgeTemporaryDiagrams, ReshapeDiagramEdgesLayout, StoreDiagram, UpdateDiagram)

_REFUSED.update(_ND_REFUSED)


def __getattr__(name):
    if name.startswith("_"):
        raise AttributeError(name)
    reason = _REFUSED.get(name)
    message = f"arcpy.un.{name} {reason}." if reason else f"arcpy.un.{name} is not built in grapy yet."

    def missing(*_, **__):
        raise NotBuiltError(message)

    return missing
