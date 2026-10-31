# ADR-226 — grapy holds network datasets and solves routes on them

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` for the model · `LOW` for agreement with Pro's numbers, which this cannot reproduce exactly |
| **Decided** | 2026-10-30, by owner decision on scope (*"Ağ veri seti (ağ analizi)"*), [ADR-224](ADR-224-grapy-aims-at-arcpys-whole-buildable-surface.md) §5.7. The design below is INFERRED and listed for confirmation in §5.6. |
| **Supersedes** | — |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

`arcpy.na`, `arcpy.nax` and `arcpy.nd` hold about 230 names, and every one is refused: there is no network dataset.
These names cover building a network from lines (CreateNetworkDataset, BuildNetwork), its travel modes and
restrictions, and the solvers: route, closest facility, service area, OD cost matrix, location-allocation and
vehicle routing. A user tool that computes the nearest hospital along roads cannot run.

Esri's own solvers are proprietary: hierarchy, turn handling, and the vehicle routing heuristics are not published as
algorithms. What *is* published is the network's data model (edges, junctions, connectivity policies, cost
attributes, restrictions, travel modes) and each solver's problem statement and output fields.

## 2. Alternatives considered

### Alternative A — grapy's own network model over the GeoPackage, solved with shortest-path algorithms (chosen)

Edges and junctions are built from line feature classes by the documented connectivity policies (end point or any
vertex). Costs are evaluated per edge. Solvers use Dijkstra (`scipy.sparse.csgraph`, already in the image) and,
where the problem is NP-hard, a published heuristic. **For:** no new dependency, and the problems are
textbook-defined. **Against:** every solver is an *equivalent*, not Esri's computation.

### Alternative B — embed an external routing engine (OSRM, Valhalla, pgRouting)

**For:** they are mature and fast. **Against:** they are services or PostgreSQL extensions, not a library a fenced
user tool can load; they bring their own data models; and pgRouting would put routing in the datastore rather than
in the user's tool. Rejected for grapy. pgRouting may later be a server feature, which is a different decision.

### Alternative C — refuse group 2 networks

Rejected by the owner's scope answer.

## 3. Counterarguments to the preferred option

Route answers will differ from Pro's whenever Pro's hierarchy or tie-breaking would choose another of two equal-cost
paths. A user comparing numbers will find differences. §5.4 makes every solver an equivalent under ADR-224 §5.6:
marked, counted apart, and saying so in its run. On a network with a unique shortest path, Route's total cost
*is* defined, and the tests use such networks.

## 4. Evidence

Dijkstra's algorithm gives the documented "shortest path by the impedance" exactly when costs are non-negative,
which the documentation requires. No performance claim is made. A user tool runs under ADR-223's limits, and a
network too large for them fails by those limits, by name.

## 5. Decision

1. A network dataset is grapy tables in the GeoPackage: source classes, connectivity groups and policy, cost and
   restriction attributes (field or constant evaluators; ~~script evaluators refused~~ — **custom evaluators, Python
   classes, built on 2026-10-31 by [ADR-246](ADR-246-grapy-runs-custom-evaluators-keeps-traversals-and-builds-solver-properties.md)**,
   which also built the traversed elements and the solver property objects this ADR's condition 3 had refused), and
   travel modes. Building it writes edge and junction tables.
2. Solvers: Route, Closest Facility, Service Area, OD Cost Matrix, with the documented outputs and field names.
   ~~Location-Allocation and Vehicle Routing come later, with a named heuristic, or are refused.~~ Location-Allocation
   was built with Teitz and Bart's vertex substitution; **vehicle routing, last mile delivery, waste collection and
   the travelling salesman order of a route were built on 2026-10-31 by
   [ADR-239](ADR-239-grapy-solves-fleet-routing-and-honours-turns.md)**, the owner having ruled that no name may be
   refused with a schedule.
3. Turns, hierarchy, traffic, time windows and live data are refused by name until each is defined. **Turns and time
   windows were defined on 2026-10-31 by [ADR-239](ADR-239-grapy-solves-fleet-routing-and-honours-turns.md)** — turn
   feature classes, global turn categories, U-turn policies, and a solver over the edge-expanded network; hierarchy,
   traffic and live data stay as this item left them.
4. Each solver is marked `_runtime.equivalent("Dijkstra's shortest path …", "Dijkstra 1959")` (ADR-224 §5.6).
5. `arcpy.nax`'s object API (`nax.MakeNetworkDatasetLayer`, `nax.Route` …) is built over the same model as the
   `arcpy.na` tools.
6. ~~**INFERRED, for confirmation:**~~ **Confirmed 2026-10-30 by owner decision in Q-195** (*"Hepsi kalsın"*): the table layout, Dijkstra as the equivalent for every path solver, and refusing
   hierarchy and turns at first.

## 6. Consequences

- **State.** In the GeoPackage the tool works on. Nothing in the catalogue or the server.
- About 230 refused names become buildable, mostly as equivalents.

## 7. Assumptions this decision rests on

- Impedances are non-negative, as the documentation requires.

## 8. Dependencies

[ADR-224](ADR-224-grapy-aims-at-arcpys-whole-buildable-surface.md), [ADR-225](ADR-225-grapy-holds-topology-and-relationship-classes.md)
(connectivity reuses its noding), scipy (already in the wheel set).

## 9. Revisit triggers

- Network analysis is wanted as a server service, not only inside a user tool. pgRouting in the datastore is then
  the candidate, and that is a new ADR.

## 10. Dissent

None recorded.

## 11. Conditions

1. **CreateNetworkDataset and BuildNetwork** build edges and junctions from line classes by end-point and any-vertex
   connectivity, tested on a hand-drawn network. **DISCHARGED 2026-10-30**: the same hand-drawn network builds 6 edges and 7 junctions with end-point connectivity, and 9 and 8 with any-vertex. Seven grapy tables hold the model, registered as the `grapy_network` extension.
2. **Route, ClosestFacility, ServiceArea and OD Cost Matrix** solve a network whose answers are unique, against
   hand-worked costs, each marked as an equivalent. **DISCHARGED 2026-10-30**: through `arcpy.na` (the Make*AnalysisLayer tools, AddLocations, Solve) and through `arcpy.nax`'s object API. Each solver is Dijkstra over `scipy.sparse.csgraph`, marked as an equivalent, with Service Area polygons as an equivalent of their own.
3. **Every remaining name** in `na`, `nax` and `nd` is built or refused with its reason. **DISCHARGED 2026-10-30**: no name in `na` or `nax` answers the generic message. `na` builds 22 of 62 and `nax` 35 of 95; every other name gives its reason (solver property objects, Esri's direction rules, NP-hard problems, traversed elements, the network query API, turns). `nd` turned out to be Network Diagrams, a different data model; all 74 of its names are refused as group 2. Of `na` and `nax`, 22 and 58 names were still unexamined when the reference list reached the builder. NP-hard solvers, turns, directions and Esri's template formats are refused by name.
