# D-118 — what the count costs a GetFeature, and what a ceiling costs instead

**Run 2026-08-24.** PostgreSQL 17 in `gis-experiment-postgis`. The row's own trigger was *a
measurement, and it does not exist yet* — with an instruction attached: **do not change it before
measuring**, because the performance gate has been wrong five times about where this server's cost
is and four of those were arguments rather than numbers.

[D-118](../../docs/architecture-debt.md): WFS 2.0 makes `numberReturned` a required attribute on
`wfs:FeatureCollection`, so it must be known before the first feature is written. The two ways to
know it are to buffer the page — which [A-037](../../docs/architecture-assumptions.md) rules out,
allocation being the binding constraint — or to ask the provider how many rows match. So
`WfsEndpoints` asked, on every request.

## The corpus: 6,499,215 rows, `public.planet_osm_polygon`

| Statement | Time |
|---|---:|
| `count(*)`, unfiltered | **577 ms** |
| the page it accompanies — 1,000 rows, no `order by` | 7.6 ms |
| the page with `order by osm_id` (no index on that column) | 466 ms |
| `count(*)` with a bounding-box filter (40,998 rows) | 14 ms |

**The count is 75× the page it is written beside.** The filtered case is cheap, exactly as the row
guessed; the unfiltered one is not.

## What a bounded count costs on the same table

`select count(*) from (select 1 from t where … limit n) s`

| Ceiling | Time |
|---:|---:|
| 10,000 | 8.1 ms |
| **100,000** | **17.9 ms** |
| 250,000 | 29.7 ms |
| 1,000,000 | 101 ms |
| unbounded (6,499,215) | 577 ms |

**Flat in the table's size and linear in the ceiling**, which is the property that matters: a table
ten times larger costs the same.

## The layer this deployment actually has: 46,041 rows

| | Time |
|---|---:|
| `count(*)` | 3.9 ms |
| the page — 1,000 rows by indexed identity | 0.4 ms |
| the whole `GetFeature` request, end to end | 44 ms |

**9% of the request.** Immaterial, and the reason this row could not be settled by looking at the
deployment in front of us. The count is `O(table)` and the page is `O(page)`, so the share is not a
constant — it is whatever the deployment's largest table makes it.

## What was changed

`IFeatureSource.CountUpToAsync(query, ceiling)`, and the `results` path asks for
`max(startIndex + limit + 1, 100_000)`.

- **`numberReturned` stays exact always.** It is derived from a count that is exact below the
  ceiling, and at the ceiling the page is full by definition.
- **`numberMatched` stays exact up to 100,000** and becomes `"unknown"` above it — a value WFS 2.0
  defines for this case. Every layer here is under it, so no client loses the number it draws a
  scrollbar from.
- **`resultType=hits` still counts the whole table.** That is what hits is for: the client asked
  for the number and nothing else.

**Why 100,000 rather than `startIndex + limit + 1`.** The tight bound was written first and
measured: a page of ten from a 1,421-row layer answered `"unknown"`. That is the row's own warning
— removing the count removes the paging metadata every client uses — and it would have applied to
every layer this deployment publishes. The ceiling costs 17.9 ms on the corpus, against 577 ms, and
buys back the exact total for every table under it.

## What this does not settle

- **No end-to-end WFS measurement at corpus scale.** `planet_osm_polygon` is not published here and
  has no index on a candidate identity column, so a published version of it would page slowly for a
  reason that is not this row's. The corpus numbers above are the database's half, measured
  directly; the end-to-end numbers are from the 46,041-row layer.
- **The ceiling is not configuration.** A setting invites a deployment to raise it back to the cost
  this removed, and nothing has asked for one.
