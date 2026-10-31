# ADR-239 — grapy solves fleet routing with published heuristics, and its networks hold turns

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` for the model and the heuristics · `LOW` for agreement with Pro's routes, which this cannot reproduce exactly |
| **Decided** | 2026-10-31, by owner decision on scope: no `arcpy.na` or `arcpy.nax` name may be refused with a schedule, and the NP-hard solvers and turns were refused with one (*"an NP-hard problem whose published heuristic is not chosen yet"*, *"needs turns, refused until they are defined"*). The heuristics, the turn model and the gaps the pages leave open are INFERRED and listed for confirmation in §5.8 and [Q-207](../open-questions.md). |
| **Supersedes** | — (amends [ADR-226](ADR-226-grapy-holds-network-datasets.md) §5.2 and §5.3) |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

[ADR-226](ADR-226-grapy-holds-network-datasets.md) built grapy's network datasets and their shortest-path solvers, and
left two kinds of name refused with a schedule. **Fleet routing** — `arcpy.nax.VehicleRoutingProblem`,
`LastMileDelivery`, `WasteCollection` and their enumerations, and `arcpy.na`'s Make…AnalysisLayer tools for them,
AddVehicleRoutingProblemBreaks and …Routes, UpdateFleetRoutingAssignments and …Results — was *"an NP-hard problem
whose published heuristic is not chosen yet"*. **Turns** — CreateTurnFeatureClass, TurnTableToTurnFeatureClass,
IncreaseMaximumEdges, PopulateAlternateIDFields, UpdateByAlternateIDFields, UpdateByGeometry, `nax.Turn` and
`nax.DrivingSide` — *"needs turns, refused until they are defined"*. The guard test
`test_no_name_is_refused_as_a_batch` forbids both wordings; the owner ruled on 2026-10-31 that they go.

The pages define every input and output class, field, code and constraint of the three fleet problems, and the turn
feature class's fields, the turn categories and the U-turn policies. They do not define the algorithms. The VRP
analysis layer page says only that the solver builds a time-neutral origin–destination matrix and decides assignment
and sequence from it.

## 2. Alternatives considered

### Alternative A — published heuristics as equivalents, over grapy's own network (chosen)

Solomon's insertion heuristic I1 (Solomon 1987) to build routes, ruin and recreate (Schrimpf, Schneider,
Stamm-Wilbrandt and Dueck 2000) with threshold accepting (Dueck and Scheuer 1990) to improve them, from a fixed seed;
waste collection as capacitated arc routing tasks, each street edge traversed whole, its direction chosen by dynamic
programming (Beullens, Muyldermans, Cattrysse and Van Oudheusden 2003). Turns as data in the network's tables, and a
solver over the edge-expanded graph: Dijkstra (1959) on the line graph Caldwell (1961) introduced for turn penalties,
with turn-prefix states for turns of more than two edges (Gutiérrez and Medaglia 2008). **For:** no new dependency;
every method has a citation; the result is reproducible; a fenced tool can run it. **Against:** every answer is an
equivalent, not Pro's; a heuristic gives no optimality guarantee.

### Alternative B — an exact or industrial solver library (OR-Tools, VROOM, a MIP solver)

**For:** OR-Tools (Apache 2.0) is mature, fast, and models time windows, capacities, pickups and deliveries and
breaks. **Against:** a large native wheel in the fenced runner's image for one module's names; its model is its own,
so the pages' constraints (two time windows with violation limits, renewals, route zones, the three break kinds,
overtime costs) would be translated into it anyway; it is a Tier 2 adoption for what Tier 1 says we write
(build-vs-adopt-policy §Tier 1: the query engine and cartographic logic — routing is the server's domain). Rejected
for now; §9 names when to reconsider.

### Alternative C — refuse each name with its own reason

**Against:** there is no reason that belongs to the names — the problems are textbook-defined and the pages define the
answer's shape. Refusing them would be the schedule under another wording. Rejected by the owner's ruling.

## 3. Counterarguments to the preferred option

A heuristic's routes will differ from Pro's on any instance with more than a handful of orders: Pro's objective weights
(time window and transit importance) are not published, and the INFERRED weights of §5.8 (f) are guesses. A user
comparing totals will find differences, and a test cannot prove the search is good on large instances — only that it
finds the optimum where brute force can check it. Pure-Python route evaluation is slow: a few hundred orders take
minutes, and no benchmark measures it ([D-330](../architecture-debt.md)).

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| The construction and improvement find the optimum on small instances | `test_heuristic_finds_the_brute_force_optimum`: six orders, two routes, capacities, two time windows, a fixed cost — every assignment and order enumerated, the heuristic's objective equal to the minimum | `src/grapy/tests/test_fleet.py` |
| The travelling salesman order of a route is optimal | Held and Karp's programme is exact; the test compares a five-stop route, open and closed, against every permutation | `test_route_finds_the_best_order` |
| Hand-worked fleet answers | two routes on a line, four orders: each takes its side, 10 minutes, sequences, arrivals, depot visits and route shapes as worked; LMD anchoring and sequence gap; a break five minutes into the drive | `test_fleet.py` |
| Restricted turns, turn delays and U-turn policies change routes as defined | a prohibited left at C sends the route to a dead end and back (U-turns allowed), or round the block (none); a three-edge turn binds only when its first edge was travelled; global left/right/straight delays; a two-edge turn's delay replaces the global turn; a service area reaches less through a delayed turn | `src/grapy/tests/test_turns.py` |
| The documented constraints and codes | each VRP code is the page's (0–16); hard windows, specialties, capacity and zones keep orders off routes with that code | VRP analysis layer page, LastMileDelivery and WasteCollection output data types |

No performance claim is made.

## 5. Decision

1. **Turns** (`grapy/_turns.py`). A turn feature class is a line class with `Edge1End` and `Edge#FCID`, `Edge#FID`,
   `Edge#Pos` for 1 … its maximum edges (default 5, at most 50). A network's turn sources are `grapy_network_turn_source`;
   BuildNetwork writes each turn as the chain of network edges it traverses, with directions, to
   `grapy_network_turn`; a turn that does not chain is left out with a warning. Turn evaluators are `FIELD` or
   `CONSTANT` on a turn source; the global turns take a `TURN_CATEGORY` evaluator (delays in seconds for straight,
   left, right and reverse, azimuth range 30–150° by default), a time attribute only, as the turn category evaluator
   page documents. A travel mode's U-turn policy — ALLOW_UTURNS, NO_UTURNS, ALLOW_DEAD_ENDS_ONLY,
   ALLOW_DEAD_ENDS_AND_INTERSECTIONS_ONLY — is stored and honoured at junctions. When a network has turns, or a travel
   mode limits U-turns, every solver uses the edge-expanded Dijkstra; otherwise ADR-226's.
