# WFS filter review 1 — break the read-only surface

**Run 2026-08-20.** Scope: the WFS 2.0 read-only surface added on 2026-08-19,
answering anonymous callers at `/wfs`. Files in scope: `FilterReader`,
`GmlGeometryReader`, `ValueReference`, `SafeXml`, `WfsXmlRequest`, `WfsRequest`,
`PredicateSql`, `AttributePredicate`, and the host's `WfsEndpoints`
(`TryQuery`, `TryIdentity`, `TryFind`, `VisibleAsync`).

**Adversarial, and against the running server.** Every claim below was tried
against the live dev server (`https://127.0.0.1:8443`, schema `gisserver_look`),
not read off the code. The method is [injection-sweep-1](injection-sweep-1.md)'s:
the code's account of itself is a claim, not an answer — D-41 shipped because a
comment said a parameter was parsed and it was not. Where a bound is *written
down*, the test was whether it *holds*, not whether it exists.

The surface is defended well where it says it is. **One request, ~223 KB,
crashes the whole process** — and it does so precisely where the design's own
coverage claim has a hole.

---

## Result

**One critical finding, two minor. The injection story holds; the availability
story does not.**

| # | Severity | What | Reproduced |
|---|---|---|---|
| **F-1** | **Critical** | An anonymous POST with a nested GML geometry collection overflows the stack and terminates the server. No depth guard covers `GmlGeometryReader`. | **Yes — server taken down twice, restarted twice** |
| F-2 | Low | The XML POST binder lets a `request` **attribute** override the operation the root element name denotes. KVP has no such override; the two encodings disagree. | Yes |
| F-3 | Low | The capabilities abstract and a binder error both state *"GetPropertyValue is not implemented"*, while the operation is advertised in `OperationsMetadata` and works. A document contradicts itself. | Yes |

---

## F-1 — A nested geometry collection is unbounded recursion, and it is not behind any depth guard

**Severity: critical. Remote, unauthenticated, single request, whole-process
kill. This is the exact failure mode `SafeXml` and `PredicateSql` say they
prevent, arriving through the one recursive walker that counts nothing.**

### The claim under test

Two files state that recursive descent over caller XML is bounded so a deep
document cannot overflow a stack that .NET cannot catch:

