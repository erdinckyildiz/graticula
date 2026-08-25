# ADR-048 — CI does not run the real-data suites, and says so on every run

| | |
|---|---|
| **Status** | `ACCEPTED` |
| **Confidence** | `HIGH` for the exclusion · `MEDIUM` for whether announcing it is enough |
| **Decided** | 2026-08-25 |
| **Answers** | part of [Q-117](../open-questions.md) |
| **Supersedes** | — |
| **Superseded by** | — |

---

## 1. Context

**Actions ran for the first time on 2026-08-25**, after nine months of 0-second
`startup_failure`s ([D-63](../architecture-debt.md), [Q-135](../open-questions.md)).
The first complete run failed, and one of the two causes is this ADR's subject.

**Seven classes** in `Graticula.Platform.Postgres.Tests` compare this server against
PostGIS on **a real OpenStreetMap extract** — `public.planet_osm_polygon` and
`public.planet_osm_line`, an osm2pgsql schema. On a developer machine that is 6.5
million real polygons. In CI there is nothing, and the classes **fail rather than
skip**, which is deliberate: a test that goes green with its subject absent is
worse than no test, and this repository has written that trap down four times.

So CI cannot simply run them, and cannot simply ignore them either without
deciding what that means.

## 2. Alternatives considered

### Alternative A — Seed a stand-in corpus and run them

**Argument for.** `tools/ci-corpus.sql` already exists and already runs in this
job, so the machinery is there. A generated corpus that satisfies the queries
would let the suites run everywhere and CI would be complete.

**Argument against, and it was tried before it was rejected.** On 2026-08-25 the
seed was rewritten to build `public.planet_osm_polygon` with 240 generated
polygons. The suites then asked for more than the shape: **500 single-ring
polygons** (one asserts the count, not a limit), multi-ring polygons, a `name`
column, a **second table** `public.planet_osm_line`, and an **`Index` node in the
query plan**. Satisfying all of that is inventing a fake osm2pgsql database so
that a gate can go green on data the suites were written not to trust — the
script's own header calls a generated corpus *"a weaker check than the local
one"*.

**And the attempt nearly broke something that worked.** `tools/ci-corpus.sql`
serves a *different* audience: the two **discovery** suites, which pick the
richest polygonal table they can find. `GeometryCorpus`'s own notes record that
adding `cicorpus` to a developer database once made those suites silently switch
from real cadastral polygons to sixty generated ones **and stay green** — the
failure they exist to prevent. Repointing that script would have re-created it.
Reverted.

### Alternative B — Exclude them in CI, silently

**Argument for.** One filter, no ceremony, green build.

**Argument against.** A gate that is quiet about what it skipped claims more than
it proved, which is §67's warning applied to ourselves. The cheapest way for CI to
become a lie is for an exclusion to be added once and never mentioned again.

### Alternative C — Load a real extract in CI

**Argument for.** It is the only option that makes CI prove what a developer
machine proves.

**Argument against.** Downloading and importing an OSM extract on every push costs
minutes and bandwidth for a check whose value is *breadth of real geometry*, and a
small extract has less of that than the developer database it is standing in for.
It buys the appearance of the local check at a fraction of its strength.

## 3. Counterarguments to the preferred option

**The strongest one: an exclusion nobody reads is the same as a silent one.** This
decision rests on somebody noticing a step's output in a green run, and people do
not read the logs of green runs. The mitigation is that the step runs `if:
always()` and derives its list from the code — so it cannot go stale — but nothing
forces a reader.

**The second: this is the beginning of a slope.** The first exclusion is always
defensible. The tenth is how a suite becomes decorative. What limits it here is
that the trait names a *reason* rather than a symptom: `Corpus=RealData` is a
claim about what the test needs, and a class that does not need real data cannot
honestly wear it.

**The third: it leaves the strongest tests running only where they were written.**
21 tests, and they are the ones comparing ring winding and round-trips against
PostGIS on real geometry — the ones that found the Douglas-Peucker ring defects.
They now run on exactly one machine, which is the position
[Q-117](../open-questions.md) exists to be uncomfortable about.

## 4. Evidence

