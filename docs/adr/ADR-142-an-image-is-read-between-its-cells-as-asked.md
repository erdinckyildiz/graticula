# ADR-142 — An image is read between its cells as the request asks

| | |
|---|---|
| **Status** | `ACCEPTED` |
| **Confidence** | `HIGH` — the arithmetic is checked on ramps whose answers are known, and end to end on a model |
| **Decided** | 2026-10-03 by owner decision (*"devam et … image layers'ın kalan işlerini tamamla"*, the imagery list's next item) |
| **Supersedes** | — |
| **Superseded by** | — |
| **Amends** | [ADR-123](ADR-123-imagery-comes-into-studio.md) (§3: `interpolation` is applied, no longer read and ignored), [ADR-127](ADR-127-an-image-service-exports-its-values.md) (values may be read between cells when asked) |

---

## 1. Context

ADR-123 read `interpolation` and did not apply it. Measured on 2026-10-03, what the service did was three different
things: a picture in the image's own reference was scaled bilinear by the canvas whatever was asked; a picture in
another reference — Web Mercator, every web map — was nearest neighbour, blocky up close, whatever was asked; and the
values were always nearest. The service document said `defaultResamplingMethod: "Bilinear"`, true for the first case
only.

## 2. Alternatives considered

### Alternative A — Apply what is asked; say the default truthfully (chosen)

- **`interpolation`** is applied on every path: `RSP_NearestNeighbor`, `RSP_BilinearInterpolation` and
  `RSP_CubicConvolution` (Catmull-Rom). `RSP_Majority` is refused by name — for classes, nearest keeps every value one
  the image holds — and so is any other name.
- **The default, when the request does not say**: nearest for one band of bytes, which may be classes, and for a
  display rule that colours values (ADR-138's class colour maps); bilinear for everything else — heights, photographs,
  raster functions' results. `defaultResamplingMethod` states it, per service.
- **Values (TIFF, LERC) stay nearest unless asked**, so a value in the file is one the image holds (ADR-127); asked,
  they are interpolated, and a cell next to no-data takes its nearest value rather than a blend with −9999.
- Colours are blended premultiplied, so a no-data edge fades rather than darkens.

### Alternative B — Bilinear everywhere

**Against:** a land-cover class blended with its neighbour is a class that does not exist (CoverageWarp's own note).

## 3. Counterarguments to the preferred option

- *Pro and the JS SDK send `RSP_NearestNeighbor` by default*, so an ArcGIS client gets nearest unless its user chooses
  otherwise — as against ArcGIS Server. Studio's Map Viewer sends nothing and gets the default.
- *Cubic overshoots near the image's edge*, where its sixteen neighbours are clamped.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| Nearest takes the cell; bilinear weighs four; cubic passes through centres and is exact on a ramp away from its edge; a no-data neighbour falls back to nearest; a transparent neighbour fades a colour | `ResamplerTests` | this repository |
| A float model says Bilinear; a 4 × 4 TIFF over 2 × 2 cells holds two values nearest and values between them bilinear; Majority is refused by name | `ImageryDisplayTests` | this repository |
| Pro's replayed request with `interpolation=RSP_NearestNeighbor` still draws | `ImageServerConformanceTests` | this repository |

## 5. Decision

`interpolation` is applied to pictures and values, with the per-service default of §2 stated in
`defaultResamplingMethod`.

## 6. Consequences

**Positive.** An elevation model or photograph is smooth up close in a web map; a class image stays its classes.

**Negative.** None known.

**State.** None.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | The imagery list is worked through in order | Stated by the owner, 2026-10-03 |

## 8. Dependencies

**Depends on:** ADR-123, ADR-127, ADR-138.

**Depended on by:** —

## 9. Revisit triggers

- An owner needing to set a service's default resampling, rather than having it follow the image's type.
