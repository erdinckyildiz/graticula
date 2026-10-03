# ADR-148 — An image may be downloaded by everyone it is shared with, and every image is given its pyramid

| | |
|---|---|
| **Status** | `ACCEPTED` |
| **Confidence** | `HIGH` — both are checked end to end, with a second user |
| **Decided** | 2026-10-03 by owner decision, asked directly: who may download (*"Download yeteneği"*), and pyramids for older and registered images (*"İkisi de"*) |
| **Supersedes** | — |
| **Superseded by** | — |
| **Amends** | [ADR-143](ADR-143-an-uploaded-image-is-given-back.md) (who may download), [ADR-139](ADR-139-an-uploaded-image-is-given-its-overviews.md) (which images are given overviews) |

---

## 1. Context

ADR-143 gave an uploaded image's file to whoever manages it, and asked whether others should have it, as ArcGIS's
Download capability allows. ADR-139 built overviews on upload only, leaving images uploaded before it and images
registered in place without them, and asked whether to write beside a registered file. The owner chose both.

## 2. Alternatives considered

### Alternative A — ArcGIS's Download capability; overviews for every image (chosen)

- **Download.** An image service has a Download capability, off by default (migration 76). Off, whoever manages it may
  take its file; on, everyone it is shared with may — from Studio's Overview, from `/admin/coverages/{name}/file`, and
  from the service's own `download` and `file` operations, as ArcGIS clients ask. The service's `capabilities` names
  `Download` when it is on. The same refusal for not seeing it and not being let. A file registered from the server's
  disk is the administrator's and is not offered. Settings › General turns it on and off.
- **Pyramids for every image.** A background pass at startup, and after each registration, gives every image without
  overviews its `.ovr` — beside the uploaded file, and beside a registered file, as ArcGIS's Build Pyramids writes it;
  each image of a mosaic likewise. A folder this server may not write is said in the log and passed over; the image
  keeps working without overviews. The catalogue is told the levels.

### Alternative B — Overviews for registered files kept in this server's own folder

**Against:** a second place for a file's overviews, which GDAL, QGIS and ArcGIS would not find beside the file.

## 3. Counterarguments to the preferred option

- *Writing beside a registered file changes the owner's folder*, as ArcGIS does. A read-only folder is respected.
- *Deleting a registered image service leaves the `.ovr`*, as ArcGIS leaves its pyramids: they belong to the file.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| Off, a reader the service is shared with sees it and is refused its file and `download`; on, `capabilities` names Download and the file, `download` and `file` give it byte-identical; off again, refused | `ImageryGroupSharingTests` | this repository |
| An image registered without overviews is given two levels beside its file, in the background | `ImageryDisplayTests` | this repository |

## 5. Decision

As §2.

## 6. Consequences

**Positive.** Imagery is shared as ArcGIS shares it, and no image reads every pixel to be seen whole.

**Negative.** None known.

**State.** `coverage.download`.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | Both are wanted | Stated by the owner, 2026-10-03 |

## 8. Dependencies

**Depends on:** ADR-139, ADR-143.

**Depended on by:** —

## 9. Revisit triggers

- A deployment whose registered imagery lives on storage this server must not write to at all.
