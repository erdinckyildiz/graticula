# ADR-115 — Editor tracking stops as well as starts, and editors may be kept to their own features

| | |
|---|---|
| **Status** | `ACCEPTED` |
| **Confidence** | `HIGH` — the writer's own-only predicate existed and was measured dormant; this turns it on per layer |
| **Decided** | 2026-10-01, by owner decision (*"Sırayla git"*, the ArcGIS reviewer's second-pass item 4) |
| **Supersedes** | — |
| **Superseded by** | — |

---

## 1. Context

The ArcGIS reviewer's second pass: *"Editor tracking only turns on … There is no off … There is also no 'editors
see/edit only their own features': `ownershipBasedAccessControlForFeatures = null`."* Portal's layer settings carry
both: *Disable editor tracking*, which stops recording and keeps the fields, and *Editors can only update and delete
the features they added* — ownership-based access control, which Field Maps reads to offer *Edit* only on a feature
the user added. A field crew sharing one layer is the setting it exists for.

ADR-064 had once kept `features:edit` callers to their own rows; ADR-075 replaced that with *owner, administrator or a
shared-update group, each reaching every feature*, and left the writer's own-only predicate (`EditBatch.OwnOnly`,
`PostGisFeatureWriter.OwnerClause`) in place with nothing turning it on.

## 2. Alternatives considered

### Alternative A — A per-layer switch that turns the dormant predicate on (chosen)

**Argument for.** The writer already adds `and <creator> = @owner` to every update and delete and explains a refused
row (`WhyNotAsync`); one boolean per layer decides when. Per layer, as Portal's is.

### Alternative B — Also *editors see only their own features*

**Argument against, for now.** It is a filter on every read — query, count, extent, statistics — and on tiles, which
are cached per layer and shared by every caller. A per-caller tile is a different cache. Recorded as a revisit trigger.

## 3. Counterarguments to the preferred option

- *A feature added before tracking has no creator*, so under own-only nobody but the owner and administrators can
  change it. That is Portal's behaviour too.
- *The owner and administrators are not bound*, which `ownershipBasedAccessControlForFeatures` cannot say; Portal's
  document does not say it either.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| Portal's *Editors can only update and delete the features they added* and *Disable editor tracking* | ArcGIS Enterprise *Manage hosted feature layers* | publicly documented |
| An editor kept to their own changes their own and not another's; the owner is not kept; stopping tracking keeps the columns | `EditorTrackingConformanceTests` | this repository |

## 5. Decision

5.1 Migration 73: `layer.edit_own_only`, false by default. `PUT /admin/layers/{layer}/ownership-access`
`{editOwnOnly}` (the owner or an administrator), refused unless the layer records who added each feature. A view's
layer may have it.

5.2 While it is on, an update or delete by anybody but the layer's owner or an administrator reaches only rows whose
creator is the caller, on the ArcGIS and OGC faces alike. The layer document says
`ownershipBasedAccessControlForFeatures` with others and anonymous allowed to query and not to update or delete.

5.3 `DELETE /admin/hosted/{layer}/editor-tracking` stops recording: the columns' roles go, the columns and their
values stay. Refused while own-only is on.

5.4 Studio's Settings › Feature layer offers *Stop recording* beside *Start recording*, and the own-only box.

## 6. Consequences

**Positive.** Field crews share a layer without editing each other's work; tracking is no longer a one-way door.

**Negative.** No *see only their own*.

**State.** One column.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | Field Maps honours `ownershipBasedAccessControlForFeatures` from the layer document | `INFERRED` — publicly documented behaviour, not run against Field Maps here |

## 8. Dependencies

**Depends on:** ADR-064 (editor tracking), ADR-075 (who edits a layer).

**Depended on by:** —

## 9. Revisit triggers

- Somebody needs editors to see only their own features, which needs per-caller reads and tiles.

## 10. Conditions

1. **An editor kept to their own changes their own features and not others'; the owner is not kept** —
   **DISCHARGED 2026-10-01**, `EditorTrackingConformanceTests`.
2. **Tracking stops and keeps its columns, and is refused while own-only relies on it** — **DISCHARGED 2026-10-01**,
   `EditorTrackingConformanceTests`.
3. **Studio offers both** — **DISCHARGED 2026-10-01**, `OwnershipAccessScreenTests`.
