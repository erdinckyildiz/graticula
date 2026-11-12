"""arcpy.nd against ArcGIS Pro 3.7: one case for each name grapy builds.

Every case runs on the water utility network of ADR-232 §4 (the network `tools/pro-compare/adr232-diagrams.py`
measured), built here in its own geodatabase (``nd``) the same way on both sides, with a pole and two associations more:

- devices S (a source, the subnetwork controller), V1 and V2 (valves, category Isolating); tees T and E; lines L1 to
  L4 (S–V1–T–V2 along y = 0, T–E up to (20, 10)); a vault VA at V2 holding it (a containment association); a pole P
  at (6, 3) with V1 structurally attached; a junction–junction connectivity association T–V1;
- every association goes in through ImportAssociations, on both sides.

A diagram is read through ExportDiagramContent's JSON: junctions and containers named back through their associated
global IDs, with their positions; edges as [name, from, to, aggregated names] (``R`` a reduction edge).

**Pro applies only the first layout a Python process runs on a file geodatabase's diagrams** — later ones succeed and
move nothing (measured 2026-10-31, ADR-232 condition 5) — and stores only one diagram a process (a later StoreDiagram
fails, ERROR 001771), so every case in ``LAYOUT_CASES`` must run in a process of its own: one harness call per case,
the others together.
"""
import csv
import json
import os
import shutil

CASES = {}
LAYOUT_CASES = []
_STATE = {}


def case(name, layout=False):
    def register(function):
        CASES[name] = function
        if layout:
            LAYOUT_CASES.append(name)
        return function
    return register


def _pro(ap):
    return not hasattr(ap, "graticula")


# ---- the network --------------------------------------------------------------------------------------------------

def _net(ap, I):
    """ADR-232 §4's water network, built once a run: {gdb, fds, un, device, line, junction, vault, ids, names}; the
    structure class ``vault`` holds the vault VA and the pole P."""
    if "un" in _STATE:
        return _STATE
    if "failed" in _STATE:          # built once a run: a network that failed is not built again for every case
        raise RuntimeError(f"the network could not be built: {_STATE['failed']}")
    try:
        return _build(ap, I)
    except Exception as error:
        _STATE["failed"] = f"{type(error).__name__}: {error}"
        raise


