# Publish at scale — ADR-057 conditions 1, 3 and 6

Three questions the composition decision left open with one shape: *this is fine
on three of them, and nobody has looked at a thousand.*

Run 2026-09-08 against a local fixture — Graticula on 127.0.0.1:8451, PostgreSQL
16.4 / PostGIS 3.4.3 in Docker on the same machine, schema `gisname`, HTTPS with
a self-signed certificate. Client and server on one host, so these are lower
bounds on a real deployment's latency and upper bounds on nothing.

Scripts: [scale.py](scale.py) (conditions 1 and 3), [preview.py](preview.py)
(condition 6). Raw output: [measured.json](measured.json),
[preview.json](preview.json).

---

## Condition 1 — the name check on a full folder

**Question.** §5e asks the server whether a name is free while somebody types.
Whether that scales was unknown; the implementation is one indexed lookup
(`FindServiceAtAsync` on `coalesce(lower(folder), '')`, `lower(name)`, which is
the expression `service_name_in_folder_ci` is built on), and the alternative it
was chosen over is a catalogue listing filtered in the browser.

**Method.** Forty checks of a free name and forty of a taken one, against the
same folder holding **3** services and then holding **1,000**. Same server, same
session, same process — so the only thing that differs is the folder.

| Folder | Free name, median | Free, p95 | Taken name, median | Taken, p95 |
|---|---|---|---|---|
| 3 services | 15.1 ms | 17.3 ms | 6.7 ms | 17.2 ms |
| **1,000 services** | **5.7 ms** | 17.6 ms | **7.5 ms** | 17.4 ms |

**Answer: the folder's size does not move it.** A thousand services in one
folder cost the check nothing measurable — the full folder's median is *lower*
than the empty one's, which is the first forty requests paying for a cold
connection pool rather than the index doing something clever.

**What is worth reading twice is the p95**, which is ~17–18 ms in all four sets
while three of the four medians are under 8. That is bimodal, and it is not the
query: an index lookup does not have two speeds. It is most likely the TLS
connection being re-established or a GC pause, and it is the same in an empty
folder as in a full one — so it is a property of this harness, not of the
feature. Left un-chased because 18 ms is well inside a keystroke either way.

**Consequence for the 250 ms debounce.** At ~6 ms a check, the debounce is not
protecting the server from cost; it is protecting the operator from a sentence
that changes under their fingers. That is still a reason, and it is a different
one from the one it was written with.

---

## Condition 3 — a composition of a thousand layers

**Question.** §5h writes a service, its groups and its layers in one
transaction. Every composition anybody had published held three things.

**Method.** One `POST /admin/publish` naming **1,000 layers** over 1,000
distinct tables — distinct because `layer_table_unique_in_service` allows a
table once per service. The tables are empty, which is deliberate: this measures
the catalogue transaction, which is what the condition asks about, not the data.

| What | Time |
|---|---|
| **1,000-layer composition, one transaction** | **0.68 s**, answered 201 |
| 1,000 single-layer services, one request each | 25.4 s (≈25 ms each) |
| Deleting those 1,000 services, one request each | 58.4 s (≈58 ms each) |

**Answer: one long transaction is not the problem.** 1,001 catalogue rows in
under a second, and the per-row cost inside the transaction (≈0.7 ms) is
**thirty-five times lower** than the per-service cost of a round trip. The
transaction is the cheap part of publishing; the HTTP request around it is not.

**The one number that surprised.** Deleting is more than twice as slow as
creating, per service — 58 ms against 25 ms — because the delete path walks
group indices and unpublishes layer by layer where publishing writes them in
one statement. Nobody has complained, and it is written here so the next person
timing a bulk teardown does not think they have found a fault.

---

## Condition 6 — where a preview stops being instant

**Question, and it has three parts.** Where does a preview stop being instant;
is the bound the row count or the vertex count; and what does a composition of a
whole database cost, since §5j redraws **every** layer on every change the
screen cannot coalesce. The ceiling is 4,000 rows per layer and that figure was
chosen for feel.

**Method.** A generated corpus of polygons on a grid, crossing row count with
vertex count, drawn through `POST /admin/publish/preview` at a fixed 900×620
frame and a fixed extent. Seven repeats after one warm draw thrown away — the
first request against a table pays for the shape lookup `ServiceContexts` then
remembers for thirty seconds, and measuring that would report a cache miss as
the cost of drawing. Medians below.

### Rows against vertices

