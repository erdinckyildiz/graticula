# ADR-314 — Contingent values are served as protobuf in the encoding an ArcGIS server was observed to write, and their JSON takes the shape it was observed to have

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` — every key and field number below was read off answers ArcGIS Online wrote for public layers, paired value by value with the same layer's JSON (some 60 layers, about 20,000 values, no mismatch). It is not `HIGH` because no client has yet read this server's answer on a device (condition 1), and four field types were not seen at all (§5.3) |
| **Decided** | 2026-11-23, by owner decision through a question dialog: *"Gözleyerek türet"* — derive the protobuf encoding of the `contingentValues` answer by observing a real ArcGIS server's answer, and disclose the derivation, as [ADR-288](ADR-288-offline-sync-is-in.md) did for the mobile geodatabase. No file Esri wrote is committed; test vectors are built from this project's own values. Every reading the observation does not settle is marked `INFERRED` in §5 and listed in §5.8 |
| **Supersedes** | — (amends [ADR-312](ADR-312-a-hosted-layer-keeps-and-serves-contingent-values.md) §5.4 and §5.9, items 3, 5, 6 and 7, which the observation refuted) |
| **Superseded by** | — |

---

## 1. Context

ArcGIS Field Maps 26.2.2, measured by the lead on the Android emulator against dev on 2026-11-23, asks a layer's contingent
values only as **`FeatureServer/0/contingentValues?f=pbf`**. It asked twice, was answered this server's 400 (D-474), and then
offered every coded value — a field crew's form with no narrowing at all. It ignored the service document's
`supportedContingentValuesFormats: "JSON"`, which ADR-312 §5.4 had added on the same day in the hope that it would ask
for JSON instead.

Esri's REST reference for the resource says a protobuf definition *"can be downloaded"*
([Query Contingent Values](https://developers.arcgis.com/rest/services-reference/enterprise/query-contingent-values-feature-service/)),
but no public schema exists: [Esri/arcgis-pbf](https://github.com/Esri/arcgis-pbf) has only `FeatureCollection.proto`,
whose messages are a query result's and do not fit this answer.

ADR-312 §5.9 also recorded seven readings of the **JSON** encoding taken from the reference's examples rather than its
prose. Pairing a real server's JSON with its protobuf tests those readings for free, and four of them turned out wrong.

## 2. Alternatives considered

### Alternative A — derive the encoding by observing a real server's answer (chosen)

**Argument for.** It is what a client reads, not what a page says. Public ArcGIS Online layers serve both formats of one
definition to anonymous readers, so the observation needs nothing created and nothing signed in. Varying one thing at a
time across layers that already exist — a range, a `NULL`, a retired value, a subtype, a text code against an integer
code — maps each JSON key to a field. It is the method ADR-288 already disclosed for the mobile geodatabase.

**Argument against.** An observed encoding covers only what was observed: field types no public layer uses stay unknown,
and a later ArcGIS release may add fields. And reverse-engineering a format a vendor did not publish has to stay on the
right side of §5's line — reading answers is not reproducing source.

### Alternative B — keep refusing protobuf and wait for a public schema

**Argument for.** Nothing inferred is served. ADR-008 §2's rule — refuse an unbuilt option by name — is already met.

**Argument against.** Field Maps then narrows nothing, on the client the band was meant for. The refusal is visible in
the log and in a crew's form, and the schema may never be published.

### Alternative C — answer `f=pbf` with JSON

**Argument for.** One line of code.

**Argument against.** A protobuf parser reading JSON bytes fails or, worse, reads garbage as fields. ADR-312 rejected it
for that reason; nothing has changed.

## 3. Counterarguments to the preferred option

- *An observed format is a guess with evidence, not a contract.* True, and it is why the confidence is `MEDIUM`, why the
  unobserved types are degraded rather than guessed (§5.3), and why the research note says exactly which observation
  stands behind each field.
- *Changing the JSON shape breaks clients that read ADR-312's shape.* Only this server's own Map Viewer read it, and it
  is changed in the same commit. Every ArcGIS client reads ArcGIS's shape, which is the observed one.
- *Serving retired values contradicts ADR-312 §5.4's "a retired value is not served".* That sentence was an inference
  from REST having no key for retirement. REST has one — `retired: 1` — and ArcGIS serves retired values with it; a client
  that does not see a retired value cannot label an existing feature's (D-480).

## 4. Evidence

The observation, its sources, dates and the field-by-field mapping are in the research note
[contingent-values-pbf.md](../research/contingent-values-pbf.md). Summary:

| Claim | Evidence | Source |
|---|---|---|
| ArcGIS Online answers `contingentValues?f=pbf` as `application/x-protobuf`, and `queryContingentValues?f=pbf` with the same bytes for one layer | 23 public layers fetched 2026-11-23, both formats | the research note §1 |
| Every value of every observed definition decodes to its JSON twin under the map of §5.2 | A script compared 14 layers in the first pass and some 60 in the second, about 20,000 values; the only differences were date codes, which §5.2 explains | the research note §3 |
| A layer has a **`fieldGroups`** resource listing each group's `name`, `restrictive` and field names, and the layer document says `hasFieldGroups`, `supportsFieldGroups`, `supportsContingentValues` and `supportedContingentValuesFormats: "JSON, PBF"` | Read on every observed layer | the research note §4 |
| The JSON puts a range in one slot as `{"range":[min,max]}`, a layer without subtypes puts `domains` and `contingentValues` on the group, a retired value is served with `"retired":1`, and a field's `id` is its position in the group | Every observed answer | the research note §5 |
| `compactFormat=false` and `domainDictionaries` `complete` and `trimmed` have the shapes of §5.5 | Fetched for three layers | the research note §5 |
| The protobuf answer does not change with `compactFormat` or `domainDictionaries` | Byte-identical answers | the research note §2 |
| The QA portal's hosting server could not be used for a controlled layer | `createService` answered 500; the server's `rest/info` and `admin/logs/query` answered 500 (2026-11-23) | the research note §0 |

## 5. Decision

### 5.1 How it was learned

**Observed, never copied.** Public ArcGIS Online feature layers whose documents say `hasContingentValuesDefinition: true`
were found through the public item search, and each was fetched anonymously as `contingentValues?f=json` and `?f=pbf`
(read only; nothing was created, changed or signed in to). The captures are kept outside the repository, in
`D:\temp\cv-pbf-capture\`. No captured file is committed. The unit tests encode **this project's own definitions** and
decode them with a reader written in the test; nothing Esri wrote is a test vector.

The QA portal (ADR-288's second source) was tried for a controlled layer with this project's values and could not be
used: its hosting server answered 500 to everything (§4). Nothing was created there.

### 5.2 The protobuf answer

A layer's answer is one message; `queryContingentValues` repeats it once per layer. Proto3 rules hold: a field equal to
its default is not written, except a value's one-of member, which is always written.

| Message | Field | Wire | Meaning |
|---|---|---|---|
| answer | 1 | message, repeated | one layer |
| layer | 1 | varint | `layerID` (omitted for 0) |
| layer | 2 | message | the definition |
| definition | 1 | message, repeated | one field group |
| definition | 2 | string, repeated | the **string table**: every text code the layer's values use, once each, in first-use order |
| field group | 1 | string | `name` |
| field group | 2 | message | the group's values |
| values | 1 | message, repeated | one contingent value |
| value | 1 | varint | `id` |
| value | 2 | sint32 (zigzag) | subtype code (omitted for 0) |
| value | 3 | varint | retired, 1 (omitted when not) |
| value | 4 | message, repeated | one entry per field of the group, in the group's order |
| entry | *(none set)* | — | `Any` |
| entry | 1 | empty message | `Null` |
| entry | 2 | message: 1 min, 2 max, sint | a range on a SmallInteger or Integer field |
| entry | 3 | message: 1 min, 2 max, double | a range on a Date field, in **milliseconds since 1970** |
| entry | 4 | sint32 | a code on a SmallInteger field |
| entry | 5 | sint32 | a code on an Integer field |
| entry | 8 | double | a code on a Date field, as an **OLE Automation date** (days since 1899-12-30) |
| entry | 10 | varint | a code on a String field: its index in the string table |
| entry | 12 | message: 1 min, 2 max, sint | a range on a Double field, **observed only with whole-number bounds** |

What is **not** in the protobuf answer: `layerName`, `geometryType`, `hasSubType`, a group's `restrictive` and fields, the
subtypes' names and `domains`, `typeCodes` and `stringDicts`. A client takes a group's fields and `restrictive` from the
layer's `fieldGroups` resource (§5.4), and a code's meaning from the layer document's domains. A layer with no group
answers an **empty body** (observed).

### 5.3 What was not observed, and what is written for it

No public layer had a code on a Double, Single or BigInteger field, a range on a Single or BigInteger field, or a Double
range with a fractional bound. Their field numbers are unknown, and guessing one risks a parser reading it as something
else. **Such an entry is written as `Any`** — an entry with no member set — so the client offers every value of that
field in that combination rather than refusing a value the definition allows (**INFERRED**: widening is the safe
direction; a guess could narrow). The JSON answer still carries the true value. D-484 holds it until an observation
replaces it.

### 5.4 The layer's `fieldGroups` resource and flags

- **`…/FeatureServer/{id}/fieldGroups`** answers `{"fieldGroups":[{"name","restrictive","fields":[names]}]}` for the
  groups served (the same drift rule as ADR-312 §5.3), and an empty list for a layer with none (**INFERRED**: not seen).
- The layer document of a layer that serves a definition says `hasFieldGroups: true` and
  `supportedContingentValuesFormats: "JSON, PBF"` beside `hasContingentValuesDefinition: true`. The service document says
  `"JSON, PBF"` too. `supportsFieldGroups`, `supportsContingentValues` and `supportsEditingContingentValues` are **not**
  written: they describe what the server could do for any layer, this server writes definitions on hosted layers only,
  and saying nothing is what a layer without them already says (**INFERRED**).

### 5.5 The JSON answer takes the observed shape

Amending ADR-312 §5.4:

- a **range takes one slot**, `{"range":[min,max]}` (ADR-312 §5.9 item 3 refuted);
- a field's **`id` is its position in the group**, from 0 (item 5 refuted);
- a layer **without subtypes** puts `domains` and `contingentValues` on the group, with no `subTypes` (item 6 refuted);
- a **retired value is served**, with `"retired": 1` (item 7 refuted); it is still not offered for a new edit;
- a dictionary's `entries` are the text codes its values use, in first-use order (the index reading, item 2, holds);
- a date code is milliseconds since 1970, as every date in this server's JSON.

And the two options D-474 refused are answered:

- **`compactFormat=false`**: no `typeCodes` or `stringDicts`; `types` as names (`"Any"`, `"Null"`, `"Code"`, `"Range"`);
  every value carries `retired` as `true` or `false`; a code is written as its coded value's **name** where the field has
  a coded domain, and as the code where it has none (**INFERRED**: no observed code lacked a domain).
- **`domainDictionaries=complete`** adds `domainDicts`: for each domain a served group names, a coded domain as
  `{code: name}` in the domain's order and a range domain as `{"rangeMin","rangeMax"}`. **`trimmed`** keeps only the codes
  the values use, in the domain's order, and writes a range domain as `{}`. Both work with either `compactFormat`.
- `addToDefinition` reads a range in either shape and a group's `contingentValues` array on a layer without subtypes, so
  what this server serves can be sent back.

### 5.6 Studio follows

The Map Viewer's reader (`contingent.js`, `cvFromRest`) reads the observed shape, including `retired`, so an existing
feature's retired value is labelled *retired* there too (repays D-480).

### 5.7 Where it lives

The protobuf writer is `ContingentValuesPbf` in `Graticula.Api.ArcGis`, using the hand-written `ProtoBuffer` the
FeatureCollection writer already uses (no package; CLAUDE.md §4). The JSON and the protobuf are written from one
intermediate shape, so the two cannot disagree on which values are served.

### 5.8 INFERRED, for confirmation

1. Unobserved field types are written as `Any` in protobuf (§5.3).
2. A text code used by two domains is one string-table entry (§5.2): no observed layer had two domains sharing a code.
3. The order of values inside a group, which a client does not depend on: by subtype in the layer's order, then id.
4. `fieldGroups` for a layer with none answers an empty list (§5.4).
5. `supportsFieldGroups`, `supportsContingentValues` and `supportsEditingContingentValues` are not written (§5.4).
6. `compactFormat=false` writes a code as itself where the field has no coded domain (§5.5).
7. ArcGIS Enterprise writes the same encoding as ArcGIS Online: the observation is ArcGIS Online's (currentVersion 12),
   because the one Enterprise this work may use could not serve (§5.1).

## 6. Consequences

**Positive.** Field Maps can read a definition from this server through the only address it asks. The JSON answer is the
one ArcGIS clients read, not the one the examples suggested. D-474 and D-480 are repaid.

**Negative.** An observed format with four unobserved types, degraded to `Any` (D-484). A second answer format to keep
in step with the first, mitigated by writing both from one shape. The JSON shape changed under anything that read
ADR-312's, which outside this repository is nothing ArcGIS wrote.

**State.** None. Both answers are computed from the layer's stored definition per request.

**Ports created.** None; no dependency is adopted.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | Field Maps reads the protobuf answer with the `fieldGroups` resource and narrows a coded list from it | Unverified on a device against this server (condition 1) |
| — | ArcGIS Enterprise and ArcGIS Online write the same encoding | Unverified (§5.8 item 7, D-485) |

## 8. Dependencies

**Depends on:** [ADR-312](ADR-312-a-hosted-layer-keeps-and-serves-contingent-values.md) (the model and the resource),
[ADR-288](ADR-288-offline-sync-is-in.md) (the precedent for observing an unpublished format),
[ADR-030](ADR-030-reading-the-reference-implementation.md) (disclosure of a derivation).

**Depended on by:** none yet.

## 9. Revisit triggers

- Esri publishes the `.proto`, or an observed answer shows a field type §5.3 degrades: §5.2 and D-484 are revised.
- Field Maps on a device offers combinations the definition does not allow, or ignores the definition (condition 1).
- An ArcGIS Enterprise answer differs from ArcGIS Online's (D-485).

## 10. Dissent

None recorded.

## 11. Conditions

1. **Field Maps on a device narrows a coded list from a definition this server serves**, measured by the lead on the
   emulator against a build with this ADR. Open: needs the device, which this work does not drive.
2. **The protobuf answer decodes to the JSON answer's definition** for this project's own definitions — integer, text,
   date and range values, `Any`, `Null`, subtypes, a retired value and the unobserved types' degradation — checked by unit
   tests with a reader written in the test, and `f=pbf` checked by a conformance test against a running server. **DISCHARGED
   2026-11-23**: `ContingentValuesAnswerTests` (tests/Graticula.Api.ArcGis.Tests, 13 pass) encode this project's own
   definitions — every observed kind, a subtype, a retired value, a layer id, the string table shared by two domains, the
   date codes, the exact bytes of a one-value answer, and §5.3's degradation — and read them back with
   `tests/shared/ContingentValuesPbfReader.cs`; `ContingentValuesTests` (tests/Graticula.Conformance.Tests, 2 pass) fetch
   `contingentValues?f=pbf` and `queryContingentValues?f=pbf` from the local fixture and find the JSON answer's values in
   them, including a subtype.
3. **The JSON answer and `fieldGroups` have §5.4–5.5's shape**, with `compactFormat=false` and both `domainDictionaries`,
   checked by conformance tests against a running server. **DISCHARGED 2026-11-23**: `ContingentValuesTests` (the local fixture) read the
   layer and service flags, `fieldGroups`, the one-slot range, the group-level values, `retired: 1`, first-use
   dictionaries, `compactFormat=false&domainDictionaries=trimmed` and a refused `domainDictionaries=all`;
   `ContingentValuesAnswerTests` cover `complete` and `trimmed` with both formats.
