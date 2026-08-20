# Failure gate 2 — the liveness probe that would have caused the restart it exists to prevent

**Run 2026-08-20** by an independent reviewer that did not write the code, per §67.
Scope: [failure-gate-1](failure-gate-1.md)'s scenarios re-executed against all seven
protocol faces — four of which had never been through this gate — plus two it did not
attempt: a client that hangs up mid-stream, and the *latency* of the health probes
rather than only their status.

**Every row below is an executed request and response.** Where a cause is inferred from
source rather than observed, it says so.

**One deployment fact bounds everything here.** The platform store and the layers'
datastore are the same PostgreSQL instance, so stopping it removes both. Findings about
*metadata* documents and *cached* tiles during an outage are clean; findings about
*data* paths cannot separate *the catalogue is down* from *the data is down*, and are
marked where that matters.

## Result

**FAIL. Ten findings. Six repaired the same day, four recorded.**

**What it turned on, in order:**

1. **F1** — a liveness probe that costs four seconds during a database outage. An
   orchestrator with default settings restarts the container about thirty seconds in,
   the restart does not reach the database, and it empties the memory that degraded
   serving runs on. **The outage would remove its own mitigation.**
2. **F5** — one malformed query parameter produced HTTP 200 and a well-formed WFS
   document asserting a thousand features it did not contain.
3. **F2 and F3 together** — ADR-026 answers Q-95 as a property of the server. Measured,
   it is a property of three faces of seven, for metadata and cached tiles only, for
   about thirty seconds of an advertised fifteen minutes.

---

## Summary

| Scenario | Promised | Observed | Verdict |
|---|---|---|---|
| 1. Platform store unavailable | ADR-026: a resolved service stays servable | 3 faces of 7, metadata and cached tiles only, ~30 s | **FAIL** — D-127 confirmed in behaviour, and narrower than recorded |
| 1a. Recovery | not stated | Every face 200 within **0.7 s**, readiness 0.9–4.1 s, no restart, after outages up to 17 minutes | **PASS** |
| 1b. Readiness during outage and recovery | must not be green while serving is red | 0 of ~450 samples across three cycles | **PASS** — gate 1's fix holds |
| 1c. Liveness during the outage | the process must not be killed | 200 throughout, at **4.01 s** per probe | **FAIL → FIXED** |
| 2. Cache lookup during an outage | N2: an L3 lookup should not need the index | A warm tile serves for ~30 s, then 503 | **PARTLY BUILT** — an improvement on gate 1 |
| 6. Cache storage unusable | ADR-010 §3 | Not run — see below | — |
| 9. Huge or malformed input | refused with a reason | 51 of 57 named exactly what was wrong. Three defects | **FAIL → FIXED** |
| Client hangs up mid-stream | logged, connection released | Released cleanly. **Nothing logged at all** on WFS, WMS, OGC | **FAIL → part fixed** |

---

## F1. Every request blocked four seconds in authentication, including the liveness probe — HIGH, repaired

`/healthz/live` returns a constant. With the store stopped:

```
### baseline, store UP
  live 200 17ms / 5ms / 5ms
### STOPPED
t+  0.0s  live=200       7ms   ready=503      19ms
t+ 15.1s  live=200    4023ms   ready=503    8043ms
t+ 30.2s  live=200    4028ms   ready=503    8038ms
t+ 45.2s  live=200    4012ms   ready=503    8029ms
```

Six of six samples at 4.01–4.03 s, plus four more at fifteen minutes into a separate
outage. **Reproduced independently before repair**: 22 ms healthy, then 4.027 s, 4.028 s
and 4.034 s from eight seconds into the outage. The delay appears once the Npgsql pool
has drained its already-broken connectors — at t+0 the probe still cost 7 ms.

**The cause, and it is the shape of this finding rather than the number.**
`Authentication.ResolveAsync` sits in the pipeline ahead of every route and calls
`GrantsOfAsync` unconditionally — including for an anonymous caller with no session,
which is exactly what a liveness probe is. That is deliberate: ADR-015 §2a made
anonymous a real principal *so that this path has no special case*. Its
`catch (NpgsqlException)` yields anonymous, so the request still succeeds. It just pays
a full connect attempt first.

