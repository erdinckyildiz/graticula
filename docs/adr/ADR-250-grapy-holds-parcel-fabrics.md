# ADR-250 — grapy holds parcel fabrics

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` for the model, the type and topology tools, records, seeds, building, deleting, selecting, labelling, merging, duplicating, copying, appending, exporting and importing, whose pages state what each does and leave the storage and a few thresholds open (§5.8) · `HIGH` for the least-squares arithmetic on the hand-worked square of the tests, which is the published method, and — since condition 2's measurement on 2026-10-31 — for the names, codes, building, seeds, sequence, gaps and the adjustment's coordinates and corrections, which now answer as Pro 3.7 does (§5.9) · `MEDIUM` for the adjustment's precisions (within 2.5 % of Pro's) · `LOW` where Pro's order could not be pinned (the direction of a shared line, the record of a shared point) and for validation's clustering |
| **Decided** | 2026-10-31, by owner decision *"Tamamını bitirelim artık"*: the last three group-2 modules — `arcpy.parcel` (this ADR), `arcpy.topographic` (ADR-251) and `arcpy.locref` (ADR-252) — are built, and no name of them may stay refused with a schedule; and by [ADR-224](ADR-224-grapy-aims-at-arcpys-whole-buildable-surface.md) §5.7 and its condition 5: each group-2 data model has its ADR before its tools. Every storage choice and every reading of a page that does not say is INFERRED and listed for the owner in §5.8 and [Q-220](../open-questions.md). **Confirmed 2026-10-31 by owner decision in Q-220** (*"Hepsini onayla"*). |
| **Supersedes** | — |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

`arcpy.parcel` has 26 names (`tools/arcpy-surface.json`, read from Pro 3.7 on 2026-10-31). Until this ADR the whole
module was refused by `grapy/__init__.py` as *"the parcel fabric needs a data model this server does not hold yet
(ADR-224 group 2)"* — a schedule, which the owner ruled is not a reason. A toolbox that creates a fabric, loads parcels
and builds them, or checks a fabric for gaps, overlaps and disconnected points, stopped at the first call.

A parcel fabric is a *controller dataset*: one per feature dataset, controlling a records class, a points class, a
connection lines class, three adjustment classes, a polygon and a line class per parcel type, a geodatabase topology
and attribute rules (help topics *Parcel fabric data model* and *Create a parcel fabric*). The pages document the
classes' fields by **alias**, the topology's six rules by name, the attribute rules by behaviour, and each tool's
effect. They do not publish how an enterprise or file geodatabase stores the controller, nor most coded values. grapy
already holds every piece a fabric is made of: feature datasets (a name prefix), topology (ADR-225), editor tracking
and COGO fields (ADR-240), global IDs and subtypes (ADR-228), attribute rules and Arcade (ADR-235), versioning and
archiving (ADR-227).

## 2. Alternatives considered

### Alternative A — the fabric's classes as ordinary grapy classes, the controller as two grapy tables (chosen)

Every controlled class is an ordinary feature class in the fabric's feature dataset, with the documented fields;
which class plays which part is kept in two grapy tables; the topology is ADR-225's with the six documented rules;
the default attribute rules are ADR-235's. **For:** every tool works on what any reader of the GeoPackage can see;
nothing is invented but names. **Against:** the class and field names are INFERRED where the pages give aliases only
(§5.8 (a), (b)), so a toolbox that hard-codes Pro's physical names may differ.

### Alternative B — refuse the module as an Esri data model

**For:** short. **Against:** the pages document the model's behaviour well enough to build 25 of 26 tools; refusing them
for the storage alone is the convenience the owner ruled out.

### Alternative C — store the fabric in Esri's geodatabase layout

**Against:** the controller's storage (its `GDB_Items` definition and internal relationships) is not published, and a
GeoPackage is not a geodatabase (ADR-240 §3).

## 3. Counterarguments to the preferred option

Several pages describe a computation by its purpose only: the least-squares engine is a third-party program run as a
black box; validation *"performs cracking and clustering"*; the sequence *"starts with the line that is closest to
the quadrant"*; a gap's *"width"* is not defined. A grapy answer that differs from Pro there differs silently unless it
says so — which is why those five are equivalents that announce themselves (ADR-224 §5.6) and why condition 2 compares
them with Pro. The layer model is thinner than Pro's: a "parcel fabric layer" with selected parcels is, in grapy, a
layer over one of the fabric's polygon classes (§5.8 (f)).

## 4. Evidence

All pages read on 2026-10-31 at `https://doc.esri.com/en/arcgis-pro/latest/` (Pro 3.7), from the public site only; raw
text kept in the session's scratchpad. `T` = `tool-reference/parcel/`, `H` = `help/data/parcel-editing/`.

