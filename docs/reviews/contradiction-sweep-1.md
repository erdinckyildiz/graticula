# Contradiction Sweep 1 (§63)

**Run 2026-08-13.** Blocker **B1** in the [Phase 0 exit plan](../phase-0-exit-plan.md).

Trigger: three owner decisions reversed within hours of each other on 2026-08-12
(Q-67, Q-69, Q-70), followed on 2026-08-13 by roughly a tripling of scope across
Q-17a–c, Q-78 and Q-80–Q-84. The owner found the first defect unaided by pointing
at a question number, which was the argument for running this.

**Eleven findings. Three severe.** Severity is *how wrong the record is*, not how
hard the fix is — two of the severe ones are edits, and the third is a rule we
stopped following.

---

## S1 — SEVERE · Vector-first is still recorded as an owner decision, and it is no longer true

**Evidence.** [product-context.md](../product-context.md) decisions table:

> **Rendering** — Vector-first. No server-side raster tiles. WMS in the
> compatibility layer only. Raster imagery catalogued, not rasterised.

And §205, *Rendering posture — vector-first, the client renders*.

**Against.** Q-17c puts **ImageServer** in scope, which rasterises. Q-78 puts
**OGC API Maps** in scope, which renders. Q-83 flipped *server-side rendering and
legend* to in-scope. ADR-009 is `REOPENED` for exactly this reason.

**The failure mode is the interesting part.** Vector-first was never reversed. It
was *out-voted one capability at a time*, and the headline was never revisited
because no single decision contradicted it outright. This is the drift
[CLAUDE.md](../../CLAUDE.md) §2's `INFERRED` rule was added to prevent, arriving
from the opposite direction: not an inference recorded as fact, but a fact left
standing after the inferences beneath it were removed.

**Disposition.** Amend product-context to record vector-first as **superseded by
accumulation**, with the list of decisions that did it, and state the replacement
posture: *the client renders by default; the server renders when a protocol
requires it.* Applied below.

---

## S2 — SEVERE · Capabilities are in scope whose enabling ADR is `DEFERRED`

**Evidence.** [ADR-004](../adr/ADR-004-rendering-engine.md) status, as of today:

> `DEFERRED` — confirmed 2026-08-12 and **re-confirmed 2026-08-13**.

Meanwhile MapServer, OGC API Maps and server-side rendering are all recorded as
in scope by Q-78 and Q-83.

**A deferred ADR cannot support in-scope capabilities.** The completeness matrix
and the ADR register now disagree with each other, which is precisely the
condition §63 exists to detect.

**Note the ADR-004 re-confirmation happened *before* Q-78 and Q-83**, so this is
not the owner contradicting themselves in one breath — it is two decisions taken
an hour apart with nothing connecting them.

**Disposition.** `[OWNER]` — one of two things must be true, and only the owner
can say which:

1. Rendering is genuinely in v1, and ADR-004 moves from `DEFERRED` to `DRAFT`
   with the Q-77 Tier 1 line as its first section; or
2. Rendering stays deferred, and MapServer / OGC API Maps / ImageServer's
   expensive half are recorded as **in scope but explicitly not v1**, gated on
   ADR-004.

**Recommendation: (2).** It preserves the walking skeleton and matches the
sequencing already written into [protocol-surface.md](../protocol-surface.md) §5.
Raised as **Q-85**.

---

## S3 — SEVERE · §82 was applied to a list, not to capabilities

**Evidence.** [CLAUDE.md](../../CLAUDE.md) §6 is a binding project rule:

> For every proposed technology, answer: *what concrete problem does this solve?*
> If the answer is unclear, it does not go in.

Q-78 moved sixteen protocols into scope in one decision. Q-83 moved every
remaining row. **Neither answered §82 per capability.** For several — SensorThings,
EDR, 3D Tiles, OData, gRPC, MCP, PMTiles, WPS — no answer is recorded anywhere.

**This is not an argument to reverse anything.** Parity is a legitimate §82
answer wherever a real client speaks the protocol, and for OGC API Tiles or
Styles the answer is obvious. The defect is that the question was answered *per
list* when the rule requires *per technology* — and a list-level answer is
exactly how the weakest item in a bundle inherits the strongest justification.
That is Q-17a's failure at fifteen times the scale.

**Disposition.** Owed: a one-line §82 answer against each newly-scoped
capability, in [protocol-surface.md](../protocol-surface.md). Some will read
*parity; no known client in the target market* — which is weaker, honest, and
useful when the schedule needs cutting. Raised as **Q-86**.

---

## S4 — MODERATE · DuckDB may put GDAL back in the serving container

**Evidence.** [ADR-009](../adr/ADR-009-raster-engine.md) states flatly:

> **The serving container ships no GDAL.** It exists only in the job worker.

A-016 records the same as a rule adopted rather than a hope tested, and
[ADR-001](../adr/ADR-001-core-language.md) §C7 leans on it.

**Against.** Q-81 adopts DuckDB as the **file-format query engine**. DuckDB's
spatial extension bundles GDAL. If file-backed layers are queried on the request
path, GDAL is in the serving container again — through a side door, and without
the decision ever being taken.

**What it costs if unnoticed:** A-016 is invalidated, ADR-001's C7 argument
weakens, the air-gapped checklist (Q-15) grows a GDAL driver bill of materials it
did not have, and the attack surface argument for the split is void.

**Disposition.** Decide where DuckDB executes. **Recommendation: job worker only**
— files are converted at registration, which is already ADR-009's model for
raster and Q-52's model for import formats. Serving a file directly is a
convenience we have not justified. Raised as **Q-87**.

