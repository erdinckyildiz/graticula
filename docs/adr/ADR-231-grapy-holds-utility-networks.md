# ADR-231 — grapy holds utility networks

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` for the model and the configuration tools, whose pages give their parameters, keywords, CSV columns and constraints · `HIGH` for the connected, subnetwork and subnetwork controllers traces on the hand-worked network of the tests · `LOW` for agreement with Pro wherever the pages describe a trace's purpose but not its rule — upstream and downstream flow, the isolating set, how conditions combine — which grapy decides as §5.9 records |
| **Decided** | 2026-10-30, by owner decision on scope and order: the utility network is the last of the four group-2 models after subtypes and attachments (ADR-228), LAS datasets and TINs (ADR-229) and mosaic datasets (ADR-230); it is large, so this ADR decides the model and builds its core, and names what later batches add ([ADR-224](ADR-224-grapy-aims-at-arcpys-whole-buildable-surface.md) §5.7). The storage and the rules the pages leave open are INFERRED and listed for confirmation in §5.9 and [Q-199](../open-questions.md). |
| **Supersedes** | — |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

Before this decision `arcpy.un` was refused as a whole: *"the utility network needs a data model this server does not
hold yet (ADR-224 group 2)"*, and the module was not on the counter at all. A toolbox that builds a water network,
loads its rules and associations and traces a leak could not run on this server.

`tools/arcpy-surface.json` now lists `arcpy.un`: 124 names, read with `dir()` from the arcpy of the owner's Pro 3.7
install on 2026-10-30 in the same way as the other modules (names only). About sixty of them are network diagram
tools, twelve are the telecom domain network's, and the rest are the utility network itself.

What ArcGIS publishes, and what it does not:

- **The classes and their system fields are documented.** "Structure network" names StructureJunction, StructureLine,
  StructureBoundary, StructureJunctionObject and StructureEdgeObject and their fields; "Traditional domain networks"
  names a domain's seven classes, `<Domain>Device` to `<Domain>EdgeObject`, and their fields (ASSETGROUP, ASSETTYPE,
  ASSOCIATIONSTATUS, ISSUBNETWORKCONTROLLER, FROMDEVICETERMINAL …); "Utility network feature classification" says
  ASSETGROUP is the subtype field and an asset type is the coded domain on ASSETTYPE for that subtype; the
  Associations table page gives its fields and the association type codes 1 to 6; the Association status page its bit
  domain; the Dirty areas page its status bits.
- **Each tool's page gives its parameters, keywords, defaults, constraints and CSV columns**: CreateUtilityNetwork (a
  z- and m-enabled service territory), AddDomainNetwork, AddTier and AddTierGroup, AddNetworkAttribute (in line only
  for SHORT and LONG, 25 bits, sequential codes from 0, apportionable only DOUBLE …), SetNetworkAttribute, the terminal
  configuration tools (two to eight terminals, valid paths for three or four), the category tools, AddRule and
  DeleteRule, the four CSV import and export tools for rules and associations, the two for subnetwork controllers, and
  Enable, Disable and Validate Network Topology. `arcpy.un.UtilityNetwork` documents its association and controller
  methods.
- **The trace types are described in words.** "Utility network trace types", "Connectivity and traversability",
  "Starting points" and the Trace page say what each trace returns, that a connected trace ignores terminals, that a
  directional device lets a subnetwork controllers trace through only one way (Scenario 1), that two subnetworks met
  by one trace are an error, that a loop is traced whole, that an isolation trace needs a filter barrier and returns
  the minimum set of features to close. They do not say how flow direction is computed, how the isolating set is
  chosen, or how several conditions combine (that page is not in the tool's reference).
- **The storage is not documented**: the network index is "a graph persisted as binary pages", and the system tables'
  layout, the asset package and the subnetwork table internals are Esri's own.

Every page cited here was read on doc.esri.com (pro.arcgis.com redirects there) on 2026-10-30; no Esri implementation
was read (CLAUDE.md §5). `UpdateUtilityNetworkSchema`'s page answered 404.

## 2. Alternatives considered

### Alternative A — the network's classes as ordinary grapy classes, its definition and topology in grapy's tables (chosen)

The classes are feature classes and tables in the feature dataset with ArcGIS's names and system fields; asset groups
are ADR-228's subtypes, asset types its per-subtype domains, global IDs its `GlobalID`. Associations and dirty areas are
classes a cursor reads. The definition — domain networks, tiers, network attributes, categories, terminal
configurations, rules, controllers — is grapy's own tables. The topology is a record of the features as last enabled or
validated, from which grapy builds its graph for each trace. **For:** a toolbox reads and writes the features with the
cursors it already uses; the model is the one the pages describe; nothing undocumented is reproduced. **Against:** a
network made by grapy cannot be opened by Pro, nor one made in Pro by grapy.

### Alternative B — reuse the network dataset of ADR-226

**For:** its graph and Dijkstra exist. **Against:** a network dataset has no terminals, asset types, rules,
associations, tiers or subnetwork controllers; every trace type but shortest path needs them. Its edge-building is
reused in spirit (coincident vertices) but not its tables.

### Alternative C — read and write Esri's utility network system tables or asset packages

**For:** interchange with Pro. **Against:** undocumented; the clean room forbids reproducing them (CLAUDE.md §5).
Rejected; MigrateToUtilityNetwork is refused with that reason.

### Alternative D — refuse the utility network

Rejected by the owner's scope answer.

## 3. Counterarguments to the preferred option

**Several rules are grapy's, not Pro's.** Flow direction, the isolating set and condition precedence are decided in
§5.9 (f) because the pages describe the result, not the rule. On a radial network with directional devices every
reading of the pages gives the same answer, and the tests use such a network; in a mesh of several controllers, or with
barriers between a starting point and its controller, Pro's answer may differ.

**Dirty areas are found, not tracked.** grapy has no edit session to see an edit as it is made, so a utility network
tool compares the features with the topology's record and writes the dirty areas then. An edit is visible in
`<network>_DirtyAreas` after the next utility network tool runs, not at once.

**The topology is rebuilt for each trace.** grapy keeps no persisted index: a trace rebuilds the graph from the
record. That is linear in the network's size; a network too large for a run fails by ADR-223's limits. Nothing has
been measured.

## 4. Evidence

`src/grapy/tests/test_utility_network.py`, twenty-two tests on a water network drawn in its docstring: a source whose
Load terminal is the controller of Zone1, two valves of category Isolating, a check valve with Source and Load
terminals, two tees and a loop of three lines that meet through system junctions. The expected answers are worked by
hand there: downstream of L2 is T, L3, L5, the check valve, L6, V2 and the loop; upstream of L5 is T, L2, V1, L1 and S
but never L3; upstream of the loop is the whole loop and the main, and only the starting line when indeterminate flow
is ignored; the leak on L3 is isolated by V1 alone, because V2 is fed only through the leak's own area, and with
isolated features every feature beyond V1; a second source behind the check valve is not found from upstream of it
(Scenario 1 of the page) and makes a subnetwork trace from below it the documented two-subnetworks error. No
performance claim is made.

## 5. Decision

1. **Scope of this batch.** Built: the network (CreateUtilityNetwork), traditional domain networks, tier groups and
   tiers, network attributes and their assignments, network categories, terminal configurations, rules, associations
   (by CSV and by `UtilityNetwork`'s methods), subnetwork controllers (by CSV and by its methods), the network topology
   (Enable, Disable, Validate, dirty areas and errors), and Trace's CONNECTED, SUBNETWORK, SUBNETWORK_CONTROLLERS,
   UPSTREAM, DOWNSTREAM and ISOLATION types with the SELECTION and AGGREGATED_GEOMETRY results.
2. **Storage, INFERRED (§5.9 (a)).** The classes as §2 A says, in the GeoPackage the tool works on, a utility network
   named `<file>.gpkg/<feature dataset>/<network>`. grapy's sixteen tables `grapy_un*`, registered as the
   `grapy_utility_network` extension in `gpkg_extensions`, hold the definition, the topology's record
   (`grapy_un_topology_feature`, `grapy_un_topology_association`) and its errors. `<network>_Associations` has the
   associations table's documented fields; `<network>_DirtyAreas` is a polygon class with STATUS, ERRORCODE,
   ERRORMESSAGE, NETWORKSOURCEID and FEATUREGLOBALID.
3. **Configuration follows the pages.** Each tool enforces what its page states: the topology disabled for
   configuration; a tier's name unique in the network; tier groups only in a hierarchical domain; the in-line 25-bit
   budget and its sequential domain; a field's type matching its network attribute's; two to eight terminals, an
   upstream terminal in a directional configuration, valid paths for a bidirectional one of three or four; a terminal
   configuration permanent once the topology has been enabled; Subnetwork Controller only on devices and junction
   objects, and in a partitioned domain only on a directional asset type; rules respecting the feature restrictions;
   associations supported by a rule; a source controller on a downstream terminal and a sink on an upstream one in a
   partitioned domain; controller names unique in a tier; rules present before the topology is enabled.
4. **The network topology is a record.** Enable and Validate copy each feature's shape, asset group and type, terminal
   fields and network attribute fields, and each association, into the record; a trace reads only the record, as the
   pages say a trace reads the topology as last enabled or validated. Validate with an extent takes the features whose
   dirty area meets it. The graph built from the record has a junction element per terminal of a device or junction
   object (one per point otherwise), system junctions where lines meet with no point, edge elements between the
   connection points along a line or edge object, and a device's internal paths per its terminal configuration. Errors
   the pages list are found: 1 (empty geometry), 2 (Unknown asset group), 5 (line within tolerance), 8 (no connectivity
   rule), 9 (more than one junction-edge rule applies), 10 (lines of different asset types meeting), 17, 18, 34 and 35
   (associations with no containment or attachment rule). A feature or connection in error is left out of the graph.
   Esri's persisted index is not reproduced; Verify and Repair Network Topology are refused for that reason.
5. **Network attributes a condition reads**: the user's, through their field assignments, and the system attributes
   Asset group, Asset type, Is subnetwork controller, Shape length (an edge element's length), Terminal ID and Tier
   name. Flow direction, Position from/to, Source ID and the telecom attributes are not computed.
6. **Traces.** As `_untrace`'s docstring states and §5.9 (f) records: CONNECTED ignores terminals; the subnetwork
   types find their controllers against the flow and span from them, stopping at other tiers' controllers in a
   partitioned domain and passing them in a hierarchical one; meeting a controller of the tier with another subnetwork
   name is the documented error; UPSTREAM and DOWNSTREAM are an *equivalent* (ADR-224 §5.6) — flow passes from u to v
   when some path from a controller reaches u without passing v, read off the dominator tree (Cooper, Harvey and
   Kennedy 2001), so a loop is indeterminate and traced whole unless indeterminate flow is ignored; ISOLATION returns
   the filter-barrier features the resource reaches the area through, and with isolated features what loses its supply
   when they close. Starting points and barriers are a class or table with FEATUREGLOBALID, TERMINALID and
   PERCENTALONG, read as the Starting points page says. Condition and filter barriers, both scopes, output asset types
   and conditions, containers, content, structures, include barriers, ignore barriers at starting points and validate
   consistency are honoured. As the page says, a subnetwork-based trace run from Python applies no tier's trace
   configuration.
7. **Names.** Built (25): `CreateUtilityNetwork`, `AddDomainNetwork`, `AddTierGroup`, `AddTier`,
   `AddNetworkAttribute`, `SetNetworkAttribute`, `AddNetworkCategory`, `SetNetworkCategory`, `DeleteNetworkCategory`,
   `AddTerminalConfiguration`, `SetTerminalConfiguration`, `DeleteTerminalConfiguration`, `AddRule`, `DeleteRule`,
   `ImportRules`, `ExportRules`, `ImportAssociations`, `ExportAssociations`, `ImportSubnetworkControllers`,
   `ExportSubnetworkControllers`, `EnableNetworkTopology`, `DisableNetworkTopology`, `ValidateNetworkTopology`,
   `Trace`, and the class `UtilityNetwork`.
8. **Later batches — a schedule, not a refusal reason** (condition 4). Refused today as *in a later batch of
   ADR-231*: subnetworks (UpdateSubnetwork, ExportSubnetwork, SetSubnetworkDefinition, the SubnetLine rows and the
   discovered subnetworks Validate returns), trace configurations (Add, Delete, Import, Export), AddTraceLocations,
   UpdateIsConnected, SetEdgeConnectivity, SetAssociationRole, AnalyzeNetworkData, RebuildNetworkTopology,
   ApplyErrorResolutions, the cluster key tools; Trace's LOOPS, SHORTEST_PATH and PATH types, target tiers, functions,
   function barriers, propagators, nearest filtering, bitset filtering, digitized direction, locatability, synthesized
   geometry, and the JSON result types; and the telecom domain network with its circuits, colour and wavelength
   schemes (twelve names and the CIRCUIT trace). Refused for good, each with its reason: the sixty-seven network
   diagram tools (network diagrams are a model of their own, as `arcpy.nd` already says), MigrateToUtilityNetwork (an
   asset package is Esri's layout), Verify and Repair Network Topology (Esri's persisted index), and
   UpdateUtilityNetworkSchema (its page answered 404).
9. **INFERRED, for confirmation** ([Q-199](../open-questions.md)):
   - (a) the storage of §5.2: the classes in the feature dataset under ArcGIS's names; `<network>_Associations` and
     `<network>_DirtyAreas` beside them; grapy's sixteen `grapy_un*` tables; COMPATIBLE and CURRENT make version 8;
   - (b) terminal IDs 1, 2 … in the order a configuration names its terminals; an asset type with no configuration has
     one junction and no terminal ID, written `Single Terminal` in CSV files;
   - (c) coincidence within 0.001 of the data's unit (ArcGIS's default x,y tolerance; a GeoPackage keeps none), at any
     vertex of a line, and only within one network — a domain network's own classes, or the structure network's, as
     the feature restrictions page pairs them; a structure over a device is no connection and no error; a line end on
     a device with terminals takes the terminal its FROMDEVICETERMINAL or TODEVICETERMINAL names, else the only one a
     rule allows, and the field is not written back;
   - (d) the topology as a record; dirty areas found by comparing the features with it when a utility network tool
     runs (Trace, Enable, Disable, Validate); a feature validated whole when its dirty area meets the extent, dirty areas
     not clipped; past `max_number_of_errors` the first so many are written and the topology stays disabled; Validate's
     discovered subnetworks `{"discoveredSubnetworks": []}` until subnetworks are kept;
   - (e) subnetwork controllers take effect in traces at once, without a validate; `UtilityNetwork`'s methods save when
     they return;
   - (f) trace rules: a directional configuration's path carries the resource only from an upstream terminal to a
     downstream one, and a device's current path is its TERMINALCONFIGURATION value or the configuration's default;
     controllers are found against the flow and the search stops at the first controller of the tier; upstream and
     downstream find their controllers and the flow without the trace's barriers and stop at the barriers when they
     trace; the flow rule of §5.6; the isolating set and isolated features of §5.6; in a condition AND binds before OR;
     a feature passes an output filter when one of its traced elements does; filter barriers stop the non-isolation
     traces as condition barriers do; a line row without PERCENTALONG places nothing;
   - (g) a selection is made on a layer named after each class (`WaterDevice`), made when there is none; aggregated
     geometry goes to `Trace_Results_Aggregated_Points`, `_Lines` and `_Polygons` in the network's GeoPackage when no
     class is named, as the project's default geodatabase has no counterpart;
   - (h) DeleteRule's `ALL` with no description deletes every rule; ImportRules skips a rule the network has; an
     association's ASSOCIATIONSTATUS is recomputed for every feature when associations change;
   - (i) with more than four terminals valid paths are refused, all paths being valid.

## 6. Consequences

- **State.** In the GeoPackage the tool works on. Nothing in the catalogue or the server.
- **No new dependency**: shapely and the standard library.
- **Names.** `tools/grapy-refusals.json` before → after: `arcpy.un` 0 of 124 on the counter → built 25 (equivalent 1),
  refused 99, unexamined 0; all modules built 918 → 943, refused 1132 → 1231, of 2050 → 2174, equivalents 81 → 82.
- **Negative.** A network made by grapy is grapy's: Pro cannot open it, and a hosted layer imported from it carries the
  features, not the network (ADR-223 §5.5).

## 7. Assumptions this decision rests on

- A user tool's network fits in its run's memory: the graph is rebuilt in memory for each trace. Nothing has been
  measured.
- On radial networks with directional devices the pages' descriptions determine the traces' answers; §5.9 (f) matters
  only where they do not.

## 8. Dependencies

[ADR-224](ADR-224-grapy-aims-at-arcpys-whole-buildable-surface.md) §5.5–5.7,
[ADR-223](ADR-223-user-tools-run-sandboxed-on-grapy.md) (the fence and limits),
[ADR-228](ADR-228-grapy-holds-subtypes-and-attachments.md) (asset groups as subtypes, asset types as per-subtype
domains, global IDs), [ADR-225](ADR-225-grapy-holds-topology-and-relationship-classes.md) (the `gpkg_extensions`
registration it reuses), [ADR-226](ADR-226-grapy-holds-network-datasets.md) (the network dataset it does not reuse);
shapely; clean room (CLAUDE.md §5): only documented behaviour is reproduced, and no Esri file format or index.

## 9. Revisit triggers

- A toolbox depends on a later-batch name — the batch is then the next step.
- The owner changes an inference of §5.9, or Pro is found to answer a trace of §5.9 (f) differently on a network where
  the pages leave it open.
- A network too large for a run's memory: a persisted graph becomes the step.

## 10. Dissent

None recorded.

## 11. Conditions

1. **The model and its configuration**: CreateUtilityNetwork, AddDomainNetwork, AddTierGroup, AddTier, the network
   attribute, category, terminal configuration and rule tools, with their documented constraints and CSV round trips.
   **DISCHARGED 2026-10-30**: `test_utility_network.py` checks the classes and system fields, the z- and m-enabled
   territory, unique and grouped tiers, the in-line budget and its sequential domain, terminal counts and valid paths,
   terminal configurations made permanent by enabling, rules exported, deleted by ID and imported, and a terminal
   required on an asset type with terminals.
2. **Associations, controllers and the topology**: ImportAssociations, ExportAssociations, `UtilityNetwork`'s methods,
   the controller import and export, Enable, Disable and Validate with dirty areas and errors. **DISCHARGED
   2026-10-30**: an association without a rule refused, ASSOCIATIONSTATUS 4 and 1, a source controller on its upstream
   terminal refused, errors 2 and 8 on a plug with no rule and 9 on a check valve whose terminal is not named, a
   disabled topology's single 110 × 110 dirty area, and a trace that keeps reading a closed valve as open until
   Validate takes its edit — an extent away from the edit leaving it dirty.
3. **The six trace types against hand-worked answers.** **DISCHARGED 2026-10-30**: connected with feature, condition
   and category barriers and both scopes; subnetwork from a starting point and by name; subnetwork controllers through
   a check valve (Scenario 1); upstream and downstream through the loop with and without indeterminate flow; isolation
   by V1 alone and with isolated features; two subnetworks an error; output filters, containers and aggregated
   geometry of 20 m and one point.
4. **Every name of `arcpy.un` is built or refused with its own reason.** **PARTLY DISCHARGED 2026-10-30**: none answers
   the generic message, and the diagram tools, MigrateToUtilityNetwork, Verify and Repair Network Topology and
   UpdateUtilityNetworkSchema give their reasons. Twenty-eight are refused only as *in a later batch of ADR-231*, which
   is a schedule, not a reason: the sixteen of §5.8's first list and the twelve telecom names; Trace's later trace
   types, parameters and result types likewise. Each is to be built or given its own reason.
5. **Upstream, downstream and isolation are compared with Pro** on a mesh network with two controllers and on a
   network with barriers between a starting point and its controller, using the owner's Pro install (the harness of
   the Pro arcpy probes) — or the owner accepts §5.9 (f) as grapy's reading.
6. **The owner confirms or changes §5.9** ([Q-199](../open-questions.md)).
