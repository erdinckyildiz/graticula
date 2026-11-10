"""Write known geometries into a mobile geodatabase with arcpy, so the blob encoding can be read against known input.
Run with Pro's python. Output: D:/temp/offline-capture/probe/samples.geodatabase"""
import arcpy, os
OUT = r"D:\temp\offline-capture\probe"
gdb = os.path.join(OUT, "samples.geodatabase")
if os.path.exists(gdb):
    os.remove(gdb)
arcpy.management.CreateMobileGDB(OUT, "samples")
sr3857 = arcpy.SpatialReference(3857)
sr4326 = arcpy.SpatialReference(4326)

def fc(name, gtype, sr, z="DISABLED", m="DISABLED"):
    arcpy.management.CreateFeatureclass(gdb, name, gtype, spatial_reference=sr, has_z=z, has_m=m)
    arcpy.management.AddField(os.path.join(gdb, name), "label", "TEXT", field_length=40)
    return os.path.join(gdb, name)

def ins(path, rows, z=False, m=False):
    with arcpy.da.InsertCursor(path, ["SHAPE@", "label"]) as c:
        for label, geom in rows:
            c.insertRow([geom, label])

P = arcpy.Point
def pt(x, y, z=None, m=None):
    p = P(x, y)
    if z is not None: p.Z = z
    if m is not None: p.M = m
    return p

# points
p2 = fc("pt2_4326", "POINT", sr4326)
ins(p2, [("origin", arcpy.PointGeometry(pt(0, 0), sr4326)),
         ("one", arcpy.PointGeometry(pt(1, 1), sr4326)),
         ("neg", arcpy.PointGeometry(pt(-1, -1), sr4326)),
         ("ist", arcpy.PointGeometry(pt(28.9784, 41.0082), sr4326)),
         ("null", None)])
p3 = fc("pt2_3857", "POINT", sr3857)
ins(p3, [("a", arcpy.PointGeometry(pt(3150000, 4750000), sr3857)),
         ("b", arcpy.PointGeometry(pt(3150001, 4750000), sr3857)),
         ("c", arcpy.PointGeometry(pt(3150000.5, 4750000.25), sr3857))])
pz = fc("ptz_3857", "POINT", sr3857, z="ENABLED")
ins(pz, [("z42", arcpy.PointGeometry(pt(3150000, 4750000, 42), sr3857, True)),
         ("z0", arcpy.PointGeometry(pt(3150000, 4750000, 0), sr3857, True)),
         ("zneg", arcpy.PointGeometry(pt(3150000, 4750000, -10.5), sr3857, True))])
pzm = fc("ptzm_3857", "POINT", sr3857, z="ENABLED", m="ENABLED")
ins(pzm, [("zm", arcpy.PointGeometry(pt(3150000, 4750000, 42, 7), sr3857, True, True))])
mp = fc("mpt_4326", "MULTIPOINT", sr4326)
ins(mp, [("two", arcpy.Multipoint(arcpy.Array([pt(1, 1), pt(2, 3)]), sr4326))])
ln = fc("line_4326", "POLYLINE", sr4326)
ins(ln, [("seg", arcpy.Polyline(arcpy.Array([pt(0, 0), pt(1, 1)]), sr4326)),
         ("three", arcpy.Polyline(arcpy.Array([pt(0, 0), pt(1, 1), pt(2, 0)]), sr4326)),
         ("twoparts", arcpy.Polyline(arcpy.Array([arcpy.Array([pt(0, 0), pt(1, 1)]), arcpy.Array([pt(5, 5), pt(6, 7)])]), sr4326))])
lz = fc("linez_3857", "POLYLINE", sr3857, z="ENABLED")
ins(lz, [("z", arcpy.Polyline(arcpy.Array([pt(3150000, 4750000, 1), pt(3150010, 4750020, 2)]), sr3857, True))])
pg = fc("poly_4326", "POLYGON", sr4326)
ins(pg, [("square", arcpy.Polygon(arcpy.Array([pt(0, 0), pt(0, 1), pt(1, 1), pt(1, 0), pt(0, 0)]), sr4326)),
         ("hole", arcpy.Polygon(arcpy.Array([arcpy.Array([pt(0, 0), pt(0, 10), pt(10, 10), pt(10, 0), pt(0, 0)]),
                                             arcpy.Array([pt(2, 2), pt(8, 2), pt(8, 8), pt(2, 8), pt(2, 2)])]), sr4326))])
print("ok", gdb)
