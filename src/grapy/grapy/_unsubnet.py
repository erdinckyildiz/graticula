"""Subnetworks — ADR-231's second batch: a tier's subnetwork definition, the subnetworks UpdateSubnetwork keeps, those
ValidateNetworkTopology discovers, ExportSubnetwork's file and UpdateIsConnected.

What a subnetwork is and what each tool writes is what the Set Subnetwork Definition, Update Subnetwork, Export
Subnetwork and Is connected attribute pages say: UpdateSubnetwork traces each subnetwork with its tier's definition,
writes its name to the features it reaches, makes or updates its row of ``<Domain>SubnetLine`` and marks it clean;
ExportSubnetwork writes a clean one to a .json file and, acknowledged, stamps LASTACKEXPORTSUBNETWORK. The pages also
speak of a "Subnetworks table" whose layout they do not give; grapy keeps it as ``grapy_un_subnetwork`` (INFERRED,
ADR-231 §5.9 (m)), beside the SubnetLine rows a user reads. The trace itself is ``_untrace.run``, the only place a flow
is decided.
"""
from __future__ import annotations

import datetime
import json

import shapely
from shapely.ops import linemerge, unary_union

from . import _gpkg, _untrace, _utility
from ._runtime import ExecuteError

DOMAIN_ROLES = ("Device", "Line", "Junction", "JunctionObject", "EdgeObject")
VALID_LISTS = {"valid_devices": "Device", "valid_subnetwork_controller": "Device", "valid_lines": "Line",
               "aggregated_line": "Line", "valid_junctions": "Junction", "valid_junction_objects": "JunctionObject",
               "valid_junction_object_subnetwork_controller": "JunctionObject", "valid_edge_objects": "EdgeObject"}
# INFERRED (ADR-231 §5.9 (m)): ISDIRTY 0 clean, 1 dirty, 2 invalid — the three states the Update Subnetwork page names;
# ISCONNECTED 0 Unknown, 1 True, 2 False — the three values of the Is connected attribute page; a feature in several
# subnetworks of a tier carries their names joined by "::", and one in none carries "Unknown".
CLEAN, DIRTY, INVALID = 0, 1, 2
UNKNOWN, CONNECTED, NOT_CONNECTED = 0, 1, 2
SEPARATOR = "::"


def now():
    return datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.%f")[:-3] + "Z"


def default_definition():
    return {"support_disjoint_subnetwork": False, "summaries": [], "condition_barriers": [], "function_barriers": [],
            "include_barriers": True, "traversability_scope": "BOTH_JUNCTIONS_AND_EDGES", "propagators": [],
            "update_structure_features": True, "update_container_features": True,
            "edit_mode_for_default_version": "WITHOUT_EVENTING", "edit_mode_for_named_version": "WITHOUT_EVENTING",
            "manage_subnetwork_isdirty": True, "include_containers": False, "include_content": False,
            "include_structures": False, "validate_locatability": False,
            **{name: [] for name in VALID_LISTS}}


def definition(network, tier_name):
    """A tier's subnetwork definition, or the defaults a tier has before SetSubnetworkDefinition runs."""
    row = network.q("select definition from grapy_un_subnetwork_definition where network = ? and lower(tier) = lower(?)",
                    network.name, str(tier_name)).fetchone()
    found = default_definition()
    if row:
        found.update(json.loads(row[0]))
    return found


def asset_list(network, domain, role, values):
    """``AssetGroup/AssetType`` names (or ``Class/AssetGroup/AssetType``) of the domain's class of a role, as
    [class, group code, type code]."""
    class_name = network.q("select class_name from grapy_un_source where network = ? and domain = ? and role = ?",
                           network.name, domain, role).fetchone()[0]
    out = []
    for each in values or ():
        parts = [p.strip() for p in str(each).strip("'\"").split("/")]
        if len(parts) == 3:
            if network.table(parts[0]) != class_name:
                raise ExecuteError(f"ERROR 000800: '{each}' is not of {class_name.rsplit('/', 1)[-1]}.")
            parts = parts[1:]
        if len(parts) != 2:
            raise ExecuteError(f"ERROR 000800: '{each}' is written AssetGroup/AssetType.")
        group = network.asset_group(class_name, parts[0])
        out.append([class_name, group, network.asset_type(class_name, group, parts[1])])
    return out


