# Graticula against ArcGIS Enterprise 12.2

**Status:** written 2026-11-21, against v1.0.527 (`2ad90825`). This is a plan input, not a decision.
**Asked by:** the owner, 2026-11-21: *"enterprise 12.2 ile aramızdaki farklar neler. Onları da plana alabilir miyiz?"*
(what separates us from Enterprise 12.2, and can we put it in the plan).
**Answered by owner decision the same day:** band 1 is built now, band 2 goes into the plan, and band 3 is recorded
here and stays out.

ArcGIS Enterprise 12.2 was released in November 2026. ADR-181 makes this server report `currentVersion` 12.1, and
that ADR called 12.1 the current release when it was written. That is no longer true.

## Sources

The list of what is new in 12.2 is taken from Esri's public pages only:

- [What's new in ArcGIS Enterprise 12.2](https://doc.esri.com/en/arcgis-enterprise/latest/introduction/what-s-new-in-arcgis-enterprise.html)
- [Deprecations and retirements](https://doc.esri.com/en/arcgis-enterprise/latest/introduction/deprecations-and-retirements.html)
- [REST services: what's new](https://developers.arcgis.com/rest/services-reference/enterprise/whats-new/)
- [Server administration: what's new](https://developers.arcgis.com/rest/enterprise-administration/server/whatsnew/)
- [Portal administration: what's new](https://developers.arcgis.com/rest/enterprise-administration/portal/portal-admin-whats-new/)
- [Users, groups and items: what's new](https://developers.arcgis.com/rest/users-groups-and-items/whats-new-in-users-groups-and-items/)

Two limits on these sources:

- Esri's 12.2 announcement blog posts refused automated reads (403), so their content is not covered.
- **None of the pages states the `currentVersion` a 12.2 server reports**, so it has to be read from a live 12.2
  server before ADR-181 is amended. No number is assumed here.

The comparison was made by reading code, not by running clients. Each row cites the file or ADR. Where a row says
*not verified*, the cell is a reading of the source rather than a measurement.

## Verdict

Graticula covers this kind of installation: an ArcGIS Server and Portal shop that keeps its own PostGIS and edits
without versioning, and that works with hosted feature layers, Field Maps, publishing from Pro, and web maps and
scenes. Its feature, vector tile, map, image, scene, geometry and print surfaces exist, and so does the portal's item,
group, role and sharing model.

Three kinds of shop are stopped on the first day:

- shops that use a utility network, branch versioning or an enterprise geodatabase (SDE);
- shops that license Pro through the portal;
- shops that host Dashboards or Experience Builder apps in the portal.

Graticula has none of these, and two of them are out of scope by decision (ADR-205 and ADR-285).

Of the new 12.2 feature service parameters, almost none are served today. Two answers were worse than absent, and
both were confirmed in code on 2026-11-21:

- **queryAttachments ignored `returnCountOnly`** and returned the full list where a count was asked for
  (`AttachmentEndpoints.cs`, no reference to the parameter).
- **`gdbVersion` sat in the accepted-and-ignored map** (`FeatureServerQueryParameters.cs:165`), while the comment at
  line 80 of the same file said it was refused. A request for a named version got the default version's answer
  without being told so.

Both are band 1.

## A. What is new in 12.2, item by item

| 12.2 item | Graticula | Evidence |
|---|---|---|
| Service-level `FeatureServer/append`, with a `layers` array in the response | Missing | Only layer-level append is mapped (`ArcGisAppendEndpoints.cs:71-74`) |
| Hosted `supportedAppendFormats` includes `geojson` | Have | `FeatureServerMetadataWriter.cs:1183` |
| Append points from addresses in CSV or Excel | Missing | `PortalEndpoints.ContentAdds.cs:431` refuses any `locationType` other than coordinates. ADR-257's trigger for revisiting this ("when a geocoder is offered") fired with ADR-258 |
| Append to a reference (registered) layer, including Excel, shapefile and JSONL | Missing | `supportsAppend = layer.IsHosted` (`FeatureServerMetadataWriter.cs:1182`) |
| `catalogID` of relationships in a replica's mobile geodatabase | Missing (not verified) | Not found in `Graticula.Offline.Sqlite` |
| Extract Changes with `layerQueries` and `geometry` | ~~Missing~~ **Have, since 2026-11-22 ([ADR-311](../adr/ADR-311-extract-changes-on-hosted-layers-that-keep-history.md))** on a hosted service with `ChangeTracking` on; a point or line `geometry` is refused (D-469) | ~~`extractChanges` is served only for 3D meshes (`AdminEndpoints.Meshes.cs:81`)~~ `OfflineReplicaEndpoints.ExtractChanges.cs` |
| queryAttachments `returnCountOnly` | **Wrong answer** | See the verdict |
| `queryBins` and `lodBin` | Missing | No match in `src` |
| Layer query `outDistance` | **Refused (400)** | Refused as an unknown parameter, so a 12.2 client that sends it gets an error |
| Extract on joined hosted views | Missing | Graticula has no join view (ADR-113 views are single-table) |
| Contingent values and field groups | Have, since 2026-11-22 ([ADR-312](../adr/ADR-312-a-hosted-layer-keeps-and-serves-contingent-values.md)) | Stored on a hosted layer, served by `contingentValues` and `queryContingentValues`, written from Studio and `addToDefinition`; not enforced by `applyEdits`, as in ArcGIS. Not yet in an offline replica (D-472) or a Pro publish (D-473). grapy has them too (ADR-235). Since 2026-11-23 also `f=pbf`, the format Field Maps asks for, in an encoding observed on ArcGIS Online ([ADR-314](../adr/ADR-314-contingent-values-are-served-as-observed.md)) |
| OGC API Features from a hosted layer | Have | ADR-042, ADR-162 |
| Shared templates kept on publish | Partly (not verified) | `templates` in the layer document (ADR-065). Not tried from Pro |
| `GetServiceDatastoreInfo` | Missing | No match in `src` |
| Extract By Mask, Solar Radiation (raster analysis) | Out of scope | ADR-281. Solar radiation exists in grapy (ADR-243) |
| EightyTwentyAnalysis | Missing | Eight built-in analysis tools (`AnalysisEndpoints.cs:544-551`) |
| Utility and trace network changes | Not applicable | There is no utility network service (see B) |
| Linear referencing: Append Calibration Points | Missing on the server | grapy only (ADR-252) |
| Data store Find, service–data store relationships | Missing | — |
| Custom raster functions | Missing | Fixed list of raster functions (`ImageServerEndpoints.cs:117`) |
| MCP endpoints | Out of scope | v1-scope §3d |
| Portal Announcements | Missing | `SiteAnnouncements.cs` is a node-to-node channel (ADR-012 §5), not a notice to users |
| Disaster recovery endpoints (backup stores, restore, validate) | Partly | Take, list, download, delete and schedule (`BackupEndpoints.cs:58-62`). Restore is CLI-only by decision (ADR-193 §5.5). No validate |
| Scheduled tasks (`CreateEnterpriseBackup`, filters) | Partly | Scheduled backups and map area refresh. No portal `tasks` API |
| Collaboration workspaces | Missing | Partnerships are between organisations in one deployment (ADR-219) |
| Export Group Content | Missing | — |
| Map Viewer: forms (basic, choice, attachment elements) | Built 2026-11-22 | The viewer's Forms tool writes `formInfo` and Studio's editor applies it (ADR-313). Graticula's own XLSForm forms are separate (ADR-287) |
| Map Viewer: symbol layer order and lock, move-along-line animation, oriented imagery, charts | Missing | — |
| Map Viewer: time series | Partly | Time slider (ADR-132) |
| Scene Viewer: Gaussian splats with profile and line of sight | Have | `scene.js` (ADR-272, ADR-278) |
| Scene Viewer: glow, Location tool, welcome screen, obscured markers | Missing | — |
| Workforce retired, in favour of Field Maps Tasks | Have (Tasks) | ADR-286 |
| AI settings and privileges, Reality Server, Google Photorealistic 3D, cloud data store, Mission, GeoEvent, Illustrator | Not applicable | Esri products or Esri contracts |

## B. Standing gaps against Enterprise 12.2 as a whole

| Area | Graticula | Where it is recorded |
|---|---|---|
| Utility and trace network service | Missing (grapy has a local model, ADR-231) | No decision for the server side |
| Branch versioning | Missing. Non-default `gdbVersion` was ignored until band 1 | Q-41 open; ADR-205; ADR-217 refuses versioned classes |
| Parcel fabric service | Missing (grapy, ADR-250) | No decision |
| SDE and ST_GEOMETRY | Missing | Out of scope, ADR-205 |
| Raster analytics | Missing | Out of scope, ADR-281 |
| Own locator from customer address data | Missing (Photon/OSM only, ADR-258) | Out of scope, v1-scope §3d, Q-84 |
| Network analysis service | Missing (algorithms in grapy, ADR-239) | No decision |
| Stream services | Missing | No decision. GeoEvent is deprecated (last release 12.3) |
| Knowledge graph, Notebooks, Workflow Manager, Hub | Missing | No decision (`supportsHostedNotebooks = false`) |
| Offline sync | Built: on-demand and preplanned areas in Field Maps, Download Map and Sync in Pro | ADR-288 ~~`REQUIRES PROTOTYPE`~~ `ACCEPTED` 2026-11-22; D-401 repaid |
| Dashboards, Experience Builder, StoryMaps, Instant Apps | Stored, not run | ADR-264 |
| Survey123 field app | Not measured (own XLSForm client, ADR-287) | ADR-287 |
| Map Viewer: Arcade, form authoring, charts | Partly, 2026-11-22 | Form authoring and a clean-room Arcade core in the browser (ADR-313); charts missing |
| HA and multi-machine | ~~Phases 0–1~~ The server tier is built: ADR-012 phases 0–4 (two nodes, shared directories, Caddy, rolling upgrade), all discharged by 2026-11-01. **The store's own availability is missing** — ADR-012 left it "decided separately" | ADR-012, D-391; ADR-319 (store failover, group 2) |
| Backup and DR | Partly, restore is CLI-only | ADR-193 |
| Monitoring | Logs, usage and health. No metrics endpoint found (not verified) | ADR-045, ADR-061, ADR-135 |
| Pro licensing through the portal | Refused | ADR-285 |
| IWA (Windows authentication) | Missing | No decision |
| PKI and client certificates | Missing | ADR-015 names mTLS. It is not built |
| SAML, OIDC, LDAP, 2FA, API keys, multi-tenancy | Have | ADR-088–090, 200, 201, 204 |

## C. The plan

### Band 1: built now (owner decision, 2026-11-21)

These are cheap REST gaps that ArcGIS clients send today. They are built on the branches `gap-query` (ADR-309) and
`gap-append` (ADR-310).

- queryAttachments `returnCountOnly` and its neighbours
- `gdbVersion` refused when a non-default version is named
- `outDistance`
- `queryBins` with `lodBin`
- service-level append
- append to registered layers, and JSONL
- append by address when Photon is configured
- replica `catalogID`
- `GetServiceDatastoreInfo`
- portal Announcements

The last three are built only if a client is shown to call them. **`currentVersion` moves last**, after band 1 lands
and after a live 12.2 server has been read (ADR-181 is amended then, not before).

### Band 2: in the plan, all four groups chosen by the owner

Asked after band 1 started which group comes first, the owner chose **all four** (2026-11-21):
field work and editing, enterprise operations, apps, and analysis services. They are taken in that order,
which is the order they were offered in, and ~~**the order itself is `INFERRED`**, for confirmation~~ **group 1 was started by the owner on 2026-11-22** (*"Başla"*, in answer to a summary that named field work and editing first); ~~the order of groups 2 to 4 is still `INFERRED`~~ **group 2 was started by the owner on 2026-11-23** (*"Devam et"*, in answer to a report that named groups 2–4 as waiting); the order of groups 3 and 4 is still `INFERRED`.

Group 1's open decisions were answered the same day, each with the recommended option: `extractChanges` on hosted layers that keep history, with the webhook's `changesUrl` made real (ADR-311, amending ADR-266); contingent values and field groups stored, served and authored in Studio, not enforced by `applyEdits`, as in ArcGIS (ADR-312); a form designer in Studio that writes `formInfo`, applied by Studio's own editor through a browser Arcade interpreter (ADR-313); and ADR-288 moved out of prototype, with the emulator used to measure D-401.
Group 2's decisions were answered on 2026-11-23: IWA and PKI both, PKI first (*"İkisi de, önce PKI"*, ADR-315, ADR-316); validate and restore through the admin API, reopening ADR-193 §5.5 (*"Validate + API'den restore"*, ADR-317); the store's availability **with automatic failover** (*"Otomatik devralma da"* — the owner chose this over the recommended standby with manual promotion, ADR-319); and D-391 answered by a server that refuses to serve but stays alive, so the restart policy can be `unless-stopped` (*"Reddederken ayakta kal"*, ADR-318, amending ADR-016 §4b).
Each item still names the record it is waiting on. Being chosen does not decide an item. Every item that
reopens an ADR or a scope line gets its own ADR before any code is written.

| Group | Items |
|---|---|
| 1. Field work and editing | offline sync out of prototype, `extractChanges`, contingent values and field groups, Map Viewer forms and Arcade |
| 2. Enterprise operations | IWA and PKI, restore and validate through the API, HA phase 2 |
| 3. Apps | Dashboards and Experience Builder |
| 4. Analysis services | network analysis, stream service, basic raster analysis, own locator |


| Item | Size | Waiting on |
|---|---|---|
| Offline sync out of prototype (ADR-288, D-401, D-394) | L | ~~ADR-288's own conditions~~ **Done 2026-11-22:** all seven discharged, status `ACCEPTED`; D-401's race measured on the emulator and repaid the same day |
| `extractChanges` on hosted and sync-enabled layers | M | ~~A decision. ADR-288 replaced ADR-082 for replicas and said nothing about extractChanges. ADR-266's `changesUrl` depends on it~~ **Decided and built 2026-11-22 — [ADR-311](../adr/ADR-311-extract-changes-on-hosted-layers-that-keep-history.md), owner decision *"Hosted + webhook"*; `changesUrl` is real** |
| Contingent values and field groups on the server | M–L | Decided 2026-11-22 (*"Sakla, sun, Studio'da yaz"*) and built: [ADR-312](../adr/ADR-312-a-hosted-layer-keeps-and-serves-contingent-values.md) |
| Map Viewer form authoring and Arcade | L | A decision. This is the work Field Maps Designer does |
| IWA and PKI sign-in | M | ~~A decision. ADR-015's mTLS is not built~~ Decided 2026-11-23: both, PKI first (ADR-315, ADR-316) |
| Restore and validate through the API, for DR | M | ~~Reopening ADR-193 §5.5, which chose CLI-only on purpose~~ Decided 2026-11-23: reopened (ADR-317) |
| ~~HA phase 2~~ The store's availability, with automatic failover; and D-391 | L | ~~ADR-012, D-391~~ Decided 2026-11-23: ADR-319 (the server tier of ADR-012 was already built, so "phase 2" named the wrong thing); D-391 by ADR-318 |
| Running Dashboards and Experience Builder, or a path that replaces them | L | Reopening ADR-264 |
| Own locator from customer address data | L | Reopening Q-84 and v1-scope §3d |
| Stream service | L | A decision |
| Network analysis service (routing, service areas) | L | A decision. The algorithms are in grapy (ADR-239) |
| Basic raster analysis | L | Reopening ADR-281 |

### Band 3: recorded, stays out

- **Branch versioning (XL):** Q-41, ADR-217.
- **Utility and trace network service (XL).**
- **Parcel fabric service (XL).**
- **SDE and ST_GEOMETRY (XL):** ADR-205.
- **Pro licensing:** ADR-285.
- **Knowledge graph, Workflow Manager, Hub, StoryMaps hosting and Notebooks (XL each):** each is a product of its
  own.
- **MCP (M):** v1-scope §3d keeps it out, although protocol-surface.md calls it cheap. That tension is recorded
  here rather than decided.
- **Not applicable:** Esri's AI, Reality, Google Photorealistic 3D, Mission, GeoEvent and the cloud data store.