def _build(ap, I):
    folder = os.path.join(I["work"], "nd")
    shutil.rmtree(folder, ignore_errors=True)
    os.makedirs(folder)
    sr = ap.SpatialReference(3857)
    gdb = ap.management.CreateFileGDB(folder, "water")[0]
    fds = ap.management.CreateFeatureDataset(gdb, "utility", sr)[0]
    territory = ap.management.CreateFeatureclass(fds, "Territory", "POLYGON", has_z="ENABLED", has_m="ENABLED",
                                                 spatial_reference=sr)[0]
    with ap.da.InsertCursor(territory, ["SHAPE@WKT"]) as rows:
        rows.insertRow(["POLYGON Z ((-1000 -1000 0, 10000 -1000 0, 10000 10000 0, -1000 10000 0, -1000 -1000 0))"])
    un = ap.un.CreateUtilityNetwork(fds, "Water", territory)[0]
    ap.un.AddDomainNetwork(un, "Water", "PARTITIONED", "SOURCE")
    ap.un.AddTier(un, "Water", "Pressure", 1, "RADIAL")
    device, line, junction, vault = (f"{fds}/WaterDevice", f"{fds}/WaterLine", f"{fds}/WaterJunction",
                                     f"{fds}/StructureJunction")
    for table, groups, standard in ((device, [(1, "Source"), (2, "Valve")], "Standard"),
                                    (line, [(1, "Main")], "Ductile"), (junction, [(1, "Tee")], "Standard"),
                                    (vault, [(1, "Vault"), (2, "Pole")], "Concrete")):
        for code, text in groups:
            # Pro wants a domain of its own per asset group (ERROR 002580), holding the ASSETTYPE default 0 (002943).
            domain = f"{os.path.basename(table)}_{text}_Types"
            ap.management.CreateDomain(gdb, domain, domain, "SHORT", "CODED")
            ap.management.AddCodedValueToDomain(gdb, domain, 0, "Unknown")
            ap.management.AddCodedValueToDomain(gdb, domain, 1, standard)
            ap.management.AddSubtype(table, code, text)
            ap.management.AssignDomainToField(table, "ASSETTYPE", domain, code)
    for table in (device, line, junction, vault):
        ap.management.AddField(table, "NAME", "TEXT")
    ap.un.AddTerminalConfiguration(un, "Source-Load", "DIRECTIONAL", "Source true;Load false")
    ap.un.SetTerminalConfiguration(un, "Water", "WaterDevice", "Source", "Standard", "Source-Load")
    ap.un.SetNetworkCategory(un, "Water", "WaterDevice", "Source", "Standard", "Subnetwork Controller")
    ap.un.AddNetworkCategory(un, "Isolating")
    ap.un.SetNetworkCategory(un, "Water", "WaterDevice", "Valve", "Standard", ["Isolating"])
    je = "JUNCTION_EDGE_CONNECTIVITY"
    ap.un.AddRule(un, je, "WaterDevice", "Source", "Standard", "WaterLine", "Main", "Ductile", "Load")
    ap.un.AddRule(un, je, "WaterDevice", "Valve", "Standard", "WaterLine", "Main", "Ductile")
    ap.un.AddRule(un, je, "WaterJunction", "Tee", "Standard", "WaterLine", "Main", "Ductile")
    ap.un.AddRule(un, "JUNCTION_JUNCTION_CONNECTIVITY", "WaterJunction", "Tee", "Standard", "WaterDevice", "Valve",
                  "Standard")
    # Pro refuses a containment rule on an asset type with no container role (ERROR 002908).
    ap.un.SetAssociationRole(un, "Structure", "StructureJunction", "Vault", "Concrete", "CONTAINER", "RESTRICTED")
    ap.un.AddRule(un, "CONTAINMENT", "StructureJunction", "Vault", "Concrete", "WaterDevice", "Valve", "Standard")
    # a structure attaches, a container contains: the pole P, a structure, has V1 attached
    ap.un.SetAssociationRole(un, "Structure", "StructureJunction", "Pole", "Concrete", "STRUCTURE", "RESTRICTED")
    ap.un.AddRule(un, "STRUCTURAL_ATTACHMENT", "StructureJunction", "Pole", "Concrete", "WaterDevice", "Valve",
                  "Standard")
    ap.un.SetSubnetworkDefinition(un, "Water", "Pressure", None, ["Source/Standard", "Valve/Standard"],
                                  ["Source/Standard"], ["Main/Ductile"], valid_junctions=["Tee/Standard"])
    ids = {}

    def insert(table, rows):
        with ap.da.Editor(gdb), ap.da.InsertCursor(table, ["NAME", "ASSETGROUP", "ASSETTYPE", "SHAPE@WKT"]) as out:
            for row in rows:
                out.insertRow(row)
        with ap.da.SearchCursor(table, ["NAME", "GLOBALID@"]) as read:
            ids.update({n: g.upper() for n, g in read})

    insert(device, [("S", 1, 1, "POINT Z (0 0 0)"), ("V1", 2, 1, "POINT Z (6 0 0)"), ("V2", 2, 1, "POINT Z (30 0 0)")])
    insert(junction, [("T", 1, 1, "POINT Z (20 0 0)"), ("E", 1, 1, "POINT Z (20 10 0)")])
    insert(line, [("L1", 1, 1, "LINESTRING Z (0 0 0, 6 0 0)"), ("L2", 1, 1, "LINESTRING Z (6 0 0, 13 0 0, 20 0 0)"),
                  ("L3", 1, 1, "LINESTRING Z (20 0 0, 30 0 0)"), ("L4", 1, 1, "LINESTRING Z (20 0 0, 20 10 0)")])
    insert(vault, [("VA", 1, 1, "POINT Z (30 0 0)"), ("P", 2, 1, "POINT Z (6 3 0)")])
    controllers = os.path.join(folder, "controllers.csv")
    with open(controllers, "w", newline="", encoding="utf-8") as written:
        out = csv.writer(written)
        out.writerow(["SUBNETWORKCONTROLLERNAME", "FEATUREGLOBALID", "FEATURECLASSNAME", "FEATUREASSETGROUP",
                      "FEATUREASSETTYPE", "FEATURETERMINAL", "TIERNAME", "SUBNETWORKNAME", "DESCRIPTION", "NOTES"])
        out.writerow(["Plant", ids["S"], "WaterDevice", "Source", "Standard", "Load", "Pressure", "Zone1", "", ""])
    ap.un.EnableNetworkTopology(un)
    ap.un.ImportSubnetworkControllers(un, controllers)
    associations = os.path.join(folder, "associations.csv")
    with open(associations, "w", newline="", encoding="utf-8") as written:
        out = csv.writer(written)
        out.writerow(["ASSOCIATIONTYPE", "FROMFEATURECLASS", "FROMASSETGROUP", "FROMASSETTYPE", "FROMGLOBALID",
                      "FROMTERMINAL", "TOFEATURECLASS", "TOASSETGROUP", "TOASSETTYPE", "TOGLOBALID", "TOTERMINAL",
                      "ISCONTENTVISIBLE"])
        out.writerow(["Containment", "StructureJunction", "Vault", "Concrete", ids["VA"], "", "WaterDevice", "Valve",
                      "Standard", ids["V2"], "", "FALSE"])
        out.writerow(["Structural Attachment", "StructureJunction", "Pole", "Concrete", ids["P"], "", "WaterDevice",
                      "Valve", "Standard", ids["V1"], "", ""])
        # Pro names "no terminal" in a connectivity association "Single Terminal" (an empty one is ERROR 000192)
        out.writerow(["Junction Junction Connectivity", "WaterJunction", "Tee", "Standard", ids["T"], "Single Terminal",
                      "WaterDevice", "Valve", "Standard", ids["V1"], "Single Terminal", ""])
    ap.un.ImportAssociations(un, "ALL", associations)
    ap.un.ValidateNetworkTopology(un)
    _STATE.update(gdb=gdb, fds=fds, un=str(un), device=device, line=line, junction=junction, vault=vault, ids=ids,
                  names={g.strip("{}"): n for n, g in ids.items()}, folder=folder, count=0)
    return _STATE


