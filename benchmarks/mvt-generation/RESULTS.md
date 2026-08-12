# MVT Generation — Results

**Run:** 2026-08-12
**Settles:** A-019 (load-bearing), A-004, A-001; ADR-003 Alternative B
**Harness:** [`../harness`](../harness) · **Runner:** [`../run-tile-bench.ps1`](../run-tile-bench.ps1)

**These are the first measurements in this project.** Everything before them was
argument.

---

## Environment

| | |
|---|---|
| Host | Windows, 63.7 GB RAM, WSL2 capped at 15 GB |
| Database | PostgreSQL 16.4, PostGIS 3.4.3, GEOS 3.9.0, PROJ 7.2.1 (`NETWORK_ENABLED=OFF`) |
| Postgres config | `shared_buffers=1GB`, `work_mem=64MB`, `effective_cache_size=4GB` |
| Runtime | .NET 9, server GC, Release |
| Dataset | Geofabrik `turkey-latest.osm.pbf`, 611 MB, loaded by osm2pgsql 1.8.0 in 39m 49s |
| Table | `planet_osm_polygon` — **6,499,215 features, 77,089,382 vertices**, avg 11.9, max 215,488. All SRID 3857. 4,040 MB database. |

Warm measurements: three warm-up requests, then seven timed. Cold start is a
different experiment and mixing them would confound both.

## What was compared

| Path | What it is |
|---|---|
| **B** | `ST_AsMVT` — the PostGIS fast path, which **does not exist on SQL Server or Oracle** |
| **C** | In-process encoding, clipping with `NTS.Intersection` (the naive implementation, left in deliberately) |
| **C′** | In-process encoding, clipping with our own `RectClip` — bbox trivial-accept plus Sutherland–Hodgman |

## Results

Median milliseconds, and the stage breakdown for the in-process paths.

### z14 Istanbul, dense — 4,863 features

| Path | Median | Clip | Simplify | Transform | Encode | DB | Bytes |
|---|---|---|---|---|---|---|---|
| B `ST_AsMVT` | **62** | — | — | — | — | — | 195,958 |
| C NTS | 567 | **438.6** | 47.3 | 1.1 | 7.0 | 29.5 | 170,557 |
| C′ RectClip | **94** | **7.0** | 39.8 | 1.4 | 3.8 | 23.4 | 170,539 |

### z12 Istanbul, wide

| Path | Median | Clip | Simplify | Transform | Encode | DB | Bytes |
|---|---|---|---|---|---|---|---|
| B `ST_AsMVT` | **428** | — | — | — | — | — | 1,965,937 |
| C NTS | 6,200 | **4,727.9** | 881.8 | 68.6 | 99.7 | 108.5 | 1,604,319 |
| C′ RectClip | **1,471** | **30.7** | **807.6** | 31.4 | 132.4 | 128.6 | 1,604,170 |

### z16 Istanbul, close — sparse

| Path | Median | Clip | Simplify | Transform | Encode | DB | Bytes |
|---|---|---|---|---|---|---|---|
| B `ST_AsMVT` | **12** | — | — | — | — | — | 12,964 |
| C NTS | 417 | **348.5** | 3.3 | 13.9 | 0.6 | 19.2 | 12,344 |
| C′ RectClip | **48** | **9.5** | 3.4 | 0.07 | 0.2 | 12.0 | 12,368 |

## Findings

### 1. A-019 passes — in-process MVT encoding is viable

At the dense tile, **94 ms against 62 ms** for the PostGIS fast path. A 1.5×
gap, not an order of magnitude. With the cache absorbing repeat requests
(ADR-010), that is comfortably serviceable.

The multi-database promise is **not** hollow. Oracle and SQL Server can serve
tiles.

**With one caveat that must not be buried:** at z12 it is 1,471 ms against 428
ms — 3.4×, and 1.5 seconds is poor even when cached, because seeding pays it
too. Low zoom needs more work. See finding 4.

### 2. A-004 validated, decisively — and it was not close

> *Hot-path geometry overhead is material enough to justify our own primitives.*