**And it defeated a fix written for this exact failure.** `/healthz/live` was made to
answer a constant, with a comment that reads *turning an outage into a restart loop, and
destroying the running process that was the only thing still able to answer questions*.
The endpoint was correct and nobody looked at what ran in front of it. **Kubernetes'
default probe `timeoutSeconds` is 1**, so a 4-second probe fails every attempt and the
kubelet restarts the container about thirty seconds into a database outage. The restart
does not reach the database, and it empties `CatalogFallback` — which is the degraded
serving ADR-026 exists to provide. **The outage removes its own mitigation.**

Gate 1 recorded 1c as PASS on the strength of the status code. The status code was never
the thing an orchestrator acts on alone.

**Repaired.** The middleware answers the liveness path with the anonymous principal
directly, without asking the store — which is the principal an unreachable store
produces anyway, so the probe answers identically in both directions and simply does not
wait to find out. The path is a named constant now, because three guards and the route
itself were four separate string literals and every exemption was only as good as
somebody spelling it the same way. **Verified under a fresh outage: 10–22 ms
throughout**, with readiness still correctly 503.

**What is not repaired is the second half.** Data paths still cost 8–12 s per refusal
during an outage rather than failing fast, because the same connect attempt happens
twice — once in authentication and once in the endpoint. Early in the outage the same
requests refuse in 21–30 ms, so the fast path exists; it is only reached while a broken
connector is still in the pool. Under any real request rate that is a queue collapse
rather than a degradation. [D-131](../architecture-debt.md).

## F5. A bad `srsName` returned a successful document claiming a thousand features it did not have — HIGH, repaired

```
GET /wfs?…&REQUEST=GetFeature&TYPENAMES=tr_il&SRSNAME=urn:ogc:def:crs:EPSG::999999
HTTP/1.1 200 OK    Transfer-Encoding: chunked    545 bytes    curl exit 18

<wfs:FeatureCollection … numberMatched="5433" numberReturned="1000" />
```

Three things wrong at once, and the third is the bad one: the status is **200**; the body
is **well-formed XML**, because `XmlWriter` closed the open element on its way out; and it
asserts `numberReturned="1000"` while containing **zero** features. A client that
tolerates a truncated chunked stream — many do — receives a valid, complete,
successful-looking WFS response that says it returned a thousand features and returned
none. **Silent data loss presented as success, from one query parameter.**

The parser had checked the spelling, which is all a parser can check:
`urn:ogc:def:crs:EPSG::999999` is well-formed and names nothing. The transform then
failed on the first row, after the header. Nothing caught it — the log shows *the
response has already started, the error handler will not be executed*, then an unhandled
`XX000: Invalid reserved SRID` reaching Kestrel.

**Repaired, before a byte is written.** `IProjector` gained `KnowsAsync`, a cached
one-row lookup against `spatial_ref_sys`, and WFS asks it when the request names a system
other than the layer's own. **An unreachable store answers *yes*** — this exists to turn a
caller's mistake into a refusal, and it must never turn a database outage into a 400 that
blames the caller for it.

OGC API Features never had this bug because it validates against the collection's
advertised list. WFS cannot do the same: it advertises one `DefaultCRS` per feature type
and no `OtherCRS`, while usefully serving any system PostGIS knows. So it asks the weaker
and truer question — is this a system this deployment can project into at all.

```
→ HTTP 400  <ows:ExceptionReport><ows:Exception exceptionCode="InvalidParameterValue"
   locator="srsName">EPSG:999999 is not a coordinate reference system this deployment
   can project into. It is spelled correctly and the projection database does not have it.
```

## F4. A caller's bad SRID was reported as a database outage — MEDIUM-HIGH, repaired

With the database up, healthy, and answering everything else, four surfaces answered:

> *A database this server depends on is unreachable. Check /healthz/ready and
> /admin/health…*

PostGIS raises `XX000` — its catch-all class — for `Invalid reserved SRID`.
`ErrorResponse.Classify` handles `57014`, `42P01`, `42883`, `42703`, `42501` and `28P01`
individually and `XX000` fell through to the general Npgsql branch. **This is the third
time this file has been corrected for the same mistake** — its own comments record 42883
and 42703 as *a schema problem wearing a connectivity costume*, and a timeout as arriving
*wearing the connectivity costume*. A caller's parameter now wore it too.

