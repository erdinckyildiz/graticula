# Geometry and CRS Policy

**Status:** FIRST PASS — per-engine behaviour marked `VERIFY` throughout and must
be checked before implementation.
**Required by:** fresh-challenger review G4, a Phase 0 exit item
**Answers:** Q-56. **Raises:** one general rule that applies in five places.

---

## The rule this document keeps arriving at

Five separate problems below turn out to be the same problem:

> **Lossy on read means not writable.**
>
> Any representation that discarded information — Z, M, curve definition,
> coordinate precision, dimensionality — must not be the basis of a write. Either
> the write preserves what the read dropped, or the write is refused.

A client reads a 2D view of 3D data and saves it back: Z is gone. Reads a
linearised curve and saves it: the curve definition is gone. Reads a tile-quantised
geometry and saves it: precision is gone. In each case the data is destroyed
silently by a client doing something reasonable.

This is the geometry equivalent of ADR-005 §3.8's concurrency rule, and it is
just as easy to get wrong.

## 1. Invalid geometry

**The most common real-world GIS problem, and nine ADRs had not mentioned it.**

Every substantial dataset contains self-intersecting rings, unclosed rings,
duplicate consecutive vertices, wrong winding order, zero-area slivers and
bowties.

**Validity is not one concept across our three engines.** `VERIFY`:

| Engine | Behaviour |
|---|---|
| PostGIS | `ST_IsValid` against OGC simple-feature rules; `ST_MakeValid` available |
| SQL Server | `geometry` is lenient and stores invalid shapes; `geography` is stricter and rejects some at insert. Two types, two answers. |
| Oracle | `SDO_GEOM.VALIDATE_GEOMETRY_WITH_CONTEXT`, and validity is **tolerance-dependent** — the same shape can be valid at one tolerance and not another |

So "is this geometry valid" has no provider-independent answer, which means it
cannot be a uniform promise.

**Policy:**

- **Validate at registration, not per request.** A job samples or scans the layer
  and records a validity report. Per-request validation is unaffordable and
  per-request repair changes data silently.
- **Report, never silently repair.** The validity summary appears on the layer
  and in the capability report. An administrator learns about it before a user
  does.
- **The tile path is defensive and fails per feature.** A geometry that breaks
  clipping or simplification is **skipped, counted and logged** — it does not
  fail the tile. One bad polygon must not blank a map.
- **Repair is an explicit administrative action**, on hosted data only, run as a
  job, with before-and-after counts. Never automatic, never on read.

## 2. Wrong or missing SRID

Also extremely common: a table declared 4326 holding projected metres, or an
SRID of 0, -1 or null. The failure is silent — everything works and everything is
in the wrong place.

**Policy:**

- **Detect at registration with a cheap heuristic.** Compare the data's
  coordinate range against the declared CRS's valid domain. Declared 4326 with
  coordinates beyond ±180 / ±90 is wrong, and that check costs one query.
- **Refuse or warn loudly.** Never publish silently over a detected mismatch.
- **Allow an override in the service definition.** On a registered read-only
  source we cannot correct their table, so the definition must be able to say
  *the declared SRID is wrong, treat this as EPSG:x*.

That last point is a concrete instance of **Q-36** — the service definition
describing something the physical table does not. It is the first case where we
genuinely need it.

## 3. Datum transformation selection

Where several transformation paths exist between two CRS, PROJ chooses one, and
the paths differ by metres. For cadastral, survey and engineering work that
difference is legally significant, not cosmetic.

**Policy:**

- **Allow pinning a transformation pipeline** per CRS pair, per layer or
  globally.
- **Record which pipeline was used**, in the capability report and ideally in
  response metadata. A silent default is the problem; a documented default is
  not.
- Default to PROJ's best available when nothing is pinned.

Interacts with Q-15: the grids that make accurate transformation possible must
be present in an air-gapped install, and their absence changes results rather
than producing an error.

## 4. Z and M coordinates

Never mentioned in any ADR. `VERIFY`: MVT is 2D and discards Z; GeoJSON permits a
third position element but has no M; provider-native output can carry both.

**Policy:**

- **Preserve Z on the feature path** where the output format allows it.
- **Tiles are 2D**, stated in the capability report rather than discovered.
- **M is declared unsupported** unless a provider-native path carries it. It is
  rare, poorly supported everywhere, and pretending otherwise costs more than
  admitting it.
- **The write rule applies.** A client that read a 2D representation of 3D data
  cannot write that geometry back. Refuse, or require the client to send the
  full dimensionality.

## 5. Curve geometry

`CircularString`, `CompoundCurve`, `CurvePolygon`. `VERIFY` Oracle and SQL Server
support them, PostGIS partially, and MVT and GeoJSON not at all.

**Policy:**

- **Linearise on output**, with a documented and configurable tolerance.
- **Declare it in the capability report** — a client asking for GeoJSON from a
  curve layer is getting an approximation and should be told.
- **The write rule applies.** A linearised curve must never be written back.
  That would replace an exact circular arc with a polyline and nobody would
  notice until a survey disagreed.

## 6. Oversized single features — Q-56 answered

A national coastline as one polygon. §49's response-size limit refuses the
request, which makes the **layer** partly unusable rather than failing one call.

**Policy — three tiers rather than one limit:**

| Situation | Behaviour |
|---|---|
| Response exceeds the limit because of many features | Paginate, as designed |
| Response exceeds the limit because of **one** feature | Return **that feature alone**, with a warning header. The limit protects the server; a single feature the user explicitly asked for is not an attack. |
| A single feature exceeds even the absolute cap | Refuse **that feature**, with an error identifying it by id — not the query |

Refusing the query because one feature in the layer is large is the wrong
granularity, and it is what the current §49 wording implies.

The tile path is unaffected: clipping handles large geometry naturally.

## 7. Mixed geometry types

A table containing points and polygons. Both GeoJSON and MVT tolerate it; the
layer's *declared* type is what lies.

**Policy:** detect the actual set at registration and declare it honestly. Do not
force a single type, and do not claim one we have not verified.

## 8. Encoding and collation

Oracle NLS settings, Turkish dotless-i, case-insensitive matching that is
case-insensitive differently on each engine. A filter `name = 'İstanbul'` can
match on one provider and not another for reasons unrelated to anything spatial.

**Policy:** this is a **capability**, like every other provider difference.
Declare the collation and case-sensitivity behaviour in the capability report;
do not promise uniform string matching across providers. It is the subtlest
instance of ADR-008 §2's *never degrade silently*, and the easiest to overlook
because it looks like it should just work.

## 9. Coordinate precision

Tile generation quantises coordinates to the tile grid. That is correct and
lossy.

**Policy:** the write rule again. A geometry that arrived via a tile is not a
source for a write. Editing clients must read from the feature path.

## What this changes

- **Q-56 answered** — three tiers, not one limit.
- **Q-36 gains its first concrete requirement** — the SRID override.
- **A new rule for ADR-008 and ADR-005**: lossy on read means not writable,
  enforced by the write path rather than trusted to clients.
- **The capability report grows** — validity summary, dimensionality, curve
  handling, collation behaviour, transformation pipeline.
- **Registration grows** — SRID sanity check, validity scan, geometry type
  detection. All of it job work, which ADR-011 already covers.

## Still open

- `VERIFY` every per-engine claim above. This is written from general knowledge.
- What tolerance for curve linearisation, and is it per layer?
- Does the validity scan sample or scan fully on a hundred-million-row table?
- `experiments/geometry-oracle` should be extended to carry **real** pathological
  data rather than synthetic adversarial data, per G4.
