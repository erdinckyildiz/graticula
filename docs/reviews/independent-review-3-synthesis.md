# Independent Review 3 — Synthesis and Dispositions

**Run 2026-08-13.** Three reviewers, no shared context, no access to the
conversation or reasoning that produced the architecture. **41 findings, 17
severe.**

| Lens | Findings | File |
|---|---|---|
| Architectural coherence | A1–A12 | [architecture](independent-review-3-architecture.md) |
| Operability and failure | O1–O15 | [operations](independent-review-3-operations.md) |
| Process, scope, evidence | P1–P14 | [process](independent-review-3-process.md) |

---

## 1. This does not discharge §67

§67 requires a reviewer who **did not participate**. The author of the
architecture commissioned these reviews, wrote their briefs, and is recording
their results. Blind spots in *reasoning* were removed — none of the three could
see why anything was decided, only what is written, which is exactly the failure
mode of self-review. Blind spots in *framing* were not removed.

The process reviewer reached the same conclusion unprompted and said so in P8:
**"Do not tick that box on the strength of this document."**

Recorded as round 3 of the review series. **Round 4, with a reviewer who did not
participate, remains owed.**

---

## 2. Three reviewers converged on one diagnosis, independently

None saw another's output. All three named the same root cause in different
words:

> **Architecture:** *"Decision velocity outrunning propagation, in a project
> whose entire quality mechanism is the written record… The architecture will not
> fail because someone chose wrongly. It will fail because by the time anyone
> tries to build it, no document will be able to say what was chosen."*

> **Operations:** the fatal scenario is *"an upgrade… A product that eats a
> customer's authoritative data while they are following its own upgrade
> instructions does not get a second maintenance window."*

> **Process:** *"this project's problem is not that it cannot see its own
> defects. It is that seeing them has become a substitute for acting on them, and
> nothing in the process converts an honest observation into a cut."*

**The convergence is the finding.** Three independent readers, given different
briefs, arrived at variations of *the record is falling behind the decisions*.

### 2a. The specific mechanism, which was invisible from inside

[contradiction-sweep-1.md](contradiction-sweep-1.md) found eleven contradictions
and discharged blocker B1. **All eleven were forward-facing** — scope added
without propagating. **Zero were backward-facing** — premises deleted without
propagating.

The backward-facing class is larger, more mechanical, and includes every one of
A1, A2, A5, A6, A7, P11 and P14. The sweep read `product-context.md` and caught
one stale row out of five.

**A sweep that discharges its blocker while walking past a self-contradicting ADR
produces false assurance, which is worse than no sweep.** B1 is reopened.

---

## 3. Dispositions

`APPLIED` — done. `OPEN` — council work, queued. `[OWNER]` — needs the project
owner. `ACCEPTED` — the finding is correct and the situation stands, recorded
rather than fixed.

### Already applied

| # | Finding | Disposition |
|---|---|---|
| **P2** | Nine questions filed as answered, three of them the severe sweep findings | **`APPLIED`** — eight rows moved back to open, commit `09ff536`. Q-85, Q-86, Q-87, Q-82, Q-79, Q-74, Q-75, Q-77. **This was the most damaging finding in the set**: the register was reporting the sweep's own severe findings as closed, making them invisible to the next sweep. Cause: new questions inserted with edits anchored on rows that had drifted into the Answered table, never checked |

### The free win

| # | Finding | Disposition |
|---|---|---|
| **A5** | `LISTEN`/`NOTIFY` recorded as unavailable, citing a superseded section, producing a documented disclosure window and the standing argument for Redis | **`OPEN`, highest value per unit effort.** Q-70 made PostgreSQL mandatory, so the constraint is simply gone. Removing it closes a **correctness window for permission changes** that ADR-010 §8 says *"should stay uncomfortable"*, and removes an argument for a dependency CLAUDE.md §6 challenges. Touches ADR-010 §7/§8, ADR-011 §3.3, data-model, Q-30 |

