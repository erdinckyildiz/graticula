# ADR-022 condition 3 — what the six operations §2b added actually cost

**Run 2026-08-27** against a running server on this machine, over real OpenStreetMap
polygons out of `public.planet_osm_polygon` — 6,499,215 polygons, 77,089,382 vertices,
the same corpus `mvt-generation` used. `geombench2.py` and `geombench3.py` are the
harnesses.

[ADR-022](../../docs/adr/ADR-022-geometry-server.md) condition 3 is the reason:

> **The six operations added in §2b have no measured cost profile.** They are verified
> for *correctness* against PostGIS, and bounded by the deadline and the heap limit —
> but nobody has measured what a realistic `buffer` or `relation` costs, so the
> ten-second deadline and the two-worker pool are sized from overlay's numbers alone. A
> `relation` over two sets of thirty is nine hundred comparisons in one worker slot, and
> that shape did not exist when the pool was sized.

**Timed from outside, and from inside beside it.** The wall-clock number is what an
operator's deadline is compared against; every answer also carries the server's own
`cost.milliseconds`, so the gap between the two is the request rather than the geometry.

## The corpus

| band | shapes | vertices min / median / max |
|---|---|---|
| small | 60 | 5 / 7 / 14 |
| medium | 60 | 203 / 322 / 395 |
| large | 60 | 2,005 / 2,874 / 5,387 |
| huge | 3 | 116,364 / 189,585 / **215,488** |

**The winding conversion is worth recording, because the server found the harness's
bug.** GeoJSON winds an outer ring counter-clockwise and ArcGIS the other way; the first
run sent GeoJSON order and every request was refused with *"The first ring is
counter-clockwise, which ArcGIS reads as a hole"*. That refusal is the behaviour
working — a server that guessed would have buffered thirty holes and returned a plausible
answer.

## `buffer`, by input size and by width

| input | width | wall median | engine median |
|---|---|---|---|
| 30 small | 10 m | 27 ms | 8 ms |
| 30 small | 500 m | 56 ms | 36 ms |
| 30 medium | 10 m | 113 ms | 68 ms |
| 30 medium | 500 m | 414 ms | 381 ms |
| 30 large | 10 m | 4,268 ms | 3,962 ms |
| 30 large | 500 m | **refused, 503 at 10.1 s** | — |

**The deadline bites, and on an ordinary request.** Thirty polygons of about 2,900
vertices, buffered by 500 metres, is not an adversarial input — it is a five-minute
walk's worth of city blocks — and it runs past ten seconds. The refusal is correct and
the number is now known rather than assumed.

**Width matters as much as vertex count.** The same thirty shapes cost 4.3 s at 10 m and
more than 10 s at 500 m, because a wider buffer produces more arc segments per vertex and
then has to dissolve the overlaps between neighbours.

## `relation`, by pair count

| input | pairs | wall median | engine median |
|---|---|---|---|
| 10×10 small | 100 | 21 ms | 1 ms |
| 30×30 small | 900 | 13 ms | 5 ms |
| 10×10 medium | 100 | 23 ms | 8 ms |
| 30×30 medium | 900 | 70 ms | 46 ms |
| 10×10 large | 100 | 179 ms | 104 ms |
| 30×30 large | 900 | **660 ms** | 350 ms |

**The shape the condition worried about is cheap.** *"A `relation` over two sets of
thirty is nine hundred comparisons in one worker slot"* — measured, that is 660 ms on
the largest ordinary band, sixteen times inside the deadline. Nine hundred DE-9IM
evaluations over 2,900-vertex polygons is less work than one 500-metre buffer over the
same shapes, because a predicate can stop early and a buffer cannot.

## The other four, on the large band

| operation | wall median | engine median |
|---|---|---|
| `union` 30 | 2,183 ms | 1,982 ms |
| `simplify` 30 | 3,264 ms | 3,032 ms |
| `offset` 30, 10 m | 1,135 ms | 793 ms |
| `distance`, two shapes | 30 ms | 6 ms |
| `cut`, one shape by a line | 84 ms | 68 ms |

**`distance` answers.** ADR-022 condition 2's note says *"`distance` is still refused,
now with its own reason"* — that has not been true since §2b moved it to `DistanceOp`,
and it is in the engine's operation list. The note is corrected with this run.

## One enormous polygon is not the expensive case

| input | wall | engine |
|---|---|---|
| `buffer` one polygon of 215,488 vertices, 10 m | 3,473 ms | 2,251 ms |
| `buffer` one polygon of 189,585 vertices, 10 m | 2,695 ms | 1,939 ms |
| `buffer` one polygon of 116,364 vertices, 10 m | 1,544 ms | 1,138 ms |

**The largest polygon in six and a half million buffers in three and a half seconds**,
comfortably inside the deadline — while thirty ordinary ones at 500 m do not. The cost
is dominated by **how many shapes have to be dissolved against each other**, not by how
many vertices one of them has. A cap on vertices per geometry would therefore not be the
control it looks like.

## The pool

Thirty large polygons buffered by 10 m — 4.8 s of work — asked for by N callers at once.

| callers | wall for the batch | per-request median | per-request max | statuses |
|---|---|---|---|---|
| 1 | 4,802 ms | 4,802 ms | 4,802 ms | 200 |
| 2 | 5,574 ms | 4,992 ms | 4,998 ms | 200 |
| 4 | 11,547 ms | 8,078 ms | 10,343 ms | 200 |
| 8 | 17,377 ms | 10,150 ms | 15,115 ms | 200 **and 503** |

**Two run at a time.** Two callers finish in the time one takes; four take two rounds.
The pool is the two workers §2b relies on, and it behaves as described.

**At eight, two callers are refused and two are served at fifteen seconds.** The refusal
says why, and says the important half itself:

> Every geometry worker is busy and this request waited 10 seconds for one. This work
> runs in a small pool of processes so that its memory cost is bounded; try again
> shortly. **The wait and the work have separate budgets.**

So a caller's worst case is **the wait plus the work** — up to twenty seconds — and two
requests that arrive in the same millisecond can end up one refused at ten seconds and
one answered at fifteen. That is a property of the design rather than a defect: bounding
the total would mean refusing work that was going to succeed. It is written here because
nothing said it before, and *the deadline is ten seconds* reads like a promise about the
answer's latency when it is a promise about the work's duration.

## What this settles, and what it does not

**Settles.** The ten-second deadline is the right order of magnitude and it fires on
plausible input rather than only on adversarial input — which is what makes it a real
bound rather than a formality. The two-worker pool serves two concurrent expensive
requests at full speed and sheds the ninth honestly. The `relation` shape the condition
singled out is sixteen times inside the deadline.

**Does not settle.** One machine, one corpus, one geometry type: every shape here is a
polygon out of OpenStreetMap, so nothing is known about line work at scale or about mixed
inputs. Memory was not measured — the 1 GB heap ceiling is still sized from overlay's
numbers. And the numbers are wall-clock on a laptop that was also running two servers and
a PostgreSQL, so they are honest about ordering and generous about absolutes.
