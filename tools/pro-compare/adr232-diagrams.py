"""ADR-232 condition 5: network diagram rules and layouts on ADR-232 §4's water network (test_network_diagrams.py), in
ArcGIS Pro 3.7 (file geodatabase) and in grapy (GeoPackage).

    "C:/Program Files/ArcGIS/Pro/bin/Python/envs/arcgispro-py3/python.exe" adr232_diagrams.py pro   <folder>
    D:/temp/grapy-env/Scripts/python.exe                                   adr232_diagrams.py grapy <folder> <grapy src>

Every diagram is read through ExportDiagramContent's JSON; junctions, containers and edges are named back through
their associated global IDs. Prints one JSON document {case: content or error}. A tool, not a test: it needs Pro.
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

work = os.path.join(folder, f"diagrams_{side}")
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
    rows.insertRow(["POLYGON Z ((-10 -10 0, 100 -10 0, 100 100 0, -10 100 0, -10 -10 0))"])
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


def insert(table, fields, rows):
    with arcpy.da.Editor(gdb), arcpy.da.InsertCursor(table, fields) as written:
        for row in rows:
            written.insertRow(row)
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
    try:
        arcpy.nd.AddDiagramTemplate(un, template)
        for rule, args in rules:
            getattr(arcpy.nd, rule)(un, template, *args)
        diagram = arcpy.nd.CreateDiagram(un, template, everything)[1]
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
report["basic"] = case("Plain", [])  # Pro's network already has a "Basic"
report["reduce_devices"] = case("Red", [("AddReduceJunctionRule", ["ACTIVE", "INCLUDE_SOURCE_CLASSES", [device]])])
report["reduce_devices_1jct"] = case("Red1", [("AddReduceJunctionRule", ["ACTIVE", "INCLUDE_SOURCE_CLASSES", [device],
                                                                          None, None, "REDUCE_JCT_TO_1JCT"])])
report["smart_tree"] = case("Smart", [root], "ApplySmartTreeLayout")
report["smart_tree_top"] = case("SmartTop", [root], "ApplySmartTreeLayout", tree_direction="FROM_TOP_TO_BOTTOM",
                                is_unit_absolute="ABSOLUTE_UNIT", along_absolute="5 Meters",
                                perpendicular_absolute="4 Meters")
report["radial_tree"] = case("Radial", [root], "ApplyRadialTreeLayout")
report["mainline_tree"] = case("Mainline", [root], "ApplyMainlineTreeLayout")
report["grid"] = case("Grid", [], "ApplyGridLayout")
report["linear_dispatch"] = case("Linear", [], "ApplyLinearDispatchLayout")
report["geo"] = case("Geo", [root], "ApplyGeoPositionsLayout")
print(json.dumps(report, indent=1, default=str))
