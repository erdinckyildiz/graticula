# The catalogue manifest — format 1

The catalogue as a file that can be reviewed before it is applied, and applied to reproduce a configuration on
another server. Decided in [ADR-183](adr/ADR-183-the-catalogue-manifest.md); owed by
[ADR-002](adr/ADR-002-primary-data-architecture.md) condition 3.

```text
graticula tools manifest export [--out <file>]
graticula tools manifest apply <file> [--credentials <file>] [--apply]
```

Both read `Graticula__PlatformStore` and `Graticula__SecretKey`, as `migrate` does.

## What it is not

- **Not a backup.** It carries no credential and no data. Restoring a server is restoring its platform store and
  its datastore.
- **Not a migration from another product.** That is `tools migrate` (ADR-081).
- **Not a deletion.** `apply` creates and updates. What is in the store and not in the file is reported as *kept*.

## The file

One UTF-8 JSON object, two-space indentation, every object's keys in ordinal order, a newline at the end. Two
servers holding the same catalogue write the same bytes.

```json
{
  "data_sources": [ … ],
  "folders": [ … ],
  "manifest": 1,
  "relationships": [ … ],
  "schema": 85,
  "services": [ … ],
  "shared_domains": [ … ]
}
```

`manifest` is this format's version. `schema` is the platform store's schema version it was written from; `apply`
refuses a manifest from a newer schema than its store's.

### The general rule

Every object is a row of the table named below, written as PostgreSQL's `to_jsonb` writes it, with:

- **no `id`**, and no other key of the storage's own;
- **no timestamps** (`*_at`): they say when, not what;
- **no column PostgreSQL computes** (`generated always as … stored`);
- **no key of its parent**, because it is nested inside its parent;
- every other reference **replaced by the name of what it points at** (below);
- a `bytea` column **in base64**.

A column a migration adds appears in the next manifest without a change here. A new `uuid` or `bytea` column
stops `export` with its name until a rule is written for it, so a key cannot reach the file unnoticed.

A key is the column's own name. A reviewer reading a diff sees the column that changed.

### Arrays and their order

| Key | Table | Ordered by | Identity on apply |
|---|---|---|---|
| `data_sources` | `data_source` | `name` | `name`; the datastore by role (below) |
| `folders` | `folder` | `name` | `name`, without case |
| `shared_domains` | `field_domain` | `name` | `name`, without case |
| `services` | `service`, and its `item` | `folder`, `name`, without case | `folder` and `name`, without case |
| `services[].layers` | `layer` | `layer_index` | the service and `layer_index` |
| `services[].groups` | `group_layer` | `layer_index` | the service and `layer_index` |
| `services[].coverage` | `coverage` | one or `null` | the service |
| `services[].styles` | `service_style` | `name`, without case | the service and `name` |
| `services[].sprites` | `service_sprite` | `pixel_ratio` | the service and `pixel_ratio` |
| `services[].shared_with` | `item_group`, the service's item's | group name | the service and the group |
| `relationships` | `relationship` | `name` | `name` |

### References by name

| Column | In the file |
|---|---|
| `owner_principal_id` | `owner`: the principal's name, or `null` — a service's is its item's |
| `layer.data_source_id` | `data_source`: the source's name |
| `service.view_of` | `view_of`: `{"folder", "name"}` of the service, or `null` |
| `item.content_folder_id` | `content_folder`: the folder's title, under the service's owner |
| `relationship.origin_layer_id`, `related_layer_id` | `origin`, `related`: `{"folder", "service", "layer"}`, the layer by index |
| `item_group.group_id` | an entry of `shared_with`: the group's name |
| a shared domain inside `layer.field_overrides`, stored `{"id": …}` | `{"shared_domain": "<name>"}` |

Dropped and not replaced: `item_group.shared_by`, who shared it, which is history.

**A service object carries its item's columns** — `owner`, `sharing`, `description`, `tags`, `content_folder` and
`delete_protected` — as it did before migration 87 moved them from `service` to `item` (ADR-056). The file did not
change shape: a manifest written before the item existed applies to a store that has one, and an item is made for a
new service with the service's name as its title.

### A data source

```json
{ "is_datastore": false, "key_version": 1, "kind": "postgis", "location": "db.example:5432/gis", "name": "parcels" }
```

`location` is where it points, as the console says it: `host:port/database` for PostgreSQL, the folder or address
for GeoParquet. Never the connection string, never the secret, never its ciphertext. `key_version` is
informational: a credential applied here is sealed with the target's own key.

**The datastore has no `location`.** Every server registers its own platform database as the datastore when it
starts, so where the datastore points is a fact about the deployment and not the catalogue. On apply it is matched
to the target's datastore by role, whatever either server calls it.

## Applying

`apply` reads the file and the store, prints one line per object, and writes only with `--apply`:

| Line | Meaning |
|---|---|
| `create` | not in the store; will be written |
| `update` | in the store and different; will be written |
| `unchanged` | in the store and the same |
| `kept` | in the store and not in the file; left alone |
| `map` | the manifest's datastore is the target's, under another name |
| `share` | the service will be shared with that group |

### Credentials

`--credentials` names a JSON file, never an argument, so a secret stays out of shell history and the process table:

```json
{ "parcels": "Host=db.example;Port=5432;Database=gis;Username=reader;Password=…" }
```

A credential is needed for a data source the target does not have, and for one whose `location` there differs from
the file's. Its own location must be the file's. The datastore takes none.

### Refused, all at once, before anything is written

- a principal (an owner), a sharing group, or a content folder's owner the target does not have;
- a data source with no credential where one is needed, or a credential pointing elsewhere;
- a credential given for the datastore, or a target with no datastore yet (start a server on it once);
- a layer whose table or view is not in its source. **A manifest carries configuration, not data**: hosted tables
  have to be in the target's datastore first, for instance with `pg_dump -n hosted`;
- a layer naming a data source or shared domain that is neither in the target nor in the file;
- a service that is a view of one that is neither;
- a key the target's tables have no column for;
- a manifest format other than 1, or a schema newer than the target's.

### Writing

One transaction. An identity column PostgreSQL generates `always`, such as a relationship's `number` (the id an
ArcGIS client follows), is kept on insert with `overriding system value`, never updated, and its sequence is moved
past the largest number afterwards. A service's `next_layer_index` only moves forward. A store error rolls the whole
apply back, and nothing is written.

### Not validated on apply

A symbology, a style or field overrides edited by hand are stored as given and refused when a face reads them. An
exported manifest was valid in its store; a hand edit is the reviewer's.
