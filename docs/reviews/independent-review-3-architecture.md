# Independent Review 3 — Architectural Coherence and Correctness

> **Provenance, and what this is not. Read before citing it.**
>
> Produced 2026-08-13 by a reviewer with **no access to the conversation,
> reasoning or history that produced this architecture** — only the repository
> and `MASTER_GIS_PLATFORM_PROMPT.md`. Briefed to be adversarial, to cite file
> and section, to skip areas that are sound, and not to praise.
>
> **This does not discharge §67.** §67 requires a reviewer who did not
> participate. The author of the architecture commissioned this review, wrote its
> brief, and is recording its results. Blind spots in *reasoning* were removed;
> blind spots in *framing* were not. It is materially better than the two prior
> self-reviews and it is not the real thing. **Round 4 remains owed.**
>
> Findings reproduced as written. Dispositions:
> [independent-review-3-synthesis.md](independent-review-3-synthesis.md).

**Scope read:** `MASTER_GIS_PLATFORM_PROMPT.md`, `CLAUDE.md`, all 17 ADRs and
`_template.md`, `architecture-assessment.md`, `architecture-assumptions.md`,
`architecture-completeness.md`, `architecture-debt.md`, `open-questions.md`,
`phase-0-exit-plan.md`, `protocol-surface.md`,
`reviews/contradiction-sweep-1.md`, `benchmarks/mvt-generation/RESULTS.md`, and
the on-disk state of `/experiments` and `/benchmarks`.

---

## A1 — SEVERE · ADR-001's stated reason for skipping its own decisive experiment was voided the same day, and the ADR was never reopened

`docs/adr/ADR-001-core-language.md` §6, "Why the comparison was dropped", gives
three reasons. The first:

> "**In-process MVT encoding became mandatory.** `ST_AsMVT` is PostGIS-only, and
> Oracle and SQL Server are first-class (Q-50a). That puts CPU back on the hot
> path and raises the weight of C1 — geometry access — which is Go's weakest
> criterion"

Q-67 (`docs/open-questions.md`, dated **2026-08-12**, the same day) decided
"**tiles come only from hosted data**", and the datastore is PostGIS-only (Q-32).
`benchmarks/mvt-generation/RESULTS.md` states it plainly: *"In-process MVT
encoding was justified by `ST_AsMVT` being absent from SQL Server and Oracle;
there are now no tile sources that lack it… **A-019 is no longer
load-bearing**"*.

