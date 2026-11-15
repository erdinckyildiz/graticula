"""arcpy.un against ArcGIS Pro 3.7: one case for each name grapy builds.

Every case runs on a utility network built here, the water network of ``test_utility_network.WaterNetwork`` drawn in
the fixture's UTM square (one unit 10 m), in a file geodatabase on Pro and a GeoPackage on grapy, with the same calls:

    S ─L1─ V1 ─L2─ T ─L3─ V2 ─L4─ T2          S   source device, its Load terminal the controller of Zone1
                   │      │       │           V1, V2  valves (category Isolating)
                   L5     L9      L7          T, T2   tees
                   │      │       │           CV  check valve: L5 meets its Source terminal, L6 its Load
                   CV     └──L8───┘           VA  a vault (StructureJunction) containing V2
                   │
                   L6

Pro wants a coded value domain of its own per asset group, holding the ASSETTYPE default 0 (ERRORs 002580, 002943),
a container role before a containment rule (002908), a tier that lists its controllers' asset types (160882), features
written in an edit session, and a terminal on every device or junction starting point (001785); the builder does each
on both sides. A network is built afresh for a case that changes it (``_build``), once for the cases that only read it
(``_shared``).

The definition tools' answers are read through ``Describe`` of the network (its documented properties); traces through
the ELEMENTS JSON, mapped back to feature names; the export tools through the files they write, with global IDs mapped
to names.

Pro lays out and stores one diagram per process on a file geodatabase, so the cases run in three batches per side
(``BATCHES``): A the network tools and B the diagram tools, each one process, and L (``LAYOUT_CASES``) one process per
case — ``run.sh``-style, ``harness.py <side> un <out> <case …>``. The shared network is built once per output folder and
copied after (``un_seed``); delete the seed when the builder changes.
"""
import csv
import json
import os
import shutil

CASES = {}
_STATE = {}
UNIT = 10.0
SOURCE, LOAD = 2, 3            # the Source-Load configuration's terminal ids (1 is the Single Terminal)


def case(name):
    def register(function):
        CASES[name] = function
        return function
    return register


def _pro(ap):
    return not hasattr(ap, "graticula")


# ---- the network ---------------------------------------------------------------------------------------------------

DEVICES = [("S", 1, 1, 0, (0, 0)), ("V1", 2, 1, 0, (10, 0)), ("V2", 2, 1, 0, (30, 0)), ("CV", 3, 1, 0, (20, 10))]
JUNCTIONS = [("T", 1, 1, (20, 0)), ("T2", 1, 1, (40, 0))]
LINES = [("L1", None, None, [(0, 0), (10, 0)]), ("L2", None, None, [(10, 0), (20, 0)]),
         ("L3", None, None, [(20, 0), (30, 0)]), ("L4", None, None, [(30, 0), (40, 0)]),
         ("L5", None, SOURCE, [(20, 0), (20, 10)]), ("L6", LOAD, None, [(20, 10), (20, 20)]),
         ("L7", None, None, [(40, 0), (40, 10)]), ("L8", None, None, [(40, 10), (30, 10)]),
         ("L9", None, None, [(30, 10), (30, 0)])]
VAULTS = [("VA", 1, 1, (30, 0))]
ALL = {"S", "L1", "V1", "L2", "T", "L3", "V2", "L4", "T2", "L7", "L8", "L9", "L5", "CV", "L6"}


def _xy(I, x, y):
    return I["x0"] + 100 + UNIT * x, I["y0"] + 100 + UNIT * y


def _pz(I, xy):
    x, y = _xy(I, *xy)
    return f"POINT Z ({x} {y} 0)"


def _lz(I, coords):
    return "LINESTRING Z (" + ", ".join("{} {} 0".format(*_xy(I, *c)) for c in coords) + ")"


def _groups(ap, gdb, table, groups, type_name):
    for code, text in groups:
        domain = f"{os.path.basename(table)}_{text}_Types"
        ap.management.CreateDomain(gdb, domain, domain, "SHORT", "CODED")
        ap.management.AddCodedValueToDomain(gdb, domain, 0, "Unknown")
        ap.management.AddCodedValueToDomain(gdb, domain, 1, type_name)
        ap.management.AddSubtype(table, code, text)
        ap.management.AssignDomainToField(table, "ASSETTYPE", domain, code)


def _insert(ap, net, table, fields, rows):
    with ap.da.Editor(net["gdb"]), ap.da.InsertCursor(table, fields) as cursor:
        for row in rows:
            cursor.insertRow(row)
    with ap.da.SearchCursor(table, ["NAME", "GLOBALID@"]) as read:
        net["ids"].update({n: g.upper() if g.startswith("{") else "{" + g.upper() + "}" for n, g in read})


def _create(ap, I, tag):
    """A file geodatabase (a GeoPackage on grapy) with the feature dataset and its service territory, and the utility
    network created in it — nothing more."""
    folder = os.path.join(I["work"], "un_" + tag)
    shutil.rmtree(folder, ignore_errors=True)
    os.makedirs(folder)
    sr = ap.SpatialReference(I["srid"])
    gdb = ap.management.CreateFileGDB(folder, "un")[0]
    fds = ap.management.CreateFeatureDataset(gdb, "utility", sr)[0]
    territory = ap.management.CreateFeatureclass(fds, "Territory", "POLYGON", has_z="ENABLED", has_m="ENABLED",
                                                 spatial_reference=sr)[0]
    x0, y0, x1, y1 = I["x0"], I["y0"], I["x0"] + 600, I["y0"] + 600
    with ap.da.InsertCursor(territory, ["SHAPE@WKT"]) as rows:
        rows.insertRow([f"POLYGON Z (({x0} {y0} 0, {x1} {y0} 0, {x1} {y1} 0, {x0} {y1} 0, {x0} {y0} 0))"])
    return {"folder": folder, "gdb": gdb, "fds": fds, "territory": territory, "ids": {}, "sr": sr}


def _define(ap, I, net):
    """The water network's definition: domain network, tier, asset groups and types, fields, terminal
    configuration, categories, a network attribute, rules and the tier's subnetwork definition."""
    fds, gdb = net["fds"], net["gdb"]
    un = net["un"] = ap.un.CreateUtilityNetwork(fds, "Water", net["territory"])[0]
    ap.un.AddDomainNetwork(un, "Water", "PARTITIONED", "SOURCE", "Water Distribution")
    ap.un.AddTier(un, "Water", "Pressure", 1, "RADIAL")
    device, line = f"{fds}/WaterDevice", f"{fds}/WaterLine"
    junction, vault = f"{fds}/WaterJunction", f"{fds}/StructureJunction"
    net.update(device=device, line=line, junction=junction, vault=vault)
    _groups(ap, gdb, device, [(1, "Source"), (2, "Valve"), (3, "CheckValve")], "Standard")
    _groups(ap, gdb, line, [(1, "Main")], "Ductile")
    _groups(ap, gdb, junction, [(1, "Tee"), (2, "Plug")], "Standard")
    _groups(ap, gdb, vault, [(1, "Vault")], "Concrete")
    for table in (device, line, junction, vault):
        ap.management.AddField(table, "NAME", "TEXT")
    ap.management.AddField(device, "STATUS", "SHORT")
    ap.un.AddTerminalConfiguration(un, "Source-Load", "DIRECTIONAL", "Source true;Load false")
    for group in ("Source", "CheckValve"):
        ap.un.SetTerminalConfiguration(un, "Water", "WaterDevice", group, "Standard", "Source-Load")
    ap.un.SetNetworkCategory(un, "Water", "WaterDevice", "Source", "Standard", "Subnetwork Controller")
    ap.un.AddNetworkCategory(un, "Isolating")
    ap.un.SetNetworkCategory(un, "Water", "WaterDevice", "Valve", "Standard", ["Isolating"])
    ap.un.AddNetworkAttribute(un, "Device Status", "SHORT")
    ap.un.SetNetworkAttribute(un, "Device Status", "Water", "WaterDevice", "STATUS")
    je = "JUNCTION_EDGE_CONNECTIVITY"
    ap.un.AddRule(un, je, "WaterDevice", "Source", "Standard", "WaterLine", "Main", "Ductile", "Load")
    ap.un.AddRule(un, je, "WaterDevice", "Valve", "Standard", "WaterLine", "Main", "Ductile")
    ap.un.AddRule(un, je, "WaterDevice", "CheckValve", "Standard", "WaterLine", "Main", "Ductile", "Source")
    ap.un.AddRule(un, je, "WaterDevice", "CheckValve", "Standard", "WaterLine", "Main", "Ductile", "Load")
    ap.un.AddRule(un, je, "WaterJunction", "Tee", "Standard", "WaterLine", "Main", "Ductile")
    ap.un.SetAssociationRole(un, "Structure", "StructureJunction", "Vault", "Concrete", "CONTAINER", "RESTRICTED")
    ap.un.AddRule(un, "CONTAINMENT", "StructureJunction", "Vault", "Concrete", "WaterDevice", "Valve", "Standard")
    ap.un.SetSubnetworkDefinition(un, "Water", "Pressure", None,
                                  ["Source/Standard", "Valve/Standard", "CheckValve/Standard"], ["Source/Standard"],
                                  ["Main/Ductile"], valid_junctions=["Tee/Standard"])
    return net


