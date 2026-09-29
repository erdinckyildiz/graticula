# ADR-099 — Picture markers are drawn on every face

| | |
|---|---|
| **Status** | `ACCEPTED` |
| **Confidence** | `MEDIUM` — every face is written and unit-tested, and the Skia drawing is read back from pixels; nothing here has yet been run against a live server or opened in ArcGIS Pro, the JavaScript SDK or MapLibre (§4, §6) |
| **Decided** | 2026-09-29, by owner decision. The owner named the gap — the vector tile card is *Partial* chiefly because a layer's own picture markers are not carried — and asked for them to be first-class on every face: accepted in the three shapes the symbology route reads, drawn by the raster faces, published to ArcGIS clients as `esriPMS`, and drawn by the generated tile style from a sprite sheet the server builds itself. The owner also set the frame: PNG and JPEG, no URL ever fetched, pictures bounded from the header before they are decoded, stored inside the CIM document unless its size forbids it, the generated icons merged with an uploaded sheet under a reserved prefix. The numbers, the prefix, the packing and the merge are this session's design and are marked **INFERRED** where it matters (§5.8). |
| **Depends on** | [ADR-052](ADR-052-the-canonical-symbology-document-is-cim.md), [ADR-092](ADR-092-sprites-are-uploaded-per-service.md), [ADR-041](ADR-041-the-map-renderer.md), [ADR-054](ADR-054-the-symbology-document-is-not-bounded.md), [ADR-094](ADR-094-several-styles-and-allowed-origins.md), [ADR-098](ADR-098-vector-tiles-can-be-exported-as-a-package.md) |
| **Amends** | [ADR-052](ADR-052-the-canonical-symbology-document-is-cim.md) §3.8 — a picture marker is no longer refused. [ADR-092](ADR-092-sprites-are-uploaded-per-service.md) §5 — the served sheet carries generated icons beside the uploaded ones, an upload may not use their prefix, and the uploaded picture is decoded to compose the two. |
| **Supersedes** | — |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

A point layer authored in ArcGIS with an icon — a school, a hospital, a manhole — lost the icon here.

- **CIM.** A `CIMPictureMarker` fell to the reader's default branch and was reported as not drawn. A symbol
  made only of a picture was refused for having nothing to paint with; one with a picture over a circle
  drew the circle.
- **Esri `drawingInfo`.** An `esriPMS` was refused outright, citing ADR-027 condition 5: there was no sprite
  or image library.
- **The tile face.** [ADR-092](ADR-092-sprites-are-uploaded-per-service.md) let a publisher upload a sprite
  sheet per service and hand-write a style that draws from it. A layer's own symbology had no sheet, so the
  generated style could not draw its pictures, and ADR-092 §6 said so.

So the only way to put an icon on the tile face was to make a sprite sheet with another tool, upload it,
and write the style by hand — and the raster faces and the ArcGIS face still drew a circle.

What already existed made most of this a matter of joining pieces:

- [ADR-052](ADR-052-the-canonical-symbology-document-is-cim.md) stores a CIM renderer per layer and derives
  every face from it through one projection, so a new kind of symbol layer is read once.
- [ADR-054](ADR-054-the-symbology-document-is-not-bounded.md) removed the stored document's bound. What is
  left is the read bound of 2,097,152 characters per request.
- [ADR-041](ADR-041-the-map-renderer.md)'s `IMapCanvas` is the one port a raster face draws through, and its
  Skia adapter already holds a decoder.
- The per-class tile style ([D-280](../architecture-debt.md)) spreads any expression over a feature's
  attribute into one filtered style layer per class, which is the shape ArcGIS Pro draws.

## 2. Alternatives considered

### Alternative A — the picture inside the stored CIM, a sheet generated per service when asked for *(chosen)*

- A picture is stored as a `data:` URI in the `CIMPictureMarker`'s `url`, inside the layer's symbology
  document. No new table.