Beyond the misdiagnosis: 503 tells a client to retry, and retrying a bad parameter never
works.

**OGC API Features was the one face that got this right**, because it validates the CRS
against the collection's list before the database sees it.

**Repaired.** `XX000` whose message names an SRID is classified 400, with a sentence that
says the server is healthy and names the parameters worth checking. Matching on the
message is unusual here and is the honest option: `XX000` covers unrelated genuine
faults, so the code alone cannot decide, and anything else in it keeps the conservative
answer. **Verified on WMS, WFS, FeatureServer and MapServer: 400 on all four.**

## F6. Seven protocols, one error envelope — MEDIUM, repaired

During the outage, and for F4's bad CRS, WMS, WFS and OGC API Features all answered with
ArcGIS REST JSON:

```
content-type: application/json; charset=utf-8
{"error":{"code":503,"message":"A database this server depends on is unreachable…"}}
```

A WMS 1.3.0 client expects `ServiceExceptionReport`; a WFS 2.0 client expects
`ows:ExceptionReport`; an OGC API Features client expects `application/problem+json`.
`ErrorResponse.WriteAsync` is one host-level handler with no notion of which face it is
answering for.

**Worth separating from the message content, which is good.** The *handled* paths on
these faces get their envelopes right and get them right well. It is only the fallback —
which is what a client meets during an outage, which is exactly when its error handling
is being exercised hardest.

**Repaired.** The fallback chooses the envelope from the path and leaves the sentence
alone, because rewriting the message per protocol would be seven places for it to drift.
**Verified under a fresh outage:**

```
/wms   → 200 text/xml                <ServiceExceptionReport version="1.3.0" …
/wfs   → 503 text/xml                <ows:ExceptionReport version="2.0.0" …
/ogc   → 503 application/problem+json {"type":"about:blank","status":503, …
/rest  → 503 application/json         {"error":{"code":503, …
```

## F2. D-127 is observable from outside, exactly as it predicted — HIGH, not repaired

The open question was whether the difference is visible in behaviour or only in call
counts. Same service, same instant, catalogue memory fresh:

```
### STOPPED
t+2.0s  FS-svcdoc=200(age0)  MS-svcdoc=200(age0)  MS-legend=200(age0)
        VT-style=200(age0)   VT-warmtile=200(age0)
        WMS-caps=503  WMS-getmap=503  WFS-caps=503  WFS-getfeat=503
        OGC-collections=503  OGC-items=503  Portal-search=503
```

`tr_ref` is published on all five faces and answers 200 on all five when healthy. During
the outage its MapServer legend serves and its WMS `GetMap` refuses. A warm `tr_il` tile
serves off VectorTileServer and the same data refuses on WMS. The `X-Catalog-Age` header
is present on exactly the responses that degraded and absent everywhere else, which is
the server naming the mechanism itself.

**A second gap D-127 does not record.** The three covered faces degrade only for
**metadata documents and cached tiles**. `FS-query` and `MS-export` refused from t+0 —
expected here, since the datastore is the same instance. But `VT-warmtile` answering 200
*is* clean evidence, because a cached tile needs no datastore: it shows the fallback
reaching the disk cache, which is gate 1's N2, previously recorded as not built.

## F3. Degraded serving lasts about thirty seconds, not the advertised fifteen minutes — HIGH, not repaired

`CatalogFallback.DefaultWindow` is fifteen minutes and its remark explains the choice at
length. Measured across two independent outages, the degraded window is roughly **thirty
seconds**:

```
t+32.1s  FS-svcdoc=200/age28  MS-svcdoc=200/age28  MS-legend=200/age28  VT-warmtile=200/age28
t+48.1s  FS-svcdoc=503        MS-svcdoc=503        MS-legend=503        VT-warmtile=503
                                                   VT-style=200/age42
```

The catalogue memory is still well inside its window — the server is still stamping
`age42`, `age74` — and the documents refuse anyway.

**There are two caches and only one of them has a fallback.** The catalogue memory lasts
fifteen minutes; the described-shape memory lasts thirty seconds and has none, and
`/admin/health` states the number itself: `describedShapes.lifetimeSeconds: 30`. Every
document that needs a field list — the FeatureServer and MapServer service documents, the
legend, a tile — dies at thirty seconds regardless. `VT-style` is the only document that
outlives it, because it is built from the style and the catalogue alone; it served to
t+80 s and refused only when the fifteen-minute window genuinely expired, verified at
`age1047`.

