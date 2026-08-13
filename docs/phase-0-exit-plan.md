# Phase 0 — What Is Left, and When Implementation Starts

**Written 2026-08-13**, in answer to a direct question from the project owner.

This document is an assessment, not a decision. It says what state Phase 0 is
actually in, which remaining items genuinely block writing production code and
which do not, and gives a recommendation with a shape and a rough duration.

---

## 1. Where Phase 0 actually is

| | |
|---|---|
| ADRs | 12 written. 8 `ACCEPTED WITH CONDITIONS`, 1 `ACCEPTED`, 1 `DRAFT` (ADR-003), 2 `DEFERRED` (rendering, clustering) |
| Open questions | **44 open**, 30 answered |
| §81 exit criteria | **4 of 16 met** |
| §66 review gates | **0 of 9 run** |
| Benchmarks | 3 rounds, one subsystem (the tile path) |
| Independent adversarial review | **never done** — see §5 |

### The imbalance that matters more than the counts

What is decided is the engine room: service runtime, query engine, geometry
engine, caching, jobs, data model, API shape, provider model. That work is
genuinely deep and, in three areas, backed by measurement rather than argument.

What is **not** decided is almost everything the GIS administrator touches:

| Area | State |
|---|---|
| **TLS / certificates** | absent from the entire architecture (Q-55) |
| **AuthN (§41)** | not started — no local accounts, JWT, OAuth, OIDC decision |
| **Admin API (§39)** | not started |
| **Publishing (§38)** | not started |
| **Observability (§46)** | not started |
| Glyph & sprite serving | not started |
| Style document management | not started |
| Resource governance (§49) | not started |
| Deployment profiles (§53) | not started, and Q-71 just reopened packaging |

**The primary user is the GIS administrator (Q-06a), and we have designed
almost nothing that user touches.** That is the honest headline. The
architecture is strongest exactly where the administrator never looks.

### The open-question count overstates the uncertainty

Q-67, Q-69 and Q-70 deleted large parts of the problem in one day: the
three-dialect tile path, the portable platform store, the optional datastore,
two deployment shapes. A mechanical sweep would close or dissolve a significant
fraction of the 44 without any new thinking. The number to trust after that
sweep, not before it.

---

## 2. The structural argument for moving

**Several Phase 0 exit criteria cannot be met by Phase 0.**

Eight ADRs are `ACCEPTED WITH CONDITIONS`, and most of those conditions are
*numbers*: the connection budget at 1,000 services (Q-04), worker sizing and the
per-worker context budget (ADR-007 §4.1, §4.4), the L3 cache size budget (N6),
affinity-routing hit rates (A-014), the allocation term §4.14 says is missing.

None of those can be produced by more analysis. They require a running system
under load. Staying in Phase 0 to resolve them is chasing something the phase
structurally cannot deliver, and the risk of that is not theoretical — it is how
architecture documents drift from being decisions into being literature.

Run 3 is the proof of the pattern working the other way. It settled A-037 and
A-021, opened A-039, and directly caused the owner to take Q-67 — a decision
that deleted a subsystem. **Measurement changed the architecture more in one day
than analysis had in the previous several.** The same is likely true of the next
layer down, and the next layer down needs production-shaped code.

---

## 3. What genuinely blocks the first production line

Five items. Not forty-four.

| # | Item | Why it blocks | Effort |
|---|---|---|---|
| **B1** | **Contradiction sweep (§63)** | Three owner decisions reversed within hours of each other on 2026-08-12, one of them reversing a headline decision taken the same morning. Something has been missed. This is bookkeeping the reversals already made necessary | ~1 session |
| **B2** | **ADR-003 out of `DRAFT`** | Every path touches geometry, and the ADR that decides where geometry work happens is still a draft. It is mostly answered by runs 1–3 — it needs writing up and deciding, not more investigation | ~1 session |
| **B3** | **TLS and certificates (Q-55)** | A server with no transport security story cannot be built and secured later. It touches configuration, deployment, the admin API and the reverse-proxy question simultaneously. Needs a decision, not a prototype | ~1 session |
| **B4** | **AuthN (§41)** | Identity underpins the admin API, the publisher role, RBAC, hosted-item ownership and audit. Nothing user-facing can be written first and have authentication threaded through afterwards without rewriting it | ~1–2 sessions |
| **B5** | **Packaging (Q-71)** | The baseline is now three images — server, mandatory datastore, job worker with GDAL — with no story for how they are packaged, version-matched, upgraded or carried into an air-gapped install. "Run it" has no answer | ~1 session |

