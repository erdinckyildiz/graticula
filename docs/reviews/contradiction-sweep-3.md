# Contradiction sweep 3 — the §66 consistency gate, run against four surfaces one day old

**Run 2026-08-20** by an independent reviewer that did not write the code, per §67.
Scope: the protocol surfaces added on 2026-08-19 and 2026-08-20 — WFS 2.0, the ArcGIS
portal, WMS 1.3.0/1.1.1 with WMS-T, ArcGIS MapServer, OGC API Features — and every
document that describes them.

**The method is [contradiction sweep 2](contradiction-sweep-2.md)'s, in the same
direction: each decision read against the code that now exists.** Sweep 2 ran on
2026-08-15 against three faces. Four more have shipped since, in two days, and a
sweep's value decays faster than any other review here because it is entirely about
what changed.

**Two classes of finding, and they are not equally interesting.** *Behavioural*: two
faces of one server disagree, or one face disagrees with its own message.
*Documentary*: the repository says something the code contradicts. All four findings
below were reproduced before they were written down — three against the running server
and one by counting call sites.

## Result

**FAIL. Four findings. Three repaired the same day, one recorded as debt because it is
not a defect but a missing capability.**

**The documentary finding is the largest**, and it is the one this gate exists for: a
decision taken in one file and left deferred in eight others.

---

## S1. Two doors on one house had different locks — repaired

`GetMap` with an unknown style refused correctly. `GetLegendGraphic` with the same
unknown style on the same layer answered 200 with the default swatch:

```
REQUEST=GetMap&LAYERS=hosted/look_parcels&STYLES=nonexistent
    → ServiceException code="StyleNotDefined"
REQUEST=GetLegendGraphic&LAYER=hosted/look_parcels&STYLE=nonexistent
    → 200 image/png, the default swatch
```

**Both are the same rule in WMS 1.3.0 §7.3.3.4 and only one implemented it.** A client
that asks for a legend in a named style and is quietly given the default draws a key
that does not describe the map beside it — worse than the error, because a legend is
believed by a human rather than checked by a program.

**Repaired** in `WmsRequest.TryLegend`: the style name is resolved against the layer's
styles and refused as `StyleNotDefined` when it is not there, through the same call the
`GetMap` path makes.

## S2. A decision was taken in one file and left deferred in eight — repaired

**[ADR-004](../adr/ADR-004-rendering-engine.md) read `DEFERRED` and its §5 read
*Pending*, on 2026-08-20, with the renderer shipped, two faces serving from it, a
benchmark measured and a CITE run recorded.** `grep -c "ADR-041"` in that file returned
**0**. ADR-041 had amended it — in ADR-041's own front matter.

**Then the restatements, which is why this costs more than a stale header.** Eight
places said the deferral was current, each written by somebody who had read ADR-004 and
trusted it:

| Where | What it said |
|---|---|
| [v1-scope.md](../v1-scope.md) §3b | ADR-004 stays `DEFERRED` |
| [v1-scope.md](../v1-scope.md) §3b | *Q-85 dissolves* on those grounds |
| [open-questions.md](../open-questions.md) Q-26 | cross-tile labels are *no longer a problem the platform has* |
| [open-questions.md](../open-questions.md) Q-47 | WMS-client migration is unsupported |
| [protocol-surface.md](../protocol-surface.md) | Render — deferred |
| [product-context.md](../product-context.md) | WMS: **No** |
| [competitive-position.md](../competitive-position.md) §6a | ADR-004 remains `DEFERRED` |
| [architecture-completeness.md](../architecture-completeness.md) | rendering rescoped, DEFERRED |

**Q-26 is the expensive one.** It was closed on the grounds that labels are placed
client-side. The renderer places labels on the server, per request, so the question is
live again — and it was live for a day while the register said the platform did not
have the problem.

**Repaired, all nine.** ADR-004 carries a new §0b naming the un-deferral and listing
what it cost; Q-26 is reopened with the specific failure stated; Q-47 is answered again;
the five capability documents are corrected. **Q-85's old sentence is struck through
rather than deleted**, because a document that silently changes its mind teaches
nothing.

**The process finding is the one to keep.** On 2026-08-19 one surface shipped and four
capability documents were amended for it, one of them carrying a paragraph explaining
why it was deliberately *not* amended further. On 2026-08-20 four surfaces shipped and
none of those documents was touched. **This repository has a working propagation
discipline and nothing that notices when it is skipped.** Recorded as
[D-126](../architecture-debt.md), where the repayable form is named: an ADR that amends
another declares it, and `tools/registers-check.py` asserts the named file mentions the
amending ADR.

## S3. An error message named a feature the same server advertises — repaired

Sending an unknown WFS `REQUEST` produced a list of what is supported, and the list
contradicted itself:

```
REQUEST=Nonsense → "…Transaction, LockFeature and GetPropertyValue are not implemented."
GetCapabilities  → <ows:Operation name="GetPropertyValue">
REQUEST=GetPropertyValue → 200, correct values
```