def _load(ap, I, net):
    """The features, the topology enabled, the controller and the containment imported, the topology validated."""
    _insert(ap, net, net["device"], ["NAME", "ASSETGROUP", "ASSETTYPE", "STATUS", "SHAPE@WKT"],
            [(n, g, t, s, _pz(I, xy)) for n, g, t, s, xy in DEVICES])
    _insert(ap, net, net["junction"], ["NAME", "ASSETGROUP", "ASSETTYPE", "SHAPE@WKT"],
            [(n, g, t, _pz(I, xy)) for n, g, t, xy in JUNCTIONS])
    _insert(ap, net, net["line"], ["NAME", "ASSETGROUP", "ASSETTYPE", "FROMDEVICETERMINAL", "TODEVICETERMINAL",
                                   "SHAPE@WKT"], [(n, 1, 1, f, t, _lz(I, c)) for n, f, t, c in LINES])
    _insert(ap, net, net["vault"], ["NAME", "ASSETGROUP", "ASSETTYPE", "SHAPE@WKT"],
            [(n, g, t, _pz(I, xy)) for n, g, t, xy in VAULTS])
    ap.un.EnableNetworkTopology(net["un"])
    ap.un.ImportSubnetworkControllers(net["un"], _controllers_csv(net, [("Plant", "S", "Zone1")]))
    ap.un.ImportAssociations(net["un"], "CONTAINMENT", _associations_csv(net, [("VA", "V2")]))
    ap.un.ValidateNetworkTopology(net["un"])
    return net


def _controllers_csv(net, rows, name="controllers.csv"):
    path = os.path.join(net["folder"], name)
    with open(path, "w", newline="", encoding="utf-8") as written:
        out = csv.writer(written)
        out.writerow(["SUBNETWORKCONTROLLERNAME", "FEATUREGLOBALID", "FEATURECLASSNAME", "FEATUREASSETGROUP",
                      "FEATUREASSETTYPE", "FEATURETERMINAL", "TIERNAME", "SUBNETWORKNAME", "DESCRIPTION", "NOTES"])
        for controller, feature, subnetwork in rows:
            out.writerow([controller, net["ids"][feature], "WaterDevice", "Source", "Standard", "Load", "Pressure",
                          subnetwork, "", ""])
    return path


def _associations_csv(net, rows, name="associations.csv"):
    path = os.path.join(net["folder"], name)
    with open(path, "w", newline="", encoding="utf-8") as written:
        out = csv.writer(written)
        out.writerow(["ASSOCIATIONTYPE", "FROMFEATURECLASS", "FROMASSETGROUP", "FROMASSETTYPE", "FROMGLOBALID",
                      "FROMTERMINAL", "TOFEATURECLASS", "TOASSETGROUP", "TOASSETTYPE", "TOGLOBALID", "TOTERMINAL",
                      "ISCONTENTVISIBLE"])
        for container, content in rows:
            out.writerow(["Containment", "StructureJunction", "Vault", "Concrete", net["ids"][container], "",
                          "WaterDevice", "Valve", "Standard", net["ids"][content], "", "FALSE"])
    return path


def _build(ap, I, tag, load=True):
    net = _define(ap, I, _create(ap, I, tag))
    return _load(ap, I, net) if load else net


def _shared(ap, I):
    """The loaded network the reading cases share. Its first build in an output folder is kept as a seed
    (``un_seed``) that later processes copy, so that the layout cases, one per process, do not each rebuild it."""
    if "net" in _STATE:
        return _STATE["net"]
    seed = os.path.join(I["work"], "un_seed")
    folder = os.path.join(I["work"], "un_shared")
    if os.path.exists(os.path.join(seed, "net.json")):
        shutil.rmtree(folder, ignore_errors=True)
        shutil.copytree(seed, folder)
        net = json.load(open(os.path.join(folder, "net.json"), encoding="utf-8"))
        net = {k: v.replace(seed, folder) if isinstance(v, str) else v for k, v in net.items()}
        net["sr"] = ap.SpatialReference(I["srid"])
    else:
        net = _build(ap, I, "shared")
        shutil.rmtree(seed, ignore_errors=True)
        shutil.copytree(folder, seed)
        json.dump({k: str(v).replace(folder, seed) if isinstance(v, str) or k in ("un", "gdb", "fds") else v
                   for k, v in net.items() if k != "sr"}, open(os.path.join(seed, "net.json"), "w", encoding="utf-8"))
    _STATE["net"] = net
    return net


# ---- reading answers -----------------------------------------------------------------------------------------------

def _names(net):
    return {g.strip("{}").upper(): n for n, g in net["ids"].items()}


def _defined(ap, I):
    """A network defined but never enabled, shared by the definition cases in order: each adds what it reads back."""
    if "def" not in _STATE:
        _STATE["def"] = _build(ap, I, "def", load=False)
    return _STATE["def"]


# ---- Describe of a utility network, the parts a case reads -------------------------------------------------------

def _get(o, name, default=None):
    try:
        return getattr(o, name)
    except Exception:  # noqa: BLE001 — a property one side does not answer is recorded as missing
        return "<missing>"


def _domain(ap, un, name):
    d = next(x for x in ap.Describe(un).domainNetworks if x.domainNetworkName == name)
    sources = []
    for s in list(d.junctionSources) + list(d.edgeSources):
        sources.append([s.sourceName, s.sourceType, _get(s, "utilityNetworkFeatureClassUsageType"),
                        [[g.assetGroupCode, g.assetGroupName, [[t.assetTypeCode, t.assetTypeName]
                                                               for t in g.assetTypes]] for g in s.assetGroups]])
    return {"name": d.domainNetworkName, "alias": _get(d, "domainNetworkAliasName"),
            "isStructureNetwork": _get(d, "isStructureNetwork"),
            "subnetworkControllerType": _get(d, "subnetworkControllerType"),
            "subnetworkLabelFieldName": _get(d, "subnetworkLabelFieldName"),
            "subnetworkTableName": _get(d, "subnetworkTableName"), "tierDefinition": _get(d, "tierDefinition"),
            "tierGroups": [_get(g, "name") for g in _get(d, "tierGroups", [])],
            "tiers": [_tier(t) for t in _get(d, "tiers", [])], "sources": sorted(sources)}


def _valid(items):
    return sorted([_get(v, "assetGroupCode"), sorted(_get(a, "assetTypeCode") for a in _get(v, "assetTypes", []))]
                  for v in items or [])


def _tier(t, full=False):
    out = {"name": _get(t, "name"), "rank": _get(t, "rank"), "subnetworkFieldName": _get(t, "subnetworkFieldName"),
           "tierGroupName": _get(t, "tierGroupName"), "tierTopology": _get(t, "tierTopology")}
    if full:
        u = _get(t, "updateSubnetworkTraceConfiguration")
        out.update({k: _valid(_get(t, k)) for k in ("validDevices", "validLines", "validSubnetworkControllers",
                                                    "validJunctions", "validJunctionObjects", "validEdgeObjects")})
        out["manageSubnetwork.isDirty"] = _get(_get(t, "manageSubnetwork"), "isDirty")
        out["trace"] = {k: _get(u, k) for k in ("includeBarriers", "includeContainers", "includeContent",
                                                "includeStructures", "validateConsistency", "traversabilityScope",
                                                "domainNetworkName", "tierName", "allowIndeterminateFlow")}
        out["trace"]["conditionBarriers"] = [_barrier(b) for b in _get(u, "conditionBarriers", []) or []]
    return out


def _barrier(b):
    return {k: _get(b, k) for k in ("name", "type", "operator", "value", "combineUsingOr", "isSpecificValue")}


def _tier_of(ap, un, domain, name, full=True):
    d = next(x for x in ap.Describe(un).domainNetworks if x.domainNetworkName == domain)
    return _tier(next(t for t in d.tiers if t.name == name), full)


def _asset(ap, un, source, group, kind):
    for d in ap.Describe(un).domainNetworks:
        for s in list(d.junctionSources) + list(d.edgeSources):
            if s.sourceName != source:
                continue
            g = next(x for x in s.assetGroups if x.assetGroupName == group)
            t = next(x for x in g.assetTypes if x.assetTypeName == kind)
            return {k: _get(t, k) for k in ("associationRoleType", "associationDeleteType", "categories",
                                            "terminalConfigurationID", "connectivityPolicy",
                                            "isTerminalConfigurationSupported", "splitContent")}
    raise ValueError(f"no source {source}")


def _attributes(ap, un, names=None):
    out = []
    for a in ap.Describe(un).networkAttributes:
        if names is not None and a.name not in names:
            continue
        out.append({"name": a.name, "dataType": _get(a, "dataType"), "fieldType": _get(a, "fieldType"),
                    "domainName": _get(a, "domainName"), "isApportionable": _get(a, "isApportionable"),
                    "isEmbedded": _get(a, "isEmbedded"), "isNullable": _get(a, "isNullable"),
                    "isOverridable": _get(a, "isOverridable"), "isSubstitution": _get(a, "isSubstitution"),
                    "bitSize": _get(a, "bitSize"), "usageType": _get(a, "usageType"),
                    "assignments": sorted([_get(s, "networkSourceName"), _get(_get(s, "evaluator"), "fieldName")]
                                          for s in _get(a, "assignments", []) or [])})
    return out


def _categories(ap, un):
    return sorted(c.name for c in ap.Describe(un).categories)


def _terminal_configurations(ap, un):
    out = []
    for c in ap.Describe(un).terminalConfigurations:
        out.append({"id": _get(c, "terminalConfigurationID"), "name": _get(c, "terminalConfigurationName"),
                    "model": _get(c, "traversabilityModel"), "default": _get(c, "defaultConfiguration"),
                    "terminals": [[_get(t, "terminalID"), _get(t, "terminalName"), _get(t, "isUpstreamTerminal")]
                                  for t in _get(c, "terminals", [])],
                    "paths": [[_get(p, "name"), _get(p, "description")]
                              for p in _get(c, "validConfigurationPaths", []) or []]})
    return out


