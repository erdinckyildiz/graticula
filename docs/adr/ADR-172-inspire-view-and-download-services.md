# ADR-172 — A service may be an INSPIRE View and Download service

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` — the documents validate against INSPIRE's published schemas with and without the settings; the INSPIRE validator has not been run, and only scenario 1 is offered |
| **Decided** | 2026-10-03 by owner decision (*"Yap"*, in answer to the three OGC items left — SLD, WCS, INSPIRE extensions — the last named as a decision about who the product is for) |
| **Supersedes** | — |
| **Superseded by** | — |
| **Amends** | [ADR-167](ADR-167-a-service-s-ogc-documents-describe-it.md) (what a service's own OGC documents state) |

---

## 1. Context

EU public bodies publish their data under INSPIRE. A View service is a WMS 1.3.0 whose capabilities carry
`inspire_vs:ExtendedCapabilities`. A Download service of pre-defined data sets is a WFS 2.0 whose capabilities carry
`inspire_dls:ExtendedCapabilities`, which names the data set. ArcGIS Server ships both as service extensions, set
per service. Without them a European body cannot register a Graticula service in its national INSPIRE geoportal.

## 2. Alternatives considered

### Alternative A — Scenario 1, set per service, on the service's own address (chosen)

- **A service's owner states its settings in Studio** (Settings › General › INSPIRE), the same way as its fees
  (ADR-167):
  - the address of its metadata record in a discovery service (a CSW GetRecordById), required;
  - its language, as ISO 639-2/B, with `eng` as the default;
  - for a feature service, the data set's unique identifier code and namespace.

  `PUT /admin/services/{name}/ogc/inspire` stores them in migration 82's `service.ogc_inspire`. Saving with no
  metadata record clears them.
- **Its own WMS** (`…/MapServer/WMSServer`, `…/ImageServer/WMSServer`, ADR-162) is a View service. The 1.3.0
  document carries `inspire_vs:ExtendedCapabilities` with `inspire_common:MetadataUrl`, `SupportedLanguages` and
  `ResponseLanguage`, and the INSPIRE View schema is in its `schemaLocation`.
- **Its own WFS** (`…/FeatureServer/WFSServer`) is a Download service when a data set code is stated. The 2.0
  document carries `inspire_dls:ExtendedCapabilities` inside `ows:OperationsMetadata/ows:ExtendedCapabilities`, with
  the same three elements and `inspire_dls:SpatialDataSetIdentifier`. With no code it is not declared, because
  INSPIRE's schema requires one.
- **The server's own `/wms` and `/wfs` never carry it.** They describe every service, and an INSPIRE document
  describes one data set's service.
- **Scenario 1 only.** The metadata lives in a catalogue and the document links to it. Scenario 2 would carry the
  whole metadata record in the capabilities. That needs an ISO 19139 editor this server does not have. A national
  geoportal harvests scenario 1.

### Alternative B — Scenario 2, the metadata inline

**Against:** it means writing a metadata editor and its validation for the one document that already has a home in
every member state's catalogue.

### Alternative C — Leave it to a proxy

**Against:** ArcGIS does it in the server, and a body that has to run a rewriting proxy to be compliant will not
choose this server.

## 3. Counterarguments to the preferred option

- *One language.* A multilingual service answers in its one language whatever `LANGUAGE` asks, and says so in
  `ResponseLanguage`. This is permitted, and it is less than a multilingual body wants.
- *WFS 1.1 and WCS carry nothing.* INSPIRE's Download service guidance is written for WFS 2.0. A coverage's Download
  service through WCS is not offered.
- *Nothing checks the metadata record exists.* The address is stored as given, as ArcGIS stores it.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| The settings make the service's own WMS a View service with its record and language, and its own WFS a Download service naming its data set inside `ows:ExtendedCapabilities`; the server's documents say nothing; without a code it is a View service only; a bad language, a namespace without a code and a non-address are refused; clearing removes it | `OgcServiceAddressConformanceTests.A_service_s_inspire_settings_make_its_own_wms_a_view_service_and_its_wfs_a_download_service` | this repository |
| WMS 1.3.0 with the SLD capabilities and INSPIRE View validates against `capabilities_1_3_0.xsd`, `sld_capabilities.xsd` and `inspire_vs.xsd`; WFS 2.0 with INSPIRE Download validates against `wfs.xsd` and `inspire_dls.xsd`; both also validate without the settings | validation against the published schemas, 2026-10-03 | schemas.opengis.net, inspire.ec.europa.eu |

## 5. Decision

As §2, Alternative A.

## Conditions

1. The INSPIRE Reference Validator's View and Download service test suites are run against a service, and what they
   find is fixed or recorded.

## 6. Consequences

**Positive.** A European public body can register a Graticula service in its geoportal without a proxy.

**Negative.** A sixth setting on the General tab, which nearly every owner leaves empty.

**State.** Migration 82: `service.ogc_inspire`, nullable text holding JSON.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | Geoportals harvest scenario 1 | True of the INSPIRE Geoportal and the member-state catalogues read |

## 8. Dependencies

**Depends on:** ADR-162, ADR-167, ADR-168.

**Depended on by:** —

## 9. Revisit triggers

- A body that needs several languages, or scenario 2.
- A coverage that must be an INSPIRE Download service.