---

## S5 — MODERATE · Two capabilities are recorded as in scope *and* as pending confirmation

**Evidence.** Q-78 moved SensorThings and 3D Tiles to in-scope. Q-79, raised
minutes later, asks whether they were chosen or swept in by pointing at a list.
Both states are on the record simultaneously.

This one is mine, not the owner's — a bookkeeping error made while recording the
owner's decision.

**Disposition.** Marked `IN SCOPE — PENDING CONFIRMATION (Q-79)` in both places
rather than in-scope in one and questioned in the other. Applied below.

---

## S6 — MODERATE · Database driver licensing now conflicts with the outbound licence · **DEFERRED to D-06**

**Evidence.** [DEPENDENCY-LICENSES.md](../../DEPENDENCY-LICENSES.md) on the
Oracle driver:

> **Highest-risk row in this file.** Oracle client libraries have historically
> carried redistribution restrictions… our right to *ship a driver* is ours to
> verify.

That was written when **the project had no outbound licence**. It now has
**Apache-2.0** (Q-73), which warrants to every downstream user that they may
redistribute freely. Shipping a driver we may not redistribute breaks that
warranty for everyone who forks us.

**And Q-80 just multiplied the row.** MySQL has two .NET drivers with very
different licences — Oracle's official `MySql.Data` is GPLv2-with-exception,
which Apache-2.0 cannot sublicense; the community `MySqlConnector` is MIT and
can. Choosing wrongly is a licence violation, not a preference. `VERIFY` both,
and Oracle's current `Oracle.ManagedDataAccess.Core` terms, before implementation.

**Disposition — deferred by the owner, 2026-08-13, and correctly.** Licensing constrains what we may *ship*, not what we may *decide*, and nothing ships in Phase 0. Verifying now would also be work against a dependency list that moved three times the same day. **Moved to [D-06](../architecture-debt.md)** with a concrete repayment trigger — *before the first binary that bundles a database driver, a GDAL build or a Python wheel set* — rather than left as an open finding. The likely resolution is unchanged: drivers become customer-supplied rather than bundled, which is a Q-71 packaging consequence. The annotations in DEPENDENCY-LICENSES were applied and stand as a `VERIFY` note for that day.

---

## S7 — MINOR · Q-51's recorded reasoning is void, though its conclusion survives

Q-51 cut SQL Server and Oracle as platform stores because *SQLite already solves
the no-PostgreSQL requirement completely*. Q-70 deleted SQLite. The conclusion
still holds — they are not platform stores because there is exactly one platform
store — but the reason on the record is now false, and a reader arriving at Q-51
would be misled.

**Disposition.** Annotated at the Q-51 row. Applied below.

---

## S8 — MINOR · "We are narrower than GeoServer" is now false as a scope claim

[competitive-position.md](../competitive-position.md) §6 was written this morning
and says, correctly for its moment, that on the axes GeoServer competes on we are
narrower. After Q-78 and Q-83 that is true of what is **shipped** — nothing — and
false of what is **scoped**. The sentence reads as a scope claim.

**Disposition.** Qualified in place. Applied below.

---

## S9 — MINOR, but it moves the plan · B2's estimate is stale

The [exit plan](../phase-0-exit-plan.md) §3 estimates *ADR-003 out of `DRAFT`* at
one session, on the grounds that runs 1–3 mostly answered it. Since then Q-20 and
A-043 put **six geometry engines** in play. ADR-003 must now also state a
cross-engine consistency position, which runs 1–3 say nothing about.

**Disposition.** Estimate raised and the reason recorded. Applied below.

---

## S10 — MINOR · Provider symmetry is broken and the data model does not say so

[data-model.md](../data-model.md) §4 states data editing works *through our API
where we have write rights*. Q-82 recommends read-only for MySQL, MariaDB and
DuckDB **regardless of rights** — a capability difference by engine, not by
permission. The layer-modes table would tell an administrator otherwise.

**Disposition.** Deferred until Q-82 is answered; noted so it is not lost. If
Q-82 lands read-only, the table needs an engine column.

---

## S11 — MINOR · "Never degrade silently" is cited as covering something it cannot

ADR-008 §2's principle is quoted in several places as the general answer to
provider capability differences. A-043 established that six geometry engines can
return **different answers** to the same *supported* operation. A capability
report cannot express that, because every engine claims `intersects`.

ADR-008's own amendment says this. The documents citing the principle do not.

**Disposition.** No edit yet — the honest fix is a per-provider conformance
statement, which does not exist. Folded into Q-20.

---

## What the sweep says about the process, not the architecture

Three of the eleven (S1, S2, S5) exist because **decisions were taken faster than
their consequences were propagated.** None is a bad decision; each is a good
decision with an un-updated neighbour. The register worked — every one of these
was findable by reading the documents against each other, and none required new
information.

S3 is different and worth separating. It is not a propagation failure, it is a
**rule that stopped being applied** under scope pressure. §82 is one of the few
binding rules in CLAUDE.md and it was skipped for roughly thirty capabilities in
two messages. That is the finding to watch, because the next sweep cannot catch
it — a missing justification looks exactly like a justification nobody wrote down.

## Findings raised as questions

| Finding | Question | Owner |
|---|---|---|
| S2 | **Q-85** — is rendering in v1, or in scope but gated on ADR-004? | **[OWNER]** |
| S3 | **Q-86** — §82 answer per newly-scoped capability | Council |
| S4 | **Q-87** — where does DuckDB execute: job worker, or request path? | Council |