def _classes(ap, net, prefix):
    """Which of a domain network's documented classes exist (ListFeatureClasses with a feature dataset is the arcpy
    top-level sweep's; grapy answers none there, so the classes are asked for by name)."""
    suffixes = ("Junction", "Line", "Boundary", "JunctionObject", "EdgeObject") if prefix == "Structure" else (
        "Device", "Line", "Junction", "Assembly", "SubnetLine", "JunctionObject", "EdgeObject")
    return [prefix + s for s in suffixes if ap.Exists(f"{net['fds']}/{prefix}{s}") or ap.Exists(f"{net['gdb']}/{prefix}{s}")]


def _fields(ap, table):
    """A class's fields, sorted: the order fields are listed in is not compared."""
    return sorted([f.name, f.type] for f in ap.ListFields(table)
                  if f.type not in ("OID", "Geometry", "GlobalID") and f.name.lower() not in ("shape_length", "shape_area"))


# ---- system tables, read by what they hold -------------------------------------------------------------------------

def _system(ap, net, kind):
    """The network's dirty areas or associations table: Pro keeps them as UN_<id>_<kind> in the geodatabase, grapy as
    <name>_<kind> in the feature dataset. Their names are storage, not interface; what a case reads is the rows."""
    if not _pro(ap):
        return f"{net['fds']}/Water_{kind}"
    if kind not in net.setdefault("system", {}):
        for n in range(1, 200):
            path = os.path.join(net["gdb"], f"UN_{n}_{kind}")
            if ap.Exists(path):
                net["system"][kind] = path
                break
    return net["system"][kind]


def _dirty(ap, net):
    """The dirty areas as sorted [STATUS, ERRORCODE, feature name or None]."""
    names = _names(net)
    table = _system(ap, net, "DirtyAreas")
    fields = [f.name.upper() for f in ap.ListFields(table)]
    guid = "GUID" if "GUID" in fields else "FEATUREGLOBALID"
    out = []
    with ap.da.SearchCursor(table, ["STATUS", "ERRORCODE", guid]) as rows:
        for status, code, g in rows:
            out.append([status, code, names.get(str(g or "").strip("{}").upper())])
    return sorted(out, key=str)


def _column(ap, table, field, where=None):
    with ap.da.SearchCursor(table, ["NAME", field], where) as rows:
        return sorted([n, v] for n, v in rows)


# ---- traces ---------------------------------------------------------------------------------------------------------

LINE_NAMES = {n for n, *_ in LINES}


def _locations(ap, net, rows, name=None):
    """A trace location table (FEATUREGLOBALID, TERMINALID, PERCENTALONG) of [(feature name, terminal or None)]: a
    line at its middle, a device or junction at its terminal (the Load of S, the Single Terminal 1 otherwise)."""
    net["count"] = net.get("count", 0) + 1
    name = name or f"loc{net['count']}"
    path = ap.management.CreateFeatureclass(net["gdb"], name, "POINT", spatial_reference=net["sr"])[0]
    for field, kind in (("FEATUREGLOBALID", "GUID"), ("TERMINALID", "LONG"), ("PERCENTALONG", "DOUBLE")):
        ap.management.AddField(path, field, kind)
    with ap.da.InsertCursor(path, ["FEATUREGLOBALID", "TERMINALID", "PERCENTALONG", "SHAPE@XY"]) as cursor:
        for feature, terminal in rows:
            if terminal is None and feature not in LINE_NAMES:
                terminal = LOAD if feature in ("S", "CV") else 1
            cursor.insertRow((net["ids"][feature], terminal, 0.5 if feature in LINE_NAMES else None,
                              (net["x0"], net["y0"])))
    return path


def _elements(net, path):
    doc = json.load(open(path, encoding="utf-8"))
    results = doc.get("traceResults", doc)
    names = _names(net)
    out = {"elements": sorted({names.get(str(e.get("globalId", "")).strip("{}").upper(), "?")
                               for e in results.get("elements", [])})}
    if results.get("functionResults"):
        out["functions"] = [[r.get("functionType"), r.get("networkAttributeName"),
                             round(r["result"], 6) if isinstance(r.get("result"), float) else r.get("result")]
                            for r in results["functionResults"]]
    return out


def _trace(ap, I, net, kind, starts, **more):
    net["x0"], net["y0"] = I["x0"], I["y0"]
    out_json = os.path.join(net["folder"], f"trace{net.get('count', 0) + 1}.json")
    for key in ("barriers", "stopping_points"):
        if key in more:
            more[key] = _locations(ap, net, [(n, None) for n in more[key]])
    starting = _locations(ap, net, [(n, None) for n in starts]) if starts else None
    more.setdefault("result_types", ["ELEMENTS"])
    ap.un.Trace(net["un"], kind, starting, domain_network="Water", tier="Pressure", out_json_file=out_json, **more)
    return _elements(net, out_json)


ISOLATING = "Category IS_EQUAL_TO SPECIFIC_VALUE Isolating"
CLOSED = "'Device Status' IS_EQUAL_TO SPECIFIC_VALUE 1 #"


def _csv(path, net):
    """A written .csv file as rows, global IDs named; a column that is a creation date or a global ID of its own is
    left out."""
    names = _names(net)
    with open(path, newline="", encoding="utf-8-sig") as read:
        rows = list(csv.reader(read))
    out = []
    for row in rows:
        out.append([names.get(c.strip("{}").upper(), c) if c.startswith("{") or len(c) == 36 else c for c in row])
    return out


# ---- the network tools ----------------------------------------------------------------------------------------------

@case("un.CreateUtilityNetwork")
def _(ap, I, O):
    net = _create(ap, I, "create")
    un = ap.un.CreateUtilityNetwork(net["fds"], "Water", net["territory"])[0]
    d = ap.Describe(un)
    current = _create(ap, I, "create8")
    d8 = ap.Describe(ap.un.CreateUtilityNetwork(current["fds"], "Water", current["territory"], "CURRENT")[0])
    return {"current": [_get(d8, "schemaGeneration"), sorted(c.name for c in d8.categories)],
            "classes": _classes(ap, net, "Structure"), "dataType": d.dataType, "name": d.name, "territory": _get(d, "serviceTerritoryFeatureClassName"),
            "schemaGeneration": _get(d, "schemaGeneration"),
            "structure": _domain(ap, un, "Structure"), "categories": _categories(ap, un),
            "attributes": [a["name"] for a in _attributes(ap, un)],
            "terminalConfigurations": _terminal_configurations(ap, un),
            "StructureJunction": _fields(ap, f"{net['fds']}/StructureJunction"),
            "StructureLine": _fields(ap, f"{net['fds']}/StructureLine")}


@case("un.AddDomainNetwork")
def _(ap, I, O):
    net = _defined(ap, I)
    ap.un.AddDomainNetwork(net["un"], "Gas", "HIERARCHICAL", "SOURCE", "Gas Distribution")
    return {"domain": _domain(ap, net["un"], "Gas"), "classes": _classes(ap, net, "Gas"),
            "StructureJunction": _fields(ap, f"{net['fds']}/StructureJunction"),
            "GasDevice": _fields(ap, f"{net['fds']}/GasDevice"), "GasLine": _fields(ap, f"{net['fds']}/GasLine"),
            "GasSubnetLine": _fields(ap, f"{net['fds']}/GasSubnetLine")}


@case("un.AddTierGroup")
def _(ap, I, O):
    net = _defined(ap, I)
    ap.un.AddTierGroup(net["un"], "Gas", "Distribution")
    return _domain(ap, net["un"], "Gas")["tierGroups"]


@case("un.AddTier")
def _(ap, I, O):
    net = _defined(ap, I)
    ap.un.AddTier(net["un"], "Gas", "System", 1, None, "Distribution", "systemsubnet")
    ap.un.AddTier(net["un"], "Water", "Zone", 2, "MESH")
    return {"gas": _domain(ap, net["un"], "Gas")["tiers"], "water": _domain(ap, net["un"], "Water")["tiers"],
            "GasLine": _fields(ap, f"{net['fds']}/GasLine")}


@case("un.AddNetworkAttribute")
def _(ap, I, O):
    net = _defined(ap, I)
    ap.management.CreateDomain(net["gdb"], "Lifecycle", "", "SHORT", "CODED")
    for code, text in ((0, "Unknown"), (1, "In service"), (2, "Proposed")):
        ap.management.AddCodedValueToDomain(net["gdb"], "Lifecycle", code, text)
    ap.un.AddNetworkAttribute(net["un"], "Lifecycle", "SHORT", "INLINE", domain="Lifecycle")
    ap.un.AddNetworkAttribute(net["un"], "Pressure", "LONG")
    ap.un.AddNetworkAttribute(net["un"], "Rating", "LONG", is_nullable="NOT_NULLABLE")
    ap.un.AddNetworkAttribute(net["un"], "Diameter", "DOUBLE", is_apportionable="APPORTIONABLE")
    return _attributes(ap, net["un"])


@case("un.SetNetworkAttribute")
def _(ap, I, O):
    net = _defined(ap, I)
    # A nullable field: grapy's AddField keeps no nullability (management's, recorded), so a NOT_NULLABLE attribute
    # could not be assigned here on grapy.
    ap.management.AddField(net["line"], "PRESSURE", "LONG")
    ap.un.SetNetworkAttribute(net["un"], "Pressure", "Water", "WaterLine", "PRESSURE")
    return _attributes(ap, net["un"], {"Pressure", "Device Status"})


