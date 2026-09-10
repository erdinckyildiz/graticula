# ADR-061 — Metrics are aggregate; per-entity numbers live in the admin API

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` |
| **Decided** | 2026-09-09 |
| **Supersedes** | — |
| **Superseded by** | — |

---

## 1. Context

[Q-12](../open-questions.md) asks how monitoring cardinality is bounded at 1,000
services. [ADR-007](ADR-007-service-runtime.md) §5's scale table elevated it and
calls it *the item routinely forgotten and routinely fatal*.

**The shape of the problem is that the runtime does not grow and the metrics
do.** ADR-007 §5 establishes that worker count, process count and connection
count are all flat in service count, and [ADR-019](ADR-019-portal-server-split.md)
§3 puts both halves of the product in one process. So at
[CLAUDE.md](../../CLAUDE.md) §7's 1,000-service target — and at ADR-007's
ten-thousand row — nothing about the runtime multiplies. The only thing that can
is a label.

**This is decided now because it is free now and will not be later.** A grep of
`/src` for `OpenTelemetry`, `Prometheus`, `System.Diagnostics.Metrics`,
`new Meter(`, `CreateCounter`, `CreateHistogram` and `IMeterFactory` returns
nothing, and `Directory.Packages.props` carries no telemetry package. Nothing
emits a metric. The window in which this choice costs nothing is open precisely
because nobody is scraping, and it closes the moment somebody's dashboards are
built on whatever shipped first.

**And there is a decision here already, in the wrong place.**
`AdminEndpoints.cs` says, in a code comment: *"**Here rather than at /metrics,
and that is the decision.** The experiment harness had a public /metrics; this
route already exists, already resolves a principal, and already redacts
everything below this line from a caller without `admin:manageServer`. Adding a
second surface would mean a second authorization story for the same disclosure —
and D-03 records over-disclosure to unauthenticated callers as open debt."* That
reasoning is good and the placement is not: [CLAUDE.md](../../CLAUDE.md) §2 says
every architectural decision becomes an ADR, *no exceptions, no informal
decisions*, and a grep of `docs/adr/` for `/metrics` returns nothing. This
document is that repair as much as it is a new decision.

## 2. Alternatives considered

### Alternative A — Label by service and accept the cardinality

**Argument for.** It answers the question an operator actually asks during an
incident — *which of my services is doing this* — directly, in the tool they
already have, with no second surface to learn. Every mature product ends up
here, and the ones that did not add the label first are the ones whose users
built their own exporters.

**Argument against.** The arithmetic is computable from the label design without
any benchmark, and it is four orders of magnitude. Take three metrics: a request
counter with five status classes, one duration histogram (fourteen buckets plus
`+Inf`, `_sum` and `_count` — seventeen series), and a cache hit/miss pair.

| Shape | Series at 1,000 services |
|---|---|
| Aggregate, no entity label | 5 + 17 + 2 = **24**, flat in service count |
| `{service}` on all three | 5,000 + 17,000 + 2,000 = **24,000** |
| The same, plus an `operation` dimension of four | **≈ 90,000** |
| `{layer}` rather than `{service}` | multiply again by layers per service, and [ADR-038](ADR-038-how-a-geodatabase-becomes-a-service.md) makes one service N layers |

**And the decisive half of the argument is not about cost at all.** A `service`
label taken from the request path *before the catalogue resolves it* is
unbounded and attacker-controlled: `/rest/services/<anything>/FeatureServer`
mints a new series on every request. That is the identical hazard
`DatumShiftNotices` is bounded against — its own remarks say *"the second half is
attacker-controlled: a caller naming ten thousand SRIDs would otherwise grow this
without limit"* — and it turns a monitoring bill into a memory-exhaustion
vector.

### Alternative B — Per-service metrics with a cap and eviction

**Argument for.** It keeps the label and bounds the damage: hold N series, evict
the least recently used, and the attacker-controlled path segment can no longer
grow the set without limit.

**Argument against.** An evicting metric is a metric that is wrong exactly when
it matters. The series evicted under pressure are the ones nobody queried
recently, and an incident is the moment a previously quiet service starts
mattering. Worse, the eviction is silent by construction: a time series that
stops and restarts looks like a service that stopped and restarted. This trades
a bounded cost for an unbounded and undetectable inaccuracy.

### Alternative C — Aggregate metrics; per-entity numbers through the admin API *(chosen)*

**Argument for.** It is the only shape this server has ever implemented, and the
shape it implements well. `/admin/health` already carries four bounded
per-entity collections — `datumShiftNotices`, `admissionControl`, `tileCache`
and `describedShapes` — behind a route that already resolves a principal and
already scopes detail by permission. `DatumShiftNotices` is the pattern: a stated
ceiling of 256 sized explicitly against §7's 100–1,000 target, a `Truncated`
flag rather than silent loss, and a key space bounded on purpose. And the
per-request path is already in a disciplined structured log — `Log.cs` is
source-generated `LoggerMessage` throughout with unique `EventId`s enforced by a
test after three collisions.

**Argument against.** See §3. It is the alternative with the weakest answer to
the one question that matters at 2 AM.

### Alternative D — Aggregate metrics with exemplars

**Argument for.** An exemplar attaches a service identifier to a *sampled*
histogram observation, so the series count stays flat while a slow request can
still be traced to the service that made it. It is the shape that would survive
§3's counterargument without paying Alternative A's cardinality.

**Argument against.** It is not free — it needs an exporter, a sampling policy
and a trace identifier this product does not yet have — and it is unfalsifiable
today because nothing emits. Choosing it now would be choosing a mechanism for a
surface that does not exist. It is named here so that §9's first trigger has
somewhere to go rather than reopening from nothing.

## 3. Counterarguments to the preferred option

**The admin API answers a moment; alerting needs a history, and this decision
gives up on both.** An operator with 1,000 services cannot page on *service X's
p99 degraded* from a JSON document they have to poll. The console says as much
about its own resource widget: *"The lines are samples this page has taken since
you opened it… not a history the server keeps, because it keeps none."*
Aggregate-only means the single question asked during an incident — *which of my
thousand services is doing this* — is the one the metrics are structurally unable
to answer, and *grep the log* is the answer every product gives immediately
before it adds the label.

**The rebuttal is real but it is a design argument, not a measurement, and it is
recorded as one.** At 1,000 services a per-service dashboard is unreadable
anyway; what an operator alerts on is an aggregate SLO plus a top-N, and a
bounded admin-API collection is exactly a top-N. But nobody has walked ADR-007
§5's own 2 AM scenario against this design, and until somebody has, this
paragraph is a plausible story rather than evidence. That is §9's first trigger
and condition 2.

**A second counterargument, narrower and harder.** This decision reasons from
1,000 services because §7 says 100–1,000 and calls larger figures stress models.
ADR-007 §5 reasons to 10,000. The arithmetic in §2 is a tenth as alarming at 100
as it is at 1,000, and someone could fairly say the label is affordable for the
deployment this product actually targets. The answer is that the
attacker-controlled half of §2's argument does not scale with the deployment at
all — it scales with how many distinct paths a caller chooses to request — so
the prohibition on a pre-resolution path segment stands at every size, and only
the cost half weakens.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| Nothing emits a metric today, so the choice is still free | Grep of `/src` for `OpenTelemetry`, `Prometheus`, `System.Diagnostics.Metrics`, `new Meter(`, `CreateCounter`, `CreateHistogram`, `IMeterFactory` returns nothing; no telemetry package in `Directory.Packages.props` | Measured 2026-09-09 |
| No ADR has ever mentioned `/metrics` | Grep of `docs/adr/*.md` returns nothing | Measured 2026-09-09 |
| The decision exists in a code comment | *"Here rather than at /metrics, and that is the decision"* | `src/Graticula.Host/AdminEndpoints.cs` |
| The runtime is flat in service count, so only labels multiply | Worker, process and connection counts flat; *monitoring cardinality* named as the routinely fatal item | [ADR-007](ADR-007-service-runtime.md) §5 |
| One process, so services are the only multiplier | One deployable, both halves in one process | [ADR-019](ADR-019-portal-server-split.md) §3 |
| 24 series aggregate against ≈ 24,000 labelled by service, and ≈ 90,000 with an operation dimension | Arithmetic over the label design, §2's table | Computed 2026-09-09, not benchmarked |
| A path-segment label is attacker-controlled and unbounded | `/rest/services/<anything>/FeatureServer` resolves after the label would be taken | Read 2026-09-09 |
| The bounded per-entity shape already exists and is sized against §7 | `Ceiling = 256`, `Truncated` flag, remarks naming the attacker-controlled key space | `src/Graticula.Host/DatumShiftNotices.cs` |
| The per-request path is already logged under a disciplined scheme | Source-generated `LoggerMessage`, unique `EventId` enforced by `LogEventIdTests` after three collisions | `src/Graticula.Host/Log.cs` |
| Per-worker numbers are not available per service | `/admin/workers/{id}` promised by ADR-007 §4.14 does not exist — grep of `/src` for `admin/workers` returns nothing | Measured 2026-09-09 |

**What is not measured here, stated so it is not mistaken for measured:** the
cost of one time series in whatever TSDB a deployment runs. §2 computes series
counts, which are exact from the label design; it puts no number on bytes,
because that number belongs to somebody else's Prometheus.

## 5. Decision

**No metric this server emits may carry a label whose value identifies a service
or a layer, and in particular none may carry a value taken from a request path
segment before the catalogue has resolved it.** The second half is a security
rule rather than a cost rule and holds at every deployment size. Operational
numbers per entity are reported by the admin API — `/admin/health`, behind
`admin:manageServer`, in the bounded shape `DatumShiftNotices` established: a
ceiling stated in the code and sized against [CLAUDE.md](../../CLAUDE.md) §7, a
`truncated` flag, and no silent eviction. **v1 ships no exporter and no
`/metrics` surface**, because [CLAUDE.md](../../CLAUDE.md) §82's question has no
answer while no deployment is scraping; when one exists it goes behind the same
principal the admin API already resolves, rather than as a second anonymous
surface for the same disclosure. *Which service is slow* is answered by the
structured request log and by a bounded per-entity collection, not by a label.

## 6. Consequences

**Positive.** Cardinality is bounded by construction rather than by a limit
somebody has to maintain, and it is flat in service count at every size ADR-007
reasons about. The attacker-controlled series-minting path is closed before it
exists. There is one authorization story for operational disclosure instead of
two, which is the direction [D-03](../architecture-debt.md) has been walking. And
the shape is not new: four collections in `/admin/health` already have it, so a
fifth costs a reader nothing to learn.

**Negative.** An operator cannot page on a single service's latency, and the
answer to *which of my thousand services is doing this* is a log query rather
than a dashboard — §3 is not rhetorical and this decision genuinely loses that.
A bounded collection reports a moment and keeps no history, so trend questions
have no answer at all from this server. Whoever eventually wants per-service
alerting will build an exporter outside this product, and it will read the admin
API and will be worse at it than a native one would have been. **And this
decision does not make itself felt** — nothing enforces it today except
condition 1, because there is no metric to enforce it against.

**State.** *Catalogue*: **none** — this decision stores nothing and adds no column.
*Runtime*: **node-local, and that is a consequence rather than an oversight.** The bounded
per-entity collections this decision routes numbers to — `datumShiftNotices`,
`admissionControl`, `tileCache`, `describedShapes`, and any added under §5 — live in the
process that observed the thing, are lost on restart, and describe one node. A reader of
`/admin/health` on a two-node deployment is reading one node's answer. That is the price
of refusing a metrics backend, and it is the same price §6 records under *keeps no
history*: what a time series would have made durable and shared, this makes neither.

**Ports created.** None; no Tier 2 dependency is adopted.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| A-050 | Per-worker allocation rate and GC pause can be sampled continuously, cheaply enough to be always-on metrics | `UNVALIDATED` — and this ADR does not validate it. It decides where such numbers are *reported*, not whether they can be *sampled*. If A-050 is false, the process-level counters at `/admin/health` are what remains, and that is what exists today. |

## 8. Dependencies

**Depends on:** [ADR-007](ADR-007-service-runtime.md) (§5's scale table and the
flat-runtime claim), [ADR-019](ADR-019-portal-server-split.md) (one process),
[ADR-017](ADR-017-admin-api.md) (the admin surface and its principal).

**Depended on by:** any future ADR introducing an exporter, a metrics surface or
a tracing decision.

## 9. Revisit triggers

1. **The first exporter, or the first deployment that asks for one.** This is the
   trigger with all the value in it: the choice is free only while nothing is
   scraping. Condition 1 makes the trigger fire on its own rather than depending
   on somebody remembering this document exists.
2. **ADR-007 §5's 2 AM scenario walked and failed.** Take *this service is slow*
   end to end with aggregate metrics plus the request log plus `/admin/health`.
   If the service cannot be named, Alternative D's exemplars become the answer
   and §5 narrows to *no labels, but exemplars*. Condition 2.
3. **A measurement of per-series cost on a TSDB somebody actually runs.** If
   24,000 series is unremarkable on the deployments this product targets, the
   cost half of §5 weakens. The attacker-controlled half does not, and it alone
   is sufficient for the path-segment prohibition.
4. **The scale target moving.** §5 is sized against §7's 100–1,000. If that
   changes, the arithmetic in §2 is re-run against the new number.

## 10. Dissent

**Recorded rather than smoothed over, per [CLAUDE.md](../../CLAUDE.md) §2.** The
case in §3 is not answered by this decision, only outweighed: aggregate-only
metrics cannot name the slow service, and the argument that a bounded top-N in
the admin API is an adequate substitute is a design argument that nobody has
walked a real incident against. Anyone reading this after an incident where the
slow service could not be found should treat §9's second trigger as already
fired.

---

## 11. Conditions

1. **The build fails when a metrics API or exporter package is introduced without
   this ADR being read.** A rule with nothing enforcing it is a rule that is
   discovered after the first dashboard is built on top of its violation, and §6
   admits this decision otherwise makes itself felt nowhere. An architecture test
   fails on the appearance of `System.Diagnostics.Metrics`, `new Meter(`,
   `CreateCounter`, `CreateHistogram`, `IMeterFactory`, or an OpenTelemetry or
   Prometheus package reference, and names this document in its message.
   ***(Discharged 2026-09-09 — `tests/Graticula.Architecture.Tests/MetricsStayAggregateTests.cs`,
   falsified by adding a `System.Diagnostics.Metrics` using directive to
   `AdminEndpoints.cs` and confirming the build fails naming the file and this
   ADR.)***

2. **ADR-007 §5's 2 AM scenario is walked against this design before v1 ships.**
   *This service is slow*, end to end, using only what §5 leaves available: the
   aggregate metrics that do not yet exist, the request log that does, and
   `/admin/health`. The scenario either names the service or it does not, and if
   it does not, §5 narrows to Alternative D. ~~**OPEN** — this is the condition that
   would change the decision rather than confirm it, and it is not discharged by
   this document being written.~~

   ***DISCHARGED 2026-09-10 by walking it, and it changed the decision — but not to
   Alternative D.*** The walk, against the running fixture, in the order §5 leaves:

   **`/admin/health` names nothing, and that is the decision working.** Every number it
   carries is aggregate — `admissionControl.waitingForSource: 0`, `unopenableSources: []`,
   `platformStore.reachable: true`, a runtime block and a certificate block. An operator
   reads *the server is well* and cannot read *which service is slow*, which is exactly
   what §1 chose.

   **The request log carries the duration on every row.** `durationMs` is in the detail
   of each entry, so *how slow* is answerable without leaving the admin API.

   **And the store can name the service — with one query.** `request_log` has a
   `service` column, populated on **141,029 of 996,155** rows across **422 distinct
   services**, and a single group-by answers the question the scenario asks:
   `Utilities/Geometry` 4,156 requests averaging **386 ms** with a worst of 14,809;
   `hosted/ci_many` 20,423 averaging **303 ms**. That is *this service is slow*, named,
   from the log §5 already leaves available.

   **So the scenario does name the service, and §5 does not narrow to Alternative D.**
   What the walk found instead is that three faces were not filling the column:

   | face | rows | named |
   |---|---|---|
   | ArcGIS | 219,461 | **64.3%** |
   | OGC API Features | 30,678 | **0%** |
   | WMS | 9,848 | **0%** |
   | WFS | 5,674 | **0%** |

   `RequestFacts.Service` read the path and returned null unless it began `/rest`, so the
   three faces that serve the same layers filed every request under nothing.
   [D-255](../architecture-debt.md) is that defect and it is repaired: each face is read
   where that face puts the name — the collection segment for OGC, `layers` for WMS,
   `typeNames` for WFS — pinned by `EveryFaceNamesItsServiceTests`, which was falsified
   against the old rule and fails eight of its nineteen cases there.

   **What the walk did not do**, and it is worth saying rather than leaving to be
   assumed: nothing here made a service slow on purpose. The durations are what today's
   suites left behind, and they were enough because the question was whether the *route*
   exists, not whether a particular number is alarming. **What is still missing is the
   step after naming**: nothing sorts or filters the log by duration, so the operator's
   own path is a group-by in SQL rather than a screen. That is a smaller and more
   concrete want than Alternative D, and it is [D-255](../architecture-debt.md)'s
   remainder rather than a reason to reopen §5.
