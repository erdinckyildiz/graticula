# §66 Performance gate — run 2026-08-15

**Result: FAIL, and nothing was repaired.** Four findings, none of them a defect
in the code. The gate fails because the architecture's performance story rests on
one measured path, and the most-used path in the product has never been measured
at all.

---

## The question this gate asks

Not *is it fast* — four benchmark rounds already answered that for two paths, and
their results are sound. [architecture-completeness.md](../architecture-completeness.md)
posed the gate as: *"Evidence exists and the gate does not. What is missing is
the architecture-wide pass that asks what they imply together."*

So: **what do the four rounds imply together, and what does the architecture
depend on that none of them touched?**

---

## What the four rounds established, read as one body of evidence

| | |
|---|---|
| **Allocation is the ceiling, not CPU** | 80.9% GC pause at **18% CPU utilisation** (A-037, run 3). A z12 tile allocated **404 MB** (finding 10). ~139 bytes per vertex, three to four copies of every coordinate |
| **Pushdown is structural, not tuning** | A z16 tile read **201,580 vertices to emit 2,080** (finding 12, A-021). Anything that does not push down is broken by construction, not merely slow |
| **Cost does not track input size for overlay** | A 6,408-vertex adversarial input cost **153 s and 16.7 GB**; a real 72,919-vertex polygon cost **312 ms** (finding 15). No cap on input bounds the work |
| **Single-request benchmarks cannot see a GC ceiling** | Ours vs `ST_AsMVT` was 1.5× at concurrency 1 and **3.4× at 16** (finding 13). At concurrency 1 the pause amortises across idle time |
| **Round-trip count is not what a tile costs** | 16× and 256× fewer round trips bought nothing measurable; the same workload allocated **124–245× more** (finding 14) |

**Read together, these say one thing: the cost of this system is memory traffic,
and it is invisible at concurrency 1.** Every one of the four rounds that looked
for CPU found something else. That is a property of the *architecture* — WKB into
an object graph and out again — not of the tile path, and it is why the findings
below are about where else that mechanism runs.

---

## F1 — The feature query path has never been measured

**Severity: high. Not repaired.**

Runs 1–4 measured tile generation and geometry overlay. **The word "query" does
not appear in either results document.** The FeatureServer query path — the
most-used surface in the product, the one ADR-008 is about, the one every ArcGIS
client hits first — has no measurement of any kind.

It runs the same mechanism the four rounds indicted: WKB out of PostGIS, into an
object graph, out as JSON. Finding 10's "three to four copies of every
coordinate" is not a tile fact.

**A rough black-box probe was run for this gate**, because a gate that only says
*somebody should measure this* is not evidence either. Concurrency 1 → 24, the
smallest possible payload (one feature, 1 KB), three interleaved passes:

| conc | req/s across three passes | best | p50 ms | p99 ms |
|---|---|---|---|---|
| 1 | 132, 128, 115 | 132 | 6.6 | 23.0 |
| 4 | 430, 339, 436 | 436 | 8.0 | 26.4 |
| 8 | 517, 543, 604 | 604 | 11.3 | 71.7 |
| 12 | 914, 603, **416** | 914 | 14.8 | 86.9 |
| 16 | 710, 807, 666 | 807 | 17.5 | 96.4 |
| 24 | 615, 753, 730 | 753 | 26.5 | 118.0 |

**What this supports: throughput plateaus at 5–7× for 24× concurrency, and p99
rises from 23 ms to 118 ms.** Sub-linear, on the cheapest possible query.

**What it does not support, and I claimed it before checking:** on a single pass
this looked like a *regression* past concurrency 8. Three interleaved passes show
run-to-run variance of up to **2.2× at one concurrency** (416 to 914 at 12), so
the apparent cliff was noise. The correction matters more than the number: this
machine cannot support a finer claim than *sub-linear*.

**The recommendation is instrumentation, not more probing.** Runs 3 and 4 could
say "GC pause 22.2% → 35.5%" because they measured inside the process. From
outside, no client-side probe can distinguish an allocation ceiling from a
connection pool limit from a contended host. The feature path needs the counters
the tile path already had.

---

## F2 — Two of the four rounds retired their own premise, and the evidence base
is smaller than its page count

**Severity: moderate. Recorded, not repaired.**

- **Runs 1 and 2** justified an in-process MVT encoder because `ST_AsMVT` is
  absent from SQL Server and Oracle. **Q-67 then made tiles PostGIS-only**, so
  there are no tile sources lacking it. A-019 stopped being load-bearing on the
  day run 3 landed.
- **Run 4** tested the one surviving argument for keeping the encoder anyway —
  read-once-encode-many — and killed it: 124–245× more allocation for no
  measurable gain.

