# ADR-277 — A scene layer's cache is rebuilt on a schedule, and in part

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `LOW` |
| **Decided** | 2026-11-07. **That both are built is the owner's decision** ([ADR-272](ADR-272-owner-decisions-on-3d-scope.md) §5.7: *"Kısmi güncelleme de"*, partial updates as well). The design below is this ADR's. **Scheduled rebuilds were built 2026-11-07** (§4), and **partial updates the same night** (§4b), by a different route than §2 proposed: a fingerprint per feature kept in the package, not a per-row change record. |
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

`SceneLayerSourceTests.A_scene_layer_is_rebuilt_on_its_own_after_its_layer_changes` (console, on the fixture with
`Graticula:SceneRebuildSeconds=5`, 2026-11-07):

- a point scene layer is made from the editable layer;
- its owner turns *Rebuild on its own when the layer changes* on in Studio;
- one point is added through `applyEdits`;
- within the minute the sweep made it again, and its record says so;
- Studio's page says when.

The sweep:

- is `SceneRebuilds`, a site singleton (`scene-rebuilds`) that runs every five minutes, in every tenant;
- uses the request path's own maker, `MakeSceneFromAsync`, now free of the request (condition 1);
- reads a layer on the owner's behalf only when it is theirs or shared with everyone, and says so otherwise.

### 4b. Partial update, as built

A per-row change record (§2, partial A) would have touched every edit path. The package can instead carry what it was
made from. Since 2026-11-07 a 3D object package written here holds `graticula/plan.json.gz`: each leaf node with the
object ids it holds and a fingerprint of each feature (its footprint, base, height and attributes). On a rebuild, the
features read now are fingerprinted and compared with the plan:

- nothing differs: the package is not written again, and the rebuild says *nothing had changed*;
- some differ: only the leaves holding a changed, added or removed feature are rewritten, with every ancestor up to the
  root (their coarse levels and extents). Every other node's entries are copied byte for byte. An added feature goes to
  the leaf whose box centre is nearest. The statistics, the layer document and the plan are written again;
- the full build is the fallback: no plan (a package from elsewhere or before this), more than a quarter of the
  features changed, a leaf emptied, or a leaf grown past twice the node limit.

`SceneLayerPackageUpdateTests` (Core, 2026-11-07): an unchanged layer gives an identical node set; one building raised
rewrites one leaf and its ancestors, and the package reads the same as a full build of the same features; an added and
a removed building; the fallbacks. Measured on a synthetic city, one building changed (Release, arm64, not committed):

| features | full build | partial | nodes rewritten |
|---:|---:|---:|---:|
| 20,000 | 1.6–2.9 s | 0.9 s | 4 |
| 100,000 | 6.9 s | 3.6 s | 5 |

Half the time, not a tenth: reading and fingerprinting every feature, and recompressing the zip, are both still whole.
`SceneLayerSourceTests` checks that Studio's *Rebuild* of an unchanged layer says so. A point scene layer is still
always made whole; it is one small node set per level, and nobody has measured a reason.

## 5. Decision

1. Scheduled rebuilds as a site sweep, as the owner, as Alternative A describes, once the build is a service that
   takes a principal.
2. Partial updates for 3D object layers by the package's own fingerprints (§4b), with the full rebuild as the fallback. *(Amended 2026-11-07 from "once hosted layers record which rows changed".)*
3. Until then, Studio's *behind* notice and *Rebuild* stand.

## 6. Consequences

- **State.** A flag in the scene source resource. A rebuild's outcome kept on the item. A plan entry inside the
  package, which is the package's own and goes where it goes. No per-row change record.

## 7. Assumptions this decision rests on

- A rebuild as the owner is what the owner expects. `INFERRED`, for the owner to confirm when it is built: ArcGIS runs
  a scheduled rebuild as the item's owner.

## 8. Dependencies

ADR-012 (site sweeps), ADR-270 §5.4, ADR-272 §5.7, D-386 (coarse levels).

## 9. Conditions

1. **The build is a service that takes a principal**, and the request path uses it unchanged. *(Discharged 2026-11-07: `MakeSceneFromAsync` takes the service already found and allowed; the request path finds and allows it as before, and the sweep allows only the owner's own or a public layer, §4.)*
2. **The sweep**: a scene layer whose owner asked is rebuilt after its layer changes, and once only on a site of two
   nodes. *(Partly discharged 2026-11-07: the rebuild is measured, §4; once-per-site rests on the same singleton lease
   every other sweep uses, and is not separately measured on two nodes.)*
3. **Hosted layers record which rows changed since a time.** *(Discharged 2026-11-07 by being made unnecessary: the
   package's fingerprints say what changed, §4b. A per-row record would still save the reading of every feature, which
   is half of what a partial update now costs; that is a performance question with no measured need yet.)*
4. **A partial update**: one building edited, one leaf and its parents rewritten, and the package read the same as a
   full rebuild. *(Discharged 2026-11-07: `SceneLayerPackageUpdateTests`, §4b.)*
