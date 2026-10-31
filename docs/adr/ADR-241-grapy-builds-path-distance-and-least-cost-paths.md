# ADR-241 — grapy builds path distance, its factor graphs, distance accumulation and least-cost paths

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` |
| **Decided** | 2026-10-31, by owner decision on scope (round 2 of ADR-224's sweep: *"go on until everything is done, then go back to the start and look at the rest once more"*). The 43 names this ADR covers were refused with a reason that a closer reading of the pages shows to be false. The choices the pages leave open are INFERRED and listed for confirmation in §5.6 and [Q-210](../open-questions.md). |
| **Supersedes** | — |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

[ADR-224](ADR-224-grapy-aims-at-arcpys-whole-buildable-surface.md) requires every arcpy name to be built, built as a
cited equivalent, or refused with a reason of its own. Two groups of `arcpy.sa` and `arcpy.ia` names were refused
with reasons written quickly:

- **39 names** — PathDistance, PathAllocation, PathBackLink, DistanceAccumulation, DistanceAllocation and the
  seventeen factor classes (HfBinary, HfForward, HfLinear, HfInverseLinear, HfTable, VfBinary, VfLinear, VfSymLinear,
  VfInverseLinear, VfSymInverseLinear, VfCos, VfSec, VfCosSec, VfSecCos, VfHikingTime, VfBidirHikingTime, VfTable)
  in `arcpy.sa`, and the same seventeen classes in `arcpy.ia` — said *"How the path distance tools work names the
  surface distance and the horizontal and vertical factors without defining them"*.
- **4 names** — CostPath, CostPathAsPolyline, OptimalPathAsRaster and OptimalPathAsLine — said *"Cost Path's page
  does not map the codes to neighbours, define path numbering or settle ties"*.

Both reasons read one page and missed the pages beside it. The first is false: the Spatial Analyst help defines
surface distance, the HRMA and VRMA, and every factor graph with its modifiers and defaults on separate pages. The
second is false too: the back-link codes are mapped on *Understanding path distance analysis*, path numbering is on
Cost Path's own page (and *Creating the least-cost path*), and a path traced over a back-link raster has no ties to
settle.

## 2. Alternatives considered

### Alternative A — build the legacy tools from their formulas, and the surface reconstruction and lattice tracing as cited equivalents (chosen)

**Argument for.** The legacy path distance step cost is written out as a formula, and the factor graphs as formulas,
graphs with defaults and a worked table. The newer Distance Accumulation algorithm is described closely (eight
network estimates, four Eikonal steps, the effective slope) but rests on *"a combination of techniques described in
Sethian (1999) and Zhao (2004)"* that the page does not pin down, which is the case ADR-224 §5.6 makes equivalents
for. The same holds for the lattice tracing of a back-direction raster.

**Argument against.** An equivalent's numbers differ from Pro's; a first-order fast marching method is up to about 5 %
off the straight-line distance on a 41 × 41 grid (§4).

### Alternative B — keep the refusals and rewrite their reasons

**Argument for.** Nothing to maintain; no number that may differ from Pro's.

**Argument against.** The only reasons left would be that Distance Accumulation's implementation is unpublished in
detail, which the equivalent rule answers, and nothing at all for the legacy tools and the factor classes, whose
pages define them. A refusal kept against its own pages is the schedule ADR-224 forbids, in other words.

### Alternative C — answer DistanceAccumulation with the legacy network

**Argument for.** One engine; the legacy formula is exact as written.

**Argument against.** The algorithm page exists to say the network's answer is distorted (A-B'-B instead of AB). An
equivalent should do what the page says the tool does, which is reconstruct the surface.

## 3. Counterarguments to the preferred option

- The VRMA *"resolution of 0.25 degrees"* and the diagonal factor in the path distance formula are read one way here
  (§5.6 h, i); a Pro run could show the other.
- Anisotropic fast marching is not causal in general: with a strong horizontal factor, a cell's height may be
  settled before its true cheapest neighbour. The equivalent solves the Eikonal step three times along its own
  gradient, which narrows but does not remove this.
- The tracing of a back-direction raster follows the page's description of the lattice; where two cells are equally
  close to a crossing point, or the path would turn back on itself, the page says nothing.

## 4. Evidence

Pages read 2026-10-31 under `doc.esri.com/en/arcgis-pro/latest/` (the pro.arcgis.com paths redirect there; the old
`how-the-vertical-factor-affects-path-distance` path answers 404, its content is now
`how-the-horizonal-and-vertical-factors-affect-path-distance`).

| Claim | Evidence | Source |
|---|---|---|
| The step cost is `(Cost(a)·HF(a) + Cost(b)·HF(b))/2 · Surf_dist(ab) · VF(ab)`, ×1.414214 on a diagonal | Formula as text | *How the path distance tools work* |
| Surface distance is the hypotenuse of the planimetric step and the surface rise | Text | *Understanding path distance analysis* |
| With no surface or factors, Path Distance equals Cost Distance | Text; `test_without_factors_it_is_cost_distance` | *Understanding path distance analysis* |
| Back-link codes: 1 right, 2 lower right, 3 south, clockwise; 0 on sources | Text | *Understanding path distance analysis* |
| HRMA, VRMA, every factor graph and its modifiers and defaults | Text and tables | *How the horizontal and vertical factors affect path distance*; *Adjust the encountered distance using a vertical / horizontal factor*; the seventeen arcpy class pages |
| Hiking time is the reciprocal of Tobler's function in hours per metre | The page's sample table matches `1/(6000·exp(−3.5·|tan d + 0.05|))` to every printed digit (0.000198541 at 0°, 2.099409721 at −70°, 2.979204206 at 70°); `test_hiking_time_is_the_pages_table` | *Adjust the encountered distance using a vertical factor*; Tobler 1993 |
| Distance Accumulation reconstructs the surface: 8 network + 4 Eikonal estimates, the minimum kept; effective slope `C·S·M·HF·VF` at the cell | Text | *Distance accumulation algorithm* |
| Corner-connected NoData is thickened into an edge-connected barrier | Text | *Distance Accumulation* usage; *Distance accumulation algorithm* |
| Cost Path numbers the source end 1, paths 3, 4 … in scan order, merged parts 2 | Text | *Cost Path*; *Creating the least-cost path* |
| A back-direction raster is 0 on sources and (0, 360] clockwise from north; paths cross the lattice through the cell centres | Text | *Optimal Path As Raster / Line*; *Distance accumulation algorithm* |
| The equivalent's error: max 5.1 % against the straight-line distance from a centre source on 41 × 41 (the network's is 8.2 %) | `test_a_knights_move_is_nearer_the_straight_line_than_the_network` | measured |

## 5. Decision

1. **The factor classes** (`grapy/_pathdist.py`) are built under arcpy's names and parameters, with the defaults
   the class pages give, as the same classes in `arcpy.sa` and `arcpy.ia`. Each answers its graph's value for an
   array of angles (`value`). A keyword string of the old form (`"BINARY 1 45"`, `"TABLE path"`) is read as the class
   it names.
2. **PathDistance, PathAllocation, PathBackLink** are built on the legacy network: eight-neighbour steps costed by
   the formula of §4, settled cheapest-first. NoData on any input is a barrier. `source_direction` TO_SOURCE reverses
   the moving direction and the VRMA's sign. The other source characteristics are refused by name, as CostDistance
   refuses them (D-332).
3. **DistanceAccumulation, DistanceAllocation** are built as **equivalents**: fast marching (Sethian 1999) with the
   page's eight network and four Eikonal estimates, the effective slope taken at the cell entered and, where it
   depends on direction, solved again along each Eikonal step's own gradient three times. Barriers (raster, feature
   and NoData) are thickened as documented. Initial accumulation, maximum accumulation and cost multiplier are taken
   as numbers or as fields of the source features; `source_direction` as a keyword. GEODESIC is refused by name.
   Back direction, source direction and the two-band source location are written as documented.
4. **CostPath, CostPathAsPolyline** are built: paths traced over a back-link, D8 flow direction or back-direction
   raster as the range rule on Cost Path's page tells them apart, numbered as that page numbers them.
   **OptimalPathAsRaster, OptimalPathAsLine** are built as **equivalents**: slopeline tracing (Douglas 1994) across
   the lattice as *Distance accumulation algorithm* draws it, or cell by cell over a D8 raster. NETWORK_PATHS is
   refused by name.
5. **Counts** against `grapy-surface` at e56366f7: `arcpy.sa` 208 → 234 built (equivalents 21 → 25), 170 → 144
   refused; `arcpy.ia` 206 → 223 built (equivalents 29), 141 → 124 refused; all modules 1,304 → 1,347 built
   (equivalents 156 → 160), 870 → 827 refused, of 2,174. None of the 43 names is refused any more.
6. **INFERRED** (Q-210), where the pages leave a choice open:
   - (a) with a factor raster given and no factor, the Binary graph with its defaults;
   - (b) a factor exactly at a cut angle is finite (*"below which"*, *"beyond which"*);
   - (c) *inverse linear* is `1/(zeroFactor + slope·angle)`, infinite where the line is not positive; a linear line
     below zero is held at 0;
   - (d) HfTable is read as VfTable's page says a table is read (leading negatives skipped, reading stops at the next);
   - (e) a flat horizontal cell (−1) puts up no horizontal friction (HF 1);
   - (f) TO_SOURCE reverses the moving direction for the HRMA and the sign of the VRMA;
   - (g) hiking time is per map unit (metres per unit over the pace per metre); a raster without a coordinate system
     is in metres; a geographic one is refused for it;
   - (h) the 1.414214 in the diagonal path distance formula is the diagonal of the base already inside
     `Surf_dist`, counted once — read otherwise, the flat case would not equal Cost Distance, which the page says it
     does;
   - (i) the legacy VRMA is rounded to the nearest 0.25° (*"The resolution of the VRMAs is 0.25 degrees"*); the
     reconstruction does not round;
   - (j) the reconstruction uses the entered cell's own horizontal factor, not two half steps, since its page takes
     the slope at the cell only; a cost of zero or less is 1e-6; corner barriers are closed through the upper open
     cell; an Eikonal estimate belongs to the cheaper of its two neighbours' sources; the maximum accumulation is
     compared with the accumulated cost including the initial one; source location rows and columns count from 0 at
     the upper left;
   - (k) paths are numbered in row-by-row scan order from the upper left; a zone's start is its cheapest cell, the
     first scanned on a tie; a back direction in CostPath steps to its nearest of the eight neighbours;
     CostPathAsPolyline writes each path whole with `DestID` the destination's value; a one-cell path is no line;
     Optimal Path's default path type is EACH_ZONE (the dialog's first); OptimalPathAsRaster counts each path once
     in every cell its line passes through;
   - (l) of two equally cheap sources, the one settled first wins, as CostDistance already infers.

## 6. Consequences

- **State.** None: the tools read rasters and features and write rasters and feature classes where the user tool
  names them; nothing in the catalogue, nothing held between runs.
- **Positive.** A toolbox that routes a trail, a pipeline or a hiking-time map runs on grapy. Pro's own
  migration path (legacy Path Distance to Distance Accumulation, Cost Path to Optimal Path) runs on both sides.
- **Negative.** The equivalents' numbers differ from Pro's by an amount nobody has measured against Pro. The
  reconstruction and the legacy network are Python loops over a heap: a few hundred thousand cells take seconds to
  minutes, unmeasured (D-332). The legacy source characteristics stay refused.
- **Ports created.** None; numpy, shapely and pyproj only, already grapy's.

## 7. Assumptions this decision rests on

- The doc.esri.com pages of 2026-10-31 describe the current tools (they are the pages pro.arcgis.com redirects to).
- The legacy and the new tools' factor graphs are the same objects (every class page says each is used by both).

## 8. Dependencies

[ADR-223](ADR-223-user-tools-run-sandboxed-on-grapy.md) (user tools run on grapy, under its limits),
[ADR-224](ADR-224-grapy-aims-at-arcpys-whole-buildable-surface.md) (§5.6 equivalents). Clean room (CLAUDE.md §5):
built from the public pages in §4 and the cited papers (Tobler 1993; Sethian 1999; Douglas 1994); no Esri
implementation read.

## 9. Revisit triggers

- A Pro run of the small grids in `test_pathdist.py` that disagrees with an INFERRED choice of §5.6.
- Esri publishes the combination of Sethian's and Zhao's methods its reconstruction uses.
- A user tool's path distance run reaching ADR-223's time limit.

## 10. Dissent

None recorded.

## 11. Conditions

1. **PathDistance and DistanceAccumulation compared with ArcGIS Pro** on `test_pathdist.py`'s grids with a surface, a
   horizontal and a vertical factor: whether the diagonal reading (§5.6 h) and the 0.25° VRMA (§5.6 i) hold, and how
   far the equivalent is from Pro's reconstruction. Needs a Pro session.
2. **The owner answers [Q-210](../open-questions.md).**
