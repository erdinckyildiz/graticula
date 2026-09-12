# ADR-064 — Editor tracking, and what `features:edit` means

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` |
| **Decided** | 2026-09-11, building the owner's decision of 2026-09-09 ([Q-58c](../open-questions.md), [ADR-013](ADR-013-feature-service-data-model.md) §5a) |
| **Supersedes** | — |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

[D-20](../architecture-debt.md): `features:edit` is narrower here than in ArcGIS Portal. Portal's
*edit* covers adding features and changing *your own*; *full edit* reaches everybody's. This
server maps adds to `features:edit` and **every** update and delete to `features:fullEdit`,
because *your own* cannot be enforced when the server cannot tell whose feature is whose. The
narrower privilege is a consequence of that gap, not a design.

The owner decided on 2026-09-09 that editor tracking enters v1, and ADR-013 condition 6 set the
bar: *editor tracking closes D-20 or it has not been built* — four columns with the privilege
still narrowed would add a data model and keep the defect.

Q-58c adds the constraint on *where*: the values live with [Q-36](../open-questions.md)'s
machinery rather than beside it, because a per-column claim — an alias, a hidden flag, a domain,
and now *this column records who created the row* — built in two places is how two places come
to disagree. That machinery is [ADR-063](ADR-063-a-field-list-may-differ-from-the-table.md)'s
field overrides.

What an ArcGIS client expects is publicly documented in the ArcGIS REST API reference for the
feature layer resource: an `editFieldsInfo` object naming `creatorField`, `creationDateField`,
`editorField` and `editDateField`, and an `ownershipBasedAccessControlForFeatures` object with
`allowOthersToQuery`, `allowOthersToUpdate` and `allowOthersToDelete`. Those two objects are the
whole of the contract a client reads; how Portal maintains the columns is not reproduced here.

## 2. Alternatives considered

### Alternative A — four fixed columns on every hosted table

**Argument for.** Portal's hosted layers use `created_user`, `created_date`, `last_edited_user`
and `last_edited_date`, and an operator coming from Portal recognises them. Creating them on every
import makes tracking a property of the product rather than a setting.

**Argument against.** It says nothing about registered layers, where the table is the customer's
and this server does not issue DDL — and a registered layer with its own tracking columns, which
is the ordinary case for data migrated out of an enterprise geodatabase, would stay untracked. It
also adds four columns to every table whether or not anybody edits it.

### Alternative B — a role on a column, in the field overrides *(chosen)*

**Argument for.** One mechanism for hosted and registered alike: a column is *given* a role, and
a hosted table can have the columns added first through the path ADR-058 already built. It is the
machinery Q-58c names, it inherits ADR-063's drift answer (a role naming a column that has gone
matches nothing and tracks nothing), and the one place every serving face reads a field list from
already exists.

**Argument against.** A role is a setting, and a setting can be absent: a layer nobody configured
is untracked and keeps D-20's narrow rule. That is the honest state for a layer whose owner has not
said which column records the creator, but it means closing D-20 is per layer rather than global.

### Alternative C — a separate `editor_tracking` column on `layer`

**Argument for.** Four named slots are easier to validate than roles scattered over a list.

**Argument against.** It is the second mechanism Q-58c forbids. The Fields page would show a
column's label and hidden flag from one place and its tracking role from another.

## 3. Counterarguments to the preferred option

**Replacing a value the client sent is a silent change**, and this repository's rule is that
nothing degrades silently. The answer is that the document is the contract: a tracked column is
advertised `editable: false`, and a client that sends it anyway is a client echoing back a row it
read — ArcGIS web clients send every attribute on an update. Refusing would fail an ordinary edit
for a value the client never meant to change. So the value is replaced, and the replacement is
stated here and in the layer document rather than discovered.

**A feature with no creator is nobody's**, so a `features:edit` holder cannot change any row that
existed before tracking was turned on. That is narrower than they might expect on day one. It is
also the only answer that does not guess: *probably yours* is the guess D-20 refused to make.

**The name recorded is the account's name**, and names can be reused after an account is deleted.
Portal records usernames too, and the alternative — a principal id — is not something an ArcGIS
client can display or compare.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| The two objects are what a client reads | ArcGIS REST API, *Layer / Table* resource: `editFieldsInfo`, `ownershipBasedAccessControlForFeatures` | public reference |
| D-20's narrowing is the only reason updates need `fullEdit` | `Program.ApplyEditsAsync` and `OgcFeaturesEndpoints.Writes` both cite D-20 at the check | code, 2026-09-11 |
| The field list every face validates against is one object | ADR-063 §6, `FieldOverrides.Apply` | code |
| The writer enforces *your own*, and a row with no creator is nobody's | `EditorTrackingWriterTests` 4/4 against PostGIS. **Falsified**: with the ownership predicate removed from the `where`, `Own_only_changes_and_deletes_your_own_and_refuses_the_rest` fails on `Assert.False` and the other three still pass — so the test is about the predicate, not about the fixture | tests, 2026-09-11 |
| A client's value for a tracked column is replaced, on add and on update | `An_add_is_signed_by_the_account_and_dated_by_the_database` sends a creator of its own and a 1999 edit date; `An_update_signs_the_editor_and_leaves_the_creator` sends the caller as creator of somebody else's row. Both are overwritten | tests, 2026-09-11 |
| Both faces, two real accounts, one running server | `EditorTrackingConformanceTests` passes against the console fixture: ArcGIS results refuse *somebody else's* and *no creator*, OGC answers `204`/`403`/`403`, the five bad role settings are `400`, and the tracked layer's document carries both objects with `created_user` not editable. The full conformance suite ran around it, 499 of 502, the three failures environmental — two need the server's log and one a second publishable table — and the two log tests pass with the log given | fixture 8461, 2026-09-11 |

## 5. Decision

**A column may be given one of four roles through the layer's field overrides — `creator`,
`created`, `editor`, `edited` — and a layer with a `creator` column is editor-tracked.** The
creator and editor columns must be text; the created and edited columns must be dates; a role is
held by at most one column, and a tracked column cannot be hidden. On an add this server writes
the caller's account name into the creator and editor columns and **the database's clock** into
the created and edited columns; on an update it writes the editor and edited columns. A value a
client sends for a tracked column is replaced. **On a tracked layer, a caller whose right to edit
comes only from `features:edit` may update and delete the features whose creator is them**; a
feature with no creator needs `features:fullEdit`, as does everybody else's, and editing conferred
by a group (ADR-036 §4a) reaches every feature as it does today. **An untracked layer keeps the
narrow rule**, because there the server still cannot tell. The layer document carries
`editFieldsInfo` and `ownershipBasedAccessControlForFeatures` for a tracked layer, marks tracked
columns not editable, and offers `Update` and `Delete` to a `features:edit` holder. A hosted layer
can have Portal's four columns added and assigned in one action from its Fields page.

## 6. Consequences

**Positive.** D-20 closes on every layer that is tracked, and `features:edit` means on those layers
what it means in Portal. Registered data that already carries tracking columns can be tracked
without DDL. The roles are a fourth key in a JSON array an older build already reads, and an older
build ignores it — which leaves updates needing `fullEdit`, the safe direction for a downgrade.

**Negative.** Closing D-20 is per layer. A features:edit holder cannot change rows that predate
tracking. The ownership test is a predicate on every update and delete of a tracked layer, and a
refusal costs one more look inside the transaction to tell *not yours* from *not there* — the same
shape as ADR-005's precondition check, and only on the path that refuses.

**Ports created.** None. The writer takes the roles and the caller's name on the batch.

**State.** *Catalogue*: a `tracks` key on entries of `layer.field_overrides`, no migration.
*Runtime*: none.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| A-027 | Writes that bypass this server are not seen by it | **Holds, and bounds this.** A row written by psql has whatever creator the writer put there, or none — this server does not see it and does not pretend to |

## 8. Dependencies

**Depends on** ADR-013 (§5a, condition 6), ADR-063 (the overrides), ADR-036 (group editing),
ADR-058 (adding a hosted column), ADR-018 (privileges).

**Depended on by** [ADR-065](ADR-065-domains-and-subtypes.md) — domains and subtypes, the rest
of §5a, which sit on the same overrides and take no domain on a column with a role.

## 9. Conditions

1. **D-20 closes, measured on both writing faces.** A `features:edit` holder updates and deletes
   their own feature and is refused on another's and on one with no creator, through ArcGIS
   `applyEdits` and OGC API Features alike; a `features:fullEdit` holder is refused nothing. This
   is also ADR-013 condition 6. **DISCHARGED 2026-09-11** — `EditorTrackingConformanceTests`,
   against a running server with an administrator and an `editor` user type. Through ArcGIS
   `updateFeatures` and `deleteFeatures` the editor changes and deletes its own feature and is
   refused on the administrator's and on one added before tracking was on; through OGC API
   Features `PATCH` and `DELETE` it gets `204` for its own and `403` for the other two, and the
   refused feature reads back unchanged. The administrator is refused nothing on either face. The
   writer's half is falsified (§4).
2. **A tracked column cannot be written by a client.** A value sent for one is replaced by this
   server's, asserted on add and on update. **DISCHARGED 2026-09-11** — the writer tests (§4), and
   end to end: a `created_user` sent by the client on an ArcGIS add, an ArcGIS update and an OGC
   create reads back as the account that made the edit.
3. **A role is refused on a column that cannot hold it**, and a tracked column cannot be hidden —
   refused when the overrides are written, not discovered at edit time. **DISCHARGED 2026-09-11** —
   `PUT /admin/layers/{name}/fields` answers `400`, naming the reason, for a date role on a text
   column, a name role on a date column, an unknown role, a tracked column set hidden and two
   columns given one role; the roles already in place are unchanged after all five. Measured end
   to end in the same test.

## 10. Revisit triggers

- A client that relies on writing a tracked column — an offline sync replaying edits with their
  original authors, which Portal supports through `editorTrackingInfo` on the service.
- A deployment that renames accounts and expects ownership to follow.

## 11. Dissent

None recorded.
