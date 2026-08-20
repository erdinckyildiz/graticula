# Correctness gate 2 — five protocol faces, asked the same questions

**Run 2026-08-20** by an independent reviewer that did not write the code, per §67.
Scope: everything added on 2026-08-19 and 2026-08-20 — WFS 2.0, the ArcGIS portal
surface, WMS 1.3.0/1.1.1 with WMS-T, ArcGIS MapServer, OGC API Features Parts 1 and
2, and the renderer and shared code underneath them.

**Against the running server, never off the page.** Every finding below was
reproduced with live requests at `https://127.0.0.1:8443`; source was read only to
locate the cause of behaviour already observed. That is
[injection-sweep-1](injection-sweep-1.md)'s method and
[wfs-filter-review-1](wfs-filter-review-1.md)'s, and it is the method because D-41
shipped on a comment that said a parameter was parsed when it was not.

## Result

**FAIL. Five defects, all repaired the same day, and every one of them was a wrong
answer with a 200 on it.** Not one raised an error anywhere — which is the class this
gate exists for and the reason a passing test suite did not catch a single one.

**The tool that found four of the five had never been available before today.** The
same layer is now served through five faces. Ask each of them the same question and
compare; where two disagree about one layer, one is wrong and nothing in the server
knows it.

---

## 1. The axis rule knew one code, not one class — CRITICAL

`AxisOrder.IsLatitudeFirst` answered true for EPSG:4326 alone. **EPSG:4258 (ETRS89) —
the standard geographic system across most of Europe — has the identical
authoritative axis order and was written longitude-first**, on WFS and on WMS 1.3.0,
with neither surface restricting requests to a CRS it knows.

```
srsName=urn:ogc:def:crs:EPSG::4326 → <gml:pos>40.00659023009433 32.85857646252386</gml:pos>
srsName=urn:ogc:def:crs:EPSG::4258 → <gml:pos>32.85857646252386 40.00659023009433</gml:pos>
```

On the input side the same, and worse, because the *wrong* order is the one that
matched:

```
BBOX=40.005,32.881,40.007,32.883,…EPSG::4258   (correctly lat-first) → 0 matched
BBOX=32.881,40.005,32.883,40.007,…EPSG::4258   (wrongly lon-first)   → 1 matched
```

**Repaired.** The rule is now the EPSG geographic 2D block, 4000–4999, which covers
4326, 4258, 4269 and 4267. **It is a heuristic and `AxisOrder` says so in its own
remarks**, along with what it still misses: geographic systems numbered outside the
block, and the handful of projected systems the authority defines northing-first.
The general answer is a per-CRS lookup and it is not available — this deployment's
`spatial_ref_sys.srtext` carries no `AXIS` clauses at all, so the database cannot be
asked. [Q-123](../open-questions.md), narrower than it was.

**And the same list was being written five more times.** Three endpoint files and two
metadata writers each carried their own two- or three-code notion of *geographic*, for
scale denominators and unit names. All five now ask `AxisOrder.IsGeographic`.

## 2. A negotiated CRS changed the header and not the coordinates — CRITICAL

OGC API Features Part 2. `CRS84` and `EPSG/0/4326` are the same datum in opposite
orders; asking for the second returned the first's coordinates under the second's
name:

```
…/items?limit=1                          → Content-Crs: <…/OGC/1.3/CRS84>
                                           "coordinates":[32.858576,40.006590]
…/items?limit=1&crs=…/EPSG/0/4326        → Content-Crs: <…/EPSG/0/4326>
                                           "coordinates":[32.858576,40.006590]   ← unchanged
```

`crs=…/EPSG/0/3857` reprojected correctly, so the transformation worked and only the
axis swap was missing. **`OgcNames.SridOf` computes a `latitudeFirst` flag and every
caller deciding the *output* CRS discarded it** — `out _` — while the *input* `bbox`
path used it correctly.

**This is worse than not offering the extension**, because the `crs` conformance
class was claimed: a client trusts `Content-Crs` to know how to read the geometry and
places every point in the wrong hemisphere.

**Repaired.** The flag is carried on the request and through `GeoJsonWriter`, whose
`latitudeFirst` parameter defaults to false and is documented as *plain GeoJSON must
never pass true* — RFC 7946 has one axis order and no way to declare another.

## 3. One layer, two answers about its own appearance — HIGH

Four faces were asked what `hosted/look_parcels` looks like. Three agreed and one did
not:

| Asked | Answer |
|---|---|
| `/FeatureServer/0` (`drawingInfoGenerated: false`) | `[207,227,242,217]` |
| `/MapServer/legend` swatch, decoded | `(207,227,242)` |
| `/wms?…GetMap`, rendered pixels | pale blue |
| **`/MapServer/0`** | **`[204,187,68,115]`** |

`MapServerEndpoints` called the two-argument `FeatureServerMetadataWriter.DrawingInfo`,
which always synthesises an appearance from the layer's name and geometry. The
symbology-aware path existed one file over and was not taken. **Most ArcGIS clients
read this document to build a table of contents and never fetch `/legend`.**