| Rows in the table | 5 vertices | 50 vertices | 500 vertices |
|---|---|---|---|
| 250 | 30.8 ms | 42.1 ms | 98.8 ms |
| 1,000 | 44.9 ms | 67.0 ms | 290.9 ms |
| 2,000 | 46.0 ms | 97.9 ms | 439.2 ms |
| 4,000 | 61.1 ms | 163.3 ms | 715.7 ms |
| 8,000 *(ceiling bites)* | 54.1 ms | 160.2 ms | 755.5 ms |
| 16,000 *(ceiling bites)* | 52.5 ms | 171.2 ms | 1,112.9 ms |

**Answer to the second part, and it is the one that matters: the cost is in the
vertices, not the rows.** Holding rows fixed and going from 5 vertices to 500
multiplies the time by **10×** at every row count. Holding vertices fixed and
going from 250 rows to 4,000 multiplies it by 2× at 5 vertices and 7× at 500.
**4,000 simple polygons draw in 61 ms; 1,000 complex ones take 291 ms.** A
ceiling on the row count is a bound on the cheaper variable.

**The ceiling does what it was built to do, and only that.** The 8,000- and
16,000-row rows are where it bites, and at 5 and 50 vertices they cost the same
as 4,000 — the bound holds. At 500 vertices it does not: 16,000 rows capped to
4,000 drawn still takes **1,113 ms** against 4,000 rows' 716 ms.

> **Corrected 2026-09-09 — the observation above stands and the reason given for
> it was wrong.** This paragraph said the gap was there *because the `LIMIT`
> bounds what is returned and not what the database reads and simplifies*, and
> that the work happens in PostGIS before the ceiling applies. The plans say
> otherwise. `r16000_v500` at `limit 4000` is an `Index Scan using
> r16000_v500_pkey` with `Buffers: shared hit=4012` — **4,012 buffers for 4,000
> rows, so it read 4,000 and not 16,000.** The `LIMIT` does bound the read.
>
> **It is a plan flip.** `r4000_v500` at the same limit is a `Gather Merge →
> Parallel Seq Scan → Sort` with two workers: under the ceiling the planner reads
> the whole small table in parallel and *three cores* simplify. Over it, the
> `LIMIT` looks selective on the identity index `PostGisFeatureSource.
> AppendOrderAndPaging` is obliged to order by, so it takes a single-threaded
> index scan and *one core* does the same 4,000 simplifications. With
> `max_parallel_workers_per_gather = 0` the difference disappears — the three
> v500 tables cost **1,102 / 1,151 / 1,175 ms** whatever their row count.
>
> **The correction matters rather than tidying.** *A `LIMIT` cannot express a
> vertex bound* was the reason [Q-148](../../docs/open-questions.md) was a
> question rather than a task. It can: a row limit divided out of a measured
> per-feature width **is** a vertex bound, and that is what the preview now does.

**Where the cost is spent, since the answer is not the wire.** The response is
21–22 KB at 4,000 rows regardless of vertex count — simplification to one pixel
is done in the database and collapses a 500-vertex ring to a handful of points
before it is sent. So the ten-fold difference is PostGIS reading and simplifying
geometry, not our rendering and not the network.

### A composition of many layers

1,000-row, 5-vertex layers, one preview of all of them:

| Layers | Median |
|---|---|
| 1 | 40.0 ms |
| 5 | 66.7 ms |
| 10 | 105.5 ms |
| 25 | 205.3 ms |
| 50 | 397.0 ms |

**Linear, at about 7 ms a layer beyond the first.** A composition of a
fifty-table database previews in 0.4 s on simple data — usable, and the
right order of magnitude for a screen that redraws on pan. On dense data the
same fifty layers would be tens of seconds, and no per-layer row ceiling
prevents that.

### The number, and the criterion it was chosen by

**4,000 stays.** The criterion is: the ceiling exists to stop a *large* layer
making the preview unusable, and on that shape it is measured to work — 8,000
and 16,000 rows cost what 4,000 costs. Lowering it would buy nothing on the case
that is actually slow: 1,000 dense rows already take 291 ms, so a lower row
ceiling reaches the dense case only by making the drawing a sample far more
often, which costs the operator a picture they can trust in exchange for a
problem it does not fix.

**What is not settled is the dense case**, and it now has a number instead of a
suspicion: a single 500-vertex layer of 16,000 rows costs 1.1 s, and fifty such
layers would be a minute. That is [Q-148](../../docs/open-questions.md) —
whether a preview should bound vertices rather than rows, and if so how, since a
`LIMIT` cannot express it.
