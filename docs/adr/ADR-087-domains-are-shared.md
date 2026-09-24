# ADR-087 — A domain is a named object that fields on many layers point at

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `HIGH` |
| **Decided** | 2026-09-24, by owner decision, in two answers. On 2026-09-23, asked ADR-065's first `INFERRED` item: *"Domain'ler paylaşılsın"* — domains are shared across layers, as a geodatabase shares them. On 2026-09-24, asked the four questions ADR-065 left to the ADR that builds it: who changes a shared domain — *its owner and administrators*; what deleting one in use means — *refused, and the refusal counts the layers*; where names are unique — *across the server*; what becomes of the domains stored on layers today — *converted to shared ones by the migration*. Each answer was the option recommended to the owner, and each is the enterprise geodatabase's own behaviour. |
| **Supersedes** | — (amends [ADR-065](ADR-065-domains-and-subtypes.md) §5, whose per-column domain becomes a reference; discharges its condition 5) |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

[ADR-065](ADR-065-domains-and-subtypes.md) stored a column's domain inside the layer's field overrides, so
an archive whose fifty classes used one `Material` wrote it fifty times, and changing it meant changing
fifty layers. The owner reversed that choice; ADR-065 §9 condition 5 carried the reversal and named the
three questions its Alternative B raised.

## 2. Alternatives considered

### Alternative A — a table of domains, fields store the id *(chosen)*

**Argument for.** One row per domain, so one edit reaches every field; a field's override keeps
`{"id": …}` where it kept the whole domain, so every consumer — the writer that enforces, the layer
document, `queryDomains`, the Fields page — reads a `FieldDomain` exactly as before, resolved at read time.

**Argument against.** A read has to resolve the id. Done inside the catalogue's existing select, only for a
layer whose overrides name one, so it costs no second round trip ([D-249](../architecture-debt.md)).

### Alternative B — keep each copy and rewrite all of them on an edit

**Argument for.** An older build still reads the copies, so no contract migration.

**Argument against.** Two records of one fact — the thing this repository keeps a rule against — and an edit
that has to find and rewrite every copy, subtypes included, in one transaction.

## 3. Counterarguments to the preferred option

- **A contract migration, at 55.** An older build reads `{"id"}` as a domain it cannot parse and stops
  refusing values outside it, which is the one failure a domain exists to prevent, so the rollback window
  closes at this release. Written into the migration's caution.
- **The same name with other values is refused through the admin surface and numbered on import.** A name
  a person typed and a different one stored would surprise them; an import nobody typed would otherwise lose
  a layer for a word. The import report says what was renamed.
- **A shared domain outlives the layers that used it**, as a geodatabase's does. The Domains screen lists
  the ones nothing uses, and deletes them.

## 4. Evidence

- **Migration 55 on a copy of the fixture store**, seeded with three layers: two `Material` lists with the
  same codes became one row that both fields point at; a third `Material` with other codes became
  `Material (2)`; a `Pressure` range on a column and on a subtype became one row both point at.
- **`DomainConformanceTests.A_shared_domain_is_one_object_every_field_points_at_and_one_edit_reaches_them_all`**
  against a running server: two layers given one domain by name share one row with two uses; one edit
  reaches both layers' documents and both writers (the new code is written, one outside it refused); a
  change that does not fit a column is refused naming it; the same name with other values through a layer is
  `409`; deleting it in use is `409` naming both layers; taken off both fields, it is deleted. **Controlled:**
  with the override writing the domain whole instead of by id, the test fails — the second layer's document
  still carries two codes after the edit.
- `A_domain_the_document_reports_is_refused_outside_on_both_faces`, ADR-065's, passes over shared domains. It
  failed once in a full run, and the cause was the design working: a console run that died mid-test had left a
  `Material` with other values, and the test's save of `Material` was the `409` §5.4 says it is. The test now
  clears unused domains of its names first, and passes with one planted.
- `SharedDomainsScreenTests` and `FieldsValuesPageTests` (console): the Domains screen names where each is used,
  offers Delete only where it works, and saves a change with one `PUT`; the Values editor shows a shared domain
  read-only, keeps a typed draft across the choices, refuses a taken name at Done, and sends no domain write.

## 5. Decision

