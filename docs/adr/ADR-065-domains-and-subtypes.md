# ADR-065 — Domains and subtypes

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` |
| **Decided** | 2026-09-12, building the owner's decision of 2026-09-09 ([Q-58c](../open-questions.md), [ADR-013](ADR-013-feature-service-data-model.md) §5a) and the owner's request of 2026-09-12, *"domain ve subtype ları istiyorum"* |
| **Supersedes** | — |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

ADR-013 §5a put domains, subtypes and editor tracking in v1. Editor tracking was built on
2026-09-11 ([ADR-064](ADR-064-editor-tracking-and-what-features-edit-means.md)); the other two
were the one row of [v1-scope](../v1-scope.md) §2 still marked **NOT BUILT**. Measured
2026-09-12 before this work: `subtypeField` and `CodedValue` appeared nowhere in `src`, every
field of every layer document carried `domain: null`, and `Graticula.Import.Reader` read each
geodatabase field's domain name and dropped it under a comment saying the schema had nowhere to
put it.

Three constraints were already written down and are inputs here, not choices:

- **Where the values live** — Q-58c: with [ADR-063](ADR-063-a-field-list-may-differ-from-the-table.md)'s
  field overrides rather than beside them, because a label, a hidden flag, a role and a domain are
  all claims about one column.
- **What a reported domain means** — ADR-013 condition 5: *a domain or a subtype the server reports
  is a domain or a subtype the server enforces on write*, and the test is that a write outside it is
  refused with a refusal naming the domain.
- **What a client reads** — the ArcGIS REST API's public reference: a field's `domain` is a
  `codedValue` object (`name`, `codedValues` of `{name, code}`) or a `range` object (`name`,
  `range: [min, max]`); a layer names its subtype column as `typeIdField` and `subtypeField`, gives
  `defaultSubtypeCode`, and lists its subtypes twice — `types` (`id`, `name`, `domains`, `templates`)
  and `subtypes` (`code`, `name`, `defaultValues`, `domains`) — with `{"type": "inherited"}` for a
  column a subtype does not change. Templates carry a `prototype` whose `attributes` a client
  copies into a new feature.

## 2. Alternatives considered

### Alternative A — a domain per column, in the layer's field overrides *(chosen)*

**Argument for.** It is Q-58c's machinery, so one object carries every claim about a column and
every face already reads it: `FieldOverrides.Apply` attaches the domain to the field description
that the ArcGIS document, the writer and the admin surface share, which is how a domain reported
and a domain enforced cannot come apart. It inherits ADR-063's drift answer for free — a domain on
a column that is dropped or retyped governs nothing rather than refusing everything. And it is how
ArcGIS Portal's hosted feature layers already work: a hosted layer's domains are properties of its
fields in the layer definition, set through `updateDefinition`, not a register the layer points
into — so an operator coming from Portal meets the shape they know.

**Argument against.** A geodatabase's domains are workspace objects shared by many feature classes.
Importing an archive whose fifty classes use one `Material` domain writes it fifty times, and
changing it later means changing fifty layers.

### Alternative B — a server-wide register of named domains that fields point at

**Argument for.** It is the enterprise geodatabase's model, so a migration keeps the sharing the
source had, and one edit reaches every layer using a domain.

**Argument against.** It is a new catalogue object with a lifecycle — who may edit a domain other
people's layers use, what deleting one in use means, whether names collide across folders and owners
— and nothing in v1's scope needs any of it. It is also the second mechanism Q-58c forbids: the
Fields page would show a column's label from one place and its values from another.

### Alternative C — derive domains from the database

**Argument for.** A registered PostGIS table may already say what it allows — a `CHECK (x IN (…))`,
an enum type, a foreign key to a lookup table — and reading that is truer than any setting.

**Argument against.** None of those carries the *name* a client shows beside a code except a lookup
table, and which lookup table is the domain is a guess. It would also make a hosted layer's values
DDL, which ADR-058 lets an operator edit from a screen and this would turn into a second screen for
the same fact. It stays attractive as an *import suggestion* for registered data, and is recorded as
a revisit trigger rather than dismissed.

## 3. Counterarguments to the preferred option

**Refusing a value outside a domain is stricter than the product clients come from.** A geodatabase
does not enforce a domain on a write — validation is a separate step — and a client that has always
been able to post an out-of-list value through a feature service will be refused here. That is
ADR-013 condition 5 doing what it says, and the reason it says it is this repository's three earlier
instances of a setting stored and not honoured (D-67). It is still a behaviour difference an
operator migrating data with known out-of-list values will meet, and the refusal is written to tell
them which value and which domain.

**What is checked is what is written, not the row.** An update that moves a feature to a subtype
whose list excludes a value already in the row, without sending that column, is accepted. Checking
the row would refuse an edit for something the client never sent. The consequence is that a table
can hold values no current domain allows — which it already could, since A-027 says a write that
bypasses this server is not seen by it.

**A subtype can replace a column's domain and cannot remove one.** The wire has `inherited` and a
domain object, and no agreed spelling for *none for this subtype*. A geodatabase that removes a
field's domain in one subtype imports with the column's domain applying to that subtype too, which
is stricter than the source.

**No template is generated for a layer without subtypes.** A client may build templates of its own
from the renderer; a single template invented here would be a choice nobody made, presented as
though somebody had. The cost is that such a layer's document has `templates: []`.

**Subtype default values are carried to clients and never written by this server.** A client that
creates a feature from a template sends them; one that posts a bare feature gets exactly what it
sent. Filling them in server-side would be a write the client did not ask for.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| The wire shapes | ArcGIS REST API, *Domain objects* and *Layer (Feature Service)*; web map specification, *type* and *template* objects | public reference, read 2026-09-12 |
| A coded-value list cannot be read through the GDAL binding | MaxRev.Gdal.Core 3.13.1: `FieldDomain.GetMinAsDouble` is bound, `GetEnumeration` is not; no `CreateCodedFieldDomain` either | the binding assembly's exported symbols, 2026-09-12 |
| A geodatabase's domains and subtypes are readable as XML | The OpenFileGDB driver lists `GDB_Items` under `LIST_ALL_TABLES=YES`; its `Definition` column holds `GPCodedValueDomain2` and `GPRangeDomain2` documents, and for a feature class the `SubtypeFieldName`, `DefaultSubtypeCode` and `Subtype`/`SubtypeFieldInfo` elements the published *XML schema of the geodatabase* names | GDAL driver documentation; Esri's published XML schema description |
| An Esri-written archive's coded values and range reach a served layer | GDAL's own `Domains.gdb` test archive (not committed, Q-138) imported through `/admin/hosted/import` and published: the served `Roads` layer's `maxspeed` is `range` 40–100 `SpeedLimit`, `mediantype` is `codedValue` `MedianType` 0 (None) and 1 (Cement), `surfacetype` is `RoadSurfaceType` 1–4; the publish job reports `domains: [maxspeed, mediantype, surfacetype]` and nothing not carried | fixture 18465 on the VPS, by hand, 2026-09-12 |
| A domain in an imported archive reaches the served field | `GeodatabaseReadsCorrectlyTests`: the fixture's `count` field carries a range domain written by GDAL into `GDB_Items`, and the served `count` field's `domain` is `range` 0–100 named `Visits` | fixture 18465 on the VPS, 2026-09-12 |
| The writer refuses a value outside a column's domain and a subtype's, and writes nothing | `DomainWriterTests` 6/6 against PostGIS. **Falsified**: with the stored-subtype read disabled, `An_update_that_does_not_say_its_subtype_is_checked_against_the_one_it_is` fails and the other five pass | VPS datastore, 2026-09-12 |
| Both writing faces, the document and the admin surface agree | `DomainConformanceTests` against a running server: the document carries the domains, `typeIdField`, `subtypeField`, `types` with per-type domains and templates; ArcGIS `addFeatures` and `updateFeatures` refuse out-of-list, out-of-range, unknown-subtype and subtype-narrowed values naming the domain; OGC API Features answers `400` naming the domain and `204` inside it; eight bad settings are `400` and leave what was stored; the subtype column cannot be dropped. The whole conformance suite ran around it, 502 of 503: the one failure is the fixture's, since `DataSourceLifecycleConformanceTests` rewrites `Database=gis` to `postgres` and this fixture's database is `gis_domains` | fixture 18465 on the VPS, 2026-09-12 |

## 5. Decision

**A column may be given a domain through the layer's field overrides: a list of codes with names,
or a range.** Text columns take a list; short, long, single and double columns take either; date
columns take a range; 64-bit integer, boolean, GUID and binary columns take none. The object id,
the identity column and a column with an edit role take none. **One integer column may be the
layer's subtype column**, carried on that column's override: a list of subtypes, each with a code,
a name, starting values per column and a domain per column that replaces the column's own for
features of that kind, and a default subtype. **Both are enforced by the writer both writing faces
share**: a value outside the domain that governs it — the feature's subtype's when it has one, the
column's otherwise, and the subtype a feature already is when an update does not say — is refused
with a sentence naming the column, the domain and what it allows; a null is the column's
nullability to decide. The layer document carries every field's `domain` and, for a layer with
subtypes, `typeIdField`, `subtypeField`, `defaultSubtypeCode`, `types` with templates, and
`subtypes`. A domain or subtype that no longer fits the table — a retyped or dropped column — is
inert. The subtype column cannot be dropped while it holds subtypes. A geodatabase import carries
its archive's domains and subtypes, judged by the same rules, and names what it did not carry.

Four parts of this are **`INFERRED`** rather than stated by the owner, and are listed for
confirmation: per-layer rather than shared domains (Alternative A over B); refusing rather than
accepting out-of-domain values is ADR-013 condition 5's, which the owner accepted with §5a but did
not word; no generated template for a layer without subtypes; and a ceiling of 10,000 codes to a
domain and subtypes to a layer.

## 6. Consequences

**Positive.** The last NOT BUILT row of v1-scope §2 is built. An ArcGIS editing client shows a
drop-down, a range check and per-subtype templates for a layer served here, and a hand-made request
cannot put a value in the table that the drop-down would not have offered. A migrated geodatabase
keeps its lists instead of arriving as bare codes.

**Negative.** Shared domains are copied per layer. An import from an archive whose domains do not
fit the columns the rows produced — a range on a date the rows turned into text — loses that domain
and says so in the job's report rather than failing the layer. Every write to a layer with a domain
pays a lookup per governed value, and an update that writes a subtype-governed column without its
subtype pays one query per batch to read the subtypes it is changing. **Only the ArcGIS face
describes them.** OGC API Features enforces a domain through the same writer and does not advertise
one — this server has no queryables or schema document for a collection, which is where an `enum`
or a `minimum` and `maximum` would go — and WFS is read-only (ADR-039). A client of the OGC face learns a list from the refusal rather than before
it sends.

**Ports created.** None. The writer takes the subtypes beside the editor-tracking roles.

**State.** *Catalogue*: `domain` and `subtypes` keys on entries of `layer.field_overrides`, no
migration. An older build ignores both and serves the layer without them — which is the state before
this, and the safe direction only for reading: an older build does not enforce what it does not
read.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| A-027 | Writes that bypass this server are not seen by it | **Holds, and bounds this.** A row written by psql may hold any value; a domain here governs what this server writes |

## 8. Dependencies

**Depends on** ADR-013 (§5a, condition 5), ADR-063 (the overrides), ADR-064 (roles, which rule a
column out), ADR-058 (the drop guard), ADR-038 (the geodatabase import).

**Depended on by** nothing yet.

## 9. Conditions

1. **ADR-013 condition 5, on both writing faces.** A write outside a reported domain is refused and
   the refusal names the domain, through ArcGIS `applyEdits` and OGC API Features. **DISCHARGED
   2026-09-12** — `DomainConformanceTests` (§4), with the writer's half falsified.
2. **An ArcGIS client reads what the document says.** A client that opens a layer served here shows
   the list, respects the range and offers one template per subtype. The shapes are the public
   reference's and are asserted key by key; what Esri's JavaScript API and ArcGIS Pro actually do
   with them is not measured, and the first person to find out should not be the owner — ADR-057
   condition 5's argument. One attempt on 2026-09-12 loaded the JavaScript API 4.29 into a
   headless Chrome on a page of the fixture and the layer's `load()` did not return in 45 seconds;
   the renderer had crashed on the map page before that. Neither says anything about this server,
   and neither is counted.
3. **An Esri-written archive's subtypes are carried.** The coded values and ranges of an Esri-written
   archive are measured (§4); its subtypes are not, because no archive with subtypes was available to
   read, and the parser follows the published schema's element names rather than a document seen.
   Discharged by importing one and finding its subtypes on the served layer.
4. **The Fields page sets them.** An operator can give a column a list or a range and a layer its
   subtypes, starting values and per-subtype values from the screen, through the design review every
   screen here goes through, with the first-run state tested. **DISCHARGED 2026-09-12.** The design
   review, run in a browser against the fixture, found four defects and all four are repaired and
   re-measured: every Values summary was cut at 1280 and the date range at 1440 (the column now
   wraps, and a range of new years' days reads as the years — nothing truncated at 1024, 1280 or
   1440); the editor opened under the whole table, three unrelated rows from the row pressed (it now
   opens under that row, and under the column's row inside a subtype); the name box inherited the
   right-aligned monospace box of a numeric setting and cut "Pipe material"; and a domain named after
   its column produced *"'diameter' cannot take the domain 'diameter'"*. It also judged that people
   would press Done and believe they had saved, so the sentence saying nothing is stored until Save
   moved above the buttons. Keyboard focus, `aria-expanded` and the live region were measured and
   found sound, and the first-run state shows every control. `FieldsValuesPageTests` holds the
   first-run state, the editor under its row, focus returning to the button, a typed label surviving
   the redraw, and the body Save sends. The console suite ran around it against the same fixture, 172
   of 175: one failure was timing over the tunnel and passed alone, and the other two — the service
   Sharing page and the Publish tree's reprojection mark — fail identically with this change's
   console files replaced by the previous commit's and its review layers removed, so they are not
   this page. CI, which runs that suite in the environment it was written for, passed all 175 on
   the same commit (run 34720565843), so the two were the bare host and the tunnel.

## 10. Revisit triggers

- An operator maintaining the same domain on many layers by hand — Alternative B's case.
- A registered table whose `CHECK` constraints or lookup tables already describe its values —
  Alternative C as an import suggestion.
- A migration refused over out-of-list values the source held, which would argue for an
  accept-and-report mode per layer.

## 11. Dissent

None recorded.
