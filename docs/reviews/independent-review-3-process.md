# Independent Review 3 — Process Discipline, Scope Realism, Evidence Hygiene

> **Provenance, and what this is not. Read before citing it.**
>
> Produced 2026-08-13 by a reviewer with **no access to the conversation,
> reasoning or history that produced this architecture** — only the repository
> and `MASTER_GIS_PLATFORM_PROMPT.md`. Briefed to check whether the project
> obeys its own binding rules, to cite file and section, and not to praise.
>
> **This does not discharge §67**, and this reviewer says so independently in
> P8: *"Do not tick that box on the strength of this document."*
> **Round 4 remains owed.**
>
> Findings reproduced as written. Dispositions:
> [independent-review-3-synthesis.md](independent-review-3-synthesis.md).

---

## P1 — SEVERE · The entire evidence-generating apparatus specified in §6–§9 was never built. One voice wrote everything, including both reviews of itself

§6 mandates a twelve-role council, §7 independent analysis before debate, §8
structured debate with recorded disagreement, §9 *"a **separate** Architecture
Judge"* assigning status and confidence. A repository-wide search for the role
names returns **zero hits outside the master prompt itself**.
`architecture-assessment.md` never uses the word "council".
`fresh-challenger-review-2.md` line 16: *"This review was written by the same
agent that made every decision under review."*

**Why it matters.** Every ADR status (`ACCEPTED WITH CONDITIONS` ×12) and every
confidence level is **self-assigned by the author of the decision, with no
judge**. Every "Dissent" section is the author steelmanning themself — and two
(ADR-001 §10, ADR-012 §10) still read *"To be recorded during the debate round"*,
a debate that never happened. The project admits this for §67 only; it does not
admit §6, §7, §8 and §9 were also never instantiated. The self-review's own
closing line is the tell: *"Treat its findings as real and its coverage as
suspect."* **Coverage is the whole point of a council.**

---

## P2 — SEVERE · Nine questions sit in the "Answered" table that are not answered, three of them raised as defects hours earlier

The Answered table's header has five columns. Eight rows have **four cells**,
because they were pasted in the *open*-table format: **Q-85, Q-86, Q-87, Q-82,
Q-79, Q-74, Q-75, Q-77**. Their "Date" cell holds `**[OWNER]**` or `Council`. A
ninth, **Q-84**, has "Council, with the reference-data choice **[OWNER]**" where
the date belongs. Their text confirms it — Q-85: *"**Recommendation: the
second**"*; Q-86: *"Owed: one line each"*; Q-75: *"The sandbox itself remains
open"*.

**Q-85, Q-86 and Q-87 are the three findings the contradiction sweep of the same
day escalated as open.** Four of the nine are `[OWNER]` escalations awaiting the
owner.

Additionally: **Q-52 and Q-33 each appear in both tables** with different
statuses; **Q-24 is marked "Closed" while still in the "Blocking Phase 0"
table**.

**Why it matters.** §61 is *"Do not hide uncertainty"*. This is worse than hidden
uncertainty — the reader who checks the register, the mechanism the project
trusts most, is told nine live questions including three severe contradictions
and the entire §82 debt are closed. **The three severe sweep findings are now
invisible to the next sweep.**

---

## P3 — SEVERE · The committed scope is not achievable, and nowhere is there any statement of who is going to build it

In two messages on 2026-08-13 the scope became: 29 protocol faces over 10
engines; six SQL dialects with six geometry engines; full ArcGIS FeatureServer
with `applyEdits`, GeometryServer, GPServer and ImageServer; a Python SDK,
sandbox and curated wheel set; a geocoder with locale-specific address parsing;
an IoT observation store and a 3D/terrain pipeline, both *"new products rather
than new endpoints"* with *"no foundation in anything decided"*; free migration
tooling; self-service publishing; conformance against 13 OGC suites; and later a
raster rendering engine plus a vector renderer with *"a better symbology"* than
SLD.

Against that: **zero lines of production code**; `architecture.md`,
`service-runtime.md`, `query-engine.md`, `rendering.md`, `raster.md`,
`deployment.md` and `performance.md` are **12–16-line stubs** although §68
mandates all of them; and the exit plan's own §1 says *"we have designed almost
nothing that user touches."*

I searched for any statement of team size, funding, staffing or delivery
capacity. **There is none.** The only sizing language is *"a multi-year
programme"* — with no subject.

**Why it matters.** For calibration, `competitive-position.md` §1 records
GeoServer at ~20 years with multi-vendor OSGeo governance, and Honua at 4,608
commits from a single vendor — doing a **subset** of this. The exit plan's *"None
of it changes §3"* is true only of the first line of code and is **the most
dangerous sentence in the document**: it lets a tripling of scope pass without a
capacity check because it does not move the nearest milestone.

