# ST_AsMVT against NextGIS Web's tile path — Results

**Run:** 2026-09-19, twice. **Asks:** the NextGIS Web comparison
([research/nextgis-web-comparison.md](../../docs/research/nextgis-web-comparison.md)) said that
[ADR-021](../../docs/adr/ADR-021-tile-encoding.md)'s in-database encoding is faster than NextGIS Web's
GDAL-based one, and the owner asked for it to be checked. **Script:** [`bench.py`](bench.py).

## Environment

| | |
|---|---|
| Host | Windows 11, the development machine — not the showcase |
| Database | PostgreSQL 16.10 (portable, native Windows), PostGIS 3.6.2, GEOS 3.14.1dev, `shared_buffers=1GB`, `work_mem=64MB` |
| Client | Python 3.12, GDAL 3.13.3 (conda-forge), psycopg 3, on the same machine |
| Dataset | Geofabrik `turkey-latest.osm.pbf` downloaded 2026-09-19, the `multipolygons` layer inside 28.0–30.0 E, 40.7–41.6 N (Istanbul and around), read with GDAL's OSM driver into EPSG:3857 — **927,350 polygons**, a GiST index, `vacuum analyze` |
| Tiles | the three from [mvt-generation](../mvt-generation/RESULTS.md): z14 9510/6142 (dense), z12 2377/1535 (wide), z16 38041/24570 (close) |
| Method | three warm-up requests, seven timed, median; both paths timed end to end from the same client |

**Not comparable with the August runs** in `mvt-generation`: a different PostgreSQL, PostGIS and GEOS,
native Windows rather than WSL2, and a different extract. Only the two paths *within* this run compare.

## What was compared

| Path | What it is |
|---|---|
| **B** | The statement `PostGisTileSource` runs: `ST_AsMVTGeom` with a 64-unit buffer and `ST_AsMVT`, one round trip, the tile comes back as `bytea` |
| **N** | NextGIS Web's shape, **reconstructed from reading its public source** (`feature_layer/api_mvt.py`, `vector_layer/feature_query.py`, GPL-3.0; nothing copied): the database clips each geometry with `ST_ClipByBox2D` to the tile padded by 5 % and simplifies with `ST_SimplifyPreserveTopology` at `extent/512` tile units; the client builds OGR features from the WKB and GDAL's MVT driver writes the tile into `/vsimem` with the same options NextGIS Web passes |

**N is a lower bound for NextGIS Web, not a copy of it.** NextGIS Web also turns each SQLAlchemy row into
its own Python feature object before handing it to OGR; that step is not here, so NextGIS Web itself would
be slower than N, not faster. Both carry the same three attributes (`osm_id`, `name`, `building`).

## Results

Run 1 (run 2 in brackets, where it differs by more than a few per cent).

| Tile | Path | Median ms | DB ms | Encode ms | Bytes | Features in tile | Invalid | Vertices |
|---|---|---|---|---|---|---|---|---|
| z14 dense | **B** `ST_AsMVT` | **177** (178) | 177 | — | 220,289 | 4,707 | 0 | 34,940 |
| z14 dense | **N** GDAL MVT | **567** (560) | 179 | 378 | 208,089 | 4,878 | 0 | 26,846 |
| z12 wide | **B** `ST_AsMVT` | **2,635** (2,816) | 2,635 | — | **2,164,480** | 48,862 | 0 | 324,745 |
| z12 wide | **N** GDAL MVT | **7,450** (7,550) | 1,030 | 6,504 | 465,789 | **11,490** | **3** | 52,715 |
| z16 close | **B** `ST_AsMVT` | **24** (30) | 24 | — | 14,515 | 295 | 0 | 2,053 |
| z16 close | **N** GDAL MVT | **54** (56) | 16 | 39 | 15,514 | 331 | 0 | 1,997 |

*Invalid* and *Vertices* are read back from the encoded tile with GDAL's MVT reader, so they describe what
a client receives, not what either path intended.

## Findings

### 1. The claim holds: ST_AsMVT is 2.2–3.2× faster on every tile

z14 **177 against 567 ms (3.2×)**, z12 **2,635 against 7,450 ms (2.8×)**, z16 **24 against 54 ms (2.3×)**,
and the second run agrees within a few per cent. The database half of N is no slower than B's — at z12 it
is less than half, because N simplifies before it returns anything — and **the whole difference is the
encode step outside the database**: 378 ms at z14 and 6.5 s at z12 to turn rows into a tile, against
nothing for B, whose encode happens inside the same statement that reads the rows. That is what ADR-021's
fourth run found against our own in-process encoder, measured again against a different one.

### 2. At z12 the two paths are not producing the same thing — and ours is the worse tile

N's tile is **465,789 bytes with 11,490 features**; B's is **2,164,480 bytes with 48,862**. GDAL printed,
on every z12 request: *"At least one tile exceeded the default maximum tile size of 500000 bytes and was
encoded at lower resolution"* — its MVT writer has a size ceiling and, past it, coarsens the tile and
drops features until it fits. Three of the geometries it emitted are invalid.

So at low zoom NextGIS Web answers slower with a tile that is bounded but lossy, and **Graticula answers
faster with a 2.1 MB tile that nothing bounds.** Neither is good. A 2 MB vector tile is four times the
size the MVT tooling treats as a ceiling, and 2.6 s is the cost of the first request for it; the tile
cache pays it once per tile, and a map opened at z12 over a city pays it for every tile in view.

**This is the question [Q-34](../../docs/open-questions.md) said was "now its own row" on 2026-09-09 —
*does the tile path generalise at all, and by statement or by table?* — and that row was never written.**
It is written now as [Q-157](../../docs/open-questions.md), with these numbers as its first evidence.

### 3. What this does not say

- Nothing about NextGIS Web's cached tiles, which is what its users mostly receive; this is the cost of
  making a tile, not of serving one.
- Nothing about arm64 or the showcase's datastore.
- Nothing about NextGIS Web's raster path, which renders through QGIS and was not compared.
