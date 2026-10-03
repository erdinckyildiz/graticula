# ADR-167 — A service's own OGC documents describe it

| | |
|---|---|
| **Status** | `ACCEPTED` |
| **Confidence** | `MEDIUM` — the documents are checked; ArcGIS Manager's other OGC properties (contact a service, an external capabilities file) are not reproduced |
| **Decided** | 2026-10-03 — follows from the owner's *"Sonra da OGC tarafına geç"* and *"devam et"* |
| **Supersedes** | — |
| **Superseded by** | — |
| **Amends** | [ADR-162](ADR-162-ogc-at-a-service-s-own-address.md) |


> **Amended 2026-10-03 by [ADR-172](ADR-172-inspire-view-and-download-services.md).** A service's own WMS and WFS documents also carry its INSPIRE settings when its owner states them: View service on WMS 1.3.0, Download service on WFS 2.0.
---

## 1. Context

ArcGIS Manager lets a service's owner set what its WMS and WFS capabilities documents say — title, abstract,
keywords, fees, access constraints — and European catalogues harvesting OGC services read exactly those. Here every
document was titled *Graticula*, described the server, and stated no fees or constraints, including at a service's
own address (ADR-162), where the document is about one service.

## 2. Alternatives considered

### Alternative A — At a service's own address, the service's own words (chosen)

- WMS and WFS documents at `…/WMSServer` and `…/WFSServer` are titled with the service's name, take its description as
  the abstract and its tags as keywords — the description and tags its owner already edits in Studio — and state the
  fees and access constraints its owner sets.
- Migration 80 adds `service.ogc_fees` and `service.ogc_access_constraints`, null for none stated;
  `PUT /admin/services/{name}/ogc/terms` sets them, by the owner or an administrator, at most 1,000 characters each.
  Studio's *OGC services* section has the two fields under the switches (ADR-166).
- `/wms` and `/wfs` describe the server, as before. Contact information stays the server's.

### Alternative B — A metadata editor of its own per protocol

**Against:** a second description and second keyword list that drift from the item's own.

## 3. Counterarguments to the preferred option

- *No contact a service*: a catalogue sees the server's contact for every service.
- *No external capabilities file*, which ArcGIS allows for INSPIRE.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| Fees and constraints set on an image service appear in its own WMS document and not in `/wms` | `OgcServiceAddressConformanceTests.An_image_service_is_a_wms_layer_at_its_own_address_and_drawn` | this repository |
| A feature service's own WFS document states them and is titled with its name | measured on the fixture, 2026-10-03 | this session |

## 5. Decision

As §2, Alternative A.

## 6. Consequences

**Positive.** A catalogue harvesting a service's address learns what it is and on what terms.

**Negative.** None worth recording.

**State.** `service.ogc_fees`, `service.ogc_access_constraints`, migration 80.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | An item's description and tags are what its owner would want an OGC catalogue to show | confirmed by the owner, Q-161 |

## 8. Dependencies

**Depends on:** ADR-162, ADR-166.

**Depended on by:** —

## 9. Revisit triggers

- An owner who needs a contact a service, or INSPIRE's external capabilities.
