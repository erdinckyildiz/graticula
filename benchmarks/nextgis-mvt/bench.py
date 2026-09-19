"""ST_AsMVT against the shape of NextGIS Web's tile path, on the same tiles and rows.

Answers one question from the 2026-09-19 NextGIS Web comparison: is the claim that
ADR-021's in-database encoding is faster than NextGIS Web's GDAL-based one true, and by
how much?

Two paths, each timed end to end from this client:

  B  the statement PostGisTileSource runs: ST_AsMVTGeom + ST_AsMVT, one round trip,
     the encoded tile comes back as bytea.

  N  NextGIS Web's shape, reconstructed from reading its public source
     (feature_layer/api_mvt.py, vector_layer/feature_query.py): the database clips
     with ST_ClipByBox2D to the tile padded by 5%, simplifies with
     ST_SimplifyPreserveTopology at extent/512 tile units, returns WKB; the client
     builds OGR features and GDAL's MVT driver encodes them into /vsimem.

N is a lower bound for NextGIS Web, not a copy of it: NextGIS Web also builds its own
Python feature objects from SQLAlchemy rows before handing them to OGR, and that is not
done here. Nothing below is NextGIS Web code; the SQL functions and the driver options
are the published behaviour the comparison describes.

Usage:  python bench.py "host=localhost port=55499 dbname=bench user=postgres"
"""

import statistics
import sys
import time
import uuid

import psycopg
from osgeo import gdal, ogr, osr

gdal.UseExceptions()

EXTENT = 4096
BUFFER = 64
WORLD = 20037508.342789244
TABLE = "polygons"
ATTRS = ["osm_id", "name", "building"]

TILES = [
    ("z14 Istanbul (dense)", 14, 9510, 6142),
    ("z12 Istanbul (wide)", 12, 2377, 1535),
    ("z16 Istanbul (close)", 16, 38041, 24570),
]

WARM = 3
RUNS = 7


def tile_bounds(z, x, y):
    size = 2 * WORLD / (1 << z)
    minx = -WORLD + x * size
    maxy = WORLD - y * size
    return minx, maxy - size, minx + size, maxy


def path_b(conn, z, x, y):
    cols = "".join(f", t.{c}" for c in ATTRS)
    sql = f"""
        with bounds as (select ST_TileEnvelope(%s, %s, %s) as geom),
        tile as (
            select ST_AsMVTGeom(t.geom, bounds.geom, {EXTENT}, {BUFFER}, true) as geom{cols}
            from {TABLE} t, bounds
            where t.geom && bounds.geom
        )
        select ST_AsMVT(tile.*, 'polygons', {EXTENT}, 'geom') from tile
    """
    t0 = time.perf_counter()
    with conn.cursor() as cur:
        cur.execute(sql, (z, x, y))
        data = bytes(cur.fetchone()[0])
    t1 = time.perf_counter()
    return data, {"total": (t1 - t0) * 1000, "db": (t1 - t0) * 1000, "encode": 0.0}


SRS = osr.SpatialReference()
SRS.ImportFromEPSG(3857)


def path_n(conn, z, x, y):
    minx, miny, maxx, maxy = tile_bounds(z, x, y)
    w, h = maxx - minx, maxy - miny
    pad = 0.05
    box = (minx - w * pad, miny - h * pad, maxx + w * pad, maxy + h * pad)
    simplification = EXTENT / 512
    tolerance = ((2 * WORLD) / (1 << z)) / EXTENT * simplification

    sql = f"""
        select {", ".join(ATTRS)},
               st_asbinary(st_simplifypreservetopology(
                   st_clipbybox2d(st_force2d(geom), st_makeenvelope(%s, %s, %s, %s, 3857)), %s))
        from {TABLE}
        where geom && st_makeenvelope(%s, %s, %s, %s, 3857)
    """
    t0 = time.perf_counter()
    with conn.cursor() as cur:
        cur.execute(sql, (*box, tolerance, *box))
        rows = cur.fetchall()
    t1 = time.perf_counter()

    path = f"/vsimem/{uuid.uuid4()}"
    ds = ogr.GetDriverByName("MVT").CreateDataSource(
        path,
        options=[
            "FORMAT=DIRECTORY",
            "TILE_EXTENSION=pbf",
            f"MINZOOM={z}",
            f"MAXZOOM={z}",
            f"EXTENT={EXTENT}",
            "COMPRESS=NO",
        ],
    )
    layer = ds.CreateLayer("polygons", srs=SRS, geom_type=ogr.wkbMultiPolygon)
    layer.CreateField(ogr.FieldDefn("osm_id", ogr.OFTString))
    layer.CreateField(ogr.FieldDefn("name", ogr.OFTString))
    layer.CreateField(ogr.FieldDefn("building", ogr.OFTString))
    defn = layer.GetLayerDefn()
    for osm_id, name, building, wkb in rows:
        f = ogr.Feature(defn)
        f.SetField(0, osm_id)
        f.SetField(1, name)
        f.SetField(2, building)
        if wkb is not None:
            f.SetGeometryDirectly(ogr.CreateGeometryFromWkb(bytes(wkb)))
        layer.CreateFeature(f)
    ds = None  # flush: the driver writes the tile here

    fh = gdal.VSIFOpenL(f"{path}/{z}/{x}/{y}.pbf", "rb")
    data = b""
    if fh is not None:
        gdal.VSIFSeekL(fh, 0, 2)
        size = gdal.VSIFTellL(fh)
        gdal.VSIFSeekL(fh, 0, 0)
        data = bytes(gdal.VSIFReadL(1, size, fh))
        gdal.VSIFCloseL(fh)
    gdal.RmdirRecursive(path)
    t2 = time.perf_counter()
    return data, {"total": (t2 - t0) * 1000, "db": (t1 - t0) * 1000, "encode": (t2 - t1) * 1000,
                  "rows": len(rows)}


