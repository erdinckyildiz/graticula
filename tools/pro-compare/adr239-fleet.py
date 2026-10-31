"""ADR-239 condition 5: one fleet routing instance in ArcGIS Pro 3.7 and in grapy — the objective weights of §5.8 (f)
and the break placement of (g) first.

    "C:/Program Files/ArcGIS/Pro/bin/Python/envs/arcgispro-py3/python.exe" adr239_fleet.py pro   <folder>
    D:/temp/grapy-env/Scripts/python.exe                                   adr239_fleet.py grapy <folder> <grapy src>

Both sides load the SAME GeoPackage of roads (``roads_input.gpkg/roads``, written by whichever side runs first, field ``Minutes``)
into a feature dataset in Web Mercator and build a network with a ``Minutes`` time cost and a ``Length`` distance. Pro's
CreateNetworkDataset makes only Length; the Minutes attribute is added to the template Pro writes for that network
(CreateTemplateFromNetworkDataset → CreateNetworkDatasetFromTemplate). A tool, not a test: it needs Pro.

Instances:
  weights — a line road 0..100 m (10 min), depot at 50, one open route; order ``a`` at 5 (4.5 min), ``b`` at 100.
            a-then-b costs 14.0 and reaches b at 8:14; b-then-a costs 14.5 and reaches b at 8:05. b's window closes
            at 8:14 - L, so a-then-b is L minutes late. a-then-b is chosen exactly when w·L < 0.5, so the L at which the
            choice flips measures w, the weight of a minute late, for each Time Window Importance.
  breaks  — the same road, depot at 50, closed route, orders at 10, 20, 80, 90 with a minute of service each, and one
            break of each kind in turn: a time window break, a travel time break, a work time break.
"""
import datetime
import json
import os
import re
import shutil
import sys

side, folder = sys.argv[1], sys.argv[2]
if side == "grapy":
    sys.path.insert(0, sys.argv[3])
    import grapy as arcpy
    import grapy.na as na
    import grapy.nax as nax
    from grapy import _runtime  # noqa: F401
else:
    import arcpy
    import arcpy.na as na
    import arcpy.nax as nax
    arcpy.CheckOutExtension("Network")

DAY = datetime.date(2026, 1, 5)
SR = arcpy.SpatialReference(3857)
LINES = ["LINESTRING(0 0,100 0)"]


def at(hour, minute=0.0):
    return datetime.datetime.combine(DAY, datetime.time(hour)) + datetime.timedelta(minutes=minute)


def shapefile(tag="", minutes=10.0):
    """The one input both sides read: written once, by whichever side runs first."""
    gpkg = os.path.join(folder, f"roads_input{tag}.gpkg")
    path = os.path.join(gpkg, "roads")
    if not os.path.exists(gpkg):
        arcpy.management.CreateSQLiteDatabase(gpkg, "GEOPACKAGE")
        arcpy.management.CreateFeatureclass(gpkg, "roads", "POLYLINE", spatial_reference=SR)
        arcpy.management.AddField(path, "Minutes", "DOUBLE")
        with arcpy.da.InsertCursor(path, ["SHAPE@WKT", "Minutes"]) as cur:
            cur.insertRow(["LINESTRING (0 0, 100 0)", minutes])
    return path


def pro_network(roads, tag=""):
    gdb = os.path.join(folder, f"fleet_pro{tag}.gdb")
    if os.path.exists(gdb):
        shutil.rmtree(gdb)
    arcpy.management.CreateFileGDB(folder, f"fleet_pro{tag}.gdb")
    fds = arcpy.management.CreateFeatureDataset(gdb, "t", SR)[0]
    target = arcpy.management.CreateFeatureclass(fds, "roads", "POLYLINE")[0]  # 32-bit object ids, as a network needs
    arcpy.management.AddField(target, "Minutes", "DOUBLE")
    arcpy.management.Append(roads, target, "NO_TEST")
    first = arcpy.na.CreateNetworkDataset(fds, "net", ["roads"], "NO_ELEVATION")[0]
    template = os.path.join(folder, f"fleet_template{tag}.xml")
    if os.path.exists(template):
        os.remove(template)
    arcpy.na.CreateTemplateFromNetworkDataset(first, template)
    xml = open(template, encoding="utf-8").read()
    attr = re.search(r"<EvaluatedNetworkAttribute .*?</EvaluatedNetworkAttribute>", xml).group(0)
    minutes = (attr.replace("<ID>1</ID>", "<ID>2</ID>").replace("<Name>Length</Name>", "<Name>Minutes</Name>")
               .replace("<Units>Meters</Units>", "<Units>Minutes</Units>"))
    xml = xml.replace(attr, attr + minutes)
    assigns = re.findall(r"<NetworkAssignment .*?</NetworkAssignment>", xml)
    extra = "".join(a.replace("<NetworkAttributeName>Length</NetworkAttributeName>",
                              "<NetworkAttributeName>Minutes</NetworkAttributeName>").replace("!Shape!", "!Minutes!")
                    for a in assigns)
    xml = xml.replace("</NetworkAssignments>", extra + "</NetworkAssignments>")
    open(template, "w", encoding="utf-8").write(xml)
    arcpy.management.Delete(first)
    net = arcpy.na.CreateNetworkDatasetFromTemplate(template, fds)[0]
    arcpy.na.BuildNetwork(net)
    mode = arcpy.na.TravelMode()
    mode.name = "Driving"
    mode.impedance = "Minutes"
    mode.timeAttributeName = "Minutes"
    mode.distanceAttributeName = "Length"
    # ReplaceNetworkTravelModes takes a JSON object of travel modes keyed by name (its error 030403 says so).
    modes = os.path.join(folder, f"fleet_travel_modes{tag}.json")
    with open(modes, "w", encoding="utf-8") as f:
        json.dump({"Driving": json.loads(str(mode))}, f)
    arcpy.na.ReplaceNetworkTravelModes(modes, net)
    return net, "Driving"


