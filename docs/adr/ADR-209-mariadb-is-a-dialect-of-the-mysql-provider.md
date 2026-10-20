# ADR-209 — MariaDB is a dialect of the MySQL provider

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` |
| **Decided** | 2026-10-20, by owner decision: *"mariadb deneyelim"*, and after MariaDB 11.8 was measured, asked whether to build it, the owner chose *"Yap, MySQL'deki kapsamla"* — the same scope as MySQL — over reading only or recording the measurement ([Q-185](../open-questions.md)). Three things built on that are `INFERRED` and listed in §5 for confirmation. |
| **Reverses** | [ADR-208](ADR-208-a-registered-mysql-database-is-served-like-postgis.md) §5's inference *"and not MariaDB"* |
| **Supersedes** | — |
| **Superseded by** | — |

---

## 1. Context

[ADR-208](ADR-208-a-registered-mysql-database-is-served-like-postgis.md) serves a registered MySQL 8.0 database and
inferred, without measuring, that MariaDB stays out because its spatial support has no SRID-aware axis order and no
`ST_GEOMETRY_COLUMNS`. Both are true, and neither turns out to be a reason to leave it out: they are differences a
provider can absorb. MariaDB 11.8.9 runs on the test server since 2026-10-20 (`graticula-mariadb`,
`127.0.0.1:13307`, memory capped at 512 MB), and the MySQL provider's own 62 tests were run against it unchanged
before anything was designed: 44 failed, and 43 of those on one thing — the tests' own table definitions — so the
measurement below was taken directly.

### 1a. What MariaDB 11.8 does, measured against what the MySQL provider leans on

| Question | MySQL 8.4 | MariaDB 11.8 |
|---|---|---|
| A column's SRID | `point not null srid 4326`, **enforced** on insert | `point ref_system_id=4326 not null` (before `NOT NULL`); **not enforced** — a 3857 point went into a 4326 column |
| Axis order under 4326 | latitude first; `'axis-order=long-lat'` asks otherwise | **as written**, x then y; the option does not exist (error 1582 on a third argument) |
| Z and M | none | none (`ST_GeomFromWKB` of a Z point is NULL) |
| Spatial index | used by `MBRIntersects` | used by `MBRIntersects` (`EXPLAIN`: range on the index) |
| Catalogue | `ST_GEOMETRY_COLUMNS`, `ST_SPATIAL_REFERENCE_SYSTEMS` | `GEOMETRY_COLUMNS` (`G_TABLE_SCHEMA`, `SRID`, a numeric `GEOMETRY_TYPE`; SRID 0 when none is declared); `SPATIAL_REF_SYS` holds two rows and no 4326 |
| Relations under 4326 | on the ellipsoid | **on the plane**, as PostGIS's `geometry` is |
| `ST_IsValid` | yes | **no such function**, nor `ST_Validate` |
| `ST_Collect` (aggregate), `ST_SRID(g, s)`, `ST_Longitude` | yes | **none**; an envelope's first and third vertices give a box |
| `JSON_VALUE … RETURNING` | yes | **no**; `JSON_VALUE` returns text, cast after |
| `json_table`, window functions, `percentile_cont` | yes | yes |
| `INSERT … RETURNING` | no | yes |
| Triggers, session variables | yes; 1419 under a binary log | yes; no binary log by default |
| `SHOW GRANTS` with a role | the role's privileges as the user's | the role's own lines, `… TO \`role\`` — read the same way |
| A native UUID column | none | `UUID`, read back as text |

## 2. Alternatives considered

### Alternative A — a second provider, `Graticula.Providers.MariaDb`

**Against.** Same driver, same wire protocol, the same SQL in all but a dozen places. Two providers would carry every
future fix twice, which is [D-46](../architecture-debt.md)'s shape built in on purpose.

### Alternative B — a dialect inside the MySQL provider, chosen by what the server says it is *(chosen)*

The provider asks the server's version when it connects; MariaDB says so in it. Each difference in §1a is one
branch in one place — the WKB functions, the catalogue query, the column DDL a history table needs, the extent, the
JSON reads, the GUID storages — and everything else is shared and tested once against both.