2. **Turn tools built:** CreateTurnFeatureClass (with a template less the documented turn table names, and a network
   to join), IncreaseMaximumEdges, PopulateAlternateIDFields, UpdateByAlternateIDFields, UpdateByGeometry,
   TurnTableToTurnFeatureClass (ArcView turn tables; its nodes table and ArcInfo INFO tables refused, their layouts
   not being published), `NetworkDataset.turns()` and `turnCount`, Describe's `supportsTurns` and `turnSources`, and
   CreateNetworkDataset taking a turn class as a turn source. `arcpy.graticula` adds AddNetworkTurnSource,
   SetNetworkDrivingSide and AddNetworkTravelMode's `u_turns`.
3. **Fleet routing** (`grapy/_fleet.py`) — VehicleRoutingProblem (schema versions One and Two), LastMileDelivery and
   WasteCollection, and their analysis layers — with the documented inputs, outputs, fields and violation codes, by
   the heuristics of §2 A; each solver marked `__grapy_equivalent__` and warning once. AddVehicleRoutingProblemBreaks
   and …Routes fill the layers as documented; UpdateFleetRoutingResults and …Assignments resequence a waste collection
   layer's routes by Or-opt moves (Or 1976), allowed to violate time, distance and capacity with a warning, as
   documented.
4. **The travelling salesman order** of a route — `FIND_BEST_ORDER`, `PRESERVE_FIRST`, `PRESERVE_LAST`,
   `PRESERVE_BOTH`, and nax.Route's `findBestSequence`, `preserveFirstStop`, `preserveLastStop`, `returnToStart` — by
   Held and Karp's exact programme (1962) up to ten stops and nearest neighbour then 2-opt (Croes 1958) beyond.
5. **Refused, with its own reason:** ~~`nax.Turn`, as the network element class custom evaluators receive (INFERRED,
   §5.8 d), with Edge and Junction under ADR-226 §5.1's script-evaluator reason.~~ **`nax.Turn` was built on 2026-10-31
   with the custom evaluators, by [ADR-246](ADR-246-grapy-runs-custom-evaluators-keeps-traversals-and-builds-solver-properties.md).** Directions, barriers on the fleet
   solvers, curb approaches other than either side, and preassigned renewal visits keep their refusals.
