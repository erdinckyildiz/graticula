# ADR-127 — An image service exports its values as a GeoTIFF

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` — the file is read back by this server's reader and by GDAL; what ArcGIS Pro does with it is not yet measured |
| **Decided** | 2026-10-01. Inferred from the owner's *"devam et"*, said to a report that asked whether to build the raw export — read as yes, listed under [Q-158](../open-questions.md) with the scope it extends — **confirmed by the owner 2026-10-01** |
| **Supersedes** | — |
| **Superseded by** | — |
| **Amends** | [ADR-043](ADR-043-imageserver-and-the-raster-face.md) (the formats `exportImage` writes) |


> **Amended 2026-10-02 by [ADR-137](ADR-137-an-image-service-answers-lerc.md).** `format=lerc` answers the same values as Lerc2; the revisit trigger below was measured and is recorded there.

> **Amended 2026-10-03 by [ADR-142](ADR-142-an-image-is-read-between-its-cells-as-asked.md).** Values stay nearest unless `interpolation` asks for bilinear or cubic.

---

## 1. Context

The ArcGIS reviewer's second imagery pass, 2026-10-01, ranked it second: `exportImage` refused `format=tiff` and
`format=lerc`, so the only value a client could get out of an image service was one pixel through `identify`. ArcGIS
Pro's Export Raster and analysis read an image service's values as TIFF; the JS SDK's `ImageryLayer` renders on the
client from LERC or TIFF. An elevation model that can only be looked at is a picture of one.

## 2. Alternatives considered

### Alternative A — TIFF now, LERC later (chosen)

A GeoTIFF in the values' own type over the asked extent and size, through the same plan and nearest-neighbour warp the
picture is drawn with, so the two agree. Written by a small writer of our own (`GeoTiffWriter`): one uncompressed
strip, the three GeoTIFF tags, GDAL's no-data tag.

### Alternative B — LERC first

**For:** what the JS SDK prefers. **Against:** a format of its own to implement and test against Esri's decoder; TIFF
is what Pro uses and is read by everything, so it is the larger door for the smaller cost.

### Alternative C — Write the TIFF through the reader's library

**Against:** a Tier 2 library behind the reader port would have to grow a writer port for one fixed arrangement this
file writes in under two hundred lines; [build-vs-adopt](../build-vs-adopt-policy.md) permits the library, and does
not require it.

## 3. Counterarguments to the preferred option

- *Uncompressed* makes a 4096² float export 64 MB. It is bounded by the same size ceiling as a picture, and a client
  asking for values asks for exactness.
- *Nearest neighbour only*: `interpolation` is still read and not applied, as ADR-123 §3 recorded.
- *Outside the image is its no-data value, or zero when it declares none* — zero is then ambiguous. Stated.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| Every type and band count an image service holds is written and read back in place, with its values and no-data | `GeoTiffWriterTests` | this repository |
| A float model exported in its own reference and warped into Web Mercator carries the model's metres | `ImageryDisplayTests` | this repository |
| GDAL reads an export as GTiff, places it, and names EPSG:4326 and EPSG:3857 | `gdalinfo` on two exports of `ci_imagery`, 2026-10-01 | local run |

## 5. Decision

`exportImage?format=tiff` answers a GeoTIFF of the coverage's values in their own type; `supportedImageFormatTypes`
names `TIFF`. `f=json` answers where it is, as for a picture.

## 6. Consequences

**Positive.** An image service's values leave it — for Pro's Export Raster, a script, a client renderer.

**Negative.** LERC is still refused.

**State.** None.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | The owner's *"devam et"* takes the raw export in, with imagery's scope | Confirmed by the owner 2026-10-01 — [Q-158](../open-questions.md) |

## 8. Dependencies

**Depends on:** ADR-043, ADR-123.

**Depended on by:** —

## 9. Revisit triggers

- The owner answers Q-158 otherwise.
- The JS SDK's `ImageryLayer` is measured refusing to render without LERC.

## 10. Conditions

1. **ArcGIS Pro's Export Raster is run against an image service on this server and the file it writes holds the
   values.** Not run: this machine has no Pro.
