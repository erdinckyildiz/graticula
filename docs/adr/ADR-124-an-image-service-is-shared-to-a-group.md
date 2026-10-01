# ADR-124 — An image service is shared to a group, and identify refuses what it does not apply

| | |
|---|---|
| **Status** | `ACCEPTED` |
| **Confidence** | `HIGH` — each defect is reproduced by a test that failed before the change |
| **Decided** | 2026-10-01, in [ADR-123](ADR-123-imagery-comes-into-studio.md)'s order — its scope is [Q-158](../open-questions.md), confirmed by the owner 2026-10-01 |
| **Supersedes** | — |
| **Superseded by** | — |
| **Amends** | [ADR-036](ADR-036-groups.md) (what a group holds), [ADR-123](ADR-123-imagery-comes-into-studio.md) §5.1 (what identify reads) |

---

## 1. Context

The ArcGIS reviewer's second imagery pass, 2026-10-01, after v1.0.263. Two defects, both measured:

- **A group-shared image service answered nobody but its owner.** The group rows are written against the
  `service` row, which an image service has — but they were never read for a coverage: `ImageServerEndpoints`, the
  portal listing and the directory evaluated a coverage's sharing with no groups. Worse, the scope could not be set
  at all: the ownership check behind `PUT /admin/services/{name}/sharing` looked the service up among feature
  services only, so an image service's scope answered *no service* and stayed private. In Portal, group sharing is
  how access is granted; without it an imagery layer is private or the whole organisation's.
- **`identify` accepted what it does not apply.** A `renderingRule` asking for a slope answered the elevation, a
  `mosaicRule` was read and ignored, a point off the image answered `"value": null` with no location, and
  `exportImage?f=kmz` answered a PNG — the [D-125](../architecture-debt.md) shape ADR-123 removed from `exportImage`.

## 2. Alternatives considered

### Alternative A — Read the service's group rows for a coverage, and refuse in identify what export refuses (chosen)

**Argument for.** The rows exist and the evaluator already takes them; the coverage only has to carry them. The
refusal already exists as `ImageServerExportParameters.TryUnoffered`.

**Argument against.** A coverage now carries a second copy of its service's sharing facts.

### Alternative B — A coverage-specific sharing table

**Against:** two places to share one service, which is the propagation shape D-130 records.

## 3. Counterarguments to the preferred option

- `NoData` is a string where a number is otherwise written; it is ArcGIS's spelling, and a client that reads a
  number has always had to read it.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| A member reads a group-shared image service and Portal search lists it; a stranger gets neither; unsharing removes it | `ImageryGroupSharingTests` | this repository |
| identify refuses a raster function and a mosaic rule; off the image it says `NoData` with the point; `f=kmz` is refused | `ImageServerConformanceTests.Identify_refuses_what_it_does_not_apply_and_says_NoData_off_the_image` | this repository |

## 5. Decision

5.1 `PublishedCoverage.SharedWith` is its service's group shares, read with it, and every coverage access check
passes it to `LayerAccess.Evaluate`. The ownership check for sharing finds an image service when no feature service
of the name exists.

5.2 `identify` refuses what `exportImage` refuses; a point off the image, or on a pixel whose every band is its
no-data value, answers `"value": "NoData"` with its location. `exportImage` refuses an `f` other than `image`,
`json` and `pjson`.

## 6. Consequences

**Positive.** An imagery layer is shared as any item is. No identify answers a question it did not answer.

**Negative.** None known.

**State.** None: the group rows already existed.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | Imagery is in scope | Confirmed by the owner 2026-10-01 — [Q-158](../open-questions.md) |

## 8. Dependencies

**Depends on:** ADR-036 (groups), ADR-043 (ImageServer), ADR-123.

**Depended on by:** —

## 9. Revisit triggers

- The owner answers Q-158 otherwise.
