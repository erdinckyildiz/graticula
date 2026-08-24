# ADR-030 — Reading the reference implementation, not only its documentation

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` |
| **Decided** | 2026-08-16 |
| **Supersedes** | — |
| **Superseded by** | — |

---

## 1. Context

A checkout of a third-party GIS application server sits at
`C:\Personal\Projects\REFERENCES\Gecici\gecici-server`. It is the closest thing
to a peer this project has: .NET 10 over PostGIS, exposing GeoServices REST
(FeatureServer, MapServer, ImageServer, GeometryServer, GPServer), OGC API,
classic OGC WMS/WFS/WMTS/WCS, STAC, OData v4, MVT, 3D Tiles, gRPC and MCP from
one container. It is 4,422 C# files (36 MB), 2,114 test files (24 MB), 78 CI
workflows, 4,928 commits since 2025-12-17, and it has not tagged a `v*` release.

The checkout is **anonymised**. The product name was rewritten throughout with
`git filter-repo`, and the project owner confirmed on 2026-08-16 that the
`LICENSE` file is also not the real one. So the checkout's own identity fields
are not evidence: neither the name it carries nor the licence it declares may be
cited from it.

That is narrower than it sounds, and the difference is worth stating, because
this repository names the product it is a copy of — **Honua** — 48 times across
19 documents, including four register answers whose reasoning is explicitly
sourced to it (Q-16, Q-28, Q-78, Q-81) and [ADR-029](ADR-029-affinity-routing-is-not-the-default.md).
Those citations predate the anonymised checkout and are not affected by it. What
this ADR forbids is treating *this checkout's* scrubbed strings as fact. Whether
the registers should go on naming the product is a separate question and not
this ADR's to answer.

The question that forced this decision: may we read `src/`, or only `docs/`?

Two rules were being conflated, and the distinction matters:

- **Tier 3** ([build-vs-adopt-policy.md](../build-vs-adopt-policy.md)) forbids
  *adopting* a finished GIS server — embedding, forking, vendoring. It explicitly
  keeps such products valuable as "objects of study". It never restricted
  reading, and it is untouched by this ADR.
- **Clean room** is the rule in question. The governing specification, §5 of
  [MASTER_GIS_PLATFORM_PROMPT.md](../../MASTER_GIS_PLATFORM_PROMPT.md), forbids
  *reproducing* proprietary source, undocumented internals and proprietary
  algorithms. It does not forbid reading them. The prohibition on reading came
  from CLAUDE.md §2's tighter restatement — "studied for publicly documented
  behaviour and architectural reasoning only" — which is stricter than the
  document it summarises.

So this ADR does not override the governing specification. It removes a
tightening that CLAUDE.md had added on top of it, and it states what the
governing specification's actual line — *reproduce* — is taken to mean.

## 2. Alternatives considered

### Alternative A — Documentation only (the status quo)

**Argument for.** "We never opened the source" is a statement that can be
defended. It is the only version of the claim that survives someone else's
scrutiny, because it is about an act, not about an intention. The reference's
`docs/` tree already carries most of what is transferable: 74 ADRs, a five-tier
CI gate model, an evidence-based capability catalogue, conformance runbooks.
Those are the parts written to be read.

**Argument against.** It answers a question nobody asked. The prohibition
protects against *reproduction*, and refusing to read is a proxy for that, not
the thing itself. It also fails on its own terms: the conformance harness and CI
workflows recommended under a "documentation only" reading are themselves code,
so the line was already being crossed while being asserted.

### Alternative B — Black-box behavioural probing

**Argument for.** The reference runs from `docker compose up`. Observing its HTTP
responses produces exactly what v1 needs for ArcGIS compatibility — metadata
document shapes, error bodies, tile headers — as *measurements* rather than as
borrowed text, and measurements are what §3 asks for. Provenance stays clean.

**Argument against.** Behaviour is not reasoning. It shows what a correct answer
looks like and nothing about why the implementation is shaped the way it is,
which is the part that is expensive to work out. It is a complement, not a
substitute, and this ADR keeps it (see Decision).

### Alternative C — Reading permitted, reproduction not (chosen)

**Argument for.** It matches the governing specification instead of a stricter
paraphrase of it, and it puts the constraint where the risk actually is: on what
we write, not on what we look at. Copyright attaches to expression. Reading is
not reproduction, and a rule that treats them as identical spends real capability
to buy a claim we may not need.

**Argument against.** See §3 — the whole of it.

### Alternative D — Establish the real licence first

**Argument for.** It is the cheapest gate in the list and it can dissolve the
question. If the reference is AGPL or GPL, its code is compatible with this
project's copyleft position and could be *reused* with attribution, not merely
read. If it is Apache or MIT, likewise with attribution and a
[DEPENDENCY-LICENSES.md](../../DEPENDENCY-LICENSES.md) entry. Only in the
source-available-but-proprietary case does clean room bind at full strength.

**Argument against.** It blocks work on an answer the owner may not have to hand,
and it is not exclusive with Alternative C. Retained as a revisit trigger.

## 3. Counterarguments to the preferred option

**The asymmetry is not symmetric.** "We did not read it" is provable by absence.
"We read it and took only ideas" is unfalsifiable, and unfalsifiable in the
direction that hurts: the party asserting it carries the burden and has nothing
to discharge it with. This ADR spends that defence permanently. No later policy
change restores it, because the reading will already have happened.

**The licence is unknown, so the worst case is unassessed.** Deciding to read
before knowing what we are reading means accepting an unbounded rather than a
measured risk. This is the strongest argument against, and it is the one
Alternative D answers cheaply.

**For a gift, reputation is the exposure, not litigation.** This project is given
away. A credible public claim that its structure came from a commercial product
would end it regardless of whether any court agreed. Legal analysis is the wrong
frame for the actual failure mode.

**"Inspiration" has no observable boundary.** Nobody — including the person doing
it — can reliably tell reading-then-deciding from reading-then-transcribing. The
distinction this ADR relies on cannot be audited from the outside, which is why
the conditions below are about disclosure rather than about intent.

**The transferable value is lower than it looks.** [ADR-001](ADR-001-core-language.md)
has not settled the language. The reference's decomposition is substantially a
.NET-shaped answer — source generators, minimal APIs, Aspire, analyzer-enforced
module boundaries. Reading it before the language is chosen risks anchoring on
constraints that will not be ours.

**Scale invites the wrong lesson.** 36 MB of implementation covering every
protocol, and still no release. [docs/v1-scope.md](../v1-scope.md) cut to PostGIS
plus three services deliberately. Reading widely in `src/` is a standing
invitation to widen scope by imitation.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| The reference is a peer-scope product, not a library | 4,422 `.cs` files / 36.3 MB in `src`, 2,114 test files / 24.0 MB, 78 workflows | Measured on the checkout, 2026-08-16 |
| The checkout is current, not a stale mirror | `HEAD` = `2e7b161d7`, 2026-08-16 03:43 UTC | `git log -1` |
| Its identity fields are unreliable | Name rewritten via `git filter-repo` (`.git/filter-repo/commit-map`, 404 KB); owner states the `LICENSE` file is not the real licence | Owner, 2026-08-16 |
| Peer scope has not produced a release in eight months | "Versioned `v*` releases have not been tagged yet" | Reference `README.md` |
| The governing spec forbids reproduction, not reading | "Do not reproduce: proprietary source code, undocumented proprietary internals, protected implementation details, proprietary algorithms" | MASTER §5, L191–196 |
| Tier 3 already permits study | "They remain valuable as **objects of study** (§4, §16)" | [build-vs-adopt-policy.md](../build-vs-adopt-policy.md) L88–90 |

## 5. Decision

Reading the reference checkout is permitted in full, `src/` and `tests/`
included, for architectural learning. What remains forbidden is reproduction of
expression: no verbatim copying, no transliteration into another language, no
file-by-file reconstruction of their decomposition, no lifting of an algorithm
whose form is theirs rather than a standard's. The owner stated the same
boundary in deciding this — *"birebir alalım demiyorum"* — and it is recorded
here as the boundary, not as an aspiration. Black-box probing (Alternative B) is
adopted alongside, because measured response shapes are evidence in a way that
read code is not.

## 6. Consequences

**Positive.** The most detailed peer artefact available becomes readable at the
level where reasoning actually lives. Four specific transfers are already
identified — a generated, drift-gated capability catalogue; a five-tier CI gate
model with an explicit burden of proof for entering the PR lane; conformance
suites run as evidence-producing CI lanes; and a parser → AST → SQL seam. The
first two bear directly on live debts: the §66 gates — 0 of 9 run when this ADR was written —
whose live tally is the §66 table in
[architecture-completeness.md](../architecture-completeness.md) — and the
condition count that [tools/conditions.py](../../tools/conditions.py) computes
but no gate enforces.

**Negative.** The independent-creation claim is weakened permanently and cannot
be restored. The licence is unknown, so the exposure is unbounded rather than
bounded. Reading a 36 MB implementation is a time sink with no natural stopping
point. Anchoring on a .NET-shaped architecture before ADR-001 is a live risk.
Scope creep by imitation is a live risk.

**Ports created.** None. This ADR adopts no dependency; Tier 3 stands unchanged.

**Conditions.** Three, in §11 — where [tools/conditions.py](../../tools/conditions.py)
can find them.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | The reference's licence, once known, does not forbid what has already been read | UNVALIDATED — this is the exposure named in §3 |
| — | Reading-then-deciding is distinguishable from reading-then-transcribing by whoever reviews our code | UNVALIDATED, and unverifiable from outside; condition 1 exists because of it |

Both are new and belong in
[architecture-assumptions.md](../architecture-assumptions.md) with IDs on the
next pass.

## 8. Dependencies

**Depends on:** nothing. The decision is the owner's and rests on no other ADR.

**Depended on by:** [ADR-001](ADR-001-core-language.md) — anchoring risk if
read before the language is settled. Any future ADR citing the reference as
evidence inherits condition 1.

## 9. Revisit triggers

- The real licence becomes known and is incompatible with distributing this
  project under GPL/AGPL — reopen immediately, and audit every entry in the
  reading log against what was written afterwards.
- Any reviewer, internal or external, cannot articulate what distinguishes a
  named subsystem of ours from the corresponding one there.
- The reading log accumulates entries whose "where it was written down" column
  is empty — the conditions are not holding, so the decision they justify does
  not hold either.
- A scope decision is argued from the reference's feature surface rather than
  from [v1-scope.md](../v1-scope.md).

## 10. Dissent

Recorded, and it is mine (Claude, as the agent that argued the other side before
the decision). I argued for Alternative A and then Alternative D: the provability
asymmetry in §3 is not answered by any condition in §6, and deciding to read
before establishing the licence accepts an unbounded risk in exchange for
learning that is partly non-transferable while ADR-001 is open. The owner
weighed that and decided otherwise, on the grounds that the rule as written
protected against reproduction and was being applied to reading — which is
correct, and is why this ADR is `ACCEPTED` rather than `REJECTED`. The dissent
is not withdrawn; it is the reason conditions 1 and 2 exist, and the reason the
first revisit trigger is written the way it is.

## 11. Conditions

1. **Disclosure.** Any ADR, register entry or production change whose reasoning
   was informed by reading the reference says so in its own text. An undisclosed
   derivation is the failure this ADR exists to make visible, and it is the only
   one no reviewer can catch from the outside.
2. **Reading log.** Reads are recorded in
   [reference-reading-log.md](../research/reference-reading-log.md): what was
   read, what was taken, and where it was written down. A read with no
   corresponding entry is a process failure, not a private matter. The log's
   value is the last column — an entry that took nothing is worth recording as
   such.
3. **Standards before source.** Where a behaviour is specified publicly — the
   ArcGIS REST API documentation, an OGC specification — that specification is
   the citation. The reference may confirm a reading of a spec; it may not become
   the source for one. Black-box measurement (Alternative B) is the preferred
   route wherever it can answer the question, because it produces evidence
   instead of provenance.
