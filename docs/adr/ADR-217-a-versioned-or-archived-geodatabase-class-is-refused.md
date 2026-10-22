# ADR-217 — A versioned or archived geodatabase class is refused, not served wrongly

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` |
| **Decided** | 2026-10-22, overnight, under the owner's *"ben uyurken açık konuları kapatır mısın?"* — the ArcGIS veteran's second review, finding N6 and the fifth of its top five. **The whole policy is INFERRED** (§5) and is the owner's to confirm. |
| **Supersedes** | — |
| **Superseded by** | — |

---

## 1. Context

The veteran (N6): the registered-database providers do not detect an enterprise geodatabase. SQL Server's default
geodatabase storage is native `geometry`, so a geodatabase's feature classes publish here and are writable, and:

- a **branch-versioned** class keeps every version's edits — inserts, updates as new rows, deletes as flagged rows — in
  the one table, so a plain read answers all of them as current;
- an **archived** class keeps every past state in the table, so a plain read answers history as current;
- a **traditionally versioned** class keeps its edits in delta tables (`A<id>`, `D<id>`), so a plain read misses them
  and a plain write bypasses them.

ArcGIS refuses to edit versioned data outside a version. The veteran called it a corruption risk *"in exactly the
deployments a migration would start from"*. There is no real geodatabase here to test against; the failure is read
from the code and from Esri's public documentation of the system tables.

## 2. Alternatives considered

### A — Detect by public names, refuse at publish *(chosen)*

The markers are documented: `GDB_BRANCH_ID` on a branch-versioned class; `GDB_FROM_DATE` and `GDB_TO_DATE` on an
archived one; a registry row (`sde.SDE_table_registry` in SQL Server, `SDE.TABLE_REGISTRY` in Oracle,
`sde.sde_table_registry` in PostgreSQL) whose `A<id>` delta table exists, for a traditionally versioned one. A publish
of any of the three is refused with the reason and what to publish instead.

### B — Serve them read-only

**For.** Nothing is lost to a reader who knows. **Against.** The read is the fault: a branch-versioned or archived
table read plainly answers deleted and superseded rows, and a versioned one misses its edits. Read-only removes the
corruption and keeps the wrong answer.

### C — Read the default version properly

**For.** What ArcGIS does. **Against.** It means implementing branch-versioning's and archiving's read rules, or
reading a geodatabase's internals beyond its documented names — §5 of the project's rules, and a great deal of work
for a case nobody here can test.

## 3. Counterarguments to the preferred option

- *"A table that is only registered, neither versioned nor archived, is edited through this server as before — but
  its object ids are the geodatabase's."* The writer already refuses an insert where the database does not make the
  key (`KeyMade`, ADR-205/210), which is the case for a geodatabase's `OBJECTID`; updates and deletes are what ArcGIS
  itself allows SQL on unversioned data. Attachments and history still create tables in the customer's schema
  (ADR-207), which the veteran also named; that is not changed here.
- *"Detection by column names can misfire on a table that happens to have them."* A table with `GDB_BRANCH_ID` or
  both `GDB_FROM_DATE` and `GDB_TO_DATE` is, in practice, one ArcGIS made; the refusal says what it saw.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| PostgreSQL: each of the three is refused, a registered-only table publishes | Live on the fixture with imitation tables: three 422s naming the reason, one 201 | 2026-10-22 |
| SQL Server: the same | `SqlServerGeodatabaseTests`, LocalDB | provider suite 22 of 22 |
| Oracle: branch and archive by columns | `OracleGeodatabaseTests`, Oracle 26ai Free | 2026-10-22 |
| Oracle: the registry query finds a versioned class only while its delta table exists | By hand: a temporary SDE schema and `TABLE_REGISTRY` on the VPS's FREEPDB1, removed after | same |
| **Against a real geodatabase** | **Not measured** — none is available here | condition 1 |

## 5. Decision

At publish — a single layer and a composition alike — a registered table is checked (`GeodatabaseClasses`, and each
engine's `GeodatabaseRefusalAsync`): branch-versioned, archived and traditionally versioned classes are refused with
422 and a sentence naming the marker and what to publish instead (a view of the wanted rows; for a versioned class,
its `_EVW` view). A table only registered is served as before. A database that cannot be asked is not refused for it.
MySQL keeps no geodatabase and is not asked. **INFERRED, all of it** — that refusal is better than read-only, the
markers, and the exemption of registered-only tables.

## Conditions

1. **Measured against a real enterprise geodatabase** — a branch-versioned, an archived and a traditionally versioned
   class in SQL Server or Oracle, made by ArcGIS Pro — before this is called done.
2. **The probe says it too**: a source's table listing marks these classes as skipped with the same sentence, so the
   console does not offer what publish refuses.
3. **What is already published is checked once**: a layer published before this ADR from such a class keeps being
   served wrongly until somebody looks; a one-off scan at upgrade could say which.

## 6. Consequences

**Positive.** The corruption path the veteran named is closed at the door; the answer says what to do instead.

**Negative.** A shop whose data is all versioned can publish none of it directly; it must publish views. A layer
already published from such a class is not touched (condition 3).

**State.** None: checks at publish time, against the source.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | Esri's documented system-table names and GDB_ columns are what a geodatabase made by Pro 3.x and Enterprise 11.x carries. | INFERRED, unmeasured |

## 8. Dependencies

**Depends on:** ADR-205 (Oracle), ADR-210 (SQL Server), ADR-013 §3 (no geodatabase internals), ADR-207 (what is kept
beside a registered table).

## 9. Revisit triggers

- A real geodatabase is available: condition 1.
- An owner decision to read a geodatabase's default version (alternative C).

## 10. Dissent

The veteran offered *"refuse (or serve read-only)"*; read-only is rejected here because the read itself is wrong.
