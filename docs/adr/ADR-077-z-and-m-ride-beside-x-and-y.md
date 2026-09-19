# ADR-077 — Z and M ride beside x and y, and a flat geometry pays nothing for it

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `HIGH` |
| **Decided** | 2026-09-16. Step 2 of [ADR-074](ADR-074-z-and-m-ordinates.md) §5, chosen by the owner (*"3B adım 2: geometri modeli"*). **Three semantics were put to the owner and are theirs**: when an operation keeps a vertex it keeps its Z, and a vertex it creates gets Z interpolated along its edge; an operation that builds a new shape — buffer, union, convex hull — returns two dimensions and says so; and **M follows the same rules as Z**. **The representation, the measurements and where the capability stops in this step are this ADR's** |
| **Supersedes** | ADR-074 §5's *re-run the tile benchmark*, whose premise §1 corrects |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

`XySequence` holds coordinates as one interleaved `double[]` — `x0, y0, x1, y1` — because a measurement
said so: a coordinate as an object cost half a z12 tile's allocation. `Point` holds `X` and `Y`. Nothing
in the model can hold an elevation or a measure, which is why every surface drops them (ADR-074).

ADR-074 §5 planned this step as *make `XySequence` stride-aware, then re-run the tile benchmark*. **Both
halves of that sentence were wrong, and measuring before building is what found it:**

- **Stride-aware would have been a silent misreading everywhere.** About a dozen consumers read
  `AsSpan()` directly as `x, y, x, y`. An interleaved `x, y, z` buffer handed to them would draw every
  third number as a coordinate, with no exception anywhere.
- **The tile benchmark does not exercise this type.** Production vector tiles are encoded by PostGIS
  (`PostGisMvtEncoder`, `ST_AsMVT`); the C# tile harness measures experiment code that was never
  promoted. `XySequence` carries load on the **query** path: WKB decoded by `WkbReader`, written by the
  ArcGIS writers.

## 2. Alternatives considered

### Alternative A — Z and M in their own arrays beside the x/y buffer, flat geometries unchanged *(chosen)*

The x/y buffer stays exactly as it is, so every consumer reads what it read. An elevation and a measure
per coordinate live in their own arrays, sharing the view's offset and count, so slicing a ring out of a
polygon slices its Z with it for free.

### Alternative B — interleave with a stride of 2, 3 or 4

Fewer arrays. **Rejected** by §1's first point: every existing `AsSpan()` consumer would misread a 3D
sequence silently, and each would need a stride parameter it could forget.

### Alternative C — a separate 3D geometry hierarchy

No cost to 2D at all, by construction. **Rejected**: every operation, writer and predicate would need a
second overload or a type switch, and the step-3 surfaces would each become a fork.

## 3. Decision — the representation

- **`XySequence` keeps one field for its data.** For a flat sequence it is the `double[]` it always was;
  for one with Z or M it is a small sealed object holding that buffer and the `Z` and `M` arrays. A
  type test on a sealed class is what reading the buffer costs. `AsSpan()` is unchanged; `Ordinates`,
  `HasZ`, `HasM`, `Z(i)`, `M(i)`, `ZSpan()` and `MSpan()` are new; `Slice` carries the extra ordinates;
  equality compares them.
- **`Point.Create(x, y, z, m)` returns a plain `Point` when both are null**, and an instance of a private
  subclass when either is present. `Point` is no longer sealed; `Z`, `M` and `Ordinates` are virtual and
  answer null / none on a flat point.
- **`WkbReader.Read(wkb, keepOrdinates, out dropped)`** reads XYZ, XYM and XYZM in WKB's order when asked,
  and allocates the extra arrays only then. **Every existing caller still passes nothing and still drops
  them** — so no surface changes in this step, and `dropped` still tells a writer the read was lossy.

## 4. Decision — measured, and a flat geometry pays nothing

[`benchmarks/geometry-ordinates/RESULTS.md`](../../benchmarks/geometry-ordinates/RESULTS.md): decode WKB and
write ArcGIS JSON, 50,000 two-ring polygons and 1,000,000 points.

| Build | Polygons | Points |
|---|---|---|
| Before | 53.79 MB | 83.92 MB |
| First version — a second field in `XySequence`, an array on every `Point` | 54.55 MB (+1.4%) | **91.55 MB (+9.1%)** |
| **Chosen** | **53.79 MB** | **83.92 MB** |

**The first version was rejected by its own measurement**: eight bytes on every ring and every point,
nine per cent more allocation on a point layer for a capability no surface serves yet, where allocation is
the binding constraint (A-037). Times overlap the baseline's.

## 5. Decision — the semantics step 3 builds on (owner, 2026-09-16)

1. **A vertex an operation keeps keeps its Z and M.** Reprojection and generalization run in PostGIS,
   which already does this.
2. **A vertex an operation creates gets Z and M interpolated linearly along the edge it lies on** —
   densify, clipping to a rectangle, a split.
3. **An operation that builds a new shape returns two dimensions and says so** — buffer, union,
   intersection, difference, convex hull.
