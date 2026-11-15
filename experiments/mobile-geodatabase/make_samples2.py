import arcpy, os
OUT = r"D:\temp\offline-capture\probe"; gdb = os.path.join(OUT, "samples2.geodatabase")
if os.path.exists(gdb): os.remove(gdb)
arcpy.management.CreateMobileGDB(OUT, "samples2"); sr = arcpy.SpatialReference(4326)
P = arcpy.Point; A = arcpy.Array
def fc(name, g):
    arcpy.management.CreateFeatureclass(gdb, name, g, spatial_reference=sr); arcpy.management.AddField(os.path.join(gdb, name), "label", "TEXT", field_length=40); return os.path.join(gdb, name)
def ring(x0, y0, w, h): return A([P(x0, y0), P(x0, y0 + h), P(x0 + w, y0 + h), P(x0 + w, y0), P(x0, y0)])
pg = fc("poly", "POLYGON")
with arcpy.da.InsertCursor(pg, ["SHAPE@", "label"]) as c:
    c.insertRow([arcpy.Polygon(A([P(0, 0), P(0, 3), P(2, 0), P(0, 0)]), sr), "tri"])
    c.insertRow([arcpy.Polygon(ring(0, 0, 1, 1), sr), "sq"])
    c.insertRow([arcpy.Polygon(A([ring(0, 0, 1, 1), ring(5, 5, 1, 1)]), sr), "two_outer"])
    c.insertRow([arcpy.Polygon(A([ring(0, 0, 10, 10), A([P(2, 2), P(4, 2), P(4, 4), P(2, 4), P(2, 2)])]), sr), "one_hole"])
    c.insertRow([arcpy.Polygon(A([ring(0, 0, 10, 10), A([P(2, 2), P(4, 2), P(4, 4), P(2, 4), P(2, 2)]), A([P(6, 6), P(8, 6), P(8, 8), P(6, 8), P(6, 6)])]), sr), "two_holes"])
    c.insertRow([arcpy.Polygon(A([ring(0, 0, 1, 1), ring(5, 5, 1, 1), ring(9, 9, 1, 1)]), sr), "three_outer"])
ln = fc("line", "POLYLINE")
with arcpy.da.InsertCursor(ln, ["SHAPE@", "label"]) as c:
    c.insertRow([arcpy.Polyline(A([A([P(0, 0), P(1, 1)]), A([P(5, 5), P(6, 7)]), A([P(9, 9), P(9, 10), P(10, 10)])]), sr), "three_parts"])
    c.insertRow([arcpy.Polyline(A([A([P(0, 0), P(1, 1), P(2, 2)]), A([P(5, 5), P(6, 7)])]), sr), "parts_3_2"])
mp = fc("mpt", "MULTIPOINT")
with arcpy.da.InsertCursor(mp, ["SHAPE@", "label"]) as c:
    c.insertRow([arcpy.Multipoint(A([P(1, 1)]), sr), "one"])
    c.insertRow([arcpy.Multipoint(A([P(1, 1), P(2, 3), P(4, 4)]), sr), "three"])
print("ok")
