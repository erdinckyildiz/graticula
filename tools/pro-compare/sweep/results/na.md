# arcpy.na against ArcGIS Pro 3.7

Measured 2026-11-12, case module `cases/na.py`, 46 names (every built `arcpy.na` name; `_defined()` with and
without imports finds the same 46). Unit tests pinning each fix: `src/grapy/tests/test_pro_sweep_na.py`.

**The network.** Every case solves on a network the case module builds with documented tools, the same way on both
sides: a 5 × 5 grid of 150 m blocks over the fixture's square in a feature dataset (each block side bent at its middle
by its own offset, so no two routes cost the same), CreateNetworkDataset (`NO_ELEVATION`), BuildNetwork. Pro's
CreateNetworkDataset makes one cost attribute, Length in metres, and no travel mode; travel modes come from
ReplaceNetworkTravelModes with a JSON file. A file geodatabase holds one feature class of a name across its feature
datasets, so each grid is named after its dataset.

**The one asymmetric input.** The fleet routing names need a time cost, which neither CreateNetworkDataset nor any
documented tool adds on both sides. Pro's side is a template: CreateTemplateFromNetworkDataset of the base network,
its Length attribute and evaluators copied in the XML Pro wrote as Minutes (`!minutes!` for `!Shape!`), then
CreateNetworkDatasetFromTemplate; grapy's (whose template tools are refused by name) is
`arcpy.graticula.AddNetworkAttribute`. The two networks then hold the same attributes.

**First run** is the first comparison Pro completed (with grapy's Length already measured on the ellipsoid: that was
found by a probe before it, see *Length* below): MATCH 10, DIFF 16, SCHEMA 2, PRO-ERROR 17, BOTH-ERROR 1. The
PRO-ERRORs were the case's (feature class names repeated across feature datasets, `GetNASublayer` given a sublayer's
display name where Pro takes its class name, the Walk travel mode without a time attribute on a network with a time
cost, turns inserted outside an edit session, routes added without depots, a turn table without `NODE_`) and were
corrected; three stay, below.
**Now: MATCH 39, DIFF 5, PRO-ERROR 2** (both NOT COMPARABLE).

## What Pro does that grapy now does

- **Length is measured on the ellipsoid.** Pro's Length evaluator (`!Shape!`) is each edge's geodesic length: a route
  of 410 m on the UTM 35N grid near its central meridian costs 410.164 (410 / 0.9996); on Web Mercator at the equator
  a north–south kilometre costs 993.306 m (1 − e²), an east–west one 1000; 300 km off the UTM central meridian a
  999.29 m line costs exactly its geodesic length. grapy summed planar lengths. `_network._geodesic_lengths`; every
  solver (na, nax) reads it through `Graph`. Ten existing tests on Web Mercator networks pinned planar costs; each now
  scales its north–south legs by 1 − e², with a comment.
- **CreateNetworkDataset makes no travel mode.** grapy made one, Length. A solve with none uses Pro's `<Custom>` mode
  (impedance and distance Length); `_network.default_mode` (INFERRED on networks with other costs: the first distance
  cost, the first time cost).
- **Sublayer schemas.** Point barriers are class `Barriers` (grapy `PointBarriers`); every input carries
  `CurbApproach` 0, `SnapZ` 0.0, `Attr_<cost>` 0.0 for every cost attribute, and `Cutoff_<cost>`/`Breaks_<cost>`
  where Pro has them; a route's stops carry time windows, arrive/depart curb approaches and times, `LocationType` 0
  and a `Sequence` numbered as they are added; routes carry Start/End times (and UTC); totals and cumulative costs are
  written for the travel mode's impedance, time and distance attributes and the accumulated ones (grapy: impedance
  and accumulated only), with `Wait_`/`Violation_` fields for its time attribute; location-allocation facilities and
  demand points default Weight 1, FacilityType 0, DemandCount 0; a vehicle routing order AssignmentRule 3.