def _all(net):
    return [net["device"], net["line"], net["junction"], net["vault"]]


def _subset(ap, net, table, where):
    """A feature layer of one class's features a clause selects (a diagram's input)."""
    _STATE["count"] += 1
    return ap.management.MakeFeatureLayer(net[table], f"nd_sub{_STATE['count']}", where)[0]


def _template(ap, net, name, rules=()):
    ap.nd.AddDiagramTemplate(net["un"], name)
    for rule, args in rules:
        getattr(ap.nd, rule)(net["un"], name, *args)
    return name


def _diagram(ap, net, template, features=None):
    return str(ap.nd.CreateDiagram(net["un"], template, features if features is not None else _all(net))[1])


def _layer(ap, net, diagram):
    _STATE["count"] += 1
    return ap.nd.MakeDiagramLayer(net["un"], diagram, f"nd_view{_STATE['count']}")[0]


def _export(ap, net, diagram):
    _STATE["count"] += 1
    out = os.path.join(net["folder"], f"content{_STATE['count']}.json")
    ap.nd.ExportDiagramContent(net["un"], diagram, out, "INCLUDE_DIAGRAM_PROPERTIES", "INCLUDE_GEOMETRIES",
                               "INCLUDE_ATTRIBUTES", "INCLUDE_AGGREGATIONS", "USE_CODED_VALUE_NAMES")
    return json.load(open(out, encoding="utf-8"))


def _content(ap, net, diagram):
    """A diagram's junctions {name: (x, y)}, containers {name: [xmin, ymin, xmax, ymax]}, edges and counts."""
    doc = _export(ap, net, diagram)
    names = net["names"]
    label = {}
    found = {"J": {}, "C": {}, "E": []}

    def named(feature):
        return names.get(str(feature.get("assocGlobalID", "")).strip("{}").upper(), "#")

    for j in doc.get("junctions", []):
        name = named(j)
        label[j.get("id")] = name
        g = j.get("geometry") or {}
        found["J"][name] = [round(g.get("x", 0), 3), round(g.get("y", 0), 3)]
    for c in doc.get("containers", []):
        name = named(c)
        label[c.get("id")] = name
        g = c.get("geometry") or {}
        xs = [p[0] for ring in g.get("rings", []) for p in ring]
        ys = [p[1] for ring in g.get("rings", []) for p in ring]
        found["C"][name] = [round(min(xs), 3), round(min(ys), 3), round(max(xs), 3), round(max(ys), 3)] if xs else \
            sorted(g)
    for e in doc.get("edges", []):
        name = "R" if e.get("aggregations") else named(e)
        agg = sorted(names.get(str(a.get("globalID", "")).strip("{}").upper(), "?") for a in e.get("aggregations", []))
        g = e.get("geometry") or {}
        path = [[round(p[0], 3), round(p[1], 3)] for p in (g.get("paths") or [[]])[0]]
        found["E"].append([name, label.get(e.get("fromID")), label.get(e.get("toID")), agg, path])
    found["E"].sort(key=str)
    info = doc.get("diagramInfo") or {}
    found["info"] = {k: info.get(k) for k in ("junctionCount", "edgeCount", "containerCount")}
    return found


