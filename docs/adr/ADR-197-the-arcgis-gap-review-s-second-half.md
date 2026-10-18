# ADR-197 — The ArcGIS gap review's second half: what a layer says it can count, compute and page

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` |
| **Decided** | 2026-10-18, by owner decision — *"Küçük ArcGIS farkları"* chosen from the open findings of the ArcGIS veteran's review of 2026-10-17 |
| **Supersedes** | — |
| **Superseded by** | — |

---

## 1. Context

The ArcGIS veteran's review of 2026-10-17 (its row 15, and V-88 in the owner's list) found a layer document
that said less than the server does and, in one place, less than ArcGIS says:

- `supportsCountDistinct` was `false`, though both providers have answered `returnCountOnly` with
  `returnDistinctValues` with the number of distinct combinations since 2026-08-19.
- `supportsSqlExpression` was `false`, and `onStatisticField` took a field name only. Esri's layer reference
  ties the flag to an expression in `outStatistics`, `groupBy` or `orderBy`, with `str_angle*100` its one example.
- `standardMaxRecordCount`, `tileMaxRecordCount`, `maxRecordCountFactor` and `uniqueIdField` were absent, and
  `supportsQueryWithResultType` was `false`. The Maps SDK reads all four and falls back to its own guesses.

The rest of row 15 — `find`, `queryTopFeatures`, `having`, empty `templates`, the lost error log — was repaired
the day before (`99536c98`).

## 2. Counting distinct values, and a statistic over an expression

**`supportsCountDistinct` is `true` on every layer.** Esri's reference names the flag without defining it; the
query page's only count of distinct values is `returnCountOnly=true` with `returnDistinctValues=true` (*"can be used
with `returnCountOnly` to return the count of distinct values of subfields"*). **That this is what the flag means is
`INFERRED`** — listed for confirmation below. `AdvertisedCapabilityTests` now drives it rather than listing it as
unexercised.

**`onStatisticField` takes an SQL expression on PostGIS layers.** [ADR-008](ADR-008-query-engine.md) §4a-iv records
how: the expression is parsed by the where grammar as the operand of one `IS NULL` test, refused unless that is the
whole tree, and written through the same emitter with every literal bound. It needs an `outStatisticFieldName`, and
only `count`, `min` and `max` take a value that is not a number. GeoParquet layers say `false` and refuse it in their
own words. **`groupByFieldsForStatistics` takes one too since D-301 was repaid**, answered as `Expr1`, `Expr2`… as Esri's
server answers it; **`orderByFields` refuses one**, as Esri's server does ([D-301](../architecture-debt.md)).

### Alternative — leave both flags false

**For.** No client measured here sends either. **Against.** The count of distinct values already worked, so the
`false` was a document disagreeing with the server, which this project has called a defect every other time; and the
expression costs one entry point on a grammar that already evaluates ArcGIS's functions.

## 3. The page figures

**`standardMaxRecordCount` and `tileMaxRecordCount` are the layer's `maxRecordCount`, and `maxRecordCountFactor` is
`1`.** This server cuts a standard query and a tile query at the same page and does not honour the factor (it is on
the ignored list: the ceiling stays and `exceededTransferLimit` says so), so these are the figures a client will
actually meet. **`uniqueIdField`** names the object id, `isSystemMaintained` on hosted layers.

**`supportsQueryWithResultType` stays `false`, and that was measured.** This paragraph first argued that true would move the Maps SDK onto `resultType=tile` requests, and that premise was wrong: **SDK 4.29 sends `resultType=tile` with the flag false or true**, and when a tile answers `exceededTransferLimit` it splits the tile in four and asks again. With the page cut to 100 and a 600-polygon layer, it drew **600 of 600 at zooms 13, 10 and 6** in 14 requests — six 4.9 km tiles, then eight 2.4 km quarters of the ones that overflowed — and the same 14 requests and 600 features with the flag forced true. The flag changes nothing the SDK does, and false is what this server is: it answers a tile query as a standard one, thinning nothing.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| The expression is bound, not passed through | `StatisticExpressionTests`; `SqlStaysOutOfTheQueryModelTests` permits the one member | this commit |
| The answer is right | `StatisticOverAnExpressionTests`: `avg(oid*2) = 2·avg(oid)`; a distinct count of ids equals the feature count | conformance fixture |
| SDK 4.29 draws a 600-feature layer whole through a page of 100, splitting overflowing tiles, with `supportsQueryWithResultType` false or true | headless Brave, `FeatureLayer` + `layerView.queryFeatureCount()`, requests logged by interceptor | local fixture, 2026-10-18 |
| Esri's definitions | Layer (Feature Service) and Query (Feature Service/Layer) reference pages, read 2026-10-18 | developers.arcgis.com |

## 5. Decision

Both flags are true where the source answers them; the page figures are published as the one figure they are; the
result-type flag waits for a measurement.

## 6. Consequences

**Positive.** Four keys a client reads are present and true to the server; a statistic over arithmetic answers.

**Negative.** An expression in `orderByFields` is refused, as Esri's server refuses it (D-301). The page figures say plainly that a
tile query gets no more than a standard one.

**State.** None: an expression is parsed per request and nothing is stored.

## 7. Conditions

1. **`supportsCountDistinct` means the `returnCountOnly` + `returnDistinctValues` pair** — `INFERRED`, for the
   owner's confirmation, or for an Esri layer answering both ways. *(Discharged 2026-10-18 by Esri's layers: sampleserver6's `Wildfire/FeatureServer/0` says `supportsCountDistinct:
   true` and answers the pair with 31 distinct descriptions against 517 features; `countDistinct`, `count_distinct` and
   `count` over `DISTINCT field` in `outStatistics` are each refused with a 400. The pair is the only count of distinct
   values it has.)*
2. **`supportsQueryWithResultType` is turned on only after the Maps SDK has been watched drawing a layer larger than
   its page** through `resultType=tile` against this server. *(Discharged 2026-10-18: watched — 600 of 600 drawn with a page of 100, with the flag either way; it stays false. §3.)*
3. D-301 is repaid or deferred by decision. *(Discharged 2026-10-18: repaid — a grouped expression answers as `ExprN`, as Esri's server answers it, and an ordering expression is refused, as Esri's server refuses it.)*

## 8. Dependencies

**Depends on:** [ADR-008](ADR-008-query-engine.md) §4a, [ADR-083](ADR-083-the-where-clause-evaluates-arcgis-standard-functions.md), [ADR-084](ADR-084-the-page-size-is-one-number.md).

## 9. Revisit triggers

A client observed sending an expression in `orderByFields`; a client observed
behaving differently on `maxRecordCountFactor: 1`.

## 10. Dissent

None recorded.
