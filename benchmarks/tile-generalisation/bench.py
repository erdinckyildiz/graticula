"""Q-157: does generalising in the tile statement bound a low-zoom tile, and what does it cost?

The owner's decision of 2026-09-23: simplify by zoom in the statement PostGisTileSource runs, and drop
polygons smaller than a pixel. This measures statements over the 927,350 Istanbul polygons of
benchmarks/nextgis-mvt, on tiles from z10 to z16:

  B    the statement as it was: ST_AsMVTGeom + ST_AsMVT, nothing dropped, nothing simplified.
  S1   B, leaving out a feature whose box is smaller than a pixel both ways — a pixel being the tile's
       width over 512, the size an ArcGIS or MapLibre client draws a vector tile at.
  S1b  S1 with the box read once, in a lateral.
  S2   S1, and ST_Simplify at one tile unit (the tile's width over 4096), the grid ST_AsMVTGeom snaps to.
  S2b  S2 with the box read once.
  S3   S1, and ST_Simplify at half a pixel (four tile units).
  P    the statement shipped (ADR-085): S1's rule with points exempt, S3's simplification through z14.

Three warm-ups, then fifteen runs of every statement interleaved — one of each per round — and the median;
what a client receives is read back with GDAL's MVT reader: features, vertices, invalid geometries.

Usage:  python bench.py "host=localhost port=55501 dbname=bench user=postgres" [B,S1,S3,...]
"""
import statistics
import sys
import time
import uuid

import psycopg
from osgeo import gdal

gdal.UseExceptions()

EXTENT = 4096
BUFFER = 64
PIXELS = 512
TABLE = "polygons"
ATTRS = ["osm_id", "name", "building"]

TILES = [
    ("z10 Istanbul", 10, 594, 383),
    ("z12 Istanbul (wide)", 12, 2377, 1535),
    ("z13 Istanbul", 13, 4755, 3071),
    ("z14 Istanbul (dense)", 14, 9510, 6142),
    ("z15 Istanbul", 15, 19020, 12285),
    ("z16 Istanbul (close)", 16, 38041, 24570),
]

WARM = 3
RUNS = 15


def statement(variant):
    cols = "".join(f", t.{c}" for c in ATTRS)

    # The tile's width in metres, once, from the same envelope ST_AsMVTGeom uses.
    span = "(ST_XMax(bounds.geom) - ST_XMin(bounds.geom))"

    # Four calls on the geometry read it four times; one box2d read once is what S1b and S2b test.
    small = (
        f" and (ST_XMax(t.geom) - ST_XMin(t.geom) >= {span} / {PIXELS}"
        f"   or ST_YMax(t.geom) - ST_YMin(t.geom) >= {span} / {PIXELS})"
    )

    small_once = (
        f" and (ST_XMax(k.b) - ST_XMin(k.b) >= {span} / {PIXELS}"
        f"   or ST_YMax(k.b) - ST_YMin(k.b) >= {span} / {PIXELS})"
    )

    geometry = {
        "B": "t.geom",
        "S1": "t.geom",
        "S1b": "t.geom",
        "S2": f"ST_Simplify(t.geom, {span} / {EXTENT}, true)",
        "S2b": f"ST_Simplify(t.geom, {span} / {EXTENT}, true)",
        "S3": f"ST_Simplify(t.geom, {span} / {EXTENT} * 4, true)",
        "P": "t.geom",
    }[variant]

    if variant == "P":
        # <b>The statement PostGisTileSource runs after Q-157</b>, spelled as its Generalised and
        # LargeEnough write it: the output geometry once in a lateral, a point never left out, and half a
        # pixel of simplification through z14.
        g = "o.g"
        large = (f"(ST_Dimension({g}) = 0 or ST_XMax({g}) - ST_XMin({g}) >= {span} / {PIXELS}"
                 f" or ST_YMax({g}) - ST_YMin({g}) >= {span} / {PIXELS})")
        general = f"case when %s <= 14 then ST_Simplify({g}, {span} / {PIXELS * 2}, true) else {g} end"
        return f"""
            with bounds as (select ST_TileEnvelope(%s, %s, %s) as geom),
            tile as (
                select ST_AsMVTGeom({general}, bounds.geom, {EXTENT}, {BUFFER}, true) as geom{cols}
                from {TABLE} t, bounds, lateral (select t.geom as g) o
                where t.geom && bounds.geom and {large}
            )
            select ST_AsMVT(tile.*, 'polygons', {EXTENT}, 'geom') from tile
        """

    once = variant.endswith("b")
    where = "" if variant == "B" else (small_once if once else small)
    lateral = ", lateral (select box2d(t.geom) as b) k" if once else ""

    return f"""
        with bounds as (select ST_TileEnvelope(%s, %s, %s) as geom),
        tile as (
            select ST_AsMVTGeom({geometry}, bounds.geom, {EXTENT}, {BUFFER}, true) as geom{cols}
            from {TABLE} t, bounds{lateral}
            where t.geom && bounds.geom{where}
        )
        select ST_AsMVT(tile.*, 'polygons', {EXTENT}, 'geom') from tile
    """


