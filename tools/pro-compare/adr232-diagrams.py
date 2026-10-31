"""ADR-232 condition 5: network diagram rules and layouts on ADR-232 §4's water network (test_network_diagrams.py), in
ArcGIS Pro 3.7 (file geodatabase) and in grapy (GeoPackage).

    "C:/Program Files/ArcGIS/Pro/bin/Python/envs/arcgispro-py3/python.exe" adr232_diagrams.py pro   <folder>
    D:/temp/grapy-env/Scripts/python.exe                                   adr232_diagrams.py grapy <folder> <grapy src>

Every diagram is read through ExportDiagramContent's JSON; junctions, containers and edges are named back through
their associated global IDs. Prints one JSON document {case: content or error}. A tool, not a test: it needs Pro.

Pro applies only the first layout a Python process runs on a file geodatabase's diagrams — later ones succeed and move
nothing (measured 2026-10-31) — so run one layout case per process: ADR232_CASES=radial_tree (a comma list; all by
default). ADR232_SCALE and ADR232_SCALE_Y stretch the network (x, y), ADR232_SUBSET=1 or 2 makes the diagram of a
smaller tree (S, V1, T, E three levels deep, or S, V1, T two), for the relative units of D-338.
"""
import csv
import json
import os
import shutil
import sys

side, folder = sys.argv[1], sys.argv[2]
if side == "grapy":
    sys.path.insert(0, sys.argv[3])
    import grapy as arcpy
else:
    import arcpy

SCALE = float(os.environ.get("ADR232_SCALE", "1"))
SCALE_Y = float(os.environ.get("ADR232_SCALE_Y", os.environ.get("ADR232_SCALE", "1")))
work = os.path.join(folder, f"diagrams_{side}_x{SCALE:g}_y{SCALE_Y:g}_{os.environ.get('ADR232_CASES', 'all').replace(',', '-')}{'_sub' + os.environ.get('ADR232_SUBSET') if os.environ.get('ADR232_SUBSET') else ''}")
if os.path.exists(work):
    shutil.rmtree(work)
os.makedirs(work)
arcpy.env.overwriteOutput = True
SR = arcpy.SpatialReference(3857)
gdb = arcpy.management.CreateFileGDB(work, "water")[0]
fds = arcpy.management.CreateFeatureDataset(gdb, "utility", SR)[0]
territory = arcpy.management.CreateFeatureclass(fds, "Territory", "POLYGON", has_z="ENABLED", has_m="ENABLED",
                                                spatial_reference=SR)[0]
with arcpy.da.InsertCursor(territory, ["SHAPE@WKT"]) as rows:
    rows.insertRow(["POLYGON Z ((-1000 -1000 0, 10000 -1000 0, 10000 10000 0, -1000 10000 0, -1000 -1000 0))"])
un = arcpy.un.CreateUtilityNetwork(fds, "Water", territory)[0]
arcpy.un.AddDomainNetwork(un, "Water", "PARTITIONED", "SOURCE")
arcpy.un.AddTier(un, "Water", "Pressure", 1, "RADIAL")
device, line, junction, vault = (f"{fds}/WaterDevice", f"{fds}/WaterLine", f"{fds}/WaterJunction",
                                 f"{fds}/StructureJunction")
for table, groups, standard in ((device, [(1, "Source"), (2, "Valve")], "Standard"), (line, [(1, "Main")], "Ductile"),
                                (junction, [(1, "Tee")], "Standard"), (vault, [(1, "Vault")], "Concrete")):
    for code, text in groups:
        # Pro wants a domain of its own per asset group (ERROR 002580), holding the ASSETTYPE default 0 (002943).
        domain = f"{os.path.basename(table)}_{text}_Types"
        arcpy.management.CreateDomain(gdb, domain, domain, "SHORT", "CODED")
        arcpy.management.AddCodedValueToDomain(gdb, domain, 0, "Unknown")
        arcpy.management.AddCodedValueToDomain(gdb, domain, 1, standard)
        arcpy.management.AddSubtype(table, code, text)
        arcpy.management.AssignDomainToField(table, "ASSETTYPE", domain, code)