**Everything else can be decided while building**, and several items will be
decided *better* while building.

## 4. What explicitly does not block

- **The ADR conditions.** They are numbers. See §2.
- **Observability, resource governance, backpressure tuning, clustering.** All
  need a running system to be designed against anything real.
- **Glyph and sprite serving, style management.** Tile-path concerns, and tiles
  are the *second* workload (Q-06b).
- **Q-68 — do we keep our own MVT encoder.** Settled by measuring
  read-once-encode-many during the tile phase, not before it.
- **The air-gapped checklist (Q-15).** Written by attempting an offline install
  of the skeleton, which is the only way it gets tested rather than asserted.
- **The three 2 AM scenarios (F5).** They were proposed as a test of whether the
  observability model exists. Better answered against a system that can be
  observed.
- **Q-49 — why this product should exist.** Attempted three times from the desk
  with no strong answer, and desk research will not produce one. It needs
  conversations with organisations running ArcGIS Server, which can and should
  happen in parallel with building rather than gating it. **A working skeleton
  makes those conversations better, not worse.**

---

## 5. The one thing that should not be skipped

**§67's fresh-challenger review has never actually happened.**

Both "adversarial" rounds in [reviews/](reviews/) were written by the same agent
that produced the architecture, which §67 explicitly forbids. Round 2 says so at
the top of its own file. Their findings were real and were applied — F12 changed
CLAUDE.md, G4 produced the geometry and CRS policy — but coverage from a
self-review is worth much less than its findings suggest, because the blind
spots are shared.

Starting implementation on an architecture that has never had an independent
adversarial read is the largest process risk in this plan, and it is not
mitigated by the author reviewing it a third time.

**Pragmatic option:** a reviewer with no prior context, given only the documents
and the master prompt, with no access to the reasoning that produced them. That
is not the same as a different person and should not be recorded as if it were,
but it is materially better than a fourth self-review. It costs roughly one
session and should run against the state *after* B1–B5, not before.

---

## 6. Recommendation

**Three steps. Implementation starts at step 3, realistically about a week of
working sessions from now at the pace of the last two days.**

### Step 1 — closure sweep (~2 sessions)

Contradiction sweep (B1), ADR-003 to `ACCEPTED` (B2), and a mechanical pass over
the 44 open questions closing everything Q-67/Q-69/Q-70 dissolved. Run the §66
review gates that do not need code: correctness, simplicity, consistency,
licensing.

### Step 2 — decide what cannot be discovered by building (~3 sessions)

TLS (B3), AuthN (B4), packaging (B5), and the admin API's *shape* — not its full
surface, but enough that the skeleton has somewhere to put publish, list and
inspect. Then the independent review from §5.

### Step 3 — walking skeleton (§71–§73)

One vertical slice, end to end, production code written fresh:

> A registered PostGIS layer, published through the admin API, served as OGC API
> Features, over TLS, behind authentication, from a packaged deployment.

That single slice exercises the platform store, the provider model, the query
engine, the API layer, authN, publishing and packaging — and it produces the
first numbers for the connection budget and worker sizing that eight ADRs are
currently waiting on. It deliberately excludes tiles, which are the second
workload and whose remaining questions (Q-66, Q-68) are best answered after the
feature path is real.

**Phase 0 does not end when the questions run out. It ends when the questions
that are left are ones only running code can answer.** On the evidence in §2
and §3, that point is close — five decisions away, not forty-four.

---

## 7. What this plan does not claim

- It does not claim the architecture is finished. §1 says plainly that the
  administrator-facing half barely exists.
- It does not claim B1–B5 is a complete list. It is a list of what blocks *the
  slice described in step 3*. A different first slice would block on different
  things.
- It does not resolve Q-49. Nothing here answers why this product should exist,
  and that remains the largest open risk in the project — larger than any
  technical item in this document.
