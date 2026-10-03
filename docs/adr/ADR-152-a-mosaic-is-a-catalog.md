# ADR-152 — A mosaic is a catalog: queried, ordered by a rule, and edited as ArcGIS edits one

| | |
|---|---|
| **Status** | `ACCEPTED` |
| **Confidence** | `MEDIUM` — each operation is checked end to end; ArcGIS's ordering methods that need a camera (seamline, true nadir) are not reproduced |
| **Decided** | 2026-10-03 by owner decision (*"Bunları da ekleyelim"*, *"Tüm maddeleri yap"* — the imagery list's items 21 and 26) |
| **Supersedes** | — |
| **Superseded by** | — |
| **Amends** | [ADR-140](ADR-140-several-images-are-one-mosaic.md) (a mosaic was one picture), [ADR-147](ADR-147-a-mosaic-grows-and-resamples.md) (images could be added, not removed) |


> **Amended 2026-10-03 by [ADR-158](ADR-158-a-mosaic-combines-and-is-reordered.md)** — `MT_MIN`, `MT_MAX`, `MT_MEAN`, `MT_BLEND` and `MT_SUM` are served, and Studio reorders a mosaic — **and by [ADR-159](ADR-159-a-multidimensional-file-is-a-service-of-slices.md)** — `multidimensionalDefinition` chooses a multidimensional service's slices.

---

## 1. Context

ADR-140 made several images one image service drawn as one picture, and ADR-147 let it grow. ArcGIS's mosaic dataset is
more than a picture: its images are rows a client lists (`query`), chooses among and orders (`mosaicRule`), and edits
(`add`, `update`, `delete`). This server refused `mosaicRule` and had no `query`; an image could not be taken out.

## 2. Alternatives considered

### Alternative A — The catalog over the virtual raster, its rows kept beside the coverage (chosen)

- **The catalog.** Every image service is a catalog — a mosaic of many images, a service over one file of one. Each
  image has an object id that stays its own when others are added and removed and is never given again (migration 77
  keeps the next one), a name (the file it was sent as, or what its owner renamed it), its footprint, its cell size and
  its position in the drawing order. A coverage published before this has its files numbered from one; an upload named
  by this server is called *Image n*.
- **`query`** answers the rows as ArcGIS does — `OBJECTID`, `Name`, `AcquisitionDate`, `ZOrder`, `LowPS`, `CenterX`,
  `CenterY`, `Shape_Area` and a footprint polygon — filtered by `objectIds`, `where`, an extent and `time`, as features,
  ids or a count. `where` is evaluated by the platform store over the rows given to it as JSON, through the parser every
  other `where` here goes through, so the whole grammar works and nothing written is pasted into a statement.
- **`mosaicRule`** on `exportImage`, `identify`, `getSamples` and `computeStatisticsHistograms`: `esriMosaicNone`,
  `LockRaster` (`lockRasterIds`), `Northwest`, `Center`, `Nadir` (as Center: this server's images all look straight
  down), `Viewpoint` and `Attribute` (`sortField`, `sortValue`, `ascending`), with `fids` and `where`; `MT_FIRST` puts
  the first in the order on top and `MT_LAST` the last. `MT_MIN`, `MT_MAX`, `MT_MEAN`, `MT_BLEND`, `MT_SUM`, the
  seamline method and a `multidimensionalDefinition` are refused by name. The service says `mosaicOperator: Last` —
  without a rule the last image added is on top, as ADR-147 drew it.
- **Editing.** ArcGIS's `uploads/upload` then `add` (`rasterType` *Raster Dataset*), `update` (`Name`,
  `AcquisitionDate`) and `delete` (`rasterIds`) work on an uploaded service, by whoever may manage it; `add` takes the
  path Studio's Add images takes. The last image is not deleted — deleting the service is how. The service names `Edit`
  among its capabilities when its images can be changed, `Catalog` always.
- **Studio.** Settings › Images lists the rows in drawing order; each is renamed, dated and, in an upload, removed.
- **A float mosaic declaring no no-data** reads NaN where no image lies, not zero — a gap between tiles, or a rule that
  leaves none, is no value.

### Alternative B — A mosaic dataset of our own, a table per mosaic

**Against:** a second store of what the virtual raster already says; the rows are a coverage's few, kept as JSON beside it.

## 3. Counterarguments to the preferred option

- *Reordering images in Studio is not offered*; a rule orders them per request, and an image removed and added again
  goes on top.
- *Removing an image writes the virtual raster again*, as adding one does; a mosaic of hundreds pays that per edit.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| Two images on the same ground: `query` lists both with names and footprints and a `where` picks one; the last is on top, a lock or `MT_FIRST` puts the first there, a `where` in the rule keeps one, `fids` naming none draws nothing; `MT_BLEND` is refused naming MT_FIRST; `update` dates one, the service gains `timeInfo`, `time` chooses it; `delete` removes one and the other keeps its id; the last is not deleted; `uploads/upload` and `add` add one as id 3, not 2 | `MosaicCatalogConformanceTests` | this repository |

## 5. Decision

As §2.

## 6. Consequences

**Positive.** Imagery from many dates and sources is one service a client can sort, filter and edit as in ArcGIS.

**Negative.** No reorder in Studio; compositing operators are refused.

**State.** `coverage.images` (migration 77).

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | A mosaic's catalog is small enough to evaluate a `where` over per request | Unmeasured past 500 images, the mosaic's limit |

## 8. Dependencies

**Depends on:** ADR-140, ADR-147.

**Depended on by:** ADR-153.

## 9. Revisit triggers

- An owner asking to reorder images.
- A client asking for MT_MEAN or MT_BLEND.
