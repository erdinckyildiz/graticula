# ADR-051 — An appearance is chosen by looking at it

| | |
|---|---|
| **Status** | `ACCEPTED` |
| **Confidence** | `HIGH` |
| **Decided** | 2026-09-03 |
| **Supersedes** | — |
| **Superseded by** | — |

---

## 1. Context

[ADR-033](ADR-033-symbology.md) settled what a layer's
appearance *is*: one canonical MapLibre Style Spec v8 document per layer, with an
Esri `drawingInfo` derived from it and accepted into it. What it did not settle is
how a person chooses one.

Until 2026-09-03 the Studio answer was a textarea holding that document, the
derived `drawingInfo` beside it, and colour swatches. That is an editor for
somebody who already knows the MapLibre style specification, and the project owner
said so plainly: *bana gui editör lazım*.

The problem is not the absence of widgets. It is that **a swatch is not a map**.
The question somebody has when they open this screen is *what will this layer look
like*, and neither a hex colour nor a JSON document answers it. Two of the three
things that decide the answer — how dense the features are, and how thick a
1-pixel outline reads at the size anybody actually sees it — are not visible in
either.

## 2. Alternatives considered

### Alternative A — controls only, no picture

**Argument for.** Cheapest. A form over the three renderer families the product
already has (`simple`, `uniqueValue`, `classBreaks`) is a day's work, needs no new
endpoint, and removes the requirement to know MapLibre.

**Against.** It answers *which colour* and leaves *what does it look like*
unanswered, which is the question. Every appearance would still be chosen by
storing it, going to the map, and coming back — the loop the screen exists to
close.

### Alternative B — the browser draws the preview

**Argument for.** No server round trip, instant feedback, no new endpoint. The
console already has MapLibre GL available in principle.

**Against.** It would be a picture of *the browser's* reading of the style, not of
the renderer that serves the layer. Graticula draws WMS and image tiles with its
own `MapRenderer`; anywhere the two disagree — and they will, because ours is a
deliberately smaller renderer — the preview would be confidently wrong. A preview
that lies is worse than no preview, because it is believed.

It also needs the layer's features in the browser, which means either a vector
tile request per edit or a feature query, and the record ceiling and the framing
rules would have to be reimplemented on the client to match.

### Alternative C — the server draws the candidate through the same path (**chosen**)

`POST /admin/layers/{name}/symbology/preview` takes a candidate document —
MapLibre or `drawingInfo`, converted on the way in exactly as `PUT` would convert
it — and answers a PNG drawn by `ThumbnailEndpoints.PictureAsync`: the same
renderer, the same framing on the features actually drawn, the same record
ceiling as the layer's thumbnail.

**Argument for.** The picture is of the thing that will serve the layer. Because
the conversion is the `PUT` conversion, a `drawingInfo` pasted from ArcGIS
previews as it will store, including its losses — so the editor cannot produce
something the paste path would refuse.

**Against.** A round trip per edit. Mitigated by debouncing at 250 ms and by the
picture being 336×224; measured at roughly 1.8 KB and a few tens of milliseconds
on the fixture. This is a single-operator screen, not a serving path.

## 3. Decision

1. **The preview is drawn by the server**, through
   `WmsEndpoints.DrawLayerAsync` with a symbology override, which is the same
   call the thumbnail makes. `DrawLayerAsync` gained one optional parameter and
   no second drawing path exists.

2. **`POST /admin/layers/{name}/symbology/preview`** requires
   `Privilege.ContentPublishFeatures` — the privilege that could store the
   document and look at the result anyway, so the endpoint reveals nothing new.
   It answers `image/png` with `Cache-Control: no-store`, **writes nothing**, and
   refuses an unreadable document with `400` and a sentence rather than with a
   broken image.

3. **The controls write the document box, and Store sends the box.** There is one
   state on screen, not two that could disagree. Editing the box by hand still
   works, and the controls then stop claiming to describe it — a MapLibre
   expression has no checkbox, and pretending otherwise would silently discard it.

4. **The form offers the three families ADR-033 already converts** — `simple`,
   `uniqueValue`, `classBreaks` — and builds a `drawingInfo`, not MapLibre. That
   is the shape the controls are in, the endpoint takes both, and it means the
   editor travels the same conversion as a paste from ArcGIS.

5. **The controls follow the geometry.** A polygon has an outline, a line *is* the
   stroke, and only a marker has a size. Offering all three to everybody is the
   cheaper thing to build and it is what makes a form read as generated.

## 4. Consequences

**State.** None. The preview endpoint writes nothing to the catalogue and holds
nothing at runtime: no cache, no session, no node-local memory. The candidate
document exists for the duration of one request and is discarded. This is
asserted rather than asserted-about — the layer's stored symbology is read
before and after a preview and has to be the same text.

- One new admin endpoint, no new drawing code, no new symbology vocabulary.
- The console must check that a preview response is actually an image before
  showing it. Anything in front of the server — a proxy, a portal's sign-in page,
  the console suite's own write trap — can answer a `POST` with a cheerful
  `200 application/json`, and assigning that to an `<img>` shows a broken-image
  glyph, which a reader parses as *this style draws nothing*.
- A preview is a `POST` because it carries a whole document. That makes it
  invisible to the console test suite, which traps every non-`GET` so that a click
  cannot change a shared fixture. The suite's trap is **not** relaxed for it: the
  page's half is proved in `SymbologyEditorTests` against an answer the test hands
  it, and the server's half in
  `SymbologyPreviewDrawsTheCandidateTests`, which compares three answers to show
  that the picture is a function of the document.

## 5. Status of this decision under the CIM reversal

The project owner decided on 2026-09-03 that the **canonical** symbology model
becomes Esri's CIM rather than MapLibre, reversing ADR-033 §1. That decision
changes *what document this endpoint receives and stores*. It does not change
anything decided here: the preview is still drawn by the server through the
serving renderer, still writes nothing, still refuses in a sentence, and the
controls still write one document that Store sends. The ADR recording the
reversal owns the vocabulary question; this one owns the loop.