| Claim | Evidence | Source |
|---|---|---|
| The 26 tools' Python signatures, parameters, keywords, defaults and derived outputs | Tool pages, all answering 200 | `T/add-parcel-type.html`, `T/analyze-parcels-by-least-squares-adjustment.html`, `T/append-parcels.html`, `T/applyparcelleastsquaresadjustment.html`, `T/buildparcelfabric.html`, `T/copy-parcels.html`, `T/create-parcel-fabric.html`, `T/create-parcel-records.html`, `T/create-parcel-seeds.html`, `T/deleteparcels.html`, `T/disableparceltopology.html`, `T/duplicate-parcels.html`, `T/enable-parcel-topology.html`, `T/export-parcel-record-features.html`, `T/export-sequenced-parcel-features.html`, `T/find-adjacent-parcel-points.html`, `T/find-disconnected-parcel-points.html`, `T/find-gaps-and-overlaps.html`, `T/generate-parcel-fabric-links.html`, `T/import-parcel-points.html`, `T/merge-collinear-parcel-boundaries.html`, `T/remove-parcel-type.html`, `T/select-parcel-features.html`, `T/set-parcel-line-label-position.html`, `T/upgradearcmapparcelfabric.html`, `T/validateparcelfabric.html` |
| The controlled datasets, their fields by alias, the domains by name, the adjustment classes and their statistics | *Parcel fabric data model* | `H/aboutparcelfabricschema.html` |
| One fabric per feature dataset; its spatial reference; associated datasets prefixed with the fabric's name | *Create a parcel fabric*; Create Parcel Fabric | `H/createaparcelfabric.html`, `T/create-parcel-fabric.html` |
| The six default topology rules; user rules stay when disabled | *Work with the geodatabase topology* | `H/aboutparcelfabrictopology.html` |
| PRESERVE, MUST HAVE A RECORD and SHAPE IS FIXED | *Parcel fabric attribute rules* | `H/parcelfabricattributerules.html` |
| Seeds, strata loops, the record of a built line, record polygons and the 2,000-parcel limit | *Create seeds*, *Build parcels from polygons / lines*, *Create parcel fabric records* | `H/createseeds.html`, `H/buildparcelsfrompolygons.html`, `H/buildparcelsfromlines.html`, `H/createparcelfabricrecords.html` |
| What goes into a least-squares adjustment, the defaults 30" and 0.15 m, direction sets per point and record, the 1.96 outlier test, DynAdjust as the engine | *Least squares adjustment*, *Run a least-squares adjustment*, *How points and lines are processed*, *The DynAdjust engine* | `H/least-squares-parcel-fabric.html`, `H/runleastsquaresadjustment.html`, `H/processingpointsandlineslsa.html`, `H/thedynadjustengine.html` |
| Historic features; the parcel fabric versions and the validation error datasets | *Parcel fabric associated layers*, *Parcel fabric versions* | `H/parcelfabricassociatedlayers.html`, `H/parcelfabricversions.html` |
| The published adjustment method used for the equivalent | Ghilani, C. D. and Wolf, P. R., *Adjustment Computations: Spatial Data Analysis*, 5th ed., Wiley 2010, ch. 10–21; DynAdjust user guide, Geoscience Australia (Apache-2.0) | published book; github.com/icsm-au/DynAdjust |
| Buffer and intersection for the gaps and overlaps equivalent | OGC Simple Features Access 1.2.1 (06-103r4) §6.1.15 | opengeospatial.org |

The tests (`src/grapy/tests/test_parcel.py`, `test_parcel_lsa.py`) work every answer out by hand in their comments:
ten lines and eight points from three parcels, the record each shared line takes, a 10,000 m² seed parcel, a gap of
10 m² and an overlap of 5 m², the square whose two drawn-off corners the adjustment puts back exactly, a 0.05 m blunder
that alone fails the 1.96 test, and the clockwise sequence from each corner.

