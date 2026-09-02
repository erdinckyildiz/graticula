# D-58 — what a rendered thumbnail costs, beside the sample the browser draws

**Run 2026-08-24.** One server, one layer, both paths, median of five.

[D-58](../../docs/architecture-debt.md) says the service list's preview is *a sample the
browser draws, not a picture of the layer*, and its trigger is **when there is a renderer**.
That happened: `Graticula.Render.Skia` exists, [ADR-041](../../docs/adr/ADR-041-the-map-renderer.md)
is the map renderer, and `GET /rest/services/{name}/MapServer/export` turns a service into an
image. The row was not repaid because `preview.js` carries measured numbers and the
alternative carried none — *"a render per row against a list of forty has no number at all"*.

This is that number.

## What was measured, and against what

A scratch database beside the development store, so nothing here touched the deployment's
own catalogue: **5,000 polygons**, `public.parcels`, registered — **not hosted** — because
whether the export path covers a registered layer was one of the open questions and a hosted
one would not have answered it.

Both requests ask for the same eighty pixels of the same layer:

- **preview** — `query` with `resultRecordCount=800`, geometry only, `maxAllowableOffset=0.01`,
  which is what `preview.js` sends and why its own numbers are what they are.
- **thumbnail** — `MapServer/export` at `size=80,80`, `format=png`, over the layer's extent.

| | median time | bytes on the wire |
|---|---:|---:|
| preview (query, drawn by the browser) | **17–23 ms** | **139.5 kB** |
| thumbnail (rendered by the server) | **70–76 ms** | **1.8 kB** |

## What it means

**The render is three to four times slower per request and seventy-seven times smaller.** On
a list of forty rows that is **5.6 MB against 72 kB**.

**And the asymmetry is worse than the table shows**, because the two costs are paid by
different people. D-58's own trigger describes the thumbnail as *cached, drawn once rather
than per viewer*: the 70 ms is one layer's one-time cost, while the 139.5 kB is paid by every
viewer on every visit. The preview's session cache helps a returning viewer and does nothing
for the next one.

**The registered-layer question is answered.** The measured layer came from a registered data
source and rendered. The export path is not hosted-only, which the tile path is (Q-67) and
which is why a tile-based preview was rejected when this row was written.

## What this does not settle

**The swap itself.** The preview is a `<canvas data-preview>` emitted from five places in
`console.js`, and the `cover` object those places read has a different shape at three of
them. So the change is five markup sites, a fallback for a service with no renderable face,
and somebody looking at the result — on a screen whose owner reviews every screen. That is a
UI change with an owner in it, and [D-46](../../docs/architecture-debt.md) instance 8 is the
record of what happens when a screen is rebuilt without looking at the one next to it.

**What a thumbnail looks like without symbology.** These numbers are cost, not appearance.
The render uses the derived renderer — a colour generated from the layer's name
([ADR-028](../../docs/adr/ADR-028-symbology.md)) when no style is stored — and whether forty
of those read better than forty sampled outlines is a question for eyes rather than for a
stopwatch.

**Caching.** The 70 ms is measured warm and uncached. What a cached thumbnail costs to
invalidate when a layer's data changes is [ADR-010](../../docs/adr/ADR-010-caching.md)'s
question and is not asked here.

---

## Run 2026-09-02 — the swap, measured after it was made

The three things above under *what this does not settle* were settled, and the numbers moved
because the implementation is not the same as the probe: the console asks
`/admin/thumbnail?service={qualified}&layer={n}` rather than `MapServer/export`, the render is
**336×224** rather than 80×80 so one picture serves both slots on a high-density screen, and the
answer is **held in memory for five minutes** and revalidated with an `ETag`.

Same server, same fixture store, `curl` against a warmed process.

| layer | old path, per viewer per visit | new path, first ask | new path, held | after that |
|---|---:|---:|---:|---:|
| `ci_buildings` | 2,018 B + 1,830 B in two requests, 22–29 ms | 1,807 B, 24 ms | 1,807 B, 13–25 ms | **304, 0 B** |
| `ci_many` | 2,323 B + **118,954 B**, 23–27 ms | 17,756 B, 58 ms | 17,756 B, 13 ms | **304, 0 B** |
| `ci_parcels` | — | 2,249 B, 36 ms | — | — |
| `ci_editable` | — | 1,236 B, 41 ms | — | — |
| `ci_observations` | — | 2,171 B, 32 ms | — | — |

**The dense layer is where the whole argument lives.** `ci_many` cost **121.3 kB across two
requests** to draw 800 of its features; it costs **17.8 kB once** to draw all of them, and 0 B on
every later visit. `ci_buildings` is small enough that the two paths are within a rounding error
of each other on the wire — which is worth stating plainly, because a reader who tries this on a
small layer will not see the difference the row is about.

**The pictures are not blank.** Counted from the PNGs themselves, opaque pixels out of 75,264:
`ci_many` 16,200 (21.5%), `ci_parcels` 3,382 (4.5%), `ci_buildings` 2,397 (3.2%),
`ci_observations` 446 (0.6%), `ci_editable` 222 (0.3%). The last two are sparse point layers and
that is what a sparse point layer looks like — the failure this was checking for is a render that
returns a valid empty PNG, which none of them is.

**The first request in a cold process cost 1.06 s**, and it is not the render: it is the first
catalogue read and the first connection out of the pool. The second cold render of a *different*
layer cost 32–58 ms. Quoting the 1.06 s as the cost of a thumbnail would be quoting the cost of
starting a server.

### What is still not settled

**Invalidation.** The five minutes is a judgement, not a measurement. Nothing tells the cache that
a symbology changed, so a changed service shows its old picture until the entry ages out. The
alternative — a hook on every write path that can change what a map looks like — is a list
nobody can keep complete, and a bounded staleness is a smaller wrong than an unbounded one.

**A list of forty has still not been timed end to end.** Every number here is one request. What
forty concurrent `fetch`es cost against one process is the question the old preview's
*one at a time* comment was answering, and the new path has no such comment because each answer is
a couple of kilobytes from memory — which is reasoning rather than a measurement, and is written
here as one.