def _shape(found):
    """A diagram without its positions: which features are in it and how they connect."""
    return {"J": sorted(found["J"]), "C": sorted(found["C"]), "E": [e[:4] for e in found["E"]],
            "info": found["info"]}


def _rules_case(ap, I, name, rules, features=None):
    net = _net(ap, I)
    template = _template(ap, net, name, rules)
    return _shape(_content(ap, net, _diagram(ap, net, template, features)))


def _root(net):
    return ("AddSetRootJunctionByAttributeRule", ["ACTIVE", net["device"], "NAME = 'S'"])


# ---- templates ----------------------------------------------------------------------------------------------------

def _names(ap, net):
    return sorted(str(ap.nd.GetDiagramTemplateNames(net["un"])[0]).split(";"))


@case("nd.GetDiagramTemplateNames")
def _(ap, I, O):
    # a new network's names, and what its default templates make of every feature and of the vault alone
    net = _net(ap, I)
    names = _names(ap, net)
    made = {}
    for template in names:
        made[template] = _content(ap, net, _diagram(ap, net, template))
        made[template + " (vault)"] = _shape(_content(ap, net, _diagram(ap, net, template, _vault_only(ap, net))))
    return {"names": names, "diagrams": made}


@case("nd.AddDiagramTemplate")
def _(ap, I, O):
    net = _net(ap, I)
    result = ap.nd.AddDiagramTemplate(net["un"], "Added")
    return {"names": _names(ap, net), "outputs": [str(result[i]).replace(net["un"], "<un>")
                                                  for i in range(result.outputCount)]}


@case("nd.AlterDiagramTemplate")
def _(ap, I, O):
    net = _net(ap, I)
    _template(ap, net, "Old")
    ap.nd.AlterDiagramTemplate(net["un"], "Old", "Renamed", "NOT_DEFAULT_TEMPLATE", None, None, None, None,
                               "ENABLE_DIAGRAM_EXTENSION", "a description")
    names = _names(ap, net)
    # ExtendDiagram, which would read the extension flag, fails in headless Pro (ERROR 999999 every way it was asked)
    diagram = _diagram(ap, net, "Renamed", [_subset(ap, net, "device", "NAME = 'S'")])
    return {"names": names, "diagram": _shape(_content(ap, net, diagram))}


@case("nd.DeleteDiagramTemplate")
def _(ap, I, O):
    net = _net(ap, I)
    _template(ap, net, "Gone")
    diagram = _diagram(ap, net, "Gone")
    ap.nd.DeleteDiagramTemplate(net["un"], "Gone")
    try:
        _export(ap, net, diagram)
        left = True
    except Exception:  # noqa: BLE001 — the diagram went with its template
        left = False
    return {"names": _names(ap, net), "diagramLeft": left}


@case("nd.ExportDiagramTemplateDefinitions")
def _(ap, I, O):
    net = _net(ap, I)
    _template(ap, net, "Exported", [("AddReduceJunctionRule", ["ACTIVE", "INCLUDE_SOURCE_CLASSES", [net["device"]]])])
    out = os.path.join(net["folder"], "exported.ndbd")
    if os.path.exists(out):
        os.remove(out)
    ap.nd.ExportDiagramTemplateDefinitions(net["un"], "Exported", out)
    return {"written": os.path.isfile(out) and os.path.getsize(out) > 0}


@case("nd.ImportDiagramTemplateDefinitions")
def _(ap, I, O):
    net = _net(ap, I)
    _template(ap, net, "Source", [("AddReduceJunctionRule", ["ACTIVE", "INCLUDE_SOURCE_CLASSES", [net["device"]]])])
    out = os.path.join(net["folder"], "imported.ndbd")
    if os.path.exists(out):
        os.remove(out)
    ap.nd.ExportDiagramTemplateDefinitions(net["un"], "Source", out)
    _template(ap, net, "Target")
    ap.nd.ImportDiagramTemplateDefinitions(net["un"], "Target", out)
    return _shape(_content(ap, net, _diagram(ap, net, "Target")))