## 5. Decision

1. **Storage** (`grapy/_parcel.py`). A fabric is `<file>.gpkg/<dataset>/<fabric>`; its classes are ordinary feature
   classes in its feature dataset — `<fabric>_Records`, `<fabric>_Points`, `<fabric>_Connections`,
   `<fabric>_AdjustmentLines` (subtype field `MeasurementType`), `<fabric>_AdjustmentPoints`,
   `<fabric>_AdjustmentVectors`, and per parcel type `<type>` and `<type>_Lines` — each with global IDs, editor
   tracking (ADR-240), and its documented fields; the line classes are COGO enabled. Its topology is
   `<fabric>_Topology` (ADR-225). Which class is which is grapy's `grapy_parcel_fabric` and `grapy_parcel_class`,
   registered in `gpkg_extensions` as `grapy_parcel_fabric`. The domains `PF_YesNo`, `PF_COGOType`,
   `PF_LabelPosition`, `PF_AdjustmentConstraint` and `PF_AreaUnits` are made in the GeoPackage and assigned, with
   Pro's codes and field defaults (§5.9); `ExportStatus` is made where ExportParcelRecordFeatures writes. The three default attribute rules are ADR-235 rules on their classes. A class may also be
   named under its fabric's path, as the pages' samples write it (`…/CountyFabric/Tax_Lines`).
2. **Built (20)**, each to its page: AddParcelType, AppendParcels, ApplyParcelLeastSquaresAdjustment,
   BuildParcelFabric, CopyParcels, CreateParcelFabric, CreateParcelRecords (its EXPRESSION through grapy's Arcade,
   ADR-235), CreateParcelSeeds, DeleteParcels, DisableParcelTopology, DuplicateParcels, EnableParcelTopology,
   ExportParcelRecordFeatures, FindAdjacentParcelPoints, FindDisconnectedParcelPoints, ImportParcelFabricPoints,
   MergeCollinearParcelBoundaries, RemoveParcelType, SelectParcelFeatures, SetParcelLineLabelPosition.
3. **The topology.** EnableParcelTopology adds the points, connection lines and every type's lines and topology
   polygons, and the six rules: each type's lines *Endpoint Must Be Covered By* the points, *Must Not Have Dangles*,
   *Must Not Self-Overlap*, *Must Not Self-Intersect*, *Must Be Covered By Boundary Of* the type's polygons, whose
   *Boundary Must Be Covered By* the lines. Disable removes them and the fabric's classes, keeping user rules.
4. **Equivalents (5)**, each announcing itself through `_runtime.equivalent`:
   - **AnalyzeParcelsByLeastSquaresAdjustment** — a parametric least-squares adjustment of distances and direction
     sets in the grid, iterated to the convergence tolerance, the free network held by every point at 10 m, error ellipses,
     standardized residuals and Pope's tau (Ghilani and Wolf; DynAdjust's names), for Esri's DynAdjust run.
   - **ExportSequencedParcelFeatures** — a clockwise walk of the parcel's exterior ring from the line end nearest
     the chosen corner of its envelope; the formats' `#` run as a zero-padded number.
   - **FindGapsAndOverlaps** — pairwise intersections and holes of the union, kept when a negative buffer of half the
     maximum width empties them.
   - **ValidateParcelFabric** — ADR-225's validation (its §5.3 clustering, the rules' predicates), within an extent
     only the features and errors that meet it.
   - **GenerateParcelFabricLinks** — the points' locations at the From and To dates read from the history ADR-227
     keeps for the fabric's points (branch versioned or archived), for the feature service's DEFAULT history the
     page reads.
