# ADR-073 — A FeatureServer query answers in PBF, on the grid the caller names

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` |
| **Decided** | 2026-09-15. The owner asked for the gaps an experienced ArcGIS user would see to be worked through (*"kalandan devam et"*), and this was on that list as *PBF çıktısı ve koordinat nicemleme yok*. **That `f=pbf` should be answered is the owner's, through that instruction. The default grid when a request names none, the refusal of statistics in pbf and the refusal of an extent in another reference are this ADR's choices** |
| **Supersedes** | — |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

Every layer document said `supportedQueryFormats: "JSON"` and `supportsCoordinatesQuantization:
false`, `f=pbf` was refused, and `quantizationParameters` was accepted and ignored. The ArcGIS Maps
SDK for JavaScript reads those two flags to decide how to ask for features: against ArcGIS it asks
for `f=pbf` with a quantization grid sized to the screen, and against this server it fell back to
full-precision JSON on every layer. A large polygon layer therefore reached a browser as several
times the bytes, and was parsed as text, which is where the client spends its time.

Esri publishes the message: `FeatureCollection.proto` and a README with worked examples, at
`github.com/Esri/arcgis-pbf`, under Apache 2.0. It is a public specification in the sense
[CLAUDE.md §5](../../CLAUDE.md) and ADR-030 require for a citation, and nothing here is derived from
anything else.

## 2. Alternatives considered

### Alternative A — encode the published message by hand, buffered, bounded by the response ceiling *(chosen)*

**Argument for.** What a FeatureCollection needs from Protocol Buffers is varints, zigzag,
length-delimited fields and packed repeats — a page of code against Google's published encoding
rules. The field numbers come from the proto. The writer mirrors `FeatureServerQueryWriter` rule for
rule (header, types, aliases, value encoding, `exceededTransferLimit`), so the two formats answer
the same rows and columns.

**Argument against.** A length-delimited message states its length first and the features are
inside one, so the response is held in memory until the last feature — which the JSON face
deliberately stopped doing (ADR-062).

### Alternative B — adopt a Protocol Buffers library and generate the types

**Argument for.** A conformant encoder nobody here has to get right.

**Argument against.** A runtime dependency and a code generator in the build, to write a dozen
messages, and the generated types still buffer nested messages the same way. The part that can be
wrong — which vertex is absolute, which way `y` runs, how a value is typed — is not the encoder's.

### Alternative C — keep refusing `f=pbf`

**Argument for.** Nothing new to be wrong about.

**Argument against.** The fallback works, so nothing breaks — but the one client most users meet
draws every layer here more slowly than it draws the same data from ArcGIS, and the reason is a flag.

## 3. Counterarguments to the preferred option

**The per-part delta rule rests on the specification's examples, not on a client.** The README's
polygon example starts its second ring at `56, 56` rather than at the difference from the first
ring's last vertex, and its polyline example does the same, so each part here begins with an
absolute vertex. The independent decoder used as a control (`arcgis-pbf-parser`) reads it the same
way. **No ArcGIS client has yet drawn a multi-part geometry from this server**; if the Maps SDK reads
differences across parts, every multi-part shape past its first part would be drawn displaced, with
no error. Single-part shapes and points do not depend on the rule. That is condition 1, and why
confidence is `MEDIUM`.

**Buffering reverses a streaming decision for one format.** The bound is the response byte ceiling
(Q-113), checked after each feature exactly as the JSON writer checks it, and the encoding is several
times denser, so the same ceiling holds more rows in less memory than the JSON that used to be sent.
A deployment with no ceiling set buffers a whole page of up to `maxRecordCount` features.

**`mode=view` is not ArcGIS's generalisation.** Here a vertex that lands on the cell of the one kept
before it is dropped, and a part that collapses below two vertices (a line) or four (a ring) goes
with it; a shape with nothing left keeps its row and loses its geometry. ArcGIS's view mode is
documented to generalise to the tolerance, which may drop more. The result here is never wrong at
the grid's resolution — it is sometimes larger than it needs to be.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| Field numbers, enums, the transform and the value oneof | `FeatureCollection.proto` | `github.com/Esri/arcgis-pbf`, Apache 2.0 |
| Geometry is always integers, delta-encoded, with a transform in the payload | README, *Querying* | same |
| Each part starts from an absolute vertex | README polygon and polyline examples (`56, 56`; `1 1` in feature 1) | same |
| The server's bytes decode to the input coordinates, holes, parts and attributes | A two-polygon, one-hole feature on a 0.5 grid with an upper-left origin, written by `FeatureCollectionPbfWriter` and decoded by `arcgis-pbf-parser` 0.0.4: coordinates exact, rings and parts as written | Scratchpad control, 2026-09-15; the parser is not shipped or referenced |
| Rings are wound as the JSON writer winds them; view mode drops cells and collapsed parts; the ceiling truncates after a feature | `FeatureCollectionPbfTests`, decoding with `tests/shared/PbfReader.cs`, written from the proto and not from `/src`. Falsified: carrying deltas across parts fails the per-part test | `tests/Graticula.Api.ArcGis.Tests` |
| pbf and json answer the same rows, columns, ids, counts and coordinates on a live server | `APbfAnswerIsTheJsonAnswerTests` | `tests/Graticula.Conformance.Tests`, CI |

## 5. Decision

`f=pbf` on `FeatureServer/{id}/query` — and so on `MapServer/{id}/query`, which is the same handler —
answers `application/x-protobuf` with the specification's `FeatureCollectionPBuffer`, version `"1"`:
a `featureResult` for features, a `countResult` for `returnCountOnly`, an `idsResult` for
`returnIdsOnly` and an `extentCountResult` for `returnExtentOnly`. `outStatistics` with `f=pbf` is
refused with a reason.

Coordinates are written on the grid `quantizationParameters` names: `tolerance` is the cell, the
`extent`'s upper-left or lower-left corner (`originPosition`, default upper-left) is the origin,
and `mode` is `view` (default) or `edit`. An extent in a reference other than the response's is
refused. Without `quantizationParameters` the grid is a billionth of a degree or a tenth of a
millimetre, from an origin of zero, in edit mode. A json answer is written at full precision and
still logs `quantizationParameters` as ignored.

Values follow the JSON writer: the object id as `uint_value` (or `sint64_value` beyond 32 bits), a
64-bit integer as text, a date as epoch milliseconds in `sint64_value`, a boolean as `sint_value`
1/0, a GUID braced in `string_value`, null as `null_value`. The layer and service documents say
`supportedQueryFormats: "JSON, PBF"` and `supportsCoordinatesQuantization: true`.

**Conditions.**

1. **An ArcGIS Maps SDK for JavaScript client draws a multi-part polygon layer from this server in
   the right place** — the check §3 says is missing — before this is `ACCEPTED`. If it does not, the
   per-part rule is wrong and the flags go back to `JSON` until it is repaired.

## 6. Consequences

**Positive.** The Maps SDK asks this server for pbf as it asks ArcGIS, and receives a smaller answer
it parses without text. Any client that reads the published proto can use the format.

**Negative.** A pbf page is held in memory until it is complete (§3). Two encoders now describe one
answer, and a rule changed in one and not the other is a disagreement between formats;
`APbfAnswerIsTheJsonAnswerTests` compares them on every CI run for exactly that reason.

**State.** None. Nothing is stored; the grid is per request.