# ---- rules ----------------------------------------------------------------------------------------------------------

def _rule_case(name, rules, features=None, keep_positions=False):
    def run(ap, I, O):
        net = _net(ap, I)
        made = rules(net) if callable(rules) else rules
        given = features(ap, net) if features else None
        if keep_positions:
            template = _template(ap, net, name.split(".")[1], made)
            return _content(ap, net, _diagram(ap, net, template, given))
        return _rules_case(ap, I, name.split(".")[1], made, given)
    CASES[name] = run


_rule_case("nd.AddReduceJunctionRule",
           lambda n: [("AddReduceJunctionRule", ["ACTIVE", "INCLUDE_SOURCE_CLASSES", [n["device"]]])])
_rule_case("nd.AddReduceJunctionByAttributeRule",
           lambda n: [("AddReduceJunctionByAttributeRule", ["ACTIVE", n["device"], "NAME = 'V1'"])])
_rule_case("nd.AddReduceJunctionByCategoryRule",
           [("AddReduceJunctionByCategoryRule", ["ACTIVE", "INCLUDE_CATEGORIES", ["Isolating"]])])
_rule_case("nd.AddReduceEdgeByAttributeRule",
           lambda n: [("AddReduceEdgeByAttributeRule", ["ACTIVE", n["line"], "NAME = 'L4'"])])
_rule_case("nd.AddRemoveFeatureRule",
           lambda n: [("AddRemoveFeatureRule", ["ACTIVE", "JUNCTIONS", "INCLUDE_SOURCE_CLASSES", [n["junction"]]])])
_rule_case("nd.AddRemoveFeatureByAttributeRule",
           lambda n: [("AddRemoveFeatureByAttributeRule", ["ACTIVE", n["line"], "NAME = 'L4'"])])
_rule_case("nd.AddRemoveFeatureByCategoryRule",
           [("AddRemoveFeatureByCategoryRule", ["ACTIVE", "JUNCTIONS", "INCLUDE_CATEGORIES", ["Isolating"]])])
_rule_case("nd.AddCollapseContainerRule", [("AddCollapseContainerRule", ["ACTIVE"])])
_rule_case("nd.AddCollapseContainerByAttributeRule",
           lambda n: [("AddCollapseContainerByAttributeRule", ["ACTIVE", n["vault"], "NAME = 'VA'"])])
# Pro requires a category (ERROR 000735 without one): every container but those of Isolating
_rule_case("nd.AddCollapseContainerByCategoryRule",
           [("AddCollapseContainerByCategoryRule", ["ACTIVE", "BOTH", "EXCLUDE_CATEGORIES", ["Isolating"]])])


def _vault_only(ap, net):
    return [_subset(ap, net, "vault", "NAME = 'VA'")]


_rule_case("nd.AddExpandContainerRule", [("AddExpandContainerRule", ["ACTIVE"])], _vault_only)
_rule_case("nd.AddExpandContainerByAttributeRule",
           lambda n: [("AddExpandContainerByAttributeRule", ["ACTIVE", "KEEP_VISIBLE", n["vault"], "NAME = 'VA'"])],
           _vault_only)
_rule_case("nd.AddExpandContainerByCategoryRule",
           [("AddExpandContainerByCategoryRule", ["ACTIVE", "KEEP_VISIBLE", "BOTH", "EXCLUDE_CATEGORIES", ["Isolating"]])],
           _vault_only)
_rule_case("nd.AddConnectivityAssociationsRule", [("AddConnectivityAssociationsRule", ["ACTIVE"])])
_rule_case("nd.AddStructuralAttachmentsRule", [("AddStructuralAttachmentsRule", ["ACTIVE"])])
_rule_case("nd.AddStartIterationRule",
           lambda n: [("AddStartIterationRule", ["ACTIVE"]),
                      ("AddReduceJunctionRule", ["ACTIVE", "INCLUDE_SOURCE_CLASSES", [n["device"]]]),
                      ("AddStopIterationRule", ["ACTIVE"])])