5.1 **`field_domain`** (migration 55): id, name unique without case across the server, the ArcGIS domain
object as `definition`, the owner, when it changed. A field's override, and a subtype's per-column domain,
store `{"id": …}`.

5.2 **Resolved in the catalogue's select**, by a subquery that runs only for a layer whose overrides name an
id, so every face reads the domain it always read.

5.3 **`/admin/domains`** — `GET` (each with where it is used and whether the caller may change it), `POST`,
`PUT /{id}`, `DELETE /{id}` — under `content:publishFeatures`. Changing or deleting needs the domain's owner or
an administrator. A change is judged against every field that uses it — the column's type for a column's
domain, the whole set of subtypes, starting values included, for a subtype's — and refused naming the first it
does not fit. A delete of one in use is `409` naming where it is used.

5.4 **A layer's fields name a shared domain by id, or give one whole.** Whole with an id must say what the
shared one says — its values are changed on the domain, where that judgement runs. Whole without an id is the
shared domain of that name when it says the same, `409` when it says something else, and a new shared domain
owned by the caller when the name is free. New domains are made only after every other check of the save has
passed.

5.5 **A geodatabase import lands each domain once**: the shared domain of that name when it says the same,
else the name numbered — `Material (2)` — and reported.

5.6 **The console: a shared domain is chosen on a layer and changed on the Domains screen.** A column's Values
editor offers the shared domains that fit the column — a list of numbers for a number column, of text for a text
column, a date range for a date column — shows the one chosen read-only, names the other fields that use it, and
links to it on Studio's *Domains* screen. A new list is named there, a name already taken is refused at Done
rather than after Save, and leaving a shared one for a new one starts from a copy of it under a name of its own.
The Domains screen lists every shared domain with its values and where it is used, changes one in its own editor
with its own Save — confirming the fields it reaches by name — creates one before any field uses it, and deletes
one nothing uses.

**Revised after the design review of 2026-09-24**, whose blocker was the first version's: a shared domain edited
on a layer's Fields page was written to the domain before the layer's fields, so a refusal of the second left
every other layer changed and the page saying *refused*. Changing a domain in one place, in one write, removes
that case rather than reporting it. A fault the review did not reach was found the same day by ADR-088's: the Domains screen's new-domain kind
redrew nothing unless a layer's Fields editor was open, through the console's shared change listener; repaired
there, with a test that fires it with none open. The review's other findings — a typed draft lost by arrowing through the
choices, lists offered that could not fit the column, the warning counting rather than naming, the new-list
path not saying the name must be new, read-only boxes that looked editable, a date range printed in
milliseconds, the screen scrolling sideways at 390 px — are repaired as it described them.

## 6. Consequences

**Positive.** One `Material` for fifty classes; one edit, judged once against every use.

**Negative.** The rollback window closes at 55. A name is taken server-wide, so two publishers who want
different `Status` lists name them differently.

**Ports created.** `IFieldDomainStore`.

**State.** `field_domain` in the platform store; references in `layer.field_overrides`.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | Domains number in the hundreds, not millions, so listing them all for a save is cheap | Unmeasured; a geodatabase with a thousand domains is large |

## 8. Dependencies

**Depends on:** [ADR-065](ADR-065-domains-and-subtypes.md), [ADR-063](ADR-063-a-field-list-may-differ-from-the-table.md)
(the overrides that carry the reference), [ADR-075](ADR-075-a-layer-is-edited-by-its-owner.md)
(owner or administrator).

**Depended on by:** —

## 9. Revisit triggers

- Two publishers on one server asking for the same domain name with different values.
- A save of a layer's fields measured slow because of the shared-domain listing.

## 10. Dissent

None recorded.

## 11. Conditions

1. **An Esri-written archive whose classes share a domain lands it once** — measured on a real archive, the
   way ADR-065 §4 measured coded values and ranges; the path is the one the conformance test drives through
   the admin surface, not yet through an import.
2. **The showcase is migrated** — 55 is a contract, so the showcase's backup-and-copy rehearsal before it is
   the evidence that the conversion is right on real data. **PARTLY DISCHARGED 2026-09-24**: v1.0.164 migrated a
   copy and then the showcase to 55 without error, and it serves. But the showcase had no layer with a domain, so
   the conversion itself ran on nothing there; the evidence that it converts correctly is still the seeded copy of
   §4. Discharged by the first real store with domains going through it.