@case("un.AddNetworkCategory")
def _(ap, I, O):
    net = _defined(ap, I)
    ap.un.AddNetworkCategory(net["un"], "Protective")
    return _categories(ap, net["un"])


@case("un.SetNetworkCategory")
def _(ap, I, O):
    net = _defined(ap, I)
    ap.un.SetNetworkCategory(net["un"], "Water", "WaterJunction", "Tee", "Standard", ["Protective", "Isolating"])
    ap.un.SetNetworkCategory(net["un"], "Water", "WaterDevice", "Valve", "Standard")
    return {"tee": _asset(ap, net["un"], "WaterJunction", "Tee", "Standard"),
            "valve": _asset(ap, net["un"], "WaterDevice", "Valve", "Standard")}


@case("un.DeleteNetworkCategory")
def _(ap, I, O):
    net = _defined(ap, I)
    ap.un.AddNetworkCategory(net["un"], "Temporary")
    before = _categories(ap, net["un"])
    ap.un.DeleteNetworkCategory(net["un"], "Temporary")
    return {"before": before, "after": _categories(ap, net["un"])}


@case("un.AddTerminalConfiguration")
def _(ap, I, O):
    net = _defined(ap, I)
    ap.un.AddTerminalConfiguration(net["un"], "Three", "BIDIRECTIONAL", None, "A;B;C", "Top A-B;Bottom A-C", "Top")
    ap.un.AddTerminalConfiguration(net["un"], "Pairs", "DIRECTIONAL", "A true;B false;C false", None,
                                   "AB A-B;ABAC 'A-B,A-C'", "AB")
    return _terminal_configurations(ap, net["un"])


@case("un.SetTerminalConfiguration")
def _(ap, I, O):
    net = _defined(ap, I)
    try:
        ap.un.SetTerminalConfiguration(net["un"], "Water", "WaterDevice", "Valve", "Standard", "Three")
        refused = None
    except Exception as error:  # noqa: BLE001 — Valve/Standard is named in a rule
        refused = str(error).splitlines()[0][:13]
    _groups(ap, net["gdb"], net["device"], [(4, "Meter")], "Standard")
    ap.un.SetTerminalConfiguration(net["un"], "Water", "WaterDevice", "Meter", "Standard", "Three")
    return {"valve": refused, "meter": _asset(ap, net["un"], "WaterDevice", "Meter", "Standard")}


@case("un.DeleteTerminalConfiguration")
def _(ap, I, O):
    net = _defined(ap, I)
    ap.un.AddTerminalConfiguration(net["un"], "Spare", "DIRECTIONAL", "In true;Out false")
    ap.un.DeleteTerminalConfiguration(net["un"], "Spare")
    return [c["name"] for c in _terminal_configurations(ap, net["un"])]


def _rules(ap, net, kind="ALL", tag="rules"):
    path = os.path.join(net["folder"], f"{tag}.csv")
    ap.un.ExportRules(net["un"], kind, path)
    return _csv(path, net)


@case("un.AddRule")
def _(ap, I, O):
    net = _defined(ap, I)
    ap.un.AddRule(net["un"], "JUNCTION_JUNCTION_CONNECTIVITY", "WaterJunction", "Tee", "Standard", "WaterJunction",
                  "Plug", "Standard")
    ap.un.AddRule(net["un"], "EDGE_JUNCTION_EDGE_CONNECTIVITY", "WaterLine", "Main", "Ductile", "WaterLine", "Main",
                  "Ductile", via_class="WaterJunction", via_assetgroup="Tee", via_assettype="Standard")
    return _rules(ap, net, tag="addrule")


@case("un.ExportRules")
def _(ap, I, O):
    net = _shared(ap, I)
    return {"ALL": _rules(ap, net, "ALL", "export_all"),
            "CONTAINMENT": _rules(ap, net, "CONTAINMENT", "export_containment")}


@case("un.DeleteRule")
def _(ap, I, O):
    net = _defined(ap, I)
    ap.un.DeleteRule(net["un"], "JUNCTION_JUNCTION_CONNECTIVITY",
                     ["7: From[WaterJunction.Tee.Standard] To[WaterJunction.Plug.Standard]"])
    return _rules(ap, net, tag="deleterule")


@case("un.ImportRules")
def _(ap, I, O):
    net = _defined(ap, I)
    path = os.path.join(net["folder"], "import_rules.csv")
    with open(path, "w", newline="", encoding="utf-8") as written:
        out = csv.writer(written)
        out.writerow(["RULETYPE", "FROMFEATURECLASS", "FROMASSETGROUP", "FROMASSETTYPE", "FROMTERMINAL",
                      "TOFEATURECLASS", "TOASSETGROUP", "TOASSETTYPE", "TOTERMINAL", "VIAFEATURECLASS",
                      "VIAASSETGROUP", "VIAASSETTYPE", "VIATERMINAL"])
        out.writerow(["Junction Junction Connectivity", "WaterJunction", "Plug", "Standard", "", "WaterDevice",
                      "Valve", "Standard", "", "", "", "", ""])
    ap.un.ImportRules(net["un"], "ALL", path)
    return _rules(ap, net, tag="importrules")


@case("un.SetEdgeConnectivity")
def _(ap, I, O):
    net = _defined(ap, I)
    ap.un.SetEdgeConnectivity(net["un"], "Water", "WaterLine", "Main", "Ductile", "ANY_VERTEX")
    return _asset(ap, net["un"], "WaterLine", "Main", "Ductile")


@case("un.SetAssociationRole")
def _(ap, I, O):
    net = _defined(ap, I)
    ap.un.SetAssociationRole(net["un"], "Water", "WaterJunction", "Plug", "Standard", "CONTAINER", "CASCADE")
    return {"plug": _asset(ap, net["un"], "WaterJunction", "Plug", "Standard"),
            "vault": _asset(ap, net["un"], "StructureJunction", "Vault", "Concrete")}


@case("un.SetSubnetworkDefinition")
def _(ap, I, O):
    net = _defined(ap, I)
    ap.un.SetSubnetworkDefinition(net["un"], "Water", "Pressure", "SUPPORT_DISJOINT",
                                  ["Source/Standard", "Valve/Standard"], ["Source/Standard"], ["Main/Ductile"],
                                  condition_barriers=ISOLATING, include_barriers="EXCLUDE_BARRIERS",
                                  valid_junctions=["Tee/Standard", "Plug/Standard"])
    return _tier_of(ap, net["un"], "Water", "Pressure")


# ---- the loaded network ---------------------------------------------------------------------------------------------

@case("un.EnableNetworkTopology")
def _(ap, I, O):
    net = _build(ap, I, "enable", load=False)
    _insert(ap, net, net["device"], ["NAME", "ASSETGROUP", "ASSETTYPE", "STATUS", "SHAPE@WKT"],
            [(n, g, t, s, _pz(I, xy)) for n, g, t, s, xy in DEVICES])
    _insert(ap, net, net["line"], ["NAME", "ASSETGROUP", "ASSETTYPE", "FROMDEVICETERMINAL", "TODEVICETERMINAL",
                                   "SHAPE@WKT"], [(n, 1, 1, f, t, _lz(I, c)) for n, f, t, c in LINES[:3]] +
            [("L10", 0, 0, None, None, _lz(I, [(60, 60), (70, 60)]))])
    _insert(ap, net, net["junction"], ["NAME", "ASSETGROUP", "ASSETTYPE", "SHAPE@WKT"],
            [("T", 1, 1, _pz(I, (20, 0))), ("P", 2, 1, _pz(I, (70, 60)))])
    ap.un.EnableNetworkTopology(net["un"])
    return {"dirty": _dirty(ap, net), "valid": ap.un.UtilityNetwork(net["un"]).HasValidNetworkTopology()}


@case("un.DisableNetworkTopology")
def _(ap, I, O):
    net = _shared(ap, I)
    ap.un.DisableNetworkTopology(net["un"])
    out = {"dirty": _dirty(ap, net), "valid": ap.un.UtilityNetwork(net["un"]).HasValidNetworkTopology()}
    ap.un.EnableNetworkTopology(net["un"])
    out["after_enable"] = _dirty(ap, net)
    ap.un.ValidateNetworkTopology(net["un"])
    return out


@case("un.ValidateNetworkTopology")
def _(ap, I, O):
    net = _shared(ap, I)
    with ap.da.Editor(net["gdb"]), ap.da.UpdateCursor(net["device"], ["STATUS"], "NAME = 'V1'") as rows:
        for row in rows:
            rows.updateRow([1])
    before = _dirty(ap, net)
    x, y = _xy(I, 60, 60)
    try:
        ap.un.ValidateNetworkTopology(net["un"], f"{x} {y} {x + 10} {y + 10}")
        refused = None
    except Exception as error:  # noqa: BLE001 — no network feature in the extent
        refused = str(error)[:12]
    x0, y0 = _xy(I, 35, -5)
    x1, y1 = _xy(I, 45, 5)
    ap.un.ValidateNetworkTopology(net["un"], f"{x0} {y0} {x1} {y1}")
    outside = [refused, _dirty(ap, net)]
    result = ap.un.ValidateNetworkTopology(net["un"])
    out = {"before": before, "outside": outside, "after": _dirty(ap, net), "outputs": result.outputCount}
    with ap.da.Editor(net["gdb"]), ap.da.UpdateCursor(net["device"], ["STATUS"], "NAME = 'V1'") as rows:
        for row in rows:
            rows.updateRow([0])
    ap.un.ValidateNetworkTopology(net["un"])
    return out


