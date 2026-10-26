# ADR-215 — What ArcGIS ignores is not refused

| | |
|---|---|
| **Status** | `ACCEPTED` |
| **Confidence** | `HIGH` |
| **Decided** | 2026-10-22, overnight, under the owner's *"ben uyurken açık konuları kapatır mısın?"* — the ArcGIS veteran's second review (2026-10-21), findings N3 and N8. **The line drawn in §5 is INFERRED** and listed for confirmation. |
| **Supersedes** | — (narrows ADR-008 §2's rule; does not reverse it) |
| **Superseded by** | — |

---

## 1. Context

ADR-008 §2: a query parameter this server does not implement is refused with a reason rather than accepted and
ignored, because a client that asks for something and is answered as though it had not has been lied to. The veteran
(N3) measured where that rule costs more than it protects, against Esri's own `sampleserver6`, which answered every
one of these with features:

| Sent | Who sends it | Here before |
|---|---|---|
| `dojo.preventCache=…`, `_=…` | JS API 3.x, jQuery — cache-busters | 400 |
| `Where=1=1` | any client, another case | 400 |
| `text=`, `returnUniqueIdsOnly=false`, `timeRelation=…Overlaps`, `outTimeReference=`, `applyVCSProjection=false` | clients writing out ArcGIS's defaults | 400 |
| `lang`, `locale` | newer SDKs | 400 |

Every legacy municipal app built on Web AppBuilder or jQuery failed on its first query.

N8: the layer document never said `supportsApplyEditsWithGlobalIds`, although `applyEdits` has honoured
`useGlobalIds=true` since 2026-09-15; the JS SDK and Field Maps decide on that flag whether to send GlobalIDs.

## 2. Alternatives considered

### A — Accept what cannot change the answer; keep refusing what could *(chosen)*

Cache-busters and the caller's language name no data. A default written out asks for what the server gives anyway.
A name in another case is the same parameter. Each of those is accepted. A value that *would* change the answer —
`text=Ankara`, `returnUniqueIdsOnly=true`, another `timeRelation` — is still refused with its reason, and so is a
parameter nobody has named here (`foo=bar`).

### B — Ignore every unknown parameter, as ArcGIS does

**For.** The most compatible. **Against.** It is exactly the silent degradation ADR-008 §2 forbids: a client sending
a filter this server does not know would be answered unfiltered.

## 3. Counterarguments to the preferred option

- *"An unknown parameter is still refused, and ArcGIS ignores it; some app will still fail."* Yes, and it will fail
  with a sentence naming the parameter, which is the information needed to add it to the accepted list with a reason.
  That is ADR-008's trade and it is kept.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| Each row of §1's table is answered | Live on the fixture: `count: 8` for each; `Where=1=0` → `count: 0` (the capitalised name filters) | 2026-10-22 |
| A changing value is still refused | Live: `text=Ankara` → 400 naming it; `foo=bar` → 400 | same |
| Unit coverage | `FeatureServerQueryParametersTests`: 13 accepted, 3 still refused | Host tests, 127 of 127 |
| The flag is said | `ALayerWithGlobalIdsTests` asserts `supportsApplyEditsWithGlobalIds: true` | conformance, 864 of 869 (the five known local failures) |

## 5. Decision

Parameter names are matched in any case. `_`, `dojo.preventCache`, `lang` and `locale` are accepted and ignored, each
with its reason. `text`, `fullText` and `uniqueIds` are accepted empty, `returnUniqueIdsOnly` and `applyVCSProjection`
false, `timeRelation` at `esriTimeRelationOverlaps`, `outTimeReference` empty; another value of any of them is refused
as before. An unknown parameter is still refused. A layer document says `supportsApplyEditsWithGlobalIds: true` where
the layer has a GlobalID column and edits.

**INFERRED, for the owner:** *(Confirmed by the owner 2026-10-26 — Q-189: "189'un hepsini onayladım.")* where the line sits — that `lang`/`locale` are harmless here because every message is
English, and that `returnTrueCurves=true` stays refused (a client asking for curves gets vertices).

**Not done, and the owner's to decide: `maxRecordCountFactor`** *(Confirmed as it stands — accepted and not honoured — with the rest of Q-189, 2026-10-26.)* (N8's second half). It is accepted and the page stays
at `maxRecordCount` (ADR-197 §3). Honouring it would let a query fetch up to five pages at once, inside the
deployment's ceiling — fewer requests for a drawing client, more rows per request — and the ceiling exists because of
the owner's own concern of 2026-08-19 about many requests against a three-million-row table. That trade is not this
ADR's to make overnight.

## 6. Consequences

**Positive.** JS 3.x, jQuery and default-writing clients query. Field Maps and the JS SDK learn they may send
GlobalIDs.

**Negative.** The accepted list is longer, and each entry is a claim that ignoring it loses nothing; a false entry is
the degradation ADR-008 forbids (the list's own remarks say so).

**State.** None: request parsing and a document flag.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | No layer here has a vertical coordinate system or a time zone of its own, so the defaults of `applyVCSProjection` and `outTimeReference` are this server's only answers. | Holds today |

## 8. Dependencies

**Depends on:** ADR-008 §2, ADR-197 §3.

## 9. Revisit triggers

- A layer gains a time zone or a vertical reference: `outTimeReference` and `applyVCSProjection` stop being defaults
  this server always meets.
- The owner decides `maxRecordCountFactor`.

## 10. Dissent

The veteran would ignore unknown parameters outright; ADR-008's rule is kept against that.