| Claim | Evidence |
|---|---|
| The suites need an osm2pgsql schema, not just polygons | Failures on 2026-08-25 against a 240-polygon stand-in: `Expected: 500 / Actual: 180`, `relation "public.planet_osm_line" does not exist`, `column "name" does not exist`, and an assertion on `Index` in the query plan |
| They fail rather than skip on purpose | Their own message: *"These tests exercise the read path against real data and fail rather than skip; load the corpus with experiments/_env, or exclude this class by name."* |
| A generated corpus has silently weakened these suites before | `tests/shared/GeometryCorpus.cs`: adding `cicorpus` to a developer database made two suites switch to sixty generated polygons **and stay green** |
| The filter is correct in both directions | Measured 2026-08-25: `Corpus!=RealData` runs 341 tests, all passing; `Corpus=RealData` runs 21, all passing against the local extract. **And that measurement did not prove what it looked like it proved** — see below |

**The first version of this decision tagged three classes and there were seven.** The three
were the ones a CI failure happened to name. The other four failed on the next run — 24
tests — and the miss has a cause worth keeping: the search was for `planet_osm_polygon`,
and `PostGisTileSourceTests` reads `public.osm_buildings`. **Tagging what you were shown
rather than what you were looking for is [D-46](../architecture-debt.md) exactly**, and the
local measurement above could not catch it, because on a machine that *has* the extract
every class passes whether it is traited or not. A filter's completeness is only visible
where the data is missing.

**So the trait set is derived rather than listed.** All seven classes refuse in the same
words — *"… is not loaded"* — because they fail rather than skip by design, and
`a_real_data_test_without_the_trait_ci_filters_on` reads that sentence: a class that says
it needs a real extract must carry the trait CI filters on. A new suite is caught when it
is written rather than by the CI run that would otherwise find it.

## 5. Decision

**All seven classes carry `[Trait("Corpus", "RealData")]`, CI runs
`--filter 'Corpus!=RealData'`, and the job prints what it excluded on every run.**

- The trait names what the test **needs**, not which job it belongs to, so a class
  that stops needing real data stops being excluded by deleting one line.
- The announcement **derives its list from the code** — it greps the trait — so it
  cannot drift from what was actually skipped.
- It runs `if: always()`, so a failing job still says what it did not attempt.
- `tools/ci-corpus.sql` is unchanged. It serves the discovery suites and it serves
  them correctly.

**What CI proves, stated plainly:** everything except the comparison of this
server's geometry handling against PostGIS on real data. That comparison runs on a
developer machine and nowhere else.

### 5a. A second exclusion, and it is a different kind

**`PollerPoolTests` needs a running host rather than real data**, and the distinction
matters: it reads `pg_stat_activity` for the sessions the background pollers hold, so
with no server every assertion is about an absence. It passed locally because a
development server happened to be up — an environment assumption nobody made on purpose.

**It is not skipped. It is moved.** `[Trait("Needs", "RunningHost")]`, excluded from
the `datastore` job which has a database and no host, and **run in `conformance`**, which
starts one. Excluding a test in one job and never running it in another is how an
exclusion becomes a deletion, and the announcement step says which of the two kinds each
class is: `Corpus=RealData` runs nowhere in CI, `Needs=RunningHost` runs elsewhere in CI.

**This is the second exclusion, and §7 condition 1 said that reopens the decision.** It
is recorded here rather than in a new ADR because the condition was about a *pattern of
skipping*, and this is the opposite: a test that was failing for an environmental reason
now runs in the environment that suits it. The condition stands for the next one.

## 6. Consequences

**Positive.** CI can go green honestly. The exclusion is visible on every run,
derived rather than typed, and reversible in one line per class.

**Negative.**

- **24 tests now run in exactly one place**, and they are among the most valuable
  in the repository. If that machine goes away, so does the check.
- **This is a partial answer to [Q-117](../open-questions.md)** and should not be
  read as the whole one. That question asks what CI proves about a repository
  somebody else clones; this ADR narrows the claim rather than widening the proof.
- The announcement is a convention, not a mechanism. Nothing fails if somebody
  adds an exclusion without one.

## 7. Conditions

1. **A second exclusion reopens this.** One is a decision; two is a pattern, and a
   pattern needs a rule rather than another ADR.
2. **If a real extract ever becomes cheap to load in CI** — a cached artefact, a
   prebuilt image — Alternative C should be re-costed, because it is the only one
   that makes the check travel.

## 8. Revisit triggers

- **A defect ships that these suites would have caught**, which is the evidence
  this decision was wrong.
- **The developer machine holding the corpus is lost or replaced**, at which point
  the 21 tests are running nowhere and this ADR's negative consequence has arrived.