**What would have to be cut:** the observation store and 3D/terrain entirely
(Q-79 already asks whether they were swept in — treat silence as yes); the
geocoder; MySQL, MariaDB and DuckDB as providers (three of six geometry engines,
and A-043 with them); GPServer, the Python SDK and the sandbox; ImageServer's
expensive half; and all of Tier B/C/D in `protocol-surface.md` §2. What survives
is v1 as `competitive-position.md` §6a already defines it — the ArcGIS exit path
on PostGIS/Oracle/SQL Server — **which is a coherent product. The documents
contain the right answer; nothing in the process forced the choice.**

---

## P4 — SEVERE · Answering Q-49 dissolved an exit criterion that was the only validation path for at least six assumptions

The criterion is ticked `[x]`: *"§81's requirement to test it with real GIS teams
is dissolved rather than met."*

Now read the assumption register's *How it gets validated* column:

- **A-003** (*most services are idle, making shared workers viable*) —
  *"Workload modelling; any real deployment telemetry we can obtain."* The
  register's own note: *"A-003 is the load-bearing assumption under the
  shared-worker model"* — under ADR-007, the flagship decision.
- **A-008** (*administrators will not hand-tune*) — *"Still needs a real
  operator's view."*
- **A-015** (*warm state is small*) — in "the load-bearing five".
- **A-025** (*a small number of pinned contexts suffices*) — *"Observe pin counts
  in real use."*
- **A-028** (*administrators declare volatility usefully*).
- **A-002**, **A-033**.

**Why it matters.** The reasoning for dissolving is about *market justification*
and is correct on its own terms. But the criterion carried a second load — **it
was the project's only scheduled contact with a real operator** — and that load
was dropped silently. §81 still requires *"Load-bearing assumptions validated —
at minimum A-003"*, which is unticked and now **has no route to being ticked**.

---

## P5 — SEVERE · The project measured the second workload three times and the first workload zero times, then ticked the benchmark criterion

Q-06b: *"Features first, then vector tiles."* `benchmarks/` contains exactly one
result set: `mvt-generation/RESULTS.md` — all tiles, all PostGIS.
`benchmarks/feature-query/` — named in ADR-001 §6 as the other thing that
*"replaces the comparison"* — **does not exist.** The matrix nevertheless reads
*"tile path done three times"*.

Sharper: ADR-001 §3's revised weighting makes **C6 streaming large result sets
`Critical`** and **C8 driver quality across three engines `Critical`**, and
explicitly **lowers C1 geometry to `Medium`** because *"the geometry engine is
largely not on the hot path."* **Every measurement taken since is of C1's
territory.** The two criteria the language ADR itself called critical are
unmeasured on any engine.

D-05 is honest that the feature path is unmeasured on SQL Server and Oracle. It
does not say the feature path is unmeasured **on PostGIS either**.

**Why it matters.** CLAUDE.md §3's standing challenge is being satisfied by
benchmarks of the wrong thing. **That is measuring what was reachable, not what
mattered.**

---

## P6 — SEVERE · There are no performance targets, so "validated" against them is unfalsifiable; and one measurement has become the project's universal performance authority

`docs/performance.md` is 14 lines: *"**Status:** STUB."* Its own text says *"A
target without a benchmark is an aspiration."* **No target latency or throughput
exists anywhere.**

Against that: **A-019** is `VALIDATED` on *"In-process MVT encoding **meets our
latency targets**"*. ADR-001 §9's primary revisit trigger is *".NET misses **the
absolute targets**."* Neither can fire.

And the one number that exists — 80.9% GC pause at 18% CPU — is cited **16 times
across 10 documents**. ADR-013 §4a uses it to argue that materialising an
attachment *"reproduces A-037's ceiling on demand"* — but run 3 measured churn of
millions of small `Coordinate` objects at ~139 bytes per vertex, **not
multi-megabyte byte buffers, which allocate on a different heap with a different
collection profile.** RESULTS.md caps its own claim: *"One workload shape… One
machine, one dataset, one city."*

**Why it matters.** The benchmark work is the best material in this repository
and its caveats are exemplary. The failure is downstream: **a bounded measurement
of one path has been promoted into a general law** and used to settle decisions
about rendering, attachments and overlay that it does not cover. This is the same
defect as an unevidenced claim, wearing a citation.

---

## P7 — SEVERE · ADR-001 is the only unconditionally `ACCEPTED` decision and it is the one the rules required a prototype for

CLAUDE.md §7, binding: *"**Language:** genuinely open. To be decided by evidence
in ADR-001, **including a prototype**."* Assessment §26 ranks
`experiments/lang-slice` **"First. Everything downstream waits on it."** §27 sets
Phase 1's entry requirement as **"ADR-001 decided by prototype."**

