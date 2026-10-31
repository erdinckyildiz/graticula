# ADR-191 — ArcGIS Pro publishes hosted feature layers here

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` |
| **Decided** | 2026-10-17, by owner decision (*"arcgis pro üzerinden hosted feature layer publish etmeyi planlıyorum … copy as feature düşünüyorum"*, then *"başla"*) |
| **Supersedes** | [Q-159](../open-questions.md)'s *"şimdilik hayır"* for this one path; [ADR-040](ADR-040-the-portal-surface-is-how-arcgis-pro-connects.md) §4f and §5 in part |
| **Superseded by** | — |

---

## 1. Context

The ArcGIS gap review of 2026-10-17 ranked *no publishing from Pro* as the first blocker for an ArcGIS shop: every
Pro-centred organisation shares its data with *Share As Web Layer*, and this server served no portal write API.
The owner decided to serve the one path that matters most — **Copy all data → Feature**: the data is copied into
this server's datastore and Graticula serves it as a hosted feature layer.

**What Pro does was measured, not assumed** (dev.graticula.com, 2026-10-17). With publishing advertised behind a
dev-only setting (`Graticula:ProPublishCapture`), arcpy — Pro's own sharing engine, driven by a script because the
Share pane's buttons stayed grey (§9) — staged a service definition and walked the whole sequence, which the
request log and a recorder kept:

1. `GET /sharing/rest/accounts/self/isServiceNameAvailable?name=…&type=Feature Service`.
2. `GET /sharing/rest/search` for a hosted service of that name owned by the caller.
3. `POST content/users/{u}/addItem?async=true&multipart=true&filename=x.sd` (type `Service Definition`), then
   `items/{id}/addPart` (partNum=1, the file), `items/{id}/commit`, `items/{id}/status`.
4. `POST content/users/{u}/publish` with `itemid`, `filetype=serviceDefinition`, `overwrite`,
   `buildInitialCache=false` — no `publishParameters`; the service's name and settings are inside the file.

Before that, Pro exchanges its portal token at the hosting server's **admin** URL (`/admin/generateToken`) and reads
`system/directories` there.

**The service definition, read from the copy Pro uploaded to this server.** A 7z archive (LZMA, not solid) holding
`cd/<name>.gdb` — a **file geodatabase** with the data — `serviceconfiguration.json` (service name, folder,
capabilities, `maxRecordCount`), `servicedescriptions/featureserver/featureserver.json` (the layers),
`p30/<name>.mapx` and `.msd` (the map, with its CIM symbology), `manifest.xml` and `esriinfo/` (item info,
metadata, thumbnail).

## 2. Alternatives considered

### Alternative A — Serve Pro's publish path, importing the service definition's geodatabase (chosen)

**Argument for.** The data arrives as a file geodatabase, which this server already imports (ADR-038) into the
datastore as a hosted service; the new work is the portal calls around it and opening the archive. The owner gets
exactly what they asked for, in the tool their users already work in.

**Argument against.** It reopens the portal's write half (ADR-040 §5), a surface that has to keep up with Pro's
behaviour, and it reads a file format whose layout Esri does not publish.

### Alternative B — Keep publishing in Studio only; tell Pro users to upload a geodatabase

**Argument for.** No new surface, no undocumented format, nothing to keep up with.

**Argument against.** It is the status quo the gap review ranked first, and the owner decided against it.

### Alternative C — Accept Pro's upload but ask for the data in another format

**Argument for.** Avoids the service definition.

**Argument against.** Pro sends what it sends; there is no setting that makes *Share As Web Layer* upload a
GeoPackage. Not available.

## 3. Counterarguments to the preferred option

- **The service definition's layout is undocumented.** It was learned by opening the file Pro sent to this server —
  observing what a client gives us, which [ADR-030](ADR-030-reading-the-reference-implementation.md) and §5 of the
  project rules allow; nothing of Esri's software was opened. The parts read are a 7z container (a public format), a
  file geodatabase (read by GDAL's OpenFileGDB driver, already used here), and two JSON documents whose fields are the
  service's own published REST properties. The `.msd`/`.mapx` are not read in this ADR. **Condition 4** records this
  and keeps it to those parts.
- **A second portal write path besides Studio.** Mitigated by importing through the same pipeline Studio's upload
  uses, so a service published from Pro is the same kind of row, owned and shared the same way.

## 4. Evidence

The request sequence and the archive's contents above were recorded on dev.graticula.com on 2026-10-17 (request
log; the recorder's files under the dev container's capture directory; a local copy of the archive unpacked with 7-Zip).
No implementation has been measured yet; §6's conditions carry what must be.

## 5. Decision

1. **The portal serves Pro's hosted-feature publish path**: `accounts/self/isServiceNameAvailable`, `addItem` (single
   or multipart) with `addPart` and `commit`, item `status`, `publish` with `filetype=serviceDefinition`, and the item
   `share`/`update` Pro sends after it.
2. **A service definition is opened inside the existing archive bounds**, as a 7z container read by a Tier 2 library
   behind this server's own reader, with the same size, member-count and path rules the zip import has; only
   `cd/*.gdb/**`, `serviceconfiguration.json` and `servicedescriptions/**` are read.
3. **Its geodatabase is imported by the hosted-import pipeline Studio's upload uses (ADR-038)**, into a service named
   by `serviceconfiguration.json`'s `serviceName`, owned by the caller, in the hosted folder; `capabilities` sets the
   service's operations and `maxRecordCount` its page size, each only ever narrowing what this server allows.
4. **The portal names this server as its hosting server, and advertises publishing to those who may publish**
   (`portal:user:createItem`, `portal:publisher:publishFeatures`, the sharing privileges, `canSharePublic`) — from the
   member's own role, not from a setting.
5. **Map image, tile and vector tile publishing from Pro, and *Reference registered data*, are not in this ADR.**
   Each answers with a sentence that names it. ~~Overwrite~~ is §5.6, added 2026-10-17 at the owner's request
   (*"Paylaşım seviyesi ve overwrite"*).
6. **Overwrite Web Layer replaces the data and keeps the service.** `publish` with `overwrite=true` on a hosted service
   the caller owns or administers replaces each layer's rows with the geodatabase layer of the same name — through
   ADR-103's overwrite (`PostGisImporter.AppendAsync` with replace, one transaction per layer), with its guards:
   protection, views, the lock wait, a value that does not fit. **The service's id, URL, layer indexes, sharing and
   groups stay**, and object ids keep counting. It is **not** unpublish-and-republish, because a layer index is never
   reused here and a republished service would be a different service to every map naming it. **A changed set of
   layers is refused** with nothing written (ADR-103 §5.4 keeps a schema change out of an overwrite); every layer is
   read before any is written. *A column the new data adds is not written, and is named in the audit record*, as
   Update data does — Pro is not told, which is §6's new negative.

   Measured on dev 2026-10-17 with arcpy: Pro reads a `publish` answer without a `jobId` as a failure and **deletes
   the service item the answer named** — its rollback — so an overwrite answers with a job id that `status` reports
   completed. After it, Pro sends `content/updateItems` (the item's categories, empty) and `shareItems` with the
   draft's level; `groups=false` there means none.
7. **The map's drawing comes with the data**, on a publish and on an overwrite — added 2026-10-17 at the owner's
   question (*"Semboloji de gidiyor mu?"*; it did not). `servicedescriptions/featureserver/featureserver.json`, already
   inside §5.2's reading, holds each layer's REST document under `resources[name=layers].resources[].contents`, with
   its ArcGIS `drawingInfo`; that is stored as the layer's symbology through the same conversion Studio uses for a
   pasted one (`SymbologyConversion.Read`, to CIM — ADR-052). A second copy with CIM symbols under
   `resourceInfo.advancedDrawingInfos` is not read, and neither are the `.mapx`/`.msd`, so condition 4 is unchanged. A
   drawing this server cannot read leaves the generated appearance and the share goes on; its losses are not yet
   reported to anyone. Measured with arcpy on dev: the point symbol arrived exactly (`esriSMS`, colour
   `[130,95,31]`, size 4, outline 0.7), `drawingInfoGenerated` false. The item's pop-up
   (`featureserveritemdata.json`) is not read yet.
   **The item's summary and tags come with it** — added 2026-10-18, measured on dev: Studio's item page said *No
   description yet* over a summary and two tags typed in Pro's pane. **They are not on the wire**: the dev wire log of
   that publish shows `addItem` and `addPart` with no such fields, `commit` with only `title` and `type`, `publish` with
   `itemid`, `filetype`, `buildInitialCache` and `overwrite`, and no item update after it. Pro writes them into the
   service definition's `esriinfo/iteminfo.xml` (`snippet`, `summary`, `description`, `tags`), so **that file joins
   condition 4's list — owner decision 2026-10-18**, asked in those terms (*"Evet, ekle"*). It is item information, not
   the map's symbols the condition keeps out, and it is read with no DTD, no resolver and a one-megabyte bound. The
   summary (`snippet`, else `summary`, else `description`) is stored as the service's description and the
   comma-separated tags as its tags, which Studio's item page reads; an overwrite writes only what the pane held.
   **The drawing on an overwrite** was paired by the geodatabase's dataset name, which a lone layer's drawing — keyed by
   Pro's map-layer name — never matched, so an overwrite dropped the map's symbology; it is now paired as the publish
   pairs it. **The layer is called what Pro's map called it** — owner decision 2026-10-18, *"Eklenen her bir katman kendi
   adı ile gelmeli... Pro'da TOC'taki isim olmalı. ArcGIS öyle değil mi?"*; the service stays `Turkey_Provinces`. The
   same pairing sets the layer's **title** (ADR-179) to `contents.name` (*Provinces by area*, over the dataset
   `tur_admin1`), on a publish and on an overwrite, and the ArcGIS faces call a layer by its title when it has one
   (`PublishedLayer.DisplayName`: the service and layer documents, the layer's template, MapServer's, identify and find).
   This takes the revisit trigger ADR-179 §9 named. **A name is a label there and an address in this server's own
   clients**, and a first attempt that only renamed the documents was taken back the same day: Studio asked
   `/admin/layers/Provinces%20by%20area/symbology` and got 404. So the documents keep the catalogue name beside the
   label, as `catalogName` on each layer entry of the service document and on the layer document — an extra property
   ArcGIS clients ignore — and Studio and the web map editor address the layer by it. Resolving `/admin/layers/{x}` by
   title instead was rejected: titles are free text, not unique, and D-109 already says names are not. **Every layer
   with a title is named by it on the ArcGIS faces, not only Pro's** — the CITE fixture's `cite:*` layers among them;
   asked of the owner in those terms and released on their word the same day (*"maine çık"*).
8. **Publishing from Pro is on by default** — owner decision 2026-10-17, *"bence bunu main'e artık geçirebiliriz"*.
   `Graticula:PublishFromPro` (default `true`) carries the portal's `/studio` and `/portal` addresses, ~~the Enterprise
   version pair~~ *(one pair whatever publishing says since 2026-10-19: Enterprise 12.1's — ADR-181 §5)*, ~~the hosting server under `/arcgis`~~ the hosting server's admin API under `/arcgis/admin` *(the rest
   of `/arcgis`, and `/server/rest`, answer always since 2026-10-17 —
   [ADR-194](ADR-194-an-arcgis-shop-s-addresses-answer.md))*, the publish and share routes and the privileges; a deployment
   sets it to `false` to keep the read-only portal of ADR-040 §5. Recording Pro's calls —
   the wire log, the unknown-call recorder and the share forms — stays on `Graticula:ProPublishCapture`, which only
   dev.graticula.com sets: it is a measuring tool, and it once wrote a sign-in's password in clear.
9. **Share As Web Map keeps the map** — 2026-10-17, from Pro's own `SharingJobLog.log` read by another session: the
   layers published, then *"Failed to add web item"*, because `addItem` took only a service definition. A
   `type=Web Map` `addItem` now stores its `text` as a Studio web map (ADR-079) — the caller's, private, with its
   title, snippet, tags, description and PNG thumbnail, in the folder of the path when it is the caller's — and
   answers `{success, id, folder}`; `share`/`shareItems` reach a map as they reach a service, and the user's item route
   reads it back. Adding a web map needs `content:create`, not the publishing privilege. **Not yet driven through Pro's
   UI**: measured by `ShareAsWebMapTests` against the wire shape the trace recorded.

## 6. Consequences

**Positive.** Pro's normal sharing workflow reaches this server. The owner's demonstration — publish from Pro, see it
in Studio and on the map — becomes possible.

**Negative.** A write surface that follows Pro's behaviour; an undocumented container read on the server; the portal
says *hosting server* again, which ADR-040 §4f withheld. An overwrite whose data gained a column drops that column
without telling Pro (§5.6). A service with several layers is overwritten one transaction per layer, so a failure
part-way leaves the earlier layers replaced.

**Ports created.** An archive reader for 7z behind the existing archive port, so the library's types stay out of Tier 1.

**State.** Staged uploads (the service definition, until it is published or expires). No new catalogue table is
expected; a published service is the same row Studio's upload makes.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | Pro's sequence above is the same for larger data (several `addPart`s) and for Pro's Share pane as for arcpy | `INFERRED` — measured with arcpy and one part |
| — | The geodatabase inside a service definition is always under `cd/` and is a complete copy of the shared layers | `INFERRED` — one archive read |

## 8. Dependencies

**Depends on:** [ADR-038](ADR-038-how-a-geodatabase-becomes-a-service.md), [ADR-040](ADR-040-the-portal-surface-is-how-arcgis-pro-connects.md),
[ADR-024](ADR-024-shapefile-import.md) (archive bounds), [ADR-030](ADR-030-reading-the-reference-implementation.md).

**Depended on by:** —

## 9. Revisit triggers

- Pro's Share pane keeps Analyze and Publish grey while arcpy publishes: the pane checks something the script does
  not (observed 2026-10-17 — the pane asks the portal's root address dozens of times and fails reading the server's
  cache directory). Condition 2.
- A service definition whose data is not a file geodatabase under `cd/`.

## 10. Dissent

None recorded.

## 11. Conditions

1. **Published from Pro and drawn.** A layer shared from Pro with *Copy all data → Feature* appears in Studio as a
   hosted feature layer, owned by the caller, and draws on the map — on dev, then on the showcase.
2. **The Share pane itself works**, not only arcpy: Analyze and Publish enabled in Pro's UI. *(Met on dev
   2026-10-17, driven through Pro's UI: a polygon layer published from Share As Web Layer → "Web layer published
   successfully", and `hosted/Map` answers it. **Two causes were found.** (a) Pro greys the pane with *"Your portal
   does not support publishing"* when the portal URL has no Web Adaptor name — Esri's article 000031246 names the
   format `https://<host>/<webadaptorname>`, and this portal lived at the root. The portal now answers under
   **`/studio`** as well (the owner's suggestion: Studio is this product's portal), with `customBaseUrl` and
   `portalHostname` naming it, while `/studio/` itself stays Studio's page. (b) Pro's analyzer then refused the data
   as newer than the server (*"Minimum version: 11.2"*) until the portal said a real Enterprise's version pair,
   `currentVersion "2025.1"` with `enterpriseVersion "11.5.0"` — measured against a public 11.5 portal. Both were
   behind the capture setting until §5.8 made publishing the default.)*
3. **The archive is bounded**: an over-size, over-member or path-escaping service definition is refused before
   anything is written, with a test for each.
   **DISCHARGED 2026-10-31.** `ServiceDefinitionArchiveTests` opens 7z archives written by 7-Zip
   (`tests/Graticula.Host.Tests/ServiceDefinitions/make_fixtures.py`), two of them with one entry renamed in the header
   to `../evl` and to `C:/evl`, and an over-member and an over-size archive through `ExtractAsync`'s bounds given small
   (9 entries, 702 bytes against 18 and 703): each is refused and **the zip does not exist afterwards**. That needed a
   change: the paths were judged entry by entry while copying, so an escaping entry after the geodatabase was refused
   with the data already copied out, and the over-size bound was only counted while copying. Every path and the
   declared size are now judged before the zip is created, the size is still counted while copying, and a refusal
   deletes what was written. With the old order and no delete the three path and size tests fail (measured).
4. **What is read is what this ADR names**: `cd/*.gdb`, `serviceconfiguration.json`, `servicedescriptions/**`, and
   since 2026-10-18 `esriinfo/iteminfo.xml` (owner decision, §5.7);
   reading the `.msd`/`.mapx` (for symbology) is a later decision with its own note on the clean-room line.
   **DISCHARGED 2026-10-31.** `Only_the_named_parts_are_read_and_only_the_geodatabase_is_copied` extracts an archive that
   also holds a `v/map.mapx` whose content is not JSON, a thumbnail, a `cd/readme.txt` beside the geodatabase and a file
   in a folder of its own: the service's settings, its one layer's drawing and the item's snippet and tags come from the
   three files named, and the zip holds the three `cd/data.gdb/` files and nothing else. Letting a file directly under
   `cd/` through makes it fail (measured). Only `servicedescriptions/featureserver/featureserver.json` is read of
   `servicedescriptions/**`.
5. **Several parts**: a service definition larger than one `addPart` is reassembled in order, with a test.
   **DISCHARGED 2026-10-31.** `commit`'s join is `PortalEndpoints.ReassembleAsync`, and
   `A_definition_sent_in_parts_out_of_order_is_reassembled_in_their_numbers_order` cuts a service definition into eleven
   parts (so that 10 and 11 would sort before 2 by an unpadded name), writes them in reverse, and gets the archive back
   byte for byte, the parts gone and the archive opening. Joining in arrival order instead makes it fail (measured).
6. **Overwrite from Pro's own pane**, not only arcpy: *Overwrite Web Layer* in Pro's UI replaces the data and keeps the
   item id and the sharing the pane shows. *(Met on dev 2026-10-17, driven through Pro's UI: Share → Web Layer →
   Overwrite Web Layer on `tur_admin_public` → "Web layer published successfully"; the item id, public access and its
   group stayed, and the row was rewritten (object id 1 → 2). **Three things it needed.** (a) The pane fills its
   Sharing Level and groups from the user item, `content/users/{u}/items/{id}`, which answered 404 — the pane then
   offered Owner for a public service, and publishing it would have made the service private; it is now served with
   `sharing.access` and `sharing.groups`. (b) A layer name used by several services — the same data published four
   times — made the overwrite's layer lookup refuse as ambiguous (409); it now looks inside the service being
   overwritten. (c) Pro itself refuses **Everyone** for a layer with editing on unless *Approve for public data
   collection* is ticked on the Configuration tab, and falls back to Organization without saying so; that is Pro's
   rule, not this server's.)*
