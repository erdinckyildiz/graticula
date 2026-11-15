import arcpy, sys
gdb = sys.argv[1]; fc = gdb + "/main.graticula_test_offline_020406_points"
sr = arcpy.SpatialReference(3857)
p = arcpy.Point(3160000, 4760000); p.Z = 7
ed = arcpy.da.Editor(gdb); ed.startEditing(False, False); ed.startOperation()
with arcpy.da.InsertCursor(fc, ["SHAPE@", "name"]) as c: oid = c.insertRow([arcpy.PointGeometry(p, sr, True), "device_add"])
with arcpy.da.UpdateCursor(fc, ["name"], "objectid = 1") as c:
    for r in c: c.updateRow(["device_upd"])
with arcpy.da.UpdateCursor(fc, ["objectid"], "objectid = 5") as c:
    for r in c: c.deleteRow()
ed.stopOperation(); ed.stopEditing(True)
print("inserted", oid)
with arcpy.da.SearchCursor(fc, ["objectid", "name", "SHAPE@WKT"]) as c:
    for r in c: print("  ", r)
