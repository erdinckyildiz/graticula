# ADR-116 — Update data may update the features a file matches and add the rest

| | |
|---|---|
| **Status** | `ACCEPTED` |
| **Confidence** | `HIGH` — two statements in the transaction append already runs |
| **Decided** | 2026-10-01, by owner decision (*"Sırayla git"*, the ArcGIS reviewer's second-pass item 6) |
| **Supersedes** | ADR-105's refusal of `upsert` |
| **Superseded by** | — |

---

## 1. Context

The ArcGIS reviewer: *"No upsert (refused, `ArcGisAppendEndpoints.cs:196`). Upsert on a matching field is the
standard nightly-sync script."* Portal's Append offers *Add features and update existing features*, matched on a
field the publisher chooses (`upsert=true`, `upsertMatchingField`), with `skipUpdates`, `skipInserts` and
`updateGeometry`. ADR-105 refused `upsert` by name until it was built.

## 2. Alternatives considered

### Alternative A — Update then insert from the staging table, in append's transaction (chosen)

**Argument for.** Append already copies the file into a temporary table and casts each column into the layer's;
upsert is an `UPDATE … FROM` the same table on the key, then the `INSERT` restricted to keys that matched nothing.
One transaction, so a value that does not fit leaves the layer as it was, as append does.

### Alternative B — `INSERT … ON CONFLICT`

**Argument against.** It needs a unique index on the matching field, which a hosted layer's columns do not have and
which Portal asks the publisher to create first. Checking the data instead refuses the same ambiguous cases without
asking anyone to change the layer.

## 3. Counterarguments to the preferred option

- *Checking uniqueness reads the matched keys of the layer.* Indexed or not, it is the same read the `UPDATE` makes.
- *A file row with an empty key is added*, not matched — Portal does the same.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| Portal's Append upsert and its companions | ArcGIS REST API *Append (Feature Service/Layer)* | publicly documented |
| A matched feature takes the file's values and shape, an unmatched row is added, a repeated key is refused and changes nothing | `UpdateDataConformanceTests.An_upsert_updates_what_matches_and_adds_the_rest` | this repository |

## 5. Decision

5.1 `PostGisImporter.AppendAsync` takes an `Upsert(MatchOn, SkipUpdates, SkipInserts, UpdateGeometry)`. Before
anything changes it refuses a key that appears twice in the file, and a key in the file held by more than one feature
of the layer. A matched feature takes the file's matched columns, and its geometry unless `updateGeometry=false` or
the row has none; rows whose key is empty or matches nothing are added. Not with `truncateExisting`.

5.2 ArcGIS's `append` and the native `/admin/hosted/{layer}/append` take `upsert=true` with `upsertMatchingField`
(natively also `matchOn`), `skipUpdates`, `skipInserts` and `updateGeometry`. The answer says how many were updated
and added; ArcGIS's `recordCount` is both.

5.3 Studio's Update data offers *Update matching features, add the rest* with a *Match on* list of the layer's
columns.

## 6. Consequences

**Positive.** The nightly sync from a source system works through Portal's own call and Studio's dialog.

**Negative.** No `useGlobalIds` matching yet; a GlobalID is matched like any other field when named.

**State.** None.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | Refusing ambiguous keys is acceptable where Portal would require a unique index | `INFERRED` |

## 8. Dependencies

**Depends on:** ADR-103 (Update data), ADR-105 (ArcGIS append).

**Depended on by:** —

## 9. Revisit triggers

- A client relies on `useGlobalIds=true` matching.

## 10. Conditions

1. **An upsert updates matched features and adds the rest, and refuses a repeated key with nothing written** —
   **DISCHARGED 2026-10-01**, `UpdateDataConformanceTests`.
2. **Studio offers it and asks for the field** — **DISCHARGED 2026-10-01**,
   `ItemStructureTests` (Update data).