- **Curb approaches.** A route stop's ArriveCurbApproach/DepartCurbApproach and a closest facility route's
  IncidentCurbApproach/FacilityCurbApproach: the side of the vehicle (1 right, 2 left) — the stop's SideOfEdge when
  travelling the edge's digitised way, the other side against it (`_network._curb`).
- **GetNAClassNames' OUTPUT** lists the inputs a solve writes to: a route's Stops, a location-allocation's Facilities
  and Demand Points.
- **NAClassFieldMappings** lists the input properties Pro lists (time windows and `Attr_` included; outputs left out).
- **Service area polygon names** write whole cutoffs without decimals: `f1 : 0 - 200` (grapy `0.0 - 200.0`).
- **Solver properties defaults:** a route's orderingType `PRESERVE_NONE` and useTimeWindows `NO_TIMEWINDOWS`; a closest
  facility's timeOfDayUsage `NOT_USED` with no time of day; a service area's STANDARD polygons are `SIMPLE_POLYS` (grapy
  answered `DETAILED_POLYS`, and refused `SIMPLE_POLYS`; it now refuses `DETAILED_POLYS`, HIGH detail).
- **TravelMode** answers `useHierarchy` as `NO_HIERARCHY`/`USE_HIERARCHY`, and its JSON (str, ExportNetworkTravelModes)
  writes `uturnAtJunctions` with the REST words and `simplificationToleranceUnits` (grapy wrote `uTurns`); it reads
  either. A network's travel modes keep their type and description (grapy stored neither, so a mode replaced as
  AUTOMOBILE came back OTHER — found by the nax sweep, measured here too).
- **Refusals Pro makes:** ReplaceNetworkTravelModes refuses a travel mode with no time attribute on a network with a
  time cost (ERROR 030403, before anything is replaced); UpgradeNetwork refuses a network made at the current release
  (ERROR 030087 — every grapy network); AddVehicleRoutingProblemRoutes refuses routes with neither depot (ERROR
  030241); a waste collection solve fails on a stop with no CollectionMode when the analysis gives no default (ERROR
  030092; grapy INFERRED both sides).
- **AddVehicleRoutingProblemRoutes' defaults** are times alone, 8:00 and 10:00 on Esri's time-only date 1899-12-30,
  whatever the layer's default date, and AssignmentRule 1. `_fleet.moment` reads a 1899-12-30 time on the default date.
- **Fleet distances in the na layers** are in international miles (1609.344 m); grapy divided by the US survey mile,
  as arcpy.nax's DistanceUnits measure (D-323). The na layers now pass their own table (`na._FLEET_DISTANCE`); nax is
  unchanged.
- **A waste stop taken off its route** by UpdateFleetRoutingAssignments (NOT_ASSIGNED) reports no violated constraint;
  grapy reported a capacity violation.
- **TurnTableToTurnFeatureClass** reads an ArcView `.dbf` (grapy read only GeoPackage tables), requires `NODE_` (ERROR
  000591 without it) with `ARC1_`/`ARC2_` or `F_EDGE`/`T_EDGE`, and draws each turn from three quarters along its
  first edge to a quarter along its second with Edge#Pos 0.75 and 0.25 (grapy: the middles, positions 1 and 0).

## Every name

