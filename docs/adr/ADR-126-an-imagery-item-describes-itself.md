# ADR-126 — An imagery item describes itself, and sits in its owner's folder

| | |
|---|---|
| **Status** | `ACCEPTED` |
| **Confidence** | `HIGH` — measured on the portal face Pro and the Python API read |
| **Decided** | 2026-10-01, in [ADR-123](ADR-123-imagery-comes-into-studio.md)'s order — its scope is [Q-158](../open-questions.md), still `INFERRED` |
| **Supersedes** | — |
| **Superseded by** | — |
| **Amends** | [ADR-111](ADR-111-an-item-carries-tags.md) (whose tags an item carries), [ADR-114](ADR-114-content-folders-and-move.md) (which items a folder holds) |

---

## 1. Context

The ArcGIS reviewer's second imagery pass, 2026-10-01: an image service's portal item was written with a null
description and snippet, its folder as its only tag, no thumbnail and no `Hosted Service` keyword on an upload — so
in Pro's portal pane and the Python API's search an imagery layer had nothing to say about itself. Its owner could not
change that: the description and tags writes refused an image service as one they did not own, the ownership check
reading feature services only (fixed in [ADR-124](ADR-124-an-image-service-is-shared-to-a-group.md)). And My
content's folders never held one, the listing placing every image service at the root.

## 2. Alternatives considered

### Alternative A — Read what the service row already holds (chosen)

Description, tags and content folder live on `service`, which an image service has; the coverage reads them with
its other service facts. The picture is the image drawn whole under its own style, as Display draws it.

### Alternative B — A kept thumbnail, drawn once

**For:** cheaper on a busy portal. **Against:** a style change leaves it stale, and the draw is already bounded by the
admission budget every export meets. Revisit if the item listing becomes a measured cost.

## 3. Counterarguments to the preferred option

- The thumbnail is drawn per request. Recorded above.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| Its owner's description and tags reach the portal item; an upload carries `Hosted Service`; the item's picture is a PNG | `ImageryUploadTests.An_imagery_item_has_its_owners_description_tags_and_a_picture` | this repository |
| Studio offers the description on an image service's page | `ImportFormTests.An_image_services_display_is_set_on_its_page` | this repository |

## 5. Decision

An image service's portal item carries its service's description (as `description` and `snippet`), its tags with its
service folder, a thumbnail drawn from the image, and `Hosted Service` when this server holds the file. My content
lists it in the content folder it was moved to.

## 6. Consequences

**Positive.** An imagery item reads like any other in Pro, the Python API and Studio.

**Negative.** A thumbnail costs a draw.

**State.** None: the columns already existed.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | Imagery is in scope | `INFERRED` — [Q-158](../open-questions.md) |

## 8. Dependencies

**Depends on:** ADR-111, ADR-114, ADR-123, ADR-124.

**Depended on by:** —

## 9. Revisit triggers

- Item listings measured slow because of imagery thumbnails.