def request(network, domain, tier, name, found, extra=None):
    """The subnetwork trace a tier's definition makes, with a tool's own barrier parameters added to it (INFERRED,
    §5.9 (m))."""
    extra = extra or {}
    return _untrace.Request(
        trace_type="SUBNETWORK", domain_network=domain, tier=tier, subnetwork_name=name,
        condition_barriers=list(found["condition_barriers"]) + list(extra.get("condition_barriers") or []),
        function_barriers=list(found["function_barriers"]) + list(extra.get("function_barriers") or []),
        propagators=list(found["propagators"]) + list(extra.get("propagators") or []),
        include_barriers=extra.get("include_barriers", found["include_barriers"]),
        traversability_scope=extra.get("traversability_scope") or found["traversability_scope"], report=False)


def trace(network, graph, domain, tier, name, found, extra=None):
    """(outcome, the features it reaches with the definition's containers, content and structures)."""
    outcome = _untrace.run(network, graph, request(network, domain, tier, name, found, extra))
    keys = _untrace.related(graph, _untrace.features(graph, outcome.ids), found["include_containers"],
                            found["include_content"], found["include_structures"])
    return outcome, keys


def controller_names(network, tier):
    return {r[0] for r in network.q("select subnetwork_name from grapy_un_controller where network = ? and "
                                    "lower(tier) = lower(?)", network.name, tier)}


def rows(network, tier):
    return {r[0]: r for r in network.q("select name, isdirty, isdeleted, controllers, features from grapy_un_subnetwork "
                                       "where network = ? and lower(tier) = lower(?)", network.name, tier)}


def _subnet_line(network, domain):
    return network.q("select class_name from grapy_un_source where network = ? and domain = ? and role = 'SubnetLine'",
                     network.name, domain).fetchone()[0]


def _set_subnet_dirty(network, domain, tier, name, state):
    table = _subnet_line(network, domain)
    network.q(f'update "{table}" set ISDIRTY = ? where DOMAINNETWORKNAME = ? and TIERNAME = ? and SUBNETWORKNAME = ?',
              state, domain, tier, name)


# ---- discovered subnetworks ----------------------------------------------------------------------------------------

def discover(network):
    """Validate's discovered subnetworks: in each tier whose definition manages Is dirty, a subnetwork whose controllers
    are new, gone, or whose extent differs from what UpdateSubnetwork last wrote is marked dirty, and named in Validate's
    output as {domainNetworkName, tierName, subnetworkName} (INFERRED, §5.9 (m))."""
    found = []
    graph = None
    for domain, tier in network.q("select t.domain, t.name from grapy_un_tier t join grapy_un_domain d on d.network = "
                                  "t.network and d.name = t.domain where t.network = ? and d.kind = 'Traditional' "
                                  "order by t.domain, t.rank", network.name).fetchall():
        settings = definition(network, tier)
        if not settings["manage_subnetwork_isdirty"]:
            continue
        names, kept = controller_names(network, tier), rows(network, tier)
        for name in sorted(names | set(kept), key=str.lower):
            row = kept.get(name)
            dirty = False
            if row is None:
                network.q("insert into grapy_un_subnetwork values (?, ?, ?, ?, null, ?, 0, null, null, null)",
                          network.name, domain, tier, name, DIRTY)
                dirty = True
            elif name not in names:
                if not row[2] or row[1] == CLEAN:
                    dirty = not row[2]
                    network.q("update grapy_un_subnetwork set isdeleted = 1, isdirty = ? where network = ? and tier = ? "
                              "and name = ?", DIRTY if dirty else row[1], network.name, tier, name)
            elif row[1] == CLEAN:
                graph = graph or _utility.build(network)
                try:
                    _, keys = trace(network, graph, domain, tier, name, settings)
                    dirty = sorted([list(k) for k in keys]) != json.loads(row[4] or "[]")
                except ExecuteError:
                    dirty = True
                if dirty:
                    network.q("update grapy_un_subnetwork set isdirty = ? where network = ? and tier = ? and name = ?",
                              DIRTY, network.name, tier, name)
            if dirty:
                _set_subnet_dirty(network, domain, tier, name, DIRTY)
                found.append({"domainNetworkName": domain, "tierName": tier, "subnetworkName": name})
    return found


