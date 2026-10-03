# ADR-156 — Clip, Remap, Mask, Statistics and Arithmetic

| | |
|---|---|
| **Status** | `ACCEPTED` |
| **Confidence** | `MEDIUM` — each is checked against closed-form answers; ArcGIS's enumerations for Statistics' and Arithmetic's types are read from its documentation |
| **Decided** | 2026-10-03 by owner decision (*"Tüm maddeleri yap"* — the imagery list's item 23) |
| **Supersedes** | — |
| **Superseded by** | — |
| **Amends** | [ADR-151](ADR-151-ndvi-band-arithmetic-and-a-choice-of-bands.md) (the functions served) |

---

## 1. Context

After ADR-151 the raster functions an image service applied were the surface three and the band three. The next most
asked are cutting an image to an area, classifying it, masking it by value, smoothing it over a neighbourhood and
scaling it.

## 2. Alternatives considered

### Alternative A — Five more functions in the same pipeline (chosen)

By ArcGIS's names and arguments, wherever the others apply:

- **`Clip`** — `ClippingGeometry` (a polygon or an envelope in the image's reference) and `ClipType` 1, keeping what is
  inside, or 2, what is outside; outside is no value, and the image is drawn as it is. `ClippingRaster` is refused.
- **`Remap`** — `InputRanges` (low inclusive, high exclusive), `OutputValues`, `NoDataRanges`, `AllowUnmatched`. By
  itself it is a raster function; under a Colormap it is still ADR-138's display rule.
- **`Mask`** — `IncludedRanges`, a low and a high for each band; a pixel outside any is no value.
- **`Statistics`** — `Type` 1 minimum, 2 maximum, 3 mean, 4 standard deviation, 5 median, 6 majority, 7 minority, over
  `KernelColumns` × `KernelRows`, odd, up to 15; the window is read half a kernel wider, as a slope is read a cell
  wider, so a tile's edge is not an edge of the data.
- **`Arithmetic`** — `Operation` 1 plus, 2 minus, 3 times, 4 divided by, and `Raster2` a number: each band against it.
  A second raster is refused, an image service being one image.

Remap, Statistics and Arithmetic make new values and are stretched over what is in view; Remap and Statistics are of
one band, and refused on a colour image.

### Alternative B — Stop at the six

**Against:** a classification and a clip are what a GIS analyst asks of imagery before anything else.

## 3. Counterarguments to the preferred option

- *None is offered in Studio's Display*; they are asked for by clients, as ArcGIS's are. A stored default would carry a
  geometry or a table a style does not hold.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| Each function's answer on a 3 × 3 window, and each refusal naming what it takes | `ValueFunctionTests` | this repository |
| Over HTTP on a surface rising 10 a column: Remap 2, a mean of 30, 30 × 2 = 60, masked and clipped cells NoData, clipped-outside 30, a clipped picture drawn | `ClassesAndMensurationConformanceTests` | this repository |

## 5. Decision

As §2.

## 6. Consequences

**Positive.** Classification, clipping and smoothing are a request, not a download.

**Negative.** Not chosen in Studio.

**State.** None.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | ArcGIS numbers Statistics' types 1–7 and Arithmetic's operations 1–4 as here | Read from its documentation |

## 8. Dependencies

**Depends on:** ADR-136, ADR-151.

**Depended on by:** —

## 9. Revisit triggers

- A client asking for a function still refused — Curvature, Convolution, a second raster.
