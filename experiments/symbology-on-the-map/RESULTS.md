# Symbology on the map — can the editor sit beside a map the reader pans?

**Run 2026-09-04.** Owner question, after two ArcGIS Online Map Viewer videos: should the
symbology editor move onto the map, the way Map Viewer does it?

---

## 1. The question this answers, and the one it does not

[ADR-051](../../docs/adr/ADR-051-an-appearance-is-chosen-by-looking-at-it.md) decided that the
preview is drawn **by the server**, through the same renderer that serves the layer, because a
browser-drawn one would be *"a picture of the browser's reading of the style"* and **a preview
that lies is worse than no preview, because it is believed.** That decision is not in question
here.

What is in question is the *frame*. Today the preview is one PNG at 336×224 over the extent the
features happen to occupy, computed by `ThumbnailEndpoints.Framed`. A fixed frame cannot answer
*what does this look like at z14 over Ankara*, which is most of what somebody choosing an
appearance wants to know.

So: **draw the same thing, with the same renderer, at the viewport's own extent, on every view
change.** The only open question is whether that round trip is affordable at the rate panning
asks for it. That is what was measured.

**Not answered here:** where the controls go, whether the panel deepens the way Map Viewer's
does, or whether the editor becomes the Visualization tab. Those are design decisions and this
is a measurement.

## 2. What was built

- `AdminEndpoints.ReadPreviewFrame` — an optional `?bbox=minx,miny,maxx,maxy` and `?size=WxH`
  on `POST /admin/layers/{name}/symbology/preview`, in the layer's own coordinate reference.
  Absent, the endpoint behaves exactly as it did. `size` is capped at 2048 in each direction.
- `ThumbnailEndpoints.RenderAsync` became `internal` and took two optional dimensions. Every
  existing caller passes neither and gets 336×224.
- `index.html` + `probe.js` here: an OpenLayers map for the ground and the panning, one ordinary
  `<img>` over it whose source is that POST, redrawn on `moveend`, with the round trip timed on
  the panel. An element rather than an OpenLayers image source on purpose — a source with a
  custom loader is a different API in almost every major version, and what is being measured is
  the server.

## 3. Measured

Chromium, one client, `127.0.0.1:8449` against the `gisconsole` fixture schema. Five runs per
view, median reported. Map area 1020×820, which is roughly what a 340-pixel panel leaves at
1440×900.

| Layer | Classes | Document | View | Median | PNG |
|---|---|---|---|---|---|
| `ci_many` | 256 | 113 kB | whole extent | **78 ms** | 63 kB |
| `ci_many` | 256 | 113 kB | half | 56 ms | 21 kB |
| `ci_many` | 256 | 113 kB | quarter | 58 ms | 9 kB |
| `ci_many` | 256 | 113 kB | close | 48 ms | 5 kB |
| `ci_parcels` | 1 | small | whole extent | 42 ms | 6 kB |
| `ci_parcels` | 1 | small | close | 41 ms | 4 kB |
| `ci_EarlyAlert_routes` | 1 | 312 B | whole extent | 46 ms | 35 kB |
| `ci_EarlyAlert_routes` | 1 | 312 B | close | 34 ms | 15 kB |

**Against today's fixed frame**, same document, same endpoint, 336×224:

| Layer | Today, 336×224 | Map, 1020×820 | For |
|---|---|---|---|
| `ci_many` | 46 ms | 78 ms | 11× the pixels |
| `ci_parcels` | 13 ms | 42 ms | 11× the pixels |
| `ci_EarlyAlert_routes` | 13 ms | 46 ms | 11× the pixels |

**Interactively** — four pans and a zoom on `ci_many`, then a class colour changed:

```
median of 6 draws: 76 ms      last: 84 ms · 11 kB
median of 9 draws: 78 ms      last: 54 ms ·  4 kB
```

## 4. What the numbers say

**The loop is affordable.** The worst case in the fixture — 256 classes over the whole extent at
map size — is 78 ms, and everything else is 34–58 ms. A redraw fires on `moveend`, not during
the drag, so what a reader sees is the old picture while they pan and the new one about a
twentieth of a second after they let go. That is how every map-image layer in the world behaves.

**Zooming in gets cheaper, not dearer**, because fewer features fall in the frame: 78 → 48 ms and
63 → 5 kB from the whole extent to a close view. The expensive case is the one you see once, on
arrival.

**Bytes are not a problem.** Twenty pans of the worst layer is about 1.3 MB, and most views are
under 20 kB.

## 5. What this does not measure, and should be said

- **Localhost.** No network. Add the round trip of wherever the operator actually is; the
  console is usually near its server, but this number is a floor, not a promise.
- **One client.** Nothing here says what ten publishers styling at once cost the render pool.
- **No labels.** `RenderAsync` deliberately does not call `FinishLabels` — text at 104 pixels is
  a smear on a thumbnail. A map normally has labels, and their cost is not in these figures.
  Whether this path should start drawing them is part of the design decision, not a detail to
  slip in with a size parameter.
- **A separate process against the same schema.** The prototype server is not the one the
  console tests drive; caches are its own and were cold at the first draw of each layer, which
  is why the first row of each view is reported alongside the median.

## 6. Screenshots

`shots/proto-1-open.jpg` — `ci_many`, 256 classes, drawn by this server over an OpenStreetMap
ground at the layer's own extent.
`shots/proto-3-zoomed.jpg` — the same document two zoom levels in, still the server's drawing.
`shots/proto-4-edited.jpg` — a class colour changed in the panel; the map redraws from a document
that is never stored.

## 7. What it costs to keep

The `bbox` parameter was production code added to answer an experiment's question, and this
section said it should be reverted if the map-shaped editor was not built — an endpoint parameter
with no caller is a control for a feature that does not exist.

**It was built the same day and the debt is closed.** The symbology editor's preview is a map now
(`console.js`, `symBuildMap`), and every redraw carries the viewport's `bbox` and `size`. The
parameter has a caller.