def grapy_network(roads, tag=""):
    gpkg = os.path.join(folder, f"fleet_grapy{tag}.gpkg")
    if os.path.exists(gpkg):
        os.remove(gpkg)
    gpkg = arcpy.management.CreateFileGDB(folder, f"fleet_grapy{tag}")[0]
    fds = arcpy.management.CreateFeatureDataset(gpkg, "t", SR)[0]
    arcpy.conversion.ExportFeatures(roads, os.path.join(fds, "roads"))
    net = na.CreateNetworkDataset(fds, "net", ["roads"], "NO_ELEVATION")[0]
    arcpy.graticula.AddNetworkAttribute(net, "Minutes", "Cost", "Minutes", [("roads", "BOTH", "FIELD", "Minutes")])
    arcpy.graticula.AddNetworkTravelMode(net, "Driving", "Minutes", [], "Minutes", "Length")
    na.BuildNetwork(net)
    return net, "Driving"


I2 = nax.VehicleRoutingProblemInputDataType2
O2 = nax.VehicleRoutingProblemOutputDataType2


def solve(net, mode, depot, orders, routes, extra=None, factor=None):
    vrp = nax.VehicleRoutingProblem(net, nax.VehicleRoutingProblemSchemaVersion.Two)
    vrp.travelMode = mode
    vrp.defaultDate = datetime.datetime.combine(DAY, datetime.time())
    vrp.timeUnits = nax.TimeUnits.Minutes
    if factor is not None:
        vrp.timeWindowFactor = getattr(nax.Importance, factor)
    tables = [(I2.Depots, [{"Name": "D", "SHAPE@XY": depot}]), (I2.Orders, orders), (I2.Routes, routes)]
    for input_type, rows in tables + list((extra or {}).items()):
        fields = sorted({k for r in rows for k in r})
        with vrp.insertCursor(input_type, fields) as cur:
            for r in rows:
                cur.insertRow([r.get(f) for f in fields])
    result = vrp.solve()
    if not result.solveSucceeded:
        return {"failed": [str(m) for m in result.solverMessages(nax.MessageSeverity.All)]}
    out = {}
    want = {O2.Orders: ["Name", "RouteName", "Sequence", "ArriveTime", "DepartTime", "ViolationTime", "WaitTime"],
            O2.Routes: ["Name", "OrderCount", "StartTime", "EndTime", "TotalCost", "RegularTimeCost", "TotalTime",
                        "TotalTravelTime", "TotalViolationTime", "TotalWaitTime", "TotalBreakServiceTime"],
            O2.Breaks: ["RouteName", "Sequence", "ArriveTime", "DepartTime", "ServiceTime", "ViolationTime",
                        "ArriveCurbApproach"]}
    for output, fields in want.items():
        have = {f.lower() for f in result.fieldNames(output)}
        fields = [f for f in fields if f.lower() in have]
        rows = []
        for row in result.searchCursor(output, fields):
            rows.append({f: (v.strftime("%H:%M:%S") if isinstance(v, datetime.datetime) else
                             round(v, 4) if isinstance(v, float) else v) for f, v in zip(fields, row)})
        out[output.name] = rows
    return out


