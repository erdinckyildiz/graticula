# ADR-137 — An image service answers LERC

| | |
|---|---|
| **Status** | `ACCEPTED` |
| **Confidence** | `HIGH` — every blob type opens in Esri's own decoder with the values it was given, and the JS SDK renders it |
| **Decided** | 2026-10-02 by owner decision (*"Devam et"*, answering a report that recommended LERC as the next imagery item) |
| **Supersedes** | — |
| **Superseded by** | — |
| **Amends** | [ADR-127](ADR-127-an-image-service-exports-its-values.md) (LERC is no longer refused), [ADR-136](ADR-136-an-elevation-model-is-shaded-and-sloped.md) (`rasterFunctionInfos` is an operation as well as a field) |

---

## 1. Context

ADR-127 wrote the values as TIFF and left LERC refused, with a trigger: *the JS SDK's `ImageryLayer` is measured
refusing to render without LERC*. That was measured on 2026-10-02 against the ArcGIS Maps SDK for JavaScript 4.31
pointed at this server, and the premise was half wrong:

- **A plain `ImageryLayer` does not need LERC.** It asks `exportImage` for `format=jpgpng` and draws it.
- **An `ImageryLayer` that renders on the client does.** One with a `pixelFilter`, or with `format: "lerc"`, asks
  `format=lerc&lercVersion=2&compressionTolerance=0.01`, and was refused: the map drew nothing.
- **And it did not load at all on v1.0.277.** ADR-136 set `allowRasterFunction: true`, and the SDK then asks the
  service's `rasterFunctionInfos` *operation*, which this server did not serve. The field in the root was not enough.

## 2. Alternatives considered

### Alternative A — Lerc2 written by this server (chosen)

`format=lerc` answers Lerc2, blob version 3, as Esri publishes the format (github.com/Esri/lerc, Apache-2.0): a
header with a Fletcher-32 checksum, a run-length coded mask of the pixels that hold a value, and the values in 8×8
blocks, each written constant, raw, or quantized to the client's `compressionTolerance` and bit-stuffed, whichever is
smaller. One blob a band. Integer values are always exact; floating-point values are exact when no tolerance is named.
The values are the TIFF export's: the same plan, the same nearest-neighbour warp, the same raster function — and
ground outside the image, or at its no-data value, is masked out rather than given a value.

`LercWriter` is written from the format as Esri publishes it in that repository. **No code was copied** (ADR-030's
disclosure: the derivation is the published format, Apache-2.0, not a reference product), and the result is checked
against Esri's own decoders rather than trusted.

### Alternative B — Esri's LERC library through P/Invoke

**For:** the reference encoder. **Against:** a native library on every platform this ships to (arm64 included), for a
writer of some four hundred and fifty lines whose output the reference decoder checks; raster I/O is Tier 2 and permits the
library, [build-vs-adopt](../build-vs-adopt-policy.md) does not require it.

### Alternative C — Leave it refused

**Against:** the measurement above — a client-rendered imagery layer draws nothing.

## 3. Counterarguments to the preferred option

- *Version 3 only.* Lerc1 (`lercVersion=1`) is refused by name: every LERC decoder since 2016 reads Lerc2 and tells the
  two apart by the blob's own key, so an absent `lercVersion` is answered with Lerc2.
- *No Huffman coding for 8-bit images*, which Esri's encoder may choose. The blocks are what every decoder reads; an
  8-bit export is at most 4096² bytes before the blocks compress it.
- *A quantized float can be a float32 rounding over the tolerance* (0.0100021 at 0.01, near 100) — the decoder's own
  arithmetic, and Esri's encoder has it too.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| A plain `ImageryLayer` asks `jpgpng` and draws; one with a `pixelFilter` or `format: "lerc"` asks LERC and drew nothing while it was refused; `allowRasterFunction` without the operation stopped the layer loading | ArcGIS Maps SDK for JavaScript 4.31 pointed at the fixture, requests and a screenshot's pixels recorded | local run, 2026-10-02 |
| Every type (8, 16 and 32-bit integers, 32 and 64-bit floats), a mask, three bands, NaN, a constant image and an empty one decode in Esri's decoder to the values written, exactly or within the tolerance, and the checksum agrees | 12 blobs decoded by the `lerc` 4.2.0 npm package | local run, 2026-10-02 |
| An export of `ci_imagery` as LERC is the TIFF export's values: 55,348 pixels agree, none differ, 10,652 outside the image are masked out; 41 KB against 66 KB | the same decoder | local run, 2026-10-02 |
| The JS SDK renders the LERC on the client: the `pixelFilter` is called and the map is drawn | the 4.31 probe above | local run, 2026-10-02 |
| Values, mask, bands, tolerance and size, read back by a decoder of the format | `LercWriterTests` | this repository |
| The service answers LERC, refuses `lercVersion=1` and a negative tolerance by name, lists `LERC`, and serves `rasterFunctionInfos` | `ImageryDisplayTests` | this repository |

The SDK probe and the npm decoder run where a browser and Node are, not in CI; the suite's own decoder keeps the format
from drifting between those runs.

## 5. Decision

`exportImage?format=lerc` answers the values as Lerc2; `supportedImageFormatTypes` names `LERC`; and
`ImageServer/rasterFunctionInfos` answers the functions the root lists.

## 6. Consequences

**Positive.** A JS SDK imagery layer that renders on the client — a pixel filter, a client-side renderer — draws from
this server, from data a quarter to two-thirds the TIFF's size.

**Negative.** **A renderer set on an `ImageryLayer` that does not render on the client is still not drawn.** The same
probe found the SDK sending a `RasterStretchRenderer` to the server as a `renderingRule` chain —
`Colormap(Stretch(Raster))` — which ADR-136 refuses by name. That is the next decision, not this one.

**State.** None.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | LERC is wanted next | Stated by the owner, 2026-10-02 |

## 8. Dependencies

**Depends on:** ADR-043, ADR-127, ADR-136.

**Depended on by:** —

## 9. Revisit triggers

- A client measured asking `lercVersion=1`, or reading a LERC blob version this server does not write.