@case("un.RebuildNetworkTopology")
def _(ap, I, O):
    net = _shared(ap, I)
    x0, y0 = _xy(I, -5, -5)
    x1, y1 = _xy(I, 15, 5)
    ap.un.RebuildNetworkTopology(net["un"], f"{x0} {y0} {x1} {y1}")
    return {"dirty": _dirty(ap, net), "trace": _trace(ap, I, net, "CONNECTED", ["L4"])}


@case("un.ExportAssociations")
def _(ap, I, O):
    net = _shared(ap, I)
    path = os.path.join(net["folder"], "export_associations.csv")
    ap.un.ExportAssociations(net["un"], "ALL", path)
    return _csv(path, net)


@case("un.ImportAssociations")
def _(ap, I, O):
    net = _shared(ap, I)
    a = ap.un.UtilityNetwork(net["un"])
    out = {"before": _column(ap, net["device"], "ASSOCIATIONSTATUS")}
    with ap.da.SearchCursor(_system(ap, net, "Associations"), ["GLOBALID"]) as rows:
        ids = [r[0] for r in rows]
    with ap.da.Editor(net["gdb"]):
        for g in ids:
            a.DeleteContainmentAssociation(g)
    out["deleted"] = _column(ap, net["device"], "ASSOCIATIONSTATUS")
    ap.un.ImportAssociations(net["un"], "CONTAINMENT", _associations_csv(net, [("VA", "V2")], "again.csv"))
    out["after"] = _column(ap, net["device"], "ASSOCIATIONSTATUS")
    out["vault"] = _column(ap, net["vault"], "ASSOCIATIONSTATUS")
    ap.un.ValidateNetworkTopology(net["un"])
    return out


@case("un.ExportSubnetworkControllers")
def _(ap, I, O):
    net = _shared(ap, I)
    path = os.path.join(net["folder"], "export_controllers.csv")
    ap.un.ExportSubnetworkControllers(net["un"], path)
    return _csv(path, net)


@case("un.ImportSubnetworkControllers")
def _(ap, I, O):
    net = _build(ap, I, "controllers", load=False)
    _insert(ap, net, net["device"], ["NAME", "ASSETGROUP", "ASSETTYPE", "STATUS", "SHAPE@WKT"],
            [(n, g, t, s, _pz(I, xy)) for n, g, t, s, xy in DEVICES[:2]])
    _insert(ap, net, net["line"], ["NAME", "ASSETGROUP", "ASSETTYPE", "FROMDEVICETERMINAL", "TODEVICETERMINAL",
                                   "SHAPE@WKT"], [(n, 1, 1, f, t, _lz(I, c)) for n, f, t, c in LINES[:1]])
    ap.un.EnableNetworkTopology(net["un"])
    ap.un.ImportSubnetworkControllers(net["un"], _controllers_csv(net, [("Plant", "S", "Zone1")]))
    return {"device": [[n, v] for n, v in _column(ap, net["device"], "ISSUBNETWORKCONTROLLER")],
            "name": _column(ap, net["device"], "SUBNETWORKCONTROLLERNAME"),
            "tier": _column(ap, net["device"], "TIERNAME")}


@case("un.UtilityNetwork")
def _(ap, I, O):
    net = _build(ap, I, "object")
    u = ap.un.UtilityNetwork(net["un"])
    ids = net["ids"]
    out = {"methods": sorted(m for m in dir(u) if not m.startswith("_") and m != "DeleteStructualAttachmentAssociation"),
           "valid": u.HasValidNetworkTopology()}
    with ap.da.Editor(net["gdb"]):
        u.DisableSubnetworkController("WaterDevice", ids["S"], "Load")
    out["disabled"] = _column(ap, net["device"], "ISSUBNETWORKCONTROLLER")
    with ap.da.Editor(net["gdb"]):
        u.EnableSubnetworkController("WaterDevice", ids["S"], "Load", "Plant2", "Zone2", "Pressure", "again", "")
    out["enabled"] = _column(ap, net["device"], "SUBNETWORKCONTROLLERNAME")
    with ap.da.Editor(net["gdb"]):
        u.AddContainmentAssociation("StructureJunction", ids["VA"], "WaterDevice", ids["V1"], True)
    path = os.path.join(net["folder"], "object_associations.csv")
    ap.un.ExportAssociations(net["un"], "ALL", path)
    out["associations"] = sorted(r[0] for r in _csv(path, net)[1:])
    return out


@case("un.UpdateIsConnected")
def _(ap, I, O):
    net = _build(ap, I, "isconnected")
    ap.un.DisableNetworkTopology(net["un"])
    _insert(ap, net, net["junction"], ["NAME", "ASSETGROUP", "ASSETTYPE", "SHAPE@WKT"], [("LONE", 1, 1, _pz(I, (60, 60)))])
    ap.un.EnableNetworkTopology(net["un"])
    ap.un.UpdateIsConnected(net["un"])
    return {t: _column(ap, net[t], "ISCONNECTED") for t in ("device", "junction", "line")}


@case("un.UpdateSubnetwork")
def _(ap, I, O):
    net = _shared(ap, I)
    ap.un.UpdateSubnetwork(net["un"], "Water", "Pressure", "SPECIFIC_SUBNETWORK", "Zone1")
    with ap.da.SearchCursor(f"{net['fds']}/WaterSubnetLine", ["SUBNETWORKNAME", "TIERNAME", "SHAPE@LENGTH"]) as rows:
        lines = sorted([n, t, round(length, 3)] for n, t, length in rows)
    return {"subnetline": lines, **{t: _column(ap, net[t], "SUBNETWORKNAME") for t in ("device", "junction", "line")}}


@case("un.ExportSubnetwork")
def _(ap, I, O):
    net = _shared(ap, I)
    path = os.path.join(net["folder"], "subnetwork.json")
    ap.un.ExportSubnetwork(net["un"], "Water", "Pressure", "Zone1", "NO_ACKNOWLEDGE", path,
                           result_types=["FEATURES", "CONNECTIVITY"])
    doc = json.load(open(path, encoding="utf-8"))
    names = _names(net)
    results = doc.get("resultTypes") or doc
    feats = doc.get("featureElements") or doc.get("features") or []
    found = {"keys": sorted(doc)}
    found["features"] = sorted({names.get(str(f.get("globalId", "")).strip("{}").upper(), "?") for f in feats})
    found["controllers"] = sorted(names.get(str(c.get("globalId", "")).strip("{}").upper(), "?")
                                  for c in doc.get("controllers", []))
    found["connectivity"] = len(doc.get("connectivity", []))
    del results
    return found


@case("un.Trace")
def _(ap, I, O):
    net = _shared(ap, I)
    out = {}
    for name, kind, starts, more in (
            ("connected", "CONNECTED", ["L4"], {}),
            ("connected_barrier", "CONNECTED", ["L4"], {"barriers": ["V1"]}),
            ("connected_isolating", "CONNECTED", ["L3"], {"condition_barriers": ISOLATING}),
            ("connected_edges_only", "CONNECTED", ["L3"], {"condition_barriers": ISOLATING,
                                                           "traversability_scope": "EDGES_ONLY"}),
            ("subnetwork", "SUBNETWORK", ["L6"], {}),
            ("controllers", "SUBNETWORK_CONTROLLERS", ["L6"], {}),
            ("downstream_L2", "DOWNSTREAM", ["L2"], {}),
            ("downstream_L5", "DOWNSTREAM", ["L5"], {}),
            ("upstream_L5", "UPSTREAM", ["L5"], {}),
            ("upstream_L8", "UPSTREAM", ["L8"], {}),
            ("upstream_L8_ignore", "UPSTREAM", ["L8"], {"allow_indeterminate_flow": "IGNORE_INDETERMINATE_FLOW"}),
            ("isolation_L3", "ISOLATION", ["L3"], {"filter_barriers": ISOLATING}),
            ("isolation_L3_isolated", "ISOLATION", ["L3"], {"filter_barriers": ISOLATING,
                                                            "include_isolated_features": "INCLUDE_ISOLATED_FEATURES"}),
            ("shortest_path", "SHORTEST_PATH", ["L1", "L7"], {"shortest_path_network_attribute_name": "Shape length"}),
            ("loops", "LOOPS", ["L4"], {}),
            ("function", "CONNECTED", ["L4"], {"functions": "ADD 'Shape length' # # #"}),
            ("containers", "CONNECTED", ["L4"], {"include_containers": "INCLUDE_CONTAINERS"})):
        try:
            out[name] = _trace(ap, I, net, kind, starts, **more)
        except Exception as error:  # noqa: BLE001 — a refusal is part of the answer
            out[name] = {"error": type(error).__name__}
    return out