### Propagation debt — one remedy, many symptoms

| # | Finding | Disposition |
|---|---|---|
| **A2** | `architecture-assessment.md` describes an architecture that no longer exists — wrong language basis, wrong ADR count, four platform stores, ADR-003 `DRAFT` | **`OPEN`, blocking.** It is a §81 criterion and every §66 gate would run against it |
| **A6** | ADR-005 §3.3 contradicts §3.3a forty lines above; ADR-010 §1, ADR-011 §3.2/§3.3/§5/§7, ADR-002 §6/§7/§9 all carry deleted premises | **`OPEN`** — backward-facing sweep, §2a |
| **A7** | Statuses are labels not gates: work proceeds inside `REOPENED` ADRs from outside them | **`OPEN`** — ADR-006 and ADR-009 must absorb what ADR-015/016/017 already decided for them, or be re-closed |
| **P11** | Four stale rows in `product-context.md`, the document CLAUDE.md makes authoritative | **`OPEN`** — datastore optional/mandatory, compatibility surface, licence row, data-ownership TBD |
| **P14** | Exit plan §1's headline numbers are stale and the matrix cites them as its §81 verdict | **`OPEN`** — 17 ADRs not 12, 48 open questions not 44 |

### ADR-001 — the most exposed decision in the repository

| # | Finding | Disposition |
|---|---|---|
| **A1**, **P7** | The stated reason for skipping the prototype was voided the same day by Q-67; the ADR was never reopened; it is the only unconditionally `ACCEPTED` decision and the one CLAUDE.md §7 required a prototype for; `experiments/lang-slice` has a README and no code; its A-001 row says `VALIDATED` in text and `UNVALIDATED` in status | **`OPEN`, and the status is not defensible as written.** Minimum honest action: fix the contradictory row, record that reason 1 is dead, and change the status to `ACCEPTED WITH CONDITIONS` / `LOW` or `REQUIRES PROTOTYPE`. **Whether to actually run the comparison is `[OWNER]`** — it is the one decision the rules demanded evidence for, and .NET may still be right |
| **P5** | The two criteria ADR-001 itself rated `Critical` — C6 streaming, C8 driver quality — are unmeasured on any engine, while three rounds measured C1, which the same ADR downgraded to `Medium` | **`OPEN`** — `benchmarks/feature-query` does not exist and the day-one workload has never been measured, on any engine including PostGIS |

### Evidence and status hygiene

| # | Finding | Disposition |
|---|---|---|
| **A3** | Every condition-discharging experiment except the tile benchmark is absent, and the exit plan reclassified conditions as non-blocking | **`ACCEPTED` as fact, `OPEN` as labelling.** Deferring measurement is defensible; **`ACCEPTED WITH CONDITIONS` whose conditions are permanently deferred is `ACCEPTED`**, and the register should say which it means |
| **P6** | No performance targets exist anywhere, so A-019's *"meets our latency targets"* and ADR-001's *".NET misses the absolute targets"* are both unfalsifiable. One measurement is cited 16 times across 10 documents, including for attachments where its allocation profile does not transfer | **`OPEN`** — `performance.md` is a stub and its own text says *"a target without a benchmark is an aspiration"* |
| **P12** | A-004 validated against the worst alternative rather than the best; A-019's Validated row still carries a claim its own run 3 retracted; A-021 holds two statuses in one row against the register's own splitting rule; A-016 validated by fiat | **`OPEN`** — four re-ratings |
| **A8** | ADR-003 claims `HIGH` confidence while Q-68 asks whether tier 2 should exist at all, and §6b's hot-path condition was not re-applied after tier 1 took the hot path | **`OPEN`** — confidence to `MEDIUM` pending Q-68 |

### Upgrade and recovery — the fatal path

