# ADR-058 — The datastore's schema is edited from the screen

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `HIGH` for the rule and the refusals · `MEDIUM` for the dependency list |
| **Decided** | 2026-09-08, by owner decision |
| **Supersedes** | — |
| **Superseded by** | — |

---

## 1. Context

**Owner instruction, 2026-09-08:** *"datastore üzerindeki fieldlerin değişimi kendi
sorumluluğunda, yani db'ye bağlanıp kimse değiştirmeyecek. Ekrandan yapılacak tüm
değişiklikler."*

This settles a question that has been open since 2026-08-12.
[Q-35](../open-questions.md) asks **which runtime schema changes we offer**, and
[data-model.md §5](../data-model.md) already draws the line the instruction
confirms: a *registered* table's schema is changed in the source database by
whoever administers it, and a *hosted* one is changed through our administrative
API, because we own it and can sequence the change.

**What has been missing is not the rule but any way to obey it.**
`POST /admin/hosted/define` takes a schema when a feature class is *created* and
there is no route, and no screen, that changes one afterwards. So a datastore
layer's shape is frozen at creation and the only repair is to drop it and import
again — which loses the item, its sharing, its symbology and the service built
over it. The instruction says every change goes through the screen; today the
screen does not exist and neither does the endpoint under it.

**The registered half is not affected and is not what this is about.** A DBA
altering a registered table underneath us stays exactly as
[data-model.md §3](../data-model.md) describes and as
[Q-37](../open-questions.md) measured: the shape is re-read within 30 seconds,
`POST /admin/layers/{name}/refresh` shortens that to now, and a request already
answering finishes on the old shape. This decision narrows *the datastore*, where
nobody else is supposed to be holding the other end.

## 2. Alternatives considered

### Alternative A — Leave it. A hosted layer's schema is fixed at import

**Argument for.** It is where we are, it has cost nothing so far, and the
datastore was described from the beginning as a place data is *put*, not a place
it is designed. Re-importing is a real route: the file is usually still there.

**Argument against.** The owner has just said the screen is the only path, so
*no path* is not an answer. And re-importing is not the same act: it destroys the
item, its sharing, its symbology and any service composed over the layer
([ADR-057](ADR-057-composing-and-publishing-a-service.md)). A person who wants to
add a `notes` column loses a week of configuration to get it. That is the
difference between an omission and a defect.

### Alternative B — Schema editing through the feature API

**Argument for.** It is where a client already is: a layer's own address, beside
`applyEdits`. One surface, one token, no second place to look.

**Argument against.** It is DDL on a read surface, and this repository has a rule
about that shape — the administrative acts live under `/admin` and the protocol
faces answer. It also makes the feature API's authorization carry a privilege it
was not designed around: `content:publishFeatures` is about publishing, not about
dropping columns from somebody's data. ArcGIS itself puts these on a **separate
admin endpoint** for the same reason (§4).

### Alternative C — Add and delete a field on the admin surface, with a dependency check

**Argument for.** It matches where every other administrative act on a layer
already lives, it takes the privilege that already means *this layer is yours to
change*, and the refusals it needs are ones this server can already compute: it
knows a layer's time field, its symbology and its filter, so it knows which
columns something depends on.

**Argument against.** It is a new surface with a destructive operation on it, and
the dependency list is a thing that must be kept in step: every future feature
that reads a column by name — a label expression, a popup, a join — has to
remember to declare itself, or the delete that breaks it will be allowed. That
cost is real and is §6's first negative.

### Alternative D — A service definition separate from the physical table

**Argument for.** This is the structural idea in
[research/runtime-schema-evolution.md §3](../research/runtime-schema-evolution.md),
and it is a good one: the definition can describe things the table cannot —
computed fields, aliases, hidden columns, per-role visibility ([Q-36](../open-questions.md)) —
it can be validated before any DDL runs, and it gives rollback something to roll
back to.

