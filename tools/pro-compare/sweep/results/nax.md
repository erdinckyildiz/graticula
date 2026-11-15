# arcpy.nax against ArcGIS Pro 3.7

Measured 2026-11-12, case module `cases/nax.py`, 77 names (every built `arcpy.nax` name: `_defined()` with and without
imports finds the same 77). The module builds its own network (it does not share the arcpy.na sweep's): a 5 × 5 street
grid of 100 m blocks in UTM 35N, `Length` (from CreateNetworkDataset) and `Minutes` (a field), and two travel modes
written with ReplaceNetworkTravelModes. Pro's CreateNetworkDataset makes no attribute but Length and no travel mode,
and arcpy has no tool that adds an attribute, so on Pro `Minutes` goes in through the network's XML template
(CreateTemplateFromNetworkDataset, one attribute added, CreateNetworkDatasetFromTemplate) and on grapy through
`arcpy.graticula.AddNetworkAttribute`; everything after is the same calls on both sides. Enumerations are compared as
their [name, value] members; solver results by exporting each output; the custom evaluator classes by what a
temporary evaluator is handed during a solve.

**Pro's Length is geodesic** (a 100 m block near the central meridian costs 100.04 m, 100 / 0.9996). The arcpy.na
sweep fixed that in `_network.py` on its branch; this branch does not, to keep the two from conflicting. The verdicts
"now" are with that fix applied to the worktree for the run (not committed here): without it ClosestFacility, Edge,
LocationAllocation, MakeNetworkDatasetLayer, NetworkDataset and OriginDestinationCostMatrix are DIFF on distance
totals alone (MATCH 63, DIFF 13, BOTH-ERROR 1).

First run: 13 PRO-ERRORs, all the case's (Pro's NAClassFieldMap cannot be constructed, AltID must exist before
PopulateAlternateIDFields, a solver needs a travel mode before it is made, `self.attribute` is None in `attach`, …).
First comparable run: MATCH 11, DIFF 60, GRAPY-ERROR 3, BOTH-ERROR 3.
Now: MATCH 69, DIFF 7, BOTH-ERROR 1 (LastMileDelivery, not comparable).

