# ADR-181 — The ArcGIS REST API version this server reports is 12.1

| | |
|---|---|
| **Status** | `ACCEPTED` |
| **Confidence** | `MEDIUM` |
| **Decided** | 2026-10-13, by owner decision |
| **Supersedes** | — |
| **Superseded by** | — |

---

## 1. Context

Every ArcGIS REST document this server writes — `/rest/info`, the services directory, FeatureServer,
MapServer, VectorTileServer, ImageServer, GeometryServer — carries `currentVersion`. It was `10.81`, chosen
before any client had been run against the server, with the stated caveat *"nothing has been
conformance-tested against any release."*

The owner saw `Current Version 10.81` on the new `turkiye/istanbul_elevation` ImageServer page on
2026-10-13, asked why, and decided: *"Bence 12.1'e çekelim"* — move it to 12.1. That is a direct
decision, not an inference.

The number is not a label. Clients compare against it to decide what to ask for, so raising it can
make a client attempt operations this server does not serve. This ADR records what was measured
before the number moved.

The portal surface (`/sharing/rest`) reports its own version, `PortalEndpoints.PortalVersion` = `11.2`.
This decision does not touch it: the owner was looking at a server page, and the portal's version is a
different API.

## 2. Alternatives considered

### Alternative A — keep 10.81

**Argument for.** Nothing reported a problem with it, and a lower number makes clients ask for less.

**Argument against.** It is a 2020 release. ArcGIS Online's hosted services report `12` today, and a
GIS administrator reading 10.81 reads a server five years behind. The number was never measured either
way, so keeping it is not the cautious option it looks like.

### Alternative B — 11.x, the version the portal surface claims

**Argument for.** One number across both surfaces.

**Argument against.** The two APIs version separately at Esri too (ArcGIS Online's portal says
`2026.2` while its services say `12`). And it is not what the owner asked for.

### Alternative C — 12.1 (chosen)

**Argument for.** The owner's choice; the current ArcGIS Enterprise release (general availability
May 2026); and the measurement in §4 found nothing in the JavaScript SDK that 12.1 turns on and this
server cannot answer.

**Argument against.** See §3.

## 3. Counterarguments to the preferred option

- **Only one client was read.** ArcGIS Pro, ArcGIS Runtime/Maps SDKs, the Python API and Field Maps
  have their own version gates, and none of them was read or run for this decision. Pro in particular
  is a closed binary; its gates can only be found by running it.
- **A higher number is a larger promise.** Anyone comparing this server to Enterprise 12.1 will expect
  12.1's operations, and several (dynamic layers, version management, utility networks) are absent.
  The capability flags say so honestly, but a human reads the version first.

## 4. Evidence

`@arcgis/core` 5.1.27, the current npm release, was unpacked and every numeric comparison against a
version between 10.81 and 13 was read.

| Claim | Evidence | Source |
|---|---|---|
| 12.1 is a real ArcGIS Enterprise release | General availability announced 2026-05-21 | [VertiGIS support note](https://support.vertigis.com/hc/en-us/articles/36911107283090-VertiGIS-Studio-Products-and-ArcGIS-Enterprise-12-1) |
| Esri's own services report 12 or below today | `services.arcgis.com` → `12`; `elevation.arcgis.com` → `11.5`; `sampleserver6` → `10.91`; `tiles.arcgis.com` → `10.81` | `curl …?f=json`, 2026-10-13 |
| At ≥ 10.91 the SDK sends an ImageServer `identify` / histogram extent as an envelope instead of a polygon | `this.version<10.91&&"extent"===e.geometry?.type&&(e.geometry=…fromExtent(…))` | `@arcgis/core` 5.1.27 |
| That changes nothing here | `computeStatisticsHistograms` answers an envelope (200, 777,600 cells counted); `identify` refuses an envelope *and* a polygon — it takes a point — so the SDK's extent identify failed before and fails after | demo.graticula.com, 2026-10-13 |
| At ≥ 11.1 a MapServer gets `supportsSublayerOrderBy` | `"tile"!==i&&s&&t.currentVersion>=11.1`, where `s` is `supportsDynamicLayers` | `layers/mixins/ArcGISMapService.js` |
| That is not reached here | `MapServerMetadataWriter` writes `supportsDynamicLayers = false` | source |
| The other gates above 10.81 are services this server does not have | version management (`featureServiceVersion<=11.1`), utility network (`_serverVersion<=11.1`), knowledge graph (`>11.2`) | `@arcgis/core` 5.1.27 |

## 5. Decision

Every ArcGIS REST server document reports `currentVersion: 12.1` and `/rest/info` reports
`fullVersion: "12.1.0"`, from the one constant `FeatureServerMetadataWriter.CurrentVersion`. ImageServer
wrote its own literal `10.81` and now reads the constant. The portal surface keeps its own version.

## 6. Consequences

**Positive.** The server reads as current to whoever looks. ImageServer can no longer drift from the
other faces, which it would have done here.

**Negative.** A larger promise to clients that were not measured (§3). One formatting fault surfaced
and is fixed with it: the HTML directory wrote two fixed decimals, which would have read `12.10`.
(A second one was suspected — `fullVersion` formatted in the host's culture — and is not real: the
build sets `InvariantGlobalization`, so no culture but the invariant one can be current.)

**State.** None: the version is a constant in the code, and nothing is stored or held at runtime.

**Ports created.** None.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | ArcGIS clients other than the JavaScript SDK gate no operation this server lacks between 10.81 and 12.1 | **Unverified** — see condition 1 |

## 8. Dependencies

**Depends on** ADR-023 (the services directory), ADR-043 (ImageServer).

**Depended on by** —.

## 9. Revisit triggers

- Any ArcGIS client asks this server for an operation it does not serve and the request traces to a
  version comparison above 10.81.
- A JavaScript SDK release adds a comparison between 10.81 and 12.1 that reaches a face this server has.

## Conditions

1. **ArcGIS Pro is pointed at a 12.1 server and adds a FeatureServer, a MapServer, a VectorTileServer and
   an ImageServer layer without a request the server refuses.** Pro's gates cannot be read; they have to
   be run. **PARTLY DISCHARGED 2026-10-13**, ArcGIS Pro 3.7
   through arcpy against the local fixture over plain HTTP, every request read through a recording relay. All four
   are added and none is broken; FeatureServer, VectorTileServer and — after this run's repair — MapServer draw.
   The run found two refusals that had nothing to do with 12.1: Pro POSTs MapServer's `layers` (404) and sends
   export's `bbox` and identify's `mapExtent` as envelope objects (refused), so Pro had never drawn one of this
   server's map services; both are answered since v1.0.362. **Open:** the ImageServer is added and draws nothing,
   because Pro asks `format=None&pixelType=F32&bsq=true` for the values themselves and is answered with a drawn PNG
   (Q-168), and `/services/rest/info` is a 404 Pro asks on the way in.

## 10. Dissent

None recorded. The concern in §3 was raised with the owner before the change, not after.