So of four rounds, two measured a component that the architecture then decided
not to have. **Their findings about allocation, copying and pushdown transfer;
their headline comparisons do not.** A reader counting rounds overestimates the
evidence.

**What is genuinely good here, and worth recording as such:** the encoder is
**not in `src/`**. `RectClip`, `TileSimplify` and `MvtEncoder` exist only under
`benchmarks/harness/`. `CLAUDE.md`'s rule — experiments are disposable and never
promoted — held under exactly the pressure it was written for: a fast, working,
well-measured component that the architecture no longer needed. That is the
process working, and it is the reason this finding is about bookkeeping rather
than about dead code in the product.

---

## F3 — The numbers that govern the runtime are still chosen, not measured

**Severity: moderate. Recorded, not repaired.**

Every one of these is a number the architecture acts on, with nothing behind it:

| Number | Where | Status |
|---|---|---|
| **30 s statement timeout** | [D-08](../architecture-debt.md) | *"because a number was needed"*. Bounds the overlay worker, the tile build, every query |
| **Connection budget per provider** | [ADR-007](../adr/ADR-007-service-runtime.md) condition 3 | Undischarged. *"Must be produced with real numbers, per provider, before any deployment guidance is published"* — and §1 of [deployment.md](../deployment.md) was written today |
| **Catalogue read on every request** | [D-17](../architecture-debt.md) | Deliberate, and its cost has never been measured. It is one platform-store round trip on the hot path |
| **Attachment streaming under load** | A-041 | The bounded pool exists because a slow client holding a streaming connection is slowloris pointed at the connection budget. Never exercised |
| **Context weight distribution** | `benchmarks/worker-model` | Does not exist. [ADR-029](../adr/ADR-029-affinity-routing-is-not-the-default.md) reversed a design partly *because* it does not, and condition 2 requires it before the reversal is reconsidered |
| **Tile reprojection on a miss** | Q-96 | Measured at 3.5× and then not revisited after ADR-026 changed what a miss costs |

The statement timeout is the one to fix first: it is a single number that bounds
three separate subsystems, and none of them chose it.

---

## F4 — The harness has now been wrong three times

**Severity: moderate. This is a finding about the evidence, not the code.**

Recorded in the results documents:

1. **Run 1** — a PowerShell header read indexed into a string and returned ASCII
   character codes. Caught because a feature count of 52 was implausible.
2. **Run 2** — `GC.GetAllocatedBytesForCurrentThread()` in an async handler that
   resumes on a different pool thread, subtracting two unrelated threads and
   reporting **−14.8 MB** allocated. Caught because negative allocation is
   impossible.

And today, a third, caught differently:

3. **This gate's own first probe** reported the 1,000-feature query regressing
   past concurrency 8. It was running at **71–77 MB/s**, and a control
   measurement against a file-served glyph range put **the Python client's own
   ceiling at 66 MB/s**. The regression was the client. It was caught by checking
   rather than by being absurd — the number was entirely plausible.

The results documents already say *"both were found by a value being absurd
rather than by review, which is luck."* Three for three, and the third would have
survived review, because it looked right.

**The rule this suggests, and it costs one measurement:** every load result gets
a control run against a path the server barely touches, at the same concurrency
and payload size, before it is believed.

---

## What held

- **The four rounds' internal method is sound.** Interleaved runs, counters
  rather than wall clocks where it matters, and each document states its own
  limits at length. This gate found nothing wrong with them.
- **Their own "what this does not show" sections are unusually honest** and
  anticipate most of what a reviewer would ask: one machine, one city, warm only,
  PostGIS in WSL on 6 of 16 processors, absolute latencies unstable to ±40%.
- **The disposable-experiment rule held** under pressure (F2).
- **Today's single-flight change was measured before and after** — 12 builds to
  1 — with a harness that synchronises its clients, which is the discipline F4
  asks for.

---

## Disposition

| Finding | Action |
|---|---|
| F1 feature path unmeasured | **[D-30](../architecture-debt.md)** opened. Needs in-process instrumentation, not another black-box probe |
| F2 evidence base smaller than it looks | Recorded here. No action: the transferable findings do transfer, and the encoder correctly never shipped |
| F3 governing numbers unmeasured | The statement timeout (D-08) and the connection budget (ADR-007 condition 3) are the two that bound other subsystems |
| F4 harness wrong three times | Recorded. A control run against a server-cheap path, at the same concurrency and payload, before any load result is believed |

**The gate is recorded as FAIL** because F1 alone is disqualifying: a performance
review cannot pass an architecture whose primary surface has never been measured.
Nothing here says the server is slow — it says nobody knows, on the path that
matters most.

**And this is a self-review**, so §67's standing objection applies exactly as it
does to the others.
