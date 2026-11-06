# ADR-271 — The portal's search answers `bbox`

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` |
| **Decided** | 2026-11-07, overnight, under [ADR-270](ADR-270-scene-viewer-to-arcgis-enterprise-parity.md)'s direction from the owner (*"aynı arcgis enterprise'da olduğu gibi"*). The comparison with ArcGIS Online ([3d-parity-arcgis.md](../reviews/3d-parity-arcgis.md) §E) listed Scene Viewer's *only content within the map area* as owed, blocked on the search. **Which items a box finds, and that an item without an extent is not one of them, is this ADR's reading of ArcGIS's behaviour** and is marked `INFERRED` in §5. |
| **Supersedes** | The refusal of `bbox` in `PortalSearchParameters` (the ArcGIS veteran's review of 2026-11-02, E). `filter` stays refused. |
| **Superseded by** | — |
| **Confirmed** | 2026-11-07: the owner confirmed every `INFERRED` reading here (§5.2 and §7) — [ADR-272](ADR-272-owner-decisions-on-3d-scope.md) §5.8. The markers are left in place so the reading can be told from the confirmation. |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

The ArcGIS search reference takes `bbox`, a box in degrees, beside `q`; ArcGIS Online's and Enterprise's Scene Viewer and
Map Viewer use it for *only content within the map area*. Until this ADR, Graticula's search refused `bbox` by name.
That refusal was written on 2026-11-02 against a worse failure, a search that answered everything as if the box had been
applied, and its reason was cost: a search listing carries no extent, because an item's extent comes from its layers'
described shapes, and a cold cache reads those from the source.

## 2. Alternatives considered

### Alternative A — answer the box last, on what `q` and `categories` matched (chosen)

The box is the last narrowing. Each matched item's extent is read by the code its own item page uses: a service's layers
(cached per layer, as every request reads them), a web map's initial view, an image service's raster, a scene layer
package's layer document. **The cost is bounded by what the query matched, not by the server.**

**Argument against.** A search with an empty `q` and a box matches everything, and pays every extent once on a cold cache.

### Alternative B — keep an extent column on every item, written when it changes

A spatial index answers a box in one statement. **Rejected for now:** the extent of a registered layer changes when its
source changes, which nothing here observes, so a stored extent would go stale silently. That is a worse wrong answer
than a slow right one.

### Alternative C — keep refusing

**Rejected:** ArcGIS's viewers use the box, and the parity comparison found it as a gap.

## 3. Counterarguments to the preferred option

- **An empty-`q` box on a large server.** Every service's extent is read once per layer and cached after; at the scale
  target (100–1,000 services, CLAUDE.md §7) the first such search is the slow one. This is a revisit trigger, not a
  measured cost: **no benchmark has been run** (§9).

## 4. Evidence

- `ThePortalAnswersAnArcGisClientAsArcGisDoesTests`: a box is read in degrees, a malformed one is refused by name,
  `filter` is still refused, and an extent meets a box when they overlap or touch.
- `ThePortalAnswersAsArcGisDoesTests` (conformance): a box nowhere near any layer finds nothing, and a reversed box is
  refused.
- `SceneViewerToolsTests.The_add_list_follows_the_view_when_asked_to` (console, run on the fixture 2026-11-07): with the
  view over the South Pacific, `ci_buildings` is not offered and the list says why; with the camera over it, it is.

## 5. Decision

1. `bbox=minx,miny,maxx,maxy` in degrees is answered by `/sharing/rest/search` and a group's search. A box that is not
   four finite numbers with min ≤ max is refused by name. A box across the antimeridian is refused rather than read as
   the world; the scene viewer does not send one.
2. An item is found when its extent overlaps or touches the box. **An item with no extent is not found by a box**: a
   PDF, an app, a layer whose source cannot be read, and for now a web scene, whose view is a camera. `INFERRED` from
   ArcGIS, where a spatial search matches the item's extent.
3. The search listing still carries `extent: []`; only the item's own page fills it. Filling it on a listing is a
   separate change with its own cost.
4. The scene viewer's Add tab has *Only within the current view*: it sends the view's extent in degrees and searches again
   when the camera settles.

## 6. Consequences

- Scene Viewer's *only content within the map area* is built (3d-parity-arcgis.md §E).
- A client that sent `bbox` and was refused is now answered.
- The 2D map viewer can offer the same with no server change. That is not done here.
- **State.** None. Nothing is stored; each extent is read per request through the per-layer cache that already exists, which is node-local.

## 7. Assumptions this decision rests on

- The box is in degrees. `INFERRED`: ArcGIS's search reference gives the box with no reference of its own.

## 8. Dependencies

- `PortalEndpoints.ExtentAsync`, `MapExtent` and `CoverageExtentAsync`; `SceneServerEndpoints.PackageExtentAsync`.

## 9. Conditions

1. **The owner confirms §5.2 and §7**: that an item with no extent is not found by a box, and that the box is in
   degrees. *(Discharged 2026-11-07: confirmed by the owner, ADR-272 §5.8.)*
2. **The cost of an empty-`q` box is measured** on a catalogue at the scale target, cold and warm, before any viewer
   sends one without a `q`. The scene viewer always sends a type clause. *(Open.)*

## 10. Revisit triggers

- An empty-`q` box takes longer than a second on a warm cache: then Alternative B, with a way to invalidate.
- A user asks for a web scene to be found by a box.

## 11. Dissent

None recorded.
