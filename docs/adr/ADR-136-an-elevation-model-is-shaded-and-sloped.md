# ADR-136 — An elevation model is shaded, sloped and given its aspect

| | |
|---|---|
| **Status** | `ACCEPTED` |
| **Confidence** | `HIGH` — the arithmetic is checked on planes whose answers are known in closed form, and end to end on a model whose slope is known |
| **Decided** | 2026-10-02 by owner decision (*"Başla"*, answering whether to build raster functions, recommended as the next imagery item) |
| **Supersedes** | — |
| **Superseded by** | — |
| **Amends** | [ADR-123](ADR-123-imagery-comes-into-studio.md) (§5.1: `renderingRule` is applied for three functions), [ADR-043](ADR-043-imageserver-and-the-raster-face.md) (`allowRasterFunction`) |


> **Amended 2026-10-02 by [ADR-137](ADR-137-an-image-service-answers-lerc.md).** `rasterFunctionInfos` is served as an operation too: the JS SDK reads it there whenever `allowRasterFunction` is true, and did not load while it was refused.


> **Amended 2026-10-03 by [ADR-151](ADR-151-ndvi-band-arithmetic-and-a-choice-of-bands.md).** NDVI, BandArithmetic and ExtractBand are served beside these, pixel by pixel, for images of several bands.

---

## 1. Context

An owner who uploads an elevation model wants it as relief, as slope, as the way the ground faces — the first thing a
DEM is for. ADR-123 refused every `renderingRule` rather than draw the plain image as if a function had been applied.
ArcGIS clients ask an image service for exactly these by `renderingRule`, and Map Viewer offers them on an imagery
layer.

## 2. Alternatives considered

### Alternative A — Hillshade, Slope and Aspect, computed by this server (chosen)

Horn's third-order finite difference over each cell's eight neighbours, the method Esri documents for its own tools,
applied to the first band:

- **Hillshade** — 0 to 255, a sun at an azimuth and an altitude (315° and 45° by default, as ArcGIS's), drawn grey.
- **Slope** — degrees, 0 to 90, drawn flat green to red at 45° and steeper (ordinary terrain is under 15°, and a 0–90° ramp drew it one green).
- **Aspect** — compass degrees, clockwise from north; a flat cell faces nowhere and is left clear.

The cell size is in metres, a geographic model's degrees converted at the window's latitude. A window is read a cell
wider on every side the file allows and cut back, so a tile's edge is not shaded as if the ground stopped there. The
function applies wherever the image is read: `exportImage` (a picture, or the function's values as a 32-bit GeoTIFF),
tiles, and `identify` (the slope at the point, not the height). The service names them in `rasterFunctionInfos` with
`allowRasterFunction: true`; any other function is refused by name, and so is a function on a colour image.

The owner may show the service through one by default, on its Display page (`;function:<name>` in the stored style,
beside the colour settings, which are kept for when it is set back). An empty rule or `{}` asks for the service's own
drawing — its default function, if it has one — and `{"rasterFunction":"None"}` for the values themselves; `identify`
follows the same rule and says which function answered (`rasterFunction`), and the service names its default
(`defaultRasterFunction`, a field of ours) so a viewer can say what "the service's own drawing" is. The Map Viewer
offers the functions on a one-band image service that declares them — not on an 8-bit image, which is a picture rather
than heights — as the Web Map's own `renderingRule`, with a key, and labels a clicked value by its function.

### Alternative B — GDAL's `gdaldem`

**For:** established. **Against:** a child process for every tile, and Tier 1 cartographic logic of a few dozen lines;
build-vs-adopt keeps it ours.

## 3. Counterarguments to the preferred option

- Three functions, not ArcGIS's catalogue. NDVI, band arithmetic, colormaps are refused by name; the list is the
  service's own `rasterFunctionInfos`, so a client asks only for what is served.
- Slope on an overview is the slope of the coarser grid: the steepness a picture at that scale shows, as ArcGIS's
  dynamic functions give it.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| A plane rising one cell size a cell is 45° steep; aspect is the compass direction faced, in the four directions; a flat cell is lit by the altitude alone and faces nowhere; an absent neighbour gives no answer | `RasterFunctionTests` | this repository |
| On a model with a known gradient: slope a little over a degree, aspect north-west, hillshade grey, identify through Slope; NDVI refused; the owner's default function draws the slope's colours | `ImageryDisplayTests` | this repository |
| The Map Viewer shows an imagery layer through a function and keeps it in the map | `WebMapImageryTests` | this repository |

## 5. Decision

An image service applies Hillshade, Slope and Aspect to its first band wherever it is read, by `renderingRule` or by
its owner's default; other functions are refused by name.

## 6. Consequences

**Positive.** An elevation model is useful the day it is uploaded, in Studio and in ArcGIS clients.

**Negative.** None known.

**State.** None new: the default rides in the stored style's text.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | Raster functions are wanted next | Stated by the owner, 2026-10-02 |

## 8. Dependencies

**Depends on:** ADR-043, ADR-123, ADR-127.

**Depended on by:** —

## 9. Revisit triggers

- A client measured asking for a function chain (`rasterFunction` with a `Raster` argument that is itself a function).