for table in (device, line, junction, vault):
    arcpy.management.AddField(table, "NAME", "TEXT")
arcpy.un.AddTerminalConfiguration(un, "Source-Load", "DIRECTIONAL", "Source true;Load false")
arcpy.un.SetTerminalConfiguration(un, "Water", "WaterDevice", "Source", "Standard", "Source-Load")
arcpy.un.SetNetworkCategory(un, "Water", "WaterDevice", "Source", "Standard", "Subnetwork Controller")
arcpy.un.AddNetworkCategory(un, "Isolating")
arcpy.un.SetNetworkCategory(un, "Water", "WaterDevice", "Valve", "Standard", ["Isolating"])
je = "JUNCTION_EDGE_CONNECTIVITY"
arcpy.un.AddRule(un, je, "WaterDevice", "Source", "Standard", "WaterLine", "Main", "Ductile", "Load")
arcpy.un.AddRule(un, je, "WaterDevice", "Valve", "Standard", "WaterLine", "Main", "Ductile")
arcpy.un.AddRule(un, je, "WaterJunction", "Tee", "Standard", "WaterLine", "Main", "Ductile")
arcpy.un.AddRule(un, "JUNCTION_JUNCTION_CONNECTIVITY", "WaterJunction", "Tee", "Standard", "WaterDevice", "Valve",
                 "Standard")
# Pro refuses a containment rule on an asset type with no container role (ERROR 002908).
arcpy.un.SetAssociationRole(un, "Structure", "StructureJunction", "Vault", "Concrete", "CONTAINER", "RESTRICTED")
arcpy.un.AddRule(un, "CONTAINMENT", "StructureJunction", "Vault", "Concrete", "WaterDevice", "Valve", "Standard")
arcpy.un.SetSubnetworkDefinition(un, "Water", "Pressure", None, ["Source/Standard", "Valve/Standard"],
                                 ["Source/Standard"], ["Main/Ductile"], valid_junctions=["Tee/Standard"])
ids = {}


def scaled(wkt):
    import re
    return re.sub(r"(-?[0-9.]+) (-?[0-9.]+) (-?[0-9.]+)",
                  lambda m: f"{float(m.group(1)) * SCALE} {float(m.group(2)) * SCALE_Y} {m.group(3)}", wkt)


def insert(table, fields, rows):
    with arcpy.da.Editor(gdb), arcpy.da.InsertCursor(table, fields) as written:
        for row in rows:
            written.insertRow(row[:-1] + (scaled(row[-1]),))
    with arcpy.da.SearchCursor(table, ["NAME", "GLOBALID@"]) as read:
        ids.update({n: g.upper() for n, g in read})


insert(device, ["NAME", "ASSETGROUP", "ASSETTYPE", "SHAPE@WKT"],
       [("S", 1, 1, "POINT Z (0 0 0)"), ("V1", 2, 1, "POINT Z (6 0 0)"), ("V2", 2, 1, "POINT Z (30 0 0)")])
insert(junction, ["NAME", "ASSETGROUP", "ASSETTYPE", "SHAPE@WKT"],
       [("T", 1, 1, "POINT Z (20 0 0)"), ("E", 1, 1, "POINT Z (20 10 0)")])
insert(line, ["NAME", "ASSETGROUP", "ASSETTYPE", "SHAPE@WKT"], [
    ("L1", 1, 1, "LINESTRING Z (0 0 0, 6 0 0)"), ("L2", 1, 1, "LINESTRING Z (6 0 0, 13 0 0, 20 0 0)"),
    ("L3", 1, 1, "LINESTRING Z (20 0 0, 30 0 0)"), ("L4", 1, 1, "LINESTRING Z (20 0 0, 20 10 0)")])
