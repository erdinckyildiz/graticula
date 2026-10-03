# ADR-151 — NDVI, band arithmetic and a choice of bands

| | |
|---|---|
| **Status** | `ACCEPTED` |
| **Confidence** | `MEDIUM` — the arithmetic is checked against closed-form answers; ArcGIS's defaults for NDVI's scale are read from its documentation, not measured against a running ArcGIS |
| **Decided** | 2026-10-03 by owner decision (*"Bunları da ekleyelim"*, then *"Tüm maddeleri yap"* — the imagery list's item 20) |
| **Supersedes** | — |
| **Superseded by** | — |
| **Amends** | [ADR-136](ADR-136-an-elevation-model-is-shaded-and-sloped.md) (which functions are served), [ADR-138](ADR-138-a-renderer-sent-as-a-rule-is-drawn.md) (what a display rule may be laid over) |


> **Amended 2026-10-03 by [ADR-156](ADR-156-clip-remap-mask-statistics-arithmetic.md).** Clip, Remap, Mask, Statistics and Arithmetic are served beside these.


> **Amended 2026-10-03 by [ADR-158](ADR-158-a-mosaic-combines-and-is-reordered.md).** The band functions are offered in the Map Viewer too, as a layer's *Shown as*, with their bands chosen there.

---

## 1. Context

An image service drew Hillshade, Slope and Aspect, and refused every other raster function and any `bandIds` but the
bands as they are. Whoever works with satellite imagery asks first for NDVI, an index of their own, and a false-colour
combination — the commonest raster functions ArcGIS clients send after the stretch.

## 2. Alternatives considered

### Alternative A — Pixel functions in the same pipeline as the surface functions (chosen)

Three functions, by ArcGIS's names and arguments, applied wherever ADR-136's are — the picture, the raw export (TIFF,
LERC), `identify`, `getSamples` and `computeStatisticsHistograms`:

- **`NDVI`** — `VisibleBandID` and `InfraredBandID`, from zero. ArcGIS's default answer is 0 to 200 (the index × 100 +
  100); `Scientific: true` answers −1 to 1. Drawn on a brown-to-green ramp over its range. An absent band, or red and
  infrared summing to zero, is no value.
- **`BandArithmetic`** — `Method` 0 takes `BandIndexes` as an expression over `B1`…`Bn` (from one, as ArcGIS writes
  them): numbers, + − × ÷, unary minus, parentheses. Method 1 (NDVI, `"NIR Red"`) and 2 (SAVI, `"NIR Red L"`) are
  written as that expression. Any other method is refused, saying it can be written as Method 0. Division by zero is
  no value. Drawn stretched over what is in view.
- **`ExtractBand`** — `BandIDs`, from zero, in the order asked; the bands keep their own type and no-data. `bandIds`
  on `exportImage` and `identify` asks the same and is read as it. `BandNames` is refused: this server's images carry
  none.

A band the image does not have is refused counting as ArcGIS counts — "0 to 3", "B1 to B4". A surface function on a
colour image is refused as before; NDVI on one is what it is for.

**A display rule over a function.** The JS SDK lays a renderer over a layer's raster function as Stretch over NDVI;
ADR-138's rule now draws the function under it, stretched by the function's statistics rather than the image's. A
display function under a display function is still refused.

**Studio.** Settings › Display offers, for an image of several bands, *Band combination* (three bands as red, green and
blue), *NDVI* (a red and a near-infrared band; stored as −1 to 1) and *Band arithmetic* (an expression), each with its
own controls and refusals beside them; the service's default function is stored with its arguments
(`ndvi:2:3:s`, `extractband:3,2,1`, `bandarithmetic:…`). The Map Viewer shows a service drawn through NDVI with its key.

### Alternative B — Only `bandIds`, leaving the indices to the client's pixel filter

**Against:** a pixel filter is the JS SDK's alone; Pro, the REST directory and every other client ask the server.

## 3. Counterarguments to the preferred option

- *Map Viewer's per-layer function list offers only the surface functions*; a band function there needs its bands
  chosen, and is not built. A web map saved elsewhere with an NDVI rule is drawn.
- *A band arithmetic expression's range is unknown*, so it is stretched over what is in view, and two tiles of one map
  may stretch differently — as ArcGIS's dynamic range adjustment does.
- *Composite Bands*, which combines bands of several images, is not one of these: an image service here is one image.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| NDVI 0.5 is 150 by default and 0.5 when scientific; an absent band is no value; SAVI and an own expression agree with closed form; division by zero and an unreadable expression are refused or empty, naming the part | `BandFunctionTests` | this repository |
| Over HTTP, on a four-band image: identify answers 0.5, 0, 150, 70 and "90 30" for NDVI, ArcGIS's NDVI, an expression and `bandIds=3,2`; the statistics of an area through NDVI run 0 to 0.5; Stretch over NDVI draws; a fifth band is refused "0 to 3"; a stored NDVI default is the service's | `BandFunctionConformanceTests` | this repository |

## 5. Decision

As §2.

## 6. Consequences

**Positive.** Multispectral imagery is useful from the first request.

**Negative.** Map Viewer cannot yet choose a band function per layer.

**State.** None new: the default is in the coverage's style text.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | ArcGIS's NDVI defaults to 0–200 and `Scientific` gives −1 to 1 | INFERRED from Esri's raster function documentation, not measured |

## 8. Dependencies

**Depends on:** ADR-136, ADR-138.

**Depended on by:** —

## 9. Revisit triggers

- A Map Viewer user choosing NDVI per layer.
- A client whose NDVI request does not match the scale assumed in §7.
