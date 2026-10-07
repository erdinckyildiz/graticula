# ADR-163 — An image service answers WMTS over its own tiles

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` — a WMTS tile is checked byte for byte against the ArcGIS tile; no WMTS client has been watched drawing it |
| **Decided** | 2026-10-03 — follows from the owner's *"Sonra da OGC tarafına geç"*; it adds a face to an existing operation and reverses nothing |
| **Supersedes** | — |
| **Superseded by** | — |
| **Amends** | [ADR-097](ADR-097-vector-tiles-through-ogc-api-tiles-tilejson-and-wmts.md) (WMTS served vector tiles only) |

---

## 1. Context

ArcGIS turns WMTS on for a cached map or image service, and QGIS and ArcGIS Pro read it as pictures. This server's
WMTS (ADR-097) served Mapbox vector tiles only, which those clients cannot draw. An image service already served
tiles on its own grid through ArcGIS's `tile` operation.

## 2. Alternatives considered

### Alternative A — WMTS at `…/ImageServer/WMTS` over the service's own grid (chosen)

- `GetCapabilities` (key-value and `…/WMTS/1.0.0/WMTSCapabilities.xml`) describes one layer, the service, on one tile
  matrix set — the service's `TilingScheme`, in its own reference — with WMTS scale denominators (resolution in metres
  over 0.28 mm; a degree counted as 111,319.49 m on a geographic grid), top-left corners in the reference's own axis
  order, and a RESTful template as ArcGIS writes it.
- `GetTile`, key-value or RESTful, is the `tile` operation: same picture, same bytes, same cache.

### Alternative B — WMTS for map services too

**Against, for now:** a map service here is drawn on demand and has no tile grid of its own; giving it one is a
caching decision ADR-093 makes for vector tiles and nobody has made for raster maps.

## 3. Counterarguments to the preferred option

- *One tile matrix set, the service's own.* A client that only reads Web Mercator (`GoogleMapsCompatible`) gets a
  different grid for a service stored in another reference.
- *PNG only.*

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| Capabilities name EPSG:4326 and the RESTful template; a RESTful and a key-value `GetTile` equal the ArcGIS `tile` byte for byte | `OgcServiceAddressConformanceTests.An_image_service_is_a_wms_layer_at_its_own_address_and_drawn` | this repository |

## 5. Decision

As §2, Alternative A.

## Conditions

1. QGIS adds the layer from the capabilities address and draws it over a basemap in the same reference.
   **PARTLY DISCHARGED 2026-10-06.** QGIS 3.28 adds an image service's WMTS from its `WMTSCapabilities.xml` address
   (`default028mm`) and draws it — more than a quarter of 4,096 sampled pixels drawn — after the GetTile refusals and `SECTIONS`
   this ADR's face was missing were added (OGC's WMTS 1.0 suite: 15 failures to 0). It was not drawn over a basemap.
   **DISCHARGED 2026-10-07:** drawn over OpenStreetMap's tiles in EPSG:3857, in a window across the coverage's east
   edge — every sampled pixel has the basemap, and 2,112 of 4,096 are changed by the WMTS layer, the half the coverage
   covers.

## 6. Consequences

**Positive.** Image services reach WMTS clients without a second cache.

**Negative.** Map services still have no raster WMTS.

**State.** None.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | WMTS clients accept a tile matrix set that is not one of OGC's well-known ones | Unvalidated (condition 1) |

## 8. Dependencies

**Depends on:** ADR-043, ADR-097, ADR-162.

**Depended on by:** —

## 9. Revisit triggers

- A client that reads only `GoogleMapsCompatible`.
- A decision to cache map services as raster tiles.
