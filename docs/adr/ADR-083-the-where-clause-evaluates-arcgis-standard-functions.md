# ADR-083 — The where clause evaluates ArcGIS's standardized functions

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` |
| **Decided** | 2026-09-23, by owner decision. Asked which SQL functions `where` should evaluate — the fourth ArcGIS review's V-75, after `CAST(…)` was answered *'CAST' is not a field of this layer* — the owner chose *"ArcGIS'in standart listesi"*: the functions ArcGIS documents for standardized queries, and arithmetic. Which functions that list holds is read from ArcGIS Online's published SQL reference (§4); the shape below is this ADR's. |
| **Supersedes** | — (widens the grammar [WhereClause](../../src/Graticula.Core/Features/WhereClause.cs) described as closed, and amends [ADR-008](ADR-008-query-engine.md) §4a's *no function calls, arithmetic or column-to-column comparison*) |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

`where` is the parameter every ArcGIS client builds, and until this decision its grammar compared a
column with a literal, folded case through `UPPER`/`LOWER` of a bare column, and evaluated date
keywords. Everything else was refused, deliberately: the grammar exists because the obvious
implementation — paste the clause after `where` — is remote code execution on the datastore, and a
grammar that is closed cannot be widened by accident.

ArcGIS clients do not stay inside that. Dashboards and Experience Builder filter with
`EXTRACT(YEAR FROM date_field) = …` and `CHAR_LENGTH`, the Maps SDK's search writes
`UPPER(TRIM(field))`, scripts written against ArcGIS Enterprise use `CAST`, `SUBSTRING` and arithmetic
between fields, and ArcGIS publishes the list of what a standardized query may contain. Each of those
was a 400 here. V-75's message half repaired *'CAST' is not a field*; which functions to evaluate was
put to the owner, who chose the published list.

## 2. Alternatives considered

### Alternative A — the published standardized list, as a typed expression tree *(chosen)*

**Argument for.** The list is what clients generate from, so it is the list that answers the most
clauses a client actually sends; being ArcGIS's, it is a boundary somebody else drew, and anything
outside it is refused with the list rather than with a guess. Building expressions as a tree keeps the
property the grammar exists for: no text the caller wrote reaches SQL — columns are matched against the
layer and re-quoted, literals are bound, functions are enum members spelled by the emitter.

**Argument against.** It is a real grammar, with arithmetic precedence and a bracket that can open
either a group of predicates or a value, and two datastores that spell some functions differently.

### Alternative B — the four the question named (`CAST`, `SUBSTRING`, `COALESCE`, arithmetic)

**Argument for.** Less grammar, less to spell per dialect.

**Argument against.** The expression tree, the dialect object and the bracket ambiguity are the whole
cost, and four functions pay all of it; the rest of the list is a line each. And it is a list nobody
else publishes, so every refusal of `CHAR_LENGTH` would be a decision this project took alone.

### Alternative C — none; keep the refusal and its message

**Argument for.** The grammar stays small and every client learns its limits from a sentence.

**Argument against.** The owner decided otherwise, for the reason V-75 gave: the clauses are ordinary.

## 3. Counterarguments to the preferred option

- **A wider grammar is a wider attack surface.** Mitigated rather than argued away: the new nodes are
  enum members and bound values, the emitter re-matches every column, both parser and emitter cap
  nesting, and the injection tests stand unchanged. One old line moved, and §5 says which.
- **The two datastores disagree.** They do, measurably (§4), and a function that answers differently
  from a PostGIS layer and a GeoParquet layer is a wrong answer on one of them. That is why the
  disagreements are named and fixed in one object, and why an agreement test runs every one of them
  against both databases.
- **Type checks need column types.** A caller that does not give them gets no checks and the database's
  own error, which is what it got before.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| ArcGIS's standardized list | Date: `CURRENT_DATE()`, `CURRENT_TIME()`, `CURRENT_TIMESTAMP()`, `EXTRACT`; numeric: `ABS`, `CEILING`, `FLOOR`, `COS`, `SIN`, `TAN`, `LOG` (natural), `LOG10`, `POWER`, `ROUND`, `TRUNCATE`, `MOD`, `NULLIF`; string: `CHAR_LENGTH`, `CONCAT`, `CURRENT_USER`, `POSITION(a, b)`, `SUBSTRING(s, start, length)`, `TRIM(BOTH\|LEADING\|TRAILING ' ' FROM s)`, `UPPER`, `LOWER`; `CAST(number AS FLOAT\|INT)`, `CAST(string AS DATE\|TIME)` | ArcGIS Online, *SQL support in ArcGIS Online* (doc.arcgis.com/en/arcgis-online/reference/sql-agol.htm), read 2026-09-23. The Enterprise page did not answer. |
| `7 / 2` is 3 in PostgreSQL and 3.5 in DuckDB | One-row table, both engines | Measured 2026-09-23, PostgreSQL 16 (local portable cluster) and DuckDB 1.5.5 — the version DuckDB.NET 1.5.5 carries |
| `round(double, n)`, `trunc(double, n)`, `mod(double, n)` do not exist in PostgreSQL; DuckDB has them | Same table: PostgreSQL *function does not exist*; DuckDB 7.5, 7.5, 1.5 | Same measurement |
| `cast(s as varchar(4))` cuts in PostgreSQL and not in DuckDB | `'  Ankara  '` → `'  An'` and the whole string | Same measurement |
| `cast(x as float)` is eight bytes in PostgreSQL and four in DuckDB | DuckDB `float` is `REAL` | Same measurement, and DuckDB's type list |
| `CAST('09/23/2026' AS DATE)` works in PostgreSQL and not in DuckDB | DuckDB: *invalid date field format* | Same measurement |
| Every function selects the same rows from both | 20 clauses over 1,500 generated rows, each selecting some and not all, ids and counts equal | `GeoParquetAgainstPostgisTests.The_standardized_functions_select_the_same_rows_from_both_providers`, passing locally 2026-09-23 |
| The ArcGIS face answers them | `floors / 2 = 1`, `MOD(floors, 2) = 0`, `CAST(built AS VARCHAR(2)) = '19'`, `(floors + 1) * 2 > 6` answered; `SUBSTRING(floors, 1, 1)` refused naming the field | Local fixture, `hosted/ci_buildings`, 2026-09-23 |
| `supportsSqlExpression` is not this | The flag is for expressions in `outStatistics`, `groupBy` and `orderBy` | Esri, *Layer (Feature Service)* reference, read 2026-09-23 |

## 5. Decision

**`where` evaluates ArcGIS's standardized functions and `+ - * /`, as a typed expression tree the emitter
spells per datastore.**

5.1 **The list.** `CAST` (to `INT`/`INTEGER`, `SMALLINT`, `BIGINT`, `FLOAT`/`DOUBLE [PRECISION]`, `REAL`,
`VARCHAR`/`CHAR`/`TEXT` with an optional length, `DATE`), `EXTRACT(YEAR|MONTH|DAY|HOUR|MINUTE|SECOND FROM …)`,
`CHAR_LENGTH`, `CONCAT`, `POSITION(a, b)` and `POSITION(a IN b)`, `SUBSTRING(s, start[, length])` and
`SUBSTRING(s FROM start [FOR length])`, `TRIM([BOTH|LEADING|TRAILING] [' '] [FROM] s)`, `UPPER`, `LOWER`,
`ABS`, `CEILING`, `FLOOR`, `ROUND(n[, places])`, `TRUNCATE(n[, places])`, `MOD`, `POWER`, `LOG` (natural,
as ArcGIS defines it), `LOG10`, `SIN`, `COS`, `TAN`, `NULLIF`, and `COALESCE`, which the owner's question
named and the published list does not. `CURRENT_DATE()` and `CURRENT_TIME` join the date keywords.

5.2 **Not evaluated, with a reason.** `CURRENT_USER` — the clause is parsed without knowing who sent it.
`CAST … AS TIME` — no column here holds a time of day. Any other function is refused with the list.

5.3 **The model.** `ScalarExpression` (column, constant, arithmetic, negation, function call, cast,
extract, trim) and five predicate records that take one where a function, a cast or arithmetic was
written. **A bare column keeps every rule it had**: its literals are typed against it, `UPPER(column)` is
still the case-insensitive comparison, and a literal on the right still builds a `Comparison` — so every
tree built before this decision is built identically after it, and WFS and OGC API Features, which never
produce the new records, are untouched.

5.4 **The dialect.** `SqlDialect` holds what §4 measured: DuckDB divides two integers with `//` so both
truncate, PostgreSQL casts to `numeric` for `ROUND`, `TRUNCATE` and `MOD`, a text length is written as a
`substring` in both, `FLOAT` is written `double precision` in both, and a written date is read once by the
parser. The placeholder, which was the only dialect difference until now, moved into it.