5. **Refused parameters, each by name** (ADR-224 §5.4): CreateParcelFabric's `config_keyword` (an enterprise
   geodatabase's DBTUNE storage parameters); the DISPLAY extent of BuildParcelFabric, ValidateParcelFabric and
   GenerateParcelFabricLinks (no map display on the server); lines whose `AzimuthType` is 2–5 in the adjustment
   (geodetic azimuths and ellipsoid arc distances; grapy's adjustment is in the grid); a fabric class in a relationship
   class with a table outside the fabric, in CopyParcels and ExportSequencedParcelFeatures (the related table and its
   relationship class would have to be copied with it).
6. **Refused (1)** (`parcel._REFUSED`): **UpgradeArcMapParcelFabric** — it reads an ArcMap parcel fabric, a controller
   dataset only an Esri geodatabase holds (the page lists file, enterprise and mobile ones), and grapy reads none of
   them: its workspaces are GeoPackages, which cannot hold one; a file geodatabase's table format Esri does not
   publish; an enterprise geodatabase is a database server a fenced tool cannot reach (ADR-223 §5.3).
7. **The research's verdicts, checked against the pages.** The research proposed 20 built, 4 equivalents and 2
   refused. GenerateParcelFabricLinks moved from refused to an equivalent: its page says the tool runs only on a
   feature service's DEFAULT version, but what it needs from there is each point's earlier location, which ADR-227's
   history holds for a GeoPackage class — refusing it would have been the convenience the owner ruled out.
8. ~~**INFERRED, for the owner's confirmation**~~ **Confirmed 2026-10-31 by owner decision in Q-220** (*"Hepsini onayla"*) ([Q-220](../open-questions.md)):
   - (a) **names**: the classes `<fabric>_Records`, `<fabric>_Points`, `<fabric>_Connections` (the tool page's word;
     the help says *ConnectionLines*), `<fabric>_Adjustment{Lines,Points,Vectors}`, `<type>` and `<type>_Lines` (the
     samples), the topology `<fabric>_Topology`; the tables `grapy_parcel_fabric` and `grapy_parcel_class`;
   - (b) **field names** from the aliases with the spaces removed (`IsSeed`, `StatedArea`, `StatedAreaUnit`,
     `CalculatedArea`, `MiscloseRatio`, `MiscloseDistance`, `COGOType`, `IsCOGOGround`, `ParentLineID`,
     `DirectionAccuracy`, `DistanceAccuracy`, `LabelPosition`, `AdjustmentConstraint`, `XYAccuracy`, `ValidationStatus`,
     `RecordType`, `RecordedDate`, `RetiredParcelCount`, the adjustment classes' fields, `StandardizedStudentTStatistic`,
     `PelzersReliability`), the documented ones kept (`CreatedByRecord`, `RetiredByRecord`, `IsFixed`, `Preserve`,
     `FloorOrder`, `ParcelCount`); `ArcLength` as ADR-240 names it; the optional COGO Accuracy and `AzimuthType` fields
     are not added but read when a user adds them; `ValidationStatus` is made and left empty, with no domain;
   - (c) **coded values**: `PF_YesNo` 0 No, 1 Yes; `PF_COGOType` 1 Entered (documented), 2 From Geometry, 3 Computed;
     `PF_LabelPosition` 1 Left, 2 Right, 3 Centered; `PF_AdjustmentConstraint` 1 XY free Z constrained, 2 XYZ
     constrained; `PF_AreaUnits` 1–7 (square metres, hectares, square kilometres, square feet, US survey square feet,
     acres, US survey acres); `PF_COGOAccuracy` made with no codes; `ExportStatus` 1 Created By Record, 2 Retired By
     Record, 3 both; `MeasurementType` subtypes 1–6 in the page's order;
   - (d) a new fabric's topology starts **disabled**, since AddParcelType needs it disabled and the pages never tell a
     user to disable it first; EnableParcelTopology turns it on;
   - (e) **topology**: an administrative type's lines take the four line rules (its polygons are out, as documented);
     connection lines take *Endpoint Must Be Covered By* only; xy ranks points 1, lines 2, polygons 3; Disable keeps
     a user rule and the fabric classes it names, as a rule cannot name a class outside its topology;
   - (f) a **"parcel fabric layer" with a selection** is a layer over one of the fabric's classes; with selected
     parcels it gives their lines, points, records and connection lines (Copy, Append, Analyze);
   - (g) **record names** are compared without case in every tool (CreateParcelSeeds documents it);
   - (h) a **record polygon** with no parcels is the envelope of the lines it created or retired (documented for
     connection lines only); seeds are not counted as parcels;
   - (i) a **seed** is a square around the loop's pole of inaccessibility, its half side a tenth of the distance to
     the loop; an existing parcel or seed in the loop suppresses a new one;
   - (j) **building**: a neighbour's corner, an existing point or line end lying on a polygon's edge splits its line;
     two lines are the same when their ends agree within the fabric's xy tolerance; a made point takes its line's
     record; equal records are ranked by object id; an administrative type is built like any other;
   - (k) a **parcel's lines** are the lines of its type covered by its boundary (within the xy tolerance), a historic
     parcel's historic ones and the current ones where none; SelectParcelFeatures' layers are named after the classes;
     a line on no parcel keeps its label position;
   - (l) **merging**: "numerical rounding limits" is one second of arc; lines of different records are not merged;
     the merged line keeps the longer line's other fields; a positive radius curves clockwise; only current lines;
   - (m) **ExportParcelRecordFeatures**: the outputs take the fabric's class names; the status field is
     `ExportedStatus` (alias *Exported Status*); the records output has no status field;
   - (n) **ExportSequencedParcelFeatures**: the outputs take the fabric's class names; a multipart parcel is sequenced
     on its largest part's exterior; the connection lines are those meeting the parcel's points; the record is the
     one that created the parcel; the formats default to `#`;
   - (o) **DuplicateParcels**: `repeat`, `start` and `increment` default to 1; system fields are not copied;
   - (p) **the adjustment**: weighted points are grid coordinate observations with their XY Accuracy, fixed points (Is
     Fixed or XYZ constrained) the same at 5 mm, neither written to AdjustmentLines; a direction set is the lines of one
     record at a point, two at least, its backsight the line of lowest object id; a curve's distance is its chord; Scale
     multiplies a ground distance and Rotation is added to a ground direction; the Standardized Normal Statistic is the
     correction over its own standard deviation (the published w-test) where the page's sentence divides by the
     *Adjusted Standard Deviation*; Student's t is that over the a-posteriori sigma, Pelzer's reliability the
     measurement's standard deviation over the correction's; the error ellipse is scaled by the a-posteriori variance
     factor and XY Uncertainty is 2.4477 times its semi-major axis; a consistency check marks no point for update; the
     correction is adjusted minus measured; Source is the line class's name without its dataset; at most 20 iterations;
     a line without a point at an end is left out with a warning; ApplyParcelLeastSquaresAdjustment reshapes only the
     lines AdjustmentLines names, and polygons follow every moved vertex;
   - (q) **copying and appending**: Copy keeps global IDs, Append gives new ones and remaps the records; records match
     by name; coincident points are within the target's xy tolerance and KEEP_SOURCE updates the target point with the
     source's values; Append drops Parent Line ID; Copy's names default to the input's; an attribute rule is "reading a
     class outside the fabric" when its `FeatureSetByName` names one;
   - (r) **points**: ImportParcelFabricPoints' search distance defaults to the xy tolerance; a source point with more
     than one match is not imported; the conflicts table's fields are `SourceOID` and `TargetOID`; FindAdjacent's
     "closer than" includes the tolerance, so 0 finds stacked points;
   - (s) FindGapsAndOverlaps' output has a `Type` field (Gap or Overlap); it reads current parcels, not seeds;
   - (t) the **default attribute rules**' Arcade scripts and error numbers are grapy's; their errors go to ADR-235's
     `GDB_Validation*Errors` tables rather than `<fabric>_Validation*Errors`;
   - (u) **GenerateParcelFabricLinks**: dates are UTC; each output has `PointID`; a point made after the From date
     gives neither a link nor an anchor.

   Of these, (b), (c), (d), (h), (i), (j), (n), (p) and (s) were changed in part by condition 2's measurement (§5.9);
   (a) held.
9. **Measured beside ArcGIS Pro 3.7 on 2026-10-31** (condition 2; `tools/pro-compare/adr250-parcel.py` builds the
   same fabric in a file geodatabase and in a GeoPackage, `tools/pro-compare/adr250-compare.py` compares the
   adjustments). Where Pro's outputs pin a rule grapy now answers as Pro does:
   - **names and codes** — the eight classes are named as (a) inferred; Pro's physical names replace the alias-derived
     ones where they differ (`EllipseMajor`, `EllipseMinor`, `EllipseDirection`, `MeasurementStdDev`, `AdjustedStdDev`,
     `Residual`, `NormalStatistic`, `StudentStatistic`, `PelzerReliablity` as Pro spells it); `VALIDATIONSTATUS` is on
     polygons, lines and connection lines, not points; `PF_LabelPosition` is 1 Right, 2 Left, 3 Centered;
     `PF_AreaUnits` holds the projection engine's 66 area units, 109401 Hectare to 109466 Square Nanometer; the
     MeasurementType subtypes are 1 Direction Set, 2 Distance, 3 Geodetic Latitude, 4 Geodetic Longitude; a new fabric
     has no `PF_COGOAccuracy` or `ExportStatus`; the defaults are Pro's (Direction Accuracy 30, Distance Accuracy 0.15,
     Label Position 1, Is Seed 0, Adjustment Constraint 1, Measurement Type 1). Every field name, domain code and
     default of the eight classes now agrees with Pro's, but for the attribute-rule domains below;
   - **topology** — a new fabric's topology starts enabled (EnableParcelTopology warns 002007); AddParcelType runs on
     an enabled topology and its classes join it; BuildParcelFabric runs on a disabled one; Enable and Disable in the
     wrong state warn 002007 and 002006 rather than fail;
   - **building** — every polygon vertex, a collinear one too, ends a line and gets a point (the tests' three parcels:
     11 lines and 9 points in both); each parcel's own lines run clockwise round it; a whole-fabric build leaves seeds
     as seeds, and a build named by a record builds that record's; the shared lines' records, the record polygons and
     counts and the label positions agree with Pro;
   - **seeds** — a circle of radius 1 at the loop's centroid (Pro: loops 10, 100 and 1,000 m square and 400 by 20 m,
     area pi in each; grapy's has 64 sides where Pro's is a true arc, area 3.1365); a seed counts in its record's
     Created Parcel Count;
   - **the sequence** — a format's run of `#` is the number and what follows is kept (`ABC-##1` gives ABC-011 …
     ABC-051); the four corners' sequences agree line for line and point for point;
   - **gaps and overlaps** — the field is `GapOverlap`, holding `GAP` or `OVERLAP`, and WITHIN_LAYER names the input in
     `ParcelInput1`; the 10 m² gap and 5 m² overlap of the six boxes agree;
   - **the adjustment** — only Adjustment Constraint XYZ constrained makes a control point (Pro reports "XYZ point
     constraints not found" where only Fixed Shape is set); fewer than two controls is no error, the points then held
     at 10 m as a free network's are; at most 15 iterations; a direction set's backsight is the smallest azimuth and its
     rows, after the backsight's, are the angles from each direction to the next in azimuth order, with sqrt(2) times a
     direction's standard deviation; the degrees of freedom are measurements less unknown coordinates; every point of a
     weighted run is marked for update; the points' standard deviations and ellipses are a-priori, the ellipses at 95 %,
     and XY Uncertainty is the radius of the 95 % circle; Z is reported held at 5 mm. On seven networks of the tests'
     square (in EPSG:3035 at its origin, where Pro's geodetic and grapy's grid distances agree) the adjusted
     coordinates agree to 0.15 mm, every distance correction to 0.06 mm, every angle correction to 0.02", the normal
     statistics to 0.02, the adjusted and correction standard deviations to 2.5 %, the ellipses to 0.6 % and XY
     Uncertainty to 0.5 % (Pro's own approximation of the 95 % circle is not published).

   What still differs, and why: the direction a shared line takes and the record a point between parcels of different
   records takes follow an order of Pro's that is neither object id nor record rank (one line and two points of the
   eleven and nine); Pro also makes the relationship classes `RecordHasConnections`, `RecordHas<type>` and
   `RecordHas<type>Lines`, Z-enabled classes, and the attribute-rule domains (`Validation Status` on
   `VALIDATIONSTATUS`, default 6, and four more) that ADR-235's rules in grapy do not make; Pro writes a weighted point
   as Geodetic Latitude and Longitude rows, its longitude's standard deviation the XY Accuracy times the cosine of the
   latitude; and Pro's adjustment is on the ellipsoid, reducing each distance through the projection, where grapy's is
   in the grid — [D-355](../architecture-debt.md).

## 6. Consequences

- **Counts.** `arcpy.parcel` moves from 0 built and 26 refused (the whole module, as a schedule) to 25 built — of which
  5 are equivalents — and 1 refused, with none unexamined (`tools/grapy_refusals.py` survey, 2026-10-31).
- **State.** Two grapy tables, the fabric's classes, domains, topology and attribute rules in the GeoPackage a tool
  works on; nothing on the server.
- **Negative.** Physical names may differ from Pro's (§5.8 (a)–(c)); the adjustment's statistics and the four other
  equivalents may differ in their numbers; the adjustment is 2D grid only; every edit of a fabric point runs two
  Arcade rules, which makes large point loads slower.
- **Ports created.** None; numpy and shapely, which grapy already uses.

## 7. Assumptions this decision rests on

- The aliases' words, spaces removed, are the physical names where a page gives none — true of every name the pages
  do give (`CreatedByRecord`, `RetiredByRecord`, `ParcelCount`).
- A published least-squares adjustment and DynAdjust agree to numerical precision on the same measurements and
  weights; they differ only in statistics the pages name but do not define.

## 8. Dependencies

[ADR-223](ADR-223-user-tools-run-sandboxed-on-grapy.md) §5.3 (no network),
[ADR-224](ADR-224-grapy-aims-at-arcpys-whole-buildable-surface.md) §5.4, §5.6, §5.7,
[ADR-225](ADR-225-grapy-holds-topology-and-relationship-classes.md) (the topology and its validation),
[ADR-227](ADR-227-grapy-holds-versions-and-archives.md) (the points' history for the links),
[ADR-228](ADR-228-grapy-holds-subtypes-and-attachments.md) (global IDs, subtypes, attachments),
[ADR-235](ADR-235-grapy-holds-attribute-rules-contingent-values-and-replicas.md) (attribute rules, Arcade),
[ADR-240](ADR-240-grapy-holds-editing-behaviours-and-registration.md) (editor tracking, COGO).
Clean room (CLAUDE.md §5): built from the public pages of §4 and a published textbook; no Esri implementation read and
no install folder opened.

## 9. Revisit triggers

- Esri publishes the fabric's physical field names or coded values, or the DynAdjust statistics Pro writes.
- A Pro comparison (condition 2) shows a difference in a built (not equivalent) tool.
- grapy gains a geodetic adjustment (AzimuthType 2–5) or a reader of file geodatabases.

## 10. Dissent

None recorded.

## 11. Conditions

1. **The owner answers [Q-220](../open-questions.md)** — the INFERRED choices of §5.8. **DISCHARGED 2026-10-31** — confirmed as built by owner decision in Q-220 (*"Hepsini onayla"*).
2. **Compared with ArcGIS Pro** on a file geodatabase: the three-parcel build of the tests (lines, points, the record
   of each shared line, record polygons and counts), the physical class and field names and the coded values, a
   seed's shape, the least-squares numbers on the tests' square (corrections, statistics, ellipses), the sequence
   from each corner, and the gaps and overlaps of the tests' six boxes. Needs a Pro session with a Standard licence.
   **PARTLY DISCHARGED 2026-10-31**: Pro 3.7 (ArcInfo) and grapy on every case the condition names
   (`tools/pro-compare/adr250-parcel.py`, `adr250-compare.py`; §5.9): the build gives 11 lines and 9 points in both,
   the shared lines' records, record polygons, counts and label positions agree; every field name, domain code and
   default of the eight classes agrees; the seed is Pro's 1 m circle; the four sequences agree exactly; the gap of
   10 m² and overlap of 5 m² agree; on seven adjustments of the square the coordinates agree to 0.15 mm, the
   corrections to 0.06 mm and 0.02", the precisions to 2.5 %. Changed to follow Pro: (b), (c), (d), (h), (i), (j),
   (n), (p), (s) of §5.8, as §5.9 lists. What remains is the adjustment on the ellipsoid rather than the grid
   ([D-355](../architecture-debt.md)), the relationship classes, Z and attribute-rule domains Pro makes, the weighted
   points' geodetic rows, and the order Pro gives a shared line's direction and a shared point's record, which one
   fabric does not pin.
3. **A large fabric measured**: building and adjusting a fabric of some thousands of parcels, whose time the Arcade
   rules on points and the pure-Python node search in building bear on (ADR-224 §5.4 asks nothing of speed, but a
   toolbox will).
