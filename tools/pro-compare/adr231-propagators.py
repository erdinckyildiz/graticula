# A comparison with ArcGIS Pro 3.7 (ADR-231 condition 5, D-338, 2026-10-31). A tool, not a test: it needs Pro and is not run in CI.
"""ADR-231 condition 5, propagators with the attribute on every class (D-338): function barriers, propagators and path traces on the two-controller mesh of
test_utility_network.MeshNetwork, in ArcGIS Pro 3.7 (file geodatabase) and in grapy (GeoPackage).

    "C:/Program Files/ArcGIS/Pro/bin/Python/envs/arcgispro-py3/python.exe" adr231_mesh.py pro   <folder>
    D:/temp/grapy-env/Scripts/python.exe                                   adr231_mesh.py grapy <folder> <grapy src>

ADR231_PRESSURE="VB,A3b,CV,D1" sets four of the PRESSURE values (4,4,2,2 by default): 1,1,2,2 and 1,1,1,1 are the
runs that pinned the propagation rule (2026-10-31), compared with adr231-compare.py.

Each trace asks for the ELEMENTS result in a JSON file (and FUNCTIONS where functions are given); the elements are
mapped back to their NAME through the global IDs. Prints one JSON document {trace: [names] or error}. A tool, not a
test: it needs Pro.
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

# ADR231_PRESSURE="VB,A3b,CV,D1": the PRESSURE of the closed valve, the line beside it, the check valve and the line
# before it — "4,4,2,2" by default, the values of the first run.
P_VB, P_A3B, P_CV, P_D1 = (int(v) for v in os.environ.get("ADR231_PRESSURE", "4,4,2,2").split(","))
work = os.path.join(folder, f"mesh_{side}_{os.environ.get('ADR231_PRESSURE', '4,4,2,2').replace(',', '')}")
if os.path.exists(work):
    shutil.rmtree(work)
os.makedirs(work)
arcpy.env.overwriteOutput = True
SR = arcpy.SpatialReference(3857)

gdb = arcpy.management.CreateFileGDB(work, "mesh")[0]
fds = arcpy.management.CreateFeatureDataset(gdb, "utility", SR)[0]
territory = arcpy.management.CreateFeatureclass(fds, "Territory", "POLYGON", has_z="ENABLED", has_m="ENABLED",
                                                spatial_reference=SR)[0]
with arcpy.da.InsertCursor(territory, ["SHAPE@WKT"]) as rows:
    rows.insertRow(["POLYGON Z ((-100 -100 0, 200 -100 0, 200 200 0, -100 200 0, -100 -100 0))"])
un = arcpy.un.CreateUtilityNetwork(fds, "Water", territory)[0]
arcpy.un.AddDomainNetwork(un, "Water", "PARTITIONED", "SOURCE", "Water Distribution")
arcpy.un.AddTier(un, "Water", "Pressure", 1, "MESH")
device, line, junction = f"{fds}/WaterDevice", f"{fds}/WaterLine", f"{fds}/WaterJunction"
for table, groups in ((device, [(1, "Source"), (2, "Valve"), (3, "CheckValve")]), (line, [(1, "Main")]),
                      (junction, [(1, "Tee")])):
    for code, text in groups:
        # Pro wants a domain of its own per asset group (ERROR 002580), holding the ASSETTYPE default 0 (002943).
        domain = f"{os.path.basename(table)}_{text}_Types"
        arcpy.management.CreateDomain(gdb, domain, domain, "SHORT", "CODED")
        arcpy.management.AddCodedValueToDomain(gdb, domain, 0, "Unknown")
        arcpy.management.AddCodedValueToDomain(gdb, domain, 1, "Standard")
        arcpy.management.AddSubtype(table, code, text)
        arcpy.management.AssignDomainToField(table, "ASSETTYPE", domain, code)
    arcpy.management.AddField(table, "NAME", "TEXT")
arcpy.management.AddField(device, "STATUS", "SHORT")
arcpy.management.AddField(line, "PRESSURE", "SHORT")
arcpy.management.AddField(device, "PRESSURE", "SHORT")
arcpy.management.AddField(junction, "PRESSURE", "SHORT")
arcpy.un.AddTerminalConfiguration(un, "Source-Load", "DIRECTIONAL", "Source true;Load false")
for group in ("Source", "CheckValve"):
    arcpy.un.SetTerminalConfiguration(un, "Water", "WaterDevice", group, "Standard", "Source-Load")
arcpy.un.SetNetworkCategory(un, "Water", "WaterDevice", "Source", "Standard", "Subnetwork Controller")
arcpy.un.AddNetworkCategory(un, "Isolating")
arcpy.un.SetNetworkCategory(un, "Water", "WaterDevice", "Valve", "Standard", ["Isolating"])
arcpy.un.AddNetworkAttribute(un, "Device Status", "SHORT")
arcpy.un.SetNetworkAttribute(un, "Device Status", "Water", "WaterDevice", "STATUS")
arcpy.un.AddNetworkAttribute(un, "Pressure", "SHORT")
arcpy.un.SetNetworkAttribute(un, "Pressure", "Water", "WaterLine", "PRESSURE")
arcpy.un.SetNetworkAttribute(un, "Pressure", "Water", "WaterDevice", "PRESSURE")
arcpy.un.SetNetworkAttribute(un, "Pressure", "Water", "WaterJunction", "PRESSURE")
je = "JUNCTION_EDGE_CONNECTIVITY"
arcpy.un.AddRule(un, je, "WaterDevice", "Source", "Standard", "WaterLine", "Main", "Standard", "Load")
arcpy.un.AddRule(un, je, "WaterDevice", "Valve", "Standard", "WaterLine", "Main", "Standard")
for terminal in ("Source", "Load"):
    arcpy.un.AddRule(un, je, "WaterDevice", "CheckValve", "Standard", "WaterLine", "Main", "Standard", terminal)
arcpy.un.AddRule(un, je, "WaterJunction", "Tee", "Standard", "WaterLine", "Main", "Standard")
source, load = 2, 3
ids = {}
LINES = {"A1", "A2", "A3a", "A3b", "A4", "B1", "B2", "B3", "B4", "C1", "D1", "D2", "E1"}
for table, fields, rows in (
        (device, ["NAME", "ASSETGROUP", "ASSETTYPE", "STATUS", "PRESSURE", "SHAPE@WKT"],
         [("S1", 1, 1, 0, 1, "POINT Z (0 0 0)"), ("S2", 1, 1, 0, 1, "POINT Z (40 0 0)"), ("CV2", 3, 1, 0, 1, "POINT Z (15 10 0)"),
          ("CV", 3, 1, 0, P_CV, "POINT Z (30 10 0)"), ("VB", 2, 1, 1, P_VB, "POINT Z (25 0 0)")]),
        (junction, ["NAME", "ASSETGROUP", "ASSETTYPE", "PRESSURE", "SHAPE@WKT"],
         [("J1", 1, 1, 1, "POINT Z (10 0 0)"), ("J2", 1, 1, 1, "POINT Z (20 0 0)"), ("J3", 1, 1, 1, "POINT Z (30 0 0)"),
          ("J4", 1, 1, 1, "POINT Z (10 10 0)"), ("J5", 1, 1, 1, "POINT Z (20 10 0)")]),
        # PRESSURE: a value per line for the propagator and the functions (A3b 4 and D1 2, the rest 1).
        (line, ["NAME", "ASSETGROUP", "ASSETTYPE", "FROMDEVICETERMINAL", "TODEVICETERMINAL", "PRESSURE", "SHAPE@WKT"],
         [("A1", 1, 1, load, None, 1, "LINESTRING Z (0 0 0, 10 0 0)"), ("A2", 1, 1, None, None, 1, "LINESTRING Z (10 0 0, 20 0 0)"),
          ("A3a", 1, 1, None, None, 1, "LINESTRING Z (20 0 0, 25 0 0)"),
          ("A3b", 1, 1, None, None, P_A3B, "LINESTRING Z (25 0 0, 30 0 0)"),
          ("A4", 1, 1, None, load, 1, "LINESTRING Z (30 0 0, 40 0 0)"),
          ("B1", 1, 1, None, None, 1, "LINESTRING Z (10 0 0, 10 10 0)"),
          ("B2", 1, 1, None, source, 1, "LINESTRING Z (10 10 0, 15 10 0)"),
          ("B3", 1, 1, load, None, 1, "LINESTRING Z (15 10 0, 20 10 0)"),
          ("B4", 1, 1, None, None, 1, "LINESTRING Z (20 10 0, 20 0 0)"),
          ("C1", 1, 1, None, None, 1, "LINESTRING Z (20 0 0, 20 -10 0)"),
          ("D1", 1, 1, None, source, P_D1, "LINESTRING Z (30 0 0, 30 10 0)"),
          ("D2", 1, 1, load, None, 1, "LINESTRING Z (30 10 0, 30 20 0)"),
          ("E1", 1, 1, None, None, 1, "LINESTRING Z (10 10 0, 10 20 0)")])):
    with arcpy.da.Editor(gdb), arcpy.da.InsertCursor(table, fields) as written:  # Pro: an edit operation required
        for row in rows:
            written.insertRow(row)
    with arcpy.da.SearchCursor(table, ["NAME", "GLOBALID@"]) as read:
        ids.update(dict(read))
names = {g.strip("{}").upper(): n for n, g in ids.items()}
controllers = os.path.join(work, "controllers.csv")
with open(controllers, "w", newline="", encoding="utf-8") as written:
    out = csv.writer(written)
    out.writerow(["SUBNETWORKCONTROLLERNAME", "FEATUREGLOBALID", "FEATURECLASSNAME", "FEATUREASSETGROUP",
                  "FEATUREASSETTYPE", "FEATURETERMINAL", "TIERNAME", "SUBNETWORKNAME", "DESCRIPTION", "NOTES"])
    for name, controller in (("S1", "Plant1"), ("S2", "Plant2")):
        out.writerow([controller, ids[name], "WaterDevice", "Source", "Standard", "Load", "Pressure", "Zone1", "", ""])
# Pro refuses a controller its tier does not list (ERROR 160882), so the tier lists what the mesh holds.
arcpy.un.SetSubnetworkDefinition(un, "Water", "Pressure", None,
                                 ["Source/Standard", "Valve/Standard", "CheckValve/Standard"],
                                 ["Source/Standard"], ["Main/Standard"], valid_junctions=["Tee/Standard"])
arcpy.un.EnableNetworkTopology(un)
arcpy.un.ImportSubnetworkControllers(un, controllers)
arcpy.un.ValidateNetworkTopology(un)

count = [0]


def locations(points):
    """A table of FEATUREGLOBALID, TERMINALID, PERCENTALONG: (name, terminal or None)."""
    count[0] += 1
    name = f"loc{count[0]}"
    if side == "pro":
        path = arcpy.management.CreateFeatureclass(gdb, name, "POINT", spatial_reference=SR)[0]
    else:
        path = arcpy.management.CreateFeatureclass(work, f"{name}.gpkg", "POINT", spatial_reference=SR)[0]
    for field, kind in (("FEATUREGLOBALID", "GUID"), ("TERMINALID", "LONG"), ("PERCENTALONG", "DOUBLE")):
        arcpy.management.AddField(path, field, kind)
    with arcpy.da.InsertCursor(path, ["FEATUREGLOBALID", "TERMINALID", "PERCENTALONG", "SHAPE@XY"]) as written:
        for n, terminal in points:
            if terminal is None and n not in LINES:  # Pro: a device its terminal, a junction 1 (else error 001785)
                terminal = load if n in ("S1", "S2") else 1
            written.insertRow((ids[n], terminal, 0.5 if n in LINES else None, (0, 0)))
    return path


def trace(kind, starts, **more):
    count[0] += 1
    out_json = os.path.join(work, f"trace{count[0]}.json")
    for key in ("barriers", "stopping_points"):
        if key in more:
            more[key] = locations([(n, None) for n in more[key]])
    types = ["ELEMENTS"] + more.pop("extra_types", [])
    try:
        arcpy.un.Trace(un, kind, locations([(n, None) if isinstance(n, str) else n for n in starts]),
                       domain_network="Water", tier="Pressure", result_types=types, out_json_file=out_json, **more)
    except Exception as error:  # noqa: BLE001 — the message is the observation
        return {"error": str(error).strip().splitlines()[-2:] if str(error).strip() else repr(error)}
    with open(out_json, encoding="utf-8") as f:
        doc = json.load(f)
    results = doc.get("traceResults", doc)
    found = sorted({names.get(str(e.get("globalId", "")).strip("{}").upper(), "?") for e in results.get("elements", [])})
    answer = {"elements": found}
    if results.get("functionResults"):
        answer["functions"] = [(r.get("functionType"), r.get("networkAttributeName"), r.get("result"))
                               for r in results["functionResults"]]
    return answer


closed = "'Device Status' IS_EQUAL_TO SPECIFIC_VALUE 1 #"
traces = {
    "sub_C1_closed": ("SUBNETWORK", ["C1"], {"condition_barriers": closed}),
    "sub_A4_closed": ("SUBNETWORK", ["A4"], {"condition_barriers": closed}),
    "down_S1_closed": ("DOWNSTREAM", ["S1"], {"condition_barriers": closed}),
    "prop_max_sub_C1_lt3": ("SUBNETWORK", ["C1"], {"propagators": "Pressure # PROPAGATED_MAX IS_LESS_THAN 3"}),
    "prop_max_sub_C1_lt5": ("SUBNETWORK", ["C1"], {"propagators": "Pressure # PROPAGATED_MAX IS_LESS_THAN 5"}),
    "prop_max_down_S1_lt5": ("DOWNSTREAM", ["S1"], {"propagators": "Pressure # PROPAGATED_MAX IS_LESS_THAN 5"}),
    "prop_max_sub_C1_eq1": ("SUBNETWORK", ["C1"], {"propagators": "Pressure # PROPAGATED_MAX IS_EQUAL_TO 1"}),
    "prop_min_sub_C1_eq1": ("SUBNETWORK", ["C1"], {"propagators": "Pressure # PROPAGATED_MIN IS_EQUAL_TO 1"}),
    "prop_max_down_S2_lt4": ("DOWNSTREAM", ["S2"], {"propagators": "Pressure # PROPAGATED_MAX IS_LESS_THAN 4"}),
    "prop_max_down_S1_lt2": ("DOWNSTREAM", ["S1"], {"propagators": "Pressure # PROPAGATED_MAX IS_LESS_THAN 2"}),
    "prop_max_up_C1_lt2": ("UPSTREAM", ["C1"], {"propagators": "Pressure # PROPAGATED_MAX IS_LESS_THAN 2"}),
    "prop_bitand_sub_C1": ("SUBNETWORK", ["C1"], {"propagators": "Pressure # PROPAGATED_BITWISE_AND DOES_NOT_INCLUDE_ANY 4"}),
    "prop_max_sub_C1_closed": ("SUBNETWORK", ["C1"], {"propagators": "Pressure # PROPAGATED_MAX IS_EQUAL_TO 1",
                                                       "condition_barriers": closed}),
    "sub_C1": ("SUBNETWORK", ["C1"], {}),
    "down_S1": ("DOWNSTREAM", ["S1"], {}),
}
report = {"side": side}
for key, (kind, starts, more) in traces.items():
    report[key] = trace(kind, starts, **dict(more))
print(json.dumps(report, indent=1))
