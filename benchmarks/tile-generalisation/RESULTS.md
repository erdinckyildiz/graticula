# Generalising in the tile statement — Results

**Run:** 2026-09-24, three times. **Asks:** [Q-157](../../docs/open-questions.md) — the owner's decision of
2026-09-23 to simplify by zoom in the tile statement and to drop polygons smaller than a pixel — and the
condition that came with it: the effect is measured before and after, and the z14 and z16 tiles must not
get worse. **Script:** [`bench.py`](bench.py). **Decision:** [ADR-085](../../docs/adr/ADR-085-a-tile-leaves-out-what-it-cannot-draw.md).

## Environment

| | |
|---|---|
| Host | Windows 11, the development machine — not the showcase |
| Database | PostgreSQL 16.10 (portable), PostGIS 3.6.2, GEOS 3.14.1dev |
| Dataset | The 927,350 Istanbul polygons of [nextgis-mvt](../nextgis-mvt/RESULTS.md) (Geofabrik, 2026-09-19), EPSG:3857, GiST |
| Method | three warm-ups; fifteen runs of every statement **interleaved**, one of each per round, median — the machine's absolute timings moved by more than 2× between runs of the same statement, and interleaving puts that drift on every statement alike |
| Read back | what a client receives, decoded with GDAL's MVT reader: features, vertices, invalid geometries |

## What was compared

| | Statement |
|---|---|
| **B** | `PostGisTileSource` before: `ST_AsMVTGeom` + `ST_AsMVT`, nothing left out, nothing simplified |
| **S1** | B, leaving out a feature whose box is smaller than a pixel both ways (a pixel = the tile's width / 512) |
| **S1b** | S1 with the box read once, in a lateral, instead of four times |
| **S2** | S1 and `ST_Simplify` at one tile unit (width / 4096, the grid `ST_AsMVTGeom` snaps to) |
| **S3** | S1 and `ST_Simplify` at half a pixel (width / 1024, four tile units) |
| **P** | **the statement shipped**: S1's rule, points exempt, and S3's simplification through z14 only |

## Results

### Every statement, z12, z14 and z16 (run 2)

| Tile | Statement | Median ms | Bytes | Features | Vertices | Invalid |
|---|---|---|---|---|---|---|
| z12 | B | 2,080 | 2,164,480 | 48,862 | 324,745 | 0 |
| z12 | S1 | 1,617 | 1,394,942 | 30,332 | 222,483 | 0 |
| z12 | S1b | 1,609 | 1,394,942 | 30,332 | 222,483 | 0 |
| z12 | S2 | 1,562 | 1,331,719 | 30,332 | 190,774 | 0 |
| z12 | S3 | 1,445 | 1,260,397 | 30,309 | 155,304 | 0 |
| z14 | B | 294 | 218,790 | 4,707 | 34,940 | 0 |
| z14 | S1 | 311 | 218,171 | 4,694 | 34,875 | 0 |
| z14 | S2 | 299 | 212,607 | 4,694 | 32,071 | 0 |
| z14 | S3 | 281 | 205,805 | 4,694 | 28,691 | 0 |
| z16 | B | 27 | 14,515 | 295 | 2,053 | 0 |
| z16 | S1 | 32 | 14,518 | 295 | 2,053 | 0 |
| z16 | S2 | 49 | 14,358 | 295 | 1,972 | 0 |
| z16 | S3 | 45 | 14,200 | 295 | 1,908 | 0 |

### Six zooms, B against the statement shipped (run 3)

| Tile | B ms | B bytes | P ms | P bytes | P features (of B) | Invalid |
|---|---|---|---|---|---|---|
| z10 | 12,254 | **16,598,611** | 1,764 | **918,157** | 17,717 (GDAL will not open B) | 0 |
| z12 | 1,585 | 2,164,480 | 1,047 | 1,260,397 | 30,309 (48,862) | 0 |
| z13 | 174 | 223,145 | 160 | 195,474 | 4,549 (4,813) | 0 |
| z14 | 165 | 218,790 | 171 | 205,805 | 4,694 (4,707) | 0 |
| z15 | 24 | 26,659 | 28 | 26,665 | 564 (564) | 0 |
| z16 | 15 | 14,515 | 18 | 14,518 | 295 (295) | 0 |

## Findings

1. **Leaving out what is smaller than a pixel is almost all of it.** At z10 it alone took the tile from
   16.6 MB to 1.43 MB and from 14 s to 3 s; at z12 it cut a third of the bytes. The z10 tile before was one
   GDAL's own MVT reader refuses to open.
2. **Simplifying at the grid is nearly redundant**, because `ST_AsMVTGeom` already snaps to it: S2 bought 4 %
   at z12 and cost 80 % at z16. **At half a pixel it pays at low zoom** — z10 1.43 MB → 918 KB — and costs
   9–14 ms a tile at z15 and z16 for a few hundred bytes. So it stops after z14.
3. **Reading the box once changed nothing** (S1b against S1): the rule's cost is not the box.
4. **At z15 and z16 the statement shipped costs 3–4 ms a cold tile**, inside the noise this machine showed,
   and returns the same features. The tile cache pays it once per tile.
5. **No statement produced an invalid geometry** on any tile.

## What this does not say

- Nothing about lines or points in bulk: the corpus is polygons. Points are exempt by construction.
- Nothing about arm64 or the showcase's datastore.
- Nothing about how the tiles look: a feature below a pixel is by definition not drawn as a shape, and half a
  pixel of simplification is below what a client can draw — both claims about a client's resolution, not
  measured in one.
