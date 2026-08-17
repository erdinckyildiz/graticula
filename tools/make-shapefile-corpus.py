"""Build the shapefile corpus the reader tests read.

Run:  pip install pyshp && python tools/make-shapefile-corpus.py

Writes into tests/Graticula.Core.Tests/corpus/shapefile. The files are checked
in, so this only needs running when a case is added or the corpus is lost.
Build a shapefile corpus for the reader tests.

pyshp is an independent implementation of the same published specification,
which is the point: a parser verified only against files it was written
alongside proves nothing. Real geometry comes out of the PostGIS corpus.
"""
import io, os, struct, zipfile, subprocess, json
import shapefile

OUT = r"c:\Personal\Projects\GIS\tests\Graticula.Core.Tests\corpus\shapefile"
os.makedirs(OUT, exist_ok=True)


def write(name, kind, records, fields, encoding="utf-8", prj=None, cpg=None):
    path = os.path.join(OUT, name)
    w = shapefile.Writer(path, shapeType=kind, encoding=encoding)
    for fname, ftype, size, dec in fields:
        w.field(fname, ftype, size, dec)
    for shape, values in records:
        if shape is None:
            w.null()
        elif kind == shapefile.POINT:
            w.point(*shape)
        elif kind == shapefile.POLYLINE:
            w.line(shape)
        elif kind == shapefile.POLYGON:
            w.poly(shape)
        elif kind == shapefile.MULTIPOINT:
            w.multipoint(shape)
        w.record(*values)
    w.close()
    if prj:
        io.open(path + ".prj", "w", encoding="utf-8").write(prj)
    if cpg:
        io.open(path + ".cpg", "w", encoding="ascii").write(cpg)
    return path


WGS84 = ('GEOGCS["GCS_WGS_1984",DATUM["D_WGS_1984",SPHEROID["WGS_1984",'
         '6378137.0,298.257223563]],PRIMEM["Greenwich",0.0],'
         'UNIT["Degree",0.0174532925199433]]')

# 1. Points with plain attributes.
write("points", shapefile.POINT,
      [((10.0, 20.0), [1, "first", 1.5]),
       ((30.0, 40.0), [2, "second", 2.5])],
      [("id", "N", 10, 0), ("name", "C", 40, 0), ("value", "N", 12, 3)],
      prj=WGS84)

# 2. A polygon with a hole: outer clockwise, inner counter-clockwise.
outer = [[0, 0], [0, 10], [10, 10], [10, 0], [0, 0]]
hole = [[3, 3], [7, 3], [7, 7], [3, 7], [3, 3]]
write("holed", shapefile.POLYGON, [([outer, hole], ["with-hole"])],
      [("label", "C", 20, 0)], prj=WGS84)

# 3. Two separate outer rings in one record: a multipolygon.
a = [[0, 0], [0, 5], [5, 5], [5, 0], [0, 0]]
b = [[20, 20], [20, 25], [25, 25], [25, 20], [20, 20]]
write("twoparts", shapefile.POLYGON, [([a, b], ["two"])],
      [("label", "C", 20, 0)], prj=WGS84)

# 4. Turkish text in Windows-1254, declared by a .cpg.
write("turkish_cp1254", shapefile.POINT,
      [((1.0, 2.0), ["Şişli Çayırı"]), ((3.0, 4.0), ["Üsküdar Ğölü"])],
      [("ad", "C", 60, 0)], encoding="cp1254", prj=WGS84, cpg="ISO8859-9")

# 5. The same text in UTF-8, declared by a .cpg.
write("turkish_utf8", shapefile.POINT,
      [((1.0, 2.0), ["Şişli Çayırı"]), ((3.0, 4.0), ["Üsküdar Ğölü"])],
      [("ad", "C", 60, 0)], encoding="utf-8", prj=WGS84, cpg="UTF-8")

# 6. Turkish text with NO .cpg, which must be refused rather than guessed.
write("turkish_undeclared", shapefile.POINT,
      [((1.0, 2.0), ["Şişli Çayırı"])],
      [("ad", "C", 60, 0)], encoding="cp1254", prj=WGS84)