The same document also hardcoded `hasLabels = false` for every layer, including one
whose stored style has a `symbol` layer and whose map does draw names.

**Repaired.** `Drawing` is public now, the layer's stored symbology is passed to it,
and `hasLabels` is asked of the compiled style.

## 4. An instant that exists matched nothing — HIGH

OGC API Features `datetime`, on a layer whose timestamps are exact midnights:

```
datetime=2026-08-10T00:00:00Z                       → 0        (id 9 carries exactly this)
datetime=2026-08-02T00:00:00Z/2026-08-05T00:00:00Z  → 3 of 4   (id 4 is exactly the end)
datetime=2026-08-02T00:00:00Z/2026-08-05T12:00:00Z  → 4        (end off a boundary: correct)
```

**A .NET tick is 100 nanoseconds; PostgreSQL's `timestamptz` resolves to a
microsecond.** The exclusive upper bound was `parsed.AddTicks(1)`, which round-trips
through the database back to `parsed` — so the predicate became `column >= X AND
column < X`, unsatisfiable by construction. Asking a temporal layer for the moment one
of its own rows carries returned nothing, silently.

WMS-T's independent `TimeWindow` parser did not have the bug, which is the positive
control that located it.

**Repaired** — `AddMicroseconds(1)`.

**And a second defect in the same area, found while testing the first.** The
collection document reported no temporal interval at all while `datetime` filtering
worked on it, so a client reading the document concluded the collection had no time
and never sent the parameter. Both faces now take the same measurement from the same
cache.

## 5. A capability promised and missing — HIGH

Every MapServer document claimed `"capabilities":"Map,Query,Data"`. There is no
`/MapServer/{id}/query` route:

```
GET …/MapServer/0?f=json          → "capabilities":"Map,Query,Data"
GET …/MapServer/0/query?…         → 404, empty body
POST …/MapServer/0/query          → 404
```

ADR-041 §5.5 scoped this face to export, identify and legend; the string was simply
untrue. A capabilities string is a machine-readable contract a client checks *before*
it acts.

**Repaired** by making the claim true rather than the route: `"Map"`. Querying the
same data works at `/FeatureServer/{id}/query`.

## 6. Not a defect — MEDIUM, and withdrawn on retest

`MapServer/export` answered 503 for every layer with a stored style, consistently over
several minutes, while the same layers rendered through WMS. The reviewer flagged it
as a lead rather than a certainty and named the possibility: the security gate was
probing the same server concurrently.

**Retested afterwards: 200.** The 503 comes from `BudgetedFeatureSource` when the
connection budget is exhausted, and two agents plus a conformance suite is enough to
exhaust it. **The finding stands as a capacity observation and not as a code defect**,
and the reviewer's own uncertainty is why it did not get repaired as one.

---

## What held, with the method

- **Paging, on three faces and at scale.** WFS `STARTINDEX`/`COUNT` across all 46,041
  rows of `tr_yol` including the tail boundary — 46,041 unique ids, zero duplicates,
  range exactly 1–46,041. ArcGIS `resultOffset`/`resultRecordCount` with
  `exceededTransferLimit` accurate at both ends. OGC `offset`/`limit` likewise, with
  an oversized `limit` honestly clamped and the true offset exposed in `next`.
- **Feature counts agree across faces exactly**: `tr_il` 5,433, `tr_ilce` 25,280,
  `tr_yol` 46,041, `look_parcels` 12 — WFS `hits`, ArcGIS `returnCountOnly` and OGC
  `numberMatched`.
- **Attribute filters agree across faces exactly**: `kod=TR-50` → 87 through a WFS
  `PropertyIsEqualTo`, an ArcGIS `where`, and an OGC property parameter. The Turkish
  `İ`/`ş` case was checked separately after a shell-quoting artefact produced a false
  negative on all three at once.
- **WMS 1.3.0 against 1.1.1 for EPSG:4326** — the one case the old rule got right —
  verified by pixel-exact `GetFeatureInfo` picks that only succeed with each version's
  correct order.
- **`TRANSPARENT` and `BGCOLOR`** decoded pixel by pixel: `0x00FF00` with
  `TRANSPARENT=FALSE` gives exactly `(0,255,0,255)`; `TRANSPARENT=TRUE` gives alpha 0.
- **A bbox matching nothing** returns a valid 200 image on both WMS and MapServer,
  not an error — ADR-041 condition 5, asserted from the outside.
- **WFS GML geometry values match MapServer `identify` and OGC `items`** for the same
  feature, once axis order is accounted for.

## What this gate says about the suite it did not use

**Not one of the five defects was caught by 1,564 passing tests.** Four needed a
question no single-face test can ask — *do two faces agree about one layer* — and the
fifth needed somebody to read a claim in a document and then try it. Both are now
tests: `GateFindingsTests` carries one per finding, and it is deliberately organised
by provenance rather than by subject so the next reader can find them together.