4. **Tiles and rendered images are two-dimensional at their edge.** Mapbox Vector Tiles have no Z, and a
   picture has no elevation; this is the format's fact rather than a choice.

## 6. Conditions

1. **A flat geometry costs nothing measurable.** **DISCHARGED** 2026-09-16 — §4.
2. **The reader keeps XYZ, XYM and XYZM in WKB's order, and still drops them by default.**
   **DISCHARGED** 2026-09-16 — `ZAndMRideBesideXyTests`.
3. **Each step-3 surface that starts returning Z turns `keepOrdinates` on for itself and makes its own
   `hasZ` / `hasM` true in the same change**, with a test that reads a 3D row back through that surface.
   **PARTLY DISCHARGED** 2026-09-16 — ArcGIS `query` in `f=json`, §8, and in `f=pbf` on 2026-09-17, §9;
   `applyEdits` on 2026-09-17, §10, whose test writes a 3D row and reads it back from the column; OGC API
   Features and WFS on 2026-09-19, §11. **DISCHARGED** 2026-09-19 — `AThreeDimensionalRowComesBackWholeTests`
   publishes a `PointZM` row and reads it back through ArcGIS `query`, OGC API Features and WFS GML, and writes
   one through `applyEdits`, on the running server in CI.
4. **The in-process operations follow §5 before any surface feeds them a 3D geometry** — `Densify`
   interpolates, `ConvexHull` returns 2D and reports it. *(Open — step 3. Still not fed one: a filter and
   every GeometryServer operation refuse a geometry that declares Z or M, and an edit's only operations —
   projection and repair — run in PostGIS, §10.)*
5. **A Z layer is edited from a real ArcGIS client and the elevation it sent is what the table holds.**
   *(Open — the writer is tested against PostGIS and the reader against JSON written by hand; what Pro and
   Field Maps actually send, `hasZ` on every geometry or `z` on a point without it, is the claim that matters.)*

## 7. Consequences

- The model can hold elevations and measures; ArcGIS `query` serves them in `f=json` (§8) and `f=pbf`
  (§9), `applyEdits` stores them (§10), OGC API Features and WFS return Z (§11), and since
  [ADR-080](ADR-080-hosted-tables-keep-z-and-m.md) an import keeps them; vector tiles and map images are
  two-dimensional.
- `Point` is unsealed, which a derived class outside Core could now exploit; the subclass that exists is
  private, and nothing constructs points by reflection.
- The plan's next benchmark is the query path's, and the harness for it is in the repository.

## 8. Step 3, first surface — ArcGIS `query` (2026-09-16)

1. **`returnZ` and `returnM` are honoured.** They ask for ordinates rather than switch a surface on:
   `FeatureQuery.KeepOrdinates` carries them to the source, and the reader keeps what was asked and the
   column has — `WkbReader.Read` takes a mask rather than a boolean, so `returnZ` alone on an XYZM column
   returns Z and not M. Nothing asked is byte-for-byte the answer before this step.
2. **The layer document says `hasZ` / `hasM` from the column's declaration.** ADR-074 §3 kept them false
   because they describe what `query` returns; `query` now returns them to a caller who asks, which is
   what a client reads these flags to decide to do. The query answer says `hasZ` / `hasM` beside
   `geometryType` when its features carry them, and each geometry says so on itself — `z` and `m` fields
   on a point, `[x, y, z, m]` vertices elsewhere.
3. **Z survives an output reference and a generalization; M does not survive a generalization, so that
   combination is refused.** Measured on PostGIS 3.4.3 / GEOS 3.11.1: `ST_Transform` keeps Z and M,
   `ST_SnapToGrid` keeps both, `ST_SimplifyPreserveTopology` and `ST_ReducePrecision` keep Z and return no
   M. §5.1 says a kept vertex keeps its measure, so `returnM=true` with `maxAllowableOffset` or
   `geometryPrecision` is refused with the reason rather than answered without the M it claims.
   `AQueryReturnsTheOrdinatesItAskedForTests` holds the Z half against a real column.
4. ~~**`f=pbf` refuses `returnZ` / `returnM` for now.**~~ *(Superseded 2026-09-17 by §9: `f=pbf` answers
   them.)*
5. **A layer with Z or M stops offering `Create`, and with it `Editing`.** A client that reads
   `hasZ: true` sends an elevation with every new feature, which `applyEdits` cannot yet write, and a flat
   shape was already refused by the typed column. `Update` and `Delete` stay for attributes;
   `allowGeometryUpdates` stays false. The `applyEdits` step turns `Create` back on.
6. **The HTML query form offers Return Z and Return M per layer**, as it does `time`.

## 9. Step 3, the same surface in `f=pbf` (2026-09-17)

1. **The header says `hasZ` / `hasM` (fields 10 and 11) from what the column declares and the caller asked
   for**, before any feature, because a reader takes every vertex's stride from them. A geometry short of
   that promise throws rather than writing a short vertex or a zero.