6. Every enumeration of the three solvers is built: the input and output data types (both VRP schemas),
   VehicleRoutingProblemSchemaVersion, StopCollectionMode, DrivingSide and Importance (which was listed among the
   direction names and is the VRP's time window and transit factor).
7. The seed is fixed (`_fleet.SEED`), so a solve is reproducible.
8. ~~**INFERRED, for the owner's confirmation**~~ **Confirmed 2026-10-31 by owner decision in Q-207** (*"Hepsini onayla"*) ([Q-207](../open-questions.md)):
   - (a) *Storage*: grapy's tables `grapy_network_turn_source`, `grapy_network_turn` and `grapy_network_property`;
     `Edge#FCID` holds the network's source id (1, 2 … as given), a GeoPackage having no feature class ids.
   - (b) *Turn semantics*: the rotation is measured from the last segment of the edge arrived on to the first of the
     edge left by; a two-edge turn feature replaces the global turn at its transition, a longer one is charged when its
     last edge is entered and the global turns inside it still apply; a stop part-way along an edge breaks no turn
     through that edge, and a U-turn there is no turn.
   - (c) *U-turn policies*: a dead end is a junction with one edge, an intersection one with three or more; a U-turn at
     a stop is the stop's, free.
   - (d) `nax.Turn`, whose page answered 404, is the custom evaluator's turn element, as Edge and Junction are.
   - (e) `DrivingSide`'s values (Unknown 0, LeftSide 1, RightSide 2); a network drives on the right unless set; a
     one-side waste collection pass collects the curb side.
   - (f) *The fleet objective*: serving orders first, then TotalCost, plus lateness × CostPerUnitTime × 10 / 1 / 0.1
     for High / Medium / Low importance, excess pair transit likewise, a soft zone's straight-line distance ×
     CostPerUnitDistance (or CostPerUnitTime), less Revenue; spatial clustering accepted with no term.
   - (g) *Breaks*: placed by the route's simulation — a time window break when its window opens (or before travel
     would miss it, waiting for it at the last stop otherwise); a travel or work time break part-way along the leg
     where the limit is reached (or before a service that would pass it); one never needed is not taken.
   - (h) *Renewals*: inserted where the load would no longer fit, at the renewal with the least detour, as many as
     needed; a loaded waste route empties at a renewal before its end depot.
   - (i) *Start*: at the earliest start (the start depot's window and InboundArriveTime permitting), then later by as
     much of the first order's wait as the latest start allows.
   - (j) The default date is today; UTC fields are null (no time zone attribute); an order served late has Status 6.
   - (k) Schema One's `ViolatedConstraints` is the sum of 2^code.
   - (l) LastMileDelivery's Sequence is n × sequenceGap from 0 at the start depot; its AssignmentRule 3 (Unassign)
     leaves the order off every route.
   - (m) *Waste collection*: a unit is the stops on one edge (and side, one-side); its edge is traversed whole; a null
     capacity is zero; a renewal with a Fraction unloads that fraction; with no stop collection mode, both sides; turns
     between units are not charged; RouteLines' ReportGrouping is the route's number; times are times of day.
   - (n) UpdateFleetRoutingAssignments' selection is that of a layer made over the Stops sublayer.
   - (o) AddVehicleRoutingProblemRoutes adds one route named `Route1` … by default; a new break's Precedence follows
     the route's existing breaks.
   - (p) TurnTableToTurnFeatureClass reads the edges' values as the line features' object ids, and draws each turn
     from the middle of its first edge through the junction to the middle of its second.
   - (q) FIND_BEST_ORDER without a preserve option leaves both ends free; with returnToStart the first stop is kept.

## 6. Consequences

**State.** In the GeoPackage the tool works on — the turn tables beside ADR-226's, and a layer's sublayers — and, for
a nax solver, in the process while it runs. Nothing in the catalogue or the server.

**Positive.** Every name of the two refused kinds but one is built; a user tool that routes a fleet, or a network with
turn restrictions, runs. Turns reach every ADR-226 solver, not only the new ones. A route can now find its best order.

**Negative.** Answers differ from Pro's (§3). Route evaluation is pure Python and unmeasured (D-330). Ten new INFERRED
choices await the owner.

**Ports created.** None: no dependency was added.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | Impedances and turn costs are non-negative, as the documentation requires of costs | Holds by the evaluators' definitions |

## 8. Dependencies

**Depends on:** [ADR-224](ADR-224-grapy-aims-at-arcpys-whole-buildable-surface.md),
[ADR-226](ADR-226-grapy-holds-network-datasets.md) (the network model and its solvers).

**Depended on by:** —

## 9. Revisit triggers

- A user tool's fleet solve stops at ADR-223's time limit — then OR-Tools (Alternative B) or a compiled evaluator is
  weighed with a benchmark.
- An owner's Pro run of one of the tests' instances: the objective weights of §5.8 (f) and the break placement of
  (g) are the first to change.

## 10. Dissent

None recorded.

## 11. Conditions

1. **Turns are defined and every solver honours them**: restricted turns, turn delays, global turn categories,
   multi-edge turns and U-turn policies change answers as documented, tested by hand-worked routes and a service area.
   **DISCHARGED 2026-10-31**: `test_turns.py`, seven tests.
2. **The fleet solvers find known optima**: hand-worked instances and a brute-force check of the heuristic.
   **DISCHARGED 2026-10-31**: `test_fleet.py`, nine tests, the brute-force one over every assignment and order of six
   orders on two routes.
3. **No name of the two kinds is refused with a schedule**: built, or refused with a reason of its own.
   **DISCHARGED 2026-10-31**: of the 33 names, 32 are built and `nax.Turn` is refused with the custom evaluator reason;
   `tools/grapy_refusals.py --check` holds the counts.
4. **The owner confirms §5.8's inferences**, or names the ones to change ([Q-207](../open-questions.md)). **DISCHARGED 2026-10-31** — confirmed as built by owner decision in Q-207 (*"Hepsini onayla"*).
5. **One instance is solved in ArcGIS Pro and compared**, the differences recorded — the objective weights and the break
   placement first.