_rule_case("nd.AddStopIterationRule",
           lambda n: [("AddStartIterationRule", ["ACTIVE"]),
                      ("AddReduceJunctionRule", ["ACTIVE", "INCLUDE_SOURCE_CLASSES", [n["junction"]]]),
                      ("AddStopIterationRule", ["ACTIVE"])])
_rule_case("nd.AddDiagramFeatureCapabilityByAttributeRule",
           lambda n: [("AddDiagramFeatureCapabilityByAttributeRule",
                       ["ACTIVE", n["device"], "NAME = 'V1'", "PREVENT_TO_REDUCE_JUNCTION"]),
                      ("AddReduceJunctionRule", ["ACTIVE", "INCLUDE_SOURCE_CLASSES", [n["device"]]])])
_rule_case("nd.AddDiagramFeatureCapabilityByCategoryRule",
           lambda n: [("AddDiagramFeatureCapabilityByCategoryRule",
                       ["ACTIVE", "INCLUDE_CATEGORIES", ["Isolating"], "PREVENT_TO_REDUCE_JUNCTION"]),
                      ("AddReduceJunctionRule", ["ACTIVE", "INCLUDE_SOURCE_CLASSES", [n["device"]]])])
_rule_case("nd.AddSpatialQueryRule",
           lambda n: [("AddSpatialQueryRule", ["ACTIVE", n["device"], "WITHIN_A_DISTANCE", n["junction"],
                                               "15 Meters"])],
           lambda ap, n: [n["junction"]])
_rule_case("nd.AddTraceRule", [("AddTraceRule", ["ACTIVE", "CONNECTED"])],
           lambda ap, n: [_subset(ap, n, "device", "NAME = 'S'")])
_rule_case("nd.AddSetStartingPointByAttributeRule",
           lambda n: [("AddSetStartingPointByAttributeRule", ["ACTIVE", n["device"], "NAME = 'V2'"]),
                      ("AddTraceRule", ["ACTIVE", "UPSTREAM", "Water", "Pressure"])])


# ---- layouts --------------------------------------------------------------------------------------------------------
# Each in a process of its own (LAYOUT_CASES). A tree layout needs a root: S, set by AddSetRootJunctionByAttributeRule.

_TREES = ("SmartTree", "MainlineTree", "RadialTree")


def _layout_case(name, add=None, apply=None, args=None):
    def run(ap, I, O):
        net = _net(ap, I)
        short = name.split(".")[1]
        rules = [_root(net)] if any(t in (add or apply or "") for t in _TREES) or short == \
            "AddSetRootJunctionByAttributeRule" else []
        features = None
        if "PartialOverlapping" in name or "MainRing" in name:
            # the association T–V1 drawn as an edge lies on L2: something to spread, and a ring
            rules.append(("AddConnectivityAssociationsRule", ["ACTIVE"]))
        if "MainRing" in name:
            # Pro refuses a main ring on disjoint graphs (ERROR 002637): no pole
            features = [net["device"], net["line"], net["junction"]]
        if add:
            rules.append((add, ["ACTIVE", *(args or [])]))
        diagram = _diagram(ap, net, _template(ap, net, short, rules), features)
        if apply:
            getattr(ap.nd, apply)(_layer(ap, net, diagram), *(args or []))
        return _content(ap, net, diagram)
    CASES[name] = run
    LAYOUT_CASES.append(name)


for _kind in ("AngleDirected", "Compression", "ForceDirected", "Grid", "LinearDispatch", "MainRing", "MainlineTree",
              "PartialOverlappingEdges", "RadialTree", "ReshapeDiagramEdges", "SmartTree", "SpatialDispatch"):
    _layout_case(f"nd.Add{_kind}Layout", add=f"Add{_kind}Layout")
for _kind in ("AngleDirected", "Compression", "ForceDirected", "GeoPositions", "Grid", "LinearDispatch", "MainRing",
              "MainlineTree", "PartialOverlappingEdges", "RadialTree", "SmartTree", "SpatialDispatch"):
    _layout_case(f"nd.Apply{_kind}Layout", apply=f"Apply{_kind}Layout")
_layout_case("nd.ReshapeDiagramEdgesLayout", apply="ReshapeDiagramEdgesLayout")
# the root rule seen through the layout that reads it: a smart tree from top to bottom in absolute units, which Pro and
# grapy drew to the coordinate in ADR-232's measurement
_layout_case("nd.AddSetRootJunctionByAttributeRule", apply="ApplySmartTreeLayout",
             args=[None, "FROM_TOP_TO_BOTTOM", "ABSOLUTE_UNIT", "5 Meters", None, "4 Meters"])