| Name | First run | Now | Action | Note |
|---|---|---|---|---|
| AddFieldToAnalysisLayer | MATCH | MATCH | — |  |
| AddLocations | DIFF | MATCH | fixed | Sequence, CurbApproach, LocationType, SnapZ, Attr_ defaults; Pro's location fields |
| AddVehicleRoutingProblemBreaks | PRO-ERROR | MATCH | case / fixed | case added routes without depots; grapy now refuses that as Pro does (030241) |
| AddVehicleRoutingProblemRoutes | PRO-ERROR | MATCH | fixed | first run: the time network failed (case); then EarliestStartTime/LatestStartTime 1899-12-30 08:00/10:00 and AssignmentRule 1 |
| BuildNetwork | MATCH | MATCH | — | the route's Length (geodesic, see above) |
| CalculateLocations | MATCH | MATCH | — |  |
| CheckIntersectingFeatures | MATCH | MATCH | — |  |
| ClosestFacilitySolverProperties | DIFF | MATCH | fixed | timeOfDayUsage NOT_USED |
| CopyNetworkAnalysisLayer | DIFF | MATCH | fixed | Barriers class name; Routes' time fields |
| CopyTraversedSourceFeatures | DIFF | DIFF | recorded | everything matches but the system junctions' SourceOID: Pro numbers the grid's junctions by x then y ((150,0) is 6), grapy as the edge ends are met (2); EID (internal) and the NA class table's random suffix are not compared |
| CreateNetworkDataset | DIFF | MATCH | fixed | no travel mode |
| CreateTurnFeatureClass | PRO-ERROR | MATCH | case | Pro makes a turn class in a feature dataset only (030074 at the geodatabase root) |
| DeleteNetworkAnalysisLayer | MATCH | MATCH | — |  |
| ExportNetworkTravelModes | PRO-ERROR | MATCH | case / fixed | case: time network; the file's keys |
| GetNAClassNames | DIFF | MATCH | fixed | Barriers; OUTPUT lists the inputs a solve writes |
| GetNASublayer | DIFF | MATCH | fixed | Pro's fields |
| GetSolverProperties | DIFF | MATCH | case | Pro's class name prints oddly; not compared |
| GetTravelModes | PRO-ERROR | MATCH | case / fixed | case: Walk needs a time attribute there; useHierarchy keyword; type and description kept |
| IncreaseMaximumEdges | PRO-ERROR | MATCH | case | as CreateTurnFeatureClass |
| LocationAllocationSolverProperties | MATCH | MATCH | — |  |
| MakeClosestFacilityAnalysisLayer | DIFF | MATCH | fixed | fields; Incident/FacilityCurbApproach |
| MakeLastMileDeliveryAnalysisLayer | PRO-ERROR | PRO-ERROR | not comparable | Pro: "ERROR 030353: Network has no time zone attribute." A time zone attribute needs a time zone table and a template whose time zone XML is not documented; grapy has no time zones |
| MakeLocationAllocationAnalysisLayer | DIFF | MATCH | fixed | Weight 1, fields |
| MakeNetworkDatasetLayer | SCHEMA | MATCH | fixed | Routes' time fields |
| MakeODCostMatrixAnalysisLayer | MATCH | MATCH | — |  |
| MakeRouteAnalysisLayer | DIFF | MATCH | fixed | fields, defaults, curb approaches |
| MakeServiceAreaAnalysisLayer | DIFF | DIFF | recorded | lines match; polygons are grapy's EQUIVALENT (Pro's STANDARD polygons lie on a ~25 m lattice, x.25 coordinates; its algorithm is not published). Names fixed (`0 - 200`) |
| MakeVehicleRoutingProblemAnalysisLayer | PRO-ERROR | DIFF | recorded / fixed | the plan matches (route, order sequence, travel times, distances once the international mile was taken); open: order times (Pro whole seconds, grapy fractions), UTC times (Pro = local with no time zone, grapy null), Arrive/DepartCurbApproach (Pro 0, grapy the side) |
| MakeWasteCollectionAnalysisLayer | PRO-ERROR | MATCH | case / fixed | Pro wants a collection mode (030092); grapy now too |
| NAClassFieldMap | MATCH | MATCH | — |  |
| NAClassFieldMappings | DIFF | MATCH | fixed | Pro's properties |
| ODCostMatrixSolverProperties | MATCH | MATCH | — |  |
| PopulateAlternateIDFields | PRO-ERROR | MATCH | case | turns edited in an edit session; Edge#FCID not compared (Pro writes the geodatabase's object class id) |
| ReplaceNetworkTravelModes | MATCH | MATCH | fixed | also: 030403 for a mode without a time attribute on a timed network (unit test) |
| RouteSolverProperties | DIFF | MATCH | fixed | orderingType PRESERVE_NONE, useTimeWindows NO_TIMEWINDOWS |
| ServiceAreaSolverProperties | DIFF | MATCH | fixed | polygonType SIMPLE_POLYS |
| Solve | SCHEMA | MATCH | fixed | Routes' time fields |
| TravelMode | DIFF | MATCH | fixed | useHierarchy keyword; JSON keys |
| TurnTableToTurnFeatureClass | PRO-ERROR | MATCH | fixed | .dbf read, NODE_ required, quarters and positions 0.75/0.25 |
| UpdateAnalysisLayerAttributeParameter | BOTH-ERROR | PRO-ERROR | not comparable | the network needs an attribute parameter, which Pro defines only in the network's properties dialog or a template; a parameter written into the template XML by guess was ignored (parameterCount 0), and Pro then fails with "ERROR 999999: Something unexpected caused the tool to fail" |
| UpdateByAlternateIDFields | PRO-ERROR | MATCH | case | as PopulateAlternateIDFields |
| UpdateByGeometry | PRO-ERROR | MATCH | case | as PopulateAlternateIDFields |
| UpdateFleetRoutingAssignments | PRO-ERROR | DIFF | fixed in part | case: Pro takes the selection on the layer's own Stops sublayer (030399 for a feature layer made over it; grapy accepts both). Fixed: a stop taken off its route reports no ViolatedConstraint (grapy reported capacity). Open: RouteLineID, as UpdateFleetRoutingResults |
| UpdateFleetRoutingResults | PRO-ERROR | DIFF | recorded | stops' assignment and sequence match; RouteLineID, the id of a RouteLines row, differs (Pro 17, grapy 8): Pro splits route lines differently (EQUIVALENT waste solver, its line segmentation not published) |
| UpgradeNetwork | PRO-ERROR | MATCH | fixed | ERROR 030087 for a current network |
| VehicleRoutingProblemSolverProperties | PRO-ERROR | MATCH | case | Pro's answers no impedance, accumulators or time of day (AttributeError); grapy answers them (left) |

