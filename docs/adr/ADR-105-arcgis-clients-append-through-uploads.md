# ADR-105 — ArcGIS clients append to a hosted layer through `uploads/upload` and `append`

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` — the two operations are shaped as the public ArcGIS REST reference documents them and share the engine ADR-103 measured; no ArcGIS client has been run against them yet |
| **Decided** | 2026-10-01, by owner decision (*"2 devam sırayla"*, the second item of the ArcGIS reviewer's list) |
| **Supersedes** | — |
| **Superseded by** | — |
| **Amends** | [ADR-103](ADR-103-a-hosted-layer-is-updated-from-a-file.md) §5.4 (the ArcGIS route it left out) |

> **Amended 2026-10-01 by [ADR-116](ADR-116-an-update-may-upsert.md), owner decision (*"Sırayla git"*).** `upsert=true`
> is no longer refused: with `upsertMatchingField` it updates the features a file row matches and adds the rest, and
> without one it is refused for that reason. §5.3's list and the *Negative* line below describe the state before.

---

## 1. Context

ADR-103 gave a hosted layer *Update data* through the server's own routes and Studio, and left ArcGIS's
`FeatureServer/{id}/append` out (§5.4, Alternative B). The ArcGIS reviewer ranked the missing ArcGIS route second:
the ArcGIS API for Python's `FeatureLayer.append` and scripted nightly reloads call it. The owner asked to continue
the list in order.

The public reference ([Append (Feature Service/Layer)](https://developers.arcgis.com/rest/services-reference/enterprise/append-feature-service-layer/),
[Upload](https://developers.arcgis.com/rest/services-reference/enterprise/upload/)) describes a file uploaded to
`…/FeatureServer/uploads/upload`, answered with an `item.itemID`, and then `…/FeatureServer/{id}/append` with
`appendUploadId`, `appendUploadFormat`, `fieldMappings`, `appendFields`, `truncateExisting`, `upsert` and others.

## 2. Alternatives considered

### Alternative A — Both operations over ADR-103's engine, the rest refused by name (chosen)

**Argument for.** One engine: the file is read by the import's readers and written in one transaction with the
layer's schema kept, whichever door it came through. The parameters that map onto it — `fieldMappings`,
`appendFields`, `truncateExisting` — are honoured; the ones that do not — `upsert`, `appendItemId`, the other
formats, `async=true` — are refused with the reason.

**Argument against.** `upsert` is what some reload scripts use. Refusing it says so; it does not do it.

### Alternative B — Also portal items (`appendItemId`) and upsert

**Argument against.** `appendItemId` needs files stored as portal items, which ADR-056 has not built; upsert needs
a matching key and an update path through the same transaction. Each is its own decision.

## 3. Counterarguments to the preferred option

- *`async=true` is the default of some clients.* Then they are refused with a sentence that says to send
  `async=false`; the same was decided for `truncate`. A job-backed status URL is a later decision.
- *An upload lives on one node.* It is a file waiting for its append, kept an hour; behind a balancer without
  affinity the append may reach another node and be told the upload is not there. Recorded, not solved.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| The request and answer shapes | ArcGIS REST reference, *Append* and *Upload* | publicly documented |
| The engine keeps the layer as it was on any failure | `PostGisAppendTests` | tests/Graticula.Platform.Postgres.Tests |

## 5. Decision

5.1 `POST /rest/services[/{folder}]/{service}/FeatureServer/uploads/upload` keeps a multipart `file` for an hour for
the caller who sent it and answers `{success, item: {itemID, itemName, description, date, committed}}`.

5.2 `POST …/FeatureServer/{id}/append` reads `appendUploadId` with `appendUploadFormat` `geojson` or `shapefile`,
applies `fieldMappings` (`[{"name": target, "source": source}]`) and `appendFields`, replaces with
`truncateExisting=true` (refused while the item is protected, ADR-103 §10.4), and answers
`{layerName, submissionTime, lastUpdatedTime, recordCount, status: "Completed"}`. An upload is spent once appended.

5.3 Refused by name, nothing written: `upsert=true`, `async=true`, `appendItemId`, `edits`, and every other format.

5.4 Owner or administrator of a hosted layer, as ADR-103.

## 6. Consequences

**Positive.** A script or the ArcGIS API for Python reloads a hosted layer without deleting and republishing it.

**Negative.** No upsert, no portal items, no async job; an upload is node-local.

**State.** An upload is a file in the import scratch directory and an entry in memory on the node that received
it, for an hour; nothing in the catalogue.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | The ArcGIS API for Python's `append` sends `async=false` when asked not to wait | `INFERRED` |

## 8. Dependencies

**Depends on:** ADR-103, ADR-075.

**Depended on by:** —

## 9. Revisit triggers

- An ArcGIS client is found that sends only `async=true`.
- Portal items land (ADR-056) — `appendItemId` then has something to name.

## 10. Conditions

1. **Upload, append with a field mapping, the upload spent, and `truncateExisting` are tested against the running
   server**, and `upsert` refused. **DISCHARGED 2026-10-01** —
   `UpdateDataConformanceTests.An_ArcGIS_client_uploads_then_appends_maps_fields_and_an_upsert_needs_its_field` (renamed by ADR-116).
2. **The ArcGIS API for Python's `FeatureLayer.append` is run against it** and the layer counted afterwards — not yet.