@case("nd.ApplyTemplateLayouts", layout=True)
def _(ap, I, O):
    net = _net(ap, I)
    template = _template(ap, net, "TemplateLayouts")
    diagram = _diagram(ap, net, template)
    ap.nd.AddGridLayout(net["un"], template, "ACTIVE")         # added after the diagram, so only this run applies it
    ap.nd.ApplyTemplateLayouts(_layer(ap, net, diagram))
    return _content(ap, net, diagram)


@case("nd.AddDiagramFeatureSelectionToLayout", layout=True)
def _(ap, I, O):
    net = _net(ap, I)
    template = _template(ap, net, "Selection", [
        ("AddDiagramFeatureSelectionToLayout", ["ACTIVE", "SELECT_BY_ATTRIBUTES", None, None, net["junction"],
                                                "NAME = 'E'"]),
        ("AddGridLayout", ["ACTIVE"])])
    return _content(ap, net, _diagram(ap, net, template))


# ---- diagrams -------------------------------------------------------------------------------------------------------

@case("nd.CreateDiagram")
def _(ap, I, O):
    net = _net(ap, I)
    template = _template(ap, net, "Created")
    result = ap.nd.CreateDiagram(net["un"], template, _all(net))
    found = _content(ap, net, str(result[1]))
    found["outputCount"] = result.outputCount
    return found


@case("nd.ExportDiagramContent")
def _(ap, I, O):
    net = _net(ap, I)
    doc = _export(ap, net, _diagram(ap, net, _template(ap, net, "Exporting")))

    def keys(items):
        return sorted({k for item in items for k in item})

    return {"keys": sorted(doc), "junctionKeys": keys(doc.get("junctions", [])), "edgeKeys": keys(doc.get("edges", [])),
            "containerKeys": keys(doc.get("containers", [])),
            "diagramInfoKeys": sorted(doc.get("diagramInfo") or {}),
            "attributesOfS": sorted(next((j.get("attributes") or {} for j in doc.get("junctions", [])
                                          if str(j.get("assocGlobalID")).upper() == net["ids"]["S"]), {})),
            "sourceMapping": sorted(doc.get("sourceMapping") or {}), "spatialRef": doc.get("spatialRef"),
            "aggregationKeys": keys(a for e in doc.get("edges", []) for a in e.get("aggregations", []) or [])}


@case("nd.MakeDiagramLayer")
def _(ap, I, O):
    net = _net(ap, I)
    diagram = _diagram(ap, net, _template(ap, net, "Layered"))
    layer = ap.nd.MakeDiagramLayer(net["un"], diagram, "nd_layer")[0]
    desc = ap.Describe(layer)
    out = {"name": getattr(layer, "name", str(layer)), "dataType": getattr(desc, "dataType", None),
           "describedName": getattr(desc, "name", None)}
    for key in ("diagramTemplateName", "diagram", "network", "templateName", "isStored"):
        try:
            value = getattr(desc, key)
        except Exception:  # noqa: BLE001 — a property one side does not have
            continue
        out[key] = value if isinstance(value, (str, int, float, bool, type(None))) else type(value).__name__
    return out


def _info(ap, net, diagram):
    doc = _export(ap, net, diagram)
    info = doc.get("diagramInfo") or {}
    return {k: info.get(k) for k in ("template", "tag", "junctionCount", "edgeCount", "containerCount")}


# Pro stores one diagram a process: a later StoreDiagram answered ERROR 001771 "Failed to store diagram" (measured
# 2026-11-12), so the cases that store run alone too
for _name in ("nd.StoreDiagram", "nd.AlterDiagramProperties", "nd.DeleteDiagram", "nd.ChangeDiagramsOwner",
              "nd.PurgeTemporaryDiagrams"):
    LAYOUT_CASES.append(_name)


@case("nd.StoreDiagram")
def _(ap, I, O):
    net = _net(ap, I)
    diagram = _diagram(ap, net, _template(ap, net, "Storing"))
    ap.nd.StoreDiagram(_layer(ap, net, diagram), "Stored", "PUBLIC", "a#b")
    return _info(ap, net, "Stored")


