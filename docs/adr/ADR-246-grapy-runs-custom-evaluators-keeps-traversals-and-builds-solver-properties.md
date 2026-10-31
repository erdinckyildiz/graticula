# ADR-246 — grapy runs custom evaluators, keeps what its solvers traversed, and builds the solver property objects

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `HIGH` that the three groups are buildable · `MEDIUM` for the choices the pages leave open (§5.6) |
| **Decided** | 2026-10-31, by owner instruction for round 4 of the grapy surface (*"look again at refusals whose reason is grapy's own design choice"*). The choices the pages leave open are INFERRED and listed for confirmation in §5.6 and [Q-216](../open-questions.md). |
| **Supersedes** | — (reverses part of [ADR-226](ADR-226-grapy-holds-network-datasets.md) §5.1, and [ADR-239](ADR-239-grapy-solves-fleet-routing-and-honours-turns.md) §5.5's refusal of `nax.Turn`) |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

Three groups of `arcpy.na` and `arcpy.nax` names were refused for a reason that was grapy's own design choice, not a
missing page:

- **Custom evaluators** — `AttributeEvaluator`, `NetworkElement`, `Edge`, `Junction`, `Turn`, `AdjacentEdge`,
  `Attribute`, `AttributeParameter`, `AttributeParameterUsage`, `AttributeUsage`, `NetworkTimeUsage` — *"script
  evaluators, refused (ADR-226 §5.1)"*. ADR-239 added `Turn` to them as the custom evaluator's turn element.
- **Traversed elements** — `TraversedEdge`, `TraversedElement`, `TraversedElementType`, `TraversedJunction`,
  `TraversedTurn` and `na.CopyTraversedSourceFeatures` — *"grapy's solvers keep routes, not their traversed
  elements"*.
- **Solver property objects** — `GetSolverProperties` and the six `…SolverProperties` classes — *"grapy's layers take
  their settings from the Make tools"*. `na.UpdateAnalysisLayerAttributeParameter` stood with them: *"grapy's
  evaluators take none"*.

Each reason describes what grapy had chosen to hold, not what the pages leave undefined. The pages define all three:

- the *custom evaluators* topic and the AttributeEvaluator, NetworkQuery, NetworkElement, Attribute,
  AttributeParameter, AttributeUsage, AttributeParameterUsage and NetworkTimeUsage pages;
- the *Route output data types* page (RouteEdges, RouteJunctions, RouteTurns), the Route class's `returnRouteEdges`,
  `returnRouteJunctions` and `returnRouteTurns`, the TraversedElement, TraversedEdge, TraversedJunction, TraversedTurn
  and TraversedElementType pages, and *Copy Traversed Source Features* with its *tool output* page;
- GetSolverProperties and the six solver property pages.

The Edge, Junction, Turn and AdjacentEdge class pages answered 404 on doc.esri.com on 2026-10-31. What they are is
nevertheless fixed by the pages that use them: NetworkElement's `isEdge`, `isJunction`, `isTurn`, and NetworkQuery's
methods, which take and answer them.

## 2. Alternatives considered

### Alternative A — build the three groups over grapy's own model (chosen)

grapy's solvers are Python, so a custom evaluator runs inside them: the travel mode's graph calls it when it is built
for a solve. The solvers already find each route's path as the pieces of edges it runs along and the turns between
them; keeping that path, rather than only its shape and totals, is the traversal. An analysis layer already holds its
settings and its travel mode; the solver property objects read and write those. **For:** no new dependency, every name
follows its page, and no algorithm is invented. **Against:** a custom evaluator runs the user's Python in every solve,
once per element — as it does in Pro — and grapy measures nothing about its speed.

### Alternative B — keep the refusals

**Against:** the owner's round-4 instruction asks for each refusal whose reason is grapy's own choice to be looked at
again; none of these reasons belongs to the name. Rejected.

### Alternative C — build the evaluator API but call it only from nax solvers

**Against:** persisted evaluators are part of the network, so every solve on it — `arcpy.na` layers included — must
use them, as the topic says. Rejected; both modules share `_network.Graph`, which attaches them.

## 3. Counterarguments to the preferred option

The Traversed* objects are documented as reached through a directions customizer's `DirectionsQuery`, which stays
refused (Esri's direction rules are not published). Building the objects without their documented door could look
like a name built for the count. The answer is `arcpy.graticula.TraversedElements(result)`: the objects are the same
ones, read from the RouteEdges, RouteJunctions and RouteTurns the pages define, and the door is named as grapy's own
(INFERRED, §5.6 h). The directions-only properties (names, landmarks, direction points, times) answer empty, as an edge
with no directions configuration would.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| A temporary evaluator changes a solve, as the topic's sample does | the topic's doubling evaluator makes W→N 4 minutes instead of 2; `refresh` runs once per solve; one for a missing attribute is not attached; `attach` returning False detaches it; the network is unchanged after | `src/grapy/tests/test_network_evaluators.py` |
| Restriction, junction and turn values change routes | a restricted CN sends the route round by E (4); half a minute at C makes it 2.5; a restricted C leaves no route; a three-minute turn sends it to the dead end and back (3) | same |
| The NetworkQuery methods answer as documented | junctions of an edge each way, positions, azimuths, source info, the edges at a junction, a turn's edges and junction, base values | same |
| A persisted evaluator and attribute parameters | a class stored by `updateNetworkDatasetSchema` scales every solve by its parameter; a travel mode's, a layer's (UpdateAnalysisLayerAttributeParameter) and the solver properties' values reach it; a class that does not load leaves a warning and the solve goes on | same |
| A route keeps its edges, junctions and turns | RouteEdges, RouteJunctions and RouteTurns with the documented fields and hand-worked Attr_/Cumul_ values; a global turn as an ImplicitTurns row; the Traversed* objects in travel order | same |
| CopyTraversedSourceFeatures writes the documented outputs | a route, a service area and a vehicle routing problem (4, 8 and 4 minutes along a line, depots and orders as NA_CLASS junctions); an OD cost matrix layer refused as documented | same |
| The solver properties read and write the layer | every kind: impedance, accumulators and path shape change the next solve; refused values refused | same |
| ADR-239's tests stay green | `test_turns.py`, `test_fleet.py`, `test_network.py` unchanged but for the refusal list in `test_refused_by_name` | CI's command, §11 |

No performance claim is made.

## 5. Decision

1. **Custom evaluators** (`grapy/_evaluators.py`). `AttributeEvaluator(attribute_name, specific_sources=None)` with
   the documented read-only properties and methods. **Temporary** evaluators are a `NetworkDataset` object's
   `customEvaluators`; a nax solver, or an `arcpy.na` analysis layer, made from that object uses them. **Persisted**
   ones are stored by `NetworkDataset.updateNetworkDatasetSchema(custom_evaluators={attribute: {"class": …,
   "sourceNames": […]}})` and imported by every solve on the network; one that does not load is left out with a warning.
   Each solve's graph attaches them in the topic's order — attribute looked up, `attach`, `refresh` — and calls each
   overridden `edgeValue`, `junctionValue` and `turnValue` for the elements of its sources; the answer replaces the
   base value. A junction now carries a cost and a restriction when an evaluator gives one, and every solver — plain,
   over turns, fleet — honours them. **This reverses ADR-226 §5.1's refusal of script evaluators** for the custom
   evaluator API; a `SCRIPT` row in the evaluator table stays refused, pointing to the class.
2. **Attribute parameters.** `arcpy.graticula.AddNetworkAttributeParameter` defines one (the dialog's job in Pro);
   a travel mode's `attributeParameters`, a layer's solver properties and `UpdateAnalysisLayerAttributeParameter` give
   it a value for a solve; `NetworkQuery.attributeParameters` reads it. grapy's own field and constant evaluators take
   none.
3. **Traversals** (`grapy/_traversal.py`). The route and closest facility solvers keep each leg's path; Service Area
   keeps each line's edge piece; the vehicle routing and last mile delivery solvers keep their legs. From them grapy
   writes nax Route's **RouteEdges**, **RouteJunctions** and **RouteTurns** (`returnRouteEdges` …, True by default as
   documented for a network dataset), ClosestFacility's RouteEdges, **CopyTraversedSourceFeatures**' edges, junctions
   and turns (route, closest facility, service area, VRP and LMD layers, as documented; others refused as documented),
   and the **Traversed*** objects through `arcpy.graticula.TraversedElements`.
4. **Solver properties** (`grapy/_solverprops.py`). GetSolverProperties and the six classes, every documented property
   reading or writing the layer's settings or its own copy of its travel mode; `applyTravelMode`. Values grapy's
   solvers do not build — time of day, hierarchy, directions, and the service area and location-allocation options
   the Make tools refuse — are refused when set.
5. **Still refused**, each with its own reason: `DirectionsQuery` and the directions classes (Esri's direction rules);
   the `…AtTime` evaluator methods are defined but never called, grapy's solves being time neutral (traffic, ADR-226
   §5.3).
6. ~~**INFERRED, for the owner's confirmation**~~ **Confirmed 2026-10-31 by owner decision in Q-216** (*"Hepsini onayla"*) ([Q-216](../open-questions.md)):
   - (a) An `Edge` is one direction of travel along a network edge (`eid`, `direction`); Edge, Junction, Turn and
     AdjacentEdge's pages answered 404.
   - (b) `AdjacentEdge` is an Edge as reached from a junction, what `NetworkQuery.edges(junction)` answers.
   - (c) `None` from a value method keeps the base value; a junction's base value is 0 or False.
   - (d) A junction cost is charged when a route passes through the junction; a restricted junction cannot be passed.
   - (e) Enumeration values number the members in the order the pages list them; `NetworkTimeUsage.Unused`, named only
     by NetworkQuery's page, is 0.
   - (f) A temporary evaluator replaces a persisted one for the same attribute; `updateNetworkDatasetSchema` given
     `None` for an attribute removes its evaluator; the class is imported from `sys.path`.
   - (g) Traversal: consecutive pieces of one edge with no turn between are one edge; `RouteEdgeID`/`RouteTurnID` are
     EIDs and `RouteJunctionID` a junction's EID or a stop's ObjectID; a network junction's `SourceName` is
     `<network>_Junctions`; a restriction's `Attr_` is 0 (grapy's restrictions are hard); a service area writes no
     turns; times are None; a ClosestFacility result always keeps RouteEdges (its page names no switch).
   - (h) `arcpy.graticula.TraversedElements(result, route_id)` is the door to the Traversed* objects;
     `TraversedEdge` answers both `toPostition` (the page's spelling) and `toPosition`; `generalizedAzimuths` is the
     chord's azimuth.
   - (i) Solver properties: `orderingType` is `PRESERVE_BOTH` until set; service area polygons answer `DETAILED_POLYS`;
     settings grapy does not compute with (capacity, decay parameter, market share, time zones, time windows,
     clustering, time of day usage) are kept and change nothing; a property change that names a new impedance or
     accumulation adds its fields to the sublayers, as the Make tool would have.

## 6. Consequences

**State.** Persisted evaluators and attribute parameters in `grapy_network_property` (ADR-239's table); a layer's
traversal and its solver property values in the process, as the layer is.

**Positive.** 25 refused names are built: 16 in `arcpy.nax`, 9 in `arcpy.na`. A user tool can scale costs from its
own data, read what a route travelled, and change a layer between solves.

**Negative.** An evaluator's speed is the user's; grapy measures none. Seven new INFERRED choices await the owner.

**Ports created.** None: no dependency was added.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | A custom evaluator's value methods are pure for one solve: called again for the same element, they answer the same | The topic's samples are; grapy calls each once per element and direction |

## 8. Dependencies

**Depends on:** [ADR-224](ADR-224-grapy-aims-at-arcpys-whole-buildable-surface.md),
[ADR-226](ADR-226-grapy-holds-network-datasets.md), [ADR-239](ADR-239-grapy-solves-fleet-routing-and-honours-turns.md).

**Depended on by:** —

## 9. Revisit triggers

- Directions are built (a published rule set is chosen): `DirectionsQuery` becomes the documented door to the Traversed*
  objects, and `TraversedElements` an alias.
- Traffic is built: the `…AtTime` methods start being called.

## 10. Dissent

None recorded.

## 11. Conditions

1. **The three groups are built to their pages and tested by hand-worked answers.** **DISCHARGED 2026-10-31**:
   `test_network_evaluators.py`, nine tests; ADR-239's `test_turns.py` and `test_fleet.py` unchanged and green.
2. **The owner confirms §5.6's inferences**, or names the ones to change ([Q-216](../open-questions.md)). **DISCHARGED 2026-10-31** — confirmed as built by owner decision in Q-216 (*"Hepsini onayla"*).