@case("un.AddTraceLocations")
def _(ap, I, O):
    net = _shared(ap, I)
    ids = net["ids"]
    path = os.path.join(net["gdb"], "added_locations")
    ap.un.AddTraceLocations(net["un"], path, "DO_NOT_LOAD_SELECTED_FEATURES", "CLEAR_LOCATIONS",
                            [["WaterLine", ids["L3"], "", 0.25], ["WaterDevice", ids["V1"], "(1/Single Terminal)", ""]])
    started = os.path.join(net["folder"], "from_locations.json")
    ap.un.Trace(net["un"], "CONNECTED", path, domain_network="Water", tier="Pressure", result_types=["ELEMENTS"],
                out_json_file=started)
    # The class read back with its global IDs named (they differ between the two sides' networks); LAYERID, an id of
    # Pro's map layer, and the row's own GLOBALID are left out.
    names = _names(net)
    fields = [f.name for f in ap.ListFields(path) if f.type not in ("OID", "Geometry", "GlobalID")
              and f.name.upper() != "LAYERID"]
    rows = []
    with ap.da.SearchCursor(path, fields + ["SHAPE@WKT"]) as read:
        for row in read:
            rows.append([names.get(str(v).strip("{}").upper(), v) if f == "FEATUREGLOBALID" else v
                         for f, v in zip(fields + ["SHAPE"], row)])
    return {"fields": [[f.name, f.type] for f in ap.ListFields(path) if f.name in fields], "rows": rows,
            "trace": _elements(net, started)}


def _trace_config(net, path):
    doc = json.load(open(path, encoding="utf-8"))
    configs = doc if isinstance(doc, list) else doc.get("traceConfigurations", [doc])
    out = []
    for c in configs:
        out.append({k: c.get(k) for k in ("name", "description", "tags", "traceType", "resultTypes")})
        conf = c.get("traceConfiguration") or {}
        if isinstance(conf, str):
            conf = json.loads(conf)
        out[-1]["conditionBarriers"] = conf.get("conditionBarriers")
        out[-1]["domainNetworkName"] = conf.get("domainNetworkName")
        out[-1]["tierName"] = conf.get("tierName")
    return out


@case("un.AddTraceConfiguration")
def _(ap, I, O):
    net = _shared(ap, I)
    ap.un.AddTraceConfiguration(net["un"], "Downstream from the plant", "DOWNSTREAM", "every pipe below", "plant;water",
                                "Water", "Pressure", condition_barriers=ISOLATING, result_types=["SELECTION"])
    path = os.path.join(net["folder"], "configs1.json")
    ap.un.ExportTraceConfigurations(net["un"], "Downstream from the plant", path)
    return _trace_config(net, path)


@case("un.ExportTraceConfigurations")
def _(ap, I, O):
    net = _shared(ap, I)
    path = os.path.join(net["folder"], "configs2.json")
    ap.un.ExportTraceConfigurations(net["un"], "Downstream from the plant", path)
    doc = json.load(open(path, encoding="utf-8"))
    first = doc[0] if isinstance(doc, list) else doc
    return {"type": type(doc).__name__, "keys": sorted(first) if isinstance(first, dict) else None,
            "configs": _trace_config(net, path)}


@case("un.ImportTraceConfigurations")
def _(ap, I, O):
    net = _shared(ap, I)
    path = os.path.join(net["folder"], "configs1.json")
    ap.un.DeleteTraceConfiguration(net["un"], "Downstream from the plant")
    ap.un.ImportTraceConfigurations(net["un"], path)
    again = os.path.join(net["folder"], "configs3.json")
    ap.un.ExportTraceConfigurations(net["un"], "Downstream from the plant", again)
    return _trace_config(net, again)


@case("un.DeleteTraceConfiguration")
def _(ap, I, O):
    net = _shared(ap, I)
    ap.un.DeleteTraceConfiguration(net["un"], "Downstream from the plant")
    path = os.path.join(net["folder"], "configs4.json")
    try:
        ap.un.ExportTraceConfigurations(net["un"], "Downstream from the plant", path)
        return {"left": _trace_config(net, path)}
    except Exception as error:  # noqa: BLE001 — nothing left to export
        return {"left": type(error).__name__}


@case("un.EnableClusterKeys")
def _(ap, I, O):
    net = _build(ap, I, "cluster")
    ap.un.EnableClusterKeys(net["un"])
    return {t: [f for f in _fields(ap, net[t]) if "CLUSTER" in f[0].upper()] for t in ("device", "junction", "line")}


@case("un.CalculateClusterKeys")
def _(ap, I, O):
    net = _build(ap, I, "cluster")
    ap.un.EnableClusterKeys(net["un"])
    ap.un.CalculateClusterKeys(net["un"])
    out = {}
    for t in ("device", "junction", "line"):
        field = next((f[0] for f in _fields(ap, net[t]) if "CLUSTER" in f[0].upper()), None)
        out[t] = None if field is None else len({v for _, v in _column(ap, net[t], field) if v is not None})
    return out


# ---- network diagrams -----------------------------------------------------------------------------------------------
# Pro applies only the first layout a process runs on a file geodatabase's diagrams (measured 2026-10-31, ADR-232), so
# the layout cases (LAYOUT_CASES) are run one per process: harness.py ... un <out> <case>.

def _content(ap, net, diagram, positions=False):
    """A diagram as ExportDiagramContent answers it: its junctions, containers and edges named back through their
    associated global IDs; positions rounded to the centimetre when asked."""
    net["count"] = net.get("count", 0) + 1
    out = os.path.join(net["folder"], f"content{net['count']}.json")
    ap.un.ExportDiagramContent(net["un"], diagram, out, "INCLUDE_DIAGRAM_PROPERTIES", "INCLUDE_GEOMETRIES",
                               "INCLUDE_ATTRIBUTES", "INCLUDE_AGGREGATIONS", "USE_CODED_VALUE_NAMES")
    doc = json.load(open(out, encoding="utf-8"))
    names = _names(net)
    label = {}
    found = {"J": {}, "C": {}, "E": []}
    for j in doc.get("junctions", []):
        name = names.get(str(j.get("assocGlobalID", "")).strip("{}").upper(), "system")
        label[j.get("id")] = name
        g = j.get("geometry") or {}
        found["J"].setdefault(name, []).append([round(g.get("x", 0) - net.get("gx", 0), 2),
                                                 round(g.get("y", 0) - net.get("gy", 0), 2)] if positions else 1)
    for c in doc.get("containers", []):
        name = names.get(str(c.get("assocGlobalID", "")).strip("{}").upper(), "?")
        label[c.get("id")] = name
        found["C"][name] = 1
    for e in doc.get("edges", []):
        name = "R" if e.get("aggregations") else names.get(str(e.get("assocGlobalID", "")).strip("{}").upper(), "?")
        agg = sorted(names.get(str(a.get("globalID", "")).strip("{}").upper(), "?") for a in e.get("aggregations", []))
        found["E"].append([name, label.get(e.get("fromID")), label.get(e.get("toID")), agg])
    found["E"].sort(key=str)
    found["J"] = {k: sorted(v) if positions else len(v) for k, v in found["J"].items()}
    found["info"] = {k: doc.get("diagramInfo", {}).get(k) for k in ("junctionCount", "edgeCount", "containerCount")}
    return found


def _everything(net):
    return [net["device"], net["line"], net["junction"], net["vault"]]


def _template(ap, net, name, rules=()):
    ap.un.AddDiagramTemplate(net["un"], name)
    for rule, args in rules:
        more = net.pop("kwargs", {}) if rule in LAYOUTS else {}
        getattr(ap.un, rule)(net["un"], name, *args, **more)
    return name


def _diagram(ap, I, name, rules=(), features=None, positions=False):
    net = _shared(ap, I)
    net["gx"], net["gy"] = _xy(I, 0, 0)
    _template(ap, net, name, rules)
    diagram = ap.un.CreateDiagram(net["un"], name, features or _everything(net))[1]
    return net, diagram, _content(ap, net, diagram, positions)


def _rule_case(name, rules, features=None):
    def run(ap, I, O):
        _AP[0] = ap
        net = _shared(ap, I)
        resolved = [(r, [a(net) if callable(a) else a for a in args]) for r, args in rules]
        feats = features(net) if features else None
        return _diagram(ap, I, name.split(".")[-1], resolved, feats)[2]
    CASES[name] = run


def _d(key):
    return lambda net: net[key]


def _ds(*keys):
    return lambda net: [net[k] for k in keys]


_rule_case("un.AddDiagramTemplate", [])
_rule_case("un.AddReduceJunctionRule", [("AddReduceJunctionRule", ["ACTIVE", "INCLUDE_SOURCE_CLASSES",
                                                                   _ds("device")])])
_rule_case("un.AddReduceJunctionByAttributeRule", [("AddReduceJunctionByAttributeRule",
                                                    ["ACTIVE", _d("device"), "NAME = 'V1'"])])
_rule_case("un.AddReduceEdgeByAttributeRule", [("AddReduceEdgeByAttributeRule", ["ACTIVE", _d("line"),
                                                                                 "NAME IN ('L7', 'L8')"])])
_rule_case("un.AddRemoveFeatureRule", [("AddRemoveFeatureRule", ["ACTIVE", "JUNCTIONS", "INCLUDE_SOURCE_CLASSES",
                                                                 _ds("junction")])])
_rule_case("un.AddRemoveFeatureByAttributeRule", [("AddRemoveFeatureByAttributeRule", ["ACTIVE", _d("line"),
                                                                                       "NAME = 'L6'"])])
_rule_case("un.AddCollapseContainerRule", [("AddCollapseContainerRule", ["ACTIVE", "BOTH",
                                                                         "INCLUDE_SOURCE_CLASSES", _ds("vault")])])
_rule_case("un.AddCollapseContainerByAttributeRule", [("AddCollapseContainerByAttributeRule",
                                                       ["ACTIVE", _d("vault"), "NAME = 'VA'"])])