2. **Z and M are written after each vertex's x and y, as integers on their own grid — absolute on every
   vertex, not differences.** **The specification does not say which; this was measured.** The published
   proto defines `mScale`/`zScale` (fields 3 and 4 of `Scale`) and their translations and says nothing about
   how `coords` uses them. One polyline with elevations 110, 120, 130 was written both ways and read by the
   ArcGIS Maps SDK for JavaScript 4.30: the delta-encoded file decoded to 110 on every vertex, the absolute
   one to 110, 120, 130. The writer's own bytes were then read by the same client and came back exact
   (110.25, 120.5, 130.75 and M 11, 12, 13). `Z_is_written_absolute_on_every_vertex` pins it on the bytes,
   because a decoder written beside the writer would agree with either choice.
3. **The Z and M grid is a tenth of a millimetre from zero, whatever `quantizationParameters` says.** That
   parameter's `tolerance` is in the output reference's units, which for a geographic reference are degrees;
   an elevation rounded to one would be none.
4. **A flat answer is byte-for-byte what it was**: no header flags, and a `Scale` / `Translate` with two
   fields.

## 10. Step 3, `applyEdits` (2026-09-17)

1. **An edit's geometry is read with the Z and M it declares.** `hasZ` / `hasM` set the stride of every
   position, and a point's `z` and `m` members are read with or without them. A position with fewer numbers
   than declared, more, or a null among them is refused with the count — a missing Z is not zero metres. An
   unclosed ring is closed with the first vertex's Z and M. **A filter and a GeometryServer operation still
   refuse a declared Z**, because they read x and y and would drop it (condition 4).
2. **WKB carries them.** `WkbWriter` writes ISO type codes plus 1000, 2000 or 3000 and the ordinates after
   each x and y; a flat geometry is the bytes it was.
3. **The writer holds a geometry to what the row stores, in both directions.** An update is compared with
   the row's `ST_Zmflag`; an add, and an update into a row with no geometry, with the column's declared
   type. A flat shape over an elevation is refused, and so is an elevation into a flat row: the first
   discards a stored value, the second a sent one. A column typed as bare `geometry` declares nothing and
   takes what it is sent. **`geometry(Geometry, 3857)` is not bare** — its typmod declares x and y, and
   `postgis_typmod_type` names both `Geometry`; v1.0.115 read the name alone, took the second for the first,
   and CI's datastore job caught PostGIS refusing the Z with 22023. Only `atttypmod < 0` is bare. The refusal is this server's sentence, before PostGIS answers in typmods.
4. **Projection and repair were measured, not assumed** — PostGIS 3.4.3 / GEOS 3.9.0 on CI's image, on a
   branch that ran one SQL file: `ST_Transform`, `ST_Multi` and `ST_GeometryN` keep Z and M; ISO ZM WKB is
   read; **`ST_MakeValid` keeps Z and gives the vertex it creates an elevation interpolated along the edge —
   §5.2 exactly — and returns no M at all.** So an invalid polygon carrying Z is repaired as Q-153 decided,
   and **an invalid polygon carrying M is refused**, with the reason, rather than stored without its measures.
   `AnEditKeepsTheOrdinatesTheRowStoresTests` asserts the measurement too, so a PostGIS that starts keeping M
   says so by failing.
5. **`Create` and `allowGeometryUpdates` are offered on a layer with Z or M again.** §8.5 took `Create` away
   for one release because a client reading `hasZ: true` would send what the writer could not store; it
   can now.
6. **The sentences that said *this server* is two-dimensional say *this path* now** — the shared clause, the
   publish-time note on a registered 3D table, and the shapefile import's warning, whose table is still
   two-dimensional until step 4.

## 11. Step 3, OGC API Features and WFS (2026-09-19)

1. **Both return the elevation the column declares, unasked.** Neither standard has a `returnZ`: a GeoJSON
   position carries an optional altitude (RFC 7946 §3.1.1) and a GML `posList` says its `srsDimension`, and a
   client reads what is there. So the query each builds keeps Z where `StoredOrdinates` has it.
2. **Neither returns M, and that is the format's fact** — §5.4's rule for tiles. RFC 7946 says a position
   SHOULD NOT have more than three elements, and GML has no measure. A layer's M stays in its table and is
   served by the ArcGIS FeatureServer.
3. **The axis swap moves two numbers, not three.** A latitude-first reference writes latitude, longitude,
   elevation, in both formats.
4. **An OGC API Features edit reads the third element as Z** (`GeoJsonGeometry.TryRead(keepZ: true)`), refuses a
   fourth and a geometry whose positions disagree about having one, and meets the writer's rule from §10. **An
   import still drops Z and says so** — hosted tables are two-dimensional until step 4.
5. **The WFS GML reader is a filter's and stays two-dimensional**, as every filter here does (condition 4).
6. **A fixture could not be made through the API**, because no API makes a 3D table yet; `cifree.zz_three_d` is
   SQL beside the other free tables, and `FreeTableAsync` passes over any table whose type declares Z or M,
   because every other caller of it edits flat.

**State.** None.
