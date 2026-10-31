# Whether ArcGIS Pro reads a GeoPackage grapy wrote (2026-10-31). A tool, not a test: it needs Pro and is not run in CI;
# src/grapy/tests/test_gpkg_conformance.py checks the GeoPackage 1.4 points without it.
#
# Two steps, one per Python:
#   D:/temp/grapy-env/Scripts/python.exe -I tools/pro-compare/gpkg-pro-reads.py write <scratch>/grapy.gpkg
#   "C:/Program Files/ArcGIS/Pro/bin/Python/envs/arcgispro-py3/python.exe" -I tools/pro-compare/gpkg-pro-reads.py read <scratch>/grapy.gpkg
#
# `write` makes a point, a line and a polygon feature class (EPSG:4326) and a table, each with a text and a double
# field and rows. `read` opens each in Pro: arcpy.Describe, a da.SearchCursor with SHAPE@ (OID@ for the table), and
# ExportFeatures / ExportTable into a file geodatabase, and exits 1 if any step fails. The export target is a file
# geodatabase because ExportFeatures to a shapefile fails with ERROR 002809 on Pro's own GeoPackages too; the table is
# not named "tab", which Pro reads as a MapInfo .tab file name, again for its own GeoPackages as much as grapy's.
#
# Before the fix every feature class failed: Describe said Table with a geom:Blob field, the cursor "table has no
# geometry", and ExportFeatures ERROR 000732.
import os
import sys
import tempfile

CLASSES = (("pts", "POINT", [((29.0, 41.0), "a", 1.5), ((30.0, 40.0), "b", 2.5)]),
           ("lines", "POLYLINE", [("LINESTRING (29 41, 30 40)", "l", 3.0)]),
           ("polys", "POLYGON", [("POLYGON ((29 40, 30 40, 30 41, 29 41, 29 40))", "p", 4.0)]))
TABLE = ("attrs", [("t", 5.0)])


def write(file):
    sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "src", "grapy"))
    import grapy as arcpy
    if os.path.exists(file):
        os.remove(file)
    sr = arcpy.SpatialReference(4326)
    for name, kind, rows in CLASSES:
        arcpy.management.CreateFeatureclass(file, name, kind, spatial_reference=sr)
    arcpy.management.CreateTable(file, TABLE[0])
    for name in [c[0] for c in CLASSES] + [TABLE[0]]:
        arcpy.management.AddField(f"{file}/{name}", "NAME", "TEXT")
        arcpy.management.AddField(f"{file}/{name}", "VAL", "DOUBLE")
    for name, kind, rows in CLASSES:
        token = "SHAPE@XY" if kind == "POINT" else "SHAPE@WKT"
        with arcpy.da.InsertCursor(f"{file}/{name}", [token, "NAME", "VAL"]) as cursor:
            for row in rows:
                cursor.insertRow(row)
    with arcpy.da.InsertCursor(f"{file}/{TABLE[0]}", ["NAME", "VAL"]) as cursor:
        for row in TABLE[1]:
            cursor.insertRow(row)
    print("wrote", file)


def read(file):
    import arcpy
    out = arcpy.management.CreateFileGDB(tempfile.mkdtemp(prefix="gpkgpro"), "out.gdb")[0]
    failed = 0
    expected = {name: len(rows) for name, _, rows in CLASSES}
    expected[TABLE[0]] = len(TABLE[1])
    for name, count in expected.items():
        path = os.path.join(file, "main." + name)
        is_table = name == TABLE[0]
        print("==", name)
        try:
            d = arcpy.Describe(path)
            print("  describe:", d.dataType, getattr(d, "shapeType", None),
                  getattr(getattr(d, "spatialReference", None), "factoryCode", None), [f"{f.name}:{f.type}" for f in d.fields])
            if d.dataType != ("Table" if is_table else "FeatureClass"):
                raise RuntimeError(f"described as {d.dataType}")
            with arcpy.da.SearchCursor(path, ["OID@" if is_table else "SHAPE@", "NAME", "VAL"]) as rows:
                read_rows = [(r[0] if is_table else r[0].WKT, *r[1:]) for r in rows]
            for row in read_rows:
                print("  row:", row)
            if len(read_rows) != count:
                raise RuntimeError(f"{len(read_rows)} rows read, {count} written")
            target = os.path.join(out, name + "_out")
            (arcpy.conversion.ExportTable if is_table else arcpy.conversion.ExportFeatures)(path, target)
            exported = int(arcpy.management.GetCount(target)[0])
            print("  export:", exported, "rows")
            if exported != count:
                raise RuntimeError(f"{exported} rows exported, {count} written")
        except Exception as error:
            failed += 1
            print("  FAILED:", " | ".join(str(error).strip().splitlines()[:3]))
    print("failed:", failed)
    return 1 if failed else 0


if __name__ == "__main__":
    if len(sys.argv) != 3 or sys.argv[1] not in ("write", "read"):
        sys.exit("usage: gpkg-pro-reads.py write|read <file>.gpkg")
    sys.exit(write(sys.argv[2]) if sys.argv[1] == "write" else read(sys.argv[2]))
