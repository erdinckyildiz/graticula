# ADR-103 — A hosted layer is updated from a file: append, or overwrite

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` — the write path is the import's, measured by its own tests; what is new is one `INSERT … SELECT` and a truncate in the same transaction. What is not measured is what an ArcGIS Pro user expects from *Overwrite*, which this does not implement (§5.4) |
| **Decided** | 2026-10-01, by owner decision |
| **Supersedes** | — |
| **Superseded by** | — |
| **Amends** | [v1-scope.md](../v1-scope.md) §2 (the hosted row: a third way in) |

---

## 1. Context

The ArcGIS reviewer walked the rebuilt Studio item on 2026-10-01 ([ADR-102](ADR-102-the-studio-item-is-one-page-and-every-setting-has-one-home.md))
and ranked this first among what still differs from Portal: *"No Update data / Overwrite / Append. A publisher
meets this in the first week, and for Pro users it is the most common Portal action after sharing."* Asked
whether it belongs in v1, the owner answered *"Hepsine evet"* on the same day.

What existed: a hosted layer could be **created** from a file (`POST /admin/hosted/import`, GeoJSON or a zipped
shapefile, or a geodatabase through a job), **emptied** (`truncate`, both the native and the ArcGIS admin route),
and **edited** feature by feature (`applyEdits`). The truncate's own comment names the job it was built for —
*"the nightly reload: empty the layer, then append"* — and the second half did not exist. So the only way to
refresh a layer's data from a new file was to delete the layer and publish it again, which changes its id, its
URL and every map that points at it.

## 2. Alternatives considered

### Alternative A — Append and overwrite through a staging table, one transaction (chosen)

**Argument for.** The file is read by the importer's own readers, so a file that publishes is a file that
updates. It is copied into a temporary table in the types it was read as; one `INSERT … SELECT` casts each
column to the layer's type, stamps the file's reference on the geometry and transforms it only when the two
differ. Overwrite truncates in the same transaction, so a file that does not fit leaves the layer exactly as it
was — the case Portal users fear most is a half-overwritten layer.

**Argument against.** It holds the file in memory (the import's 64 MB and one million features), and a truncate
inside a transaction takes `ACCESS EXCLUSIVE` for its length. For the layer sizes the import accepts, that is
the import's own profile; the two-second lock timeout turns a busy layer into a retry rather than a stall.

### Alternative B — ArcGIS's own `append` operation on the FeatureServer

**Argument for.** Pro's *Append* geoprocessing tool and the ArcGIS API for Python call
`FeatureServer/{id}/append`, so implementing it would serve those clients directly.

**Argument against.** It takes an uploaded *item* (`appendItemId`/`appendUploadId`), field mappings, upsert
keys and an asynchronous status URL — the upload and item machinery behind it is most of the work, and nothing
here stores an uploaded file as an item. Doing A first gives B its engine; B is the adapter over it, later.

### Alternative C — Delete and republish

**Argument for.** Already possible.

**Argument against.** The layer's id, URL, symbology, field overrides, history and every web map pointing at it
are lost. It is the gap, not an alternative.

## 3. Counterarguments to the preferred option

- *Overwrite in Portal replaces the schema too.* Portal's *Overwrite* republishes from the original source and
  can change fields. This does not: the layer keeps its columns and its geometry type, and a file column the
  layer lacks is ignored and named. Changing the schema is Data › Fields' job. A Portal user expecting new
  columns to appear will be surprised; the answer says which columns were not written.
- *Object ids.* Overwrite keeps counting, as truncate does — an id a client has seen is never given to another
  feature. A client that expects ids to start at 1 after an overwrite is wrong about this server and right about
  some others.
- ~~*Delete protection does not stop it.*~~ It does, by owner decision 2026-10-01 — see §10.4.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| The import writes by binary COPY in one transaction | `PostGisImporter.ImportAsync`, `InsertAsync` | src/Graticula.Providers.PostGis/PostGisImporter.cs |
| Truncate exists, keeps identity, and has no append after it | `TruncateAsync` and its comment | src/Graticula.Host/HostedDataEndpoints.cs |
| No `append` or overwrite existed anywhere in `src` | grep, 2026-10-01 | — |
| Portal offers *Update data › Overwrite entire layer / Add or update features* on a hosted feature layer's item page | Portal documentation, *Manage hosted feature layers* | publicly documented behaviour |

## 5. Decision

5.1 **Two routes, owner or administrator, hosted layers only:** `POST /admin/hosted/{layer}/append` and
`POST /admin/hosted/{layer}/overwrite`, multipart with a `file` (GeoJSON, or a zipped shapefile with its
`encoding` and `srid`). A zipped geodatabase is refused with the sentence to export the one feature class.
Privilege `content:publishFeatures` and ADR-075's *manages* rule, as truncate.

5.2 **One transaction.** The file is copied into a temporary table; overwrite truncates the layer and its
attachment tables; one `INSERT … SELECT` writes. A value that does not cast, a constraint the layer keeps, a
geometry type that is not the layer's, or a busy table (two-second lock timeout) answers 400 or 409 **and
nothing is written**.

5.3 **The layer keeps its schema and its reference.** Columns match by the name the import would have given
them; a file column the layer lacks is ignored and named in the answer; a layer column the file lacks takes its
default. The geometry is stamped with the file's reference and transformed into the layer's only when they
differ, made multi when the layer is, and given the layer's ordinates.

5.4 **Not in this decision:** ArcGIS's `FeatureServer/{id}/append` (Alternative B) *(built the same day over this engine — [ADR-105](ADR-105-arcgis-clients-append-through-uploads.md))*, upsert by a key field
(*Add or update features*), and schema-changing overwrite. Each is a later decision on top of this engine.

5.5 **Afterwards:** the layer's tiles are purged and its context forgotten (as any schema change), the service's
`updated_at` moves, the kept thumbnail is drawn again, statistics are refreshed (`ANALYZE`) so the published
extent follows the data, and the audit log records `layer.append` or `layer.overwrite`.

5.6 **In Studio:** an *Update data* action on the item's Overview, beside *Export data*, for whoever manages a
hosted item: choose the layer, *Add features* or *Replace all features*, choose the file; the answer is said in
the dialog.

## 6. Consequences

**Positive.** A layer's data can be refreshed without losing its id, URL, style, field labels, history or the
maps that use it. The nightly reload the truncate was written for is now two calls — or one.

**Negative.** Overwrite does not change the schema, which Portal's does. Held in memory to the import's limits.
No ArcGIS-client route yet, so Pro's *Append* tool does not reach it.

**State.** None new in the catalogue: the rows change in the layer's own hosted table, and the service's
`updated_at` moves as any schema change moves it. At runtime a temporary staging table lives for the length of
one transaction on one connection; the tile purge and context forget are node-local, as for truncate.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | A publisher refreshing data wants the layer's schema kept, not replaced | `INFERRED` |

## 8. Dependencies

**Depends on:** ADR-075 (who manages a layer), ADR-080 (ordinates a column keeps), ADR-102 (Overview's actions).

**Depended on by:** —

## 9. Revisit triggers

- An ArcGIS client is found that needs `FeatureServer/{id}/append` — build Alternative B over this engine.
- A file larger than the import's limits needs updating — the same move to a job the geodatabase import made.

## 10. Conditions

1. **The engine is tested against a real table**: append adds rows and keeps the old; overwrite replaces them and
   keeps counting ids; a geometry-type mismatch and an uncastable value each leave the table as it was.
   **DISCHARGED 2026-10-01** — `PostGisAppendTests`, six cases against PostGIS, including an overwrite whose insert
   fails after its truncate and leaves both rows.
2. **The route is tested for who may call it**: a non-owner is refused; a registered layer is refused.
   **PARTLY DISCHARGED 2026-10-01** — `UpdateDataConformanceTests` checks anonymous refusal, append, overwrite and a
   wrong file changing nothing, counted through the FeatureServer. The registered-layer refusal is the shared
   `HostedLayerAsync` truncate already goes through, and is not exercised here: the local fixture has no registered
   layer.
3. **Studio's *Update data* is tested** as the other Overview actions are. **DISCHARGED 2026-10-01** —
   `ItemStructureTests.Update_data_sends_the_file_to_the_chosen_layer`.
4. ~~**Delete protection does not block overwrite — `INFERRED`, put to the owner.**~~ **Reversed by owner decision 2026-10-01 (*"1. evet"*): protection blocks overwrite**, 409 and nothing written; append is not blocked, since it removes nothing. **Truncate is blocked with it — `INFERRED`**: it is the more destructive of the two, and leaving it open would have made the protection stop the lesser act. **DISCHARGED 2026-10-01** — `UpdateDataConformanceTests` protects the layer and finds both refused and its rows unchanged.