def run(conn, variant, z, x, y):
    sql = statement(variant)
    t0 = time.perf_counter()
    with conn.cursor() as cur:
        # P names the zoom a second time, for its simplification rule, as the production statement does.
        cur.execute(sql, (z, x, y, z) if variant == "P" else (z, x, y))
        data = bytes(cur.fetchone()[0] or b"")
    return data, (time.perf_counter() - t0) * 1000


def inspect(data, z, x, y):
    if not data:
        return {"features": 0, "invalid": 0, "vertices": 0}

    name = f"/vsimem/{uuid.uuid4()}.pbf"
    gdal.FileFromMemBuffer(name, data)

    try:
        try:
            ds = gdal.OpenEx(name, gdal.OF_VECTOR, allowed_drivers=["MVT"],
                             open_options=[f"Z={z}", f"X={x}", f"Y={y}", "CLIP=NO"])
        except RuntimeError:
            # GDAL's MVT reader will not open a tile past its own size bound; the bytes still count.
            return {"features": "unread", "invalid": "unread", "vertices": "unread"}

        n = invalid = vertices = 0

        def points(g):
            if g.GetGeometryCount() == 0:
                return g.GetPointCount()
            return sum(points(g.GetGeometryRef(k)) for k in range(g.GetGeometryCount()))

        for i in range(ds.GetLayerCount()):
            for f in ds.GetLayer(i):
                n += 1
                g = f.GetGeometryRef()
                if g is not None:
                    invalid += 0 if g.IsValid() else 1
                    vertices += points(g)

        return {"features": n, "invalid": invalid, "vertices": vertices}
    finally:
        gdal.Unlink(name)


def main():
    dsn = sys.argv[1] if len(sys.argv) > 1 else "host=localhost port=55501 dbname=bench user=postgres"

    with psycopg.connect(dsn) as conn:
        with conn.cursor() as cur:
            cur.execute("select postgis_full_version()")
            print(cur.fetchone()[0].split(" LIBXML")[0])

        print("| Tile | Statement | Median ms | Bytes | Features | Vertices | Invalid |")
        print("|---|---|---|---|---|---|---|")

        variants = tuple(sys.argv[2].split(",")) if len(sys.argv) > 2 else ("B", "S1", "S1b", "S2", "S2b", "S3")

        for label, z, x, y in TILES:
            for variant in variants:
                for _ in range(WARM):
                    run(conn, variant, z, x, y)

            # <b>Interleaved</b>: one run of each variant per round, so drift over the minutes a tile takes
            # lands on every variant alike rather than on whichever ran last.
            times = {v: [] for v in variants}
            last = {}

            for _ in range(RUNS):
                for variant in variants:
                    data, ms = run(conn, variant, z, x, y)
                    times[variant].append(ms)
                    last[variant] = data

            for variant in variants:
                data = last[variant]
                shape = inspect(data, z, x, y)

                def n(value):
                    return f"{value:,}" if isinstance(value, int) else value

                print(f"| {label} | {variant} | {statistics.median(times[variant]):,.0f} "
                      f"| {len(data):,} | {n(shape['features'])} | {n(shape['vertices'])} | {shape['invalid']} |",
                      flush=True)


if __name__ == "__main__":
    main()