5.5 **Types are checked where the caller gave them.** `SUBSTRING(population, 1, 2)` is refused with
*SUBSTRING takes text, and 'population' holds a number*; a comparison of text with a number is refused;
arithmetic is for numbers. Every caller in this server gives the types.

5.6 **One line of the old grammar moves, and one does not.** A field compared with a field —
`pop2020 > pop2010`, which arithmetic makes unavoidable anyway — is accepted; it was in the injection
test's list as *harmless-looking* and it is rebuilt from the layer's own column list like any other.
**A test with no field in it stays refused**: `'a' = 'a'`, `1 < 2`, `CHAR_LENGTH('abc') = 3` are the
same for every row, which is the shape a tautology probe takes, and a function over a literal means
nothing a caller needs. `1=1` alone is answered as it always was, before the grammar.

## 6. Consequences

**Positive.** The clauses Dashboards, Experience Builder, the Maps SDK and Enterprise scripts write are
answered, on PostGIS and GeoParquet alike, with the same rows. A function outside the list is refused by
name with the list. The emitter's dialect difference has a home instead of a placeholder parameter.

**Negative.** The grammar is larger, and a bracket is now read twice when it opens a value. Integer
division truncates on DuckDB where DuckDB itself would not — correct for this product, surprising to
anybody reading DuckDB's documentation. `SUBSTRING`'s start and `ROUND`'s places must be literal whole
numbers, which ArcGIS's examples are and SQL does not require. `MOD` and `ROUND` with places over a
PostGIS double go through `numeric`, which is exact and slower.