| # | Finding | Disposition |
|---|---|---|
| **O1** | ADR-016 §4's exact-version refusal contradicts §5/§6's expand-and-contract; between them, rolling upgrade and the documented rollback both cease to exist | **`OPEN`, severe, and it is a defect in a decision accepted the same day.** Fix is a compatibility **range**, not equality. Also unstated: restore-from-backup discards every write since the backup |
| **O2** | Backup and restore has no design; three decisions depend on it; `deployment.md` is a stub; the volume grows without bound | **`OPEN`, severe.** The composed scenario in O1+O2+O8 is unrecoverable data loss while following documented instructions |
| **O8** | The degraded admin surface reports every signal green during the disk-full failure that caused the outage | **`OPEN`** — the supervisor watches certificate expiry and not disk |
| **A9** | ADR-003 defers cross-engine consistency to "the second provider"; ADR-008 condition 1a schedules the forcing function for Phase 1. Neither references the other | **`OPEN`** |
| **P10** | F1's severe disposition survives only as a condition the project has decided it cannot meet, while the record says all dispositions were applied | **`OPEN`** — and Q-80/Q-81 raised the dialect count from three to six, making it more necessary |

### Security

| # | Finding | Disposition |
|---|---|---|
| **O3** | User-supplied Python runs in the job worker with no sandbox, alongside GDAL, credentials and unrestricted egress; the SSRF allow-list covers only the COG proxy; and *"administrators only"* is not a control because **Q-59's role set is undefined** | **`OPEN`, severe.** Q-75 is the sandbox; the role-set dependency is new and must be recorded on it |
| **O4** | The break-glass path's bounding condition is **attacker-inducible** — anyone who can saturate the datastore turns the bypass on; and its credential is by construction a static shared secret with none of the three properties ADR-015 chose opaque tokens to obtain | **`OPEN`, severe.** ADR-017 recorded it as *"the classic shape of an authentication bypass"* and missed that the gate is attacker-controlled |
| **O5** | No data-plane rate limiting, no per-principal cost accounting, anonymous first-class, and an unbounded cache key space — so one unauthenticated script makes admission control reject **everybody** | **`OPEN`, severe.** No endpoint identifies the offender and no lever blocks it |
| **O6** | ADR-015's central argument is undermined by ADR-007 §4.3, which caches effective authorization inside a pinned service context with no invalidation path for a grant change | **`OPEN`, severe** |
| **O7** | The secret-encryption key is missing from the *"completed"* state inventory, has no rotation or key-versioning design, and its loss makes every registered credential undecryptable | **`OPEN`, severe** |
| **O14** | Unwrapped provider errors are required by ADR-017 §3.3 and forbidden by security.md §5's D-03; job-log read authorization is undefined | **`OPEN`** |
| **A11** | GeometryServer publishes the worst-measured operation on the request path with caller-chosen input size, names the required caps, and gives no numbers, no condition and no assumption ID | **`OPEN`** — compare A-041, raised for the structurally identical attachment problem |

### Operational gaps

| # | Finding | Disposition |
|---|---|---|
| **O9** | No connect timeout, socket timeout or keepalive anywhere; a **dropping** firewall produces a hang beneath the statement timeout, so the circuit breaker never trips and health reports `ACTIVE` | **`OPEN`** |
| **O10** | Runaway in-process work has no cancel path; the only lever is a whole-worker recycle that kills every other tenant, and the offending feature cannot be removed without dropping the layer | **`OPEN`** |
| **O11** | Stale-while-error's *"not negotiable"* exception depends on a polled invalidation, so it fails exactly when it matters. Also: §4's key-change means entries are **orphaned rather than purged**, so *"a purged entry stays purged"* describes a mechanism that does not exist | **`OPEN`** — largely dissolved by fixing A5 |
| **O12** | Clock skew is acknowledged as unwalked and is load-bearing in five mechanisms, including certificate expiry — the one the architecture called most predictable | **`OPEN`** |
| **O13** | ADR-007 condition 4 requires manual override for every adaptive behaviour; ADR-017 designed no such endpoint, and the one available lever accelerates the failure it would be used on | **`OPEN`** |
| **O15** | Air-gapped patching is transferred wholesale to the customer with no CVE process, no SBOM and no security contact | **`OPEN`** — Q-72 |

