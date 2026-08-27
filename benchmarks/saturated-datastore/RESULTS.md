# Tile encoding against a datastore that is out of CPU

**Run 2026-08-27.** **Settles:** [ADR-021](../../docs/adr/ADR-021-tiles-are-encoded-by-postgis.md)
condition 1. **Runner:** [`run.sh`](run.sh) · **Harness:** [`../harness`](../harness), unchanged
since 2026-08-12.

---

## The question, in the condition's own words

> **The datastore-saturation case is untested and is the strongest surviving argument against
> this decision.** `ST_AsMVT` puts every byte of tile cost inside PostgreSQL. On the benchmark
> machine PostgreSQL had headroom, so the comparison never saw the case where it does not …
> Moving encode to a tier that scales horizontally is an argument no single-box benchmark can
> refute. **Before this server is recommended for a deployment where the datastore is the
> constraint, measure tile throughput against a saturated datastore. If the encoder wins
> there, this ADR is reopened.**

## The answer

**It does not win. It loses, and it loses harder as the datastore gets busier.**

| Datastore | `ST_AsMVT` | local encoder | ratio |
|---|---|---|---|
| idle | **8.3 – 10.9 ms** | 74.7 – 103.1 ms | 8–11× |
| 16 active geometry queries | **31.9 ms** | 109.6 ms | 3.4× |
| 32 active geometry queries | **35.2 / 37.0 ms** | 156.8 / 155.9 ms | 4.2–4.5× |

Three runs, ten to twelve timed requests each after two uncounted warm-ups, one tile —
z=12/2394/1550 over Istanbul, 23 polygons, a 5,304-byte tile. Every run measured idle,
saturated and idle again, and the two idle readings agree within 2.6 ms on the PostGIS path.

## Why the encoder loses, measured rather than argued

The obvious reading of the condition is that `ST_AsMVT` should suffer most under contention,
because all of its work is inside the contended process. **It does suffer most in relative
terms** — 3.4× to 4× worse, against the local encoder's 1.06× to 1.5× — and it is still
several times faster in absolute terms at every level measured.

The reason is that **moving the encode out does not take the database off the path.** The
local encoder still asks PostgreSQL for the geometry, and geometry is not small:

| For the same tile | bytes PostgreSQL serialises and ships |
|---|---|
| `ST_AsMVT` | **5,304 B** — the finished tile |
| local encoder | **1,700 kB** — `ST_AsBinary(way)` for 23 rows |

**A factor of about 328.** So the path that was supposed to spare the database asks it to do
*more* work in exactly the resource that is scarce — serialise, and push through the socket —
and only then does its own encoding on top. Under load, that is the wrong direction twice.

## What this does not settle

- **One machine, one tile, one table**, which is ADR-021 condition 2 and is unchanged by this.
  A tile with thousands of small features, or a line table, may divide differently.
- **Saturation here is geometry CPU inside PostgreSQL** — `ST_Area` + `ST_Perimeter` over
  6,499,215 polygons, 8 and 24 concurrent workers on a 6-core container, reaching 16 and 32
  active queries. That is the resource `ST_AsMVT` competes for, which is why it was chosen. A
  datastore that is out of **connections**, or out of **disk**, is a different scarcity and
  this says nothing about it.
- **The two paths do not produce identical bytes** — 5,304 against 4,365 — so this compares
  two implementations of the same job rather than two ways of computing one answer.
- **Single-box, as the condition warns.** *Moving encode to a tier that scales horizontally is
  an argument no single-box benchmark can refute*, and this does not refute it: what it shows
  is that on the way to needing that tier, the encoder is behind at every point measured, and
  that the reason is the traffic between the tiers rather than the encoding itself. A
  horizontal tier would have to carry 1.7 MB per tile across a network to save 30 ms of
  PostgreSQL.

## Environment

| | |
|---|---|
| Database | PostgreSQL 16.4, PostGIS 3.4.3, GEOS 3.9.0, PROJ 7.2.1, in Docker, 6 CPUs, `shared_buffers=1GB`, `max_connections=200` |
| Table | `planet_osm_polygon` — 6,499,215 features, the same Geofabrik `turkey-latest` extract the 2026-08-12 run used |
| Harness | `benchmarks/harness`, Release, `/tiles` (`ST_AsMVT`) against `/tiles-local` (`ST_AsBinary` out, `MvtEncoder` in .NET), `pushMode=none` |
| Client | `curl`, one request at a time — this measures latency under a busy datastore, not client concurrency |
