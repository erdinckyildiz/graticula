# arcpy.nd against ArcGIS Pro 3.7

Measured 2026-11-12, case module `cases/nd.py`, 68 names (every built `arcpy.nd` name: `_defined()` of `nd.py`
intersected with `tools/arcpy-surface.json`). Every case runs on ADR-232 §4's water network (the one
`tools/pro-compare/adr232-diagrams.py` measured), built in the case module the same way on both sides, with a pole P
(a structure with V1 attached) and a junction–junction connectivity association T → V1 added; every association goes in
through ImportAssociations on both sides. A diagram is compared through ExportDiagramContent's JSON: junctions and
containers by name with their positions, edges as [name, from, to, aggregated names, path].

**Process rules found on the way.** Pro applies only the first layout a Python process runs (ADR-232 condition 5), and
stores only one diagram a process — a later StoreDiagram fails with ERROR 001771 — so the 28 layout cases and the 5
cases that store run each in a harness process of their own (`LAYOUT_CASES`; one harness call per case, the other 35
together). Pro wants "Single Terminal", not an empty value, for a terminal-less end of a connectivity association in
ImportAssociations (ERROR 000192), and a deletion semantics for SetAssociationRole STRUCTURE (000735); grapy accepts
both omissions (arcpy.un's; left to the un sweep).

First run: every case PRO-ERROR (the case's SetAssociationRole, then ImportAssociations, as above). First comparable
run: MATCH 32, DIFF 25, PRO-ERROR 8, GRAPY-ERROR 2, BOTH-ERROR 1.
Now: **MATCH 54, DIFF 10, PRO-ERROR 4** (not comparable). No case depends on a network dataset, so the na branch's
geodesic Length changes nothing here (merged; re-run after the merge).

| Name | First run | Now | Action | Note |
|---|---|---|---|---|
| AddAngleDirectedLayout | MATCH | MATCH | — |  |
| AddCollapseContainerByAttributeRule | MATCH | MATCH | — |  |
| AddCollapseContainerByCategoryRule | PRO-ERROR | MATCH | fixed / case | Pro requires a category (000735) even with EXCLUDE_CATEGORIES; grapy now does too (all five by-category rules); the case excludes Isolating |
| AddCollapseContainerRule | MATCH | MATCH | — |  |
| AddCompressionLayout | DIFF | DIFF | recorded | EQUIVALENT layout: junctions agree; Pro adds a vertex where L3 enters the vault's container (29.5, 0) |
| AddConnectivityAssociationsRule | DIFF | MATCH | aligned | Pro draws the association imported T → V1 as V1 → T: from the junction first in the diagram (INFERRED rule, one association) |
| AddDiagramFeatureCapabilityByAttributeRule | MATCH | MATCH | — |  |
| AddDiagramFeatureCapabilityByCategoryRule | MATCH | MATCH | — | category now required, as Pro |
| AddDiagramFeatureSelectionToLayout | MATCH | MATCH | fixed | MATCH; SELECT_BY_CATEGORY with no category is ERROR 000663 in Pro and now in grapy |
| AddDiagramTemplate | DIFF | MATCH | fixed | a new network has Pro's three templates Basic, CollapseContainers, ExpandContainers (grapy had none) |
| AddExpandContainerByAttributeRule | MATCH | MATCH | — |  |
| AddExpandContainerByCategoryRule | PRO-ERROR | MATCH | fixed / case | as AddCollapseContainerByCategoryRule |
| AddExpandContainerRule | MATCH | MATCH | — |  |
| AddForceDirectedLayout | DIFF | DIFF | recorded | EQUIVALENT (force-directed); positions differ |
| AddGridLayout | MATCH | MATCH | — |  |
| AddLinearDispatchLayout | DIFF | MATCH | case | Pro writes vertices repeated in place (0,0),(0,0),(6,0),(6,0)…; the case drops repeats; junctions always agreed |
| AddMainRingLayout | PRO-ERROR | DIFF | case / recorded | first run Pro ERROR 002637 (disjoint graphs: the pole) then 002638 (no ring); the case adds L5 E–V2 in its own process. EQUIVALENT ring placement differs |
| AddMainlineTreeLayout | DIFF | MATCH | fixed | fitted to the diagram and centred as the smart tree; a branch starts one step along as well as across (D-338's measured rule, seen again); edges slant to 30 % |
| AddPartialOverlappingEdgesLayout | GRAPY-ERROR | MATCH | fixed | Pro needs neither value (defaults 0.5, 0.5); the edge leaves its junction slanting to max(buffer, offset) along; equal edges: the later one on the right |
| AddRadialTreeLayout | DIFF | DIFF | recorded | D-338 (radial wedges and levels, measured but not followed); also the pole's placement |
| AddReduceEdgeByAttributeRule | MATCH | MATCH | — |  |
| AddReduceJunctionByAttributeRule | MATCH | MATCH | — |  |
| AddReduceJunctionByCategoryRule | MATCH | MATCH | — | category now required, as Pro |
| AddReduceJunctionRule | MATCH | MATCH | — |  |
| AddRemoveFeatureByAttributeRule | MATCH | MATCH | — |  |
| AddRemoveFeatureByCategoryRule | MATCH | MATCH | — | category now required, as Pro |
| AddRemoveFeatureRule | MATCH | MATCH | — |  |
| AddReshapeDiagramEdgesLayout | MATCH | MATCH | — |  |
| AddSetRootJunctionByAttributeRule | DIFF | MATCH | fixed | read through a smart tree top to bottom: disjoint parts ordered by where their roots stood across the direction (Pro put the pole after the tree here, above it left to right) |
| AddSetStartingPointByAttributeRule | DIFF | MATCH | fixed | a trace adds what it returns to the diagram (Pro kept E, L4 and the pole an upstream trace from V2 does not reach) |
| AddSmartTreeLayout | DIFF | MATCH | fixed | edges slant from the parent to the breakpoint (30 %) at the child's place across: T → (23, -10) → V2 |
| AddSpatialDispatchLayout | DIFF | DIFF | recorded | EQUIVALENT: Pro nudges every junction by up to 0.5 m; grapy leaves them |
| AddSpatialQueryRule | DIFF | MATCH | fixed | a feature a spatial query adds brings its container (V2 its vault) |
| AddStartIterationRule | MATCH | MATCH | — |  |
| AddStopIterationRule | MATCH | MATCH | — |  |
| AddStructuralAttachmentsRule | MATCH | MATCH | — |  |
| AddTraceRule | DIFF | MATCH | fixed | the connectivity associations between traced features come with the trace, drawn from → to (T → V1) |
| AlterDiagramProperties | MATCH | MATCH | case | runs alone: Pro stores one diagram a process (later StoreDiagram ERROR 001771) |
| AlterDiagramTemplate | PRO-ERROR | MATCH | case | ExtendDiagram, which would read the extension flag, fails in Pro (999999); the renamed template is read by a diagram |
| AppendToDiagram | PRO-ERROR | PRO-ERROR | not comparable | Pro: ERROR 000735 Input Map: Value is required — needs a map, which a script cannot give without opening a project |
| ApplyAngleDirectedLayout | MATCH | MATCH | — |  |
| ApplyCompressionLayout | DIFF | DIFF | recorded | as AddCompressionLayout |
| ApplyForceDirectedLayout | DIFF | DIFF | recorded | as AddForceDirectedLayout |
| ApplyGeoPositionsLayout | DIFF | MATCH | fixed | edges come back straight by default (DO_NOT_RESTORE_EDGES_GEO_POSITIONS): Pro dropped L2's vertex (13, 0) |
| ApplyGridLayout | MATCH | MATCH | — |  |
| ApplyLinearDispatchLayout | DIFF | MATCH | case | as AddLinearDispatchLayout |
| ApplyMainRingLayout | PRO-ERROR | DIFF | case / recorded | as AddMainRingLayout |
| ApplyMainlineTreeLayout | DIFF | MATCH | fixed | as AddMainlineTreeLayout |
| ApplyPartialOverlappingEdgesLayout | GRAPY-ERROR | MATCH | fixed | as AddPartialOverlappingEdgesLayout; also (2, 1) and (1, 2) measured and matched |
| ApplyRadialTreeLayout | DIFF | DIFF | recorded | as AddRadialTreeLayout |
| ApplySmartTreeLayout | MATCH | MATCH | fixed | as AddSmartTreeLayout (Pro's run came after the fix, so its first comparison is already MATCH) |
| ApplySpatialDispatchLayout | DIFF | DIFF | recorded | as AddSpatialDispatchLayout |
| ApplyTemplateLayouts | MATCH | MATCH | — |  |
| ChangeDiagramsOwner | BOTH-ERROR | PRO-ERROR | not comparable | Pro: ERROR 000229 Cannot open <file gdb utility network> — the tool opens only a service's network (owners are portal users) |
| CreateDiagram | MATCH | MATCH | — |  |
| DeleteDiagram | MATCH | MATCH | case | runs alone (one store a process) |
| DeleteDiagramTemplate | DIFF | MATCH | fixed | names include the default templates |
| ExportDiagramContent | DIFF | MATCH | fixed | attributes keyed by Pro's aliases ("Asset group", "Object ID", "Tier name" …: the network classes' system fields now carry them); sourceMapping only the diagram's classes, by alias; Web Mercator wkid 102100 |
| ExportDiagramTemplateDefinitions | MATCH | MATCH | — | file written; its format is each side's own (Esri's .ndbd is not published) |
| ExtendDiagram | PRO-ERROR | PRO-ERROR | not comparable | Pro: ERROR 999999 'Something unexpected caused the tool to fail' every way it was asked (four extension types, stored or not), headless on a file geodatabase |
| GetDiagramTemplateNames | DIFF | MATCH | fixed | Basic, CollapseContainers, ExpandContainers; the case also compares what each makes of every feature and of the vault alone: all MATCH (Basic keeps vertices and draws associations) |
| ImportDiagramTemplateDefinitions | MATCH | MATCH | — | each side reads the file it wrote |
| MakeDiagramLayer | DIFF | MATCH | fixed | Describe answers dataType DiagramLayer (grapy said Table) |
| OverwriteDiagram | PRO-ERROR | PRO-ERROR | not comparable | as AppendToDiagram |
| PurgeTemporaryDiagrams | MATCH | MATCH | case | runs alone (one store a process) |
| ReshapeDiagramEdgesLayout | MATCH | MATCH | — |  |
| StoreDiagram | MATCH | MATCH | case | runs alone (one store a process) |
| UpdateDiagram | DIFF | MATCH | case | first run Pro answered an empty stored diagram after other stores in the process; on a temporary diagram it reruns the new rule as grapy does |

## Open differences worth a debt row

- **Layouts EQUIVALENT by design, still different** (`@equivalent` in grapy, Pro's methods not published): Force
  Directed, Spatial Dispatch (Pro nudges junctions by up to 0.5 m), Compression (Pro adds a vertex where an edge enters
  a container), Main Ring (ring placement). Radial Tree stays D-338.
- **Upstream/downstream traces across a junction–junction connectivity association** (arcpy.un's `_untrace._flow`,
  left to the un sweep): with the association T–V1 beside L2, grapy marks L2 indeterminate and an UPSTREAM trace from
  V2 drops it (it returns it with TRACE_INDETERMINATE_FLOW); Pro returns L2. Hidden in this module's comparison because
  a Trace rule now adds to the diagram rather than replacing it.
- **ExportDiagramContent `userType`**: Pro writes 0 for every feature measured; grapy writes the class name (INFERRED,
  ADR-232 §5.9 (h)). The case does not compare values of `userType`.
- **INFERRED from one measurement each**, worth a second diagram when Pro is next at hand: the Connectivity
  Associations rule's direction (first junction in the diagram), the order of disjoint tree parts (by where their roots
  stood), the side of the later of two equal overlapping edges, and Partial Overlapping's slant length (the larger of
  buffer width and offset; three measurements).
- **Not comparable here**: AppendToDiagram and OverwriteDiagram (Pro needs a map: ERROR 000735 "Input Map"),
  ChangeDiagramsOwner (Pro cannot open a file geodatabase network: ERROR 000229; owners are portal users), ExtendDiagram
  (Pro fails with ERROR 999999 headless, every extension type, stored or temporary).
