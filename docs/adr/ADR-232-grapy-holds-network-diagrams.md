# ADR-232 — grapy holds network diagrams

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` for the diagram management, template and rule tools, whose pages give their parameters, keywords, defaults and what each rule does to the diagram · `HIGH` for the layouts built to their pages (geo positions, grid, linear dispatch, angle directed, partial overlapping edges, reshape edges) on the hand-worked diagrams of the tests · `LOW` for agreement with Pro's placements wherever a layout page names its arrangement but does not define it — smart, mainline and radial trees, main ring, force directed, spatial dispatch, compression — which are *equivalents* (§5.5) |
| **Decided** | 2026-10-30, by owner decision (*"Yap"*): grapy gets network diagrams, the schematic views of a utility network, on top of [ADR-231](ADR-231-grapy-holds-utility-networks.md)'s utility networks ([ADR-224](ADR-224-grapy-aims-at-arcpys-whole-buildable-surface.md) group 2). The storage and the rules the pages leave open are INFERRED and listed for confirmation in §5.9 and [Q-200](../open-questions.md). |
| **Supersedes** | — |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

Before this decision 141 names were refused as *"network diagrams, the schematic views of a utility or trace network,
need a data model this server does not hold yet"*: the whole of `arcpy.nd` (74) and the sixty-seven diagram tools
`arcpy.un` carries under the same names. `SetSubnetworkDefinition`'s `diagram_template` was refused for the same
reason. A toolbox that builds a template, makes a diagram of a feeder and lays it out as a tree could not run here.

What ArcGIS publishes, and what it does not. Every page cited was read on doc.esri.com (pro.arcgis.com redirects
there) on 2026-10-30: the Network Diagram toolbox overview and its Configuration and Layout toolset overviews; the
pages of Create, Append To, Overwrite, Extend, Update, Store, Delete, Make Diagram Layer, Alter Diagram Properties,
Change Diagrams Owner, Purge Temporary Diagrams and Export Diagram Content; Add, Alter and Delete Diagram Template,
Get Diagram Template Names, Export and Import Diagram Template Definitions, Create Geoprocessing Model From Diagram
Template; every Add … Rule page (reduce junction and its by-attribute and by-category forms, reduce edge, remove
feature ×3, collapse and expand container ×3 each, trace, set starting point, set root junction, spatial query,
connectivity associations, structural attachments, start and stop iteration, diagram feature capability ×2, diagram
feature selection to layout); and every Apply … Layout page with the Add … Layout pages of grid, smart tree, linear
dispatch, relative mainline, reshape and partial overlapping edges. The ArcGIS REST API's public *Query Diagram
Content* page was read for the JSON the Export Diagram Content page points to. No Esri implementation was read
(CLAUDE.md §5).

- **The tools' parameters, keywords, defaults and constraints are documented**, and so is what each rule does to a
  diagram: a junction with no connection removed, with one reduced onto it with its edge, with two replaced with its
  edges by a *reduction edge*, with three or more reduced onto its single upstream junction; a line and its connection
  points merged into one *reduction junction*; a container's content collapsed into it, edges reconnected to it and
  — by default — aggregated per pair of junctions; content added by expansion, the container kept or hidden; a trace
  rule's result becoming the diagram's content; connectivity associations and structural attachments drawn as
  straight edges between junctions already present; a Start/Stop Iteration sequence repeated while reductions make
  new candidates; capabilities that later rules read.
- **Some layouts are specified, some only named.** Geo Positions, Grid (a magnetic grid of the cell size), Linear
  Dispatch (equal distance, a user minimum, or iterative shifts of two-edge junctions along their path, the path
  preserved or not, leaves moved or expanded), Angle Directed (each edge turned to the nearest of 4, 8 or 12 directions
  measured from its origin junction), Partial Overlapping Edges (collinear segments in a buffer spread equally left and
  right, ``offset`` apart) and Reshape Diagram Edges (remove vertices, square, separate overlapping, reduce by angle,
  mark crossings) describe their arrangement. Smart Tree, Mainline Tree, Radial Tree, Main Ring, Force Directed,
  Spatial Dispatch and Compression name what they achieve — a hierarchical tree, a main line with branches, concentric
  levels, a ring of the longest loop, an energy equilibrium "emphasizing loops", near junctions separated, groups
  compressed toward the middle — but not how.
- **The storage is not documented**: how a diagram, its diagram features and their links to network elements are
  kept, the `.ndbd` rule and layout definitions file and the `.ndld` layer definition file.
- **Two tools act on things only Pro's application holds**: Rotate Tree turns trees round *pivot junctions*, which are
  set only interactively in a diagram map; the layer definitions are a diagram map's sublayers and symbology.

## 2. Alternatives considered

### Alternative A — diagrams in grapy's own tables and three feature classes beside the network (chosen)

A template is a row of properties and an ordered list of steps (rules, layouts, feature selections) with the parameters
its Add tool was given; a diagram is a row (template, owner, access, tags, dates, stored or temporary, system or not,
its input and the signature of the topology it was built against) and its features. The features' geometry is in
three feature classes beside the network — `<network>_DiagramJunctions`, `_DiagramEdges`, `_DiagramContainers` — with
DIAGRAMNAME, DIAGRAMFEATUREID, ASSOCSOURCEID, ASSOCGLOBALID and CONTAINERID (and FROMID, TOID on edges); what each
represents and aggregates is grapy's `grapy_nd_feature`. **For:** a cursor or a layer reads a diagram as it reads any
class, so MakeDiagramLayer's sublayers are ordinary feature layers and export as feature classes with the conversion
tools; diagrams are built from `_utility.build`'s topology and traced with `_untrace`, the one place a trace's flow is
decided; nothing undocumented is reproduced. **Against:** a diagram made by grapy cannot be opened by Pro, nor one
made in Pro by grapy.

### Alternative B — no stored diagram, only a diagram computed on demand

**For:** no storage to infer. **Against:** stored diagrams, owners, access rights, consistency, Update Diagram's
"positions kept", Append, Overwrite and Extend all assume a diagram that persists; the pages would be half-built.

### Alternative C — read and write Esri's diagram storage, `.ndbd` and `.ndld`

Rejected: undocumented; the clean room forbids reproducing them (CLAUDE.md §5).

### Alternative D — refuse network diagrams

Rejected by the owner's *"Yap"*.

## 3. Counterarguments to the preferred option

**Several layouts are grapy's, not Pro's.** Where a page names the arrangement but not its method, the layout is the
standard method of the literature (§5.5) and says so in its run; the diagram it draws is a correct tree, ring or
force equilibrium, but not Pro's picture. A toolbox comparing coordinates with Pro will differ.

**A rule's corner cases are grapy's.** The pages say what a reduction or a collapse does on the common case; which
neighbour a junction of three connections is reduced onto in a mesh, where a reduction junction is placed, whether a
removed junction takes its edges with it — §5.9 (c) records each choice.

**The diagram is rebuilt whole.** Update, Append and Overwrite run the template's rules again on the whole input and
put back the positions of the features that existed before. On a large diagram that is slower than Pro's incremental
update; nothing has been measured.

## 4. Evidence

`src/grapy/tests/test_network_diagrams.py`, thirty-eight tests on a water network drawn in its docstring — a source
whose Load terminal controls Zone1, two valves, two tees and a vault containing a valve — and on small diagrams built
in the tests for the edge layouts. Worked by hand there: a diagram of every feature has five junctions, four edges
and the vault as a rectangle 0.5 round its valve; reducing the valves leaves one reduction edge S→T aggregating V1, L1
and L2; reducing junctions of one connection in id order folds S, then V1, then V2 away; a junction of three
connections is reduced onto its neighbour nearest the controller; reducing L4 makes one junction at (20, 5) with the
reconnected edges aggregated; a Start/Stop Iteration pair removes, on its second pass, the tee the first pass left
unconnected; collapsing the vault reconnects L3 to it as a reduction edge unless a capability prevents it; expanding
it adds its valve and hiding it leaves the valve alone; a connected trace rule from one tee returns the whole network
and a downstream one from T returns T, V2 and E; a spatial query 11 m from T adds V2; the smart tree from S is S (0, 0),
V1 (2, 0), T (4, 0), E (6, 1), V2 (6, −1), and from the top with spacings 5 and 4, T at (0, −10) and E, V2 at (±2, −15);
orthogonal edges with breakpoint 0 bend at the parent's along-position; the radial tree puts V1 at (−5, 0), T at
(−10, 0) and E, V2 at (0, ±15), T at (−15, 0) with radius factor 2; the mainline tree runs S, V1, T, and one of E and
V2 straight and the other on the left at (4, 2); grid of 3 m snaps T to (21, 0) and E to (21, 9); linear dispatch puts
V1 at (10, 0), keeping its old place as a vertex with PRESERVE_PATH; spatial dispatch with factor 7 moves S and V1 to
−0.5 and 6.5; compression at 6 m gives S (7.5, 2) … E (16.5, 8); a four-direction angle layout after a force layout
leaves every edge horizontal or vertical; the main ring of a square puts its corners on an ellipse 20 × 10 and the
pendant junction 2 outward; partial overlapping edges 1 apart move to −1.5 and 2.5 with offset 4; the reshape
operations' vertices; and the diagram tools' life cycle — storing (only PUBLIC, only consistent), renaming, purging,
deleting by template, append, overwrite, extend by connectivity and containment, sublayers exported with
ExportFeatures, the JSON content with its aggregations' asset group and type names, template definitions round-tripped
through a .ndbd file, and a subnetwork system diagram that UpdateSubnetwork makes and the other tools refuse to change.
No performance claim is made.

## 5. Decision

1. **Scope.** Built in `arcpy.nd` — and in `arcpy.un` under the same names, which are the same functions: diagram
   templates (Add, Alter, Delete, Get Names, Export and Import Definitions), every diagram rule, diagrams (Create from
   features, layers, grapy's layer selections or trace locations; Append, Overwrite, Extend, Update, Store, Alter
   Properties, Change Owner, Delete, Purge; Make Diagram Layer with sublayers; Export Diagram Content), and every layout
   but two (§5.7). `SetSubnetworkDefinition`'s `diagram_template` is built: UpdateSubnetwork keeps the tier's
   subnetwork system diagrams (§5.6).
2. **Storage, INFERRED (§5.9 (a)).** As §2 A says, in the GeoPackage of the network, the tables registered as the
   `grapy_network_diagram` extension in `gpkg_extensions`.
3. **A diagram is built from the topology's record.** Each junction feature — all its terminals — is one diagram
   junction; each edge element (a line between two connection points) one diagram edge from the junction at its start
   to the junction at its end; a system junction one diagram junction; a feature with no element (a structure
   boundary, an assembly) one junction, which is a container while it has content in the diagram. The input's
   containers come with it. The template's rules run in order, the features are placed at their geographic positions,
   and the template's active layouts run, each limited by the feature selections just before it. Diagrams fail on dirty
   areas, need the topology enabled, and are inconsistent once their template's rules, layouts or container margin
   change or a feature they hold is validated again.
4. **Rules** do what their pages say (`grapy._ndrules`), with the choices of §5.9 (c).
5. **Layouts.** Built to their pages: Geo Positions, Grid, Linear Dispatch, Angle Directed, Partial Overlapping Edges
   and Reshape Diagram Edges, with the choices of §5.9 (e). **Equivalents** (ADR-224 §5.6), marked with
   `_runtime.equivalent` and saying so in their run, Add and Apply alike: Smart Tree and Mainline Tree — the tidy tree
   drawing of Reingold and Tilford (1981); Radial Tree — concentric levels with wedges by leaf count (Eades 1992); Main
   Ring — the longest cycle by depth-first search (Tarjan 1972) with tidy trees outward; Force Directed — Fruchterman
   and Reingold (1991); Spatial Dispatch — the force-scan of Misue, Eades, Lai and Sugiyama (1995); Compression — an
   orthogonal-order-preserving compaction (the same paper). Apply Template Layouts runs the template's layouts again.
   An Apply tool lays its own arguments over the template's values for the layout, which lie over the defaults, as the
   pages say.
6. **Export and subnetwork diagrams.** ExportDiagramContent writes the REST API's Query Diagram Content response
   (`_ndjson`). MakeDiagramLayer's sublayers are feature layers over the three classes, which ExportFeatures and the
   other conversion tools write as feature classes. A subnetwork system diagram is made, stored and kept by
   UpdateSubnetwork for each template of the tier's definition, and refused by Append, Overwrite, Extend, Update and
   Delete, as the pages say.
7. **Names.** Built in `arcpy.nd`, 68 of 74 (14 of them equivalents); in `arcpy.un`, the 62 of its 67 diagram names
   that are the same functions. **Refused, each for its own reason:** CreateDiagramLayerDefinition and
   ExportDiagramLayerDefinition, and the `.ndld` parameters of Add Diagram Template and the definitions tools — a layer
   definition is a diagram map's sublayers and symbology in ArcGIS Pro's application, its format unpublished;
   CreateGeoprocessingModelFromDiagramTemplate — a ModelBuilder model, and grapy has no ModelBuilder; ApplyRotateTreeLayout
   — pivot junctions are set only in Pro's interactive diagram map, so there are none to turn; ApplyRelativeMainlineLayout
   and AddRelativeMainlineLayout — the page describes the parallel-line result but not how lines are ordered, offset,
   branched and compressed, and no published method draws it, so an equivalent would be grapy's invention under Esri's
   name. With them, by parameter: a diagram from a circuit name and a Trace rule's CIRCUIT and `infer_connectivity`
   (the telecom domain network, ADR-231 §5.8), the Trace rule's `path_direction` (trace networks, which grapy does not
   hold), and PROTECTED and PRIVATE access (a file geodatabase's need a portal, and a fenced run has none, ADR-223
   §5.3). No name is refused with a schedule.
8. **Diagram layers and the map.** A diagram layer is a grapy layer naming a diagram, made by CreateDiagram (named
   after the diagram) or MakeDiagramLayer, or a diagram path `<file>.gpkg/<dataset>/<network>/<diagram>`. The *map*
   AppendToDiagram, OverwriteDiagram and CreateDiagram without features read is grapy's layers: their selections stand
   for the map's (§5.9 (g)).
9. **INFERRED, for confirmation** ([Q-200](../open-questions.md)):
   - (a) the storage of §2 A and §5.2 — `grapy_nd_template`, `grapy_nd_diagram`, `grapy_nd_feature` and the three
     `<network>_Diagram*` classes — because Esri's diagram storage is not published;
   - (b) what a diagram feature is (§5.3): one junction per junction feature with all its terminals, one edge per edge
     element, the input's containers brought in with it, an edge's two end junctions brought in with it, a container a
     rectangle of the container margin (default 0.5) round its content and a point without one;
   - (c) the rules' open cases: a removed junction takes its edges with it; a junction of three or more connections is
     reduced onto its one neighbour nearest the subnetwork controllers in hops — or, by digitized direction, the from
     junction of its only incoming edge — and kept when there is no single one, hierarchical and partitioned tiers
     alike; a reduction edge runs from the neighbour its first incoming edge came from; a reduction junction aggregates
     the line and its end junctions but never another reduction junction, and stands at the centre of what it
     aggregates; a container holding content is not reduced; a collapsed linear container drops the edges leading out
     of its content; a hidden expanded container is removed, its content no longer contained; a Start/Stop Iteration
     sequence runs at most 100 times; a Trace rule's result *replaces* the content, its starting points the diagram's
     features (a junction at every terminal, an edge at its middle) unless a Set Starting Point rule or the diagram's
     trace locations give others; a spatial query's CONTAINS holds both ways and a search distance buffers the
     existing feature;
   - (d) a connectivity association or structural attachment drawn as an edge has source id −1, written `Associations`
     in the JSON's `sourceMapping`; a reflexive one is a circle of the reflexive edge radius, 0.5 more per level;
   - (e) the layouts' open cases: the average junction size proportional units are counted in is 1 unit; the first
     root keeps its place and connected parts stand side by side across the tree direction; roots, when none is set,
     are each part's lowest diagram feature id (for "the smallest network topology index"); children are ordered by
     their current place across the tree direction; orthogonal breaks at 50 % unless set, curved edges a cubic Bézier
     of sixteen segments; the radial levels' radii grow by the initial radius times the factor to the level's power;
     a mainline tree alternates branches starting left; the main ring starts east at its lowest id and goes
     counterclockwise toward its lower-id neighbour, its width and height the ellipse's full axes; grid collisions go to
     the nearest free node; linear dispatch's iterative shift moves an equal share of the maximum per iteration and a
     leaf moves with its hub; angle directed moves an edge's to junction; force directed's temperature is 0.1, 0.5 or
     1 times the mean edge length for LOW, MEDIUM, HIGH; spatial dispatch's separation is the junction size times the
     shift factor; compression shortens gaps to the grouping distance; partial overlapping edges within 10° and
     overlapping along their length; square edges break no further than the break point along the longer axis;
     crossing marks default to above the horizontal segment;
   - (f) a layout's content moves with its top container when containers are preserved;
   - (g) a temporary diagram is named `Temporary Diagram`, then `Temporary Diagram 2` …; diagrams from trace locations
     take the starting point, barrier and stopping point classes in that order in `features`; the map is grapy's
     layers' selections; a layer gives its selection, or all it shows when nothing is selected; Append and Update keep
     the positions of features that were there, Overwrite only on a stored diagram; Extend adds features without
     running the rules; sublayers are named `<layer>/Junctions`, `/Edges` and `/Containers`; a subnetwork system
     diagram is named after the subnetwork, `<subnetwork>::<template>` when the tier names several templates; the owner
     is the operating system user;
   - (h) a `.ndbd` file grapy writes is grapy's JSON (`format`, `template`, `steps`), read by grapy, not by Pro; the
     exported content's `userType` is the network class's name (`SystemJunctions`, `Associations`, `Reduction`
     otherwise) and its attribute keys are the fields' aliases, else their names.

## 6. Consequences

- **State.** In the network's GeoPackage. Nothing in the catalogue or the server.
- **No new dependency**: shapely and the standard library.
- **Names.** `tools/grapy-refusals.json` before → after: `arcpy.nd` built 0 → 68 (equivalents 0 → 14), refused 74 →
  6; `arcpy.un` built 39 → 101 (equivalents 2 → 16), refused 85 → 23; all modules built 957 → 1087, refused 1217 →
  1087, of 2174, equivalents 83 → 111; unexamined 0.
- **Negative.** A diagram made by grapy is grapy's: Pro cannot open it, and the equivalent layouts draw correct but
  different pictures from Pro's.

## 7. Assumptions this decision rests on

- A diagram fits in a run's memory and is rebuilt whole by Update, Append and Overwrite. Nothing has been measured.
- The pages' descriptions of the rules determine their result on radial networks; §5.9 (c) matters in meshes.

## 8. Dependencies

[ADR-224](ADR-224-grapy-aims-at-arcpys-whole-buildable-surface.md) §5.5–5.7,
[ADR-231](ADR-231-grapy-holds-utility-networks.md) (the utility network, its topology and traces),
[ADR-223](ADR-223-user-tools-run-sandboxed-on-grapy.md) (the fence and limits),
[ADR-225](ADR-225-grapy-holds-topology-and-relationship-classes.md) (the `gpkg_extensions` registration it reuses);
shapely; clean room (CLAUDE.md §5).

## 9. Revisit triggers

- Esri publishes the `.ndbd` or `.ndld` layout, or a way to set pivot junctions from a tool — the names refused for
  their absence become buildable.
- The owner changes an inference of §5.9, or Pro is found to place a diagram of §4 differently where its page defines
  the arrangement.
- A diagram too large to rebuild in a run: incremental update becomes the step.

## 10. Dissent

None recorded.

## 11. Conditions

1. **Templates, diagrams and their life cycle** against hand-worked answers. **DISCHARGED 2026-10-30**:
   `test_network_diagrams.py` checks the template tools, a diagram's content and geometry, layer and map input, dirty
   areas and nonnetwork input refused, store/alter/owner/delete/purge, consistency and UpdateDiagram, append, overwrite
   and extend, sublayers, the JSON content, template definitions and subnetwork system diagrams.
2. **Every rule against a hand-worked diagram.** **DISCHARGED 2026-10-30**: reduce junction (none, one, two and three
   connections), reduce edge, remove feature with and without its constraints, collapse with
   and without a capability, expand kept and hidden, trace (connected; downstream from a set starting point), spatial
   query, connectivity associations, iteration.
3. **Every built layout against a hand-worked placement.** **DISCHARGED 2026-10-30**: the coordinates of §4.
4. **Every name of `arcpy.nd` and of `arcpy.un`'s diagram tools is built or refused with its own reason.**
   **DISCHARGED 2026-10-30**: `tools/grapy-refusals.json` holds six refused `arcpy.nd` names and five `arcpy.un` ones,
   each with the reason of §5.7, and none unexamined.
5. **The rules and layouts are compared with Pro** on the network of §4, using the owner's Pro install — or the owner
   accepts §5.9 (c) and (e) as grapy's reading.
6. **The owner confirms or changes §5.9** ([Q-200](../open-questions.md)).
