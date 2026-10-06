# ADR-165 — OGC API Features filters in Basic CQL2 text

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` — each claimed class has a case that observes it; no OGC test suite for Part 3 has been run |
| **Decided** | 2026-10-03 — follows from the owner's *"Sonra da OGC tarafına geç"*; it reverses ADR-042 §5's omission of Part 3, which was a choice of scope rather than a decision against it |
| **Supersedes** | — |
| **Superseded by** | — |
| **Amends** | [ADR-042](ADR-042-ogc-api-features.md) §5 (Part 3 not claimed) |

---

## 1. Context

OGC API Features Part 3 adds `filter`, a query language for `items`, and a `queryables` resource saying what may be
named in it. ArcGIS's OGC feature service and QGIS's OGC API provider send it. This server filtered `items` by `bbox`,
`datetime` and a property's value only, and ADR-042 §5 left Part 3 out as *a query language rather than a parameter*.
That language, at its Basic level, is the comparison grammar this server already parses for every ArcGIS `where`.

## 2. Alternatives considered

### Alternative A — CQL2 text translated into the where grammar, at Basic CQL2 (chosen)

- `filter` with `filter-lang` `cql2-text` (the default) is translated where CQL2 and the where grammar differ —
  `TIMESTAMP('…')` and `DATE('…')` to the quoted instant, bound by the column's type; `CASEI(…)` to `UPPER(…)`, and
  of a literal to the literal in capitals — and parsed by the grammar every other filter here goes through, then
  combined with `bbox`, `datetime` and property parameters.
- Spatial (`S_…`), temporal (`T_…`) and array (`A_…`) functions are refused by name, pointing at `bbox` and
  `datetime`. `cql2-json` is refused naming `cql2-text`.
- `…/collections/{id}/queryables` is a JSON Schema of the collection's properties with their types, linked from the
  collection.
- Claimed: Part 3's `queryables`, `queryables-query-parameters`, `filter`, `features-filter`; CQL2's `cql2-text` and
  `basic-cql2`, each with a case in `EveryConformanceClaimIsProvenTests`.

### Alternative B — A CQL2 parser of its own

**Against:** a second grammar for the same comparisons, which would drift from the one ArcGIS clients exercise.

## 3. Counterarguments to the preferred option

- *The where grammar refuses some Basic CQL2 a strict reader accepts*: a comparison of two literals, which it
  refuses as the shape of an injection probe; boolean literals.
- *No Advanced CQL2 claimed* although `LIKE`, `BETWEEN` and `IN` work.
- *Part 4 stays unclaimed*: writing works (ADR-042 §5b) and v1-scope withholds the claim until the surface is checked
  against that specification.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| The collection links its queryables, a JSON Schema with typed properties; a CQL2 filter narrows `items`; `cql2-json` and a spatial function are refused | `EveryConformanceClaimIsProvenTests.Filter_and_queryables_narrow_items_in_basic_cql2_text` | this repository |

## 5. Decision

As §2, Alternative A.

## Conditions

1. OGC's Part 3 / CQL2 executable test suite is run against the server, or the claim is narrowed to what it passes.
   *(2026-10-06: there is no such suite to run. OGC's `opengeospatial` organisation has executable suites for OGC API
   Features Part 1, Tiles, Processes, Maps, EDR and Coverages and none for Part 3 or CQL2, and Docker Hub's `ogccite`
   has no image for either. The condition stays open — it is met the day one is published — and the claim stands on
   this repository's own tests.)*

## 6. Consequences

**Positive.** QGIS and ArcGIS filter OGC API collections on the server.

**Negative.** Six more claims a test suite can hold the server to.

**State.** None.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | Basic CQL2 text and the where grammar agree on everything Basic CQL2 means, outside the translations above | Checked by reading CQL2's BNF; unvalidated against its test suite (condition 1) |

## 8. Dependencies

**Depends on:** ADR-042.

**Depended on by:** —

## 9. Revisit triggers

- A client sending `cql2-json` or a spatial filter.