### Alternative C — leave MariaDB out, as ADR-208 inferred

**Against.** The owner chose otherwise, and the measurement says the cost is a dialect rather than a design.

## 3. Counterarguments to the preferred option

- **The SRID is not enforced.** A MariaDB table can hold shapes in two references under one declared SRID, and this
  server would read the second as if it were the first. Mitigated, not removed: a publish counts the rows whose SRID
  is not the column's and says so, and every shape this server writes carries the declared one.
- **No validity function.** A publish cannot say how much of a MariaDB table's geometry is valid; it says it could
  not be asked, which is the answer `MySqlTableChecks` already gives when a table cannot be asked.
- **Detection by version string** is a heuristic; a proxy that rewrites the version would be read as MySQL and fail
  on the first `'axis-order'` call, with MariaDB's own error.

## 4. Evidence

§1a, measured with `D:/temp/mariadb-probe.py` and two follow-ups against MariaDB 11.8.9 on the test server,
2026-10-20; the MySQL column from ADR-208 §1a.

## 5. Decision

**A registered MariaDB database is served by `Graticula.Providers.MySql` as a dialect**, to the scope ADR-208 gives
MySQL: reading, every relation and a distance, statistics, editing with GlobalIDs and row versions, relationships,
attachments and a history.

1. **The dialect is the server's own word for itself**, read once per connection string.
2. **Geometry crosses as WKB as stored** — x then y — with no axis option.
3. **Tables come from `GEOMETRY_COLUMNS`**; one whose column declares no SRID (0) is listed as skipped with the reason.
4. **The spatial filter and the decision are MySQL's arrangement** (ADR-208 §5.3), so answers stay PostGIS's.
5. **A publish counts rows whose SRID differs from the column's** and reports them, and validity as not measurable.
6. **JSON is read by casting `JSON_VALUE`**, in both dialects — one form, since MySQL accepts it too.
7. **A GlobalID may also be kept in MariaDB's `UUID` type.**

**`INFERRED` — for the owner to confirm:**

- **One kind, `mysql`**, for both servers, told apart when connected — so a MariaDB is registered as MySQL, and the
  console says *MySQL or MariaDB*. The alternative is a `mariadb` kind and a migration.
- **MariaDB 10.6 or later**, the first with `json_table`; measured on 11.8 only.
- **Rows in another SRID are reported at publish, not filtered** — a filter would hide them without saying so.

## Conditions

1. **The dialect, and reading parity**: registration and the probe on MariaDB, and ADR-208's two parity sets — 110
   relation queries and 87 query features, and the percentiles — asked of a MariaDB layer and a PostGIS layer holding
   the same rows, alike.
2. **Editing**: the MySQL writer's tests and its rule tests (26) passing against MariaDB, a UUID GlobalID included.
3. **Relationships, attachments and history** through REST, as ADR-208 condition 4 ran them.
4. **A CI job** against a `mariadb:11.8` container beside the MySQL one.
5. **The console** names MariaDB where it offers MySQL: the *Add a source* dialog, which now offers Oracle and
   MySQL says *MySQL* alone, and a MariaDB administrator should not have to guess that it means theirs.

## 6. Consequences

**Positive.** An organisation on MariaDB — the default MySQL of most Linux distributions — publishes in place.

**Negative.** A fourth database in CI. Two MariaDB weaknesses show through as reports rather than refusals: an
unenforced SRID and no validity check.

**State.** None new: the same kind and locator.

**Ports created.** None; a branch inside the MySQL provider.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | A MariaDB server's version string names it, through whatever sits between | `UNVALIDATED` |
| — | A customer's MariaDB spatial columns declare `REF_SYSTEM_ID` | `UNVALIDATED` |

## 8. Dependencies

**Depends on:** [ADR-208](ADR-208-a-registered-mysql-database-is-served-like-postgis.md),
[ADR-207](ADR-207-attachments-and-history-on-registered-layers.md).

**Depended on by:** —

## 9. Revisit triggers

MariaDB gaining `ST_IsValid` or an enforced SRID; a customer whose MariaDB sits behind a proxy that hides its version.

## 10. Dissent

None recorded.