_rule_case("un.AddExpandContainerRule", [("AddExpandContainerRule", ["ACTIVE", "KEEP_VISIBLE", "BOTH",
                                                                     "INCLUDE_SOURCE_CLASSES", _ds("vault")])],
           features=_ds("vault"))
_rule_case("un.AddExpandContainerByAttributeRule", [("AddExpandContainerByAttributeRule",
                                                     ["ACTIVE", "KEEP_VISIBLE", _d("vault"), "NAME = 'VA'"])],
           features=_ds("vault"))
_rule_case("un.AddConnectivityAssociationsRule", [("AddConnectivityAssociationsRule", ["ACTIVE"])])
_rule_case("un.AddStructuralAttachmentsRule", [("AddStructuralAttachmentsRule", ["ACTIVE"])])
_rule_case("un.AddStartIterationRule", [("AddStartIterationRule", ["ACTIVE"]),
                                        ("AddReduceJunctionByAttributeRule", ["ACTIVE", _d("device"), "NAME = 'V1'"]),
                                        ("AddStopIterationRule", ["ACTIVE"])])
_rule_case("un.AddStopIterationRule", [("AddStartIterationRule", ["ACTIVE"]),
                                       ("AddReduceJunctionByAttributeRule", ["ACTIVE", _d("device"), "NAME = 'V1'"]),
                                       ("AddStopIterationRule", ["ACTIVE"])])
_rule_case("un.AddSetRootJunctionByAttributeRule", [("AddSetRootJunctionByAttributeRule",
                                                     ["ACTIVE", _d("device"), "NAME = 'S'"]),
                                                    ("AddSmartTreeLayout", ["ACTIVE"])])
def _lines(*names):
    """A layer of the named lines: the features a diagram is made from."""
    def make(net):
        net["layers"] = net.get("layers", 0) + 1
        where = "NAME IN ({})".format(", ".join(f"'{n}'" for n in names))
        return [_AP[0].management.MakeFeatureLayer(net["line"], f"lines{net['layers']}", where)[0]]
    return make


_AP = [None]
_rule_case("un.AddSetStartingPointByAttributeRule", [("AddSetStartingPointByAttributeRule",
                                                      ["ACTIVE", _d("line"), "NAME = 'L5'"]),
                                                     ("AddTraceRule", ["ACTIVE", "DOWNSTREAM", "Water", "Pressure"])],
           features=_lines("L1", "L5"))
_rule_case("un.AddTraceRule", [("AddTraceRule", ["ACTIVE", "DOWNSTREAM", "Water", "Pressure"])], features=_lines("L5"))
_rule_case("un.AddDiagramFeatureCapabilityByAttributeRule", [
    ("AddDiagramFeatureCapabilityByAttributeRule", ["ACTIVE", _d("vault"), "NAME = 'VA'",
                                                    "PREVENT_TO_COLLAPSE_CONTAINER"]),
    ("AddCollapseContainerRule", ["ACTIVE", "BOTH", "INCLUDE_SOURCE_CLASSES", _ds("vault")])])
_on_container = CASES["un.AddDiagramFeatureCapabilityByAttributeRule"]
_rule_case("un.CapabilityOnContent", [
    ("AddDiagramFeatureCapabilityByAttributeRule", ["ACTIVE", _d("device"), "NAME = 'V2'",
                                                    "PREVENT_TO_COLLAPSE_CONTAINER"]),
    ("AddCollapseContainerRule", ["ACTIVE", "BOTH", "INCLUDE_SOURCE_CLASSES", _ds("vault")])])
_on_content = CASES.pop("un.CapabilityOnContent")
# Which feature the capability is set on: the container, and the content inside it.
CASES["un.AddDiagramFeatureCapabilityByAttributeRule"] = lambda ap, I, O: {"on_container": _on_container(ap, I, O),
                                                                          "on_content": _on_content(ap, I, O)}
_rule_case("un.AddSpatialQueryRule", [("AddSpatialQueryRule", ["ACTIVE", _d("vault"), "INTERSECT", _d("device")])],
           features=_ds("device", "line", "junction"))

LAYOUTS = {
    # Pro refuses its default cells here (ERROR 001777, "The cells of the grid are too small").
    "AddGridLayout": ("ApplyGridLayout", {"cell_width_absolute": "20 Meters", "cell_height_absolute": "20 Meters"}),
    "AddLinearDispatchLayout": ("ApplyLinearDispatchLayout", {}),
    "AddSpatialDispatchLayout": ("ApplySpatialDispatchLayout", {}),
    "AddCompressionLayout": ("ApplyCompressionLayout", {}),
    "AddAngleDirectedLayout": ("ApplyAngleDirectedLayout", {}),
    "AddForceDirectedLayout": ("ApplyForceDirectedLayout", {}),
    "AddSmartTreeLayout": ("ApplySmartTreeLayout", {}),
    "AddMainlineTreeLayout": ("ApplyMainlineTreeLayout", {}),
    "AddRadialTreeLayout": ("ApplyRadialTreeLayout", {}),
    "AddMainRingLayout": ("ApplyMainRingLayout", {}),
    # Pro's Apply needs both values (ERROR 000530); its Add was measured taking the buffer width alone.
    "AddPartialOverlappingEdgesLayout": ("ApplyPartialOverlappingEdgesLayout", {"buffer_width_absolute": "1 Meters",
                                                                                "offset_absolute": "0.5 Meters"}),
    "AddReshapeDiagramEdgesLayout": ("ReshapeDiagramEdgesLayout", {}),
}
ROOT = ("AddSetRootJunctionByAttributeRule", ["ACTIVE", _d("device"), "NAME = 'S'"])
TREES = {"AddSmartTreeLayout", "AddMainlineTreeLayout", "AddRadialTreeLayout", "AddMainRingLayout",
         "ApplySmartTreeLayout", "ApplyMainlineTreeLayout", "ApplyRadialTreeLayout", "ApplyMainRingLayout"}
LAYOUT_CASES = []


def _layout_template_case(add):
    def run(ap, I, O):
        net = _shared(ap, I)
        rules = [(ROOT[0], [a(net) if callable(a) else a for a in ROOT[1]])] if add in TREES else []
        net["kwargs"] = LAYOUTS.get(add, (None, {}))[1]
        return _diagram(ap, I, add, rules + [(add, ["ACTIVE"])], positions=True)[2]
    CASES[f"un.{add}"] = run
    LAYOUT_CASES.append(f"un.{add}")


def _layout_apply_case(apply, kwargs):
    def run(ap, I, O):
        net = _shared(ap, I)
        rules = [(ROOT[0], [a(net) if callable(a) else a for a in ROOT[1]])] if apply in TREES else []
        net, diagram, _ = _diagram(ap, I, apply, rules)
        layer = ap.un.MakeDiagramLayer(net["un"], diagram, f"view_{apply}")[0]
        getattr(ap.un, apply)(layer, **kwargs)
        return _content(ap, net, diagram, positions=True)
    CASES[f"un.{apply}"] = run
    LAYOUT_CASES.append(f"un.{apply}")


for _add, (_apply, _kwargs) in LAYOUTS.items():
    _layout_template_case(_add)
    _layout_apply_case(_apply, _kwargs)
_layout_apply_case("ApplyGeoPositionsLayout", {})


@case("un.ApplyTemplateLayouts")
def _(ap, I, O):
    net = _shared(ap, I)
    rules = [(ROOT[0], [ROOT[1][0], net["device"], ROOT[1][2]]), ("AddSmartTreeLayout", ["ACTIVE"])]
    net, diagram, _ = _diagram(ap, I, "ApplyTemplateLayouts", rules)
    layer = ap.un.MakeDiagramLayer(net["un"], diagram, "view_template")[0]
    ap.un.ApplyGeoPositionsLayout(layer)
    ap.un.ApplyTemplateLayouts(layer)
    return _content(ap, net, diagram, positions=True)


LAYOUT_CASES.append("un.ApplyTemplateLayouts")


@case("un.CreateDiagram")
def _(ap, I, O):
    net, diagram, content = _diagram(ap, I, "CreateDiagram", features=_ds("line")(_shared(ap, I)))
    result = ap.un.CreateDiagram(net["un"], "CreateDiagram", _everything(net))
    return {"from_lines": content, "outputs": result.outputCount, "all": _content(ap, net, result[1])}


@case("un.MakeDiagramLayer")
def _(ap, I, O):
    net, diagram, _ = _diagram(ap, I, "MakeDiagramLayer")
    layer = ap.un.MakeDiagramLayer(net["un"], diagram, "made_layer")[0]
    return {"name": getattr(layer, "name", str(layer)), "isNetworkDiagramLayer":
            getattr(layer, "isNetworkDiagramLayer", "<missing>")}


@case("un.GetDiagramTemplateNames")
def _(ap, I, O):
    net = _shared(ap, I)
    _template(ap, net, "Listed")
    return sorted(n for n in ap.un.GetDiagramTemplateNames(net["un"]) if n in ("Listed", "Doomed", "Renamed"))


@case("un.DeleteDiagramTemplate")
def _(ap, I, O):
    net = _shared(ap, I)
    _template(ap, net, "Doomed")
    ap.un.DeleteDiagramTemplate(net["un"], "Doomed")
    return sorted(n for n in ap.un.GetDiagramTemplateNames(net["un"]) if n in ("Listed", "Doomed", "Renamed"))


@case("un.AlterDiagramTemplate")
def _(ap, I, O):
    net = _shared(ap, I)
    _template(ap, net, "Altered")
    ap.un.AlterDiagramTemplate(net["un"], "Altered", "Renamed", description="renamed here")
    return sorted(n for n in ap.un.GetDiagramTemplateNames(net["un"]) if n in ("Altered", "Renamed"))


