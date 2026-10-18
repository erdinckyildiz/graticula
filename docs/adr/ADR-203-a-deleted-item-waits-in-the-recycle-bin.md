# ADR-203 — A deleted item waits in the recycle bin, whole, with its ids

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` |
| **Decided** | 2026-10-18, by owner decision — [Q-180](../open-questions.md), *"Satırları taşı"*, after the ArcGIS veteran's review (V-96) |
| **Supersedes** | — |
| **Superseded by** | — |

---

## 1. Context

ArcGIS 11.x keeps a deleted item and restores it with the same id; here a delete was final, and for a hosted layer it
dropped the data. Q-180 measured two facts that decided the shape: the `service` table is read by 47 statements and
`item` by 32, so a `deleted_at` every reader must filter is one forgotten `where` away from serving a deleted
service; and `CatalogManifest` (ADR-183) translates ids to names, so a restore through it would give the item a new
id and break every map and app that names the old one. The owner chose to move the rows.

## 2. Decision

- **A delete of a service (with `drop=true`, the whole delete), a web map or a document puts the item in the recycle
  bin.** In one transaction, every catalogue row the delete would take — found by walking the store's own foreign keys
  from the root row, cascading, restricting or no-action alike — is kept as `to_jsonb` under a new entry, and a row the
  delete would set to null is kept to have its link put back; then they are deleted. **No list of tables is kept
  anywhere**, so one a later migration adds is carried without anybody remembering it.
- **A hosted layer's data is moved, not copied**: its table, attachments, history and the history's function go into
  the datastore schema `graticula_recycle` (`ALTER … SET SCHEMA` and a rename to `r{entry}_{n}`), before the
  catalogue rows; if the rows cannot be kept, the tables come back. A table another service still publishes stays.
- **Restore** moves the tables back — refused, with nothing changed, if a table of that name has been made since — and
  writes the rows back parents first, by the depth each table was found at, with their ids, in one transaction; a
  name or an id taken since, or a folder or account deleted since, refuses it and the tables are set aside again.
- **Fourteen days**, or `Graticula:RecycleDays`; then the entry and its tables are deleted for good, once an hour.
  `/admin/recycle-bin` lists an entry to its owner, to whoever deleted it, and to whoever manages all content, who may
  restore it or empty it now.

## 3. Alternatives and counterarguments

### Alternative A — soft delete

**For.** One column. **Against.** Every one of ~80 readers must filter it, and the one that does not serves a deleted
service — the failure is silent and lands on whoever was meant not to see it.

### Alternative B — a manifest snapshot

**For.** A format that already exists. **Against.** It drops ids by design (ADR-183): a restored item would have a new
id.

**Counterargument to the chosen option.** Walking foreign keys means a table that refers to an item *without* one —
a hosted table's rows, a tile on disk, an uploaded image — is not seen. The hosted tables are moved by name for that
reason; the tile cache is rebuilt from the data; an uploaded image is moved by path under the state directory's
`recycle` (D-306, the same day).

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| A hosted service's table leaves the datastore listing, comes back with its rows and the same item id, and is gone after emptying | `HostedDeleteTests` | conformance fixture |
| A web map comes back with its id and tags | `ADeletedMapComesBackTests` | conformance fixture |
| The catalogue rows of a service shared to a group, categorised and licensed are byte-identical after delete and restore (5 rows, same md5) | a check run against the fixture | 2026-10-18 |
| `layer.service_id` restricts rather than cascades — found by the first run, which the walk now treats as the item's own | the 409 that run answered | 2026-10-18 |

## 5. Consequences

**Positive.** A mistaken delete is undone in seconds, with every address that named the item still working.

**Negative.** Data waits for fourteen days in the datastore and counts against its size. A removed member's maps and
documents are still deleted for good with the account (D-306). No screen yet (D-305).

**State.** `recycle_entry` and `recycle_row` (migration 94) in the catalogue, shared by every node; the moved
relations in the datastore's `graticula_recycle` schema. Nothing is held at runtime.

## 6. Conditions

1. **D-306**: image services, a removed member's content and Pro's delete go through the bin, or are decided not to. *(Partly discharged 2026-10-18: image services do, with their uploaded files; Pro's portal delete deletes nothing. A removed member's content is left.)*
2. **A screen** in the redesigned Studio — D-305.
3. **A restore of every kind is tested against a catalogue of the showcase's size**, to see the walk's cost there. *(Partly discharged 2026-10-18 on dev.graticula.com, a copy of the showcase's catalogue: a new hosted service deleted in 0.61 s and restored in 0.31 s, a web map in 0.29 s and 0.24 s, each read back and then emptied. **Left:** a document, and a service with years of usage rows, whose walk reads more.)*

## 7. Dependencies

**Depends on:** ADR-056 (the item), ADR-034 §5k (a hosted service's data goes with it), ADR-183, ADR-078.

## 8. Revisit triggers

A table found to refer to an item without a foreign key; a restore refused for a reason the message does not name.

## 9. Dissent

None recorded.