# ---- UpdateSubnetwork ----------------------------------------------------------------------------------------------

def _names_in(value):
    return [n for n in str(value or "").split(SEPARATOR) if n and n != "Unknown"]


def _write_name(network, class_name, field, oids, name, has_isconnected):
    """``name`` added to the field of the features ``oids`` and taken from every other feature of the class that has it."""
    oid = network.oid(class_name)
    connected = ", ISCONNECTED = ?" if has_isconnected else ""
    for fid, value in network.q(f'select "{oid}", "{field}" from "{class_name}"').fetchall():
        names = _names_in(value)
        if fid in oids and name not in names:
            names = sorted(names + [name], key=str.lower)
        elif fid not in oids and name in names:
            names.remove(name)
        else:
            if fid in oids and has_isconnected:
                network.q(f'update "{class_name}" set ISCONNECTED = ? where "{oid}" = ?', CONNECTED, fid)
            continue
        text = SEPARATOR.join(names) if names else "Unknown"
        state = CONNECTED if names else UNKNOWN
        network.q(f'update "{class_name}" set "{field}" = ?{connected} where "{oid}" = ?', text,
                  *([state] if has_isconnected else []), fid)


def _columns(network, class_name):
    return {c.name.lower(): c.name for c in _gpkg.describe(network.connection, class_name)[0]}


