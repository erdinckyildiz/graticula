# ADR-166 — A service's OGC faces are turned off one by one

| | |
|---|---|
| **Status** | `ACCEPTED` |
| **Confidence** | `MEDIUM` — each face is checked against the switch end to end; ArcGIS Manager's vocabulary is followed, not its every option |
| **Decided** | 2026-10-03 — follows from the owner's *"Sonra da OGC tarafına geç"*; it amends ADR-057 §5g, which tied the OGC faces to the feature face |
| **Supersedes** | — |
| **Superseded by** | — |
| **Amends** | [ADR-057](ADR-057-composing-and-publishing-a-service.md) §5g (MapServer and the OGC faces follow the feature face) |


> **Amended 2026-10-03 by [ADR-170](ADR-170-image-services-are-wcs-coverages.md).** WCS is a face now, an image service's, and turns off with the others (migration 81).
---

## 1. Context

ArcGIS Server Manager turns each OGC capability — WMS, WFS, WCS, KML, WMTS, OGC Features — on and off per service.
Here every OGC face followed the feature face (ADR-057 §5g): a service offered WMS, WFS and OGC API Features exactly
when its features were served, and an owner could not publish to ArcGIS clients while keeping a layer out of WFS.

## 2. Alternatives considered

### Alternative A — A list of faces turned off, on the service, set by its owner (chosen)

- Migration 79 adds `service.ogc_off text[]`, empty by default — every face on, as before — constrained to `WMS`,
  `WFS`, `OGCFeatures`, `WMTS` and `KML`. An image service's row is the same table's, so one column serves both.
- `PUT /admin/services/{name}/ogc` with `{"off":[…]}`, by the owner or an administrator, audited; the stewardship
  document says what is off.
- **Each face asks**: WMS and WFS leave the service out of their documents and maps, at `/wms`, `/wfs` and the
  service's own address (ADR-162); OGC API Features leaves its collections out; WMTS (ADR-163) answers 404 in WMTS's
  exception format; `generateKml` (ADR-164) refuses, naming whether KML or the WMS it draws through is off.
- **Studio**: Settings › General has *OGC services*, a switch a face with its address beside it, saved when changed;
  KML is shown as needing WMS while WMS is off; a reader sees the switches and is told who changes them.
- The feature face still gates the OGC faces as before: turning features off turns them off too.

### Alternative B — Keep them tied to the feature face

**Against:** the owner cannot keep a layer off an anonymous WFS while serving it to ArcGIS clients, which is the
first thing an ArcGIS administrator reaches for in Manager.

## 3. Counterarguments to the preferred option

- *An address that answers nothing.* A service with WMS off still has its `WMSServer` address; its document lists no
  layers. ArcGIS answers that address with an error.
- *WCS* is not a face here at all.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| An image service's owner turns WMS, WMTS and KML off: it leaves `/wms`, WMTS answers 404, `generateKml` refuses, the ArcGIS `tile` still answers; an unknown face is refused; on again, it is back in `/wms` | `OgcServiceAddressConformanceTests.An_image_service_is_a_wms_layer_at_its_own_address_and_drawn` | this repository |
| WFS off removes a feature service from WFS and leaves WMS; on again restores it | measured on the fixture, 2026-10-03 | this session |
| The Studio section in both themes, keyboard, a reader's view and a double toggle | ux review 12, two passes | this session |

## 5. Decision

As §2, Alternative A.

## 6. Consequences

**Positive.** Owners choose who reaches a service through which protocol, as in ArcGIS Manager.

**Negative.** One more column every face reads.

**State.** `service.ogc_off`, migration 79.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | No face outside these five reads a service without consulting the list | Checked by grep of the faces' listing paths, 2026-10-03 |

## 8. Dependencies

**Depends on:** ADR-057, ADR-162, ADR-163, ADR-164.

**Depended on by:** —

## 9. Revisit triggers

- WCS or another face added.