`experiments/lang-slice/` contains a README and no code. Status: **`ACCEPTED`**,
not `REQUIRES PROTOTYPE` — a state §9 provides for precisely this. Assessment
§27's entry requirement was **never amended**. §10 Dissent: *"To be recorded
during the debate round."* §7 lists **A-001 as `UNVALIDATED` while its own text
says `VALIDATED`**, and **A-016 as `UNVALIDATED`** where the register says
`VALIDATED` — breaking the register's recording rule 3.

Part of the substituted justification is *"A direct peer built this exact
workload in .NET with 4,608 commits behind it."* That is an existence proof, not
a comparison — and `competitive-position.md` §1 records that peer as having **4
stars and no release**.

**Why it matters.** The decision is self-sealing: the entire benchmark harness is
.NET, so the revisit trigger can only be evaluated against targets that do not
exist, using a harness that cannot compare. **The honest status is `ACCEPTED WITH
CONDITIONS` / `LOW`, or `REQUIRES PROTOTYPE`.**

---

## P8 — HIGH · The Phase 0 exit gate was relaxed informally, in a document that disclaims being a decision, and the four code-free gates it scheduled were never run

`phase-0-exit-plan.md` header: *"This document is an assessment, **not a
decision**."* §2 then asserts *"Several Phase 0 exit criteria cannot be met by
Phase 0"* and §6 concludes *"Phase 0 does not end when the questions run out."*
That is an amendment to §81 and to CLAUDE.md §1, which says: *"Every
architectural decision becomes an ADR. **No exceptions, no informal
decisions.**"* There is no ADR, and the completeness matrix imports the
assessment as its verdict anyway.

Step 1 required *"Run the §66 review gates that do not need code: correctness,
simplicity, consistency, licensing."* The matrix shows **all nine gates `—`**.
The licensing gate was not run but converted into debt (D-06). The mechanical
sweep of open questions was not done — **the count went up.** The revised
estimate silently drops all four gates and the sweep.

**Why it matters.** §66 says a gate failure *"must reopen relevant decisions."* A
gate that never runs can never reopen anything. Note also that this review does
**not** discharge §67 by the project's own standard: *"That is not the same as a
different person and should not be recorded as if it were."* **Do not tick that
box on the strength of this document.**

---

## P9 — HIGH · The dependency-licensing rule was broken by four accepted ADRs, and the debt register was used to legitimise the breach rather than record it

`DEPENDENCY-LICENSES.md` rule 1: *"**Before an ADR adopts a dependency, its row
here must be `VERIFIED`.**"* Every row is `UNVERIFIED`. Meanwhile ADR-003 adopts
NetTopologySuite, ADR-009 adopts GDAL, ADR-001 adopts .NET and Npgsql, ADR-016
commits to bundling a Python wheel set, and Q-80/Q-81 add three more drivers.
D-06's justification — *"Licensing constrains what we may **ship**, not what we
may **decide**"* — is a verbatim contradiction of the rule it defers.

Three further live contradictions:

- **CLAUDE.md §7 (binding) still reads** *"copyleft (GPL/AGPL) acceptable"*, while
  `DEPENDENCY-LICENSES.md` says GPL/AGPL *"cannot be linked into anything we
  ship"*. **The binding rules file states the opposite of the decision.**
- `DEPENDENCY-LICENSES.md` lines 13–15 retain *"There is therefore no exclusion
  pressure on dependencies"* — six lines below the paragraph reversing it.
- `product-context.md` contradicts itself **inside a single table row**: *"GPL and
  AGPL dependencies are now disqualified"* / *"No dependency is excluded on
  licence grounds."*

**Why it matters.** The deferral may be right for the phase; **writing it as a
rebuttal of the rule, in the register whose purpose is to stop compromises
becoming permanent, is not.**

---

## P10 — HIGH · F1's severe disposition survives only as a condition the project has decided it cannot meet, while the record says all dispositions were applied

`adversarial-review-1.md` F1: *"A second dialect compiler must exist in **Phase
1**… no query engine feature is complete until it compiles on two dialects."*
Applied as ADR-008 condition 1a. The matrix records round 1 as *"all dispositions
applied."*

Against that: **D-05** — *"the owner has declined to install them"*; RESULTS.md —
*"**Not installed and not being installed.**"* The exit plan's slice is
PostGIS-only with no note that condition 1a applies. B2b re-gates cross-engine
work to *"the **second** provider"*.

**Why it matters.** F1's argument was that between Phase 1 and Phase 4 the query
engine accumulates *"a year of PostGIS-shaped assumptions with nothing pushing
back."* Q-80 and Q-81 raised the dialect count from three to six. **The forcing
function is more necessary than when written and is now structurally blocked by a
resourcing decision recorded in a different document.**

---