`NTS.Intersection` was **79% of the entire request** at z14 and **76%** at z12.
Replacing it with a rectangle clipper took that stage from **438.6 ms to 7.0 ms
— 63× faster** — and cut the whole request 6×.

The reason is structural rather than a matter of tuning. `Intersection` runs
general polygon-polygon overlay — robust predicates, snap-rounding, the whole
OverlayNG machinery — to clip against an axis-aligned rectangle. And in a dense
urban tile most buildings are entirely *inside* the tile, so the correct answer
for them is a bounding-box comparison and no clipping at all.

### 3. What we wrote is fast. What we adopted was slow.

The most useful line in the whole run:

| Component | Origin | z14 cost |
|---|---|---|
| Tile-space transform | **ours** | 1.4 ms |
| MVT encoder | **ours** | 3.8 ms |
| Rectangle clip | **ours** | 7.0 ms |
| Douglas–Peucker simplify | NTS | 39.8 ms |
| ~~General intersection~~ | ~~NTS~~ | ~~438.6 ms~~ |

**ADR-003 Alternative B is validated by measurement rather than by preference.**
Own hot-path primitives, adopted topology — and the boundary between the two is
now empirically placed rather than argued.

It also vindicates the build-vs-adopt tiering: MVT encoding was put in Tier 1
because the format is small and it is the hottest path. It costs 3.8 ms.

### 4. The bottleneck moved — simplify is next

After fixing the clip, at z12 **simplify is 807.6 ms of 1,471 ms — 55%.**

That is NTS `DouglasPeuckerSimplifier`, which preserves more than a tile needs.
The same argument that justified `RectClip` applies again, and it is now the
single largest remaining cost on the tile path.

Not done here, because the finding is worth recording before acting on it.

### 5. Output validated, not merely fast

An encoder that is fast and wrong is worthless, so both tiles were decoded and
structurally compared:

| | Ours | PostGIS |
|---|---|---|
| layer / version / extent | `polygons` / 2 / 4096 | `polygons` / 2 / 4096 |
| features | 4,854 | 4,725 |
| geometry types | polygon × 4,854 | polygon × 4,725 |
| moveto / closepath | 4,896 / 4,896 | 4,766 / 4,766 |
| malformed geometry | **0** | **0** |

Every ring opened is closed. Byte sizes differ by ~13% and feature counts by
129, both explained: we query the buffered envelope so we carry a few more
boundary features, and we place `osm_id` in the feature id rather than as a tag,
which is why our value table is 358 entries against 5,071.

**The C and C′ outputs are 170,557 and 170,539 bytes** — an 18-byte difference
across 4,854 features. Our clipper and NTS agree.

## What this does not show

Stated plainly, because the temptation with a first good result is to over-claim.

- **Only PostGIS was measured.** Endpoint C exists *because* SQL Server and
  Oracle lack `ST_AsMVT`, and neither has been tested. The numbers here are the
  in-process path running against the one database that does not need it.
- **One machine, one dataset, one city.** Turkish OSM data at three zooms.
- **Warm only.** No cold start, no cache-miss storm, no concurrency, no p99
  under load — this is single-request latency.
- **No visual verification.** The tiles decode and are structurally sound;
  nobody has looked at them rendered.
- **Sutherland–Hodgman has a known limitation** — it can emit degenerate
  connecting edges along the boundary for concave polygons. Tile renderers
  tolerate this. It would be unacceptable for analytical overlay, which is
  exactly why topology stays with NTS.
- **The measurement harness had a bug first time round.** A PowerShell header
  read indexed into a string and returned ASCII character codes, which produced
  a confident and entirely wrong table. Caught because a feature count of 52
  was implausible. Worth remembering: the harness needs the same scepticism as
  the thing it measures.

## Next

1. **Simplify** — the new bottleneck at low zoom (finding 4).
2. **SQL Server and Oracle** — the providers this path exists for.
3. **Concurrency and p99**, which is what ADR-007's worker model actually needs.
4. **Visual verification** of a rendered tile.
