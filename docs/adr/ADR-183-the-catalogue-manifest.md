# ADR-183 — The catalogue manifest

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` — the shape is the one [ADR-002](ADR-002-primary-data-architecture.md) condition 3 was amended to on 2026-09-09; what is new here is how it is made and applied |
| **Decided** | 2026-10-15 — the work by owner decision (*"Şartname + export + apply"*), the design below `INFERRED` from ADR-002 condition 3's amendment and [Q-104](../open-questions.md) |
| **Supersedes** | — |
| **Superseded by** | — |

---

## 1. Context

ADR-002 §4.3 promised that the catalogue, which lives in the platform store, could leave as a file and come
back. Its condition 3 said the format had to be specified before the admin API shipped. The API shipped and the
format did not, and on 2026-09-09 the condition was amended to say what is still owed given an established schema:

1. the manifest addresses everything by **name**, never by the schema's `uuid` keys, so two deployments of the
   same catalogue write byte-identical files;
2. a data source's credentials are **named, not carried**: its name, where it points and its `key_version`, never
   the secret, and an import refuses until a credential is supplied;
3. what it is for is **review before apply** and **reproducing a configuration**. Disaster recovery is a backup's
   and migration intake is [Q-16](../open-questions.md)'s.

The owner asked for the whole of it on 2026-10-15: the specification, a verb that writes it, and a verb that
applies it.

## 2. Alternatives considered

### Alternative A — Every catalogue table projected whole, keys translated to names (chosen)

Each table the manifest covers is read as `to_jsonb(row)`, its identity and timestamps dropped, each foreign key
replaced by the name of what it points at, and written back on apply through `jsonb_populate_record`, so a column
is read and written by PostgreSQL's own conversion.

**Argument for.** A column added by a migration is in the next manifest without anybody remembering to add it,
which is exactly the failure that left §4.3 unbuilt. A `uuid` or `bytea` column the projection has no rule for
stops the export with the column's name, so a new reference cannot leak a key into the file silently.

**Argument against.** The file's keys are the schema's column names, which is the *shaped by the schema* outcome
condition 3 was written to prevent. Its amendment protects the part that matters, identity; a column name is not
an identity.

### Alternative B — A hand-written document model, one class per object

**Argument for.** Friendlier keys, versioned independently of the schema.

**Argument against.** Forty columns on `service` alone, each one written twice and forgotten once. Every column
added since August would have had to be remembered by somebody. That is the D-46 shape this repository has
recorded eighteen times.

### Alternative C — Apply through the admin API, as `tools migrate` does (ADR-081)

**Argument for.** Every write passes the same validation as the console's.

**Argument against.** There is no route for most of the columns: a service's limits, tiling scheme, OGC metadata
and capability ceiling are set by a dozen endpoints. A manifest applied through them would carry only what has a
route, and reproduction would quietly be partial.

## 3. Counterarguments to the preferred option

- *Writing the store directly skips the admin API's checks.* A manifest written by `export` is a store that was
  valid. A hand-edited one is checked for names, references, owners, credentials and tables before anything is
  written, and PostgreSQL's constraints hold the rest. A symbology or style edited badly by hand is stored as it
  was given and is refused when a face reads it. That is weaker than the console, and §6 says so.
- *Snake-case keys are not friendly.* They are the names the store, the migrations and the logs use. A reviewer
  reading a diff sees the column that changed.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| Every object the manifest covers has a unique name within its parent | `data_source_name_key`, `field_domain_name_key`, `service_name_in_folder_ci`, `layer_index_unique_in_service`, `layer_name_unique_in_service`, `group_layer_index_unique`, `relationship_name_key`, `service_style_name_ci`, `coverage_one_per_service`, `folder_name_unique` | the platform store's unique indexes, read 2026-10-15 |
| A shared domain is referenced from a layer as `{"id": …}` and nothing else | `FieldDomainJson.Write(domain, byReference: true)` | this repository |
| A running server reads the catalogue from the store on each request | `CatalogFallback` falls back only during an outage | this repository |
| A manifest reproduces a configuration a second server serves alike | The local fixture's catalogue (9 services, a group, a second source) exported, applied to an empty store whose datastore held a `pg_dump` of the first one's hosted tables, and served on another port. Its counts, a query's features, a layer and a service document and a drawn MapServer PNG are the same bytes as the first server's, and the two stores write the same manifest | local fixture, 2026-10-15 |
| The datastore cannot be carried as a credential | The first run sealed the other server's datastore credential into the target. Every check passed and the second server served nothing (500, relation does not exist), because `EnsureDatastoreAsync` re-registers the server's own database on every start | local fixture, 2026-10-15 |

## 5. Decision

**5.1 What it carries.** Data sources, folders, shared domains, services with their layers, group layers, coverage,
named styles, sprites and the groups each is shared with, and relationships. **Not**: principals, roles, sharing
groups themselves, web maps, content items, jobs, logs, tile caches, settings. Those are identity, content or state,
and a manifest names principals and groups without creating them.

**5.2 The file.** One UTF-8 JSON document, two-space indentation, object keys in ordinal order, arrays in a fixed
order (by name, and layers and groups by index). `manifest` is the format's version (1) and `schema` the platform
store's version it was written from. Every object is its table's row without `id`, without its timestamps and
without the key of its parent, which is where it is nested. Every other `uuid` column is replaced by the name of
what it points at (§5.3). A `bytea` column is base64. The full rule set is in
[catalogue-manifest.md](../catalogue-manifest.md).

**5.3 References by name.** `data_source_id` → `data_source`, a source name; `owner_principal_id` → `owner`, a
principal name; `view_of` → `{folder, name}` of a service; `content_folder_id` → `content_folder`, the folder's
title under the service's owner; `origin_layer_id` and `related_layer_id` → `{folder, service, layer}` with the
layer's index; a shared domain inside `field_overrides`, `{"id": …}`, → `{"shared_domain": name}`. A `uuid` or
`bytea` column with no rule stops the export, naming it.

**5.4 A data source** is `name`, `kind`, `is_datastore`, `key_version` and `location`: `host:port/database` for
PostgreSQL, the folder or address for GeoParquet, the same summary the console shows. Never the connection string.
**The datastore is the exception, found by running it (§4)**: every server registers its own platform database as
the datastore when it starts, so its location is not written, it takes no credential, and on apply it is the
target's own datastore, matched by role.

**5.5 `graticula tools manifest export [--out file]`** writes it. **`graticula tools manifest apply <file>
[--credentials file] [--apply]`** compares it with the store and prints a plan: each object *create*, *update* or
*unchanged*, and what is in the store and not in the file as *kept*. Nothing is written without `--apply`, as
`migrate` and `symbology-migrate` do it. **Nothing is ever deleted**: a manifest is a statement of what must
exist, and removing what it does not mention is a different operation nobody asked for.

**5.6 Refused before anything is written, all reasons at once**: a principal, sharing group or folder owner the
store does not have; a data source the store does not have, or one whose `location` differs from the file's,
without a credential in `--credentials` (a JSON object, name → connection string, so a secret is never on a command
line); a credential whose own location is not the file's; a layer whose table or view is not in its source; a key
the store has no column for (a manifest from a newer schema). A supplied credential is sealed with the current key.

**5.7 One transaction.** The whole apply commits or nothing does. An identity column generated `always` (a
relationship's `number`, which ArcGIS clients hold) is kept with `overriding system value` and its sequence moved
past it.

## 6. Consequences

**Positive.** ADR-002 condition 3 is met as amended. A change can be reviewed as a diff before it reaches a server,
and a second server can be given the first one's configuration.

**Negative.** A manifest reproduces configuration and not data: a hosted layer's table must already be in the target's
datastore, and the apply refuses until it is. A hand-edited symbology or style is not validated on apply. Keys
follow column names, so a renamed column renames a key.

**State.** None new. The manifest is a file; the store is unchanged by `export`.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | A reviewer wants to see the schema's own column names | `INFERRED` |

## 8. Dependencies

**Depends on:** ADR-002, ADR-087 (shared domains by reference), ADR-016 (an explicit command, never startup).

**Depended on by:** —

## 9. Revisit triggers

- Somebody needs deletion. Then it is `apply --prune` with its own plan section and its own decision.
- A column whose value is a reference stored as something other than `uuid`.

## 10. Conditions

1. **A store's manifest, applied to an empty store and exported again, is byte-identical.** That is condition 3's
   own test of *addressed by name*. **DISCHARGED 2026-10-15** —
   `CatalogManifestTests.A_stores_manifest_applied_to_an_empty_store_is_written_back_byte_for_byte`, over a store
   holding one of everything the manifest covers: a folder, a shared domain referenced from a layer, a content
   folder, a group with a layer in it, a layer on a second source, a relationship between them, a style, a sprite
   and a group share. It fails with the shared-domain translation taken out (measured), and a second apply of the
   same file is all `unchanged`. By hand, on the local fixture, the second server served the same bytes (§4).
2. **An apply refuses, with every reason, before it writes anything**: a missing owner, a missing credential and a
   missing table in one run. **DISCHARGED 2026-10-15** —
   `CatalogManifestTests.An_apply_refuses_with_every_reason_and_writes_nothing`: all three in one plan, and the
   target holds no service afterwards.
