# ADR-015 condition 1 / A-046 — what a session lookup costs per request

**Run 2026-08-27** against a running server on this machine, PostgreSQL 17 in
`gis-experiment-postgis`. `session.py` is the harness.

[ADR-015](../../docs/adr/ADR-015-authentication.md) condition 1:

> **Session lookup cost is measured**, not assumed. If a per-request indexed read against
> the platform store is material at the concurrency ADR-007 targets, the in-process cache
> TTL becomes a stated revocation delay rather than an implementation detail.

[A-046](../../docs/architecture-assumptions.md) is the assumption under it: *an
opaque-token session lookup per request is affordable at the concurrency ADR-007 targets.*
It has been `UNVALIDATED` since 2026-08-13.

## How it is measured, and why not directly

**As a difference between two requests that are otherwise identical.** The lookup happens
inside the pipeline and cannot be timed alone from outside, so this asks for a **public**
layer document twice — once with a bearer token and once without. The public route means
both are legal and both do the same work; the only difference is that one resolves a
session and the other does not.

**Interleaved and repeated, because the first attempt produced a finding that was noise.**
A single pass at 48 callers showed the authenticated median **32 ms slower**; three
repeats showed +7, **−33** and +10. A p50 that changes sign between runs of unchanged code
is the machine, not the server — this repository has caught five wrong latency harnesses
and this was nearly the sixth. What survives repetition is throughput, so that is what is
reported.

## Serially

| | samples | p50 | p95 | min | max |
|---|---|---|---|---|---|
| anonymous — no session read | 400 | 9.22 ms | 20.68 ms | 7.85 ms | 29.68 ms |
| authenticated — one session read | 400 | 9.98 ms | 21.25 ms | 8.67 ms | 24.66 ms |

**+0.76 ms, or 8.2%.** That is one indexed read on a hashed token against a pooled
connection, and it is the same order as [D-30](../../docs/architecture-debt.md)'s 1.8 ms
for the per-request catalogue read, which fetches a larger row.

## Under concurrency

Six rounds each, alternating, 1,200 requests per round.

| callers | anonymous | authenticated | cost |
|---|---|---|---|
| 24 | 516 req/s (513–568) | 472 req/s (466–480) | **8.5%** |
| 48 | 505 req/s (497–516) | 494 req/s (480–500) | 2.0%, inside the noise |

**At 24 the cost is real and the ranges do not overlap.** At 48 the difference disappears
into whatever else saturates first — the server is at about 500 req/s either way, so the
session read has stopped being what limits it.

## What this answers

**A-046 holds. The condition's second branch does not apply.** The lookup costs about
0.8 ms and about 8.5% of throughput at the concurrency it was to be measured at, and
nothing about that is material enough to buy back with a cache. So the in-process cache
TTL **stays an implementation detail rather than becoming a stated revocation delay**,
which is the outcome the condition was written to distinguish — and the more valuable
half is that revocation stays immediate: a session removed from the store stops working
on the next request, with no window to document.

**What is not settled.** One machine, one deployment, one layer document. The absolute
numbers are a laptop also running two servers and a PostgreSQL; what transfers is the
ratio, which is what the condition asks about. And the request measured is a *cheap* one —
a layer document, about 9 ms — so 8.5% is close to the worst case: on a query costing
hundreds of milliseconds the same 0.8 ms is under a percent.
