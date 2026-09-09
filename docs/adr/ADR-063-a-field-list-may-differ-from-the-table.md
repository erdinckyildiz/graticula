# ADR-063 — A field list may differ from the table, in two ways and no more

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` — the decision is the owner's and the shape is forced by what already exists, but nothing is built yet and the drift story is argued rather than measured. |
| **Decided** | 2026-09-09 |
| **Supersedes** | — |
| **Superseded by** | — |

---

## 1. Context

**[Q-36](../open-questions.md), open since 2026-08-12 and answered 2026-09-09 by owner
decision.** It asked whether a service definition may describe fields the physical table
does not have — computed, aliased, role-hidden — and warned in its own first line that
this was *cheap to allow now, expensive to retrofit*.

**The warning came true rather than being acted on.** The model was built with a field
list read from the table, so the retrofit is now the thing being estimated instead of the
thing avoided. What makes this decidable anyway is that the row narrowed itself twice
along the way, and what is left is sharper than what was asked.

**Half of the original question was already built and nobody recorded building it.** A
layer's *SRID* may already differ from what the table declares: `POST /admin/layers` takes
`srid` and `DeclaredReference` asks the table what it holds before the write, refuses a
mismatch, and makes `overrideDeclaredSrid: true` the thing somebody types deliberately.
So a definition already diverges from its table for one property, with a guard. **The
question is therefore not *may it diverge* but *may the field list diverge, and what stops
the two drifting*.**

**Three things in the product are already shaped for this and are the reason it is not
speculative.**

- **The alias slot exists and is filled with the column name.** `alias = field.Name` in
  `FeatureServerMetadataWriter`, `MapServerEndpoints` and `RelationshipEndpoints`. Every
  ArcGIS client reads that key and every one of them is being told the column name.
- **The importer already reads the real alias and throws it away.**
  `Graticula.Import.Reader` calls `field.GetAlternativeName()` and reports it under a
  comment that says exactly why it goes nowhere: *the geodatabase's own alias, which is
  what an operator reads in ArcGIS. Reported rather than used: our schema has no alias
  column, and saying so is better than dropping it silently.* It reads
  `GetDomainName()` in the same loop, with the same note. So a geodatabase import today
  carries an operator's field labels to the door of this product and drops them.
- **Schema drift has been measured**, twice, which is the input the row said every answer
  needs. [Q-43](../open-questions.md): with a cold memory a request naming a dropped
  column is refused **400** — *fields come from the database rather than the request* —
  and with a warm one every query answers **500** for up to thirty seconds and then the
  layer repairs itself. [Q-37](../open-questions.md): a request already answering
  finishes on the old shape, in full — 41,978,067 bytes describing a table that no longer
  exists.

## 2. The question

May a layer's field list differ from its table's, and if so how, and what keeps a
divergence from becoming a lie when somebody alters the table?

## 3. Alternatives considered

### Alternative A — no divergence; the table is the field list (rejected)

**Argument for.** It is what is built. A field list that is exactly the table's cannot go
stale, cannot be reviewed, and needs no migration. Every drift question disappears because
there is nothing to drift.

**Argument against.** It makes three things permanently impossible that the product
already half-does: an operator cannot label a column, a geodatabase import cannot carry
the labels it already reads, and a table with an internal column has no way to publish
without it. The alias key goes on being sent to every ArcGIS client filled with the column
name, which is not *no alias* but *a wrong one*.

### Alternative B — alias and hidden only (chosen)

A layer carries a per-column override list. An override may set a **display alias** and may
mark a column **hidden**. It may not create a column.

**Argument for.** Both are decorations on a column that exists, so the drift story is
short: an override that names a column the table no longer has is inert, and the field
list is still the table's. It fills a slot that already exists and a value the importer
already reads. And hiding is the cheapest form of the thing people actually ask for —
*publish this table without that column* — which is otherwise a database view.

**Argument against.** Hiding is a security-shaped feature that is not a security boundary,
and confusing the two is easy: a hidden column is absent from the document, and unless the
query path refuses it in filters and in `outFields`, it is still readable by anyone who
guesses its name. That is a condition below rather than a footnote.

### Alternative C — computed fields as well (rejected by the owner)

An override may also define a field the table does not have, from an expression.

**Argument for.** It is the whole of what Q-36 asked, and it removes the need for a
database view in the cases people meet first — a concatenation, a unit conversion, a
formatted date.

**Argument against.** It is a different kind of object. An expression has a language, a
type, an evaluation site, an injection surface and a cost per row, and it must be pushed
into SQL or it defeats every predicate that touches it. It also turns drift from *inert*
into *broken*: a computed field over a dropped column has no honest answer. **The owner
chose alias and hiding**, and this ADR does not build a foundation for expressions in
anticipation — [CLAUDE.md](../../CLAUDE.md) §82, and Q-36's own lesson is that the
expensive thing is the retrofit you did not scope, not the one you did not start.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| The alias slot exists and always carries the column name | `alias = field.Name` in three writers | `FeatureServerMetadataWriter`, `MapServerEndpoints`, `RelationshipEndpoints` |
| The importer reads real aliases and drops them | `alias = Nothing(field.GetAlternativeName())`, with a comment saying our schema has no alias column; nothing in `/src/Graticula.Host` or `/src/Graticula.Platform` consumes it | `Graticula.Import.Reader/Program.cs` |
| A definition already diverges from its table for one property | `srid` on `POST /admin/layers`, guarded by `DeclaredReference` and `overrideDeclaredSrid` | ADR-036-era work, D-156 |
| Drift is survivable and its behaviour is known | 400 on a cold read, 500 for up to thirty seconds on a warm one, then self-repair; an in-flight response finishes on the old shape | Q-43, Q-37 |

## 5. Decision

**A layer carries a list of per-column overrides. An override may set a display alias and
may mark the column hidden. It may not create a column, and it may not change a column's
type, nullability or name on the wire.**

**The field list is still read from the table.** Overrides are applied to it by column
name, after it is read. This is the whole of the drift answer: an override naming a column
that no longer exists is **inert** rather than broken — it describes nothing, so it says
nothing false — and a column added to the table appears with no alias and unhidden, which
is what it would have done before this decision. Nothing has to be reconciled on a
schedule, because nothing is cached that the table does not own.

**Hidden means hidden everywhere or it means nothing.** A hidden column is absent from the
layer document, absent from `outFields=*`, refused when named explicitly, refused in a
filter or `where` clause, refused as an `orderByFields` or statistics target, and not
writable. The refusal is the same one an unknown column gets, so a caller cannot tell a
hidden column from an absent one.

**The identity, geometry and any editor-tracking column may not be hidden**, because the
protocol requires them and a layer without them is not a layer. That is refused at the
point the override is set, not at query time.

## 6. Consequences

**Positive.** An operator can label a column, which is the first thing anyone does in
ArcGIS. A geodatabase import can carry the labels it already reads, closing a gap the
importer's own comment has been documenting. A table with an internal column can be
published without it and without a view. And [ADR-013](ADR-013-feature-service-data-model.md)
§5a's domains and subtypes have somewhere to live: they are the same shape of claim about a
column, and this is the machinery they attach to rather than a second one beside it.

**Negative.** A second place where a layer's shape is decided, which is the thing §1's
*expensive to retrofit* was about. Every field-list reader — three document writers, the
query path, the write path, the filter reader — now has to apply the overrides, and a
reader that forgets is a leak rather than a cosmetic bug. It is one migration on `layer`.
And hiding will be read as access control by somebody: it is per-layer, not per-role, and
this ADR does not make it per-role.

**Ports created.** None.

**State.** *Catalogue*: one new table or column carrying the overrides, keyed by layer and
column name — shared, in the platform store, expand-only. *Runtime*: none of its own; the
overrides travel with the layer definition that `ServiceContexts` already caches, and
expire with it.

## 7. Conditions

1. **Hidden is enforced on every path that names a column, and the test is a filter
   rather than a document.** A hidden column absent from the layer document and usable in
   a `where` clause is not hidden — it is searchable one predicate at a time, which is how
   a value is recovered without ever being displayed. Discharged by a test that names a
   hidden column in `outFields`, in `where`, in `orderByFields` and in an edit, and gets
   the same refusal an unknown column gets in all four.

2. **The identity and geometry columns cannot be hidden, and it is refused at write.** A
   check at query time is a check somebody can reach a state without passing.

3. **An override that names a column the table does not have is inert and visible.** Inert
   is the design; visible is the condition — an operator who renames a column in the
   database and loses a label must be able to find out why, so the admin surface reports
   overrides that match nothing rather than silently dropping them.

## 8. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| A-023 | A cheap schema fingerprint can be polled often enough to detect drift on registered sources without loading the source database | `UNVALIDATED` *(from [the register](../architecture-assumptions.md), which is where an assumption's status lives)* — **and this decision deliberately does not rest on it**, which is the useful thing to record here. §5's overrides are applied to a field list read from the table, so a stale override is inert and nothing has to notice that the table changed. A decision that needed drift *detected* would inherit an unvalidated assumption and an unbuilt fingerprint; this one needs drift *survived*, which is measured in Q-43 and Q-37 |

## 9. Dependencies

**Depends on** — [ADR-013](ADR-013-feature-service-data-model.md) for what a layer is.

**Depended on by** — [ADR-013](ADR-013-feature-service-data-model.md) §5a: domains and
subtypes are per-column claims and attach to this machinery.

## 10. Revisit triggers

- **Somebody asks for a computed field with a concrete case.** §3's Alternative C is
  written in its strongest form so that the answer starts from there rather than from
  scratch, and the thing that decides it is whether the expression can be pushed into SQL.
- **Hiding is asked for per role.** This makes it per layer. Per role is a different
  object — it multiplies the layer document by the caller — and it is the point at which
  [A-036](../architecture-assumptions.md)'s row-level security delegation becomes the
  cheaper answer.

## 11. Dissent

**The retrofit is the cost and it is being paid late, which was avoidable.** Q-36 said in
its first line, on 2026-08-12, that this was cheap then and expensive later. It was left
open for four weeks while the field path was built around the assumption it would never be
needed, and the estimate is now larger than the feature. Nothing about the decision is
wrong; the timing is, and recording that is the only use left in it.
