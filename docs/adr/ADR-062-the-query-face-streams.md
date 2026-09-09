# ADR-062 — The query face streams, like every other face

| | |
|---|---|
| **Status** | `ACCEPTED` |
| **Confidence** | `HIGH` — the choice is between two behaviours that were both measured on the same route with the same data, and the losing one was never chosen by anybody. |
| **Decided** | 2026-09-09 |
| **Supersedes** | — |
| **Superseded by** | — |

---

## 1. Context

**[D-245](../architecture-debt.md), found 2026-09-09 while sweeping for claims the code does
not support.** The ArcGIS FeatureServer `query` face — the one that carries more bytes than
every other face in the product put together — did not stream. It buffered whole responses in
memory and released them when the handler returned.

**Nobody chose that.** `Program` built a `Utf8JsonWriter` over `context.Response.BodyWriter`,
the `IBufferWriter<byte>` overload, whose `FlushAsync` only `Advance`s the pipe; and
`BodyWriter.FlushAsync` was called nowhere in `/src`. `Response.BodyWriter` appeared exactly
once in the whole product, on that line. Every other streaming face — OGC API Features, WFS —
uses `context.Response.Body`, the `Stream` overload, which writes through.

**The writer was never the problem, which is what made this hard to see.**
`FeatureServerQueryWriter` does write a feature at a time and materialise nothing, which is
what [A-037](../architecture-assumptions.md)'s allocation argument asks of it. Its own remarks
said *streams*, `Program`'s comment said *nothing is buffered*, and both were true statements
about the code they sat beside. The sink defeated them, one line away.

**What forces a decision rather than an edit** is that the two behaviours have different
failure stories, and the register recorded that as the reason not to repair it in the sweep
that found it: a face that streams cannot report a failure that happens after the first byte,
because the status line is gone. That is [Q-91](../open-questions.md), open since D-07.

## 2. Alternatives considered

### Alternative A — make it stream (chosen)

Build the writer over `context.Response.Body` and flush on the same 32 KB threshold
`OgcFeatureWriter` uses.

**Argument for.** It restores the behaviour three separate comments already claimed, the
behaviour `FeatureServerQueryWriter` is shaped for, and the behaviour every other streaming
face in this product already has. It takes time-to-first-byte on a 21 MB answer from 3.789 s
to 0.022 s (§4). It puts a bound on memory that no configuration setting has to supply. And it
rejoins the drain to the handler, so a client that goes away mid-answer is recorded as having
gone away instead of as a clean 200.

**Argument against.** A failure after the first byte can no longer be answered in the ArcGIS
error envelope. The connection aborts and the client sees a truncated document.

### Alternative B — make the buffering deliberate

Keep the whole body in memory, but own the buffer rather than the pipe: write into a buffer
the handler controls, so a failure at row 40,000 reaches `ErrorResponse` with `HasStarted`
still false and produces a clean 500 in the ArcGIS envelope.

**Argument for.** Every failure on this face stays reportable in the protocol's own
vocabulary, which is the thing Q-91 has been unable to answer for three weeks. An ArcGIS client
gets a JSON error object it already knows how to read, rather than a socket that stopped.
Peak memory is bounded by `MaximumResponseBytes`, which already exists.

**Argument against.** It makes the busiest face the only one in the product that buffers, and
that difference would be invisible: the same writer, the same shape of handler, the same
comments. It keeps a whole response in memory per concurrent request — 21 MB × concurrency —
where the ceiling that bounds it can be *disabled by setting it to zero*, an option whose own
comment offers it as *a deployment that would rather stream without a limit*. And it keeps
time-to-first-byte at 3.789 s on 21 MB, on the face an operator will benchmark first.

### Alternative C — leave it and document it

**Argument for.** It has been like this for as long as the face has existed and nobody has
reported it.

**Argument against.** Nobody has reported it because the failure is invisible from outside: the
answer is correct, only late. The access log agrees that everything is fine, including for the
clients that did not get the whole answer (§4). *Nobody complained* about a defect nobody can
see is not evidence.

## 3. Counterarguments to the preferred option

**The strongest one is that this trades a reportable failure for an unreportable one, and Q-91
is open precisely because we do not know what a real ArcGIS client does with the unreportable
kind.** Choosing to stream commits this face to the abort story before that experiment has
been run. If Pro turns out to treat a truncated `query` response as an empty result rather than
as an error, this decision will have made a silent-wrong-answer path on the largest face.

**That is answered by where the face already stood, not by dismissing it.** Every other
streaming face here already takes the abort story, so the experiment Q-91 needs was owed
whatever this ADR decided; and D-07's row — *a client is expected to distinguish a truncated
page from a short one* — has been fired since paging shipped. What changes here is one more
face standing where three already stand, and it becomes **easier** to answer, not harder,
because the failure mode is now the same everywhere and one experiment covers all of it.

**The second is that Alternative B is the only one that makes the log honest without giving
anything up.** True, and it is why B was written in its strongest form: owning the buffer
would rejoin the drain to the handler as well. It loses on memory and on latency, and on being
a difference nobody would see.

## 4. Evidence