@case("nd.AlterDiagramProperties")
def _(ap, I, O):
    net = _net(ap, I)
    diagram = _diagram(ap, net, _template(ap, net, "Altering"))
    layer = _layer(ap, net, diagram)
    ap.nd.StoreDiagram(layer, "ToAlter", "PUBLIC", "a")
    ap.nd.AlterDiagramProperties(_layer(ap, net, "ToAlter"), "Altered", "PUBLIC", "c#d")
    return _info(ap, net, "Altered")


@case("nd.DeleteDiagram")
def _(ap, I, O):
    net = _net(ap, I)
    diagram = _diagram(ap, net, _template(ap, net, "Deleting"))
    ap.nd.StoreDiagram(_layer(ap, net, diagram), "ToDelete")
    ap.nd.DeleteDiagram(net["un"], None, ["ToDelete"])
    try:
        _export(ap, net, "ToDelete")
        return {"left": True}
    except Exception:  # noqa: BLE001
        return {"left": False}


@case("nd.ChangeDiagramsOwner")
def _(ap, I, O):
    net = _net(ap, I)
    diagram = _diagram(ap, net, _template(ap, net, "Owning"))
    ap.nd.StoreDiagram(_layer(ap, net, diagram), "Owned")
    creator = (_export(ap, net, "Owned").get("diagramInfo") or {}).get("creator")
    ap.nd.ChangeDiagramsOwner(net["un"], creator, None, ["Owned"])
    return {"creatorGiven": bool(creator)}


@case("nd.PurgeTemporaryDiagrams")
def _(ap, I, O):
    net = _net(ap, I)
    template = _template(ap, net, "Purging")
    temporary = _diagram(ap, net, template)
    stored = _diagram(ap, net, template)
    ap.nd.StoreDiagram(_layer(ap, net, stored), "Kept")
    ap.nd.PurgeTemporaryDiagrams(net["un"])
    left = {}
    for label, name in (("temporary", temporary), ("stored", "Kept")):
        try:
            _export(ap, net, name)
            left[label] = True
        except Exception:  # noqa: BLE001
            left[label] = False
    return left


@case("nd.UpdateDiagram")
def _(ap, I, O):
    net = _net(ap, I)
    template = _template(ap, net, "Updating")
    diagram = _diagram(ap, net, template)
    ap.nd.AddReduceJunctionRule(net["un"], template, "ACTIVE", "INCLUDE_SOURCE_CLASSES", [net["device"]])
    ap.nd.UpdateDiagram(net["un"], [template], [diagram], "ALL_SELECTED_DIAGRAMS")
    return _shape(_content(ap, net, diagram))


@case("nd.ExtendDiagram")
def _(ap, I, O):
    net = _net(ap, I)
    template = _template(ap, net, "Extending")
    ap.nd.AlterDiagramTemplate(net["un"], template, is_diagram_extension_enabled="ENABLE_DIAGRAM_EXTENSION")
    diagram = _diagram(ap, net, template, [_subset(ap, net, "line", "NAME = 'L2'")])
    before = _shape(_content(ap, net, diagram))
    ap.nd.ExtendDiagram(_layer(ap, net, diagram), extension_type="BY_CONNECTIVITY")
    return {"before": before, "after": _content(ap, net, diagram)}


def _selected(ap, net, table, where):
    layer = ap.management.MakeFeatureLayer(net[table], f"nd_sel_{table}")[0]
    ap.management.SelectLayerByAttribute(layer, "NEW_SELECTION", where)
    return layer


@case("nd.AppendToDiagram")
def _(ap, I, O):
    net = _net(ap, I)
    diagram = _diagram(ap, net, _template(ap, net, "Appending"), [_subset(ap, net, "line", "NAME = 'L1'")])
    _selected(ap, net, "line", "NAME = 'L4'")
    ap.nd.AppendToDiagram(_layer(ap, net, diagram))
    return _shape(_content(ap, net, diagram))


@case("nd.OverwriteDiagram")
def _(ap, I, O):
    net = _net(ap, I)
    diagram = _diagram(ap, net, _template(ap, net, "Overwriting"), [_subset(ap, net, "line", "NAME = 'L1'")])
    _selected(ap, net, "line", "NAME = 'L4'")
    ap.nd.OverwriteDiagram(_layer(ap, net, diagram))
    return _shape(_content(ap, net, diagram))