# 7. Lines.
write("lines", shapefile.POLYLINE,
      [([[[0, 0], [1, 1], [2, 0]]], ["a"]),
       ([[[0, 0], [5, 5]], [[10, 10], [15, 15]]], ["two-parts"])],
      [("label", "C", 20, 0)], prj=WGS84)

# 8. A null shape beside a real one: a feature with attributes and no location.
write("withnull", shapefile.POINT,
      [((1.0, 2.0), ["here"]), (None, ["nowhere"])],
      [("label", "C", 20, 0)], prj=WGS84)

# 9. Real geometry, straight out of the PostGIS corpus.
#
# <b>Ordered, because a LIMIT without an ORDER BY is a different corpus every time.</b>
# This query had no ordering until 2026-08-18, so which fifty polygons it exported was
# whatever the planner handed back — and a test asserted that exactly two of them carried
# a hole. Regenerating gave three, and the suite went red on a machine with the same
# import. Ordering makes the corpus a function of the database rather than of the run;
# how many holes it contains is still a property of whichever OSM extract is loaded,
# which is why ShapefileReaderTests no longer pins the number.
sql = ("select st_astext(st_transform(way, 4326)), coalesce(name, '') "
       "from planet_osm_polygon where way is not null and name is not null "
       "and st_npoints(way) between 20 and 200 order by osm_id limit 50")
try:
    out = subprocess.run(
        ["docker", "exec", "gis-experiment-postgis", "psql", "-U", "gis", "-d", "gis",
         "-t", "-A", "-F", "|", "-c", sql],
        capture_output=True, text=True, encoding="utf-8", timeout=120).stdout
except Exception as e:
    out = ""
    print("postgis export skipped:", e)

rows = [r for r in out.strip().split("\n") if r.startswith("POLYGON")]
if rows:
    recs = []
    for r in rows:
        wkt, name = r.rsplit("|", 1)
        body = wkt[wkt.index("((") + 2:wkt.rindex("))")]
        rings = []
        for ring in body.split("),("):
            pts = [[float(v) for v in p.strip().split()] for p in ring.split(",")]
            rings.append(pts)
        recs.append((rings, [name[:60]]))
    write("osm_real", shapefile.POLYGON, recs,
          [("name", "C", 60, 0)], encoding="utf-8", prj=WGS84, cpg="UTF-8")
    print(f"osm_real: {len(recs)} real polygons from PostGIS")
else:
    print("osm_real: NOT created (no rows)")

# Zip each set into an archive, as a user would.
for base in sorted({f.rsplit(".", 1)[0] for f in os.listdir(OUT)
                    if f.rsplit(".", 1)[-1] in ("shp", "shx", "dbf", "prj", "cpg")}):
    zpath = os.path.join(OUT, base + ".zip")
    with zipfile.ZipFile(zpath, "w", zipfile.ZIP_DEFLATED) as z:
        for ext in ("shp", "shx", "dbf", "prj", "cpg"):
            f = os.path.join(OUT, base + "." + ext)
            if os.path.exists(f):
                z.write(f, base + "." + ext)

# A zip bomb: one member that expands enormously from almost nothing.
with zipfile.ZipFile(os.path.join(OUT, "bomb.zip"), "w", zipfile.ZIP_DEFLATED) as z:
    z.writestr("bomb.dbf", b"\0" * (200 * 1024 * 1024))

# An archive whose members sit inside a folder.
with zipfile.ZipFile(os.path.join(OUT, "nested.zip"), "w", zipfile.ZIP_DEFLATED) as z:
    z.write(os.path.join(OUT, "points.shp"), "sub/points.shp")
    z.write(os.path.join(OUT, "points.dbf"), "sub/points.dbf")

# An archive containing another archive.
with zipfile.ZipFile(os.path.join(OUT, "russian_doll.zip"), "w", zipfile.ZIP_DEFLATED) as z:
    z.write(os.path.join(OUT, "points.zip"), "inner.zip")

print("corpus:", len([f for f in os.listdir(OUT)]), "files in", OUT)
for f in sorted(os.listdir(OUT)):
    if f.endswith(".zip"):
        print("   ", f, os.path.getsize(os.path.join(OUT, f)), "bytes")