Measured 2026-09-09 on `hosted/tr_yol` — 46,041 features, a 21,184,212-byte answer — over the
same schema, same machine, same route, same middleware. Two servers differing only in the sink.

| Claim | Evidence | Source |
|---|---|---|
| The face did not stream | `ttfb=3.789 s` of a `total=3.904 s`: 97% of the wait is before the first byte, and `curl --max-time 0.20` created no file at all | old sink, port 8443 |
| It streams now | `ttfb=0.0225 s`, `total=0.801 s` warm; **3,977,443 bytes on the wire at t=0.20 s** | new sink, port 8462 |
| The document is unchanged | both bodies `md5 7f75172d9495a6ed4b392dd7cdf898cf`, 21,184,212 bytes | both |
| The log said a partial delivery was a clean 200 | a rate-limited client (`--limit-rate 500k --max-time 3`) received **1,653,587 of 21,184,212** bytes and `request_log` recorded **200** | old sink |
| The log says so now | the same client received **1,839,026 of 21,184,212** and `request_log` recorded **499** | new sink |
| Every other streaming face already chose this | `OgcFeatureWriter` writes `Utf8JsonWriter` over a `Stream` and flushes at `BytesPending > 32 * 1024`; `Response.BodyWriter` occurred once in `/src`, on the line this ADR changes | source |
| The size ceiling survives | `BytesCommitted + BytesPending` is the response's size across a flush, because a flush moves bytes from one to the other | `FeatureServerQueryWriter` |

**A rate-limited client is what made the log claim reproducible.** Aborting on a timer lands
inside the drain window only by luck — the window is the few milliseconds between the handler
returning and the last byte leaving — and a warm server's whole answer fits inside a 3.85 s
timeout. A client that reads slowly is in that window for as long as it takes.

## 5. Decision

**The ArcGIS FeatureServer `query` face streams.** `Program` builds its `Utf8JsonWriter` over
`context.Response.Body` rather than `context.Response.BodyWriter`, and
`FeatureServerQueryWriter` flushes when `BytesPending` passes 32 KB — the threshold and the
idiom `OgcFeatureWriter` already uses, so there is one streaming policy in this product rather
than two. A failure after the first byte aborts the connection, which is what every other
streaming face here does and what `ResponseOutcome.Truncated` exists to record.

## 6. Consequences

**Positive.** Time-to-first-byte on a large answer falls from 3.789 s to 0.022 s. Peak memory
per request stops being the size of the answer. The access log stops recording a partial
delivery as a complete one, which is [D-132](../architecture-debt.md) alive on the largest
face. Three comments that had to be corrected on 2026-09-09 become true again rather than
staying corrections. And `MaximumResponseBytes = 0` stops being a way to make one request's
memory unbounded.

**Negative.** A failure after the first byte is unreportable in the ArcGIS envelope on this
face, as it already was on the other three. `Q-91`'s experiment — point a real ArcGIS client at
a server that fails mid-stream — is now owed for the face that matters most, and until it is
run the honest position is that we truncate and record it as a 500 internally.

**Ports created.** None.

**State.** *Catalogue*: none. *Runtime*: a 32 KB write buffer per in-flight `query`, node-local by construction — it is the response's own buffer and cannot outlive the request. That is the whole of the change: what this decision removes is runtime state, a whole-body hold whose size was the answer's and whose only bound was a setting a deployment may set to zero.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| A-037 | Allocation, not CPU, is the binding constraint on a large feature response | Unchanged, and this decision is what its argument was always for: the writer materialised nothing and the sink held everything anyway |

## 8. Dependencies

**Depends on** — [ADR-005](ADR-005-api-architecture.md) for the face itself.

**Depended on by** — nothing yet. [Q-91](../open-questions.md) is sharpened rather than
answered by it: the question is now about four faces rather than three, and one experiment
settles all of them.

## 9. Revisit triggers

- **A real ArcGIS client is observed treating a truncated `query` response as an empty result
  rather than an error.** Then the silent-wrong-answer path §3 warns about is real, and
  Alternative B — or a terminal error object, which Q-91 lists — has to be weighed again with
  that observation in front of it.
- **A deployment measures memory pressure that this made worse rather than better.** It should
  not: streaming replaces a whole-body hold with a 32 KB buffer. If it does, the flush
  threshold or the read-ahead is wrong, not the decision.

## 10. Dissent

**The `query` face is the one place where an ArcGIS-shaped error still mattered, and this
spends it.** ArcGIS clients read `{"error":{...}}` and nothing else; a truncated document is a
failure they have no vocabulary for. Every other face here that streams speaks a protocol whose
clients are, on the whole, more tolerant — a GeoJSON reader that gets a short document usually
fails loudly. Trading that away for latency on a face whose typical answer is a few hundred
kilobytes, not twenty-one megabytes, is a real cost paid for a case that is not the common one.

**The answer, which did not remove the disagreement:** the typical answer being small is
exactly why the cost is small, and the large answer is the one that makes an operator conclude
the server is slow. And the position being defended was never chosen — it was what
`Utf8JsonWriter` does when handed an `IBufferWriter`. Defending it now would be adopting a
decision by accident and then arguing for it.