- [SafeXml.cs:35](../../src/Graticula.Api.Wfs/SafeXml.cs#L35) — *"Nesting depth is
  not a setting at all and is counted by the reader that walks the tree — see
  `FilterReader`."*
- [PredicateSql.cs:47](../../src/Graticula.Core/Features/PredicateSql.cs#L47) —
  its own `MaximumDepth = 32`, *"because emitting is recursive and a deep enough
  tree would exhaust the stack, which cannot be caught."*
- [FilterReader.cs:132](../../src/Graticula.Api.Wfs/FilterReader.cs#L132) — the
  guard those sentences point at: `if (depth > SafeXml.MaximumDepth)` refuses at
  33 levels of `And`/`Or`/`Not`.

The guard is real and it works — a validly nested attribute filter is refused at
33 (checked). But the sentence *"the reader that walks the tree"* names one
reader and there are two. **`GmlGeometryReader` walks a tree recursively and
counts nothing.**

### The hole

A spatial predicate hands its geometry to
[FilterReader.cs:562](../../src/Graticula.Api.Wfs/FilterReader.cs#L562)
(`GmlGeometryReader.TryRead`) with **no depth argument** — the `depth` that
`FilterReader` was tracking is dropped at the boundary. Inside,
[GmlGeometryReader.cs:356](../../src/Graticula.Api.Wfs/GmlGeometryReader.cs#L356)
`TryCollection` reads the members of a `MultiSurface`/`MultiCurve`/`MultiPoint`
and calls [TryShape](../../src/Graticula.Api.Wfs/GmlGeometryReader.cs#L380) on
each — and `TryShape` routes a nested collection straight back into
`TryCollection`. There is no depth parameter anywhere in the file. The recursion
is bounded only by the document-size limits, and those are far too generous to
stop it.

A `<gml:MultiSurface>` whose `<gml:surfaceMember>` holds another
`<gml:MultiSurface>`, repeated, recurses once per level. The innermost element's
validity is irrelevant: the descent happens on the way *down*, before anything
is validated — at 1 000 levels the request completed the descent and only then
failed the cast (*"a member of the wrong kind"*), proving the recursion, not a
parse error, is what runs.

### Reproduction

POST to `/wfs`, anonymous, `Content-Type: application/xml`:

```xml
<wfs:GetFeature service="WFS" version="2.0.0"
    xmlns:wfs="http://www.opengis.net/wfs/2.0"
    xmlns:fes="http://www.opengis.net/fes/2.0"
    xmlns:gml="http://www.opengis.net/gml/3.2">
  <wfs:Query typeNames="graticula:tr_il">
    <fes:Filter><fes:Intersects>
      <fes:ValueReference>geom</fes:ValueReference>
      <!-- <gml:MultiSurface><gml:surfaceMember> repeated N times, one Polygon, then the closers -->
    </fes:Intersects></fes:Filter>
  </wfs:Query>
</wfs:GetFeature>
```

| N (nesting) | Body size | Result |
|---|---|---|
| 1 000 | 76 KB | `HTTP 400`, *"a member of the wrong kind"* — recursion completed, server **up** |
| 3 000 | **223 KB** | connection reset (curl 56), port no longer listening — server **DOWN** |
| 6 000 | 446 KB | connection reset — server **DOWN** |

Both crashes required `dev-server.sh start` to recover; the process was gone, not
merely erroring. That is the signature of `StackOverflowException`, which since
.NET 2.0 is uncatchable and terminates the process by design — so the `try/catch`
around `XElement.Load` and every `out WfsFault?` path in the file cannot save it.

### Why the size limits do not save it

- **The 1 MB character limit** ([SafeXml.cs:50](../../src/Graticula.Api.Wfs/SafeXml.cs#L50))
  and **4 MB byte limit**
  ([SafeXml.cs:68](../../src/Graticula.Api.Wfs/SafeXml.cs#L68)) both hold — a
  1.9 MB body is refused by the char limit, a 9.5 MB body by the byte limit
  (both checked). But the crash needs only ~223 KB. The limits bound the
  *document*; they do not bound the *recursion depth within it*, and ~76 bytes of
  nesting buys one stack frame.
- **The `FilterReader` depth guard** never runs: the filter here is one level
  deep (`Intersects`), and the geometry is where the depth lives.
- **GET cannot reach it** — a 2 000-deep filter on the query string is rejected by
  Kestrel's request-line limit before the handler runs (checked, connection
  reset). The vector is POST only, which is exactly the path a real client uses
  for a real filter (`WfsXmlRequest` exists for this reason), so it is not exotic.

### Fix direction (not applied)

`GmlGeometryReader` needs the same guard the other two walkers have: thread a
depth through `TryShape`/`TryCollection` and refuse past `SafeXml.MaximumDepth`,
or have `FilterReader` pass its current `depth` across the boundary at
line 562 so one counter spans both trees. The `SafeXml` comment at line 35 should
stop claiming a single reader counts depth while a second reader does not.

---

## F-2 — A `request` attribute in the XML POST overrides the operation the element name denotes

**Severity: low. No privilege boundary is crossed — every WFS operation here is
equally anonymous — but the two encodings disagree, which Attack 6 says they must
not.**

[WfsXmlRequest.cs:68](../../src/Graticula.Api.Wfs/WfsXmlRequest.cs#L68) sets the
operation from the root element name, then
[WfsXmlRequest.cs:70-75](../../src/Graticula.Api.Wfs/WfsXmlRequest.cs#L70) copies
*every* non-namespace root attribute into the same case-insensitive dictionary.
An attribute literally named `request` therefore overwrites the element-derived
value.

Reproduced — a `GetFeature` element returns a capabilities document:

```
POST <wfs:GetFeature service="WFS" version="2.0.0" request="GetCapabilities" …>
  → <wfs:WFS_Capabilities …>          (dispatched as GetCapabilities)
```

For an XML-encoded WFS request the operation *is* the root element name; there is
no `request` attribute in the grammar, and KVP cannot express "element says X,
parameter says Y". So this is a genuine divergence between the encodings and a
spec deviation. It is low severity only because the override cannot reach an
operation a KVP caller could not already call, and all of them run through
`VisibleAsync`. It is still worth closing: the attribute loop should not be
allowed to redefine `request` (and arguably `service`/`version` deserve the same
treatment, since a stray attribute silently redefining a bound parameter is the
shape of a future bug).

---

## F-3 — The capabilities document contradicts itself about GetPropertyValue

**Severity: low. A document served to clients states a fact that its own other
half denies.**

- [CapabilitiesDocument.cs:152](../../src/Graticula.Api.Wfs/CapabilitiesDocument.cs#L152)
  — the ServiceIdentification abstract: *"…LockFeature and GetPropertyValue are
  not implemented."*
- [WfsRequest.cs:114](../../src/Graticula.Api.Wfs/WfsRequest.cs#L114) — the
  unknown-operation error repeats it.
- [CapabilitiesDocument.cs:203](../../src/Graticula.Api.Wfs/CapabilitiesDocument.cs#L203)
  — the same document advertises `GetPropertyValue` in `OperationsMetadata`.

It is implemented and it works — reproduced:

```
GET /wfs?service=WFS&version=2.0.0&request=GetPropertyValue
        &typeNames=graticula:tr_il&valueReference=il&count=2
  → <wfs:ValueCollection … numberMatched="5433" numberReturned="2">
      <wfs:member><graticula:il>Nevşehir</graticula:il></wfs:member> …
```

A client that reads the abstract to decide what the server can do is told the
opposite of what the operation list says and of what the server actually does.
No security impact — `GetPropertyValue` is governed by the same `VisibleAsync`
filtering as `GetFeature` — but it is the "code's account of itself is wrong"
pattern this repository has been bitten by before, so it is recorded rather than
left. The abstract and the error text should be corrected to match the
implemented surface.

---

## What was checked and found sound

Recorded because a review that lists only failures reads as an audit of a broken
system, and because the coverage claim rests on exactly these having been *tried*
rather than *read*.

### 1 — Caller text into SQL (the central claim)

Every attempt below reached the database as a bound parameter, a
layer-matched-then-quoted identifier, or was refused by name. Nothing a caller
wrote arrived as statement text.

| Vector tried | Payload | Observed |
|---|---|---|
| `Literal` value | `Ankara' OR '1'='1` on `il` | `numberMatched="0"` — the whole string was one bound literal, not a clause |
| `ValueReference` | `il = 1 OR 1=1 --` | Refused: *"an XPath expression this server does not evaluate"* |
| `sortBy` | `il; drop table x` | Refused: *"not a property of this feature type"* |
| `propertyName` | `il,(select version())` | Refused: *"not a property of this feature type"* |
| `bbox` | `0,0,1,1);DROP` | Refused: *"not a number"* |
| `resourceId` (int id col) | `tr_il.1 OR 1=1` | Refused: *"identity column 'objectid' holds whole numbers"* |
| `count` | `1;DROP` | Refused: *"not a whole number"* |
| `srsName` | `EPSG:4326);--` | Refused: *"not a coordinate reference"* |
| `STOREDQUERY_ID` id | `tr_il.1' OR '1'='1` | Refused at the integer parse of the split identity |
| `PropertyIsLike` pattern | `An*` and metacharacters | Applied as a **bound** parameter; wildcards translated in `SqlPattern`, `%`/`_` escaped |

The structural reason it holds: identifiers are matched against the layer's real
column list and the *matched* name is what is quoted
([PredicateSql.Resolve](../../src/Graticula.Core/Features/PredicateSql.cs#L273),
[FilterReader.TryProperty](../../src/Graticula.Api.Wfs/FilterReader.cs#L660)),
operators come from a fixed enum table, and every literal goes through
[`Bind`](../../src/Graticula.Core/Features/PredicateSql.cs#L299) as `@w{n}`. The
re-match in `PredicateSql` is independent of the front end's match, so the WFS
front end cannot open an injection by forgetting a step it does not perform.

### 2 — Partial application of a filter (the "silent degradation" claim)

Every filter that the query model cannot hold as one spatial + one predicate +
identities was refused whole, never half-applied. All reproduced live and all
returned `OperationNotSupported`, none returned rows:

| Filter | Result |
|---|---|
| `Or` across a spatial and an attribute test | Refused |
| `Not` around a spatial predicate | Refused |
| Two spatial predicates under one `And` | Refused |
| `bbox` parameter **and** a spatial predicate in the filter | Refused |
| `matchCase="false"` on a comparison | Refused (no case-insensitive path exists) |
| An unknown `fes:` element (`PropertyIsFancy`) nested in a valid `And` | Refused, not dropped |

An `Or`/`And`/`Not` requires all children to parse; any unreadable child fails
the whole part ([FilterReader.cs:252](../../src/Graticula.Api.Wfs/FilterReader.cs#L252)),
so a predicate cannot be silently narrowed.

### 3 — Input bounds

| Bound | Test | Held |
|---|---|---|
| DTD prohibited | `<!DOCTYPE …>` in a KVP filter **and** in a POST body | Refused both ways: *"DTD is prohibited"* |
| Billion laughs | Nested-entity DOCTYPE in a POST body | Refused at the DTD, before any expansion |
| Document char limit (1 MB) | 1.9 MB POST body | Refused: *"the input document has exceeded a limit"* |
| Request byte limit (4 MB) | 9.5 MB POST body | Refused: *"larger than 4194304 bytes"* — before the char limit, as designed |
| Attribute-filter nesting (32) | 33-deep `And` | Refused — the guard that F-1's geometry path lacks |
| GET request-line | 2 000-deep filter on the query string | Rejected by Kestrel before the handler |

The one bound that is claimed and does **not** hold as a class is recursion
depth — see F-1. The `SafeXml` settings themselves are demonstrably applied
(DTD prohibition and the char/byte limits all fired live), so this is not the
D-41 shape of a setting that was never wired up; it is a second recursive reader
the depth story forgot.

### 4 — File read / SSRF

- **XXE blocked.** A POST body with `<!ENTITY xxe SYSTEM "file:///c:/temp/…">`
  is refused at the DTD prohibition before the entity is defined; `XmlResolver`
  is null as a second line. No file content returned.
- **No outbound-fetch vector found.** `resolve=remote` is refused
  ([WfsRequest.cs](../../src/Graticula.Api.Wfs/WfsRequest.cs)), `srsName`/CRS
  values are parsed to an integer and never dereferenced, the `NAMESPACES`
  parameter is stored in a dictionary and never fetched, and `schemaLocation` is
  written by the server, not read from the caller. Nothing in the surface
  dereferences a caller-supplied URI.

### 5 — Two encodings disagree (Attack 6)

The same attribute filter as KVP and as XML POST returned the identical count
(`numberMatched="248"` both ways). The divergence found is F-2 (a `request`
attribute overriding the element), which is a defect in the reduction, not in the
shared `WfsRequest.TryParse` the two encodings share downstream.

---

## What I could not fully test

**Anonymous vs authenticated indistinguishability (Attack 5) was verified from
the code and partially live, not end-to-end.** I did not have credentials, and
credential-guessing against `/rest/auth/login` was correctly refused by the
environment, so I could not stand up an authenticated principal to diff against
an anonymous one over a layer that *exists but is hidden*.

What I could establish:

- `TryFind` returns a single templated fault —
  [WfsEndpoints.cs:1064](../../src/Graticula.Host/WfsEndpoints.cs#L1064), *"is not
  a feature type on this server"* — for both an absent type and a type filtered
  out by sharing, differing only in the echoed name. Reproduced for
  non-existent names; the message shape does not encode existence.
- `VisibleAsync`
  ([WfsEndpoints.cs:222](../../src/Graticula.Host/WfsEndpoints.cs#L222)) filters
  the layer set *before* any database round trip, and `TryFind` runs over that
  filtered set, so a hidden layer is never opened — there is no obvious
  describe-cost timing tell, because the describe only happens for a layer that
  survived the filter.

The residual risk I could not close live: whether a hidden-but-real layer and a
truly-absent one are indistinguishable in **timing** under load, and whether any
error text downstream of `TryFind` (e.g. a `propertyName` refusal) could be
reached for a hidden layer and leak its schema. Both look closed by construction,
but "looks closed by construction" is what this method exists to distrust, and a
proper test needs an authenticated session to build the hidden-layer case. That
is the one gap in this pass.

---

## Note on conduct

F-1 was reproduced by taking the running dev server down twice; each time it was
restarted with `dev-server.sh start` and confirmed healthy (`GetCapabilities`
`HTTP 200`) before continuing. The server is up at the end of this run. No data
was modified — the surface is read-only and every crash was a process
termination, not a write.