- The sprite routes build the service's sheet from its layers' documents when a client asks, pack every
  distinct picture under a name made from its content hash, and draw the uploaded sheet above them.
- The composed picture is kept in the process under a key made of everything it is drawn from.

**Argument for.**
- One store and one reading. The picture travels with the renderer through every face, the export and a
  copy of the document; nothing can hold a renderer without its pictures.
- The sheet cannot go stale. A service's layers change through many doors — a restyle, a rename, a layer
  moved out, a service deleted — and a sheet computed from what is there when a client asks has no door to
  miss.
- Names are content hashes, so two layers sharing a file share an icon, and an edited picture is a new name
  that no cached old sheet can answer for.

**Argument against.**
- The document grows by the pictures' base64. A megabyte of pictures is about 1.4 million characters, read
  on every request that draws the layer.
- The picture is composed by the server on the first request after a change, per process.

### Alternative B — a table of pictures, referenced from the document

**Argument for.** The document stays small; a picture shared by several layers is stored once.

**Argument against.**
- A document that names a picture by id is not self-contained: exporting, copying or diffing it needs the
  table beside it, and a deleted picture leaves a symbol that draws nothing.
- It needs a migration and its own lifecycle, for a size problem the read bound already contains: with a
  per-layer bound of one megabyte of pictures, the largest document is well inside 2,097,152 characters.

### Alternative C — generate the sheet when a symbology is stored, and store it

**Argument for.** A sprite request costs a read, as for an uploaded sheet.

**Argument against.** Every door that changes a service's layers would have to regenerate it, and a door
that forgets leaves a style naming icons the stored sheet lacks — the silent blank ADR-092 exists to
prevent. It also needs somewhere to store it, which is either a migration or a second use of
`service_sprite` that an upload would overwrite.

### Alternative D — MapLibre's multiple sprites, one per source of icons

The style specification allows `sprite` to be a list of `{id, url}`, each icon addressed as `id:name`.

**Argument for.** No merging: the uploaded sheet and the generated one stay two files.

**Argument against.** Esri's own vector styles name one sprite, and ArcGIS Pro is the client this work is
for. **INFERRED**, not measured: that Pro does not read a list. A style Pro cannot draw is the failure
[D-280](../architecture-debt.md) recorded, and the single sheet is the shape known to work.

### Alternative E — fetch a picture named by URL

**Argument for.** Pro and ArcGIS Online write picture markers with URLs, and accepting them would store
more documents unchanged.

**Argument against.** A server that requests whatever address a document names can be pointed at its own
network — server-side request forgery. [ADR-094](ADR-094-several-styles-and-allowed-origins.md)'s allow-list
is about where a browser is sent, not what this process reaches for. Refused, with the reason and the way
forward: put the picture in the document.

## 3. Counterarguments to the preferred option