**The message was written before the operation and never revisited.** A code comment
one file over says as much: *GetPropertyValue was not implemented hours after §5
started*. Then it was, and the refusal text was not.

**Small, and it is in this report on purpose.** An error message is the only
documentation many clients ever read, and one that names an implemented feature as
missing sends a developer to build a workaround for a problem they do not have.

**Repaired.** The sentence names `Transaction` and `LockFeature`, which are the two
genuinely absent, and the supported list is the same string the capabilities document
is built from.

## H1. Degraded serving is a property of three faces, not of the server — recorded as debt

**[ADR-026](../adr/ADR-026-serving-through-a-platform-store-outage.md) answers Q-95
with a fallback**: a service already resolved stays servable while the platform store
is unreachable. Counting the call sites:

| Face | Reaches `CatalogFallback`? |
|---|---|
| FeatureServer | yes — the routes take it directly, 6 handlers |
| MapServer | yes, through `ServiceLookup` |
| VectorTileServer | yes, through `ServiceLookup` |
| **WFS** | **no — the name appears in two comments and no call** |
| **WMS** | **no** |
| **OGC API Features** | **no** |
| **Portal** | **no** |

`WfsEndpoints` is the one worth stating carefully: it mentions `ServiceLookup` twice
and calls it never, both times in a comment describing what every other surface does.

**Repaired 2026-08-23, and the repair is the repayable form this finding named.**
`CatalogFallback` remembers the last listing beside the last resolve; all four faces take
it and so does the REST directory at `/rest/services`, which this sweep counted as covered
because it is a FeatureServer route and which was in fact enumerating without a fallback
like the rest. A blind listing is public-only — the faces filter by sharing and while
blind the sharing value is itself remembered, so the rule lives in the fallback where five
callers cannot each forget it — and nothing remembered is a 503 rather than an empty
document. Measured at t+45 s into an outage, twenty concurrent per face: from **0 of 20**
served instantly to 19–20 of 20 at 10–14 ms medians
([the benchmark](../../benchmarks/catalogue-outage/RESULTS.md)).

**One thing this finding could not have known**, and it is why the repair is in two parts:
the listing was not the whole cost. A WMS 1.3.0 capabilities document needs
`EX_GeographicBoundingBox` on every named layer, so it makes one projection call per
distinct spatial reference — and with the listing served from memory those calls still
waited out a connect nothing would answer, at 4.0 s each. `IProjector` is behind the same
circuit breaker now.

**The finding as written follows.**

**Not repaired, and the reason is the finding.** `CatalogFallback` exposes
`FindServiceAsync` and nothing else: it resolves one named service and cannot list. The
four faces without it all begin by *enumerating* — WFS and WMS build a capabilities
document over every published layer, OGC Features answers `/collections`, the portal
searches. **So this is not four missing call sites; there is no degraded listing
capability at all.** Wiring the resolve-one path into those four would make
`GetCapabilities` fail while `GetMap` succeeded, which is a worse answer than failing
consistently.

**[D-127](../architecture-debt.md)**, with the repayable form named: a cached listing
beside the cached resolve, with its staleness stated in the document it produces.
Recording it beats half-implementing it, and this gate is not the place to design it.

---

## What held

**The behavioural sweep found two disagreements and looked for many more.** The same
layer was asked the same question through every face that answers it — the tool
[correctness gate 2](correctness-gate-2.md) built and this sweep reused:

- **Refusal codes agree across faces.** An unknown layer is `LayerNotDefined` on WMS,
  404 on MapServer, 404 on OGC Features and *not a feature type* on WFS — different
  spellings because the protocols specify different spellings, all refusals, none
  leaking whether the name exists but is private.
- **Advertised capability matches served capability**, after correctness gate 2's fifth
  finding forced `Map,Query,Data` down to `Map`. Every WFS operation in `ows:Operation`
  answers; every OGC conformance class in `/conformance` is exercisable; every WMS
  format in `GetCapabilities` renders.
- **The two rendering faces agree pixel for pixel** where they overlap — same layer,
  same extent, same size, same stored symbology.
- **Version negotiation is consistent**: WMS 1.1.1's `SRS` and 1.3.0's `CRS` return the
  same features in each version's own axis order, and neither accepts the other's
  parameter name.
- **`GisServer:*` configuration keys and the `gisserver` default schema are still
  read**, which ADR-032 §5 requires and which four new surfaces had the opportunity to
  quietly break.

**The documentary sweep read every ADR condition touched by the four new surfaces** and
found ADR-041's six and ADR-042's five discharged with evidence in the text, and
ADR-005's conditions 1 and 2 correctly still `LIVE` and unmet — CQL2 is not shipped
([Q-132](../open-questions.md)) and `OgcNames.ConformsTo` is hand-maintained. **A
condition honestly left open is not a contradiction**, and this sweep counted them as
what they are.
