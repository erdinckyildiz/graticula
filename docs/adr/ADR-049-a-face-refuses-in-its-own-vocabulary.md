# ADR-049 — A face refuses in its own vocabulary, even when that vocabulary has no word for it

| | |
|---|---|
| **Status** | `ACCEPTED` |
| **Confidence** | `HIGH` for refusing at all · `MEDIUM` for which exception code |
| **Decided** | 2026-09-02 |
| **Answers** | the remaining half of [D-180](../architecture-debt.md) |
| **Supersedes** | — |
| **Superseded by** | — |

---

## 1. Context

[ADR-031](ADR-031-service-capability-configuration.md) §2a keeps `Query` revocable on
purpose. A service with `Query` unchecked **is running and refusing**, which §2a
distinguishes from *stopped* so that an operator diagnosing an incident can tell the two
apart. The setting exists for one operation: somebody turns reading off to stop a service
answering, without taking it down.

[D-180](../architecture-debt.md) measured what that setting actually does, and the answer
was *nothing on two of the four faces*. Measured again on 2026-09-02 against a running
server with `ci_buildings` configured to offer `Create` only:

| Face | Answer |
|---|---|
| ArcGIS `FeatureServer/0/query` | **403**, naming the setting |
| OGC API Features `items` | **403**, naming the setting |
| WFS 2.0 `GetFeature` | **200**, a `wfs:FeatureCollection` with rows |
| WMS 1.3.0 `GetMap` | **200**, a 293-byte PNG |

The asymmetry is not cosmetic. It is reached for in the situation where being wrong costs
most — an incident, with an operator believing a service has stopped answering — and it is
now the *only* inconsistency of its kind: editing refuses on every face and reading refuses
on half of them, on the same setting, on the same service.

D-180 recorded why the repair stopped there rather than pretending it was finished:
**neither WFS nor WMS has an exception code that means *configured off*.** Both faces have
closed vocabularies — WMS 1.3.0 Table E.1 and OWS Common's exception codes — and neither
enumerates this case. That is a decision about how to be wrong, and this ADR is where it is
taken rather than improvised in an endpoint.

## 2. Alternatives considered

### Alternative A — Omit the layer from the capabilities document

Do not list a type or layer whose service will not answer for it. There is then nothing to
refuse, and every client behaves correctly without a new code.

**Argument for.** It is the only option that needs no exception at all, and it is what a
client's own model already handles: a layer it cannot see is a layer it does not ask for.
It is also what several servers do.

**Argument against.** It makes *refusing* indistinguishable from *absent*, which is exactly
the distinction ADR-031 §2a exists to preserve — and the operator who turned `Query` off is
the person most harmed, because the service they are diagnosing vanishes from the document
they would check. It also contradicts what the other two faces already do: the ArcGIS layer
document and the OGC collection description both still answer, so the same service would
describe itself on two faces and hide on two others.

### Alternative B — Answer an exception code outside the enumerated set

Invent `CapabilityRefused` and put it in the `code` attribute. It says exactly what
happened.

**Argument for.** Precise, self-describing, and machine-readable by anyone who reads our
documentation.

**Argument against.** Both schemas constrain the code. WMS 1.3.0's
`exceptions_1_3_0.xsd` and OWS Common's `ExceptionReport` both define the code as an
enumeration in practice, and a conforming client is entitled to reject or mishandle a value
outside it — which turns a clear refusal into a parse error, the one outcome worse than the
bug being fixed. Being unique to us is also the shape [CLAUDE.md](../../CLAUDE.md) §5
warns about: an extension that only our clients understand, on a face whose whole purpose
is that other people's clients work.

### Alternative C — The nearest enumerated code, with the sentence carrying the truth

Use an existing code that is close, put the real explanation in the exception text, and name
the parameter that carried the layer in `locator`.

**Argument for.** Every conforming client can parse it; every human reading it gets the
actual reason, because the text is where these faces have always put reasons. The code is
what a machine branches on and *refused* is the branch either way.

**Argument against.** The code is not accurate. `OperationNotSupported` says the operation
is unsupported, and `GetMap` is supported — it is this layer that is not being drawn. A
client that logs only the code will record something slightly false.

### Alternative D — Leave it, and document that the setting is partial

Say in ADR-031 that `Query` governs ArcGIS and OGC API Features only.

**Argument for.** Honest, free, and no client sees a new behaviour.

**Argument against.** It makes the documentation describe an accident. Nothing about `Query`
is per-face; an operator reading *this service is configured to offer Create* has no reason
to believe two of four faces ignore it. And the failure it leaves in place is the one the
setting was created to prevent.

## 3. Counterarguments to the preferred option

**The code we are about to send is not true, and we know it.** `OperationNotSupported` in
WMS 1.3.0 Table E.1 is glossed as *"Request is for an optional operation that is not
supported by the server"*, and `GetMap` is neither optional nor unsupported. A client that
logs the code and discards the text records something misleading. This is a real cost and it
is not fully mitigated by the sentence being right.

**A different reading of `LayerNotDefined` would be defensible.** One could argue that a
service which will not answer for a layer has not, in any useful sense, defined it. The
reason to reject that is not that it is unarguable but that it is *inconsistent with what
this server does next*: `GetCapabilities` continues to list the layer, so a client told
*not defined* can immediately read a document defining it.