Also changed in the harness: `compare.py` pairs rows in order when no shape distance between them is finite (it
crashed on Pro's shapeless service area line pieces).

| Name | First run | Now | Action | Note |
|---|---|---|---|---|
| AdjacentEdge | DIFF | MATCH | aligned | Pro's AdjacentEdge is no NetworkElement and NetworkQuery.edges(junction) answers Edge objects; grapy's did both the other way (INFERRED) |
| Attribute | DIFF | MATCH | aligned | an attribute's index from 1 (Length 1), printed and int() as that index |
| AttributeEvaluator | DIFF | MATCH | aligned | self.attribute is None during attach() and set after; case avoids a tie between equal-cost paths |
| AttributeParameter | MATCH | MATCH | — |  |
| AttributeParameterUsage | MATCH | MATCH | — |  |
| AttributeUsage | DIFF | MATCH | aligned | Restriction 2, Hierarchy 3 (grapy had them swapped) |
| BuildNetwork | DIFF | MATCH | fixed | edgeCount counts each edge once per direction (80 for 40 blocks) |
| CalculateLocations | DIFF | MATCH | fixed | SideOfEdge is 2 for a point exactly on the line (grapy said 1); _network.location_fields |
| ClosestFacility | DIFF | MATCH | fixed | Routes/Facilities/Incidents carry Pro's fields: FacilityID/IncidentID are the inputs' ID property, OutputFacilityOID/OutputIncidentOID the rows', curb approaches, location fields; cutoff in solver units. MATCH needs the na branch's geodesic Length |
| ClosestFacilityInputDataType | DIFF | MATCH | aligned | enumeration: an IntEnum with Pro's members, values and order (was string-valued) |
| ClosestFacilityOutputDataType | DIFF | MATCH | aligned | enumeration: an IntEnum with Pro's members, values and order (was string-valued) |
| CreateNetworkDataset | DIFF | DIFF | recorded | Pro creates no travel mode; grapy creates 'Length'. na.CreateNetworkDataset is the na sweep's code; left to it |
| CreateTurnFeatureClass | MATCH | MATCH | — |  |
| CurbApproach | DIFF | MATCH | aligned | enumeration: an IntEnum with Pro's members, values and order (was string-valued) |
| DecayFunctionType | DIFF | MATCH | aligned | enumeration: an IntEnum with Pro's members, values and order (was string-valued) |
| DistanceUnits | DIFF | MATCH | aligned | enumeration: an IntEnum with Pro's members, values and order (was string-valued) |
| DrivingSide | DIFF | MATCH | aligned | enumeration: an IntEnum with Pro's members, values and order (was string-valued) |
| Edge | DIFF | MATCH | aligned | isAlong, isAgainst, opposite; repr is the EID. MATCH needs the na branch's geodesic Length (base value 100.04 for a 100 m block) |
| ExportNetworkTravelModes | DIFF | DIFF | recorded | grapy loses a travel mode's type (AUTOMOBILE written back as OTHER) and description: the travel mode table keeps neither. na's code (na.ExportNetworkTravelModes, _network); left to the na sweep |
| GetTravelModes | MATCH | MATCH | — |  |
| Importance | DIFF | MATCH | aligned | enumeration: an IntEnum with Pro's members, values and order (was string-valued) |
| IncreaseMaximumEdges | MATCH | MATCH | — |  |
| Junction | MATCH | MATCH | — |  |
| LastMileDelivery | BOTH-ERROR | BOTH-ERROR | not comparable | Pro: ValueError 'Network has no time zone attribute.' at construction; no arcpy tool adds a time zone attribute to a network (CreateNetworkDataset makes none) |
| LastMileDeliveryInputDataType | DIFF | MATCH | aligned | enumeration: an IntEnum with Pro's members, values and order (was string-valued) |
| LastMileDeliveryOutputDataType | DIFF | MATCH | aligned | enumeration: an IntEnum with Pro's members, values and order (was string-valued) |
| LineShapeType | DIFF | MATCH | aligned | enumeration: an IntEnum with Pro's members, values and order (was string-valued) |
| LocationAllocation | DIFF | MATCH | fixed | Facilities' Total_/TotalWeighted_ of the impedance only; Lines' TotalWeighted_ of the impedance only; FacilityOID/DemandOID; location fields; cutoff in solver units. MATCH needs the geodesic Length |
| LocationAllocationInputDataType | DIFF | MATCH | aligned | enumeration: an IntEnum with Pro's members, values and order (was string-valued) |
| LocationAllocationOutputDataType | DIFF | MATCH | aligned | enumeration: an IntEnum with Pro's members, values and order (was string-valued) |
| LocationAllocationProblemType | DIFF | MATCH | aligned | enumeration: an IntEnum with Pro's members, values and order (was string-valued) |
| MakeNetworkDatasetLayer | DIFF | MATCH | — | DIFF was the planar Length only; MATCH with the geodesic Length |
| MessageSeverity | DIFF | MATCH | aligned | enumeration: an IntEnum with Pro's members, values and order (was string-valued) |
| NAClassFieldMap | DIFF | MATCH | fixed / case | Pro cannot construct one (TypeError); the case reads one from fieldMappings. Every property maps to its own name |
| NAClassFieldMappings | DIFF | MATCH | fixed | Pro's properties and defaults (CurbApproach 0, LocationType 0, Additional* 0, BearingTol 30), each mapped to its own name whatever the candidates |
| NetworkDataset | DIFF | MATCH | fixed | edgeCount; MATCH needs the geodesic Length. EIDs not compared (Pro numbers them per direction, spatially) |
| NetworkElement | DIFF | MATCH | aligned | AdjacentEdge no longer a subclass |
| NetworkTimeUsage | DIFF | MATCH | aligned | no Unused member (attributeValue's time_usage defaults to None) |
| OriginDestinationCostMatrix | DIFF | MATCH | fixed | Lines: no Name; Total_Time/Total_Distance in timeUnits/distanceUnits; OriginName/DestinationName; Origins' DestinationCount, Destinations' OriginCount; cutoff in solver units. MATCH needs the geodesic Length |
| OriginDestinationCostMatrixInputDataType | DIFF | MATCH | aligned | enumeration: an IntEnum with Pro's members, values and order (was string-valued) |
| OriginDestinationCostMatrixOutputDataType | DIFF | MATCH | aligned | enumeration: an IntEnum with Pro's members, values and order (was string-valued) |
| PopulateAlternateIDFields | BOTH-ERROR | MATCH | case | Pro needs the AltID field on the edge source first (030028); the case adds it |
| ReplaceNetworkTravelModes | MATCH | MATCH | — | the file is {name: travel mode JSON}, as Pro reads it |
| Route | DIFF | DIFF | fixed / recorded | Routes and Stops carry Pro's fields (curb approaches, location fields, times None, FirstStopOID the loaded feature's ObjectID). Open: RouteEdgeID - Pro's EIDs are per direction and numbered spatially, not derivable |
| RouteInputDataType | DIFF | MATCH | aligned | enumeration: an IntEnum with Pro's members, values and order (was string-valued) |
| RouteOutputDataType | DIFF | MATCH | aligned | enumeration: an IntEnum with Pro's members, values and order (was string-valued) |
| RouteShapeType | DIFF | MATCH | aligned | enumeration: an IntEnum with Pro's members, values and order (was string-valued) |
| ServiceArea | DIFF | DIFF | fixed / recorded | cutoffs in distanceUnits/timeUnits (grapy read 0.15 km as 0.15 m), default cutoffs [5, 10, 15], polygon names '0 - 0.15', Name_1/Breaks/Additional*, lines SourceName and FromCumul_/ToCumul_ by unit. Open: lines are cut differently (Pro writes zero-length, shapeless pieces) and Pro's lines carry the time cumul of a distance solve; polygons EQUIVALENT |
| ServiceAreaInputDataType | DIFF | MATCH | aligned | enumeration: an IntEnum with Pro's members, values and order (was string-valued) |
| ServiceAreaOutputDataType | DIFF | MATCH | aligned | enumeration: an IntEnum with Pro's members, values and order (was string-valued) |
| ServiceAreaOutputType | DIFF | MATCH | aligned | enumeration: an IntEnum with Pro's members, values and order (was string-valued) |
| ServiceAreaOverlapGeometry | DIFF | MATCH | aligned | enumeration: an IntEnum with Pro's members, values and order (was string-valued) |
| ServiceAreaPolygonCutoffGeometry | DIFF | MATCH | aligned | enumeration: an IntEnum with Pro's members, values and order (was string-valued) |
| ServiceAreaPolygonDetail | DIFF | MATCH | aligned | enumeration: an IntEnum with Pro's members, values and order (was string-valued) |
| StopCollectionMode | MATCH | MATCH | — |  |
| TimeOfDayUsage | DIFF | MATCH | aligned | enumeration: an IntEnum with Pro's members, values and order (was string-valued) |
| TimeUnits | DIFF | MATCH | aligned | enumeration: an IntEnum with Pro's members, values and order (was string-valued) |
| TimeZoneUsage | DIFF | MATCH | aligned | enumeration: an IntEnum with Pro's members, values and order (was string-valued) |
| TravelDirection | DIFF | MATCH | aligned | enumeration: an IntEnum with Pro's members, values and order (was string-valued) |
| TravelMode | DIFF | DIFF | recorded | Pro answers useHierarchy as 'NO_HIERARCHY', keeps type AUTOMOBILE and description None; grapy False, OTHER, ''. na.TravelMode is the na sweep's; left to it |
| TraversedEdge | MATCH | MATCH | — | hierarchy only: Pro hands these objects only to a DirectionsCustomizer, which grapy refuses |
| TraversedElement | MATCH | MATCH | — | as TraversedEdge |
| TraversedElementType | DIFF | MATCH | aligned | enumeration: an IntEnum with Pro's members, values and order (was string-valued) |
| TraversedJunction | MATCH | MATCH | — | as TraversedEdge |
| TraversedTurn | DIFF | MATCH | aligned | not a TraversedElement in Pro; grapy's now shares _Traversed instead |
| Turn | GRAPY-ERROR | MATCH | case | Pro's NetworkQuery.edges(turn) is a cursor (no len); first run GRAPY-ERROR was UpdateByGeometry on an unbuilt network |
| UpdateByAlternateIDFields | BOTH-ERROR | MATCH | case | Pro refuses an UpdateCursor on a network's turn class outside an edit session; the case uses CalculateField |
| UpdateByGeometry | GRAPY-ERROR | MATCH | case | grapy needs a built network (Pro updates nothing on an unbuilt one, silently); the case builds it first |
| VehicleRoutingProblem | DIFF | DIFF | fixed / recorded | schema One Routes hold the solve's fields only, ViolatedConstraints 0, UTC = local times, start depot FromPrevDistance 0; Pro's input field order. Open: the two orders tie at 7 min and Pro visits O2, O1, O3, grapy O2, O3, O1 (EQUIVALENT heuristic); Pro writes Arrive/DepartCurbApproach 0, ORIG_FID 0 and SnapX/SnapY on schema One stops |
| VehicleRoutingProblemInputDataType | DIFF | MATCH | aligned | enumeration: an IntEnum with Pro's members, values and order (was string-valued) |
| VehicleRoutingProblemInputDataType2 | DIFF | MATCH | aligned | enumeration: an IntEnum with Pro's members, values and order (was string-valued) |
| VehicleRoutingProblemOutputDataType | DIFF | MATCH | aligned | enumeration: an IntEnum with Pro's members, values and order (was string-valued) |
| VehicleRoutingProblemOutputDataType2 | DIFF | MATCH | aligned | enumeration: an IntEnum with Pro's members, values and order (was string-valued) |
| VehicleRoutingProblemSchemaVersion | DIFF | MATCH | aligned | enumeration: an IntEnum with Pro's members, values and order (was string-valued) |
| WasteCollection | GRAPY-ERROR | DIFF | fixed / recorded | first run GRAPY-ERROR: export could not write a time of day; Pro needs stopCollectionMode (030092) and the case sets it. Aligned: Routes echo route properties and carry no shape, null totals of measures no stop gives, null RenewalCount, EndTime to the second, Stops echo properties and location. Open: RouteLineID numbering (Pro cuts fewer route lines) |
| WasteCollectionInputDataType | DIFF | MATCH | aligned | enumeration: an IntEnum with Pro's members, values and order (was string-valued) |
| WasteCollectionOutputDataType | DIFF | MATCH | aligned | enumeration: an IntEnum with Pro's members, values and order (was string-valued) |

## Open differences worth a debt row

- **CreateNetworkDataset, TravelMode, ExportNetworkTravelModes** (arcpy.na's code, left to the na sweep): grapy's
  CreateNetworkDataset adds a `Length` travel mode Pro does not make; a travel mode loses its `type` and `description`
  (the network's travel mode table keeps neither) and answers `useHierarchy` as a bool where Pro answers
  `'NO_HIERARCHY'`.
- **Network element IDs** (Route's RouteEdgeID, NetworkDataset.edges' EID): Pro gives each direction of an edge its
  own EID and numbers them in a spatial order; grapy numbers edges once, in build order. Not derivable; a few days to
  make grapy's EIDs per direction across `_network`, `_turns`, `_traversal` and the evaluators, and the order would
  still differ.
- **ServiceArea lines and polygons**: Pro cuts the reached network into different pieces (zero-length, shapeless
  ones among them) and writes every cost attribute's cumulative value (FromCumul_Minutes in a distance solve); grapy
  writes the impedance's. Polygons are EQUIVALENT (Pro's generation is not published).
- **VehicleRoutingProblem schema One stops**: Pro writes Arrive/DepartCurbApproach 0, ORIG_FID 0 and SnapX/SnapY on
  the stops; grapy writes the sides and the order's ObjectID. When routes tie Pro's heuristic picks another order.
- **WasteCollection RouteLineID**: Pro cuts a route into fewer lines than grapy.
- **LastMileDelivery** is not comparable here: Pro refuses a network with no time zone attribute, and no arcpy tool
  makes one.
- **A failed solve**: Pro answers a result with `solveSucceeded` False and the reasons in `solverMessages` (e.g.
  030092 for a waste collection with no collection mode); grapy raises ExecuteError, and its `solverMessages` is
  always empty. A custom evaluator's `edgeValue` returning None failed Pro's solve (030024); grapy keeps the base
  value (INFERRED).
