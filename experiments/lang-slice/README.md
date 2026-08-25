# Experiment: lang-slice — SUPERSEDED, not run

**Status:** SUPERSEDED 2026-08-12. The two-language comparison was **deliberately
not run.**
**Replaced by:** `benchmarks/mvt-generation` and `benchmarks/feature-query`,
which measure absolutes in one language instead of comparing two.
**Decision it was meant to settle:** [ADR-001](../../docs/adr/ADR-001-core-language.md),
now `ACCEPTED` as .NET on paper analysis and secondary criteria.

---

## Why it was not run

This experiment carried a condition written into it from the start:

> *"If we cannot commit to tuning both fairly, we should not run it and should
> decide ADR-001 on secondary criteria instead. That is a legitimate outcome."*

That condition was invoked deliberately. Three things moved after the experiment
was specified, all in the same direction:

- ~~**In-process MVT encoding became mandatory.** `ST_AsMVT` is PostGIS-only and
  Oracle and SQL Server are first-class (Q-50a), so CPU returned to the hot path
  and geometry access — Go's weakest criterion — gained weight.~~
  **This reason is dead, and has been since 2026-08-18 — struck 2026-08-25.**
  [Q-67](../../docs/open-questions.md) made tiles hosted-only and
  [Q-88](../../docs/open-questions.md) cut v1 to PostGIS entirely, so there is no
  tile source that lacks `ST_AsMVT` and the premise this bullet rests on no longer
  exists. It was left standing in the file that carried it for seven days, which is
  [D-130](../../docs/architecture-debt.md)'s propagation shape rather than a new
  mistake.
- **The single-binary story was restored for both candidates** by the rule that
  the serving container ships no GDAL (Q-28), neutralising Go's strongest
  advantage.
- **A direct peer built this exact workload in .NET** with 4,608 commits behind
  it, which is evidence of adequacy even though it is not a benchmark.

**So one of the three has failed and the other two are weaker than they read.** The
second — a single-binary story restored for both candidates — rests on
[A-016](../../docs/architecture-assumptions.md), which
[ADR-001](../../docs/adr/ADR-001-core-language.md) §7 itself lists `UNVALIDATED`.
The third is a competitor's commit count, which §6 calls *not a benchmark* in its
own words.

**That does not make the decision wrong and it does make the file honest.** What is
open is not *should this have been Go* — it is whether the choice has the evidence
[CLAUDE.md](../../CLAUDE.md) §7 asked for, which is
[Q-01](../../docs/open-questions.md) and is a question about a rule rather than
about a language.

## What replaced it, and why that is better

**A-019 matters more than ADR-001.**

A-019 asks whether in-process MVT encoding meets our latency targets. If it
fails, the multi-database promise is hollow and the architecture changes.
ADR-001 chose between two runtimes that are probably both adequate.

A single-language prototype answers A-019 completely. It does not tell us
whether Go is faster; it tells us whether **this is fast enough**, which is the
question that actually gates the architecture.

So the effort moved from *relative* to *absolute* measurement:

| Was | Is now |
|---|---|
| Endpoint A, streamed GeoJSON, both languages | `benchmarks/feature-query` |
| Endpoint B versus C, `ST_AsMVT` versus in-process, both languages | `benchmarks/mvt-generation` |
| Which language is faster | Whether .NET hits the targets at all |

## What survives from this document

The methodology, which the benchmarks inherited and which still applies:

- **Real data, not synthetic.** Synthetic uniform data makes every
  implementation look good and hides exactly the behaviour we need to see.
- **The prohibited shortcuts** — no buffering a result set before responding, no
  caching, fixed connection pool sizes, no mixing driver modes.
- **p99 over averages**, because averages hide GC pauses.
- **Behaviour at saturation**, not only below it. Graceful degradation under
  overload is worth more to an administrator at 2 AM than being faster below it.
- **Recorded, not measured**: implementation friction, and what diagnosability
  was actually like when something was slow.

## The trigger that would revive it

**.NET missing the absolute targets.** That is what makes a language comparison
necessary — with a reason, rather than as a ritual. Recorded in ADR-001 §9.
