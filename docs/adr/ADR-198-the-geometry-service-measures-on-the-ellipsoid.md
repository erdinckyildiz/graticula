# ADR-198 — The geometry service measures on the ellipsoid when asked

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `HIGH` for lengths, areas and distances — each checked against WGS 84's known figures; `MEDIUM` for the buffer |
| **Decided** | 2026-10-18, by owner decision — *"İkisini de yeniden aç"*, reopening ADR-145's refusal after the ArcGIS veteran's review (V-90) |
| **Supersedes** | [ADR-145](ADR-145-a-geometry-request-is-answered-in-its-units.md) §2's refusal of `geodesic=true` and of a non-planar `calculationType` for the five operations below |
| **Superseded by** | — |

---

## 1. Context

ADR-145 refused a geodesic request by name rather than answering it planar, and named Alternative B — geodesic
measurement — as *"a week's work … the refusal is honest now and the measurement can follow."* The veteran's review
(V-90) listed what that refusal breaks: `geodesic=true` buffers, `calculationType=geodesic` lengths, `preserveShape`
areas, geodesic distances and a buffer in EPSG:4326 in metres all answer 400, which is what the measurement tools of
Web AppBuilder and the 3.x Maps SDK send. The owner reopened it.

## 2. Decision

**`lengths`, `areasAndLengths`, `distance`, `buffer` and `densify` answer on the ellipsoid** when asked:

- `calculationType=geodesic`, or `geodesic=true`: each edge is the shortest path on the ellipsoid.
- `calculationType=preserveShape`: the shape as drawn in the reference — its edges densified in the reference's own
  plane to a thousandth of its diagonal — measured on the ellipsoid.
- **A linear unit in a geographic reference** (metres asked of degrees), with no `bufferSR` naming a plane to work in:
  answered geodesically, since it can mean nothing else. **`INFERRED`** — ADR-145 refused it, and ArcGIS's reference
  does not say which it does.

**The datastore measures**, through PostGIS's `geography`, which computes with Karney's algorithms (GeographicLib). The
geometry is moved into **its reference's own geographic system**, read from the datastore's definition as the
transformations already do (ADR-160), so the ellipsoid is right and no datum shift enters a measure. The answer is in
the unit asked; without one, in the reference's linear unit, or metres when the reference is geographic. A densify
counts its output from the geodesic lengths before making anything, under the cap planar densify has.

`offset`, `generalize` and the overlay operations stay planar and refuse a geodesic calculation by name, as ArcGIS's
are planar.

## 3. Alternatives and counterarguments

### Alternative A — compute in the server (a GeographicLib port), not the datastore

**For.** No round trip, and the geometry service would not depend on the datastore for a measure. **Against.** A
Tier 2 library behind a port, for what one SQL statement on a pool the service already uses gives; the transformations
made the same choice (ADR-160).

### The buffer is not Esri's

**PostGIS's geography buffer** projects each shape into a plane it picks (a UTM zone, a polar stereographic or a
Lambert azimuthal one), buffers there and returns. Esri's geodesic buffer is a true locus of geodesic distance.
Within one zone the two differ by far less than a drawn line; for a buffer of hundreds of kilometres, or one that
crosses many zones, they differ visibly. Condition 2.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| A degree of longitude on the equator is 111,319.49 m; of latitude, 110,574.39 m; the 1° cell, 12,308.78 km² | the statements this ADR runs, on PostGIS 3.6 | local datastore, 2026-10-18; `A_geodesic_measure_is_the_ellipsoids` |
| preserveShape along the 60th parallel is 558,000 m against 557,469 m for the geodesic | the same | the same |
| Esri's GeometryServer answers metres in EPSG:4326 on the ellipsoid (0.011885°), and its 100 km geodesic buffer is within ≈ 50 m of ours | sampleserver6, `buffer`, compared with `ST_HausdorffDistance` | 2026-10-18 |
| A 1-km buffer at 41° N spans 0.01188° of longitude | the same; `Units_are_converted_…` | the same |

## 5. Consequences

**Positive.** The measurement tools ArcGIS's older clients use answer; an area in EPSG:4326 is in square metres, not
square degrees.

**Negative.** Five operations depend on the datastore being reachable; a measure is a round trip.

**State.** None: each measure is one statement against the datastore, and nothing is kept.

## 6. Conditions

1. **A linear unit in a geographic reference is answered geodesically** — `INFERRED`, for confirmation or for an Esri
   server's answer to the same request. *(Discharged 2026-10-18 by Esri's answer: sampleserver6's GeometryServer buffers
   a point at 41° N by 1,000 m in EPSG:4326 without `geodesic` to 0.011885° of longitude — on the ellipsoid, as this one
   does, 0.011883°.)*
2. **The buffer is compared with an Esri geodesic buffer** of the same point and distance at 100 km, and the difference
   recorded; if it is visible at map scale, the buffer is made by our own locus instead. *(Discharged 2026-10-18: at
   100 km our ring's vertices lie 99,980–100,022 m from the centre, its area is 0.99973 of Esri's and the two rings are at
   most 0.0006° (≈ 50 m) apart; at 500 km, 498,998–500,156 m, 0.99871 and ≈ 1.1 km. At the scale each circle is drawn
   whole, that is 0.05 mm and 0.2 mm on the page: not visible. The buffer stays PostGIS's.)*

## 7. Dependencies

**Depends on:** ADR-022, ADR-145, ADR-160.

## 8. Revisit triggers

A client observed sending `geodesic=true` to `offset` or `generalize`; condition 2's difference visible.

## 9. Dissent

None recorded.