**So ADR-026's answer to Q-95 is narrower than written on two axes at once**: three faces
of seven, and thirty seconds of fifteen minutes. [D-127](../architecture-debt.md) widened
to record both.

## F7. A client that hangs up was recorded nowhere — MEDIUM, half repaired

`ErrorResponse.Classify` maps `OperationCanceledException` to 499 with the remark *"It
exists so the access log distinguishes 'they left' from 'we broke'."*

```
$ grep -c -- " - 499" dev-server.log
1
$ grep -- "- 499" dev-server.log | tail -1
      GET /sharing/rest/community/self?f=json - 499
```

**The client-disconnect 499 has never been written once** across a 6.6 MB log. The single
hit is ArcGIS's own *Token Required*, which uses 499 for an unrelated meaning. The
inferred cause: a disconnect happens after the response has started, and `WriteAsync`'s
first act is to log the truncation and abort — so the branch that would emit 499 is
unreachable in the case it was written for.

Twenty deliberate hangups, five per face:

| Face | Access log | Failure log |
|---|---|---|
| FeatureServer query | `- 200` | names the truncation, correctly |
| WFS `GetFeature` | *none* | *none* |
| OGC `items` | *none* | *none* |
| WMS `GetMap` | *none* | *none* |

Once the response has started, ASP.NET's exception-handler middleware declines and
`ErrorResponse.WriteAsync` never runs, so its truncation branch is unreachable from those
faces. An operator investigating client-reported truncation on WFS had nothing to read.

**Half repaired.** WFS and OGC now run their streaming writes through a wrapper that logs
the truncation and aborts, mirroring what the FeatureServer path already did. Verified
with three forced resets on each:

```
fail: wfs[1007]  A response failed after the body had begun, so the client received a
                 truncated document and no status. The connection was aborted…
fail: ogc[1007]  (same)
```

WMS is not wrapped because it writes fully buffered documents and images — there is no
mid-stream failure to catch. **What stays open is the 499 itself**: the access log still
records `- 200` for a response nobody received, and the status code the remark promises
is still never written. [D-132](../architecture-debt.md).

**What held throughout:** the connection and the pool are released properly. After twenty
forced resets `/admin/health` was byte-identical before and after, and the next query
answered in 19 ms.

## F8. The blind refusal said "no record" while reporting the record's age — LOW-MEDIUM, repaired

At 964 s into an outage, past the fifteen-minute window:

> *…this server has **no record** of a service named 'tr_ref' from before it went quiet.
> … Public services are still being served, **from a catalogue 964s old**.*

The two clauses contradict each other inside one paragraph. `CatalogFallback` returns a
blind answer for both *never seen* and *seen and now too old to trust*, and the refusal
could not tell them apart — so an operator reads that a service they published months ago
has no record on the server, when what happened is that the outage passed fifteen
minutes.

**Repaired.** `Age` is zero when nothing was ever remembered and non-zero when the memory
expired, so the information was already on the wire and this needed no new plumbing.
There are three messages now: never seen, expired, and not-public-when-last-read.
**Not verified live** — the expired branch needs a fifteen-minute outage, and the
repair was judged by reading rather than by measuring. Stated rather than implied.

## F9. Two workers log a full stack trace every three seconds through an outage — LOW, not repaired

`GeodatabaseImporter` and `GeodatabaseInspector` each retry a job claim against the down
store. Over one 17-minute outage: **338 warnings, 1.68 MB of log** — about 100 KB/min,
roughly 145 MB/day of sustained outage. The message itself is good; it says the loop
deliberately does not exit and why. The stack trace is what does not need repeating 338
times, and a store outage is exactly when an operator wants to read the log.
[D-133](../architecture-debt.md).

## F10. The portal's first query returned an empty portal — LOW, repaired

```
q=*                       → total 0
q=owner:root              → total 12
q=type:"Feature Service"  → total 12
```

`*` is the standard ArcGIS wildcard and clients send it as their default. It was treated
as a literal word to find in the title, with nothing to say the syntax was unsupported.

