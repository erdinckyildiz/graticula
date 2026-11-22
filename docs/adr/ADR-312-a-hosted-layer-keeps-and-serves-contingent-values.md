# ADR-312 — A hosted layer keeps and serves its contingent values, Studio writes them, and nothing refuses an edit for them

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` — the resource's keys are the public reference's, but how a value is encoded in it is read off examples rather than stated, and no ArcGIS client has read a definition from this server yet (§11, conditions 5–7) |
| **Decided** | 2026-11-22, by owner decision for ArcGIS Enterprise 12.2 band 2, group 1: *"Sakla, sun, Studio'da yaz"* — the layer definition stores contingent values and field groups and serves them to clients, Studio authors them, they come in when Pro publishes, `applyEdits` does not reject a combination that is not valid, and Studio's own editor warns. Every point the public pages do not settle is marked `INFERRED` in §5 and listed in §5.9 |
| **Supersedes** | — (amends [ADR-235](ADR-235-grapy-holds-attribute-rules-contingent-values-and-replicas.md) §6: the server is no longer without them) |
| **Superseded by** | — |

---

## 1. Context

A **field group** is a set of a layer's fields whose values depend on each other; a **contingent value** is one
combination of values those fields may hold together, for one subtype. ArcGIS Pro authors them; Field Maps, the Maps SDK
for JavaScript's Editor and Pro offer only the allowed combinations while somebody edits
([Pro: contingent values](https://doc.esri.com/en/arcgis-pro/latest/help/data/geodatabases/overview/contingent-values.html)).

The 12.2 comparison ([enterprise-12-2-gap.md](../research/enterprise-12-2-gap.md)) found them *missing on the server* and
present only in grapy ([ADR-235](ADR-235-grapy-holds-attribute-rules-contingent-values-and-replicas.md)), which keeps
them in the GeoPackage a tool works on. A layer published here therefore had no way to say *these two fields go together*,
and a field crew's form offered every combination of two drop-downs.

The public REST reference serves them in three places:

- the layer document's **`hasContingentValuesDefinition`** — true when the layer has them, otherwise false or absent;
- a layer resource, **`…/FeatureServer/{id}/contingentValues`**, new at 11.2, which returns the layer's whole definition
  with `idSubset` and `fieldGroupSubset` new at 12.2
  ([Contingent Values](https://developers.arcgis.com/rest/services-reference/enterprise/fs-contingent-values/));
- a service operation, **`…/FeatureServer/queryContingentValues`**, whose answer is the same shape for several layers
  ([Query Contingent Values](https://developers.arcgis.com/rest/services-reference/enterprise/query-contingent-values-feature-service/)).

And one door that writes them: a hosted layer's **`addToDefinition`** takes a `contingentValuesDefinition` with field
groups and contingent values and answers `addedContingentValueIds`, new at 12.2
([Add To Definition (Feature Layer)](https://developers.arcgis.com/rest/services-reference/enterprise/add-to-definition-feature-layer/)).

## 2. Alternatives considered

### Alternative A — keep them on the layer, serve them, enforce nothing on the server (chosen)

**Argument for.** It is what ArcGIS does. Pro's help says the restrictions *"are enforced in the application whenever a
value is placed in a field while editing"*, and the reference documents no server refusal. A server that refused would
reject edits Field Maps and Pro send without complaint against ArcGIS, and would make a non-restrictive group, which is
documented as *allowing* other values, mean something it does not.

**Argument against.** Data that does not satisfy the definition can be written by any client that ignores it, including
a script. The definition is advice that most clients follow.

### Alternative B — enforce them in `applyEdits`, as grapy's cursors do

**Argument for.** grapy already does (ADR-235 §5.6), and a rule that is checked is a rule the data keeps.

**Argument against.** It is not ArcGIS's behaviour and the owner refused it: *`applyEdits` does not reject*. grapy enforces
because arcpy's cursors do; a feature service does not. An existing dataset that was loaded before its groups were written
would also become uneditable row by row.

### Alternative C — keep them inside the field overrides, as subtypes are

**Argument for.** ADR-065 put a layer's subtypes on its subtype column's override, so no migration was needed, and a
contingent value is also a claim about columns.

**Argument against.** A subtype is a claim about one column; a field group spans several, and its contingent values carry
ids, a subtype each and a retired flag. Splitting one definition across several columns' overrides makes *which group owns
this value* a join, and makes renaming a group a write to every column in it. A column of its own costs one expand
migration.

### Alternative D — share grapy's storage

**Argument for.** One store, one implementation.

**Argument against.** grapy's store is the GeoPackage on the machine that runs the script (ADR-235 §5.1); the server's is
its catalogue. Neither can read the other's. What they can share is the **model**, and §5.1 makes them the same one.

## 3. Counterarguments to the preferred option

- **The encoding of a value is inferred.** The reference shows `types` and `values` arrays and five type codes, but does not
  say what a string code's value is, where a range's bounds go or whether `Any` takes a slot. A client that reads them
  differently will offer the wrong combinations, and nothing on this server will notice. §5.9 lists each reading and
  condition 5 waits for a real client.
- **An unenforced rule is easy to mistake for an enforced one.** Studio's Fields page says *enforced* about Values
  (ADR-065), and a reader may assume the same of contingent values. The page says the opposite in words (§5.6).
- **Pro's publish does not carry them yet** (D-473). An owner who authors them in Pro and publishes will find them absent
  and has to write them again in Studio.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| The layer flag is `hasContingentValuesDefinition`, true or false/absent | The service operation's page | [query-contingent-values-feature-service](https://developers.arcgis.com/rest/services-reference/enterprise/query-contingent-values-feature-service/) |
| The resource's top-level keys are `typeCodes`, `stringDicts`, `contingentValuesDefinition`; the operation's are the same with `contingentValuesDefinitions` | The two pages' response examples | [fs-contingent-values](https://developers.arcgis.com/rest/services-reference/enterprise/fs-contingent-values/), [query-contingent-values-feature-service](https://developers.arcgis.com/rest/services-reference/enterprise/query-contingent-values-feature-service/) |
| `contingentValuesDefinition` carries `layerID`, `layerName`, `geometryType`, `hasSubType`, `fieldGroups`; a group `name`, `restrictive`, `fields` (`name`, `id`, `fieldType`), `subTypes` (`code`, `name`, `domains`, `contingentValues`); a value `id`, `types`, `values` | The same examples | as above |
| `typeCodes` is `["Unknown","Any","Null","Code","Range"]`, and `types` holds indexes into it | The same examples | as above |
| `stringDicts` entries are `{"domain", "entries"}` | The same examples | as above |
| `idSubset`, `fieldGroupSubset` are 12.2 parameters of the resource; `f` takes `json`, `pjson`, `pbf` | The resource's page | [fs-contingent-values](https://developers.arcgis.com/rest/services-reference/enterprise/fs-contingent-values/) |
| The operation takes `layers`, `compactFormat`, `domainDictionaries`, `f` | The operation's page | [query-contingent-values-feature-service](https://developers.arcgis.com/rest/services-reference/enterprise/query-contingent-values-feature-service/) |
| `addToDefinition` takes `contingentValuesDefinition` with `fieldGroups` and `stringDicts`; added values carry no `id`; the answer carries `addedContingentValueIds` beside `success` | Examples five and six and the response syntax | [add-to-definition-feature-layer](https://developers.arcgis.com/rest/services-reference/enterprise/add-to-definition-feature-layer/) |
| Restrictive is the default; a retired value is not selectable; `<ANY>` accepts any value, `<NULL>` a null; contingent values apply to their subtype; enforcement is in the application while editing | Pro's help topic | [contingent-values (Pro)](https://doc.esri.com/en/arcgis-pro/latest/help/data/geodatabases/overview/contingent-values.html) |
| The reference pages' prose does not define a string code's value, a range's encoding, `Null`'s slot, retirement in REST, or a layer without subtypes | Read 2026-11-22; recorded as not read rather than guessed | the three pages above |
| Esri's JavaScript blog on contingent values could not be read (403) | Fetched 2026-11-22 | esri.com/arcgis-blog …/contingent-attribute-values-in-the-arcgis-api-for-javascript |

## 5. Decision

**A hosted layer keeps one contingent-values definition in its catalogue row, serves it where the reference serves it,
takes it from Studio and from ArcGIS's `addToDefinition`, and never refuses an edit because of it.** Studio's own editing
warns, and saves.

### 5.1 The model is grapy's

A definition is a list of **field groups** — a name, whether it is restrictive, and its fields in order — and a list of
**contingent values** — an id, the group it belongs to, a subtype code or none, whether it is retired, and one entry per
field of the group: `ANY`, `NULL`, `CODED_VALUE` with a code, or `RANGE` with a minimum and a maximum. These are grapy's
words and grapy's shape (`grapy_field_group`, and `grapy_contingent_value.entries` as `[field, type, value]`, ADR-235
§5.1), so a definition read out of one can be written into the other without translation. **The two stores stay apart**
(Alternative D): grapy's is a GeoPackage, the server's is `layer.contingent_values` (**INFERRED**: no public document says
where a server keeps them).

### 5.2 Storage

`layer.contingent_values jsonb`, null for none — migration 146, an **expand**: a node older than it never reads the
column, and serves the layer without the flag, which is what it served before. The catalogue's manifest carries every
layer column already (`to_jsonb`), so backup and restore carry this one. A **view** of a hosted layer (ADR-113) takes its
source's definition when it is created, as it takes its field overrides.

### 5.3 What a write must satisfy

Checked when Studio or `addToDefinition` writes, against the layer's columns as its table has them; a refusal names what
is wrong and nothing is stored:

- a group has a name, unique within the layer without regard to case, and at least one field — **INFERRED** at least two
  is not required, because Pro's tool page does not say so;
- each field is a column of the layer, not hidden, not the object id, the global id, the geometry or the subtype column
  (as ADR-235 §5.7's *no system or subtype fields*), and in at most one place in the group;
- each contingent value names a group that exists, gives an entry for every field of that group and no other, and a
  subtype code the layer has (or none on a layer without subtypes);
- `CODED_VALUE` is a code of the field's coded-value domain for that subtype when the field has one; `RANGE` is on a
  number or date field, with a minimum not above its maximum, and inside the field's range domain when it has one; `NULL`
  is on a field that allows nulls;
- ids are unique; a new value with no id takes one more than the highest held (as ADR-235 §5.8 h).

A field is **not required to have a domain** (**INFERRED**: Pro says fields *should* have domains, not must).

**Drift (ADR-063's answer).** A group that names a column the table no longer has, or one that has since been hidden, is
left out of what is served — inert rather than broken — and is shown on Studio's page so it can be removed. Nothing is
rewritten behind the owner's back.

### 5.4 What is served

- The layer document says **`hasContingentValuesDefinition: true`** when at least one group survives §5.3's drift rule,
  and says nothing otherwise (the reference allows absent for false; every other layer's document is written exactly as
  before).
- **`…/FeatureServer/{id}/contingentValues`** answers `typeCodes`, `stringDicts` and `contingentValuesDefinition` with the
  keys of §4, for `f=json` and `pjson` (and `html`, which this server answers as JSON as it does every resource). It takes
  `fieldGroupSubset` (names, comma separated, without regard to case) and `idSubset` (ids and ranges like `1-3`). A layer
  without a definition answers the same shape with no field groups (**INFERRED**: the page does not show one).
- **`…/FeatureServer/queryContingentValues`** answers `typeCodes`, `stringDicts` and `contingentValuesDefinitions`, one per
  layer named in `layers` that has a definition, or every such layer when `layers` is empty. `layers` takes ids, ranges and
  `-n` exclusions as the hosted page says.
- **Encoding (each INFERRED from the examples, §5.9):** a field's `id` is its position in the layer document's `fields`;
  `domains` maps each group field that has a domain for that subtype to the domain's name; a `CODED_VALUE` on a number is
  the number; a `CODED_VALUE` on text is the **index of the code in that domain's `stringDicts` entries**, whose `entries`
  are the domain's codes in its order — and a text field with no coded domain gets a dictionary named for the field;
  `RANGE` takes **two slots**, minimum then maximum; `ANY` and `NULL` take **no slot**. A layer without subtypes says
  `hasSubType: false` and puts its values under **one `subTypes` entry with code 0 and an empty name**. A **retired value
  is not served** — REST has no key for retirement and a retired value is one a client must not offer.
- `f=pbf`, `compactFormat=false` and `domainDictionaries` other than `none` are **refused by name** with a 400 (D-474),
  rather than answered as JSON that a protobuf or a decoded reader would misread.

### 5.5 What writes it

- **Studio** (§5.6), through `GET` and `PUT /admin/layers/{name}/contingent-values` — the whole definition in, the stored
  one out; the privilege and ownership checks of `/admin/layers/{name}/fields`. **Hosted layers only** (**INFERRED**, from
  the reference's *layer-level editing through the admin API is only supported for hosted feature services*); a registered
  layer is refused by name.
- **ArcGIS's `addToDefinition`** on a hosted layer, with a `contingentValuesDefinition`: field groups (`name`,
  `restrictive` defaulting to true, fields as `{"name"}` objects — **INFERRED**: example five's fields are read from the
  group's `fields` or from a `fields` inside its `contingentValues`, since the summary of the page puts them there) and
  contingent values under `subTypes` in the resource's own encoding, decoded with the request's `stringDicts`. Groups
  that already exist are refused by name rather than replaced; values are appended with new ids, which the answer
  returns as `addedContingentValueIds`. A document with both `fields` and `contingentValuesDefinition` is refused, and the
  caller sends them one at a time (the existing door applies fields; adding columns and naming them in a group in one
  call would order two writes this server makes in two transactions).
- **`deleteFromDefinition` and `updateDefinition` of contingent values** are not served (D-475): the reference pages for
  them do not mention contingent values, and Studio replaces the whole definition.
- **Pro's publish** does not carry them (D-473, §5.8).

### 5.6 Studio

**Authoring** is a section of the hosted layer's **Data › Fields** view, under the Fields table, in its own script
(`contingent-studio.js`): the field groups (name, restrictive, fields from the layer's columns), and for the open group a
table of its contingent values with a control per field — *Any*, *Null*, a code from the field's domain or a typed value,
or a range — a subtype where the layer has subtypes, *retired*, and delete. One **Save** writes the whole definition and
the server's refusal is said in a polite live region. A re-render puts focus back on the control that caused it.

The page says, in words: *contingent values guide editors in Field Maps, Pro and Studio; the server does not refuse an
edit that breaks them.*

**Editing warns.** The Map Viewer's attribute form and its attribute table read the layer's `contingentValues` resource
when its document says `hasContingentValuesDefinition`, and check every restrictive group of the feature being saved. A
combination no value allows is **said in an `aria-live` region as it is typed and again when saved, and the save goes
through**. The check follows grapy's rule (ADR-235 §5.8 h): a restrictive group with no values for the feature's subtype
does not restrict, a feature whose group fields are all null is not restricted, and a retired value — which the resource
does not carry — is therefore warned about. The check is one pure function (`contingent.js`) that the editor and Node's
test runner both load, and the Map Viewer's glue is its own file (`contingent-editor.js`).

### 5.7 `applyEdits`

**Unchanged.** It does not read the definition. A combination no value allows is written, as ArcGIS writes it.

### 5.8 What does not carry them yet

- **The offline replica** (ADR-288). The mobile geodatabase's `GDB_Items` has a `ContingentValues` column, a blob, and
  [mobile-geodatabase-format.md](../research/mobile-geodatabase-format.md) records nothing of its encoding. Writing bytes
  Field Maps would read as a definition needs that encoding, and none is public. **Not written** (D-472).
- **Pro's publish** (ADR-191). The file geodatabase inside a service definition keeps contingent values in a form neither
  Esri's published XML workspace schema (which predates them) nor GDAL's OpenFileGDB driver reads. **Not read** (D-473).

### 5.9 INFERRED, for confirmation

1. Storage in a layer column of the server's own, in grapy's model (§5.1, §5.2).
2. A text code's value is its index in the domain's `stringDicts` entries, and the entries are the domain's codes (§5.4).
3. A range takes two slots, minimum then maximum (§5.4).
4. `ANY` and `NULL` take no slot (§5.4; `ANY` is shown by example five's `[3,1]` with one value, `NULL` by analogy).
5. A field's `id` is its position in the layer's `fields` (§5.4).
6. A layer without subtypes serves one `subTypes` entry with code 0 and an empty name (§5.4).
7. Retired values are kept and not served (§5.4).
8. A layer without a definition answers the resource with no field groups (§5.4).
9. Writes on hosted layers only (§5.5).
10. Example five's group fields are read from the group or from its `contingentValues` (§5.5).
11. A field need not have a domain, and a group may have one field (§5.3).
12. Studio's warning follows grapy's unrestricted cases (§5.6).

## 6. Consequences

**Positive.** A layer published here can tell Field Maps, Pro and the Maps SDK which combinations go together, through the
addresses those clients read; an owner writes them in Studio or with the ArcGIS API for Python's `add_to_definition`; and
the model is the one grapy already speaks.

**Negative.** Seven encoding readings rest on examples (§5.9). The offline replica and Pro's publish do not carry them
(D-472, D-473). A protobuf reader is refused (D-474). A definition can only grow through ArcGIS's door and be replaced
whole through Studio's (D-475). Data that breaks the definition is written by any client that ignores it — by decision.

**State.** One nullable `jsonb` column on `layer` (migration 146), shared by every node because it is the catalogue; it
travels in backups with the rest of the row. Nothing is held at runtime beyond the catalogue reads every layer already
makes. The Map Viewer keeps a layer's fetched definition for the life of the page.

**Ports created.** None; no dependency is adopted.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | ArcGIS clients read contingent values from `contingentValues` or `queryContingentValues` and enforce them in the form, as Pro's help says Pro does | Unverified against Field Maps or the JS SDK on this server (condition 5) |

## 8. Dependencies

**Depends on:** [ADR-063](ADR-063-a-field-list-may-differ-from-the-table.md) (drift), [ADR-065](ADR-065-domains-and-subtypes.md)
(domains and subtypes), [ADR-113](ADR-113-a-hosted-layer-has-views.md) (views), [ADR-235](ADR-235-grapy-holds-attribute-rules-contingent-values-and-replicas.md)
(the model), [ADR-288](ADR-288-offline-sync-is-in.md) (the replica that does not carry them yet),
[ADR-191](ADR-191-arcgis-pro-publishes-hosted-feature-layers.md) (the publish that does not carry them yet).

**Depended on by:** none yet. The forms work of the same band (ADR-313) writes `formInfo`, which is separate.

## 9. Revisit triggers

- Field Maps, the JS SDK or Pro is pointed at a layer here with a definition and offers combinations the definition does
  not allow, or refuses ones it does: §5.4's encoding is then measured against what the client expects.
- Esri publishes the encoding of a text code, a range, `NULL`, or the mobile geodatabase's `ContingentValues` blob: §5.9
  and D-472 are revised.
- A client asks for `f=pbf` (D-474) or sends `deleteFromDefinition` with contingent values (D-475).
- The owner wants the server to refuse an invalid combination: Alternative B is reopened.

## 10. Dissent

None recorded. Alternative B is the owner's refusal, not a disagreement.

## 11. Conditions

1. **The layer flag and the `contingentValues` resource are served in §5.4's shape**, with `fieldGroupSubset` and
   `idSubset`, and `queryContingentValues` with `layers`, checked by conformance tests against a running server.
   **DISCHARGED 2026-11-22**: `ContingentValuesTests.A_definition_is_kept_served_filtered_and_never_refuses_an_edit` and
   `…A_layer_with_subtypes_serves_its_values_under_each_subtype` (tests/Graticula.Conformance.Tests) against the local
   fixture, and the encoding by `ContingentValuesAreKeptAsGrapyKeepsThemTests` (tests/Graticula.Host.Tests).
2. **`applyEdits` accepts a combination no contingent value allows**, checked by a conformance test that writes one and
   reads it back. **DISCHARGED 2026-11-22**: the same conformance test adds `model = 999` against a restrictive group
   through `applyEdits`, is answered `success`, and queries it back.
3. **Studio's `PUT /admin/layers/{name}/contingent-values` stores what §5.3 allows and refuses the rest by name**, and a
   registered layer is refused, checked by tests. **DISCHARGED 2026-11-22**: the conformance tests store, refuse an
   unknown field, a code outside the domain, `NULL` on a non-null field, the subtype column and a value with no subtype
   on a subtyped layer, each by name; `ContingentValuesAreKeptAsGrapyKeepsThemTests` covers the rest of §5.3 and
   `Only_a_hosted_layer_that_is_not_a_view_is_written` the registered and view refusals.
4. **`addToDefinition` with a `contingentValuesDefinition` adds groups and values and answers
   `addedContingentValueIds`**, checked by a test against a running server. **DISCHARGED 2026-11-22**: the conformance
   test adds a group in example five's shape and a value in example six's (a text code through `stringDicts`), reads
   `addedContingentValueIds` `[5]`, finds both in the resource, and is refused a group that already exists.
5. **A real ArcGIS client reads a definition from this server and offers only the allowed combinations** — Field Maps on a
   device, or the Maps SDK for JavaScript's Editor. Open: needs a device or a browser session with the SDK, which this
   work did not drive.
6. **Studio's attribute form and table warn in an `aria-live` region and still save**, with the check tested headless in
   Node and the screens reviewed. **PARTLY DISCHARGED 2026-11-22**: `node --test tests/js/contingent.test.js` (8 pass)
   checks the decoding and the check; the attribute form was driven in headless Chrome on the fixture — typing
   `model = 999` said the combination in `#editCv` (`aria-live="polite"`), Save went through, and the status line said
   *Saved anyway*. Open: the attribute table's path was not driven in a browser, and neither screen has had its design
   review.
7. **The offline replica carries the definition** (D-472). Open: the blob's encoding is not public.
8. **Pro's publish carries the definition** (D-473). Open: the file geodatabase's storage is not public.
