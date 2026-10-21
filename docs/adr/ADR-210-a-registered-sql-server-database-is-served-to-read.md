# ADR-210 — A registered SQL Server database is served to read, and edited

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` |
| **Decided** | 2026-10-21, by owner decision: *"benim canım sql server denemek istiyor"*, and after SQL Server was found not to run on the arm64 VPS but to be installed on the development machine as SQL Server 2025 Express LocalDB, *"ya ben çalışabildiğini kanıtlamak istiyorum. performans umurumda değil"* — proof that it works, speed not the question. That the proof is **reading only** was `INFERRED` (§5.5) and the owner answered it the same day: *"tüm bağlanan db lerde yazma olmalı"*, *"Biz bu db leri hem okuma hem yazma için kullanacağız"* — every connected database is read **and written** ([Q-188](../open-questions.md)). §5.5 is amended accordingly. |
| **Reverses** | — |
| **Supersedes** | — |
| **Superseded by** | — |

---

## 1. Context

Graticula serves registered PostGIS, Oracle (ADR-205), MySQL (ADR-208) and MariaDB (ADR-209) databases. SQL Server
is the one an ArcGIS shop is likeliest to have beside them, and ADR-205 §4 recorded why it was not tried then:
`mcr.microsoft.com/mssql/server` is amd64 only, and under emulation on the arm64 VPS it exits 139. Microsoft's arm64
edition, Azure SQL Edge, was retired in September 2025.

The development machine is x64 Windows and already had **SQL Server 2025 Express LocalDB (17.0.4025.3)**. Measured on
it on 2026-10-21: `geometry` and `geography` work (Ankara to İstanbul by `STDistance`, 350,180 m), the instance accepts
SQL Server authentication as well as Windows (`IsIntegratedSecurityOnly` 0), and a test database and login were made
for the provider's tests (`~/.graticula/sqlserver-localdb.json`). LocalDB accepts local connections only, so the
showcase and dev cannot reach it; CI's runners are x64, where the official image runs.

### 1a. What SQL Server does differently, measured, that the provider has to answer

- **A reference per shape, not per column.** A `geometry` column declares no SRID; each value carries one. A table's
  reference is read from its rows, an empty table has none yet, and a table whose rows disagree cannot be served in one.
- **Two spatial types.** `geometry` is planar and takes a spatial index with a bounding box; `geography` is on the
  ellipsoid and draws a polygon's edges as great-circle arcs — so a box asked of it is not the box a client drew
  (MongoDB's `2dsphere` lost 8,481 of 200,000 points that way, other-databases-trial.md).
- **WKB longitude first for both** (`STAsBinary`; a `geography` point at 32.85 E, 39.93 N reads back `x = 32.85`).
- **T-SQL**: no boolean literal, no `ilike`, no default `like` escape and `[` is a character class, `substring` needs
  its length, `charindex`, `len`, `datepart`, `log` is natural, `round(x, n, 1)` truncates, `%`, `OFFSET … FETCH` for a
  page (and only after an ORDER BY), `OPENJSON` for an id list (compatibility level 130 and later), nulls first in an
  ascending order, a whole number averaged into a whole number, `PERCENTILE_CONT` only as a window function.
- **The driver refuses globalization-invariant mode.** Microsoft.Data.SqlClient throws *"Globalization Invariant Mode is
  not supported"* on connecting, every time; its maintainers say it cannot support the mode
  ([dotnet/SqlClient#1047](https://github.com/dotnet/SqlClient/issues/1047)), and no AppContext switch bypasses it.
  Every Graticula process ran invariant (`Directory.Build.props`).

## 2. Alternatives considered

### Alternative A — wait for an x64 machine to host SQL Server beside the showcase

What a customer-facing demonstration would need, and a VPS nobody has asked to pay for. Proof does not need it.

### Alternative B — a provider of its own, `Graticula.Providers.SqlServer`, behind the engine seam, reading only *(chosen)*

ADR-208 §5.6's seam takes a fourth engine as a member list; the reader is MySQL's in T-SQL. Writing, attachments, a
history and relationships are each refused with the reason, so the seam's every member answers.

### Alternative C — the same provider with editing, as ADR-208 built MySQL

Twice the work, and not what was asked: *"çalışabildiğini kanıtlamak"*. Left as condition-free future work behind Q-188.

## 3. Counterarguments to the preferred option

- **The host leaves invariant mode for one driver.** Every string the process compares or formats could change where a
  culture is not named. Answered in §5.6 — the invariant culture is pinned — and checked by the conformance suite rather
  than argued; it remains the largest risk this decision carries.
- **A `geography` layer is read without its index.** Its shapes are boxed as SRID-0 `geometry`, which is exact and a
  scan. Speed was ruled out of scope by the owner; a `geometry` column keeps its index.
- **The certificate is trusted, not verified** (§5.7).

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| SQL Server cannot run on the VPS | amd64-only image; exit 139 under QEMU | ADR-205 §4, measured 2026-10-19 |
| LocalDB runs spatial SQL here | SQL Server 2025 RTM-CU3 Express, `geography::Point(…).STDistance` = 350,180 m | measured 2026-10-21 |
| The provider reads correctly | 9 of 9 provider tests against LocalDB: probe offers and skips with reasons; identity order, longitude first, extent; a box keeps its edges and corners; a triangle decided exactly; a `geography` box as drawn, not along great circles; T-SQL `like` with a bracket, case folding, integer division; a page and null placement; grouped statistics and PostgreSQL's percentiles; top features; an edit refused | `tests/Graticula.Providers.SqlServer.Tests`, 2026-10-21 |
| It works through Graticula | the local fixture registered LocalDB as `kind: sqlserver`, published 17,114 OSM buildings of İstanbul's historic peninsula (EPSG:3857, `geometry`, spatial index) and 12 Turkish places (EPSG:4326, `geography`); answered counts, a box, a where clause, 300 km from Ankara, statistics by kind, output in 3857; MapServer `export` drew the buildings | `D:/temp/sqlserver-e2e.py`, 2026-10-21 |
| The host's culture change breaks nothing else | conformance suite against the fixture's host | condition 2 |

## 5. Decision

**A registered data source may be a SQL Server database. Its tables with a `geometry` or `geography` column are
published and read as a registered PostGIS table's are — through `Graticula.Providers.SqlServer`, behind the engine
seam, with SQL Server's types nowhere in a Core signature. Nothing is written to it.**

1. **The locator** is `sqlserver:` and JSON — host, port, database, user, password — sealed like every other. The
   port may be left out where the host names an instance (`host\SQLEXPRESS`, `(localdb)\MSSQLLocalDB`), which the
   driver finds by name.
2. **What is offered**: a table with a single whole-number primary key and a spatial column whose rows hold one SRID.
   An empty table and one in two references are skipped with the reason. A spatial index is not required.
3. **Reading**: MySQL's reader in T-SQL (§1a). A spatial filter's box is decided in SQL Server, planar and exact; any
   other shape, relation or distance is decided here, as for every registered engine (EngineRelations).
4. **`geography` is read as planar coordinates**: boxed as `geometry` of SRID 0 over its WKB, so a box is the box the
   client drew. The cost is its index.
5. ~~**Read only — `INFERRED`, for confirmation (Q-188).**~~ **Edited, by owner decision 2026-10-21 (Q-188).**
   `SqlServerFeatureWriter` is `MySqlFeatureWriter` in T-SQL under the same `EditColumns` rules: adds, updates and
   deletes in one transaction with a savepoint each; a new key through `OUTPUT inserted` into a table variable (so a
   trigger does not refuse it), from an `IDENTITY` column or a default, or the add is refused with the reason; GlobalIDs
   in a `uniqueidentifier` minted and looked up; a row's version a SHA-1 of its columns and shape as JSON, compared in
   the `where`; ownership in the `where`; Z and M refused. **A `geography` polygon sent clockwise is reoriented** — SQL
   Server reads a ring's left as its inside, so ArcGIS's clockwise ring would otherwise be the rest of the Earth. A table
   is offered writable where the credential holds `UPDATE` on a base table. Attachments, a history and relationships
   are still refused saying so, and a history answers *off*, so a layer can be removed (condition 5).
6. **The host runs with ICU and the invariant culture pinned.** `InvariantGlobalization` is false for
   `Graticula.Host` (and the provider's tests) alone; `Main` sets the process's cultures to the invariant one before
   anything else, so formatting and casing stay what they were, and a Turkish Windows does not upper-case `i` as `İ`.
   The server image (`aspnet:9.0-noble`) carries ICU. Default string *comparison* is now linguistic under the invariant
   culture rather than ordinal; condition 2 is what checks that nothing depended on the difference.
7. **The server certificate is trusted, not verified** (`TrustServerCertificate`): the driver encrypts by default, and
   LocalDB, Express and most internal servers present a self-signed certificate. Encryption is kept; who answered is not
   proved. [D-313](../architecture-debt.md).

## Conditions

1. **A CI job** against `mcr.microsoft.com/mssql/server` running the provider suite, as the MySQL and MariaDB jobs do. **DISCHARGED 2026-10-21** — the `sqlserver` job runs the suite against `mssql/server:2022-latest`
   as a service container; green on its first run, at `c262b66f`.
2. **The host's culture change**: the conformance suite against a host built with §5.6, and CI's whole run, green. **DISCHARGED 2026-10-21** — locally against the LocalDB fixture's host, 856 of 863, the seven
   failures five known environment ones of this machine and two a log path the fixture script had wrong (11 of 11 once
   corrected); and CI's whole run at `c262b66f`, conformance included, green on Linux with ICU.
3. **The console** offers SQL Server in *Add a source* beside PostGIS, Oracle and MySQL or MariaDB, through the
   ux-designer review every screen gets — today it is registered through the API only, as Oracle and MySQL were first.
4. **The owner confirms §5.5** — reading only — or asks for editing, which would be ADR-208's writer in T-SQL.
   **DISCHARGED 2026-10-21** — editing asked for and built: the provider suite 15 of 15 against LocalDB (a clockwise
   `geography` square stored as 9.5 thousand km², not the planet less it; a stale version refused; required and long
   values said by column), and through the local fixture's FeatureServer `applyEdits` 12 of 12 — a place added with
   Turkish letters, moved and renamed, one sent in EPSG:3857 stored in 4326, a building polygon added to the indexed
   `geometry` table, all deleted.
5. **Attachments, a history and relationships** (ADR-207) on a SQL Server layer, as MySQL and Oracle have them.

## 6. Consequences

- Graticula reads four registered SQL engines besides PostgreSQL; the seam held a fourth without a branch elsewhere
  than the registration endpoint, the locator register and a migration widening the kind (98).
- The host is no longer a globalization-invariant process, which every later change to string handling inherits.
- SQL Server cannot be shown on the showcase until an x64 machine hosts one.
- **State.** A registered source's sealed `sqlserver:` locator in the catalogue's `data_source` row (kind `sqlserver`,
  migration 98), as every registered engine's; at runtime, per node, the driver's connection pool and a table's columns
  kept a minute. Nothing in the customer's database.

## 7. Assumptions this decision rests on

- That a SQL Server table worth serving keeps one SRID in its rows; one that does not is refused, not guessed at.
- That nothing in the host relied on ordinal default string comparison (condition 2 tests it).

## 8. Dependencies

- Microsoft.Data.SqlClient 7.1.1, MIT ([DEPENDENCY-LICENSES.md](../../DEPENDENCY-LICENSES.md)).
- ADR-205 (the seam's first engine), ADR-208 §5.6 (the seam), ADR-207 (why a history must answer *off*).

## 9. Revisit triggers

- A customer who keeps editable data in SQL Server (editing, Q-188).
- An x64 host for the showcase.
- Any defect traced to the host's culture (§5.6).

## 10. Dissent

None recorded.
