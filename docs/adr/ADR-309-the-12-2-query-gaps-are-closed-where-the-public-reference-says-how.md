# ADR-309 — The 12.2 query gaps are closed where the public reference says how, and refused by name where it does not

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` |
| **Decided** | 2026-11-21 (by the agent that did the work, under the owner's approval of *band 1* of the Enterprise 12.2 gap report; the INFERRED choices in §5 are listed for confirmation) |
| **Supersedes** | — (amends [ADR-008](ADR-008-query-engine.md) §2's list of ignored and refused parameters) |
| **Superseded by** | — |

---

## 1. Context

The owner asked on 2026-11-21 what separates Graticula from ArcGIS Enterprise 12.2 and approved the cheap REST gaps that
ArcGIS clients send today. Four of them concern the query side, and two were worse than *missing* — they answered a
different question than the one asked, which [ADR-008](ADR-008-query-engine.md) §2 forbids:

1. **`queryAttachments` with `returnCountOnly`.** The parameter was never read, so a client asking for a count per feature
   got the full list. (`attachmentsWhere` and `size` were refused; `orderByFields` and the reference's actual name for the
   attachments filter, `attachmentsDefinitionExpression`, were not read at all and passed unnoticed.)
2. **`gdbVersion`.** `FeatureServerQueryParameters` carried a comment saying it was refused, and an entry in the
   accepted-and-ignored table saying *there is no version tree*. The code did what the table said. A client asking for a
   named version was answered from the default one. `applyEdits` and `queryRelatedRecords` ignored it the same way.
3. **`outDistance`**, new at 12.2 for hosted layers: an unknown parameter, so 400 with a sentence that told a 12.2 client nothing.
4. **`queryBins`** (hosted feature services since 11.3, with `lodBin` new at 12.2): absent from the code entirely.

The reference is [Query Attachments](https://developers.arcgis.com/rest/services-reference/enterprise/query-attachments-feature-service-layer/),
[Query Bins](https://developers.arcgis.com/rest/services-reference/enterprise/query-bins/),
[Query (Feature Service/Layer)](https://developers.arcgis.com/rest/services-reference/enterprise/query-feature-service-layer/) and
[What's new](https://developers.arcgis.com/rest/services-reference/enterprise/whats-new/), read on 2026-11-21.

## 2. Alternatives considered

### Alternative A — Refuse everything that is new and implement nothing

**Argument for.** Smallest change, no new behaviour to be wrong about, and it ends the silent substitution.

**Argument against.** `queryBins` is what Dashboards' histograms and the Maps SDK's binned charts call, and a hosted layer
here is already one `GROUP BY` away from it: the statistics machinery, the filters, the budget and the provider exist. Refusing
what can be built from a public specification in a day is leaving work undone and calling it caution.

### Alternative B — Implement each parameter as far as the reference states it, and refuse by name the rest *(chosen)*

**Argument for.** A client gets an answer where the reference says what the answer is, and a sentence naming the missing
piece where it does not. Nothing is advertised that is not done.

**Argument against.** Several of the choices below are read from examples rather than from prose (§5), and a wrong reading
is a wrong answer, not a refusal. They are labelled INFERRED and listed.

### Alternative C — Implement `lodBin` and `outDistance` from how the name suggests they work

**Argument for.** A 12.2 client sends them; guessing might serve it.

**Argument against.** Neither is described in any public page that was readable on 2026-11-21: the *What's new* list names them
(*"The Query Bins operation now supports the lodBin parameter"*, *"The outDistance parameter has been added to the layer-level Query
operation"*) and the operation pages do not yet define their value, unit or output. Building a behaviour from a name is how a
compatible-looking server produces a column or a bin that means something else to the client reading it. Refused.

## 3. Counterarguments to the preferred option

*gdbVersion is stricter than ArcGIS.* The reference says the parameter *applies only if the `isDataVersioned` property of the
layer is `true`*, and ours is false on every layer, so ArcGIS would ignore a non-default version on such a layer and answer. This
ADR refuses it instead. The case for ignoring is real: a client that sends a version name on every request (a map authored
against a versioned service and republished) is answered by ArcGIS and refused here. The case for refusing is that the layer
document says `isDataVersioned: false`, a client that reads it will not send one, and a client that sends one anyway is
mistaken about where its edit or read lands — which for an edit is a write to a version it did not mean. Recorded as D-463 with
the observed-client trigger that would reverse it.

*The attachments filter is in memory.* A store answers one feature's list at a time, so `attachmentsDefinitionExpression`
filters the list after it is read. For the thousand-feature ceiling that is the cost the listing already pays; it is not a push-down.

*`queryBins` is PostGIS only.* The four other providers write group expressions from a parsed operand, and this one is written
SQL. They refuse by name rather than answer; the layer document says nothing about `queryBinsCapabilities` for them.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| `returnCountOnly` answers `{parentObjectId, parentGlobalId, count}` per feature, only for features with at least one attachment, and `attachmentInfos` is absent | Reference text; conformance test `Query_attachments_with_returnCountOnly_answers_a_count_per_feature_and_no_list` | Query Attachments |
| `attachmentsDefinitionExpression` filters the attachments by the names in `attachmentProperties`/fields; `size=min,max` bytes; `resultOffset`/`resultRecordCount` page; `keywords` are case insensitive | Reference text; `Query_attachments_filters_by_size_expression_and_pages_and_refuses_what_it_does_not_do`, `TheQueryGapsOf122AreReadAsDocumentedTests` | Query Attachments |
| `queryBins` takes `bin` of `dateBin`, `fixedIntervalBin`, `autoIntervalBin`, `fixedBoundariesBin`; lower side inclusive, upper exclusive, the last upper of boundaries inclusive; the last item of an auto range goes in the last bin; empty bins are present with a count of 0 and null statistics; default statistic is `frequency` | Reference text and its examples 1–3 | Query Bins |
| The boundary fields are `lowerBoundary`/`upperBoundary`, renamed by `lowerBoundaryAlias`/`upperBoundaryAlias`, and `hideUpperBound` drops the upper | Reference text | Query Bins |
| `offset` is `{"number": n, "unit": second\|minute\|hour\|day}` | The syntax block of the *Binning* section | Query Bins |
| Bins computed by the database equal a hand count on a ten-point layer | `Query_bins_divides_a_numeric_field_by_…`, `Query_bins_divides_a_date_field_by_a_unit_and_an_offset` against PostGIS | this repository |
| `lodBin` and `outDistance` are named and not described | Fetches of all three pages and *What's new* on 2026-11-21 returned no definition of either | the pages above |

## 5. Decision

**1. `queryAttachments`** reads `returnCountOnly`, `attachmentsDefinitionExpression`, `size`, `resultOffset` and
`resultRecordCount` as the reference states them, alongside the `attachmentTypes`, `keywords` (now case insensitive, as the
reference says), `returnUrl` and `definitionExpression`/`objectIds`/`globalIds` it already read. `returnMetadata` is accepted:
`exifInfo` is null whether asked or not, which is what the reference says an attachment gets when EXIF is not enabled, and
`attachmentProperties` says it is not. `orderByFields` and `attachmentsWhere` (a name the reference does not have) are **refused
with 400**. The layer document gains `attachmentProperties` and `supportsQueryAttachmentsCountOnly`,
`supportsQueryAttachmentsWithReturnUrl` (true) and `supportsQueryAttachmentOrderByFields` (false) in
`advancedQueryCapabilities`, only where attachments are kept. `supportsAttachmentsResizing` is not said: nothing is resized.

**2. `gdbVersion`** other than empty, `sde.DEFAULT` or `DEFAULT` (any case) is **refused with 400** by the layer query,
`queryTopFeatures`, the attachment/forms faces that share the query parser, `queryRelatedRecords`, and every edit operation
(`applyEdits`, `addFeatures`, `updateFeatures`, `deleteFeatures`; with *Nothing was applied* in the sentence). The comment is
corrected, and the entry leaves the ignored table, so one helper (`FeatureServerQueryParameters.GdbVersionRefusal`) is the
single statement of what the default is.

**3. `outDistance`** is accepted when empty and **refused by name** with a value; `lodBin` is refused by name inside `queryBins`.
Neither is advertised. D-460 records what would reopen them.

**4. `queryBins`** is answered on hosted and registered PostGIS layers at `…/FeatureServer/<id>/queryBins`: the four bin types
over a numeric (or, for `dateBin`, date) field, `binOrder`, `hideUpperBound`, `firstDayOfWeek`, `snapToData: first`,
`returnFullIntervalBin`, the two aliases, `where`/`geometry`/`spatialRel`/`distance`/`units`/`inSR`/`outSR` and `outStatistics`
(any statistic the layer query takes, default a `frequency` count). It is the layer query's own handler told to read `bin`: a
bin is the group `GroupExpression` over the bin's index, so filters, the connection budget and the provider are the query's.
Refused by name, not ignored: `classificationBin`, `stackBy`, `splitBy`, `transformation`, normalization, `onExpression`,
`snapToData: last`, `async`, `returnCentroid`, `quantizationParameters`, a non-empty `outTimeReference`, spatial-aggregate
statistics, and any parameter not listed. One answer holds at most 10,000 bins, empty ones included. The layer document says
`queryBinsCapabilities` with a flag per thing done and false per thing refused, on PostGIS layers only.

**INFERRED, for confirmation** (the public reference does not settle them): **Confirmed by the owner 2026-11-22, all as written** (*"Hepsi yazıldığı gibi"*), together with the release as v1.0.528.

- **I-1.** `gdbVersion`'s default names: `sde.DEFAULT` and `DEFAULT`, case-insensitive. The reference gives `SDE.DEFAULT` as its example.
- **I-2.** Refusing a non-default `gdbVersion` on a layer whose `isDataVersioned` is false, where ArcGIS ignores it (§3).
- **I-3.** `size=min,max` is inclusive at both ends.
- **I-4.** `resultOffset`/`resultRecordCount` page over attachments in (feature id, attachment id) order, and over features when `returnCountOnly` is set; the page ceiling is the server's `MaximumRecordCount`.
- **I-5.** `attachmentsDefinitionExpression` accepts `ATTACHMENTID`, `ATT_NAME`, `CONTENT_TYPE`, `DATA_SIZE`, `KEYWORDS` and the property names `id`, `name`, `contentType`, `size`, `keywords`; comparisons, `LIKE`, `IN`, `BETWEEN`, `IS [NOT] NULL`, `NOT`, `AND`, `OR`. `GLOBALID` and `REL_GLOBALID` are not accepted.
- **I-6.** Where the three attachment filters meet, a feature appears only if one of its attachments passes all of them.
- **I-7.** Bin boundaries are the origin plus `offset` plus whole intervals (from example 3: a day bin starting 16:00 UTC with an 8 hour offset begins at midnight). Without a `start`, the origin is the first value cut back to the start of its unit in the shifted time, and a month, quarter or year begins on a calendar month. With a `start` inside a month, the first month bin is that whole month and values before `start` are excluded.
- **I-8.** A numeric bin with an `end` counts a value equal to `end` in the last bin. A fixed interval without an `end` has as many bins as the data reaches, and its bins are the full interval wide.
- **I-9.** `firstDayOfWeek` is ISO (1 Monday to 7 Sunday), and the default is Sunday. A `dateBin` with `returnFullIntervalBin` false stops its first and last bin at the data, or at `start`/`end` when given.
- **I-10.** Empty bins are returned between the first bin and the last, and from the start to a stated `end`.
- **I-11.** `queryBinsCapabilities` is at the layer document's top level; the reference defines the flags and not where they sit.

## 6. Consequences

**Positive.** A client asking for an attachment count gets a count; a client naming a version it will not get is told; a 12.2
client sending `outDistance` or `lodBin` is told which one, not *unknown parameter*. Histograms and date-binned charts work
against a hosted or registered PostGIS layer. The change adds no table and no migration.

**Negative.** Eleven choices above are inferred and a client that reads an example differently gets a differently-bounded bin.
`queryBins` ignores nothing, so a client that sends a parameter this server does not do (`stackBy`) gets a 400 where ArcGIS
would stack. `gdbVersion` is stricter than ArcGIS (§3).

**Ports created.** None.

**State.** None in the catalogue and nothing held at runtime beyond the request: `queryBins` and the attachment filters read the layer's existing fields and attachment stores, add no table and no migration, and keep no cache. The layer document gains `attachmentProperties`, `queryBinsCapabilities` and three `advancedQueryCapabilities` flags, all derived on each request.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | No entry in [architecture-assumptions.md](../architecture-assumptions.md) is touched; the inferences are in §5. | — |

## 8. Dependencies

**Depends on** (upstream): [ADR-008](ADR-008-query-engine.md) §2 (refused rather than ignored) and §4a (nothing the caller wrote
reaches SQL), [ADR-083](ADR-083-the-where-clause-evaluates-arcgis-standard-functions.md) where it exists in the tree (the statistic expressions), [ADR-207](ADR-207-attachments-and-history-on-registered-layers.md) and
[ADR-208](ADR-208-a-registered-mysql-database-is-served-like-postgis.md) (registered attachments).

**Depended on by** (downstream): none yet.

## 9. Revisit triggers

Conditions, each to be marked `**DISCHARGED 2026-…**` in its own text when met.

1. **`lodBin` and `outDistance` are described in the public reference.** On the day either page defines the value, unit and
   output, implement it from that text (D-460). Until then both are refused by name.
2. **A client observed sending a non-default `gdbVersion` to a layer that says `isDataVersioned: false` and expecting an
   answer** reopens §5 I-2 (D-463).
3. **`queryBins` asked of a layer on a non-PostGIS source by a client that matters** (D-461).
4. **Dashboards, the Maps SDK or the ArcGIS API for Python is pointed at `queryBins` and reads a bin differently from §5 I-7 to
   I-10.** The pairings are the INFERRED ones; a measured difference against Esri's own answer replaces the inference.
5. **`orderByFields` or EXIF on `queryAttachments` is asked for** (D-462).

## 10. Dissent

None recorded. The one disagreement worth the reader's attention is §3's: the reference's own words support ignoring
`gdbVersion` here, and this ADR refuses it on the grounds that ignoring hides a mistake. That is a judgement and is marked as one.
