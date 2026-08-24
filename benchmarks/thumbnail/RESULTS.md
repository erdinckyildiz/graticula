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
