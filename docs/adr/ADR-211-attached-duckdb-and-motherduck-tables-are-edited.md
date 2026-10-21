# ADR-211 — Tables of an attached DuckDB file or MotherDuck database are edited

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` |
| **Decided** | 2026-10-21, by owner decision: *"tüm bağlanan db lerde yazma olmalı"*, *"Biz bu db leri hem okuma hem yazma için kullanacağız"*, and, asked which of the DuckDB-read sources that means, *"DuckDB dosyası ve MotherDuck"* — the database file and MotherDuck, GeoParquet files staying read only ([Q-188](../open-questions.md)). |
| **Reverses** | [ADR-067](ADR-067-duckdb-sources-beyond-a-local-folder.md) §5.3–5.4's read-only attach |
| **Supersedes** | — |
| **Superseded by** | — |

---

## 1. Context

ADR-067 registered a DuckDB database file under the GeoParquet root, and a MotherDuck database, as sources read
through ADR-066's sandboxed DuckDB: one in-memory instance per source, external access off, configuration locked, the
database attached `READ_ONLY`. Every other registered engine — PostGIS, Oracle, MySQL, MariaDB and, since the same day,
SQL Server (ADR-210) — is edited under `EditColumns`. The owner asked for the same here.

### 1a. What DuckDB 1.5.5 does, measured on 2026-10-21 inside the sandbox's own settings

- **A read-write attach works under the sandbox**: insert with `st_geomfromwkb` into a `GEOMETRY` column — typed
  `GEOMETRY('OGC:CRS84')` included — `RETURNING` the key a sequence default made, update, delete, an MD5 of a row's JSON,
  and a rolled-back transaction leaving the row as it was.
- **A checkpoint needs `<file>.wal.checkpoint`.** With only the file and its `.wal` allowed, as ADR-067 allowed them,
  `checkpoint` failed — and **invalidated the database** rather than failing quietly. DuckDB checkpoints on its own when
  the log grows, so this would have broken a busy layer some time after its first edits. The third path is allowed.
- **No savepoints**: `savepoint a` is a syntax error.
- **One process at a time**: while the server holds the file read-write, another process cannot open it, read only
  included (*"being used by another process"*). Under ADR-067's read-only attach other readers could open it.
- **A write goes to the log first**, so the file's size and time — which ADR-067 versioned a table by — do not move
  with an edit.
- Core DuckDB recognises `OGC:CRS84` and not `EPSG:4326` as a type's reference, and has no `st_geomfromtext`; neither
  matters to the writer, which binds WKB.

## 2. Alternatives considered

### Alternative A — rewrite a GeoParquet file per edit

Not asked for: GeoParquet files stay read only (§5.6). Recorded because it is what *"every connected source"* would
otherwise have meant.

### Alternative B — the source's own instance, attached read-write, one batch at a time *(chosen)*

There is no second connection to write through — the file is open in one process (§1a) — so the writes run on a
duplicate of the instance the layer is read through, behind a gate.

### Alternative C — a second, unsandboxed DuckDB for writing

Would need the file closed by the reader first, and gives up ADR-066's sandbox for the writer: rejected.

## 3. Counterarguments to the preferred option

- **The file is no longer shareable while it is registered.** A user who keeps the file open in DBeaver or Python
  cannot register it, and cannot open it while it is registered. That is DuckDB's rule, not a choice made here;
  a read-only mount keeps ADR-067's behaviour (§5.2).
- **All or nothing has no savepoints to lean on** (§5.4).

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| Sandboxed read-write attach, insert, returning, rollback, hash | `D:/temp/duck-probe` on DuckDB.NET 1.5.5 | measured 2026-10-21 |
| A checkpoint without `.wal.checkpoint` allowed invalidates the database | same | measured 2026-10-21 |
| The writer under EditColumns | `DuckDbFeatureWriterTests`, 6 of 6: add, update, delete with the table's version moving after each; all or nothing keeping nothing and naming why the rest were not tried; each alone costing only itself; a stale version refused; a UUID GlobalID minted and looked up; a key with no default and a row-number identity refused by name; a read-only file opened to read and saying it cannot be edited | DuckDB provider suite 127 of 127 |
| MotherDuck read-write | **Not measured** — no token on this machine | condition 2 |

## 5. Decision

**A table of an attached DuckDB file or MotherDuck database is edited by `DuckDbFeatureWriter`, through the source's
own sandboxed DuckDB, under the rules every registered engine's writer applies (`EditColumns`).**

1. **Attached read-write**, with `<file>.wal.checkpoint` allowed beside the file and its log.
2. **Read only where it cannot be written**: when the read-write attach is refused — a read-only mount, as ADR-066 §3
   advised, or a file this process may not write — the database is attached `READ_ONLY` as before, its layers say they
   are not writable, and an edit is refused saying why.
3. **One batch at a time** per source; a new key from a column with a default (a sequence) through `RETURNING`, or the
   add refused with the reason; GlobalIDs in a `UUID` column minted and looked up; a row's version an MD5 of its columns
   and shape as JSON, compared in the `where`; ownership in the `where`; Z and M refused.
4. **All or nothing without savepoints**: one transaction, and at the first failure nothing more is tried — each edit
   after it is answered *not applied*, saying why — and nothing is kept. A batch that may keep some runs each edit in a
   transaction of its own, which is what a savepoint per edit gives elsewhere.
5. **A table's version moves with every batch this server commits** — a count of edits joins the file's size and time,
   or MotherDuck's rows and columns, and the table's listing, extent and details are read again — because a write lands
   in the log before the file.
6. **GeoParquet files stay read only** (ADR-066), and a layer identified by DuckDB's row number is not edited: the row
   number moves when a row is deleted.

## Conditions

1. **The owner's file workflow**: that a registered database file cannot be opened by another program while it is
   registered is said in the console's *Add a source* dialog and the registration's answer, not left to be discovered.
2. **MotherDuck** edited end to end with a real token, and a read-scaling token's refusal falling back to read only.
3. **End to end through FeatureServer** `applyEdits` on a server whose projection database knows EPSG:4326's area of
   use — the local fixture's PostGIS bundle does not (its `postgis_srs()` is empty), so a publish there was refused
   before an edit could be sent.

## 6. Consequences

- Every registered database Graticula serves is now read and written; GeoParquet files are the one read-only source.
- A DuckDB file registered with Graticula is Graticula's while it is registered.
- **State.** Nothing new in the catalogue; at runtime, per node, the per-source write gate and a count of edits per
  table, which a restart forgets — the file's own version moves at its next checkpoint.

## 7. Assumptions this decision rests on

- That one batch at a time per source is enough for the edit load a DuckDB file sees.

## 8. Dependencies

- ADR-066 (the sandbox), ADR-067 (the attach), ADR-210 (the owner's decision, Q-188).

## 9. Revisit triggers

- A user who needs the file open in another program while it is registered.
- DuckDB gaining savepoints.

## 10. Dissent

None recorded.