def inspect(data, z, x, y):
    """Decode a tile with GDAL's reader: features, geometry types, invalid geometries."""
    if not data:
        return {"features": 0}
    name = f"/vsimem/{uuid.uuid4()}.pbf"
    gdal.FileFromMemBuffer(name, data)
    try:
        ds = gdal.OpenEx(name, gdal.OF_VECTOR, allowed_drivers=["MVT"],
                         open_options=[f"Z={z}", f"X={x}", f"Y={y}", "CLIP=NO"])
        n = invalid = 0
        vertices = 0
        for i in range(ds.GetLayerCount()):
            lyr = ds.GetLayer(i)
            for f in lyr:
                n += 1
                g = f.GetGeometryRef()
                if g is not None:
                    if not g.IsValid():
                        invalid += 1
                    vertices += g.GetPointCount() if g.GetGeometryCount() == 0 else sum(
                        g.GetGeometryRef(k).GetPointCount() if g.GetGeometryRef(k).GetGeometryCount() == 0 else sum(
                            g.GetGeometryRef(k).GetGeometryRef(r).GetPointCount()
                            for r in range(g.GetGeometryRef(k).GetGeometryCount()))
                        for k in range(g.GetGeometryCount()))
        return {"features": n, "invalid": invalid, "vertices": vertices}
    finally:
        gdal.Unlink(name)


def bench(fn, conn, z, x, y):
    for _ in range(WARM):
        fn(conn, z, x, y)
    samples = [fn(conn, z, x, y) for _ in range(RUNS)]
    totals = [s[1]["total"] for s in samples]
    data, last = samples[-1]
    return {
        "median": statistics.median(totals),
        "min": min(totals),
        "max": max(totals),
        "db": statistics.median(s[1]["db"] for s in samples),
        "encode": statistics.median(s[1]["encode"] for s in samples),
        "bytes": len(data),
        "rows": last.get("rows"),
        "shape": inspect(data, z, x, y),
    }


def main():
    dsn = sys.argv[1]
    with psycopg.connect(dsn, autocommit=True) as conn:
        with conn.cursor() as cur:
            cur.execute("select postgis_full_version(), version()")
            pg = cur.fetchone()
            cur.execute(f"select count(*) from {TABLE}")
            count = cur.fetchone()[0]
        print(f"GDAL {gdal.__version__}")
        print(pg[1].split(",")[0])
        print(pg[0][:120])
        print(f"{TABLE}: {count:,} features")
        print()
        print("| Tile | Path | Median ms | Min | Max | DB ms | Encode ms | Bytes | Rows read | Features in tile | Invalid | Vertices |")
        print("|---|---|---|---|---|---|---|---|---|---|---|---|")
        for label, z, x, y in TILES:
            for name, fn in (("B ST_AsMVT", path_b), ("N GDAL MVT (NextGIS shape)", path_n)):
                r = bench(fn, conn, z, x, y)
                s = r["shape"]
                print(f"| {label} | {name} | {r['median']:.0f} | {r['min']:.0f} | {r['max']:.0f} | "
                      f"{r['db']:.0f} | {r['encode']:.0f} | {r['bytes']:,} | {r['rows'] or '—'} | "
                      f"{s.get('features')} | {s.get('invalid', '—')} | {s.get('vertices', '—')} |")


if __name__ == "__main__":
    main()
