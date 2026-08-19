# Paging — 3,000,000 rows walked in 5,000-row pages

**Run 2026-08-19** against the dev server and the experiment PostGIS, to answer the
owner's question directly: *"peki paged data isteyebilir miyiz. esrideki gibi. yani
max record count 5000 olsun. önce ilk 5000, sonra startindex 5001'den başlayıp
sonraki 5000 gibi."*

Yes, and this is the measurement rather than the assurance. `budget.py`'s sibling
`paging-walk.py` (kept in the session scratchpad, not the repository — it is thirty
lines of `urllib`) walks a layer page by page and asks the only question that matters
about paging: **does the union of the pages equal the layer, with nothing repeated and
nothing missed?**

## The fixture, and why it is not a real layer

3,000,000 rows built for this: a dense integer `objectid`, a trivial point geometry,
one categorical column. 378 MB, created and dropped inside the run.

The obvious candidate was the OSM corpus already on the machine — `planet_osm_polygon`
holds **6,499,215** rows — and it cannot be published through the ArcGIS surface at
all: `osm_id` is `bigint`, and ADR-013 §2a requires a unique **32-bit** integer for an
object id. That is the rule working, not a gap: the layer is servable natively and not
through this surface, and the surface says so rather than truncating somebody's ids.

## Correctness: the pages tile the layer exactly

| Layer | Rows | Page | Pages | Duplicates | Missing | Wall clock |
|---|---|---|---|---|---|---|
| `hosted/tr_yol` | 46,041 | 5,000 | 11 | **0** | **0** | 0.5 s |
| `hosted/zz_paging_3m` | 3,000,000 | 5,000 | **601** | **0** | **0** | 77 s |

Every full page answers `exceededTransferLimit: true`; the last page and the empty page
past the end answer `false`. That is what an ArcGIS client reads to know whether to ask
again, and it is the whole protocol.

**601 pages rather than 600**, because the walk asks once more after the last partial
page and gets an empty answer. A client that stops at `exceededTransferLimit: false`
makes 600 requests.

**What makes this correct is an `ORDER BY` nobody asked for.** A `LIMIT` with no order
does not have a wrong order — it has *no* order, so the provider may return any subset
and call it a page. [D-21](../../docs/architecture-debt.md) is the entry: the query
ordered by identity *when an offset was given*, and `resultOffset=0` is not "given", so
the first page came back in heap order and every later page in identity order. The two
do not line up. Measured then on `hosted/tr_il`: objectids `[1, 33]` for offset 0 and
`[2, 3]` for offset 1, four times out of four. Three of ten layers did it. The seven
that did not were small enough that heap order happened to *be* identity order — which
is why a conformance test that checks exactly this property had passed.

So the zeros in the table above are the interesting part, and they are zeros because
that defect was fixed.

## Cost: offset paging is O(offset), and the alternative is flat

Same page size, same 5,000 rows returned, two ways of saying *from here*. Two runs each,
seconds:

| Depth | `resultOffset=N` | `where objectid > N` |
|---|---|---|
| 0 | 0.025 · 0.024 | 0.022 · 0.020 |
| 100,000 | 0.035 · 0.030 | 0.018 · 0.028 |
| 1,000,000 | 0.102 · 0.098 | 0.020 · 0.019 |
| 2,000,000 | 0.163 · 0.163 | 0.019 · 0.020 |
| 2,995,000 | **0.231 · 0.226** | **0.021 · 0.021** |

**Offset grows linearly with depth and keyset does not.** PostgreSQL has to walk and
discard the rows it skips; the index scan that finds `objectid > 2995000` starts where it
is told. Ten times the cost at the end of this layer, and the ratio grows with the table.

Over a whole walk of 3,000,000 rows in 5,000-row pages the difference is the sum of that
column: roughly **75 seconds** of database time against **12**. The 77 seconds this run
took is that sum plus the HTTP.

**Nothing here can be rewritten server-side.** `offset N` means *the Nth row of this
order*, and knowing which row that is **is** the scan. A server cannot turn it into a
keyset lookup without already knowing the key — that is what the offset computed. So this
is a fact about what the client asks for, not a defect to repair:

- An ArcGIS client walking a layer with the SDK sends `resultOffset`, and it works, and
  it costs this.
- A script of your own walking a whole layer should send
  `where=objectid>{last}&resultRecordCount=5000` and pay a flat 20 ms per page. Both are
  already supported; the second is not a special feature, it is the `where` clause.

## What this run did not measure

- **Concurrency.** One client at a time. The owner's worry — *n tane request* — is bounded
  by three other things and none of them is a row count: the response-byte ceiling, the
  request deadline, and ADR-007 §4.8's connection budget. Twenty concurrent deep pages
  would each pay their own offset scan.
- **Geometry.** Every request here asked for `objectid` with `returnGeometry=false`. A real
  page of 5,000 polygons is dominated by encoding, which
  [feature-query](../feature-query/RESULTS.md) §2.2 measured and this does not.
- **A moving table.** Nothing was inserted or deleted during the walk. Offset paging over a
  table somebody is editing repeats and skips rows *by design* — the row that was at 5,001
  is at 5,000 after one delete — and keyset paging does not. That is the other reason to
  prefer the `where` form, and it is a correctness argument rather than a speed one.
