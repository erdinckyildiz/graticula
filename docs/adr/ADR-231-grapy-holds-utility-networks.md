# ADR-231 — grapy holds utility networks

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` for the model and the configuration tools, whose pages give their parameters, keywords, CSV columns and constraints · `HIGH` for the connected, subnetwork and subnetwork controllers traces on the hand-worked network of the tests · ~~`LOW` for agreement with Pro wherever the pages describe a trace's purpose but not its rule — upstream and downstream flow, the isolating set, how conditions combine — which grapy decides as §5.9 records~~ `HIGH` for upstream, downstream, isolation and condition precedence on the four networks measured in ArcGIS Pro 3.7 on 2026-10-30 (§4, condition 5): 110 of 126 traces answer as Pro's do, and the 16 others are ignored indeterminate flow in one mesh, a stated difference (§5.9 (f)) · second batch: `HIGH` for loops, shortest path, path, target tier, digitized direction and the subnetwork tools on the hand-worked network, `LOW` for agreement with Pro on what the pages leave open — function barrier order, propagation through a mesh, the JSON files' layout, the Subnetworks table — §5.9 (j) to (n) |
| **Decided** | 2026-10-30, by owner decision on scope and order: the utility network is the last of the four group-2 models after subtypes and attachments (ADR-228), LAS datasets and TINs (ADR-229) and mosaic datasets (ADR-230); it is large, so this ADR decides the model and builds its core, and names what later batches add ([ADR-224](ADR-224-grapy-aims-at-arcpys-whole-buildable-surface.md) §5.7). The storage and the rules the pages leave open are INFERRED and listed for confirmation in §5.9 and [Q-199](../open-questions.md). **Second batch 2026-10-30, by the owner's "devam et"**: every name §5.8 scheduled is built, built as an equivalent, or refused for its own reason (§5.7, §5.8; condition 4). |
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

**For the second batch**, read the same day: the tool pages of Update Subnetwork, Export Subnetwork, Set Subnetwork
Definition, Add, Delete, Export and Import Trace Configuration(s), Add Trace Locations, Update Is Connected, Set Edge
Connectivity, Set Association Role, Analyze Network Data, Rebuild Network Topology, Apply Error Resolutions, Enable and
Calculate Cluster Keys, Add Telecom Domain Network and Import Circuit Definitions; the Trace page again for LOOPS,
SHORTEST_PATH, PATH, the target tier, functions, function and filter function barriers, propagators, nearest and bitset
filtering, digitized direction, locatability and the result types; and the help pages "Utility network trace types",
"Configure a trace", "Barriers", "Attribute propagation", "Attribute substitution" and "Is connected attribute". What
they give and do not:

- **The trace types are described by their result**: a loop is where flow is ambiguous, all loop features returned; a
  shortest path runs on connectivity and "respects terminal paths but not terminal directionality"; a path trace goes
  from starting points to stopping points or to unterminated edges, `num_paths` paths (default 1) of at most `max_hops`
  (default 100), an edge and a junction-junction association one hop, a device with terminals two. How loops are
  found, which paths are chosen when more exist, and how a k-nearest filter measures nearness are not said.
- **Function barriers** pool a running total over every branch, or with local values keep one per branch and allow one
  starting point; **propagators** carry a controller's value with the flow by bitwise AND, MIN or MAX, a Propagator
  Resetter starting again from its own value, and a failing condition makes a barrier; **attribute substitution**
  gives its encoding exactly — for three phases nine bits, `From A: to A = 256 … From C: to C = 1`. The order a
  global total visits branches in, and the value an element reached twice keeps, are not said.
- **The JSON files' layout is not on the tool pages.** The ArcGIS REST API's public "Trace (Utility Network Server)"
  page documents the trace response — `traceResults` with `elements`, `connectivity`, `featureElements`,
  `associations`, `relatedRecords`, `globalFunctionResults` … — and the traceConfiguration object; the Export Trace
  Configurations page gives the exported file's seven keys.
- **Subnetworks.** The pages describe a "Subnetworks table" with Is dirty, Is deleted and the last update and
  acknowledged export, and a SubnetLine row per subnetwork, but not that table's layout; Is dirty has three states and
  Is connected three values, named but not coded.
- **What is not there.** Add Telecom Domain Network names its parameters but not the classes and tables a telecom
  domain network has; Analyze Network Data writes, and Apply Error Resolutions reads, an error resolution database
  whose tables the pages do not give beyond an `ErrorAction` field; Calculate Cluster Keys says what keys are for, not
  how they are computed; the Trace page says synthesized geometry is created but not how it is drawn.

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
barriers between a starting point and its controller, Pro's answer may differ. **It did, and was measured** (§4,
2026-10-30): barriers took part in Pro's flow and not in grapy's, terminals were numbered from 2, and isolation answered
otherwise around a controller and without barriers. grapy now answers as Pro was measured answering, except where Pro
ignores indeterminate flow in a mesh, which no rule over the network's shape reproduces (§5.9 (f)).

**Dirty areas are found, not tracked.** grapy has no edit session to see an edit as it is made, so a utility network
tool compares the features with the topology's record and writes the dirty areas then. An edit is visible in
`<network>_DirtyAreas` after the next utility network tool runs, not at once.

**The topology is rebuilt for each trace.** grapy keeps no persisted index: a trace rebuilds the graph from the
record. That is linear in the network's size; a network too large for a run fails by ADR-223's limits. Nothing has
been measured.

## 4. Evidence

`src/grapy/tests/test_utility_network.py`, twenty-two tests on a water network drawn in its docstring (forty-four
with the second batch, below, and forty-eight with the Pro measurement's, the last two on its mesh): a source whose
Load terminal is the controller of Zone1, two valves of category Isolating, a check valve with Source and Load
terminals, two tees and a loop of three lines that meet through system junctions. The expected answers are worked by
hand there: downstream of L2 is T, L3, L5, the check valve, L6, V2 and the loop; upstream of L5 is T, L2, V1, L1 and S
but never L3; upstream of the loop is the whole loop and the main, and ~~only the starting line~~ nothing when indeterminate flow
is ignored (measured in Pro 3.7, below); the leak on L3 is isolated by V1 alone, because V2 is fed only through the leak's own area, and with
isolated features every feature beyond V1; a second source behind the check valve is not found from upstream of it
(Scenario 1 of the page) and makes a subnetwork trace from below it the documented two-subnetworks error. No
performance claim is made.

The second batch's twenty-two tests work on the same network. Worked by hand: the loops trace returns V2, L4, T2, L7,
L8 and L9 and nothing hanging off them, and nothing once L8 is a barrier; the shortest path from S to T2 by Shape length
runs along the main (40) and not round the loop (60); a path from S with no stopping point ends at L6's open end through
the check valve's two terminals, and from L3 to T2 takes L4 (one hop) or, with two paths, the loop too (three hops), but
not with `max_hops` 2; a local function barrier of 15 m from L2 stops at L3 and L5 (20 m each); ADD and COUNT of Shape
length downstream of L5 are 20 and 2; a MAX propagator under 1 with V2's status 1 cuts V2 and everything beyond it from
the subnetwork; substitution 140 maps phases A, B, C to B, C, A; a bitset of 7, 3 and then 4 stops at the check valve;
the nearest Isolating valve to L3 is V2 and the two nearest V2 and V1; with a District tier controlled by the check
valve, downstream of L2 stops at it unless District is the target, and upstream of L6 is the check valve alone in
District and reaches S with Pressure as the target; downstream of L4 by digitized direction runs round the loop and
back to V2; UpdateSubnetwork writes Zone1 and Is connected True on every line and a 90 m SubnetLine, is refused while an
edit at L6's end is unvalidated, is discovered dirty after it and makes 100 m, exports Plant as the controller and, its
controller gone, clears every name to Unknown and removes the SubnetLine; a definition's summary writes 90 and its
propagator writes 0 on L1 and 2 below V1, and valid devices of sources only make the subnetwork invalid; an isolated
line is not connected; END_VERTEX stops a line connecting at a middle vertex; cascading a vault's delete deletes the
valve it contains; cluster keys put the vault with the whole network and an isolated line apart.

**Measured against ArcGIS Pro 3.7 (build 1901, Advanced licence, file geodatabase) on 2026-10-30** for condition 5:
four networks built the same way in Pro and in grapy — a radial one with one controller, a mesh of two controllers and
two check valves, that mesh with a valve VB between the controllers, and a mesh of valves for isolation — and 126
identical traces run on both, Pro's answers taken from its ELEMENTS result (its per-edge `flowDirection` too, through
`include_flow_directions`). The harness was disposable and is not kept in the repository; the third network is drawn
in `test_utility_network.py`'s `MeshNetwork`. Before this batch's corrections 69 traces were identical and 57 differed;
after them **110 are identical and 16 differ** — radial 11 of 11 (11 before), mesh 28 of 28 (21 before), mesh with VB 53 of 69 (21 before), isolation 18
of 18 (16 before) (AND before OR among them, confirmed). What Pro was measured doing, and grapy now does:

- feature and condition barriers take part in the flow — with VB closed, A1 has a direction, upstream of C1 leaves out
  A3a and VB, downstream of A1 or A4 leaves out the controller — and a feature barrier answers as the condition barrier
  does; a filter barrier changes no flow, and the trace stops at it and keeps it;
- an edge the resource enters from both ends is indeterminate and passable both ways: downstream of B1 with VB closed
  reaches J1 back through A2 (but never A1); ignoring indeterminate flow, a trace starting on such an edge returns
  nothing;
- Pro's per-edge flow directions on the two-controller mesh are grapy's, edge for edge (twelve edges in the subnetwork,
  nine upstream of C1);
- isolation returns nothing when the leak's own area holds a subnetwork controller, isolated features asked for or not,
  and returns the isolating valves with EXCLUDE_BARRIERS too;
- terminals are numbered from 2 in a configuration's order (Source 2, Load 3) and a Single Terminal is 1; an output
  condition keeps the elements that pass it — `Terminal ID = 2` lists the check valves' Source terminals, not their Load
  — and so the features those belong to.

The 16 that differ are all IGNORE_INDETERMINATE_FLOW on the mesh with VB, and are §5.9 (f)'s stated difference. Two
smaller ones were recorded rather than built: Pro lists each directional device's internal path it passes as an element
of its own (`positionFrom` 0, `positionTo` 1, with a flow direction), which no page grapy read describes, and grapy lists
the device's terminals only; and Pro ignores a barrier row on a device without terminals unless TERMINALID is 1, which
grapy also accepts as null (§5.9 (b)).

## 5. Decision

1. **Scope of this batch.** Built: the network (CreateUtilityNetwork), traditional domain networks, tier groups and
   tiers, network attributes and their assignments, network categories, terminal configurations, rules, associations
   (by CSV and by `UtilityNetwork`'s methods), subnetwork controllers (by CSV and by its methods), the network topology
   (Enable, Disable, Validate, dirty areas and errors), and Trace's CONNECTED, SUBNETWORK, SUBNETWORK_CONTROLLERS,
   UPSTREAM, DOWNSTREAM and ISOLATION types with the SELECTION and AGGREGATED_GEOMETRY results. **Second batch:** subnetworks — their definitions, UpdateSubnetwork, ExportSubnetwork,
   the SubnetLine rows and Validate's discovered subnetworks; trace locations and named trace configurations;
   UpdateIsConnected, SetEdgeConnectivity, SetAssociationRole, RebuildNetworkTopology and the cluster keys; and inside
   Trace the LOOPS, SHORTEST_PATH and PATH types, target tiers, functions, function and filter function barriers,
   propagators with attribute substitution, nearest and bitset filtering, digitized direction, locatability, the first
   spatial container, and the ELEMENTS, CONNECTIVITY, FEATURES, CONTAINMENT_AND_ATTACHMENT_ASSOCIATIONS and
   RELATED_RECORDS result types. `_untrace` remains the one place a trace's flow is decided: UpdateSubnetwork,
   ExportSubnetwork and Validate's discovery call it.
2. **Storage, INFERRED (§5.9 (a)).** The classes as §2 A says, in the GeoPackage the tool works on, a utility network
   named `<file>.gpkg/<feature dataset>/<network>`. grapy's twenty tables `grapy_un*` (sixteen before the second batch
   added per-asset-type settings, subnetwork definitions, the Subnetworks table and trace configurations), registered as
   the
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
   name. Flow direction, Position from/to, Source ID and the telecom attributes are not computed; a digitized-direction trace therefore
   takes every line as drawn (§5.9 (j)).
6. **Traces.** As `_untrace`'s docstring states and §5.9 (f) records: CONNECTED ignores terminals; the subnetwork
   types find their controllers against the flow and span from them, stopping at other tiers' controllers in a
   partitioned domain and passing them in a hierarchical one; meeting a controller of the tier with another subnetwork
   name is the documented error; UPSTREAM and DOWNSTREAM are an *equivalent* (ADR-224 §5.6) — flow passes from u to v
   when some path from a controller reaches u without passing v, read off the dominator tree (Cooper, Harvey and
   Kennedy 2001), found with the trace's feature and condition barriers closed and its filter barriers open; an edge
   the resource enters from both ends is indeterminate and passable both ways, so a loop is traced whole unless
   indeterminate flow is ignored, when it is not entered and a trace starting on it returns nothing; ISOLATION returns
   the filter-barrier features the resource reaches the area through, with or without barriers included, and with
   isolated features what loses its supply when they close — and nothing when the area holds a subnetwork controller.
   All of this as ArcGIS Pro 3.7 was measured doing (§4). Starting points and barriers are a class or table with FEATUREGLOBALID, TERMINALID and
   PERCENTALONG, read as the Starting points page says. Condition and filter barriers, both scopes, output asset types
   and conditions, containers, content, structures, include barriers, ignore barriers at starting points and validate
   consistency are honoured. As the page says, a subnetwork-based trace run from Python applies no tier's trace
   configuration. **Second batch**, as `_untrace`'s docstring states and §5.9 (j) records:
   LOOPS returns the biconnected components of three or more nodes of what the starting points reach (Hopcroft and
   Tarjan 1973), a device's terminals one node; SHORTEST_PATH is Dijkstra's over the terminal paths both ways; PATH
   returns the `num_paths` paths of fewest hops of at most `max_hops` by Yen's k shortest loopless paths (1971), the
   cheapest when a cost attribute is named and one path asked for; nearest filtering keeps the `count` features of the
   categories or asset types least costly to reach — these, with Upstream and Downstream, are Trace's *equivalent*
   (ADR-224 §5.6). A target tier lets upstream and downstream pass the controllers of every tier ranked between the
   two, the flow found from the highest; function barriers keep a breadth-first running total or one per branch;
   propagators travel with the subnetwork's span and make barriers of what fails them; a bitset filter carries the
   starting point's bits; the JSON result types write the REST API's trace response (`_unjson`).
7. **Names.** Built (25): `CreateUtilityNetwork`, `AddDomainNetwork`, `AddTierGroup`, `AddTier`,
   `AddNetworkAttribute`, `SetNetworkAttribute`, `AddNetworkCategory`, `SetNetworkCategory`, `DeleteNetworkCategory`,
   `AddTerminalConfiguration`, `SetTerminalConfiguration`, `DeleteTerminalConfiguration`, `AddRule`, `DeleteRule`,
   `ImportRules`, `ExportRules`, `ImportAssociations`, `ExportAssociations`, `ImportSubnetworkControllers`,
   `ExportSubnetworkControllers`, `EnableNetworkTopology`, `DisableNetworkTopology`, `ValidateNetworkTopology`,
   `Trace`, and the class `UtilityNetwork`. **Second batch (14), 39 in all:** `SetSubnetworkDefinition`,
   `UpdateSubnetwork`, `ExportSubnetwork`, `AddTraceConfiguration`, `DeleteTraceConfiguration`,
   `ExportTraceConfigurations`, `ImportTraceConfigurations`, `AddTraceLocations`, `UpdateIsConnected`,
   `SetEdgeConnectivity`, `SetAssociationRole`, `RebuildNetworkTopology`, `EnableClusterKeys` and
   `CalculateClusterKeys` — the last an *equivalent* (ADR-224 §5.6): the page does not say how keys are computed, and
   grapy numbers the connected components of the topology, its containment and attachment associations included
   (Hopcroft and Tarjan 1973).
8. **Later batches — a schedule, not a refusal reason** (condition 4). ~~Refused today as *in a later batch of
   ADR-231*: subnetworks (UpdateSubnetwork, ExportSubnetwork, SetSubnetworkDefinition, the SubnetLine rows and the
   discovered subnetworks Validate returns), trace configurations (Add, Delete, Import, Export), AddTraceLocations,
   UpdateIsConnected, SetEdgeConnectivity, SetAssociationRole, AnalyzeNetworkData, RebuildNetworkTopology,
   ApplyErrorResolutions, the cluster key tools; Trace's LOOPS, SHORTEST_PATH and PATH types, target tiers, functions,
   function barriers, propagators, nearest filtering, bitset filtering, digitized direction, locatability, synthesized
   geometry, and the JSON result types; and the telecom domain network with its circuits, colour and wavelength
   schemes (twelve names and the CIRCUIT trace).~~ **Emptied by the second batch, 2026-10-30**: no name gives that
   reason any more. Refused, each for its own reason: the twelve telecom names, the CIRCUIT trace, the CIRCUITS result
   type, `circuit_name` and `infer_connectivity` — Add Telecom Domain Network's page does not name the classes and tables
   a telecom domain network has, and every telecom tool reads or writes them; AnalyzeNetworkData and
   ApplyErrorResolutions — they write and read an error resolution database whose layout the pages do not give;
   `synthesize_geometries` — the page does not say how the geometry is drawn; a subnetwork definition's
   `diagram_template` — network diagrams, below. Refused for good, each with its reason: the sixty-seven network
   diagram tools (network diagrams are a model of their own, as `arcpy.nd` already says), MigrateToUtilityNetwork (an
   asset package is Esri's layout), Verify and Repair Network Topology (Esri's persisted index), and
   UpdateUtilityNetworkSchema (its page answered 404).
9. ~~**INFERRED, for confirmation**~~ **Confirmed 2026-10-31 by owner decision in Q-199** (*"Hepsini onayla"*) ([Q-199](../open-questions.md)):
   - (a) the storage of §5.2: the classes in the feature dataset under ArcGIS's names; `<network>_Associations` and
     `<network>_DirtyAreas` beside them; grapy's sixteen `grapy_un*` tables; COMPATIBLE and CURRENT make version 8;
   - (b) ~~terminal IDs 1, 2 … in the order a configuration names its terminals; an asset type with no configuration has
     one junction and no terminal ID, written `Single Terminal` in CSV files~~ **measured in Pro 3.7, 2026-10-30, and no
     longer an inference**: a configuration's terminals are numbered 2, 3 … in the order it names them (Source-Load:
     Source 2, Load 3), and the one junction of an asset type with no configuration is the Single Terminal, 1 — written
     so in trace results and read so by a `Terminal ID` condition, and `Single Terminal` in CSV files. Still grapy's: a
     network made before this numbering keeps the IDs it stored; and a barrier or starting point on a device without
     terminals is placed with TERMINALID 1 or null, where Pro silently ignores the null;
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
     controllers are found against the flow and the search stops at the first controller of the tier; ~~upstream and
     downstream find their controllers and the flow without the trace's barriers and stop at the barriers when they
     trace~~; the flow rule of §5.6; the isolating set and isolated features of §5.6; ~~in a condition AND binds before
     OR~~; ~~a feature passes an output filter when one of its traced elements does~~; filter barriers stop the
     non-isolation traces as condition barriers do; a line row without PERCENTALONG places nothing; a starting point
     the trace's barriers cut off from every controller has no flow and no upstream or downstream.
     **Measured in Pro 3.7, 2026-10-30 (§4), and no longer inferences:** upstream and downstream find the flow with the
     feature and condition barriers closed — a feature barrier answering as a condition barrier does — and with the
     filter barriers open, at which they stop and which they keep; an edge the resource enters from both ends is
     indeterminate and passable both ways, and ignoring indeterminate flow a trace starting on one returns nothing;
     on a mesh of two controllers Pro's per-edge flow directions are the dominator rule's, edge for edge; isolation
     returns its isolating features with EXCLUDE_BARRIERS too, and nothing when the leak's area holds a subnetwork
     controller; AND binds before OR; an output filter keeps the elements that pass it, and a feature is returned when
     one of its elements is. **A measured difference, kept:** with IGNORE_INDETERMINATE_FLOW in a mesh, Pro's flow
     depends on more than the network's shape. The mesh with VB open is the two-controller mesh with one more valve on
     A3 and nothing else changed, yet Pro answers 16 traces differently on it — upstream of C1 is C1 and J2 on the
     first and eleven features on the second — and its answers on it imply a circular flow, J1 → B1 → CV2 → B4 → J2 →
     A2 → J1: downstream of B1 reaches J1 through A2 and upstream of C1 reaches J1 the other way round. No rule over the
     topology gives two answers to one shape, so grapy keeps the dominator rule — which reproduces every per-edge
     direction Pro reported — and does not fit one; the explanation that Pro marks an edge indeterminate where two
     controllers' traversals cross it in opposite directions, in an order of its own, is INFERRED, not measured;
   - (g) a selection is made on a layer named after each class (`WaterDevice`), made when there is none; aggregated
     geometry goes to `Trace_Results_Aggregated_Points`, `_Lines` and `_Polygons` in the network's GeoPackage when no
     class is named, as the project's default geodatabase has no counterpart;
   - (h) DeleteRule's `ALL` with no description deletes every rule; ImportRules skips a rule the network has; an
     association's ASSOCIATIONSTATUS is recomputed for every feature when associations change;
   - (i) with more than four terminals valid paths are refused, all paths being valid;
   - (j) the second batch's trace rules: a function barrier's global total is taken in breadth-first order, each
     element's neighbours in id order, and its local total along the path the trace first reached an element by; the
     element meeting the condition is the barrier, whole (no apportioned midspan stop); nulls are skipped and COUNT
     counts the non-null values; SUBTRACT is the first value less the others; a propagated value is the first one an
     element is reached with in breadth-first order from the controllers, and a Subnetwork Tap is substituted like any
     other; a substitution value uses four-phase encoding when it has a bit above the ninth; a bitset filter carries the
     starting point's bits, a null passing them on, and a loop is kept when some bit runs all the way round; a loop is a
     biconnected component of three or more nodes; a path's hops count entering an edge 1, a junction-junction
     connection 1 and a device's terminal path 2, and ties go to the lower element id; nearest cost is the attribute
     summed on entering each element over what was traced, a count of -1 keeping all; tier rank 1 is the highest, and a
     target tier passes the controllers of every tier between; a digitized trace takes every line as drawn, the Flow
     direction attribute not being computed;
   - (k) SetAssociationRole: an asset type given no role keeps both containing and having attachments, as before roles
     existed; deletion semantics act when a utility network tool next finds the delete — CASCADE deletes the content
     or attachments, SET_TO_NONE the associations, RESTRICTED nothing (grapy cannot refuse a delete made by a cursor);
     view scale and split content are kept for a map and an editor grapy does not have;
   - (l) a JSON result file is the REST API's trace response — `traceResults`, `sourceMapping`, `resultTypes`,
     `success` — with a system junction written as network source 0 and no global ID; a connectivity entry per traced
     edge element from each junction at its start to each at its end, and per junction-junction connection;
     containment and attachment associations with either end in the result; related records with no fields named list
     every relationship class without fields; ExportSubnetwork's file adds the subnetwork's names, `isDeleted` and
     `controllers` (each element's fields and `subnetworkControllerName`), and with no result type only those;
     function results are also written as messages;
   - (m) subnetworks: the Subnetworks table is `grapy_un_subnetwork`; ISDIRTY 0 clean, 1 dirty, 2 invalid, and
     ISCONNECTED 0 Unknown, 1 True, 2 False; a feature in several subnetworks of a tier carries their names joined by
     `::`, and one in none `Unknown`; UpdateSubnetwork's own barrier parameters are added to the definition's; a
     validated change is discovered by tracing each clean subnetwork again and comparing its features with those
     UpdateSubnetwork last wrote, a new controller name or a gone one being discovered too; SubnetLine's aggregated
     lines are every line when the definition names none; the edit modes are kept and change nothing; Update Is
     Connected follows connectivity without barriers, within the feature's domain network;
   - (n) Trace with a named configuration takes the configuration's settings over the call's and keeps the call's
     locations and output paths; AddTraceLocations writes FEATUREGLOBALID, TERMINALID, PERCENTALONG and
     ISFILTERBARRIER, an object's location with no shape; an exported configuration's traceType codes 1 to 10 follow the
     order the AddTraceConfiguration page lists the types, the keys of its functions (`conditions`, `functionName`),
     propagators (`propagatedAttributeName`) and output filters (`assetType`) are grapy's, and SELECTION and ELEMENTS
     are both written `elements` and read back as SELECTION; a configuration's creator is the operating system user.

## 6. Consequences

- **State.** In the GeoPackage the tool works on. Nothing in the catalogue or the server.
- **No new dependency**: shapely and the standard library.
- **Names.** `tools/grapy-refusals.json` before → after: `arcpy.un` 0 of 124 on the counter → built 25 (equivalent 1),
  refused 99, unexamined 0; all modules built 918 → 943, refused 1132 → 1231, of 2050 → 2174, equivalents 81 → 82.
  **Second batch:** `arcpy.un` built 25 → 39 (equivalents 1 → 2), refused 99 → 85, unexamined 0; all modules built
  943 → 957, refused 1231 → 1217, of 2174, equivalents 82 → 83.
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

- ~~A toolbox depends on a later-batch name — the batch is then the next step.~~ Fired and spent by the second batch.
- Esri publishes the telecom domain network's classes and tables, or the error resolution database's layout — the
  names refused for their absence become buildable.
- The owner changes an inference of §5.9, or Pro is found to answer a trace of §5.9 (f) differently on a network where
  the pages leave it open. **Fired 2026-10-30 by the Pro 3.7 measurement (§4)**: the differences were fixed or written
  down, and §5.9 (b) and (f) say which; it stays armed for the inferences that remain.
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
4. **Every name of `arcpy.un` is built or refused with its own reason.** ~~PARTLY DISCHARGED 2026-10-30: none answers
   the generic message, and the diagram tools, MigrateToUtilityNetwork, Verify and Repair Network Topology and
   UpdateUtilityNetworkSchema give their reasons. Twenty-eight are refused only as "in a later batch of ADR-231", which
   is a schedule, not a reason: the sixteen of §5.8's first list and the twelve telecom names; Trace's later trace
   types, parameters and result types likewise. Each is to be built or given its own reason.~~ **DISCHARGED
   2026-10-30** by the second batch: of the twenty-eight, fourteen are built (one an equivalent), and the twelve telecom
   names and AnalyzeNetworkData and ApplyErrorResolutions are refused for reasons of their own (§5.8); inside Trace
   every type, parameter and result type is built except the telecom ones and `synthesize_geometries`, refused by name.
   `tools/grapy-refusals.json` holds no later-batch reason.
5. **Upstream, downstream and isolation are compared with Pro** on a mesh network with two controllers and on a
   network with barriers between a starting point and its controller, using the owner's Pro install (the harness of
   the Pro arcpy probes) — or the owner accepts §5.9 (f) as grapy's reading. The second batch adds to the comparison
   the rules of §5.9 (j) a mesh would expose: global function barriers, propagation through a loop, and which paths a
   path trace returns. **PARTLY DISCHARGED 2026-10-30**: the first half is done — 126 traces on four networks in
   ArcGIS Pro 3.7 (§4), 69 identical before and 110 after the corrections, every difference found either fixed and
   pinned by a test (barriers in the flow, an indeterminate start, isolation around a controller and without barriers,
   terminal IDs from 2, the elements an output filter keeps) or written down as a measured difference (ignored
   indeterminate flow in a mesh, §5.9 (f); a device's path element and a null TERMINALID, §4). The second half is
   open: no function barrier, propagator or path trace was run in Pro. **PARTLY DISCHARGED 2026-10-31**, the second
   half: 35 traces on the two-controller mesh (`MeshNetwork`, with a PRESSURE value on its lines) in Pro 3.7 and grapy,
   by `tools/pro-compare/adr231-mesh-traces.py`; 15 identical before, **18 after**. Identical: global and local ADD and
   MAX function barriers on connected traces from S1 (six, with and without barriers kept), connected and downstream
   traces, loops, a shortest path, upstream with a closed valve. Changed to Pro's answer: an upstream trace that a
   feature barrier or a function barrier cuts off every controller is refused, ERROR 001797 (grapy answered nothing);
   a shortest path that barriers leave none of is an empty result, not an error. Measured and not followed —
   [D-338](../architecture-debt.md) (propagators measured again 2026-10-31 with the attribute on every class; see the row): function barrier totals on a downstream trace and from a junction of several
   edges (five traces), where Pro visits neighbours in an order five traces do not pin down; propagators (six), which
   Pro refuses with ERROR 001797 where the propagated attribute has no value at the controllers; PATH traces (five),
   for which Pro returns nearly the whole connected network, dead ends included; a tie of two 30 m shortest paths,
   Pro taking the one through A2. Pro also needs TERMINALID on a starting point — the terminal on a device with
   terminals, 1 on a junction — and answers ERROR 001785 without it, where grapy takes the feature.
6. **The owner confirms or changes §5.9** ([Q-199](../open-questions.md)). **DISCHARGED 2026-10-31** — confirmed as built by owner decision in Q-199 (*"Hepsini onayla"*).
