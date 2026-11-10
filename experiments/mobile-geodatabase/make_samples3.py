import arcpy, os
OUT = r"D:\temp\offline-capture\probe"; gdb = os.path.join(OUT, "samples3.geodatabase")
if os.path.exists(gdb): os.remove(gdb)
arcpy.management.CreateMobileGDB(OUT, "samples3"); sr = arcpy.SpatialReference(4326)
P = arcpy.Point; A = arcpy.Array
for g in ("POLYLINE", "MULTIPOINT"):
    arcpy.management.CreateFeatureclass(gdb, g.lower(), g, spatial_reference=sr); arcpy.management.AddField(os.path.join(gdb, g.lower()), "n", "SHORT")
with arcpy.da.InsertCursor(os.path.join(gdb, "polyline"), ["SHAPE@", "n"]) as c:
    for n in (2, 3, 4, 5, 9, 10, 11, 12):
        c.insertRow([arcpy.Polyline(A([P(i, (i * 7) % 5) for i in range(n)]), sr), n])
with arcpy.da.InsertCursor(os.path.join(gdb, "multipoint"), ["SHAPE@", "n"]) as c:
    for n in (2, 5, 10, 11):
        c.insertRow([arcpy.Multipoint(A([P(i, (i * 7) % 5) for i in range(n)]), sr), n])
arcpy.management.CreateFeatureclass(gdb, "ptm", "POINT", spatial_reference=sr, has_m="ENABLED")
with arcpy.da.InsertCursor(os.path.join(gdb, "ptm"), ["SHAPE@"]) as c:
    p = P(1, 2); p.M = 5; c.insertRow([arcpy.PointGeometry(p, sr, False, True)])
print("ok")
