# Failure gate — run 1

**Gate:** §66 *Failure*. **Run:** 2026-08-15. **Result: FAIL, and repaired in
part.**

**What this is.** [failure-scenarios.md](../failure-scenarios.md) walked twelve
failures in Phase 0, before any code existed, and predicted what should happen.
This gate asks a different question: **what actually happens.** Every row below
was executed against a running server with a real PostGIS behind it, not
reasoned about.

**Why it was worth running.** Three of the predictions were wrong about the
built system, one of them in the direction that matters — a health probe
reporting success while the server could not serve. That one is fixed. The other
two are recorded as still open, because the honest answer is that they are not
built rather than that they were tested and passed.

---

## Summary

| Scenario | Predicted | Observed | Verdict |
|---|---|---|---|
| 1. Platform store unavailable | Degraded read-only from bound contexts (N1) | **503 on every data path**, including a public layer and a warm tile | **FAIL** — N1 not built, [Q-95](../open-questions.md) |
| 1a. Recovery after the store returns | not stated | Recovers with no restart; queries answered again within 8 s | **PASS**, and it was not designed — it is Npgsql's pooling |
| 1b. Readiness during recovery | not stated | **`/healthz/ready` answered 200 while every query answered 503** | **FAIL → FIXED** |
| 1c. Liveness during the outage | Process must not be killed | `/healthz/live` stayed 200 throughout | **PASS** |
| 2. Cache lookup during a store outage | Should not need the index (N2) | 503 before the cache is consulted at all | **FAIL** — N2 not built |
| 6. Cache storage unusable | Degrade to no-cache, never to an error (ADR-010 §3) | Server starts, tiles serve, **one** warning, correct bytes | **PASS** |
| 7. Out of memory under overlay | Bounded, not fatal | Worker heap ceiling and deadline, verified separately | **PASS** — [ADR-022](../adr/ADR-022-geometry-server.md) §9 |
| 9. Huge or malformed input | Refused with a reason | Verified across query, upload and archive paths | **PASS** |

---

## 1b — the finding worth having

**`/healthz/ready` reported ready while the server could not answer a query.**

Measured during recovery, at four-second intervals:

```text
t+4s   ready=200   query=503
t+8s   ready=200   query=200
```

**Why.** Readiness probed the platform store — `IAdminCatalog.ListLayersAsync`
— and nothing else. The serving path also needs the **datastore** pool, a
separate `NpgsqlDataSource` with its own connections and its own reconnection
timing. After the database came back, the catalogue pool recovered first and
readiness went green while queries were still failing.

**Why it matters more than it looks.** Readiness is not a diagnostic; it is the
signal an orchestrator *acts on*. A green probe in front of a red server is
worse than no probe at all, because it actively routes traffic into failures.
The window here was seconds; under load, or with a slower datastore, it is as
long as the second pool takes.

**Fixed.** `/healthz/ready` now probes both the platform store and the datastore.
Re-measured:

```text
t+4s   ready=503   query=200
t+8s   ready=200   query=200
```

The probe is now **conservative** — it can say not-ready while the server is in
fact serving, which holds traffic back slightly longer than necessary. That is
the safe direction and it is the one to be wrong in.

**Only the datastore, not every registered source.** The datastore is mandatory
([ADR-019](../adr/ADR-019-portal-server-split.md)) and every hosted layer needs
it. A *registered* source being down should fail its own layers and not take the
whole server out of rotation, which is scenario 2's containment property.

---

## 1 and 2 — the two that are simply not built

The prediction was *degraded read-only service from already-bound contexts*,
resting on two constraints the document named at the time:

> **N1 — a service context must be self-sufficient for serving.**
> **N2 — L3 cache lookup should not require the index.**

Neither is implemented, and the gate confirms it rather than assuming it. Every
data request resolves its service through `ServiceLookup` → the catalogue →
the platform store, on every request. That is deliberate — **D-17** records the
reasoning: the catalogue read carries the sharing scope and the started/stopped
status, and those are not safe to remember. But the consequence is that a
platform-store outage is total rather than degrading, including for a public
layer whose tile is already on disk.

**This is [Q-95](../open-questions.md)** — *should serving survive a
platform-store outage, and is it worth caching an authorization decision?* — and
it is the owner's, because the trade is availability against how long a
revoked permission can still be honoured. The gate adds evidence to the
question: the outage is currently total, and a warm tile for a public layer is
inside the blast radius.

---

## What was not run, and why

- **3. Data source slow.** Needs a controllable delay in the datastore, and the
  where-clause grammar deliberately has no function calls, so `pg_sleep` is not
  reachable from a request. Worth doing with a proxy that inserts latency.
- **4. Worker crash / 5. Job worker partition.** There is no supervisor and no
  job system to crash.
- **10. Certificate expiry.** Needs a clock the server believes, or a
  deliberately expired certificate. Cheap and not done.
- **11. Configuration corruption / 12. Partial upgrade.** Both need a deployment
  rather than a process.

**Recorded rather than ticked.** A gate that claims eight of twelve when it ran
five is the false assurance §66 exists to prevent.

---

## Consequences

- `/healthz/ready` probes both pools. One line of code, found only by running
  the failure.
- **N1 and N2 stay open**, now with measurements attached, and they belong to
  Q-95.
- The Failure gate is **run and FAILED**, not passed. It is re-run when Q-95 is
  answered, and the remaining scenarios above are the list.