**200 for the WMS refusal will look wrong to anybody reading access logs.** A refusal that
carries a success status is invisible to a monitor, and this is the face where an operator is
most likely to be watching status codes. The counterargument is inside this repository
already — `WmsEndpoints.RefuseAsync` records that several WMS clients treat a 4xx as a
transport failure and never read the body — but "we already decided it" is not the same as
"it is right", and if that reasoning is ever revised this decision goes with it.

**Four faces now share one predicate, and shared code drifts differently from copied code.**
`CapabilityCeilings` makes every face agree by construction, which is the intent, but it also
means one change alters four surfaces at once. That is the trade [D-46](../architecture-debt.md)
asks for and it is not free.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| WFS `GetFeature` ignored the ceiling | `GetFeature&typenames=ci_buildings` with ceiling `{Create}` answered **200**, 1,279 bytes of `wfs:FeatureCollection` | measured 2026-09-02, running server |
| WMS `GetMap` ignored the ceiling | same service, `GetMap` answered **200**, `image/png`, 293 bytes | measured 2026-09-02 |
| The other two faces did refuse | ArcGIS `query` on the same service answered **403** with *is configured to offer Create, so Query is refused here* | measured 2026-09-02 |
| `OperationNotSupported` is enumerated on both faces | WMS 1.3.0 Table E.1; OWS Common 1.1 §8.3 Table 26 | public specifications |
| `LayerNotQueryable` is scoped to `GetFeatureInfo` | WMS 1.3.0 Table E.1: *"GetFeatureInfo request is applied to a Layer which is not declared queryable"* | public specification |
| The refusal now reaches all four faces | after the change, WFS answers **403** `OperationNotSupported`, WMS `GetMap` answers a `ServiceException` `OperationNotSupported`, `GetFeatureInfo` answers `LayerNotQueryable` | measured 2026-09-02, and a conformance suite asserts it |

## 5. Decision

**A face refuses a configured-off capability in its own vocabulary, using the nearest
enumerated code, with the reason in the exception text and the layer parameter in
`locator`** — Alternative C. Concretely: WFS 2.0 `GetFeature` answers an
`ows:ExceptionReport` with `exceptionCode="OperationNotSupported"`, `locator="TYPENAMES"`
and HTTP **403**; WMS `GetMap` answers a `ServiceException` with
`code="OperationNotSupported"`, `locator="LAYERS"` and HTTP **200**, which is this face's
existing rule for exceptions rather than a new one; WMS `GetFeatureInfo` answers
`code="LayerNotQueryable"`, which is the code WMS wrote for exactly this case. **The layer
stays in `GetCapabilities` on both faces**, and `DescribeFeatureType`, the OGC collection
description, the ArcGIS layer document and `GetLegendGraphic` all keep answering: describing
what a service offers is not reading its features, and a client that cannot discover a layer
cannot be told why it is refused. **The predicate and the sentence live in one place**,
`CapabilityCeilings`, which all four faces read; only the envelope differs.

## 6. Consequences

**Positive.** The setting ADR-031 §2a created now does what it says on every face that
reads data. An operator who turns `Query` off gets one behaviour, not two, and the sentence
they see names the setting rather than blaming the caller. The predicate exists once, so the
next face cannot quietly disagree with the other four.

**Negative.** Two of the four faces now send a code that is close rather than exact, and a
client that logs only codes will record `OperationNotSupported` for something that is not
quite that. The WMS refusal carries HTTP 200, so it is invisible to a monitor watching status
codes — the sentence is in the body and only a WMS-aware reader will find it. And a client
that previously received a picture now receives an exception document with the same
`image/png` request; that is the intended change, but it is a behaviour change on a
standards face, and any deployment that had `Query` unchecked while relying on WMS was
relying on the bug.

**State.** None. This decision stores nothing: it reads the ceiling ADR-031 already keeps
on the service row, holds nothing at runtime, and is node-local only in the sense that
every node reads the same catalogue row and reaches the same answer.

**Ports created.** None. No dependency is adopted.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | Conforming WMS and WFS clients branch on the exception code and show the text | Untested against third-party clients; the text is written so a human reading it is not misled either way |

## 8. Dependencies

**Depends on**: [ADR-031](ADR-031-service-capability-configuration.md) (the setting and its
*running and refusing* state), [ADR-018](ADR-018-authorization-and-roles.md) (a turned-off
*face* is a different setting and hides the layer, which is why this one must not).

**Depended on by**: [ADR-042](ADR-042-ogc-api-features.md) and the ArcGIS face already refuse
in this shape; a fifth read face would follow this ADR rather than inventing a fifth answer.

## 9. Revisit triggers

- OGC adds an exception code meaning *configured off* to either vocabulary.
- A conforming client is measured mishandling `OperationNotSupported` from `GetMap` in a way
  that is worse than the picture it used to get.
- `WmsEndpoints.RefuseAsync`'s 200-rather-than-4xx rule is revised, which would take the WMS
  half of this decision with it.

## 10. Dissent

**Answering 200 with a refusal is the weakest part of this, and it is inherited rather than
chosen.** The argument for it — clients discard 4xx bodies — is about clients we have not
measured, and it makes a deliberate operator action invisible to every monitor that watches
status codes. The reason it stands is that changing it belongs to a decision about the whole
WMS face rather than to this one, not that the objection is answered.

**And the honest summary of the code choice is that all four options were bad.** This one was
picked because its failure mode is a slightly inaccurate log line, while the others' are a
vanished service, a parse error, or the bug.