**Argument against.** We do not have one, and adding a field does not need one.
§5f: this server stores **no field list at all** — the shape is read from
`information_schema` per table and remembered for thirty seconds — so adding a
column is already visible without writing anything down. Building a definition
layer to support an operation that does not need it is §82's question with no
answer. It is the right shape for the *next* problem and the wrong one for this
week's.

## 3. Counterarguments to the preferred option

**A delete that a dependency check missed is data loss, and the check is
hand-maintained.** Nothing in the compiler ties *this code reads a column by
name* to *this column may not be deleted*. The list is `time_field` and the
columns the symbology reads, and the next thing that reads a column by name will
not add itself. **The first draft of this ADR listed a third — a layer filter —
and there is no such thing in this server**, so the list was wrong in the
direction of imagining a dependency before it was ever wrong in the direction of
missing one. **This is the
strongest argument against and it is not answered by the design** — it is
answered by a condition (§7.2) and by the delete being explicit rather than
inferred.

**"Nobody will connect to the database" is a rule about people, and the database
does not enforce it.** The datastore's credentials exist and anybody holding them
can run DDL. The instruction makes it *our responsibility*, not *impossible*, so
the 30-second re-read has to keep working for hosted tables too — which it does,
because it is the same code path. Nothing here may be built on the assumption
that our own screen is the only writer.

**Two stores, one act.** A hosted table lives in the datastore and the catalogue
lives in the platform store. In the baseline deployment they are one PostgreSQL,
but nothing requires that, so a schema change cannot be one transaction across
both in general. §5f is what makes this survivable rather than a caveat: there is
almost nothing in the catalogue to keep in step.

## 4. Evidence

**ArcGIS's mechanism, from public documentation.** Read 2026-09-08 for the shape
rather than the implementation; nothing was read from the reference checkout, so
[ADR-030](ADR-030-reading-the-reference-implementation.md)'s reading-log
condition does not apply and its third condition — a public specification is the
citation — is what this row is.