The second reason — the no-GDAL rule restoring single-binary for both candidates
— is described as *neutralising* C7 (Go's strongest criterion), and depends on
A-016, which ADR-001 §7 itself lists `UNVALIDATED` and which sweep finding S4
says DuckDB may invalidate. The third reason is a competitor's commit count,
explicitly "not a benchmark".

So the language decision rests on: a dead argument, a neutralisation depending on
an unvalidated assumption, and prior art. ADR-001 §9's revisit triggers do not
include "the reason we skipped the comparison evaporated".

**Compounding:** ADR-001 §7's A-001 row is internally contradictory in one table
cell — text reads "**`VALIDATED` 2026-08-12**" while the Status column reads
`UNVALIDATED`.

**Why it matters:** ADR-001 §1 says "Every other structural decision narrows once
this one is made", and §8 lists six dependents. The most load-bearing decision in
the repository is now the least supported. It is marked `ACCEPTED`.

---

## A2 — SEVERE · The primary Phase 0 deliverable describes an architecture that no longer exists

`docs/architecture-assessment.md` is the §70/§85 deliverable and a §81 readiness
criterion. It is stale in at least a dozen load-bearing places:

| Assessment says | Reality |
|---|---|
| §12 "Language comparison… **Not decided — prototype required.** Narrowed to Go and C#/.NET" | ADR-001 decided .NET without the prototype |
| §13 "ADR-003 remains `DRAFT`, blocked on the language" | `ACCEPTED WITH CONDITIONS`, HIGH confidence |
| §18.6 "A relational platform store, **portable across four engines**" | Q-70: PostgreSQL only |
| §14 "What remains is rendering WMS images in the compatibility layer" | Q-47 removed WMS from v1; Q-78/Q-83 put rendering back in scope |
| §22 "**Four platform stores, three spatial dialects**" | One platform store, six dialects |
| §24 "Thirty-four recorded" assumptions | 51 |
| §25 "Forty-six recorded, sixteen answered"; Q-17, Q-16, Q-29, Q-32 "still blocking" | 87 questions; all four answered |
| §26 "`experiments/lang-slice` — settles ADR-001… **First. Everything downstream waits on it**" | Never run |
| Header "nine research notes and **nine decided ADRs**" | 17 ADRs |

**Why it matters:** §81 requires this document complete for readiness, and §66's
nine gates would run against it. `phase-0-exit-plan.md` §3 lists six blockers and
*"update the assessment"* is not among them. If the governing synthesis is wrong
about which language was chosen and which ADRs are decided, no gate run against
it is meaningful. The contradiction sweep did not catch this either.

---

## A3 — SEVERE · Every condition-discharging experiment except one does not exist, and the plan reclassifies that as non-blocking

On disk: `experiments/` contains `_env/` and `lang-slice/README.md`.
`benchmarks/` contains `harness/` and `mvt-generation/`. That is all.

Named as conditions and absent:

- `experiments/geometry-oracle` — ADR-003 §4, §6d (validation mechanism for
  A-043), §9a condition 2; assessment §26 priority "Second"
- `experiments/affinity-routing` — ADR-007 condition 1, §4.4 ("must run before it
  is relied on"), Q-63
- `benchmarks/connection-budget` — ADR-007 condition 3, Q-04 (**Blocking Phase
  0**), ADR-014 condition 2
- `benchmarks/worker-model` — ADR-007 conditions 1a and 2 (A-015)
- `benchmarks/tile-seeding` — ADR-010 §6 (A-020)
- `benchmarks/feature-query` — ADR-001 §9's revisit trigger, ADR-008 §6

ADR-008 §6 cites *"`experiments/lang-slice` endpoint C"* as the evidence source
for A-019 — that experiment has a README and no code.

`phase-0-exit-plan.md` §4 then says: "**The ADR conditions.** They are numbers.
See §2" — explicitly non-blocking.

**Why it matters:** CLAUDE.md §3's standing challenge is "Where is the benchmark
proving this assumption?" Eight ADRs are `ACCEPTED WITH CONDITIONS`; the
conditions have been reclassified as deferrable; the qualifier carries no weight.
This is not a criticism of deferring measurement — that argument is sound — it is
that **the status label no longer means what the register says it means.**
`ACCEPTED WITH CONDITIONS` with permanently deferred conditions is `ACCEPTED`.

---

## A4 — SEVERE · Affinity routing is only correct inside a band nobody has computed, and its two assumptions pull in opposite directions

ADR-007 §4.4 builds an adaptive control system: lazy binding, LRU eviction, a
context budget, affinity preference, auto-pinning, and degradation to plain
balancing under skew. The ADR says this is "five interacting feedback mechanisms
with **no damping specified** — no hysteresis on pinning, no minimum residency,
no cost accounting for a rebind, no stability criterion", and traces an
oscillation path.

- **If A-015 holds** — §4.12 sizes a warm context at "**order of megabytes**" —
  then at CLAUDE.md §7's target of **100–1,000 services** the whole estate's warm
  state is order of gigabytes. A handful of workers hold every context. LRU
  eviction, the budget, pin contention, auto-pinning and skew degradation all
  become unnecessary.
- **If A-015 fails**, the register says: "§4.4 eviction becomes costly so the
  budget shrinks; §4.12 pinning becomes the norm — **which recreates per-service
  resource allocation, the thing §3 said killed ArcSOC**."

The design is right only in an unstated middle band. **No document computes the
band.** ADR-007 §5's explosion table uses `N` for worker count and "budget × N"
for contexts and never assigns either a value; §4.4 says `benchmarks/worker-model`
"must measure context weight distribution" and that benchmark does not exist.

**Why it matters:** §82 territory — "what concrete problem does this solve?" The
answer depends on a number never estimated, even on paper, at the committed scale
target. Assessment §16 calls affinity routing "the new idea and the weakest
part"; the possibility that it is also *unnecessary at 1,000 services* is nowhere
considered. §12's dissent argues the opposite direction only.

---

## A5 — SEVERE · A deleted portability constraint is still producing a documented correctness window, and is being used to argue for Redis

ADR-002 §4b (Q-70) made the platform store "**PostgreSQL. There is no second
dialect.**" `LISTEN`/`NOTIFY` is therefore available in every deployment. Four
documents still say otherwise:

- `ADR-010` §7: "Without portable `LISTEN`/`NOTIFY` (§4a.4), that is a polled
  invalidation sequence… **the invalidation delay is bounded by the poll
  interval**… for the *wrong* class in §5.1 it is a window during which incorrect
  data is served." §5.1 lists "**permissions changed**" as the *wrong* class.
- `ADR-010` §7: "That window is **the strongest argument for L2**."
- `ADR-010` §8: "a **real correctness gap**… the mitigation is an optional
  component. That is uncomfortable and **should stay uncomfortable**."
- `ADR-011` §3.3: "`LISTEN`/`NOTIFY` is not portable (§4a.4), **so workers
  poll**" — the header amendment supersedes only the job-claim half.

Note the citation: **§4a.4**, and §4a is stamped "`SUPERSEDED` by §4b."

**Why it matters:** assessment §20 records this as "a documented **disclosure
window**". The project is knowingly carrying a security window, and a standing
argument for Redis which CLAUDE.md §6 challenges, on the strength of a constraint
it deleted. The fix is trivial and nobody noticed it was available.

---

## A6 — SEVERE · ADR-005 contradicts itself in one file, and the sweep declared its blocker discharged without catching it or its siblings

`ADR-005-api-architecture.md` §3.3 still reads:

> "**Not** MapServer, ImageServer, GeometryServer or GPServer. Those produce
> rendered images, which vector-first removed and Q-47 kept out of v1."
> … "Which ArcGIS-compatible surface to offer, if any, is still Q-17."

§3.3a, forty lines *above*, records that this justification "**is false for two
of the four**" and that GeometryServer, GPServer and ImageServer are in. §9's
revisit triggers still list Q-17 as a future event.

Same class elsewhere:

- `ADR-010` §1: "`ST_AsMVT` exists only in PostGIS… **The cache is the mechanism
  that absorbs that difference**" — voided by Q-67. A-020 still `UNVALIDATED` and
  cited as a §13 revisit trigger, though there is no longer a gap to absorb.
- `ADR-011` §3.2, §3.3, §5 ("Four claim implementations"), §7 ("**four
  dialects**").
- `ADR-002` §6 "with SQLite, PostgreSQL, SQL Server and Oracle implementations";
  §7 lists A-018 `UNVALIDATED` while §4b says `SUPERSEDED`; §9 condition 2a still
  requires "A-018 must hold".
- `architecture-completeness.md` "Data model" row: "**on any of three spatial
  engines**; **editing is out of our API**" — both false.
- `architecture-completeness.md` "Rendering engine" row — the exact row sweep
  finding S2 flagged, still unamended.

`contradiction-sweep-1.md` found eleven and `phase-0-exit-plan.md` records
"**blocker B1 discharged**". All eleven are *forward-facing* (scope added without
propagating). **Zero are backward-facing** (premises deleted without
propagating), which is the larger and more mechanical class.

**Why it matters:** §63 requires contradictions resolved before proceeding, and
the process records that as met. A sweep that discharges its blocker while
leaving a self-contradicting ADR intact has produced **false assurance, which is
worse than no sweep.**

---

## A7 — SEVERE · ADR statuses are labels, not gates: work proceeds inside `REOPENED` ADRs from outside them

- **ADR-006** is `REOPENED` with §0 listing four things it "must now answer",
  including the dependency story Q-76 owns. `ADR-016` §7 **already answered
  Q-76**, `ADR-015` §7 **already answered who may publish code**, `ADR-016` §2
  **already ships the Python runtime**, and `ADR-017` §4 already lists a "Tools"
  resource group. The reopened ADR is being decided around rather than reopened.
- **ADR-009** is `REOPENED` with §0 calling ImageServer's expensive third
  "plausibly the largest single capability in the matrix". ADR-016 and ADR-017
  build on its un-reopened parts as settled.
- **ADR-004** is `DEFERRED` and re-confirmed, while MapServer, OGC API Maps and
  server-side rendering are in scope (Q-85, `[OWNER]`, unanswered).
- **ADR-002** is `ACCEPTED WITH CONDITIONS` with condition 2a bound to an
  assumption its own §4b superseded.
- **ADR-012** is `DEFERRED` and requires "every other ADR must state which of its
  state is node-local and which is shared". ADR-013, ADR-014, ADR-015 and ADR-017
  each introduced new state; only ADR-016 §3 back-filled, and not the node-local
  split.

**Why it matters:** CLAUDE.md §2 makes status a required field so it carries
information. Currently `REOPENED` means "still being built on", `DEFERRED` means
"in scope elsewhere", `ACCEPTED WITH CONDITIONS` means "accepted".

---

## A8 — MODERATE · ADR-003 claims `HIGH` confidence for a split whose continued existence is an open question, and never re-applies its own three conditions after introducing tier 1

Header: "`HIGH` for the split, **which is measured**." The measurement (runs 1–2)
compared *our primitives* against *NTS* on a path with **no pushdown**. Run 3
added tier 1, and §6a made it *first* — "it beats the other two by an order of
magnitude".

After tier 1, RESULTS.md finding 9: "**All the geometry work we own is 21.5 ms**…
out of 323." And under load: ours-with-pushdown **69.9 req/s** against
`ST_AsMVT`'s **96.3**, GC pause 65.6% versus 0.3%.

§6b requires condition 2, "**it is on the hot path**". Tier 1 removed most of the
hot path from tier 2, and §6b was not re-applied. Q-68 asks directly — "**Do we
keep our own MVT encoder now that every tile source is PostGIS?**… there is no
performance argument for ours either" — and the exit plan files it **not
blocking**.

**Why it matters:** what the benchmarks established is that *pushdown beats
everything we own*. That is an argument against tier 2 existing, recorded in the
ADR as validation *of* tier 2. `HIGH` confidence is not supportable while Q-68 is
open.

---

## A9 — MODERATE · Two ACCEPTED ADRs schedule the same event on opposite sides of a gate

- **ADR-003** §6d: A-043 deferred, "**the trigger is the second provider, not a
  date**". Exit plan B2b: "the walking skeleton is PostGIS only, so the question
  cannot arise in it."
- **ADR-008** condition 1a (from F1): "**A second dialect compiler exists from
  Phase 1** and runs in CI… No query engine feature is complete until it compiles
  on two dialects." Assessment §27: without it, capability negotiation is
  "**unfalsifiable** until Phase 4".

A second dialect compiler in CI is precisely the mechanism that surfaces
predicate-semantics divergence — the thing A-043 defers. Neither ADR references
the other.

**Why it matters:** the forcing function was installed to fail early. It will
fail into a question with no owner, no experiment on disk, and an explicit
statement that it is out of scope for the skeleton.

---

## A10 — MODERATE · The compatibility layer is now more capable than the product it wraps, and §51's boundary has been crossed without a decision

CLAUDE.md §5 and §51 require the compatibility layer "outside the core domain".
Four drifts, none reconciled:

- `ADR-005` §3.3b elevates **GeometryServer to "v1 core rather than a cheap
  addition to the compatibility layer"**.
- `ADR-008` §2a gives the compatibility layer **best-effort** execution while the
  native API **refuses** — the ArcGIS surface answers queries the native OGC
  surface will not.
- `ADR-013` §2a: "**The compatibility layer has a stricter data requirement than
  the product it wraps**."
- `ADR-015` §4 implements `/generateToken` with credentials in the query string —
  "**a deliberate weakening of the security posture**" existing solely for
  compatibility, which `ADR-017` §5a must then forbid on the admin API.

**Why it matters:** the migration surface is becoming the primary surface by
accretion, exactly as §8 warns. `competitive-position.md` §6 already concludes the
only defensible niche is the ArcGIS exit path — so this is the strategy, and §51
has not been amended to match. One of the two should give.

---

## A11 — MODERATE · GeometryServer publishes the project's worst-measured operation on the request path with a caller-chosen input size, and no assumption was opened for it

`ADR-005` §3.3b is explicit: "It exposes the exact operation run 1 measured as
pathological… **438.6 ms — 79% of a whole tile request**… **That escape route
does not exist here.**… A caller may post Türkiye's outline — 72,919 vertices…
Overlay is superlinear."

It lists caps as "Required, not optional". But: **no numbers**; **no condition**
in §8; **no assumption ID** — compare ADR-013 §4b, which raised A-041 for the
structurally identical attachment problem, and ADR-007 §4.15, which took a
corresponding amendment.

**Why it matters:** §3.3b names the pattern correctly — "any endpoint where the
caller picks our allocation size needs a cap, not a hope" — and then leaves it as
a hope, on the shared multi-tenant request worker where D-04 is `OPEN`.

---

## A12 — MODERATE · Scope roughly tripled against an evidence base of one subsystem, and the exit gate was told it did not matter

Between Q-17a and Q-84: 16 protocols, everything remaining on the matrix, three
more dialects (six geometry engines), a Python plugin system, ImageServer, a
geocoder. `protocol-surface.md` §1 records **two of ten engines "do not exist in
any form"**, and Q-79 asks whether they were "swept in by pointing at a list".

Exit plan §6a: "**None of it changes §3.**" True, and the wrong test. §82 is
binding and per-technology; sweep S3 records it was answered "per list" for
roughly thirty items, and Q-86 is open. Meanwhile `ADR-005` remains
**`MEDIUM-HIGH`** while §3.2a admits its central claim "was **asserted rather
than proven**".

**Why it matters:** the completeness matrix shows Security, Ops and Failure
review columns `—` for nearly every row, and §66's gates 0 of 9 at the time — seven
of the nine had run by 2026-08-20. **Scope is being
added in the one dimension the process has no instrument to measure.**

---

## The single most likely reason this architecture fails

Not a wrong decision — the decisions are individually well argued, and the
measurement work in `benchmarks/mvt-generation` is genuinely good and genuinely
honest about its own limits. The failure mode is **decision velocity outrunning
propagation, in a project whose entire quality mechanism is the written record.**
Q-67, Q-69 and Q-70 reversed the storage and tile architecture in a day;
Q-17a–c, Q-78 and Q-80–Q-84 tripled scope the next; four ADRs were written the
same day. Every one was recorded well *where it landed* and propagated badly
*everywhere else* — so ADR-001's decisive premise, ADR-010's invalidation window,
ADR-011's polling justification, ADR-005's own exclusion list, ADR-002's four
backends, and the entire Phase 0 assessment now describe a system that was
superseded, and a dedicated §63 sweep declared its blocker discharged while
walking past all of them. The register is the only thing standing between this
project and its scope, and the register has begun lagging the decisions it exists
to govern. If that gap keeps widening, implementation will begin against a
synthesis naming the wrong language, eight `ACCEPTED WITH CONDITIONS` ADRs whose
conditions have been formally reclassified as non-blocking, and assumptions
A-014, A-015, A-026 and A-027 that are load-bearing, unmeasured, and in the case
of A-014/A-015 mutually constraining in a band nobody has computed. **The
architecture will not fail because someone chose wrongly. It will fail because by
the time anyone tries to build it, no document will be able to say what was
chosen.**
