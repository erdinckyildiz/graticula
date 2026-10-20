# ADR-207 — Attachments and history on registered layers

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` |
| **Decided** | 2026-10-20, by owner decision, stated directly. To *where are an Oracle layer's attachments kept*: *"hem datastore'da olabilir, hem de müşterinin registered database'inde. oracle da olur, postgresql de."* To *how is an Oracle layer's history kept*: *"tüm registered db lerde olmalı. hosted ta da olmalı."* And before both, of Oracle: *"dışarıda bırakılmasın bir şey"*. |
| **Reverses** | [ADR-002](ADR-002-primary-data-architecture.md) §4.2 for these two features only — *this server never runs DDL in a database it does not own* — and with it [ADR-205](ADR-205-a-registered-oracle-database-is-served-like-postgis.md) §5b item 4 (*no history, attachments or GlobalIDs*), which the owner had confirmed the same day as [Q-183](../open-questions.md) and reversed with the sentence above |
| **Supersedes** | — |
| **Superseded by** | — |

---

## 1. Context

Until today attachments and history were the hosted layer's alone. Both are kept in tables beside the layer's —
`<table>__attach` with its `_chunk` table (ADR-013 §4), and `<table>__history` written by a row trigger (ADR-078) —
and making those tables is DDL, which ADR-002 §4.2 forbids in a database this server does not own. So a registered
PostGIS table had neither, and ADR-205 gave a registered Oracle table neither for the same reason.

The owner has now said three things that end that:

1. **Nothing is left out of Oracle.** An Oracle layer is to have what a PostGIS one has.
2. **Attachments may be kept in either place** — in Graticula's own datastore, or in the customer's registered
   database beside the table — **and for Oracle and PostgreSQL alike**.
3. **History belongs to every registered database, and to hosted layers** (which already have it).

## 2. Alternatives considered

### Alternative A — attachments in the datastore only, history written by this server

**Argument for.** ADR-002 §4.2 holds untouched: nothing is created in a customer's database, and nothing needs the
right to create it.

**Argument against.** The owner named both places for attachments. And a history written by this server misses every
edit made around it — ArcGIS Pro, a script, a DBA — which is why ADR-078 put the history in a trigger in the first
place; a registered database is exactly the one other tools write to.

### Alternative B — attachments where the administrator chooses, history by a trigger in the source *(chosen)*

**Argument for.** It is what the owner said. A trigger is the only design that keeps a history of a table other tools
also write, and it is ADR-078's design, already built and measured for hosted layers. An attachment kept beside its
feature is backed up with it and can cascade with it; one kept in the datastore needs no right in the customer's
database at all — the administrator chooses which cost to pay.

**Argument against.** §3.

### Alternative C — Esri's geodatabase model (`GDB_ITEMS`, `__ATTACH` registered in the repository)

**Argument against.** *SDE kurmuyoruz* — ADR-205 §1a.3.

## 3. Counterarguments to the preferred option

- **This server now changes customers' schemas.** A trigger on somebody's table runs inside every write anyone makes
  to it, and a defect in it fails their writes, not ours. Mitigated by making both features **opt-in per layer by an
  administrator** — nothing is created at publish, or ever, unless somebody asks for it on that layer — and by naming
  everything created with the table's name and a suffix, so it is found and dropped with the table.
- **The connected user needs more rights.** `CREATE TABLE` for attachments in the source, `CREATE TABLE` and
  `CREATE TRIGGER` (Oracle) or ownership of the table (PostgreSQL) for history. Asking without them is refused with
  the right named, not a 500.
- **An attachment in the datastore cannot cascade** with a feature deleted by another tool: the foreign key would
  cross databases. Its row stays until a sweep finds the feature gone. Deletes made through this server remove the
  attachments in the same request.
- **A history trigger on Oracle cannot see this server's editor name** the way PostgreSQL's `set_config` lets the
  PostGIS trigger see it. The session's `CLIENT_IDENTIFIER` (`DBMS_SESSION.SET_IDENTIFIER`) carries it instead, and an
  edit made by another tool is recorded under the database user that made it.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| A trigger history of a hosted table costs what ADR-078 measured | ADR-078 conditions, measured 2026-09 | [ADR-078](ADR-078-a-hosted-layer-can-keep-its-history.md) |
| The test user can create a trigger and a sequence in Oracle Free 23ai | `grant create trigger, create sequence` and the trigger-key test, 2026-10-20 | `OracleFeatureWriterTests.A_table_whose_key_a_trigger_fills_takes_adds` |

The rest is measured as it is built, and recorded in §5's conditions.

## 5. Decision

**Attachments and history become available on registered PostGIS and Oracle layers, each switched on per layer by an
administrator.**

1. **Attachments** are kept, by the administrator's choice when switching them on, either **in the source** —
   `<table>__attach` and its chunk table beside the layer's, with a cascading foreign key, as for a hosted layer — or
   **in the datastore**, in tables named for the layer's id in the hosted schema, with no foreign key; this server
   removes a feature's attachments when it deletes the feature. Hosted layers keep theirs as they do now.
2. **History** is kept in the source by a row trigger writing `<table>__history`, as for a hosted layer — on
   PostgreSQL by the trigger ADR-078 wrote, on Oracle by its equivalent — and `historicMoment`, the change log, a
   feature's versions and a restore answer on every layer that keeps one.
3. **Nothing is created unless asked for**, and switching either off drops what switching it on made, after saying how
   many attachments or versions go with it.

**`INFERRED` — for the owner to confirm:**

- **Opt-in per layer, by an administrator.** The owner said where attachments may be kept and that history belongs
  everywhere; not that every registered table gets them unasked. Creating tables and triggers in a customer's schema
  on publish would be a change nobody requested of that schema.
- **History in the source by a trigger**, not written by this server into the datastore — the owner's answer did not
  choose between the two offered, and this is the one that records edits made by other tools.
- **Datastore attachments are swept** for features deleted elsewhere, rather than kept as orphans.

### Conditions

1. **Registered PostGIS gets history** with the hosted trigger unchanged, once its *hosted only* guards become *owns
   the table or may create in its schema*.
2. **Attachments in either place** on registered PostGIS, with the place recorded per layer in the catalogue.
3. **Oracle attachments**, in either place.
4. **Oracle history**: the trigger, `historicMoment`, changes, versions, restore.
5. **Each measured end to end through the REST face**, against a PostGIS layer doing the same.

## 6. Consequences

**Positive.** A registered layer is no longer the lesser kind: an ArcGIS client attaching photos or asking for a past
moment gets the same answer whatever database the layer is in.

**Negative.** This server's code now runs inside customers' writes (the trigger), and its tables sit in their
schemas. A datastore attachment and its feature can be in different backups.

**State.** In the catalogue: where each layer keeps its attachments (`layer.attachments_in` — none, source or
datastore). In the source database: the companion tables and the trigger, created on request. In the datastore: a
registered layer's attachments when kept there. Nothing node-local.

**Ports created.** None new; `IAttachmentStore` and the history are given further implementations.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | An administrator switching history on may create a table and a trigger in the layer's schema | Checked when asked |

## 8. Dependencies

**Depends on:** [ADR-002](ADR-002-primary-data-architecture.md), [ADR-013](ADR-013-feature-service-data-model.md),
[ADR-078](ADR-078-a-hosted-layer-can-keep-its-history.md), [ADR-205](ADR-205-a-registered-oracle-database-is-served-like-postgis.md).

**Depended on by:** —