| Claim | Evidence | Source |
|---|---|---|
| Field editing exists only for **hosted** layers | *"supports adding a definition property in a hosted feature service layer"* | [Add To Definition (Feature Layer)](https://developers.arcgis.com/rest/services-reference/online/add-to-definition-feature-layer/) |
| It is on a **separate admin endpoint**, not the feature API | The URL is `<adminservicecatalog-url>/services/<name>/FeatureServer/<id>/addToDefinition` | same |
| Three operations: add, update, delete | `addToDefinition`, `updateDefinition`, `deleteFromDefinition` | [Delete From Definition](https://developers.arcgis.com/rest/services-reference/online/delete-from-definition-feature-layer/) |
| Deleting a field is **irreversible** and is said so | *"once you delete a field, the data in the field cannot be restored"* | [Add or delete a field](https://doc.arcgis.com/en/arcgis-online/manage-data/add-or-delete-fields.htm) |
| Deletion is refused by **dependency**, not only by privilege | System fields; and fields used by *"styles stored in the layer, the time slider, filter, labels, or search"*, or by a view or relationship | same |
| A schema change **stamps the item** | *"Performing any of the operations above, except calculating field values, alters the hosted layer's schema"* — and updates a timestamp on the item page | [Attribute field use and management](https://doc.arcgis.com/en/arcgis-online/manage-data/work-with-fields.htm) |
| A hosted change needs **no restart or republish** | The documentation names neither after adding or deleting a field | [Add or delete a field](https://doc.arcgis.com/en/arcgis-online/manage-data/add-or-delete-fields.htm) |
| A **registered** source is the opposite: restart for a field-definition change, republish to add or delete one | And a map service holds a **schema lock** on its source by default, so the DBA is blocked until it is turned off — which itself requires a restart | [Change schema in map services](https://doc.esri.com/en/arcgis-enterprise/latest/administer/disabling-schema-locking-on-a-map-service.html) |
| DDL under load is not free, and Esri says so | *"users … can experience unexpected behavior, such as missing layers and fields, failing queries, and unavailable services"* | same |

**Our own measurements, which decide more of this than the documentation does.**

| Claim | Number | Source |
|---|---|---|
| A shape change is picked up without a restart | **30 s**, or immediately with `POST /admin/layers/{name}/refresh` | `ServiceContexts.Lifetime` |
| A request already answering finishes on the old shape | 41,978,067 bytes delivered naming a column dropped **76 s** earlier | [Q-37](../open-questions.md), measured 2026-08-26 |
| A request blocked behind `ACCESS EXCLUSIVE` pays for the whole wait | **30.30 s** against 0.296 s unblocked | [benchmarks/statement-timeout](../../benchmarks/statement-timeout/RESULTS.md), [D-08](../architecture-debt.md) |
| A dropped column is reported as a schema fault, not an outage | `42703` → 500 naming the divergence and the 30-second re-read | `ErrorResponse.Explain` |

**The last two together are why §5g refuses a change while the layer is being
read**: the cost of the wrong `ALTER` is not the `ALTER`, it is every request
queued behind it.

## 5. Decision

### 5a. The datastore's schema is ours, and the screen is the only path

A hosted feature class is created, altered and dropped by this server.
**Nobody connects to the datastore to change a column** — owner instruction — so
the screen and the endpoint under it are the whole of the supported route.

**This is a statement about responsibility and not about access.** The
credentials exist. §5h keeps the drift machinery working for hosted tables
exactly as it works for registered ones, so a change made behind our back is
noticed within thirty seconds rather than never. Nothing here is built on our
screen being the only writer, because that is a rule about people.

### 5b. Add a field, delete a field, and nothing else touches the column

Two operations. `POST /admin/hosted/{layer}/fields` adds one;
`DELETE /admin/hosted/{layer}/fields/{name}` removes one.

**Renaming and retyping are not offered**, and that is the same answer ArcGIS
reaches: a rename is add-copy-delete and a retype is a data migration wearing a
small word. Offering either as a one-press act would make this screen the place
where somebody narrows a column and finds out what that did to their data
afterwards. The route is stated in the refusal so it is not a dead end.

**Widening a text field is the one that will be asked for**, and it is refused
too, for now. It is provably safe in PostgreSQL and it is a real need — but
nobody has asked, and §82 says a capability with no stated problem does not go
in. §9's first trigger is somebody asking.

### 5c. A field something depends on is not deleted, and the refusal names what

Delete is refused when the column is:

- the layer's **identity or object id column**, or its **geometry column** —
  these are not fields, they are how the layer is addressed (§5d);
- its **time field** (`layer.time_field`, [Q-129](../open-questions.md));
- named by its **symbology** — every column `SymbologyPlan.Compile` reports the
  style reads, which is the classification field of a `uniqueValue` or
  `classBreaks` renderer and anything else a future style expression names.

~~- named by the layer's **filter** in `layer.definition`.~~ **Struck the same day
it was written, on trying to implement it: there is no filter.** `LayerDefinition`
carries a name, a schema, a table, a geometry column, an SRID and an identity
column, and the `layer` row adds an object id, a geometry type, a cache lifetime,
a symbology and a time field. Nothing anywhere holds a where clause. **This ADR
listed a dependency on a feature that does not exist**, which is precisely the
shape [ADR-034](ADR-034-server-and-studio.md) prohibits, reached from the
direction where the document is the thing that is wrong rather than the screen —
and it is worth leaving visible, because §7.2's whole argument is that this list
is maintained by somebody remembering. It was wrong within the hour.

**The refusal says which of these holds it and where to change that**, because
*this field is in use* sends somebody to look through four screens. This is
ArcGIS's rule reached independently and confirmed by their documentation, which
refuses a field used by *styles, the time slider, filter, labels, or search* —
three of which we do not have, which is why our list is shorter and why §7.2 is a
condition rather than a note.

**What is not checked is what does not exist yet.** We have no labels, no popups
and no saved searches; when one arrives it adds itself to this list, and §7.2 is
the condition that says so out loud rather than trusting it.

### 5d. The system columns are not fields and are not offered

The object id, the identity column and the geometry column do not appear in the
list of fields that can be deleted at all — not greyed, not refused on press.
A control drawn for an act that is always refused is
[ADR-034](ADR-034-server-and-studio.md)'s prohibition from the other direction.

### 5e. Deleting is irreversible, and the screen says so before it is pressed

Not after. The confirmation names the column and says the data in it cannot be
recovered, because that is the one fact that decides the press. This is the
sentence ArcGIS's own help leads with, and it is right.

### 5f. The catalogue is not updated, because it holds no field list

**This is the finding that makes the whole decision small, and it was read out of
the code rather than assumed.** This server stores no field list anywhere.
`ServiceContexts` reads `information_schema.columns` per table and remembers the
answer for thirty seconds; the `layer` row holds the identity column, the object
id column, the geometry column, the SRID, the geometry type, the symbology and
the time field — and nothing else about the shape.

So **adding a column requires no catalogue write at all**, and deleting one
requires a write only when it was the time field, which §5c refuses anyway. The
two-store problem in §3 is real and has almost nothing to be wrong about.

**We are therefore not adopting Alternative D**, and this is where that is
recorded: the *definition is authoritative* model in
[research/runtime-schema-evolution.md §3](../research/runtime-schema-evolution.md)
is the right shape for computed fields, aliases and per-role visibility
([Q-36](../open-questions.md)) and is not needed to add a column. Building it now
would be a layer of indirection whose only job is to describe what
`information_schema` already describes.

### 5g. A change forgets the shape, purges the tiles, and is refused while the table is busy

**After a successful change**, in this order: the remembered shape is forgotten
(`ServiceContexts.Forget`), the layer's tiles are purged, and the service's
`updated_at` moves so anything listing it sees that it changed.

**Forgetting is not optional and thirty seconds is not good enough here.** For a
registered table the TTL is the only bound available, because nothing tells us.
For a hosted one *we* are the one making the change, so serving a stale field
list afterwards would be a bound we chose to keep for no reason.

**The tiles go, and it is the *wrong* class rather than the stale one**
([ADR-010](ADR-010-caching.md) §5.1): a tile built from a column that no longer
exists is not out of date, it is incorrect. Adding a column makes tiles merely
stale, and they are purged anyway — one rule, because a screen that purged
sometimes would be a rule nobody could predict.

**And the `ALTER` is not issued blind.** `lock_timeout` bounds the wait, so a
table somebody is reading refuses the change quickly and says so, rather than
taking `ACCESS EXCLUSIVE` and queueing every request behind it for the statement
timeout — which is [D-08](../architecture-debt.md)'s measured **30.30 s**. The
operator is told to try again, which is a true sentence; a thirty-second stall
across a whole service is not something they could have understood.

### 5h. A registered table is refused here, and keeps the path it has

The endpoint refuses a layer whose source is not the datastore, naming the
reason: that table belongs to the database it was registered from and is changed
there.

**And it refuses a second case the first draft of this section did not see:
hosted is not the same as *we made this table*.** `IsHosted` says the layer's
**source** is the datastore and says nothing about the schema — a datastore
source can serve any schema of that database, and the conformance fixture
publishes one that does. Such a layer passed the check, reached `PostGisImporter`,
and hit *its* guard, which throws: the caller got a **500** for a state this
endpoint should have refused in a sentence.

**Found by a test written to check the other branch.** It borrowed a table the
way every other test in that suite does and got an unhandled exception, which is
the argument for writing the test before believing the guard. Both checks are now
made here, in the operator's words, and the importer keeps its own — that class
must never run DDL in a schema it did not create because a catalogue row pointed
at it, and a guard stated once in two places is one guard and one backstop.
Everything in [data-model.md §3](../data-model.md) continues to apply to it — the
30-second re-read, `POST /admin/layers/{name}/refresh`, and the fact that a
request already answering finishes on the old shape.

**The drift machinery stays on for hosted tables too**, per §5a. It costs nothing
extra: it is the same code, and it is what makes *responsibility* survive
somebody with a `psql` prompt.

## 6. Consequences

**Positive.**

- A hosted layer's shape can change without losing the item, its sharing, its
  symbology or the services composed over it.
- [Q-35](../open-questions.md) is answered for the one engine v1 has, in the
  narrow form the owner asked for rather than the general one.
- The registered/hosted line gets its clearest expression yet: the same server,
  two paths, and each one's reason stated where an operator can read it.

**Negative.**

- **The dependency list is hand-maintained**, and the next feature that reads a
  column by name will not add itself. §7.2 is the condition; nothing in the
  language enforces it.
- **A destructive act gains a screen.** Deleting a column was previously
  impossible through this product; it is now two presses and a confirmation. That
  is what was asked for and it is still a new way to lose data.
- **Two stores, one act**, which §5f shrinks to almost nothing and does not
  remove: a hosted table in a datastore that is not the platform store makes the
  `ALTER` and the `updated_at` two writes that can disagree. The disagreement is
  survivable — a stamp that did not move — and is not zero.
- **Nothing here helps the registered case**, which is the one with the 30-second
  window and the lock fight. `quiesce` is still absent
  ([ADR-007](ADR-007-service-runtime.md) §5b) and this decision does not touch it.

**State.** **None new, and that is §5f's finding rather than a convenience.** This
decision adds no column, no table and no runtime memory: the shape it changes lives
in the datastore's own `information_schema`, and the only thing this server holds
about it is `ServiceContexts`' thirty-second copy, which already exists and which
§5g clears at the moment of the change. Nothing here is node-local against shared,
because there is nothing here.

**Ports created.** None. This is our own datastore and our own admin surface;
no library type crosses a boundary.

## 7. Conditions

1. **The `ALTER` is issued under `lock_timeout` and the refusal is measured**, not
   assumed. §5g's whole argument is that a blocked change should fail fast rather
   than queue every reader behind it, and [D-08](../architecture-debt.md) measured
   what happens without it: 30.30 s against 0.296 s. A test that holds a read open
   and asks for a column, and gets a refusal rather than a stall, is what makes
   this real.
   ***(Discharged 2026-09-08.)*** `AlterUnderLockTests` makes a hosted table through
   the importer, holds a `select` open in a transaction — `ACCESS SHARE`, which is
   what an ordinary unfinished request takes — and asks for a column. The change is
   refused with **55P03** in about **2 s**, and the same request succeeds once the
   reader rolls back.

   **The clock is asserted beside the code, and that is the point of the test rather
   than a detail of it.** The right `SqlState` arriving after thirty seconds would be
   D-08 wearing a better error code; the bound is loose — ten seconds against a
   two-second timeout — because what is being ruled out is *waiting for the statement
   timeout*, not a tight claim about scheduling on a loaded machine.

   **It lives in `Graticula.Platform.Postgres.Tests` rather than in the conformance
   suite**, which talks HTTP and references none of our assemblies on purpose. Holding
   a lock needs a second connection, so the test is at the level that has one, and the
   endpoint's translation of 55P03 into a 409 is a visible two-line mapping above it.
2. **The dependency list is enforced by something that fails when it is
   incomplete.** §5c lists three holders — it listed four until one of them turned
   out not to exist — and §6 says the fourth real one will not add itself. Either a
   test enumerates every place this server reads a column by name and asserts each
   is covered, or the list is a comment that will be wrong within a month. **Until
   that exists, this ADR's confidence on the dependency list stays `MEDIUM` for
   exactly this reason.**
   ***(PARTLY DISCHARGED 2026-09-08, and the half that is not covered is named rather
   than glossed.)*** `EveryColumnNameIsGuardedTests` reads the two types that describe
   a published layer, takes every property whose name ends in `Column` or `Field` —
   which is the whole class of *this holds the name of a column* — and asserts each is
   named in `HoldingOn`. Adding a fifth without touching the guard fails the build.

   **Verified by breaking it**, because a test that cannot fail proves nothing: with
   the time-field branch removed the suite reports *a published layer stores the name
   of a column in TimeField, and HoldingOn does not mention it*. A second assertion
   checks the guard still exists at all, so a rename cannot turn this into a
   comparison against an empty string that passes forever.

   **What it does not cover is a column name that never becomes a property** — inside
   the symbology document, which the guard handles by compiling it rather than by
   reading a field. That half is still judgement, and the confidence stays `MEDIUM`
   because of it rather than because nothing was built.
3. **The screen goes through the ux-designer before it ships**, which is the
   owner's standing instruction and the same condition
   [ADR-038](ADR-038-how-a-geodatabase-becomes-a-service.md) carries.

   **Not yet done for this one.** The Fields view gained an *Add field* row and a Delete per
   droppable column on 2026-09-08, and the review that ran that day was of the Publish screen.
   What it found there is worth reading before this one is drawn any further: the whole Databases
   pane was unreachable by keyboard, and a paragraph that answers asynchronously had no live
   region. Both are shapes this screen has too — a table of rows with buttons in them, and a hint
   that changes after a request.
4. **A hosted layer altered behind our back is still noticed**, tested rather than
   reasoned. §5a rests on the drift path continuing to work for hosted tables, and
   the temptation once *we* own the schema is to stop asking.
   ***(Discharged 2026-09-08.)*** `ServiceContextsTests` gains the same expiry
   assertion the registered case has, on a layer whose definition says hosted. Nothing
   in that class reads `IsHosted` today, which is exactly why the test is worth having:
   the shortcut this guards against would be a branch that never fires for a registered
   layer, so no existing test would notice it appearing.

## 8. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| A-023 | A cheap schema fingerprint can be polled often enough to detect drift without loading the source | `UNVALIDATED` — and this decision does not depend on it. §5g forgets the shape at the moment of the change rather than discovering it later; A-023 is about the registered path |

## 9. Dependencies

**Depends on** — [ADR-002](ADR-002-primary-data-architecture.md) (the datastore is
PostgreSQL and ours), [ADR-034](ADR-034-server-and-studio.md) (a control is not
drawn for an act that is always refused), [ADR-010](ADR-010-caching.md) §5.1 (the
wrong class of invalidation), [ADR-038](ADR-038-how-a-geodatabase-becomes-a-service.md)
(how a hosted feature class comes to exist).

**Depended on by** — [ADR-057](ADR-057-composing-and-publishing-a-service.md) §5l,
which keeps the datastore out of the Publish screen precisely because its tables
are already services; a datastore layer whose shape can change is one more reason
that separation holds.

## 10. Revisit triggers

- **Somebody asks to widen a text column.** §5b refuses it today on §82 grounds —
  no stated problem — and a stated problem is what reopens it.
- **A second thing reads a column by name** (a label expression, a popup, a join).
  It joins §5c's list, and if it does so by somebody remembering rather than by a
  test failing, condition 2 was not met.
- **A second engine arrives.** [Q-35](../open-questions.md) was originally *per
  dialect*, and SQL Server, Oracle and MySQL each lock differently under DDL.
  Everything above is PostgreSQL's behaviour.
- **A hosted datastore that is not the platform store.** §6's two-store cost is
  theoretical while they are one database and becomes real when they are not.

## 11. Dissent

**The dependency check is the whole safety of this and it is a list somebody
maintains.** That objection is recorded rather than answered: §5c enumerates four
holders because four is what exists, and the argument that a fifth will declare
itself is an argument about diligence. Condition 2 asks for a test that fails when
the list is incomplete, and until that test exists the honest description of this
design is *safe by convention*.

**And "nobody will connect to the database" is not enforceable.** The owner's
instruction assigns responsibility; it does not remove the credentials. §5a and
§5h are written to keep the drift path alive underneath, which is the difference
between a rule and an assumption — but a reader who takes the instruction as a
guarantee will build something on it, and that is worth having said here.