**Repaired.** A bare `*` matches everything; `title:*` stays a literal, because a field
with a value is a question about that field and answering it with *everything* would be a
different lie. **Verified: `q=*` → 12, `q=title:*` → 0.**

---

## A lead, not a finding

**One sample showed `/healthz/ready` green while two serving paths were 503** — gate 1's
finding 1b exactly. **It did not reproduce.** Three further outage cycles polling
readiness and four serving paths concurrently every 0.4 s — roughly 450 samples —
produced zero occurrences, and every cycle showed readiness erring the safe way:

```
t+0.0s  ready=503 FSq=503 tile=503 OGCitems=503 WMSmap=503
t+0.5s  ready=503 FSq=200 tile=200 OGCitems=200 WMSmap=200
t+0.9s  ready=200 FSq=200 tile=200 OGCitems=200 WMSmap=200
```

The explanation is the instrument: in the sweep that produced it, probes run sequentially
and readiness was second-to-last of twenty-five, so about 20 s of wall clock separated it
from the serving probe. **The 1b fix holds**, and the original sample is recorded so
nobody re-derives it as a finding later.

---

## What held

- **Recovery is excellent, and it was not designed for.** After outages of 10 s, 20 s,
  35 s, 40 s, 80 s and 17 minutes, all seven faces returned 200 **within 0.7 s** of the
  store returning, with no restart. Gate 1 measured 8 s. It is uniform: no face lagged.
- **Readiness is conservative and names its cause**: `{"status":"not-ready","reason":
  "Failed to connect to 127.0.0.1:55432"}`. That is a reason an operator can act on
  without opening a second window.
- **The fifteen-minute cap works.** At `age1047` the fallback stopped serving. Indefinite
  stale serving is what `DefaultWindow` exists to prevent, and it is prevented.
- **Capabilities documents refuse rather than silently shrinking.** WMS and WFS
  `GetCapabilities` and OGC `/collections` all answered 503 during the outage. None
  returned a well-formed document with the layers quietly missing, which is the specific
  hazard this gate was asked to look for.
- **Malformed input: 51 of 57 cases named exactly what was wrong, in the right envelope.**
  ``​`bbox` is four numbers — minx,miny,maxx,maxy — or six with minimum and maximum
  elevation between them``; ``​`not-a-date` is not an RFC 3339 date or timestamp. Write
  2026-08-20 or 2026-08-20T14:00:00Z``; ``​`bbox-crs=banana` is not a coordinate reference
  system this collection offers. It offers: …`` — and it lists them.
- **WFS POST bodies.** A billion-laughs bomb: refused. An XXE reading a local file:
  refused. A 5,000-deep nested filter: refused by the depth budget. A 5 MB body: refused
  at the 4 MB ceiling, naming the limit. A 60 MB body hits Kestrel first and **the 413
  says which component refused it** — unusual, and right.
- **`/rest/services` and the folder listings stayed cheap** throughout.

## What was not run, and why

- **6. Cache storage unusable.** The live tile cache sits beside the DLLs the running
  process holds open, so making it unwritable would risk the session for a scenario gate
  1 already passed. Cheap to run properly at next start with the cache path pointed at a
  read-only directory.
- **3. Data source slow.** Still needs a latency proxy in front of PostGIS. F1's 4 s and
  8–12 s figures are an accidental partial measurement of it and argue for doing it
  properly.
- **4/5. Worker crash, job worker partition.** No supervisor to crash. F9 shows the job
  workers survive a store outage and keep retrying, which is half of scenario 5.
- **7. Out of memory under overlay.** Gate 1's verification stands.
- **10/11/12. Certificate expiry, configuration corruption, partial upgrade.** All need a
  deployment or a clock rather than a process.
- **The catalogue-and-data-separately-reachable case**, which is what D-127's trigger
  actually names. It needs a second PostgreSQL. A split deployment would likely make the
  three covered faces look *better* and the four uncovered ones look exactly the same.

**Six of twelve scenarios executed, against seven faces.** Recorded rather than rounded
up — a gate that claims coverage it does not have is the failure §66 exists to prevent.

**Environment restored** by the reviewer and re-verified afterwards: the container is up,
every path answers, `/healthz/ready` reports ready and `/healthz/live` is back to
milliseconds.
