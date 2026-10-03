# ADR-169 — WFS-T through the one write path

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` — each action is checked end to end in both versions; OGC's Transactional test class has not been run against it, and no WFS-T client has been watched editing |
| **Decided** | 2026-10-03 by owner decision (*"tamam başla"*, in answer to a list of four OGC items with WFS-T recommended first) |
| **Supersedes** | — |
| **Superseded by** | — |
| **Amends** | [ADR-039](ADR-039-wfs-is-the-first-surface-after-v1.md) (Alternative C, WFS-T, rejected; `ImplementsTransactionalWFS` FALSE) |

---

## 1. Context

ArcGIS turns WFS-T on for a service with feature access, and QGIS and FME edit through it. This server's WFS was
read-only (ADR-039 Alternative C) while two other faces wrote — ArcGIS `applyEdits` and OGC API Features Part 4 —
through one writer.

## 2. Alternatives considered

### Alternative A — `Transaction` reduced to the writer's batches (chosen)

- **An XML POST of `wfs:Transaction`** in WFS 2.0.0 or 1.1.0 (ADR-168), at `/wfs` and a service's own `WFSServer`
  address (ADR-162). Actions: `Insert` (features as their type's elements, the geometry in GML 3.2 or 3.1.1 in any
  reference, moved into the layer's), `Update` (`wfs:Property` by `ValueReference` or 1.1's `Name`, with a `Value`),
  `Replace` (2.0: a whole feature for those a filter selects), `Delete`. `Native` and anything else are refused by
  name.
- **A filter names what Update, Replace and Delete change**, read by the filter reader and compiled by GetFeature's own
  query (OGC Filter 1.1 included), up to the server's record ceiling; a filter selecting more is refused rather than
  half applied.
- **Each layer's actions are one `EditBatch`** on its `IFeatureWriter`, with rollback on failure, who edited
  (ADR-064), own-only editing (ADR-115), and the tiles emptied after — the path `applyEdits` and OGC API Features take.
  The identity column is refused as a property; values are converted by the same rule OGC API Features uses.
- **Rights and ceilings**: the caller must be able to edit each layer (ADR-075), and the service's capability ceiling
  must allow `Create`, `Update` or `Delete` (D-179); a refusal is an OWS exception in the request's version.
- **The answer** is `wfs:TransactionResponse` with the totals and, for inserts, each new feature's `ResourceId` (2.0)
  or `FeatureId` (1.1), with its `handle`.
- **Capabilities** list `Transaction`; 2.0's `ImplementsTransactionalWFS` is TRUE; 1.1's feature types list
  `Insert`, `Update` and `Delete`. `LockFeature` stays unimplemented and FALSE.
- An audit row a layer, `wfs.transaction`.

### Alternative B — Keep WFS read-only

**Against:** QGIS's WFS editing is how many ArcGIS shops' non-ArcGIS users edit, and the writer already exists.

## 3. Counterarguments to the preferred option

- *Atomic per layer, not across layers.* A transaction over two layers whose second fails has changed the first; the
  refusal says so.
- *No locking*: two editors are not kept apart, as `applyEdits` does not keep them apart either.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| Insert in 2.0 from EPSG:4326 lands where it was put; Update in 1.1 by an OGC Filter; Replace in 2.0 moves it; Delete removes it; an anonymous caller is refused with an exception report | `WfsTransactionConformanceTests.A_feature_is_inserted_updated_replaced_and_deleted_through_transaction` | this repository |
| The capabilities declare transactions TRUE and locking FALSE | `WriterTests.The_capabilities_declare_transactions_true_and_locking_false` | this repository |

## 5. Decision

As §2, Alternative A.

## Conditions

1. OGC's WFS 2.0 Transactional test class is run against the server, or the claim is narrowed to what it passes.
2. QGIS edits a layer through WFS-T and the edit is seen through the ArcGIS face.

## 6. Consequences

**Positive.** WFS clients edit, under the same rights and ceilings as every other face.

**Negative.** A fourth face writes; a defect in the writer now has four ways in.

**State.** None new.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | WFS-T clients send a filter, not a feature list, to name what they change | True of QGIS and FME |

## 8. Dependencies

**Depends on:** ADR-039, ADR-064, ADR-075, ADR-162, ADR-168.

**Depended on by:** —

## 9. Revisit triggers

- A client that needs `LockFeature`.
- A transaction over several layers that must be all or nothing.
