# ADR-162 — WMS and WFS at a service's own address, and image services in WMS

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` — each address and each layer is checked end to end; no ArcGIS-migrated QGIS project has been pointed at one |
| **Decided** | 2026-10-03 — `INFERRED` from the owner's *"Sonra da OGC tarafına geç"*, after an ArcGIS administrator's review listed these as the first gaps an ArcGIS shop meets. **To be confirmed:** that a service's own address, beside `/wms` and `/wfs`, is wanted |
| **Supersedes** | — |
| **Superseded by** | — |
| **Amends** | [ADR-039](ADR-039-wfs-is-the-first-surface-after-v1.md) §5 (one WFS address for the server), [ADR-041](ADR-041-the-map-renderer.md) (WMS lists vector layers only), [ADR-043](ADR-043-imageserver-and-the-raster-face.md) §3 |


> **Amended 2026-10-03 by [ADR-167](ADR-167-a-service-s-ogc-documents-describe-it.md).** A service's own WMS and WFS documents carry its name, description, tags and the fees and access constraints its owner states.

---

## 1. Context

ArcGIS gives each service its own OGC address — `…/MapServer/WMSServer`, `…/ImageServer/WMSServer`,
`…/MapServer/WFSServer` — and an organisation moving from ArcGIS has those addresses in QGIS projects, portal items
and other systems' configuration. This server answered WMS and WFS at `/wms` and `/wfs` only, every layer in one
document (ADR-039 §5). Its WMS listed only vector layers, though ADR-043 §3 expected raster to follow once the canvas
could draw an image, which it can. And `GetFeatureInfo` answered `text/xml` with JSON.

## 2. Alternatives considered

### Alternative A — The same handlers at each service's address, narrowed; image services as WMS layers (chosen)

- **Addresses.** `/rest/services[/{folder}]/{service}/MapServer/WMSServer` and `…/ImageServer/WMSServer` answer WMS;
  `…/MapServer/WFSServer` and `…/FeatureServer/WFSServer` answer WFS, GET and POST. Each is the handler `/wms` or
  `/wfs` uses, with the layers limited to that service and the capabilities document's operation addresses and title
  naming the service's address. `/wms` and `/wfs` are unchanged and list everything.
- **Image services in WMS.** Each image service the caller may see is a layer named by its service
  (`hosted/elevation`), in its own reference and extent, drawn as its `exportImage` draws it with no parameters —
  stored style, raster function, classes, default slice when multidimensional (ADR-159) — and warped when the map's
  reference differs. Not queryable. Its legend is a dark-to-light ramp. A map may mix image and vector layers, drawn
  in the order named.
- **`text/xml` feature info** is XML in ArcGIS's WMS shape — a `FIELDS` element a feature, its attributes as XML
  attributes — and is listed among the formats.

### Alternative B — Redirect a service's address to `/wms?layers=…`

**Against:** a capabilities document from `/wms` lists every layer, and a client that follows the redirect for
`GetCapabilities` gets the whole server, which is the thing the address exists to avoid.

### Alternative C — Leave the single addresses

**Against:** every ArcGIS-era bookmark breaks, and the reviewer ranked it the first thing an ArcGIS shop meets.

## 3. Counterarguments to the preferred option

- *Several addresses for one layer.* A layer is reachable at `/wms` and at its service's address; a client that
  caches by address caches it twice.
- *An image layer's legend is a generic ramp*, not its style's colours.
- *WMTS, WCS and KML at a service's address* are not added here.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| An uploaded image service is the one layer at its `ImageServer/WMSServer`, is listed at `/wms`, draws inside its extent and nothing outside, and has a legend | `OgcServiceAddressConformanceTests.An_image_service_is_a_wms_layer_at_its_own_address_and_drawn` | this repository |
| A feature service's `FeatureServer/WFSServer` lists fewer types than `/wfs`, all of them `/wfs`'s, and names its own address; its `MapServer/WMSServer` lists its layers | `OgcServiceAddressConformanceTests.A_feature_service_s_own_wfs_and_wms_list_its_layers_only` | this repository |
| `text/xml` feature info is a `FeatureInfoResponse` of `FIELDS` | `WmsConformanceTests.Feature_info_returns_attributes_rather_than_bare_identities` | this repository |

## 5. Decision

As §2, Alternative A.

## Conditions

1. The owner confirms that a service's own OGC address is wanted beside the server-wide ones (`INFERRED`).
2. A QGIS project saved against an ArcGIS Server `WMSServer` address is opened against this one.

## 6. Consequences

**Positive.** ArcGIS-era OGC addresses keep working after a move; image services reach WMS clients.

**Negative.** More routes to keep narrowed alike.

**State.** None.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | Clients that bookmarked an ArcGIS `WMSServer` address use the operation addresses its capabilities document gives | Unvalidated (condition 2) |

## 8. Dependencies

**Depends on:** ADR-039, ADR-041, ADR-043, ADR-159.

**Depended on by:** —

## 9. Revisit triggers

- The owner declining condition 1.
- A WMS client that needs an image layer's own legend.
