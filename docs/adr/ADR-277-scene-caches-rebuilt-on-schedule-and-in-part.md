# ADR-277 — A scene layer's cache is rebuilt on a schedule, and in part

| | |
|---|---|
| **Status** | `REQUIRES PROTOTYPE` |
| **Confidence** | `LOW` |
| **Decided** | 2026-11-07. **That both are built is the owner's decision** ([ADR-272](ADR-272-owner-decisions-on-3d-scope.md) §5.7: *"Kısmi güncelleme de"*, partial updates as well). The design below is this ADR's, and none of it is built yet. |
| **Supersedes** | — |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

A scene layer made here from a feature layer is a cache of it
([ADR-270](ADR-270-scene-viewer-to-arcgis-enterprise-parity.md) §5.4). What exists today:

- Studio's page says when the feature layer was edited after the cache was built, from the layer's `editingInfo`.
- *Rebuild* makes the whole package again, synchronously, as the person who pressed it.

ArcGIS Enterprise also rebuilds on a schedule, and updates only what changed (*partial*).

## 2. Alternatives considered

### Scheduled rebuild

- **A — a site sweep (proposed).** ADR-012's singleton sweeps run on one node only. Every few minutes the sweep looks
  for scene layers whose owner asked for *Rebuild when the layer changes* (a flag in the scene source resource) and whose
  feature layer changed after the build. It rebuilds each one **as its owner**: the owner's principal and tenant are
  read from the document row, never from a request. A rebuild that fails is recorded on the item and retried with a
  backoff. **What makes it hard:** every path that builds today is a request handler (`MakeSceneAsync` reads the caller
  from `HttpContext`). The build has to come out of the handler into a service that takes a principal, and the
  authorization checks have to be the same ones. That is the prototype this status asks for.
- **B — the reader's browser rebuilds when it sees the cache is behind.** Rejected: a rebuild would then depend on
  somebody opening the page.

### Partial update

- **A — rewrite only the nodes whose features changed (proposed).** Rows changed since the build are found by the
  edit stamps the hosted layer keeps (migration 127 records a time, not which rows), so the first need is a per-row
  change record. For a 3D object layer, a changed building moves only its leaf node and the coarse parents above it
  (D-386's coarse levels). The package is rewritten with those entries replaced and the rest copied as they are. For a
  point layer, the same per node.
- **B — always rebuild the whole package.** This is today's behaviour, and the fallback whenever A cannot tell what
  changed.

## 3. Counterarguments to the preferred option

- A partial update needs per-row change tracking that hosted layers do not have. That is the larger half of the work,
  and it touches every edit path.

## 4. Evidence

None yet. This ADR records a design and the owner's decision, not a result.

## 5. Decision

1. Scheduled rebuilds as a site sweep, as the owner, as Alternative A describes, once the build is a service that
   takes a principal.
2. Partial updates once hosted layers record which rows changed, with the full rebuild as the fallback.
3. Until then, Studio's *behind* notice and *Rebuild* stand.

## 6. Consequences

- **State.** A flag in the scene source resource. A rebuild's outcome kept on the item. Later, a per-row change
  record per hosted layer.

## 7. Assumptions this decision rests on

- A rebuild as the owner is what the owner expects. `INFERRED`, for the owner to confirm when it is built: ArcGIS runs
  a scheduled rebuild as the item's owner.

## 8. Dependencies

ADR-012 (site sweeps), ADR-270 §5.4, ADR-272 §5.7, D-386 (coarse levels).

## 9. Conditions

1. **The build is a service that takes a principal**, and the request path uses it unchanged. *(Open.)*
2. **The sweep**: a scene layer whose owner asked is rebuilt after its layer changes, and once only on a site of two
   nodes. *(Open.)*
3. **Hosted layers record which rows changed since a time.** *(Open.)*
4. **A partial update**: one building edited, one leaf and its parents rewritten, and the package read the same as a
   full rebuild. *(Open.)*