- **The server now decodes images.** [ADR-092](ADR-092-sprites-are-uploaded-per-service.md) §2C kept a
  decoder out, because an image decoder is an attacker-facing binary parser. Here one runs on pictures a
  publisher stored and on an uploaded sheet. What limits it: the size is read from the header before any
  decoder runs (at most 512 × 512 and 256 KB for a marker; ADR-092's 4096 and 8 MB for a sheet); the decoder
  is asked for its own size and a disagreement with the header is not decoded; only a publisher with
  `content:publishFeatures` who manages the layer can store one. The decoder is Skia's, the one that already
  encodes every map.
- **A style read back can name only pictures the layer already draws.** A MapLibre style carries a name, not
  a picture. The names this server can turn back into pictures are its own generated ones, for pictures the
  layer's stored symbology holds. Changing a class's picture through a style is refused with the way forward:
  send CIM or a `drawingInfo`, which carry the picture.
- **The tile face cannot stretch a picture.** `icon-size` is one number, so a `scaleX` other than 1 is drawn in
  the picture's own proportions there and reported as a loss; the raster faces and the Esri face keep it.
- **A tint and an off-centre anchor are not drawn.** Both are kept in the stored document and reported.
- **Where an offset goes when the picture is turned is INFERRED.** The style specification turns
  `icon-offset` with `icon-rotate`; the raster faces do the same, so the two agree. Whether ArcGIS turns the
  offset too is not in anything it publishes.
- **Nothing is measured in a client.** No ArcGIS Pro, JavaScript SDK, Field Maps or MapLibre client has drawn
  one of these layers from this server. The shapes are the ones Esri's own styles and documents use, which is
  a reason, not a measurement.
- **A service with a great many pictures makes a tall sheet.** Icons are packed at most 128 pixels a side in
  rows 1,024 wide, so about 128 distinct large pictures fill the 4,096 pixels a GPU is sure to hold at @2x.
  Past that the sheet is served anyway and some clients may draw no icons ([D-287](../architecture-debt.md)).
- **The composed sheet is kept per process.** Two servers compose the same bytes from the same inputs with the
  same build, so their ETags agree; a mixed-version pair may not, and a client then refetches once.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| A PNG or JPEG is sized from its header and refused past the bounds before anything decodes it; a URL, an SVG and a GIF are refused with a reason | `PictureMarkerTests` (Core): data URI, JPEG start of frame, a 600-pixel PNG, a JPEG header declaring 20,000 × 20,000 with no body, base64 over the byte bound, `https://`, `http://10.0.0.1`, `//host`, a relative name, SVG declared and sniffed, GIF, a declaration that disagrees with the bytes | this change |
| The per-layer bound counts each picture once | `PictureMarkerTests`: five 250 × 250 noise pictures refused together; one picture in forty classes accepted | this change |
| `esriPMS` is read from `imageData` and written back with it | `PictureMarkerTests`, `CimEsriTests.A_picture_symbol_that_only_names_its_file_is_refused_because_nothing_is_fetched`, `PictureMarkerFacesTests` (FeatureServer layer document) | this change |
| The tile style draws one `symbol` layer per class with a literal `icon-image` and no expression over a field, and reads back into the same renderer | `PictureMarkerTests`: the simple and unique-value shapes, `icon-size` arithmetic, and the round trip publishing the same style twice with offsets and rotation | this change |
| The generated sheet holds every picture once, its index matches its picture at both ratios, and it is deterministic | `PictureMarkerTests` (packing without overlap, order-independent; index below an uploaded sheet dropping a reserved name), `GeneratedSpriteSheetTests` (Host, composed by Skia and read back) | this change |
| The raster faces draw the picture where the point is, the right way up, turned clockwise, offset, faded, and draw nothing for a picture that does not decode | `PictureMarkerDrawingTests` (Render.Skia), read from pixels | this change |
| A stored picture the server cannot use is a loss, not a fallback to the generated appearance; `migrate` keeps a URL-only `esriPMS` as a loss and carries `imageData` with its picture | `PictureMarkerTests.A_stored_picture_named_by_url_is_a_loss_and_the_rest_of_the_renderer_projects`, `…A_stored_picture_beside_other_layers_is_dropped_as_the_old_reader_dropped_it`, `…The_migrate_path_keeps_a_url_only_picture_as_a_loss_and_carries_image_data_with_its_picture`; `MigrationPlanTests.Apply_asks_the_symbology_route_to_keep_a_picture_it_cannot_use` | this change |
| Every face on a live server: stored, `esriPMS` in the layer document, `symbol` layer in `root.json`, the icon in `sprite.json` and `sprite@2x.json` with a rectangle inside the PNG, a MapServer export and legend that differ from the circle's | `PictureMarkerConformanceTests` | this change. **Written, compiled, and not yet run against a live server when this ADR was written** |
| MapLibre reads `pixelRatio` per icon, scales `icon-offset` by `icon-size`, and turns it with `icon-rotate` | [MapLibre style specification](https://maplibre.org/maplibre-style-spec/) — `sprite`, `layers` › `symbol` | public specification |
| `CIMPictureMarker` carries `url`, `size`, `scaleX`, `rotation`, `offsetX`, `offsetY`, `anchorPoint`, `tintColor`; `esriPMS` carries `url`, `imageData`, `contentType`, `width`, `height`, `angle`, `xoffset`, `yoffset`, with `angle` counter-clockwise | `github.com/Esri/cim-spec` (`CIMSymbols.md`); the ArcGIS REST API and web map specification's symbol objects | public specifications |

## 5. Decision

A layer's picture markers are stored inside its CIM renderer and drawn on every face: the raster faces
draw the picture, the Esri face publishes it as `esriPMS` with its image, and the tile face draws it from
a sprite sheet the server generates for the service.

### 5.1 What is accepted

- **CIM.** A `CIMPictureMarker` whose `url` is a `data:` URI, base64, of a PNG or a JPEG. `size` is the
  height in points; `scaleX` stretches the width; `rotation` is counter-clockwise unless `rotateClockwise`
  says otherwise, and is stored counter-clockwise; `offsetX` and `offsetY` are points, `y` upward.
- **Esri `drawingInfo`.** An `esriPMS` with `imageData` and `contentType`. `width` and `height` are points;
  the height becomes `size` and a width out of proportion becomes `scaleX`; `angle`, `xoffset` and `yoffset`
  are copied. Without `imageData`, a `url` is read only when it is a `data:` URI.
- **MapLibre.** A `symbol` layer with an `icon-image` naming one of this server's generated icons for a
  picture the layer draws now; `icon-size`, `icon-offset` and `icon-rotate` are read back into size, offset
  and rotation. A point layer may be drawn by icons alone.
- **Refused, with the reason:** a URL of any kind (never fetched); SVG (no SVG reader, and an SVG can name
  other resources); anything that is not a PNG or a JPEG by its own bytes; a declared type that disagrees
  with the bytes; a picture over **256 KB** or **512 pixels** on a side; a layer whose distinct pictures
  together are over **1 MB**. An `esriPFS` — a picture *fill* — is still refused.
- **Reported, not drawn:** a tint other than white; an anchor off the centre; `scaleX` on the tile face.
- **Refused on a write, a loss on a read.** Every refusal above happens where a document is written — the
  symbology `PUT`, its preview — with the reason. A document already **stored** is read with an unusable
  picture as a loss, reported where losses are reported (the read-back's `losses` and `styleLosses`): the
  rest of the symbol and the renderer project normally. The picture layer is left out, as the reader before
  ADR-099 left it out; where it was the symbol's **only** layer, a grey marker of its size is drawn instead,
  because the old answer — *no layer this server can paint with* — sent the whole layer to its generated
  appearance, which changes a live layer's look with nobody asking. In a unique-value renderer whose other
  classes have usable pictures, that class draws no icon on the tile face and the raster faces rather than
  another class's picture, and the differing stack is reported.
- **`graticula tools migrate`** stores each layer's drawing with `?pictures=keep`. An `esriPMS` with
  `imageData` migrates with its picture. One the source gives only by `url` — or an SVG, or one past the
  bounds — is kept in the stored document as it was given (the URL is never fetched), reported as a loss,
  and read as above. Before this, such a drawing was refused whole and the layer drew its generated
  appearance.

### 5.2 Storage

In the stored CIM renderer, as it arrived. No table and no migration: the per-layer bound keeps the
largest document inside the read bound, and ADR-054 left the column unbounded.

**Derived once per document text.** Measured 2026-09-29 on a stored renderer carrying 790 KB of pictures
(1.14 million characters), per request: 2.2 ms to parse, 3.0 ms to decode, sniff and hash the pictures,
6.1 ms for the FeatureServer `drawingInfo`, 4.8 ms for the tile style, 7.5 ms to compile for a map. So a
document carrying a picture marker has its `drawingInfo`, tile style and compiled plan kept per exact text
(`PictureDocuments`, at most 16 million characters of keys, emptied when full), and a picture read from a
`data:` URI is kept per exact URI (`MarkerPicture`, 8 million characters). A hit is **0.9 ms** for the
`drawingInfo` when the text is a fresh string, and 0.7 ms when it is the same instance — the cost of hashing
and comparing the key and cloning the answer. A document without pictures is derived as before.

### 5.3 The raster faces

`SymbologyPlan` compiles the tile face's own expression form, as ADR-052 §3.5 has it do for every
renderer, so an icon layer is drawn by `PlanLayer.Icon`: the icon named by `icon-image` is looked up among
the pictures the stored renderer carries; its size is its height in the sheet times `icon-size`, as a
client computes it; `icon-offset` is multiplied by `icon-size`; `icon-rotate` is clockwise. `IMapCanvas`
gains `DrawPicture`, which takes the encoded picture and is implemented on Skia with a decode cache keyed
by content hash. So MapServer `export`, WMS `GetMap`, `GetLegendGraphic`, the MapServer legend, the
thumbnails and the symbology preview all draw it. A legend swatch draws the picture centred
and shrunk to fit.

### 5.4 The tile face and the generated sprite sheet

- **The style.** A picture's symbol layer is a `symbol` style layer with `icon-image` (the picture's
  name), `icon-size` (the marker's height in 96-dpi pixels over the icon's height in the sheet),
  `icon-offset` and `icon-rotate` when a class needs them, and `icon-allow-overlap` and
  `icon-ignore-placement` true, because ArcGIS draws every point. Classes are spread into filtered layers
  as for every other renderer (D-280), with `layout` settled per class as `paint` is. The generated style
  names `sprite` whenever an icon layer is in it, whether or not the server has glyphs.
- **Names.** `graticula-` and 24 hex characters of the SHA-256 of the picture's bytes.
- **The sheet.** Each distinct picture of every layer of the service, ordered by name, at its own size or
  shrunk so its longer side is 128 pixels, packed in shelves 1,024 pixels wide with a one-pixel gap. The
  @2x sheet holds the same icons at twice the size, resampled from the picture, at `pixelRatio` 2.
- **Merged with an uploaded sheet.** The uploaded picture is drawn at the top-left unmoved and the generated
  block below it; the uploaded index keeps every member it was sent with; each generated icon states its own
  `pixelRatio`, so a @2x sheet that falls back to an uploaded 1x picture carries both. An upload whose index
  names an icon beginning `graticula-` is refused. A service with no picture markers serves exactly what it
  served before.
- **Consistency.** A stored style is checked against the uploaded icons and the generated ones, where it is
  stored and where it is served. A generated icon never holds an uploaded sheet: replacing or removing the
  upload is not refused for it.
- **Caching.** The index is computed per request. The picture is composed on the first request after
  anything it depends on changes, and kept in the process — at most 32 — under a key of the service, the
  ratio, the uploaded sheet's own stamp and the generated names. The ETag is the bytes', as before.
  `TilePipeline.Version` does not move: no byte of a tile changed.
- **Export.** A VTPK carries the sprite files the routes serve, from the same code
  ([ADR-098](ADR-098-vector-tiles-can-be-exported-as-a-package.md)). A PMTiles archive carries no style or
  sprite, as before.

### 5.5 The Esri face

A symbol whose topmost marker is a picture is published as `esriPMS` with `imageData`, `contentType`,
`width` and `height` in points, `angle`, `xoffset` and `yoffset`, and `url` set to the picture's name. Other
layers of the stack are reported as not carried, as a stack always is.

### 5.6 The console

Studio › layer › Symbology has **+ Picture** beside *+ Fill*, *+ Stroke* and *+ Marker*. It opens a file
chooser for PNG or JPEG, checks the same bounds before the file is used, and puts the picture on top of the
class's stack as a data URI. A picture's row shows the picture, its size in points and *Replace…*.

### 5.7 What this changes elsewhere

- [ADR-052](ADR-052-the-canonical-symbology-document-is-cim.md) §3.8: a picture marker is drawn, not refused.
- [ADR-092](ADR-092-sprites-are-uploaded-per-service.md) §5: the served sheet is the uploaded one with the
  generated icons below it; the uploaded picture is decoded to compose them; the reserved prefix.

### 5.8 INFERRED, listed for confirmation

- The prefix `graticula-` rather than the owner's example `g-`, because a two-letter prefix is one real icon
  sets use and an existing upload would start being refused.
- The bounds: 256 KB and 512 pixels a picture, 1 MB a layer, 128 pixels a side in the sheet, rows of 1,024,
  32 composed sheets kept per process.
- That @2x is resampled from the picture rather than the 1x icon served again.
- Composing on request rather than on store (Alternative C).
- That an offset turns with the picture (§3).
- That ArcGIS Pro draws a single-sprite `symbol` layer with `icon-size` (Alternative D).

## 6. Consequences

**Positive.**
- A picture marker authored in ArcGIS Pro, pasted from a `drawingInfo` or chosen in the console is drawn by
  every face, and the tile face no longer needs a hand-made sheet and a hand-written style for it.
- The vector tile face's *Partial* no longer rests on picture markers.
- An uploaded sheet and a layer's pictures live in one served sheet, and each keeps its own rules.

**Negative.**
- Pictures are decoded in the server process (§3).
- Documents grow by their pictures, and every request that draws a layer reads them.
- The composed sheet costs one decode and encode per process after each change.
- A picture can be changed through a style only by sending the picture itself (§3).
- No client has drawn one yet.

**Ports created.** None. `IMapCanvas` gains `DrawPicture`, the one method on the port that takes encoded
bytes; decoding is the rasteriser's, as the font is.

**State.** *Catalogue*: none new; pictures are inside the symbology column. *Runtime*: a decode cache in the
Skia adapter (at most 512 marker-sized pictures, emptied when full) and the composed-sheet cache in the host
(at most 32, emptied when full), both registered in `EveryLongLivedCacheIsBoundedTests` or bounded by
construction.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | ArcGIS Pro draws a `symbol` layer with `icon-image` and `icon-size` from a single sprite sheet | Unvalidated; Esri's own styles use the shape |
| — | ArcGIS Pro and the JavaScript SDK draw an `esriPMS` from `imageData` without fetching `url` | Unvalidated; Esri's documents say `imageData` is the image |
| — | Real picture markers are icons of tens to a few hundred pixels and kilobytes | Reasoned, not measured on this product's users |

## 8. Dependencies

**Depends on:**
- [ADR-052](ADR-052-the-canonical-symbology-document-is-cim.md), whose single projection reads the picture
  once for every face.
- [ADR-092](ADR-092-sprites-are-uploaded-per-service.md), whose routes, checks and store this extends.
- [ADR-041](ADR-041-the-map-renderer.md), whose port gains `DrawPicture`.
- [ADR-054](ADR-054-the-symbology-document-is-not-bounded.md), which lets a document carry its pictures.
- [ADR-098](ADR-098-vector-tiles-can-be-exported-as-a-package.md), whose package carries the sheet.

**Depended on by:** —

## 9. Revisit triggers

- ArcGIS Pro, the JavaScript SDK or Field Maps is watched drawing one of these layers and does not. The
  shapes in §5.4 and §5.5 are then wrong for that client.
- A service's generated block passes 4,096 pixels at @2x ([D-287](../architecture-debt.md)).
- Somebody asks for picture fills, SVG pictures or a picture chosen through a style. Each is its own decision.
- Somebody asks to share one set of pictures across layers without storing each copy. Alternative B comes back.

## 10. Dissent

None recorded.