def update(network, domain, tier, name, extra):
    """One subnetwork updated; answers the reason it failed, or None. Its Subnetworks row and SubnetLine are written
    either way."""
    domain_row = network.domain(domain)
    tier_row = network.tier(tier)
    settings = definition(network, tier_row[1])
    field = tier_row[5]
    graph = _utility.build(network)
    names = controller_names(network, tier_row[1])
    sources = network.sources()
    classes = {s: (c, r) for s, (c, d, r) in sources.items() if d == domain_row[0] and r in DOMAIN_ROLES}
    subnet_line = _subnet_line(network, domain_row[0])
    match = (domain_row[0], tier_row[1], name)
    stamp = now()
    if name not in names:
        for class_name, role in classes.values():
            if field.lower() in _columns(network, class_name):
                _write_name(network, class_name, field, set(), name, "isconnected" in _columns(network, class_name))
        network.q(f'delete from "{subnet_line}" where DOMAINNETWORKNAME = ? and TIERNAME = ? and SUBNETWORKNAME = ?',
                  *match)
        if settings["manage_subnetwork_isdirty"]:
            network.q("update grapy_un_subnetwork set isdeleted = 1, isdirty = ?, last_update = ? where network = ? and "
                      "tier = ? and name = ?", CLEAN, stamp, network.name, tier_row[1], name)
        else:
            network.q("delete from grapy_un_subnetwork where network = ? and tier = ? and name = ?", network.name,
                      tier_row[1], name)
        return None

    def fail(reason):
        _record(network, domain_row[0], tier_row[1], name, INVALID, None, None)
        _set_subnet_dirty(network, domain_row[0], tier_row[1], name, INVALID)
        return reason

    try:
        outcome, keys = trace(network, graph, domain_row[0], tier_row[1], name, settings, extra)
    except ExecuteError as said:
        return fail(str(said))
    if _utility.dirty_meets(network, keys, graph):
        return (f"ERROR 002087: dirty areas meet the subnetwork '{name}'; validate the network topology first.")
    values = _utility.Values(graph)
    for list_name, role in VALID_LISTS.items():
        allowed = {tuple(a) for a in settings[list_name]}
        if not allowed or list_name in ("aggregated_line",) or "controller" in list_name:
            continue
        for key in keys:
            if sources[key[0]][2] == role and sources[key[0]][1] == domain_row[0]:
                data = graph.features[key][1]
                if (sources[key[0]][0], data["group"], data["type"]) not in allowed:
                    return fail(f"ERROR 002101: {graph.features[key][0]} in the subnetwork '{name}' is not of an asset "
                                f"type its tier's definition lists as valid ({list_name}).")
    for list_name in ("valid_subnetwork_controller", "valid_junction_object_subnetwork_controller"):
        allowed = {tuple(a) for a in settings[list_name]}
        if not allowed:
            continue
        for i in outcome.ids:
            e = graph.elements[i]
            found = values.controller(e)
            if found and found[1].lower() == tier_row[1].lower() and \
                    sources[e.source][2] == VALID_LISTS[list_name] and values.asset(e) not in allowed:
                return fail(f"ERROR 002101: the controller {e.globalid} is not of a valid controller asset type.")
    seeds = sorted(i for i in outcome.ids if (values.controller(graph.elements[i]) or (None, "", ""))[2] == name)
    if not settings["support_disjoint_subnetwork"] and len({graph.elements[i].feature for i in seeds}) > 1:
        within = {a: {b for b in bs if b in outcome.ids} for a, bs in graph.undirected().items() if a in outcome.ids}
        reached, _ = _utility.reach([seeds[0]], within, lambda i: False)
        if not set(seeds) <= reached:
            return fail(f"ERROR 002103: the subnetwork '{name}' is disjoint, and its tier does not support disjoint "
                        "subnetworks.")
    if settings["validate_locatability"]:
        lost = _untrace.unlocatable(graph, keys)
        if lost:
            return fail(f"ERROR 002104: {len(lost)} junction or edge object(s) in the subnetwork '{name}' cannot be "
                        "located: they have no containment or attachment association.")

    # Names, Is connected and propagated values on the domain's features.
    for source, (class_name, role) in classes.items():
        columns = _columns(network, class_name)
        oids = {k[1] for k in keys if k[0] == source}
        if field.lower() in columns:
            _write_name(network, class_name, columns[field.lower()], oids, name, "isconnected" in columns)
        for row in settings["propagators"] + list(extra.get("propagators") or []):
            target = row[5] if len(row) > 5 else None
            if not target or str(target).lower() not in columns:
                continue
            for i in sorted(outcome.ids, reverse=True):
                e = graph.elements[i]
                if e.source == source and row[0] in outcome.propagated.get(i, {}):
                    network.q(f'update "{class_name}" set "{columns[str(target).lower()]}" = ? where '
                              f'"{network.oid(class_name)}" = ?', outcome.propagated[i][row[0]], e.oid)
    # Supported subnetwork names on containers and structures.
    by_globalid = {g: k for k, (g, _) in graph.features.items()}
    holders = set()
    for row in graph.associations:
        kind = int(row["type"])
        content = by_globalid.get(str(row["to_globalid"]).upper())
        holder = by_globalid.get(str(row["from_globalid"]).upper())
        if content in keys and holder is not None and ((kind == 2 and settings["update_container_features"]) or
                                                       (kind == 3 and settings["update_structure_features"])):
            holders.add(holder)
    for source in {k[0] for k in holders}:
        class_name = sources[source][0]
        columns = _columns(network, class_name)
        if "supportedsubnetworkname" in columns:
            _write_name(network, class_name, columns["supportedsubnetworkname"], {k[1] for k in holders if k[0] == source},
                        name, False)
    # The SubnetLine row: its aggregated lines and summaries.
    aggregated = {tuple(a) for a in settings["aggregated_line"]}
    pieces = []
    for i in outcome.ids:
        e = graph.elements[i]
        if e.kind != "E" or e.shape is None or sources[e.source][2] != "Line":
            continue
        data = graph.features[e.feature][1]
        if aggregated and (sources[e.source][0], data["group"], data["type"]) not in aggregated:
            continue
        pieces.append(e.shape)
    shape = None
    if pieces:
        merged = linemerge(unary_union(pieces))
        shape = merged if merged.geom_type == "MultiLineString" else shapely.MultiLineString([merged])
    srid = int(network.q("select srs_id from gpkg_geometry_columns where table_name = ?", subnet_line).fetchone()[0])
    controllers = SEPARATOR.join(sorted({r[0] for r in network.q(
        "select controller_name from grapy_un_controller where network = ? and lower(tier) = lower(?) and "
        "subnetwork_name = ?", network.name, tier_row[1], name)}, key=str.lower))
    summaries = {}
    columns = _columns(network, subnet_line)
    for row in settings["summaries"]:
        result = _untrace.functions(network, graph, values, [list(row[:6]) + [None]], outcome.ids, "SUBNETWORK",
                                    report=False)[0]["result"]
        summaries[columns[str(row[6]).lower()]] = result
    network.q(f'delete from "{subnet_line}" where DOMAINNETWORKNAME = ? and TIERNAME = ? and SUBNETWORKNAME = ?', *match)
    names_sql = ", ".join(f'"{c}"' for c in summaries)
    network.q(f'insert into "{subnet_line}" (geom, DOMAINNETWORKNAME, TIERNAME, SUBNETWORKNAME, SUBNETWORKCONTROLLERNAMES, '
              f'ISDIRTY, LASTUPDATESUBNETWORK{", " + names_sql if summaries else ""}) values (?, ?, ?, ?, ?, ?, ?'
              f'{", ?" * len(summaries)})', _gpkg.encode(shape, srid) if shape is not None else None, *match,
              controllers, CLEAN, stamp, *summaries.values())
    _record(network, domain_row[0], tier_row[1], name, CLEAN, controllers, sorted([list(k) for k in keys]), stamp)
    return None


