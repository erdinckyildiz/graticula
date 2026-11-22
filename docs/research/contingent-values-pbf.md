# Contingent values as ArcGIS writes them: the protobuf answer and its JSON twin

Written 2026-11-23 for [ADR-314](../adr/ADR-314-contingent-values-are-served-as-observed.md). What an ArcGIS server writes
for `contingentValues?f=pbf`, and how each part was mapped to the JSON answer of the same layer.

## 0. How this was learned — the disclosure ADR-030 and ADR-288 require

**Esri does not publish this format.** The REST reference's *Query Contingent Values* page says a protobuf definition
*"can be downloaded"*, but no `.proto` for it is public; [Esri/arcgis-pbf](https://github.com/Esri/arcgis-pbf) holds only
`FeatureCollection.proto`, a query result's messages.

Everything below was therefore **observed**, by owner decision of 2026-11-23 (*"Gözleyerek türet"*). The sources:

| Source | What it gave | Whose data |
|---|---|---|
| Public ArcGIS Online feature layers whose documents say `hasContingentValuesDefinition: true`, found through the public item search (`www.arcgis.com/sharing/rest/search`, queries such as `contingent`) and through the public services directory of one organisation that publishes test layers (`services1.arcgis.com/36PP9fe9l4BSnArw`: `CarsCvRange`, `CarsSubtypesCvDateRange`, `CarsSubtypesCvRetired`, `PersonsCvRange`, `CV_notRestricted`, …) | For each layer, `contingentValues?f=json` and `?f=pbf`, the layer document and its `fieldGroups` resource; for some, `queryContingentValues`, `compactFormat=false` and `domainDictionaries` | Their owners'. **Read anonymously, nothing created or changed, no content kept in the repository** |
| Esri's sample servers (`sampleserver6`, `sampleserver7`) | Nothing: no layer on `sampleserver6` (10.91) has a definition, and `sampleserver7`'s folders need a token | — |
| Aecon's QA portal (ArcGIS Enterprise 2025.2), owner's permission to create and delete | Nothing: `createService` for a layer with this project's values answered *status code: 500*, and the hosting server's `rest/info`, `rest/services` and `admin/logs/query` answered 500 too, 2026-11-23. **Nothing was created there** (a search for the tag `graticula-test` and for the service's title found no item) | — |

66 layers were captured, 23 in the first pass. The captures stay outside the repository, in `D:\temp\cv-pbf-capture\`
(`*_cv.json`, `*_cv.pbf`, `*_layer.json`). **No captured file and no value from one is committed**; the unit tests
encode this project's own definitions.

A finding marked *(measured)* held for every observed layer; *(one source)* rests on one layer or one organisation;
*(not observed)* is a choice this server makes where the observation says nothing.

## 1. Transport

- `…/FeatureServer/{id}/contingentValues?f=pbf` answers **200 `application/x-protobuf`**. *(measured)*
- `…/FeatureServer/queryContingentValues?f=pbf&layers=0` answers the same bytes as the layer resource for a one-layer
  service. With `layers=0,1,2` it repeats the top-level message once per layer. *(measured, the second on one service)*
- A layer whose definition has no field group answers an **empty body**. *(one source)*
- An error is still JSON (`{"error":{…}}`), with 200 for `queryContingentValues` when a named table has no definition.
  *(one source)*

## 2. The messages *(measured)*

Field numbers and wire types were read with a protobuf wire-format dumper written for this (no schema), then each
message was matched against the JSON twin. Proto3 rules hold: a field at its default is not written — `layerID` 0, a
subtype code 0, a range bound 0, `retired` false — except a value entry's member, which is written even when 0 (a text
code at index 0, an integer code 0), as a `oneof` member is.

```text
Answer       1  Layer           repeated
Layer        1  layerID         varint
             2  Definition
Definition   1  FieldGroup      repeated, in the JSON's group order
             2  string          repeated: the string table
FieldGroup   1  name            string
             2  Values
Values       1  Value           repeated
Value        1  id              varint
             2  subtype code    sint32 (zigzag)
             3  retired         varint, 1
             4  Entry           repeated, one per field of the group, in the group's field order
Entry        (no member)        Any
             1  Null            empty message
             2  IntRange        1 min, 2 max: sint (zigzag)          SmallInteger and Integer fields
             3  DateRange       1 min, 2 max: double, ms since 1970   Date fields
             4  sint32          code on a SmallInteger field
             5  sint32          code on an Integer field
             8  double          code on a Date field, OLE Automation date (days since 1899-12-30)
            10  varint          code on a String field: index into the string table
            12  Range           1 min, 2 max: sint (zigzag)          Double fields — whole-number bounds only seen
```

The protobuf answer is the same bytes with `compactFormat=false` and with `domainDictionaries=none|complete|trimmed`.
*(measured on one layer, four variants)*

## 3. How each part was mapped

The method was ADR-288's: vary one thing and see which bytes move. Public layers already vary, so instead of building
them, layers that differ in one respect were paired. A script decoded every answer under the map above and compared it
value by value with the JSON: **14 layers in the first pass, then 66, about 20,000 entries, with no difference other than
the date codes below** — which the map then explained.

| Part | Paired observation |
|---|---|
| Layer, definition, group, value nesting | Every layer. `layerID` appears only on layers whose id is not 0 (`CV_SampleDemoMultiLayer` layers 1–5, `event_gdb…_view` 1–2) |
| Group name | The only string inside a group message, equal to the JSON's `name` in order |
| Value `id` | Equal to the JSON's `id` for every value; within a group the protobuf order is the server's, which is mostly but not always id order |
| Subtype code, zigzag | `CarsSubtypesCvRetired`: codes 2, 1, 3 written 4, 2, 6, code 0 absent; matches each value's `subTypes` entry |
| Retired | The same layer: field 3 = 1 on exactly the values with `"retired": 1` in JSON |
| Entries, one per field | A value of a three-field group has three entries even when JSON's `values` has fewer: `Any` and `Null` take an entry here and no slot in JSON |
| `Any` | `CarsCvRange` id 1, JSON `types [3,1]`: the second entry is an empty message |
| `Null` | The same layer's id 3, `types [3,2]`: the second entry holds field 1 with length 0 |
| SmallInteger code | `GenusDataContingentValues`, `CarsSubtypesCvDateRange`: field 4, value 2n for code n |
| Integer code | `CarsCvRange` (`carModelField`, codes 100–900): field 5, 200 for 100 |
| String code and the table | `PersonsCvRange`: field 10 holds 0–3 and the definition ends with the strings `Ch`, `Yo`, `Ad`, `Se`. `Contingent_values_manager` has two text domains; JSON's `[0,0]` (index 0 in each domain's own dictionary) is `[0,1]` here, because the table is one list of every text the layer uses in first-use order, not one per domain. The table's length equalled the sum of the dictionaries on every layer, and no observed layer had two domains sharing a code |
| Integer and SmallInteger range | `PersonsCvRange` (`Age`, SmallInteger) and `SportStadiumsRanged` (Integer): field 2, a message of two zigzag varints; `[0,14]` is written with only field 2 = 28 |
| Double range | `CarsCvRange` (`carReliabilityField`, Double): field 12, a message of two **zigzag varints**: `[-9,50]` → 17, 100. Every observed Double range has whole-number bounds, so whether a fractional bound uses another field is unknown |
| Date range | `CarsSubtypesCvDateRange`: field 3, two little-endian doubles equal to the JSON's milliseconds (`-315619200000`, `1609459200000`) |
| Date code | The same layer: field 8, a double. 43466.0 where JSON says `1546300800000` (2019-01-01): days since 1899-12-30, the OLE Automation date. Fourteen of fourteen agree, including `24108.0` for `-126230400000` (1966-01-01) |

**Not observed:** a code on a Double, Single or BigInteger field; a range on a Single or BigInteger field; a Double range
with a fractional bound; a GUID or date-only field in a group. ADR-314 §5.3 writes those as `Any` (D-484).

## 4. What the protobuf answer leaves out, and where a client finds it *(measured)*

The protobuf answer carries no field names, no `restrictive`, no subtype names, no domain names and no `typeCodes`. A
client has them from two other resources:

- **`…/FeatureServer/{id}/fieldGroups`** — not in the public reference pages read for ADR-312, found by trying the
  address after seeing the gap — answers
  `{"fieldGroups":[{"name":"…","restrictive":true,"fields":["Age","AgeGroup"]}]}` for every observed layer.
  `fieldGroups?f=pbf` answers *Bad Request*.
- **The layer document**, which on every observed layer says `hasContingentValuesDefinition: true`,
  `hasFieldGroups: true`, `supportsFieldGroups: true`, `supportsContingentValues: true`,
  `supportsEditingContingentValues: true` and `supportedContingentValuesFormats: "JSON, PBF"`, and carries the fields'
  domains and the subtypes. It does not carry the groups themselves.

## 5. The JSON answer, as observed *(measured unless marked)*

What ADR-312 §5.9 read off the reference's examples, compared with what a server writes:

| ADR-312's reading | Observed |
|---|---|
| Item 2 — a text code is an index into its domain's `stringDicts` entries | **Holds.** The entries are the codes the values use, not the whole domain — `CarsCvRange`'s `ratingDomain` has six codes and its dictionary three — in first-use order |
| Item 3 — a range takes two slots | **Refuted.** One slot: `{"range":[min,max]}` |
| Item 4 — `Any` and `Null` take no slot | **Holds** |
| Item 5 — a field's `id` is its position in the layer's `fields` | **Refuted.** Its position in the group: `PersonsCvRange`'s `Age` is the layer's second field and the group's `id` 0 |
| Item 6 — a layer without subtypes has one `subTypes` entry with code 0 | **Refuted.** The group carries `domains` and `contingentValues` itself, with no `subTypes` |
| Item 7 — retired values are not served | **Refuted.** Served, with `"retired": 1`; a value that is not retired has no `retired` key |

A date code in JSON is milliseconds since 1970.

**`compactFormat=false`** (`PersonsCvRange`, `CarsCvRange`, `CarsSubtypesCvRetired`): no `typeCodes`, no `stringDicts`;
`types` are names (`"Code"`, `"Any"`, `"Null"`, `"Range"`); every value has `"retired": true|false`; a code — text or
integer — is written as its coded value's **name** (`"Children"` for `Ch`, `"Corolla"` for 100); a range is unchanged.
Whether a code on a field with no coded domain is written as itself was not observed.

**`domainDictionaries`** (`PersonsCvRange`, `CarsCvRange`): adds `domainDicts` beside the rest, keyed by domain name, for
the domains the groups name. `complete`: a coded domain as `{"100":"Corolla", …}` in the domain's order, a range domain as
`{"rangeMin":-10,"rangeMax":100}`. `trimmed`: only the codes the values use, still in the domain's order, and a range
domain as `{}`. Both combine with `compactFormat=false`, which keeps `domainDicts` and drops the rest as above.

## 6. What this does not tell

- Whether ArcGIS Enterprise writes the same bytes as ArcGIS Online (D-485).
- The four unobserved field types (D-484).
- What a client does with any of it. Field Maps 26.2.2 asks for `f=pbf` (measured by the lead, 2026-11-23); whether it
  narrows a list from this server's answer is ADR-314 condition 1.