### Scope, and the decision nothing in the process will force

| # | Finding | Disposition |
|---|---|---|
| **P3** | The committed scope is not achievable, and **no document anywhere states who is going to build it** | **`[OWNER]`.** The reviewer names the cut: the observation store and 3D/terrain, the geocoder, MySQL/MariaDB/DuckDB, GPServer and the Python sandbox, ImageServer's expensive half, and Tier B/C/D of the protocol surface. *"What survives is v1 as competitive-position §6a already defines it — the ArcGIS exit path… The documents contain the right answer; nothing in the process forced the choice"* |
| **A12** | Scope tripled against an evidence base of one subsystem, and the exit plan's *"none of it changes §3"* let it pass without a capacity check because it does not move the nearest milestone | **`[OWNER]`**, with P3 |
| **A4** | Affinity routing is correct only inside a band nobody has computed, and A-015 either makes it unnecessary at 1,000 services or recreates the per-service allocation that killed ArcSOC | **`OPEN`** — the possibility that the flagship mechanism is *over-built* is nowhere considered |
| **A10** | The compatibility layer is now more capable than the product it wraps; §51's boundary was crossed by accretion in four places | **`[OWNER]`** — either §51 is amended or the drift is reversed |
| **P4** | Dissolving Q-49's exit criterion silently removed the validation path for A-003, A-008, A-015, A-025, A-028, A-002 and A-033 — including *"the load-bearing assumption under the shared-worker model"* | **`[OWNER]`.** The market reasoning was right; the criterion was carrying a second load nobody noticed |

### Process

| # | Finding | Disposition |
|---|---|---|
| **P1** | §6–§9's council, independent analysis, structured debate and separate Architecture Judge were never instantiated. Every status and confidence is self-assigned by the author of the decision. Two dissent sections still read *"To be recorded during the debate round"* | **`ACCEPTED`, and it must be stated where a reader will see it.** The project admits this for §67 and not for §6–§9 |
| **P8** | The exit gate was relaxed in a document that disclaims being a decision, contrary to CLAUDE.md §2's *"no informal decisions"*; the four code-free §66 gates it scheduled were never run; the promised question sweep was not done and the count went **up** | **`OPEN`** — either an ADR records the amendment, or §81 stands as written |
| **P9** | Rule 1 of the licensing register was broken by four accepted ADRs; CLAUDE.md §7 still states the opposite of Q-73; `product-context.md` contradicts itself inside one row; D-06 was written as a rebuttal of the rule it defers | **`OPEN`** — the deferral may be right, the framing is not |
| **P13** | Zero of ~25 conditions discharged; seven §68-mandated documents are stubs, including `service-runtime.md` which §18 requires **in addition to** ADR-007 | **`OPEN`** |

---

## 4. What this changes about readiness

The six blockers are still discharged and that stands. But
[phase-0-exit-plan.md](../phase-0-exit-plan.md)'s answer — *implementation is
about three sessions away* — was computed from a §1 summary that is itself stale
(P14), against a register that was reporting live questions as closed (P2).

**Recommended order:**

1. **The free win (A5)** and the **backward-facing propagation sweep** (A2, A6,
   A7, P11, P14). Mechanical, high value, and it restores the record's
   trustworthiness — which is the precondition for everything else.
2. **ADR-001's status honesty** (A1, P7). The fix is small; whether to run the
   comparison is the owner's.
3. **O1 and O2** — the upgrade and backup path, because the composed failure is
   unrecoverable data loss during a documented procedure.
4. **The `[OWNER]` cluster** — P3's cut, Q-85, A10's §51 boundary, P4's
   validation path.
5. **Round 4**, by someone who did not participate.

**Nothing in this document forces step 4, which is the point P3 was making.**
