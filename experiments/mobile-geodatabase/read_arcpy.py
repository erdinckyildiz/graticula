import arcpy, sys
gdb = sys.argv[1]
arcpy.env.workspace = gdb
print("fcs", arcpy.ListFeatureClasses(), "tables", arcpy.ListTables())
for fc in arcpy.ListFeatureClasses() + arcpy.ListTables():
    d = arcpy.Describe(fc)
    flds = [f.name for f in arcpy.ListFields(fc) if f.type not in ("Geometry", "Blob")]
    rows = []
    shp = ["SHAPE@WKT"] if getattr(d, "shapeType", None) else []
    with arcpy.da.SearchCursor(fc, flds + shp) as c:
        for r in c: rows.append(r)
    print(fc, getattr(d, "shapeType", "table"), "Z" if getattr(d, "hasZ", False) else "", "rows", len(rows))
    for r in rows[:3]: print("   ", str(r)[:220])