insert(vault, ["NAME", "ASSETGROUP", "ASSETTYPE", "SHAPE@WKT"], [("VA", 1, 1, "POINT Z (30 0 0)")])
names = {g.strip("{}"): n for n, g in ids.items()}
controllers = os.path.join(work, "controllers.csv")
with open(controllers, "w", newline="", encoding="utf-8") as written:
    out = csv.writer(written)
    out.writerow(["SUBNETWORKCONTROLLERNAME", "FEATUREGLOBALID", "FEATURECLASSNAME", "FEATUREASSETGROUP",
                  "FEATUREASSETTYPE", "FEATURETERMINAL", "TIERNAME", "SUBNETWORKNAME", "DESCRIPTION", "NOTES"])
    out.writerow(["Plant", ids["S"], "WaterDevice", "Source", "Standard", "Load", "Pressure", "Zone1", "", ""])
arcpy.un.EnableNetworkTopology(un)
arcpy.un.ImportSubnetworkControllers(un, controllers)
with arcpy.da.Editor(gdb):
    arcpy.un.UtilityNetwork(un).AddContainmentAssociation("StructureJunction", ids["VA"], "WaterDevice", ids["V2"],
                                                          False) if side == "grapy" else None
if side == "pro":
    associations = os.path.join(work, "associations.csv")
    with open(associations, "w", newline="", encoding="utf-8") as written:
        out = csv.writer(written)
        out.writerow(["ASSOCIATIONTYPE", "FROMFEATURECLASS", "FROMASSETGROUP", "FROMASSETTYPE", "FROMGLOBALID",
                      "FROMTERMINAL", "TOFEATURECLASS", "TOASSETGROUP", "TOASSETTYPE", "TOGLOBALID", "TOTERMINAL",
                      "ISCONTENTVISIBLE"])
        out.writerow(["Containment", "StructureJunction", "Vault", "Concrete", ids["VA"], "", "WaterDevice", "Valve",
                      "Standard", ids["V2"], "", "FALSE"])
    arcpy.un.ImportAssociations(un, "CONTAINMENT", associations)
arcpy.un.ValidateNetworkTopology(un)
everything = [device, line, junction, vault]
count = [0]


def content(diagram):
    count[0] += 1
    out = os.path.join(work, f"content{count[0]}.json")
    arcpy.nd.ExportDiagramContent(un, diagram, out, "INCLUDE_DIAGRAM_PROPERTIES", "INCLUDE_GEOMETRIES",
                                  "INCLUDE_ATTRIBUTES", "INCLUDE_AGGREGATIONS", "USE_CODED_VALUE_NAMES")
    doc = json.load(open(out, encoding="utf-8"))
    label = {}
    found = {"J": {}, "C": {}, "E": []}
    for j in doc.get("junctions", []):
        name = names.get(str(j.get("assocGlobalID", "")).strip("{}").upper(), f"#{j.get('id')}")
        label[j.get("id")] = name
        g = j.get("geometry") or {}
        found["J"][name] = (round(g.get("x", 0), 3), round(g.get("y", 0), 3))
    for c in doc.get("containers", []):
        name = names.get(str(c.get("assocGlobalID", "")).strip("{}").upper(), f"#{c.get('id')}")
        label[c.get("id")] = name
        g = c.get("geometry") or {}
        xs = [p[0] for ring in g.get("rings", []) for p in ring]
        ys = [p[1] for ring in g.get("rings", []) for p in ring]
        found["C"][name] = [round(min(xs), 3), round(min(ys), 3), round(max(xs), 3), round(max(ys), 3)] if xs else g
    for e in doc.get("edges", []):
        name = "R" if e.get("aggregations") else names.get(str(e.get("assocGlobalID", "")).strip("{}").upper(), "?")
        agg = sorted(names.get(str(a.get("globalID", "")).strip("{}").upper(), "?") for a in e.get("aggregations", []))
        found["E"].append([name, label.get(e.get("fromID")), label.get(e.get("toID")), agg])
    found["E"].sort(key=str)
    found["info"] = {k: doc.get("diagramInfo", {}).get(k) for k in ("junctionCount", "edgeCount", "containerCount")}
    found["keys"] = sorted(doc)
    return found