def _xml_names(path):
    import re
    text = open(path, encoding="utf-8", errors="replace").read()
    root = re.search(r"<([A-Za-z_][\w.:-]*)", re.sub(r"<\?.*?\?>", "", text, flags=re.S))
    return root.group(1) if root else None


@case("un.ExportDiagramTemplateDefinitions")
def _(ap, I, O):
    net = _shared(ap, I)
    _template(ap, net, "Exported", [("AddReduceJunctionRule", ["ACTIVE", "INCLUDE_SOURCE_CLASSES", [net["device"]]]),
                                    ("AddSmartTreeLayout", ["ACTIVE"])])
    ndbd, ndld = os.path.join(net["folder"], "exported.ndbd"), os.path.join(net["folder"], "exported.ndld")
    ap.un.ExportDiagramTemplateDefinitions(net["un"], "Exported", ndbd, ndld)
    return {"ndbd": _xml_names(ndbd), "ndld": _xml_names(ndld)}


@case("un.ImportDiagramTemplateDefinitions")
def _(ap, I, O):
    net = _shared(ap, I)
    _template(ap, net, "ToImport", [("AddReduceJunctionRule", ["ACTIVE", "INCLUDE_SOURCE_CLASSES", [net["device"]]])])
    ndbd = os.path.join(net["folder"], "to_import.ndbd")
    ap.un.ExportDiagramTemplateDefinitions(net["un"], "ToImport", ndbd)
    _template(ap, net, "Imported")
    ap.un.ImportDiagramTemplateDefinitions(net["un"], "Imported", ndbd)
    diagram = ap.un.CreateDiagram(net["un"], "Imported", _everything(net))[1]
    return _content(ap, net, diagram)


@case("un.ExportDiagramContent")
def _(ap, I, O):
    net, diagram, content = _diagram(ap, I, "ExportDiagramContent")
    out = os.path.join(net["folder"], "content_keys.json")
    ap.un.ExportDiagramContent(net["un"], diagram, out, "INCLUDE_DIAGRAM_PROPERTIES", "INCLUDE_GEOMETRIES",
                               "INCLUDE_ATTRIBUTES", "INCLUDE_AGGREGATIONS", "USE_CODED_VALUE_NAMES")
    doc = json.load(open(out, encoding="utf-8"))
    return {"content": content, "keys": sorted(doc), "info": sorted(doc.get("diagramInfo", {})),
            "junction": sorted(doc["junctions"][0]) if doc.get("junctions") else None,
            "edge": sorted(doc["edges"][0]) if doc.get("edges") else None}


@case("un.StoreDiagram")
def _(ap, I, O):
    net, diagram, _ = _diagram(ap, I, "StoreDiagram", features=_ds("line")(_shared(ap, I)))
    layer = ap.un.MakeDiagramLayer(net["un"], diagram, "view_store")[0]
    ap.un.StoreDiagram(layer, "Stored one", "PUBLIC", "a;b")
    return _content(ap, net, "Stored one")


@case("un.AlterDiagramProperties")
def _(ap, I, O):
    net, diagram, _ = _diagram(ap, I, "AlterDiagramProperties", features=_ds("line")(_shared(ap, I)))
    layer = ap.un.MakeDiagramLayer(net["un"], diagram, "view_alter")[0]
    ap.un.StoreDiagram(layer, "Before alter", "PUBLIC")
    stored = ap.un.MakeDiagramLayer(net["un"], "Before alter", "view_alter2")[0]
    ap.un.AlterDiagramProperties(stored, "After alter", "PUBLIC", "x")
    return _content(ap, net, "After alter")


def _exists(ap, net, diagram):
    try:
        _content(ap, net, diagram)
        return True
    except Exception:  # noqa: BLE001 — a diagram that is gone cannot be exported
        return False


@case("un.DeleteDiagram")
def _(ap, I, O):
    net, diagram, _ = _diagram(ap, I, "DeleteDiagram", features=_ds("line")(_shared(ap, I)))
    layer = ap.un.MakeDiagramLayer(net["un"], diagram, "view_delete")[0]
    ap.un.StoreDiagram(layer, "To delete", "PUBLIC")
    before = _exists(ap, net, "To delete")
    ap.un.DeleteDiagram(net["un"], None, ["To delete"])
    return {"before": before, "after": _exists(ap, net, "To delete")}


@case("un.ChangeDiagramsOwner")
def _(ap, I, O):
    net, diagram, _ = _diagram(ap, I, "ChangeDiagramsOwner", features=_ds("line")(_shared(ap, I)))
    layer = ap.un.MakeDiagramLayer(net["un"], diagram, "view_owner")[0]
    ap.un.StoreDiagram(layer, "Owned", "PUBLIC")
    ap.un.ChangeDiagramsOwner(net["un"], "someone", None, ["Owned"])
    return _exists(ap, net, "Owned")


@case("un.PurgeTemporaryDiagrams")
def _(ap, I, O):
    net, diagram, _ = _diagram(ap, I, "PurgeTemporaryDiagrams", features=_ds("line")(_shared(ap, I)))
    before = _exists(ap, net, diagram)
    ap.un.PurgeTemporaryDiagrams(net["un"])
    return {"before": before, "after": _exists(ap, net, diagram)}


@case("un.UpdateDiagram")
def _(ap, I, O):
    net, diagram, _ = _diagram(ap, I, "UpdateDiagram", features=_ds("line")(_shared(ap, I)))
    ap.un.UpdateDiagram(net["un"], ["UpdateDiagram"], None, "INCONSISTENT_DIAGRAMS_ONLY")
    return _content(ap, net, diagram)


@case("un.ExtendDiagram")
def _(ap, I, O):
    net = _shared(ap, I)
    feats = [ap.management.MakeFeatureLayer(net["line"], "extend_lines", "NAME = 'L3'")[0]]
    _template(ap, net, "ExtendDiagram")
    ap.un.AlterDiagramTemplate(net["un"], "ExtendDiagram", is_diagram_extension_enabled="ENABLE_DIAGRAM_EXTENSION")
    diagram = ap.un.CreateDiagram(net["un"], "ExtendDiagram", feats)[1]
    before = _content(ap, net, diagram)
    layer = ap.un.MakeDiagramLayer(net["un"], diagram, "view_extend")[0]
    ap.un.ExtendDiagram(layer)
    return {"before": before, "after": _content(ap, net, diagram)}


@case("un.AppendToDiagram")
def _(ap, I, O):
    net = _shared(ap, I)
    feats = [ap.management.MakeFeatureLayer(net["line"], "append_lines", "NAME = 'L3'")[0]]
    net, diagram, _ = _diagram(ap, I, "AppendToDiagram", features=feats)
    layer = ap.un.MakeDiagramLayer(net["un"], diagram, "view_append")[0]
    ap.management.SelectLayerByAttribute(ap.management.MakeFeatureLayer(net["line"], "append_more")[0],
                                         "NEW_SELECTION", "NAME = 'L4'")
    ap.un.AppendToDiagram(layer)
    return _content(ap, net, diagram)


@case("un.OverwriteDiagram")
def _(ap, I, O):
    net = _shared(ap, I)
    feats = [ap.management.MakeFeatureLayer(net["line"], "overwrite_lines", "NAME = 'L3'")[0]]
    net, diagram, _ = _diagram(ap, I, "OverwriteDiagram", features=feats)
    layer = ap.un.MakeDiagramLayer(net["un"], diagram, "view_overwrite")[0]
    ap.management.SelectLayerByAttribute(ap.management.MakeFeatureLayer(net["line"], "overwrite_more")[0],
                                         "NEW_SELECTION", "NAME = 'L4'")
    ap.un.OverwriteDiagram(layer)
    return _content(ap, net, diagram)


# ---- the runs -------------------------------------------------------------------------------------------------------
# Three batches, each its own process on each side: A the network tools, B the diagram tools that apply no layout, and
# the layout cases one per process (LAYOUT_CASES).

_DIAGRAM_TOOLS = {"AddDiagramTemplate", "CreateDiagram", "MakeDiagramLayer", "GetDiagramTemplateNames",
                  "DeleteDiagramTemplate", "AlterDiagramTemplate", "ExportDiagramTemplateDefinitions",
                  "ImportDiagramTemplateDefinitions", "ExportDiagramContent", "StoreDiagram", "AlterDiagramProperties",
                  "DeleteDiagram", "ChangeDiagramsOwner", "PurgeTemporaryDiagrams", "UpdateDiagram", "ExtendDiagram",
                  "AppendToDiagram", "OverwriteDiagram"}
# Pro stores one diagram per process in a file geodatabase, as it lays out one (measured 2026-11-12: a second
# StoreDiagram fails with ERROR 001771), so the cases that store one run alone too.
LAYOUT_CASES += ["un.StoreDiagram", "un.AlterDiagramProperties", "un.DeleteDiagram", "un.ChangeDiagramsOwner",
                 "un.ExtendDiagram"]
BATCHES = {"L": list(LAYOUT_CASES)}
BATCHES["B"] = [k for k in CASES if k not in LAYOUT_CASES and (
    k.split(".")[1] in _DIAGRAM_TOOLS or (k.split(".")[1].endswith("Rule") and k not in ("un.AddRule", "un.DeleteRule")))]
BATCHES["A"] = [k for k in CASES if k not in BATCHES["L"] and k not in BATCHES["B"]]
