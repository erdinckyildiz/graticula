# Q-68 widened again — a road network, mixed geometry, and a second machine — ADR-021 condition 2, 2026-10-16

The run of 2026-10-15 (`q68-widened-2026-10-15.md`) left three things by name: the lines were building outlines
and boundaries rather than a road network, no table mixed geometry types, and everything ran on one machine.

**Machines.** *Local*: the owner's Windows workstation, portable PostgreSQL 16 + PostGIS on port 55499, database
`bench` — the one the 2026-10-15 run used. *VPS*: the showcase's arm64 host (6 cores), a throwaway container of the
showcase's own datastore image (`graticula-datastore:1.0.401`, PostgreSQL 16 + PostGIS) on its loopback, the harness
run with the host's .NET 9 runtime; the container was removed afterwards and the showcase's database was not used.

**Data.**
- `q68_roads`: Geofabrik's Turkey `gis_osm_roads_free_1` (2026-10-15 extract), **2,666,291** lines, loaded with
  `shp2pgsql -s 4326 -W UTF-8 -D -g g4326` and transformed: `create table q68_roads as select osm_id::bigint, name,
  fclass, ref, st_transform(g4326, 3857)::geometry(MultiLineString, 3857) as geom from q68_roads_raw` + a GiST index.
- `q68_mixed`: the 927,350 polygons of `q68_polygons`, the 299,669 roads inside Istanbul's box (27.9–29.95 E,
  40.7–41.6 N), and a point on each polygon (`st_pointonsurface`) — 2,154,369 features of three types in one
  `geometry(Geometry, 3857)` column. **The ids are a `row_number()`**: built from OSM ids, 575 collided (a way and a
  relation may share a number), and `ST_AsMVT` given duplicate feature ids wrote different tiles from the read-once
  path, which the harness reported as a divergence — the table's fault, not either path's.

Run, both machines, both concurrencies: `GisBench q68 --table <t> --geom geom --attrs name --parents 4 --rounds 3
--child 14 --production --conc <1|4>` (Istanbul's densest four z12 tiles → z14, 64 tiles a round). Every row below
reports *the two paths agree on what a tile contains, so the timing is comparable*.

| table | machine | conc | ST_AsMVT tiles/s | read once tiles/s | read once ÷ ST_AsMVT | MB/tile ST_AsMVT vs read once | GC pause, read once |
|---|---|---|---|---|---|---|---|
| roads | local | 1 | 425.4 | 198.6 | **0.47×** | 0.03 vs 3.51 | 35 ms |
| roads | local | 4 | 1,626.7 | 367.6 | **0.23×** | 0.03 vs 3.70 | 48 ms |
| roads | VPS | 1 | 198.6 | 104.4 | **0.53×** | 0.03 vs 3.55 | 126 ms |
| roads | VPS | 4 | 748.3 | 226.9 | **0.30×** | 0.03 vs 3.70 | 65 ms |
| polygons | VPS | 1 | 22.9 | 29.0 | 1.27× | 0.11 vs 19.37 | 564 ms |
| polygons | VPS | 4 | 51.8 | 64.5 | 1.25× | 0.11 vs 19.37 | 382 ms |
| mixed | local | 1 | 11.2 | 38.1 | 3.39× | 0.19 vs 32.82 | 351 ms |
| mixed | local | 4 | 33.2 | 76.8 | 2.31× | 0.19 vs 32.82 | 277 ms |
| mixed | VPS | 1 | 15.7 | 19.2 | 1.22× | 0.18 vs 32.82 | 767 ms |
| mixed | VPS | 4 | 36.4 | 34.1 | 0.94× | 0.19 vs 32.82 | 722 ms |

The local polygons for comparison (2026-10-15, `--production`): 11.2 against 51.0 tiles/s, **4.6×** at concurrency 1.

**What this says.**
- **The throughput of reading once is the machine's and the data's, not the method's.** On the owner's workstation
  it was 2 to 5 times `ST_AsMVT` on polygons; on the arm64 host the same polygons give 1.25×, and mixed geometry
  falls to 0.94× at concurrency 4. **On a road network it loses on both machines**, by two to four times: road
  tiles are small (about 25 KB), `ST_AsMVT` answers each in a few milliseconds, and the read-once path pays its
  allocation and its collector for nothing.
- **The allocation holds everywhere**: 3.5 to 33 MB a tile read once against 0.03 to 0.19 MB, and the collector's
  pause up to three quarters of a second a round on the VPS. That is A-037 and what ADR-021 rests on.
- So the condition's *widen before treating the numbers as general* is answered: the number that generalises is
  the one the decision was taken on, and the one that does not — throughput — does not generalise in the direction
  that would argue for reopening it. Q-173's answer (a read-once *seeder*, if seeding time is ever the complaint)
  stands, and a road network is where it would gain nothing.
