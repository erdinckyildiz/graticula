# ADR-143 — An uploaded image is given back to its owner

| | |
|---|---|
| **Status** | `ACCEPTED` |
| **Confidence** | `HIGH` — the file given back is byte-identical to the one sent |
| **Decided** | 2026-10-03 by owner decision (*"devam et … image layers'ın kalan işlerini tamamla"*, the imagery list's "download" item) |
| **Supersedes** | — |
| **Superseded by** | — |
| **Amends** | [ADR-123](ADR-123-imagery-comes-into-studio.md) (condition 2: an uploaded file can be taken back) |

---

## 1. Context

An owner who uploaded a GeoTIFF had no way to take it back from Studio: a feature layer has *Export data*, an image
service had nothing. The raw export (ADR-127) writes an area at a size, not the file.

## 2. Alternatives considered

### Alternative A — The uploaded file, from the service's Overview (chosen)

`GET /admin/coverages/{name}/file` answers the file uploaded for an image service to whoever may manage it: the GeoTIFF
as it was sent, or, for a mosaic (ADR-140), one zip of its tiles under names of their own and a virtual raster
rewritten to place them by those names, which opens in GDAL, QGIS and ArcGIS as the mosaic. The overviews built
beside an image (ADR-139) are this server's and are not included. A file registered from the server's own disk is the
administrator's and is refused. Studio's Overview shows *Download* — *Download N tiles* for a mosaic — only when there
is an uploaded file to give.

### Alternative B — ArcGIS's `download` operation on the service

**Against:** it hands rasters to anyone the service is shared with, behind a capability ArcGIS leaves off by default;
an owner taking their own file back is the case asked for.

## 3. Counterarguments to the preferred option

- *A mosaic's zip is written as it streams*, so a failure part way leaves the browser a short file. The status line
  says when the request fails.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| A single upload comes back byte-identical; a mosaic as a VRT and its two tiles, each identical to what was sent, the VRT naming them | requests against the fixture; `ImageryDisplayTests` | local fixture, this repository |
| The Overview offers Download for an uploaded image and saves it | `ImportFormTests` | this repository |

## 5. Decision

An image service's owner can download the file uploaded for it, from Studio's Overview or `/admin/coverages/{name}/file`.

## 6. Consequences

**Positive.** An uploaded image is never stuck on the server.

**Negative.** None known.

**State.** None.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | The imagery list is worked through in order | Stated by the owner, 2026-10-03 |

## 8. Dependencies

**Depends on:** ADR-123, ADR-139, ADR-140.

**Depended on by:** —

## 9. Revisit triggers

- Someone the service is shared with, not its owner, needing the file — ArcGIS's `download`.
