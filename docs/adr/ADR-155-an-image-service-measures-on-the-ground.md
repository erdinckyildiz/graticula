# ADR-155 — An image service measures on the ground, and moves between its cells and the map

| | |
|---|---|
| **Status** | `ACCEPTED` |
| **Confidence** | `MEDIUM` — distances, azimuths and areas are checked against closed-form values; ArcGIS's response shapes are read from its documentation |
| **Decided** | 2026-10-03 by owner decision (*"Tüm maddeleri yap"* — the imagery list's item 24) |
| **Supersedes** | — |
| **Superseded by** | — |
| **Amends** | [ADR-043](ADR-043-imageserver-and-the-raster-face.md) (operations an image service answers) |

---

## 1. Context

ArcGIS Pro and the JS SDK measure on an image service — a point and its height, a distance and its direction, an area
— by its `measure` operation, and move between an image's columns and rows and the map by `computePixelLocation`,
`imageToMap` and `mapToImage`. None was answered.

## 2. Alternatives considered

### Alternative A — On the WGS 84 ellipsoid, whatever the image's reference (chosen)

- **`measure`** projects what it is given to WGS 84 and measures there: Vincenty's distance and initial azimuth, an area
  by spherical excess on the authalic sphere, a perimeter as the sum of its sides. `esriMensurationPoint`,
  `DistanceAndAngle`, `AreaAndPerimeter` and `Centroid`, and their 3D forms on a one-band image, which read the heights
  at the points: `Point3D` gives *z*, `DistanceAndAngle3D` the slant distance, the elevation angle and the height
  difference. Linear, area and angular units as ArcGIS names them. The height operations — from a base and a top, or
  from shadows — need a sensor model, the camera's position, and are refused by name: every image here looks straight
  down. The service names `Mensuration` and `mensurationCapabilities: Basic,3D` (one band) or `Basic`.
- **`computePixelLocation`** answers the column and row of ground points in a catalog image (ADR-152); **`imageToMap`**
  and **`mapToImage`** move a point, a polyline or a polygon between them. This server's images are north-up grids, so
  the move is the grid's own.

### Alternative B — Measure in the image's own reference

**Against:** in Web Mercator a kilometre at 41° N is reported as 1.33 km.

## 3. Counterarguments to the preferred option

- *Spherical excess on the authalic sphere* is within a tenth of a percent of the ellipsoidal area for areas a person
  measures on an image; a country-sized polygon would want Karney's.
- *Uncertainty is null*: there is no sensor model to give one.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| One degree east at 41° N measures 84.0–84.2 km at an azimuth between 89° and 90°; a point's height is the surface's 250; a 0.01° cell at 41° N measures 925,000–945,000 m² with a perimeter of 3,880–3,925 m; a height from a base and a top is refused naming the sensor model; cell 2.5, 2.5 is 30.025° E, 40.975° N and back | `ClassesAndMensurationConformanceTests` | this repository |

## 5. Decision

As §2.

## 6. Consequences

**Positive.** Measuring tools work on imagery as on ArcGIS.

**Negative.** No heights from oblique imagery.

**State.** None.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | ArcGIS's `measure` response is `{name, sensorName, distance{value, displayValue, uncertainty, unit}, …}` | Read from its documentation, not measured against Pro |

## 8. Dependencies

**Depends on:** ADR-043, ADR-152.

**Depended on by:** —

## 9. Revisit triggers

- Oblique or satellite imagery with a sensor model.