def _record(network, domain, tier, name, state, controllers, keys, stamp=None):
    row = network.q("select last_ack_export from grapy_un_subnetwork where network = ? and tier = ? and name = ?",
                    network.name, tier, name).fetchone()
    network.q("delete from grapy_un_subnetwork where network = ? and tier = ? and name = ?", network.name, tier, name)
    network.q("insert into grapy_un_subnetwork values (?, ?, ?, ?, ?, ?, 0, ?, ?, ?)", network.name, domain, tier, name,
              controllers, state, stamp, row[0] if row else None, json.dumps(keys) if keys is not None else None)


# ---- UpdateIsConnected ---------------------------------------------------------------------------------------------

def update_is_connected(network):
    """ISCONNECTED on every feature of each traditional domain network's device, line and junction classes and junction
    and edge object tables: True when a path of connectivity reaches any subnetwork controller of its domain network,
    else False — and False everywhere when there is none (the Is connected attribute page). The path is the
    connectivity the topology records, without barriers (INFERRED, §5.9 (m))."""
    graph = _utility.build(network)
    values = _utility.Values(graph)
    sources = network.sources()
    for domain, in network.q("select name from grapy_un_domain where network = ? and kind = 'Traditional'",
                             network.name).fetchall():
        controllers = [e.id for e in graph.elements if values.controller(e) is not None and e.source
                       and sources[e.source][1] == domain]
        reached, _ = _utility.reach(controllers, graph.undirected(domain), lambda i: False)
        connected = {graph.elements[i].feature for i in reached}
        for source, (class_name, d, role) in sources.items():
            if d != domain or role not in DOMAIN_ROLES:
                continue
            oid = network.oid(class_name)
            network.q(f'update "{class_name}" set ISCONNECTED = ?', NOT_CONNECTED)
            for key in connected:
                if key is not None and key[0] == source:
                    network.q(f'update "{class_name}" set ISCONNECTED = ? where "{oid}" = ?', CONNECTED, key[1])