def case(template, rules, layout=None, **layout_args):
    template = f"{template}{len(report)}"
    try:
        arcpy.nd.AddDiagramTemplate(un, template)
        for rule, args in rules:
            getattr(arcpy.nd, rule)(un, template, *args)
        features = everything
        if os.environ.get("ADR232_SUBSET"):
            # a smaller diagram: S, V1, T and E with L1, L2 and L4 (no L3, V2 or the vault)
            keep = {device: "NAME IN ('S', 'V1')", junction: "NAME IN ('T', 'E')", line: "NAME IN ('L1', 'L2', 'L4')"}
            if os.environ.get("ADR232_SUBSET") == "2":
                # smaller still: S, V1 and T with L1 and L2, a tree two deep
                keep = {device: "NAME IN ('S', 'V1')", junction: "NAME = 'T'", line: "NAME IN ('L1', 'L2')"}
            features = [arcpy.management.MakeFeatureLayer(t, f"sub{count[0]}_{i}", w)[0]
                        for i, (t, w) in enumerate(keep.items())]
        diagram = arcpy.nd.CreateDiagram(un, template, features)[1]
        if layout:
            # Pro lays out a diagram layer (ERROR 000732 on a diagram name).
            count[0] += 1
            layer = arcpy.nd.MakeDiagramLayer(un, diagram, f"view{count[0]}")[0]
            getattr(arcpy.nd, layout)(layer, **layout_args)
        return content(diagram)
    except Exception as error:  # noqa: BLE001 — the message is the observation
        return {"error": str(error).strip().splitlines()[:3]}


root = ("AddSetRootJunctionByAttributeRule", ["ACTIVE", device, "NAME = 'S'"])
report = {"side": side, "ids": ids}
SYNC = {}
CASES = {
    "basic": lambda: case("Plain", []),
    "reduce_devices": lambda: case("Red", [("AddReduceJunctionRule", ["ACTIVE", "INCLUDE_SOURCE_CLASSES", [device]])]),
    "reduce_devices_1jct": lambda: case("Red1", [("AddReduceJunctionRule", ["ACTIVE", "INCLUDE_SOURCE_CLASSES", [device],
                                                                             None, None, "REDUCE_JCT_TO_1JCT"])]),
    "smart_tree": lambda: case("Smart", [root], "ApplySmartTreeLayout"),
    "smart_tree_top": lambda: case("SmartTop", [root], "ApplySmartTreeLayout", tree_direction="FROM_TOP_TO_BOTTOM",
                                   is_unit_absolute="ABSOLUTE_UNIT", along_absolute="5 Meters",
                                   perpendicular_absolute="4 Meters"),
    "smart_tree_down": lambda: case("SmartDown", [root], "ApplySmartTreeLayout", tree_direction="FROM_TOP_TO_BOTTOM"),
    "radial_tree": lambda: case("Radial", [root], "ApplyRadialTreeLayout"),
    "mainline_tree": lambda: case("Mainline", [root], "ApplyMainlineTreeLayout"),
    "grid": lambda: case("Grid", [], "ApplyGridLayout"),
    "linear_dispatch": lambda: case("Linear", [], "ApplyLinearDispatchLayout"),
    "geo": lambda: case("Geo", [root], "ApplyGeoPositionsLayout"),
}
for k, name in enumerate((os.environ.get("ADR232_CASES") or ",".join(CASES)).split(",")):
    report[name if name not in report else f"{name}#{k}"] = CASES[name]()
print(json.dumps(report, indent=1, default=str))