## P11 — MODERATE · `product-context.md`, the document CLAUDE.md points at as the input to every ADR, carries at least four stale rows, and the sweep caught one

- **Datastore:** *"v1, PostGIS only, **optional**"* — Q-69 made it mandatory.
- **Compatibility surface:** *"**Not** MapServer, ImageServer, GeometryServer or
  GPServer"* — Q-17a/b/c put three of the four in.
- **Licence row** — self-contradicting (P9).
- **§"Remaining open items": `TBD` Data ownership model** — answered by Q-08 and
  Q-40 in the same file.

**Why it matters.** S1's insight was *"a fact left standing after the reasoning
beneath it was removed."* Four more instances sit in the same table. The sweep's
warning that *"the next sweep cannot catch it"* understates the problem: **the
first sweep did not catch what it could have.**

---

## P12 — MODERATE · Several assumption ratings are stronger than the evidence, or were never re-rated when later evidence undercut them

- **A-004** — *"`VALIDATED`, **decisively**"* on `RectClip` beating
  `NTS.Intersection` 63×. But finding 12 later measured the genuine best
  alternative — `ST_ClipByBox2D` — at 13× latency and 15× allocation, and ADR-003
  §6a puts pushdown **first**. **It was validated against the worst plausible
  alternative, not the best one**, and never re-rated.
- **A-019** — the Validated table still reads *"The multi-database promise is not
  hollow"* while RESULTS.md finding 13 says it *"was validated on a measurement
  that structurally could not detect its own most important failure mode"* and
  Q-67 removed its premise.
- **A-021** — one row, two statuses. The register's preamble says that *"is a
  signal to split the assumption"*. **Its own rule, unapplied to its most
  important row.**
- **A-016** — *"`VALIDATED` **by design decision**"*. That is validation by fiat,
  and Q-87 shows the rule was at risk before the ink dried.

---

## P13 — MODERATE · Nothing is unconditionally decided, no condition has been discharged, and seven mandated documents are stubs

Of 17 ADRs: **12 `ACCEPTED WITH CONDITIONS`, 2 `REOPENED`, 2 `DEFERRED`, 1
`ACCEPTED`** — and the unconditional one is ADR-001 (P7). F4 raised *"roughly
twenty-five conditions, zero discharged"*; the disposition was to **add a
failure-impact table**, not to discharge one.

§68's mandated set is largely unwritten: `architecture.md` (15 lines),
`service-runtime.md` (16 — §18 requires it **in addition to** ADR-007),
`query-engine.md` (12), `raster.md` (12), `rendering.md` (15), `deployment.md`
(16), `performance.md` (14).

**Why it matters.** Effort went to 17 ADRs and 13 research notes — **the
artefacts that feel like progress** — while the mandated deliverables that would
constrain them stayed empty.

---

## P14 — MINOR · The headline numbers in the exit plan, which the matrix cites as its assessment of §81, are all stale

§1: *"ADRs | **12 written**… 1 `DRAFT`"* — there are 17, none `DRAFT`, two
`REOPENED`. *"Open questions | **44 open**, 30 answered"* — actual is 48 and 48.
*"§81 exit criteria | 4 of 16 met"* is derived from this table. The rest of the
document was diligently updated with strikethroughs the same day; §1 was not.

**Why it matters.** **The most-quoted status summary in the repository is the
least maintained part of it.**

---

## If this project fails, will it be for technical or process reasons?

**Process — and specifically, the absence of anyone empowered to say no.** The
technical work is well above average: the tile benchmark is honest to the point
of self-harm (it records that its own harness was wrong twice, that a stated
hypothesis was falsified, and that a validated assumption was validated by a
method blind to its worst failure mode), the register caught A-009's reversal
exactly as designed, the sweep found real defects, and the geometry, runtime and
query decisions are reasoned at a level most shipped products never reach. None
of that will kill it.

What will kill it is that the scope roughly tripled in a single day, and every
rule written to arrest exactly that failed under exactly that pressure at exactly
that moment: §82 was answered per list instead of per capability for thirty
capabilities (the sweep caught this, then filed the remedy as "answered"); nine
live questions including that remedy were misfiled as closed; the exit gate was
relaxed in a document that disclaims deciding anything; the language decision was
accepted without the prototype three separate documents required; the only exit
criterion that would have put a real operator in front of the design was
dissolved on unrelated grounds, taking six assumptions' validation path with it;
and the review apparatus meant to catch all of this — a twelve-role council, an
independent judge, a fresh challenger — was never built, so the architecture has
been graded exclusively by its author.

**The documents already contain almost every finding above, stated more sharply
than I have stated some of them. That is the diagnosis: this project's problem is
not that it cannot see its own defects. It is that seeing them has become a
substitute for acting on them, and nothing in the process converts an honest
observation into a cut.**