def main():
    roads = shapefile()
    net, mode = pro_network(roads) if side == "pro" else grapy_network(roads)
    report = {"side": side, "weights": {}, "breaks": {}}
    # (importance, where order a lies, the minutes late L). a at x lies e = (50 - x) / 10 minutes from the depot:
    # a-then-b reaches b at 8:00 + 2e + 5 and costs that; b-then-a costs 10 + e. The detour 5 - e buys L minutes.
    coarse = (0.01, 0.03, 0.04, 0.06, 0.08, 0.3, 0.4, 0.6, 0.8, 3, 4, 6, 8)
    probes = [(f, 5.0, late) for f in ("High", "Medium", "Low") for late in coarse]
    probes += [("High", 5.0, 0.045), ("High", 5.0, 0.055), ("Medium", 5.0, 0.45), ("Medium", 5.0, 0.55)]
    probes += [("Low", 0.5, late) for late in (0.3, 0.45, 0.55, 0.8, 1, 1.5, 2, 3, 5, 7, 9)]
    for factor, x, late in probes:
        e = (50 - x) / 10
        orders = [{"Name": "a", "SHAPE@XY": (x, 0)},
                  {"Name": "b", "SHAPE@XY": (100, 0), "TimeWindowStart": at(8),
                   "TimeWindowEnd": at(8, 2 * e + 5 - late)}]
        routes = [{"Name": "R", "StartDepotName": "D", "EarliestStartTime": at(8), "LatestStartTime": at(8),
                   "CostPerUnitTime": 1.0, "CostPerUnitDistance": 0.0}]
        got = solve(net, mode, (50, 0), orders, routes, factor=factor)
        key = f"{factor} a@{x} L={late} (flips at w={round((5 - e) / late, 4)})"
        if "failed" in got:
            report["weights"][key] = got
            continue
        first = min(got["Orders"], key=lambda o: o["Sequence"] or 99)["Name"]
        report["weights"][key] = {"first": first, "routes": got["Routes"]}
    # Low importance never flipped on the road above, so the same shape a hundred times slower (1,000 minutes end to
    # end): a at 0.05 m is a detour of 0.5 minutes against up to 499 minutes late.
    slow = shapefile("_slow", 1000.0)
    slow_net, slow_mode = pro_network(slow, "_slow") if side == "pro" else grapy_network(slow, "_slow")
    for late in (1, 5, 10, 25, 50, 100, 250, 499):
        e = (50 - 0.05) * 10
        orders = [{"Name": "a", "SHAPE@XY": (0.05, 0)},
                  {"Name": "b", "SHAPE@XY": (100, 0), "TimeWindowStart": at(8), "TimeWindowEnd": at(8, 2 * e + 500 - late)}]
        routes = [{"Name": "R", "StartDepotName": "D", "EarliestStartTime": at(8), "LatestStartTime": at(8),
                   "CostPerUnitTime": 1.0, "CostPerUnitDistance": 0.0}]
        got = solve(slow_net, slow_mode, (50, 0), orders, routes, factor="Low")
        key = f"Low slow a@0.05 L={late} (flips at w={round(0.5 / late, 5)})"
        report["weights"][key] = got if "failed" in got else {
            "first": min(got["Orders"], key=lambda o: o["Sequence"] or 99)["Name"], "routes": got["Routes"]}
    orders = [{"Name": n, "ServiceTime": 1.0, "SHAPE@XY": (x, 0)} for n, x in (("o10", 10), ("o20", 20), ("o80", 80),
                                                                                 ("o90", 90))]
    routes = [{"Name": "R", "StartDepotName": "D", "EndDepotName": "D", "EarliestStartTime": at(8),
               "LatestStartTime": at(8), "CostPerUnitTime": 1.0, "CostPerUnitDistance": 0.0}]
    kinds = {"window": {"RouteName": "R", "Precedence": 1, "ServiceTime": 3.0, "TimeWindowStart": at(8, 4),
                        "TimeWindowEnd": at(8, 6), "IsPaid": 1},
             "window_late": {"RouteName": "R", "Precedence": 1, "ServiceTime": 3.0, "TimeWindowStart": at(9),
                             "TimeWindowEnd": at(9, 30), "IsPaid": 1},
             "travel": {"RouteName": "R", "Precedence": 1, "ServiceTime": 2.0, "MaxTravelTimeBetweenBreaks": 6.0,
                        "IsPaid": 1},
             "work": {"RouteName": "R", "Precedence": 1, "ServiceTime": 2.0, "MaxCumulWorkTime": 7.0, "IsPaid": 1}}
    report["breaks"]["none"] = solve(net, mode, (50, 0), orders, routes)
    for name, brk in kinds.items():
        report["breaks"][name] = solve(net, mode, (50, 0), orders, routes, extra={I2.Breaks: [brk]})
    print(json.dumps(report, indent=1, default=str))


main()