## Open differences worth a debt row

1. **Vehicle routing outputs' times and curb approaches** (MakeVehicleRoutingProblemAnalysisLayer): Pro writes visit
   times to the whole second, UTC times equal to local ones on a network without time zones, and ArriveCurbApproach /
   DepartCurbApproach 0 for orders whose CurbApproach is 0; grapy writes fractional seconds, null UTC (INFERRED) and the
   side it derives. The writer (`_fleet`) is shared with arcpy.nax, which should be measured with it: ~0.5 day.
2. **System junction numbering** (CopyTraversedSourceFeatures; nax NetworkDataset junction ids): Pro's ObjectIDs on
   this grid follow x then y; grapy's the order edge ends are met. Six junctions fit the rule; an irregular network
   should be measured before it is adopted, and existing tests renumbered: ~0.5 day.
3. **Service area polygons** (STANDARD detail) are an EQUIVALENT; Pro's lie on a lattice of about 25 m whose rule is
   not published. No fix proposed.
4. **Waste collection route lines** (UpdateFleetRoutingResults / Assignments): RouteLineID differs because Pro cuts
   route lines differently; part of the EQUIVALENT waste solver.
5. **Service area lines** carry FromCumul_/ToCumul_ for the impedance only; Pro writes them for every total (Length
   and Minutes under a timed mode). Not exercised by the case (Length only): ~2 hours.
6. **Not comparable here:** MakeLastMileDeliveryAnalysisLayer (needs a time zone attribute) and
   UpdateAnalysisLayerAttributeParameter (needs an attribute parameter Pro defines only in a dialog or an
   undocumented template element).
7. The default route's `generate_directions_on_solve` is `DIRECTIONS`, which grapy refuses by name (directions are not
   built), so a MakeRouteAnalysisLayer call with Pro's defaults is refused; the cases pass `NO_DIRECTIONS`.