**Ports created.** None.

**State.** None: nothing is stored. The grammar and the dialects are code, and a parse holds its tree for
the length of one request.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | The published ArcGIS Online list is the standardized list clients generate from | Read from the published page; the Enterprise page could not be reached on the day |

## 8. Dependencies

**Depends on:** [ADR-008](ADR-008-query-engine.md) §4a and §4a-i (the grammar, and the one permitted SQL in
the model), [ADR-066](ADR-066-geoparquet-layers-read-by-duckdb.md) §4 (the second dialect).

**Depended on by:** —

## 9. Revisit triggers

- A client measured sending a function outside §5.1 in an ordinary workflow.
- A third datastore, whose spellings are measured and added to `SqlDialect` rather than assumed.
- A request for `CURRENT_USER`, which needs the caller's name threaded into the parse — ArcGIS's
  `supportsCurrentUserQueries`.

## 10. Dissent

None recorded.

## 11. Conditions

1. **The two datastores are held to the same rows in CI**, not only locally.
   `The_standardized_functions_select_the_same_rows_from_both_providers` runs in the datastore job; this
   is discharged by the first green CI run that includes it.
2. **An ArcGIS client's own clause is answered.** The clauses in §4 were written by hand from ArcGIS's
   list; one captured from Dashboards or Experience Builder against this server — the date filter
   Dashboards builds is the likeliest — is what says the list is the one they send.
