# A benchmark (ADR-239, D-330, 2026-10-31): grapy's fleet routing solve time; not a test, not run in CI.
# Usage: <grapy python> adr239-fleet-bench.py <worktree> 25,50,100,200 [profile]
"""D-330: grapy's fleet routing solve time on a 20 x 20 street grid (100 m blocks, a minute each) with N orders."""
import cProfile, datetime, os, pstats, random, sys, tempfile, time
sys.path.insert(0, sys.argv[1] + "/src/grapy")
import grapy as arcpy
import grapy.na as na
import grapy.nax as nax
import shapely
DAY = datetime.date(2026, 1, 5)
I2 = nax.VehicleRoutingProblemInputDataType2
d = tempfile.mkdtemp(); arcpy.env.workspace = d
gpkg = arcpy.management.CreateFileGDB(d, "city")[0]
fds = arcpy.management.CreateFeatureDataset(gpkg, "t", 3857)[0]
roads = arcpy.management.CreateFeatureclass(fds, "roads", "POLYLINE")[0]
arcpy.management.AddField(roads, "minutes", "DOUBLE")
K = 20
with arcpy.da.InsertCursor(roads, ["SHAPE@", "minutes"]) as w:
    for a in range(K + 1):
        for b in range(K):
            w.insertRow((f"LINESTRING({b*100} {a*100},{(b+1)*100} {a*100})", 1.0))
            w.insertRow((f"LINESTRING({a*100} {b*100},{a*100} {(b+1)*100})", 1.0))
net = na.CreateNetworkDataset(fds, "net", ["roads"], "NO_ELEVATION")[0]
arcpy.graticula.AddNetworkAttribute(net, "Minutes", "Cost", "Minutes", [("roads", "BOTH", "FIELD", "minutes")])
arcpy.graticula.AddNetworkTravelMode(net, "Driving", "Minutes", [], "Minutes", "Length")
na.BuildNetwork(net)
for n in [int(v) for v in sys.argv[2].split(",")]:
    rng = random.Random(n)
    vrp = nax.VehicleRoutingProblem(net, nax.VehicleRoutingProblemSchemaVersion.Two)
    vrp.travelMode = "Driving"; vrp.defaultDate = DAY
    with vrp.insertCursor(I2.Depots, ["Name", "SHAPE@XY"]) as c:
        c.insertRow(["D", (1000, 1000)])
    with vrp.insertCursor(I2.Orders, ["Name", "SHAPE@XY", "ServiceTime", "DeliveryQuantity_1"]) as c:
        for i in range(n):
            x, y = rng.randrange(0, K + 1) * 100, rng.randrange(0, K) * 100 + rng.randrange(10, 90)
            c.insertRow([f"O{i}", (x, y), 5, 1])
    vehicles = max(2, n // 10 + 1)
    with vrp.insertCursor(I2.Routes, ["Name", "StartDepotName", "EndDepotName", "Capacity_1", "MaxOrderCount"]) as c:
        for r in range(vehicles):
            c.insertRow([f"R{r}", "D", "D", 12, 12])
    t = time.time()
    pr = cProfile.Profile()
    if len(sys.argv) > 3: pr.enable()
    result = vrp.solve()
    pr.disable()
    took = time.time() - t
    routed = sum(1 for r in result.searchCursor(nax.VehicleRoutingProblemOutputDataType2.Orders, ["RouteName"]) if r[0])
    print(f"N {n} vehicles {vehicles} seconds {took:.1f} routed {routed}", flush=True)
    if len(sys.argv) > 3:
        pstats.Stats(pr).sort_stats("tottime").print_stats(10)
