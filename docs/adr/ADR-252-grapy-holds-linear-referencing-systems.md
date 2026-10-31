# ADR-252 — grapy holds linear referencing systems: networks, events, intersections, calibration points and centerlines

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` for the registration tools, whose pages give their parameters, keywords, field types and constraints, and for the data model, whose user classes the data model pages tabulate · `HIGH` for the measures on the hand-worked networks of the tests (interpolation between calibration points, z in the length, the three gap rules as §5.9 (d) reads them) · `LOW` for agreement with Pro wherever a page describes a result without its rule — event behaviours, derived measures, concurrency sections, overlay columns — which grapy decides as §5.9 records |
| **Decided** | 2026-10-31, by owner decision *"Tamamını bitirelim artık"* and [ADR-224](ADR-224-grapy-aims-at-arcpys-whole-buildable-surface.md) §5.7 and condition 5: each group-2 data model gets its ADR before its tools. `arcpy.locref` was refused as a whole, *"linear referencing networks needs a data model this server does not hold yet (ADR-224 group 2)"*; a schedule is not a reason. How a GeoPackage holds an LRS, and the rules the pages leave open, are INFERRED and listed for confirmation in §5.9 and [Q-222](../open-questions.md). |
| **Supersedes** | — |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

`arcpy.locref` — ArcGIS Pro 3.7's Location Referencing toolbox, 49 names — answered every call with the module-wide
refusal above. A toolbox that loads routes, calibrates them, registers events and regenerates their shapes stopped on
the first call. The pages document the user classes of an LRS (Centerline, Centerline Sequence, Calibration Point,
Redline, LRS Networks, events, intersections) with their fields and types, and every tool's parameters and keywords.
They name but do not publish the internal tables (`Lrs_Metadata`, `Lrs_Edit_Log`, `Lrs_Locks`). Conflict prevention —
locks — "applies only when editing or geoprocessing branch-versioned data published as a feature service".

grapy has no `arcpy.lr`; the measure machinery the Location Referencing tools stand on — calibration, locating a
location on a route, dynamic segmentation — is written here, as the published methods of Calibrate Routes, Locate
Features Along Routes and Make Route Event Layer.

## 2. Alternatives considered

### Alternative A — the user classes under their documented names, registration in grapy's own tables (chosen)

Every class a user reads is an ordinary grapy feature class or table, with the fields and types the data model pages
give; what Pro keeps in `Lrs_Metadata` and `Lrs_Edit_Log` is grapy's own documented tables, as ADR-231 did for the
utility network. **For:** a toolbox reads and writes the classes with arcpy.da as it does in Pro; nothing unpublished is
reproduced. **Against:** a GeoPackage written here is not an LRS Pro opens as one; the registration is grapy's.

### Alternative B — refuse the module: Location Referencing is a licensed Esri extension

Every page says the tools "require ArcGIS Location Referencing". **Against:** that is a product licence, not a
technical dependency, and ADR-224 refuses a name only for a reason that belongs to it. Rejected.

### Alternative C — refuse the tools whose rule the page does not give (behaviours, derived measures, overlay)

**For:** literal. **Against:** ADR-224 §5.6 builds such tools as equivalents on a published method, and Apply Event
Behaviors is what makes events usable after a route edit. Rejected.

## 3. Counterarguments to the preferred option

Event behaviours are documented only by line-event examples on the Roads and Highways pages, and the reassign
activity has no outcome table at all; grapy's Apply Event Behaviors is an equivalent and may place an event differently
from Pro (condition 2). Route edits in Pro are made with interactive editing tools that are not arcpy; grapy's edit log
holds only the edits grapy's own tools made (GenerateRoutes, AppendRoutes), so a toolbox that edits centerlines and
calibration points with cursors sees them as logged edits only when it runs GenerateRoutes. Conflict prevention is not
held at all.

## 4. Evidence

Read 2026-10-31 on doc.esri.com (the pro.arcgis.com URLs redirect there). `T/` is
`…/arcgis-pro/latest/tool-reference/location-referencing/`, `RH/` `…/help/production/roads-highways/`, `PR/`
`…/help/production/location-referencing-pipelines/`. The research file, with every page and short quotes, is the
group-2 brief's `research-locref.md` (not in the repository).

| Claim | Evidence | Source |
|---|---|---|
| The minimum schema: Centerline (polyline, z; CenterlineID GUID), Centerline Sequence (CenterlineID, FromDate, ToDate, RouteID, NetworkID), Calibration Point (point, z, "cannot be m-enabled"; Measure, dates, RouteID, NetworkID), Redline (FromMeasure, ToMeasure, RouteID, RouteName, EffectiveDate, ActivityType, NetworkID); `dLRSNetworks` and `dActivityType` domains, codes not published | Data model pages | RH/lrs-data-model.html, PR/alrs-data-model.html |
| Events: EventID, RouteID, RouteName, FromDate, ToDate, Measure or From/ToMeasure, LocError; ToRouteID and ToRouteName for spanning events | Events data model; CreateLRSEvent | RH/events-data-model.html, T/createlrsevent.html |
| Intersections: IntersectionId, IntersectionName, RouteId, FeatureId, FeatureClassName, FromDate, ToDate, Measure; the class m- and z-enabled; the name `<network description><separator><layer description>`; comma only for the network on itself | Tool pages | T/modify-lrs-intersection.html, T/create-lrs-intersection.html |
| Measures between calibration points are "derived by linear interpolation"; at least two points per route | Vocabulary; data model | RH/essential-roads-and-highways-vocabulary.html, PR/alrs-data-model.html |
| Calibration point placement on simple routes and loops (ends, a third and two thirds); length counts z | *Create a new route* | PR/create-a-new-route.html |
| Gap rules: stepping increment, adding increment, Euclidean distance; offset 0 or above the m tolerance; line networks only 0 for the increments | Gap calibration page; tool page (fetched again 2026-10-31: no numeric example in the text) | RH/methods-for-calibrating-routes-with-physical-gaps.html, T/modify-network-calibration-rules.html |
| Interpolate between, extrapolate before and after, DISTANCE method | Calibrate Routes | pro.arcgis.com …/linear-referencing/calibrate-routes.htm |
| Event behaviour rules and their per-activity lists; the worked line-event examples of calibrate, extend, realign, retire, reverse, cartographic realignment; no table for reassign | Behaviour pages | RH/what-is-event-behavior.html and the per-activity pages; T/modify-event-behavior-rules.html |
| Time awareness: a null To Date "goes into the future"; an edit closes a record on its date and opens one | Time awareness | PR/time-awareness-in-pipeline-referencing.html |
| Concurrency: routes sharing a CenterlineID; SectionId, DominantFlag, DominantError 0–4; dominance rules with fields, order method and type, exceptions checked first | Tool pages | T/calculate-route-concurrencies.html, T/configure-route-dominance-rules.html, PR/route-dominance-configuration.html |
| Conflict prevention applies only to branch versioned data published as a feature service | ModifyLRS (fetched again 2026-10-31 for its 27 parameters) | T/modify-lrs.html |
| CreateLRSNetwork's DERIVE makes the output the derived network of `line_network_name`; LineID 38-character text or GUID, LineOrder long | Tool page (fetched again 2026-10-31) | T/create-lrs-network.html |
| ConfigureUtilityNetworkFeatureClasses: the Pipeline Line class serves as the centerline; file geodatabase or branch versioned connection; no services | Tool page (fetched again 2026-10-31) | T/configure-utility-network-feature-classes.html |
| ConfigureUtilityNetworkFeatureClass "requires a branch versioned enterprise geodatabase connection" and is deprecated | Tool page | T/configure-utility-network-feature-class.html |
| GenerateIntersections: editor tracking finds the edited routes; "must be enabled … to use this functionality"; start date and current user filters | Tool page (fetched again 2026-10-31) | T/generate-intersections.html |
| External events are relocated "through the web service"; the LRS (and the event store) must be in enterprise geodatabases | Tool pages | T/configure-external-event-behaviors-with-lrs.html, T/configure-external-event-with-lrs.html |
| The LR data products: CSV or table outputs whose layout is not given; the Data Product Designer's template `.json` | Tool pages; data products page | T/generate-linear-referenced-*.html, T/generate-lrs-data-product.html, RH/lrs-data-products.html |

No benchmark is at stake: the networks a toolbox builds on a server are small, and the measures are tested for
correctness on hand-worked networks.

## 5. Decision

1. **Storage** (`grapy/_lrs.py`). An LRS lives in a feature dataset. Its user classes are grapy classes with the data
   model's fields; networks are polylines with z and m, events points or polylines with z and m, intersections points
   with z and m. The registration is `grapy_lrs`, `grapy_lrs_network`, `grapy_lrs_event`, `grapy_lrs_intersection`
   (each class's settings as JSON) and `grapy_lrs_edit_log`, registered in `gpkg_extensions` as
   `grapy_linear_referencing`. NetworkID takes `dLRSNetworks`, whose codes the networks get as they are made.
2. **Measures** (`grapy/_lrs_measure.py`). A route is its parts (a part ends at a physical gap) of x, y, z, m. A
   calibration point is placed on the route at the first candidate location after the previous point's, in measure
   order (so a loop's start and end are told apart); a vertex is added there; measures are interpolated between points
   and extrapolated before the first and after the last by the nearest pair's ratio. A location's measure is the m on
   the nearest segment within the XY tolerance; an event's shape is the route between its measures.
3. **Built to their pages (33):** AppendEvents, AppendRoutes, CalculateRouteConcurrencies,
   ConfigureAddressFeatureClasses, ConfigureLookupTable, ConfigureRouteDominanceRules,
   ConfigureUtilityNetworkFeatureClasses, CreateLRS, CreateLRSEvent, CreateLRSEventFromExistingDataset,
   CreateLRSFromExistingDataset, CreateLRSIntersection, CreateLRSIntersectionFromExistingDataset, CreateLRSNetwork,
   CreateLRSNetworkFromExistingDataset, DeleteRoutes, DisableDerivedMeasureFields, DisableReferentFields,
   DisableStationingFields, EnableDerivedMeasureFields, EnableReferentFields, EnableStationingFields, GenerateEvents,
   GenerateIntersections, ModifyEventBehaviorRules, ModifyLRS, ModifyLRSEvent, ModifyLRSIntersection, ModifyLRSNetwork,
   ModifyNetworkCalibrationRules, ModifyRouteIdPadding, RemoveLRSEntity, UpdateMeasuresFromLRS.
   ConfigureUtilityNetworkFeatureClasses is built because grapy holds utility networks (ADR-231): a class is a
   utility network's Line, Device or Junction class by its role there, the line class must be the LRS centerline as the
   page says it serves, and with it configured GenerateEvents' BYPASS_EVENTS_WITH_NULL_LRS_FIELDS (which the page makes
   available only in a combined LRS and utility network deployment), OverlayEvents' utility network inputs, AppendRoutes'
   forced CONSIDER and RemoveLRSEntity's UN_FEATURE_CLASS all work. GenerateIntersections' CURRENT_USER and start date
   read the network's editor tracking (ADR-240).
4. **Built as equivalents (9)** (ADR-224 §5.6; each says so once when it runs): GenerateRoutes and
   GenerateCalibrationPoints (Calibrate Routes' DISTANCE method; *Create a new route*'s placement; the gap page's
   rules), ApplyEventBehaviors (relocation of the event's old location on the new route by Locate Features Along
   Routes, per rule), DeriveEventMeasures, TranslateEventMeasures and CalculateIntersectingRouteMeasures (Locate
   Features Along Routes), OverlayEvents (Overlay Route Events' UNION across the layers, split at dates),
   RemoveOverlappingCenterlines (a planar overlay — GEOS noding union — standing in for the Intersect and Simplify the
   page names), ReverseLineOrders (the line's order values given back in reverse sequence).
5. **Refused by parameter, never ignored** (ADR-224 §5.4): ModifyLRS `conflict_prevention="ENABLE"` (branch versioned
   data only). A feature service URL as any input (no network, ADR-223 §5.3). `bypass_conflict_prevention` is accepted
   with both keywords because conflict prevention can never be enabled, so there is no lock to bypass;
   `generate_shapes` and `record_calibration_changes` are accepted with a warning because their own pages say they are
   no longer supported.
6. **Refused by name, each for its own reason (7)** (`locref._REFUSED`): ConfigureExternalEventBehaviorsWithLRS
   (registers an event for the ArcGIS Server LRS service's Relocate Event REST operation, and requires the LRS in an
   enterprise geodatabase); ConfigureExternalEventWithLRS (requires both the event store and the LRS to be enterprise
   geodatabases); ConfigureUtilityNetworkFeatureClass (deprecated by its own page in favour of the plural tool, and
   requires a branch versioned enterprise geodatabase connection); GenerateLRFeatureCount, GenerateLRLengthSummary and
   GenerateLRRouteLog (each writes a report whose rows, columns and order no public page gives); GenerateLrsDataProduct
   (reads the Data Product Designer's template `.json`, whose schema is not published, and writes such a report).

### 5.9 INFERRED, for the owner's confirmation ([Q-222](../open-questions.md)) — **Confirmed 2026-10-31 by owner decision in Q-222** (*"Hepsini onayla"*)

- (a) **Storage**: the five `grapy_lrs*` tables and the `grapy_linear_referencing` extension; a network ID unique in the
  geodatabase, since `dLRSNetworks` is one domain per geodatabase; `dActivityType` made with no codes, none being
  published.
- (b) **Units and tolerances**: an LRS made without them has an XY and z tolerance of 0.001 and resolution 0.0001; the
  m tolerance is the XY tolerance in measure units; MILES and FEET are US survey units, INCHES and YARDS follow FEET,
  NAUTICAL_MILES is 1852 m; a network registered from an existing class is measured in its reference's linear unit
  (the page derives the unit from the XY:M tolerance ratio, and a GeoPackage keeps no m tolerance).
- (c) **Schema**: given a GeoPackage, CreateLRS makes a feature dataset named after the LRS; networks, events and
  intersections are made in the LRS's feature dataset with z and m; event measures are doubles; a GUID is a text
  field; registering existing calibration points tolerates m (CreateLRSFromExistingDataset's page allows it, the data
  model page does not); EnableDerivedMeasureFields' fields not named are the page's example names; DisableStationingFields
  keeps the fields as the other Disable tools say of theirs; an intersection's z tolerance defaults to the LRS's.
- (d) **Calibration**: points at each part's ends and at a loop's thirds; parts turned so their points increase and
  ordered by their smallest measure; STEPPING_INCREMENT steps to the next multiple of the offset above the measure
  before the gap, ADDING_INCREMENT adds the offset, ADDING_EUCLIDEAN_DISTANCE the gap's length; ATTRIBUTE_FIELDS scales
  the lengths so the route ends at its to measure, the gap steps unscaled; one calibration point extrapolates one to
  one; an earlier point of the same route whose time overlaps a new one is retired on the new one's from date, or
  deleted if it began then or later.
- (e) **Time**: a slice is half-open; an event lies on the route record that holds its from date; a route record's
  shape is built from the sequence rows and calibration points that overlap it and are still active at its end; the
  date of a logged edit is the latest from date among those rows when it is after the record's start, else the record's
  from date, else today.
- (f) **Outputs the pages do not lay out**: LocError words No Error, Route Not Found, No Match, Partial Match (and
  Multi-Match in TranslateEventMeasures, for more than one route found under ANY); OverlayEvents' layer columns
  `<layer>_<field>`, the first event by object ID where several of a layer overlap, only segments some event covers,
  a Point row per point event; TranslateEventMeasures writes the source's fields with the target's route and measures
  and `Loc_Error`; UpdateMeasuresFromLRS takes the measure at the nearest location and leaves a feature that finds no
  route unchanged; CalculateIntersectingRouteMeasures' six columns, and a blank date meaning the current routes;
  GenerateIntersections deletes and remakes a processed route's points, one per route record, with its dates, and with
  no editor tracking processes every route under CURRENT_USER and says so.
- (g) **Route IDs**: a network CreateLRSNetwork makes takes route IDs from the data; AUTOGENERATED gives a GUID; a
  multiple field ID's padding puts the odd character of LEFT_AND_RIGHT on the right, and the route ID's length is 255;
  lookup tables are enforced by AppendRoutes; ROUTE_NAME takes the ID of the route of that name; AppendRoutes makes no
  calibration points and skips (logs) a route whose ID and time are already loaded.
- (h) **Concurrency**: a section is one shared centerline; the output's RouteId, FromMeasure, ToMeasure, FromDate and
  ToDate names; dominance rules apply in their configured order, a later rule breaking an earlier one's tie; a tie left
  is broken by the smallest route ID with error 1 (Pro picks at random); error 3 means more than one event value on the
  section.
- (i) **The edit GenerateRoutes logs**: none when nothing changed; calibrate when the shape is the same; reverse when it
  runs the other way; extend when the new shape holds the old; cartographic realignment when the shape moved but the
  measure range did not; realign otherwise.
- (j) **Derived and line networks**: a derived route is the line's current routes in LineOrder, measured from 0 by
  length, its ID the LineID and its name the LineName; ReverseLineOrders changes the records in place; the pieces of
  RemoveOverlappingCenterlines take z back from the centerline they came from, and each centerline keeps its ID on its
  remainder.
- (k) **Details files**: a text file in the scratch folder, one line per issue or change (UpdateMeasuresFromLRS's a
  CSV of the values before and after).
- (l) **Behaviours**: Stay Put relocates the event's old location onto the route (split where only part of it remains,
  retired where none does); Retire retires exactly the events Stay Put would change; Cover stretches the event touching
  a new section over it; Snap moves what is off the route onto another active route there; Honor Route Measure keeps
  measures (grapy's routes take their measures from calibration points, which a cartographic realignment does not
  move); Honor Referent Location reads the referent location as an IntersectionId and the offset in the referent's
  units; a split's first piece keeps the EventID; a record that began on or after the edit's date is replaced in place;
  reassign is never logged, since no arcpy tool reassigns a route.
- (m) **Utility network classes**: a class is a utility network's Line, Device or Junction class by its role in
  `grapy_un_source`; devices and junctions overlay as point events and lines as line events.

## 6. Consequences

- **State.** Up to five grapy tables in the GeoPackage the tool works on; the user classes an LRS has in ArcGIS; nothing
  in the catalogue or on the server.
- `arcpy.locref` moves from 0 of 49 built (the module refused whole) to **42 built (9 of them equivalents) and 7
  refused**, none unexamined; no `arcpy.locref` name is refused with a schedule.
- **Negative.** An LRS written here is not one ArcGIS Pro opens as an LRS: its registration is grapy's. Event behaviours
  are grapy's reading of the examples.
- **Ports created.** None: numpy and shapely, already grapy's.

## 7. Assumptions this decision rests on

- The data model pages' user classes and fields are what a toolbox reads and writes; the internal tables are not.
- A route edit that matters to events reaches grapy through GenerateRoutes or AppendRoutes, since Pro's interactive
  route editing has no arcpy surface.

## 8. Dependencies

[ADR-223](ADR-223-user-tools-run-sandboxed-on-grapy.md) §5.3 (no network or server),
[ADR-224](ADR-224-grapy-aims-at-arcpys-whole-buildable-surface.md) (§5.4, §5.6, §5.7),
[ADR-231](ADR-231-grapy-holds-utility-networks.md) (utility network classes),
[ADR-240](ADR-240-grapy-holds-editing-behaviours-and-registration.md) (editor tracking, feature datasets' registration).
Clean room (CLAUDE.md §5): built from the public pages in §4; no Esri implementation read, no install folder opened.

## 9. Revisit triggers

- Esri publishes the layout of the LR data products, the Data Product Designer template, or the `Lrs_Metadata` and
  `Lrs_Edit_Log` tables.
- Pro 3.8 moves these tools into the Linear Referencing toolbox (the toolbox overview says so): `arcpy.lr` and
  `arcpy.locref` then share names.
- A toolbox depends on an INFERRED choice of §5.9 answering differently from Pro.

## 10. Dissent

None recorded.

## 11. Conditions

1. **The owner answers Q-222** (§5.9 (a)–(m)). **DISCHARGED 2026-10-31** — confirmed as built by owner decision in Q-222 (*"Hepsini onayla"*).
2. **Apply Event Behaviors compared with ArcGIS Pro** on the Roads and Highways pages' worked examples (calibrate,
   extend, realign, retire, reverse), on a file geodatabase LRS: which outcomes agree, and each difference either
   changed to Pro's answer or recorded. Needs a Pro session with a Location Referencing licence. *Not runnable here (checked 2026-10-31):* this machine's Pro 3.7 is ArcInfo without Location Referencing (`arcpy.CheckExtension("LocationReferencing")` answers `NotLicensed`), which conditions 2–4 all need. The comparisons need a session with that licence — the owner's to provide.
3. **GenerateRoutes and GenerateCalibrationPoints compared with ArcGIS Pro** on a gapped route under each calibration
   rule, and on a loop: §5.9 (d)'s reading of the stepping increment is the one most likely to differ.
4. **CalculateRouteConcurrencies and OverlayEvents' output columns compared with ArcGIS Pro**: §5.9 (f) and (h) name
   fields the pages do not.
