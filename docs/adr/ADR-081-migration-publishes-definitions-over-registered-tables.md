# ADR-081 — Migration publishes an ArcGIS Server's layer definitions over the tables they already read

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` |
| **Decided** | 2026-09-19. **The scope is the owner's** — [Q-16](../open-questions.md): *inventory plus definition import, and free*, with the data staying where it is. **How the second step works is this ADR's.** |
| **Supersedes** | — |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

Q-16 split migration into two steps. The first, `graticula tools inventory`, was built on 2026-09-15. It
reads an ArcGIS Server's services directory and says what comes across. The second, importing the
definitions, was not built. An operator who read the inventory then had to publish every layer again by
hand: choose the table, the geometry column and the object id, and redraw the symbology.

**A REST layer document does not say which table it reads.** That is the whole difficulty. The service
knows its layer as `Parcels`. The database knows the table as `GISDB.GISOWNER.PARCELS`, or as
`parcels`, or as something else. The only link between them is the name.

## 2. Alternatives considered

### Alternative A — a plan file between two commands *(chosen)*

**Argument for.** `plan` reads both servers and writes, for every source layer, the table it matched by
name, or why it matched none. A person reads the file, fills in or deletes what was not matched, and then
`apply` publishes exactly what the file says. The guess is made in the open, and a person checks it before
anything is published.

**Argument against.** Two commands and a file to edit, where one command would be less work.

### Alternative B — one command that matches and publishes

**Argument for.** Less to do.

**Argument against — and it decides.** A wrong match publishes somebody's service over the wrong table,
and the result looks correct: it has features and it draws. Nothing about it says it is wrong.

### Alternative C — ask the source for the table (the admin API, or the service's manifest)

**Argument for.** ArcGIS Server's admin API can describe a service's data store and its tables exactly.

**Argument against.** It needs admin credentials on the server being left. It is not the REST surface that
the inventory reads without configuration. It answers in the source's names for a database, which is
registered here under a different name. This could be a later improvement to how a plan is filled in; it
is not a reason to wait.

## 3. Counterarguments to the preferred option

Matching by name is weak. The rule is the last dotted part, letters and digits only, compared in lower case.
Two tables in different schemas with the same name tie. A layer renamed at publish time matches nothing.
Both cases leave the entry empty and say why, so the failure is a gap in the plan, not a wrong layer.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| Matching leaves ties, missing object ids and absent tables empty, with the reason | five cases | `MigrationPlanTests` |
| `plan` signs in, reads the directory and the data source's tables, and writes a plan | the CI server read as its own source | `MigrationAgainstARunningServerTests` |
| `apply` publishes over a registered table, with the drawing and an alias, and the layer document then shows them | `alias: "Ad"`, `renderer.type: "simple"` read back from the layer document | same |

## 5. Decision

1. **`graticula tools migrate plan <source> --to <target> --datasource <name>`** reads the source's services
   directory, skipping a MapServer when the same service is also a FeatureServer, and reads the tables of
   the target's registered data source. It writes a plan in which every layer has the table it matched,
   its geometry column, object id, SRID and type, its field aliases and its `drawingInfo`.
2. **A match is one table with the same name and an integer object id.** A tie, a table with no object id,
   or no table at all leaves the entry empty and gives the reason. Nothing is published.
3. **`graticula tools migrate apply <plan> --to <target>`** publishes every entry that has a table, through
   the target's own admin API (`POST /admin/layers`). It then sends the `drawingInfo` to the symbology
   endpoint, which reads it as it is, and the aliases to the field overrides. A refused drawing or alias
   is reported and the layer stays published. A layer that already exists is skipped.
4. **Private by default**, because the source's sharing was not read. Use `--sharing` to change it.
5. **The tool is a client of both servers.** It needs neither this process's store nor its key. The target
   account comes from `GRATICULA_USER` and `GRATICULA_PASSWORD`, and a source token from
   `GRATICULA_INVENTORY_TOKEN`, never from the command line.

## 6. Consequences

**Positive.** Q-16's second step exists. A layer comes across with its table, its service and folder, its
drawing and its field labels.

**Negative.**
- Sharing, group membership, editing capabilities, time settings, domains and subtypes are not carried.
  Each is set again here.
- A renderer that the symbology conversion cannot read is left behind, and the report says so.
- GeoServer is still not read (Q-16's inventory half is ArcGIS only too).

**Ports created.** None.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | A layer is usually published under its feature class's name | Unmeasured — condition 1 |

## 8. Dependencies

**Depends on:** [Q-16](../open-questions.md), the inventory (`graticula tools inventory`), [ADR-017](ADR-017-admin-api.md)'s
publish endpoint, the symbology conversion, the field overrides.

**Depended on by:** —

## 9. Conditions

1. **A real ArcGIS Server is planned against, and the share of layers matched by name is recorded.** If it
   is low, the name rule is the wrong rule. *(Open — needs a source server this repository does not have.)*

**State.** None.
