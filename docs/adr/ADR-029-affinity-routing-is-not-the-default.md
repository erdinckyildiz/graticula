# ADR-029 — Affinity routing is not the default, and A-003 stops being load-bearing

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `HIGH` for the reversal · `MEDIUM` for what replaces it at scale |
| **Decided** | 2026-08-15 |
| **Amends** | [ADR-007](ADR-007-service-runtime.md) §4.4, §4.5, §4.12 |
| **Supersedes** | — |
| **Superseded by** | — |

---

## 1. Context

[ADR-007](ADR-007-service-runtime.md) answered *"what is the modern equivalent of
ArcSOC?"* with **workers sized to the machine, not the catalogue** — one process,
per-service *contexts* rather than processes, lazy binding, LRU eviction. That
part is well evidenced and is not reopened here.

§4.4 added something further: **the router tracks which worker holds which
service contexts, and prefers a warm one.** Affinity routing, with a bounded
context budget, degrading to plain balancing under skew.

**Three things have changed since, and together they invert the default.**

**The prototype has not been run and is not close to being run.** §4.4 says
outright *"this is a hypothesis, and it is the least proven part of this ADR"*
and requires `experiments/affinity-routing/` before it is relied on (A-014).
Fourteen months of implementation later, `experiments/` contains `lang-slice`
and nothing else. `benchmarks/worker-model`, which §4.4 requires to measure
context weight distribution, does not exist either.

**The design already named its own fallback and nobody has taken it.** The F2
amendment is unusually direct: five interacting feedback mechanisms with no
damping, a named oscillation path, and an unspecified switching rule between two
control regimes *"which is exactly where systems flap"*. It then says:

> **If stability cannot be demonstrated, the correct answer is plain balancing
> with pinning as the only affinity — simpler, and provably stable.**

Stability has not been demonstrated. It has not been investigated. The
conditional has been sitting unresolved, and an unresolved conditional defaults
to the more complicated branch purely because that branch was written first.

**And the closest peer runs without it.** Honua Server was checked against its
live repository on 2026-08-15
([honua-server.md](../research/honua-server.md) §2b): one process, **stateless
instances**, catalogue in PostGIS, shared cache and jobs in Redis, scale by
adding containers behind a load balancer. They reach the same scale with no
affinity, no context budget, and nothing to validate.

**What forces a decision now** is that the owner asked whether we should adopt
Redis to close the same gap. The answer to that question turns entirely on
whether §4.4 exists, so §4.4 has to be settled first.

## 2. Alternatives considered

### Alternative A — keep §4.4 as the default and build the prototype

**Argument for.** It is the decided design, and the reasoning behind it is real:
[runtime-models-compared.md](../research/runtime-models-compared.md) §3 records
that every prior system fragments warm state across workers and then routes
blindly, and QGIS Server documents the resulting misses. Nobody has *disproved*
affinity; it has simply not been tested. And if the differentiating claim is
*the runtime that holds 1,000 services on one machine*, this is the mechanism
that claim rests on.

**Argument against.** The prototype has not been run in fourteen months, which
is evidence about what will happen in the next fourteen. Meanwhile the design's
own critique stands unanswered, and the thing being defended is unbuilt — there
is no working affinity router to regress. Keeping it as the default costs
nothing today and quietly keeps two unvalidated assumptions load-bearing.

### Alternative B — adopt Redis, as Honua did

**Argument for.** It is the demonstrated-working answer at a peer. A shared L2
means a local miss costs a network hop rather than a datastore query, which
removes most of what affinity was for. It also solves the multi-node tile cache
and gives a durable job queue.

**Argument against, and it is decisive: Redis does not hold the thing A-003 is
about.** A per-service context is connections, compiled schema, style and CRS
transforms. **A connection pool cannot live in Redis.** The only shareable part
is the describe result — `LayerDescription`, fields and extent — which is
already cached for 30 seconds and is a read from the *data source*, not the
platform store.

So Redis would relieve **A-014** (a cache miss becomes cheaper, so routing
matters less) and would not touch **A-003** (whether per-worker context budgets
survive the idle ratio). It buys the smaller half of the problem for a permanent
dependency: another image in the air-gapped bundle (Q-15), another
version-matched component ([ADR-016](ADR-016-packaging-deployment-upgrade.md)),
and a second store that can be up while holding something stale — which is
[Q-95](../open-questions.md)'s problem a second time.

Applying §82's test honestly: **what concrete problem does it solve today?**
Single node, tile cache on disk and working, no job system built, sessions in
PostgreSQL, nothing measured slow. None.

### Alternative C — plain balancing, pinning as the only affinity (chosen)

The fallback §4.4 already named. Requests go to any worker. Contexts bind lazily
and evict LRU, exactly as §4.3 says. **Nothing routes toward warm state**, and
the only way a service stays warm is an explicit or observed pin (§4.12), which
is a decision with a name rather than an emergent property of a control loop.

**Argument for.** Provably stable, because there is no feedback loop: five
interacting mechanisms become two, and neither observes the other. A-003 and
A-014 stop being load-bearing — not validated, but no longer holding anything
up. It is what the system does today, so it is also the option with no
implementation risk.

**Argument against.** It accepts the warm-state fragmentation that
`runtime-models-compared.md` §3 identified as the gap in every prior system. On
a multi-worker node, N workers each hold their own context for a hot service —
N describes instead of one, N connection pools instead of one. That is real
waste, and choosing this is choosing to pay it.

### Alternative D — delete §4.12 pinning as well

**Argument for.** Maximum simplicity: bind lazily, evict LRU, nothing else.

**Argument against.** Pinning is the answer to a real, documented requirement —
a service under an SLA must not pay a cold first request — and unlike affinity it
is not a control loop. It is a flag that exempts a context from eviction. The
complexity §4.4 was criticised for is the *interaction*, and pinning alone does
not interact with anything.

## 3. Counterarguments to the preferred option

**The strongest one: this trades away the differentiator.** §5 of
[honua-server.md](../research/honua-server.md) lists *"the runtime that holds
1,000 services on one machine"* as one of four candidate answers to Q-49 — what
this product does that others cannot. Affinity routing plus a weighted context
budget is precisely the mechanism that would make that claim true and hard to
copy. Choosing plain balancing makes us architecturally similar to a competitor
with more protocols and a four-year — corrected: eight-month — head start.

The answer is that **an unbuilt differentiator differentiates nothing**, and a
claim resting on two unvalidated assumptions is not a claim that can be made in
public. If the runtime is to be the product, §4.4 must be built and measured, and
this ADR's revisit trigger is exactly that. What is rejected is keeping it as the
*default* while it is neither.

**The second: this is a decision taken because work did not happen.** Fourteen
months of not running an experiment is being converted into an architectural
reversal, which rewards the backlog for being long. That is a fair description.
The defence is that §4.4 conditioned itself on evidence that does not exist, and
the honest response to an unmet condition is to take the branch that does not
need it — not to leave the conditional hanging and behave as though it resolved
favourably.

**The third: N workers holding N copies of a context is genuinely wasteful**, and
this ADR accepts it without measuring it. `benchmarks/worker-model` would have
told us how wasteful. It still does not exist. Condition 2.

## 4. Evidence

| Claim | Evidence |
|---|---|
| The affinity prototype has not been run | `experiments/` contains `lang-slice` and a README. `benchmarks/worker-model` does not exist. ADR-007 §4.4 requires both |
| ADR-007 already named plain balancing as the correct fallback | §4.4, F2 amendment: *"If stability cannot be demonstrated, the correct answer is plain balancing with pinning as the only affinity — simpler, and provably stable"* |
| The design is a control system with no damping | §4.4's own F2 amendment: five feedback mechanisms, a named oscillation path, an unspecified regime switch |
| A peer reaches the same scale without affinity | Honua's published architecture: one process, stateless instances, scale by adding containers. Verified 2026-08-15 |
| Redis would not remove A-003 | A connection pool cannot be shared through it. The shareable part of a context is `LayerDescription` — fields and extent — already cached 30s |
| Nothing is measured to be slow | No performance gate has been run; no deployment exists. This is an argument for not adding a dependency, not for the design being fast |
| A-003 was already downgraded | ADR-007 §4.3: *"idle services cost a row in a table, not a process"* |

**No new measurement was taken for this decision**, and that is worth stating
plainly: this is a decision about which branch of an existing conditional to
take, made because the evidence the conditional wanted was never gathered.

## 5. Decision

**Plain balancing is the default. Affinity routing is removed from the default
design and becomes a change that must earn its way in.**

- **A request goes to any worker.** Nothing tracks which worker holds which
  context, and nothing routes toward warm state.
- **Contexts still bind lazily and evict LRU** (ADR-007 §4.3), unchanged.
- **Pinning survives** (§4.12), as the only form of affinity: explicit or
  observed, named, bounded by a budget, and visible. It exempts a context from
  eviction; it does not steer a request.
- **Auto-pinning by observation (§4.5) is suspended** until §4.12's budget
  contention behaviour is specified. It is one of the five feedback mechanisms
  F2 warned about, and with affinity gone it is the only one left that could
  oscillate.
- **Redis is not adopted.** The baseline stays `gis-server →
  PostgreSQL/PostGIS`. It fails §82's test today: single node, tile cache on disk
  and working, no job system, sessions in PostgreSQL, nothing measured slow.
- **A-003 and A-014 are downgraded from load-bearing to informational.** Neither
  holds up a decision any more. They stay in the register, because the day
  affinity is reconsidered they become load-bearing again immediately.

**Scaling out, when it is needed, is by more nodes over the same PostgreSQL** —
which is what [ADR-026](ADR-026-serving-through-a-platform-store-outage.md)
already reasons about, and what the peer does. The gap that leaves is a
node-local tile cache; condition 3.

## 6. Consequences

**Positive.**

- Two unvalidated assumptions stop holding anything up, and neither can be
  validated by any route this project currently has.
- The control system F2 described no longer exists. Five interacting mechanisms
  become two that do not observe each other.
- The design matches what is built, which it did not before — ADR-007 described
  a router that has never existed.
- No new dependency, and §82's challenge list stays intact.

**Negative.**

- **Warm-state fragmentation is accepted, unmeasured.** N workers, N contexts
  for a hot service, N describes. `runtime-models-compared.md` §3 identified this
  as the gap in every prior system and this decision walks into it deliberately.
- **The strongest candidate differentiator is set aside**, and if Q-49's answer
  turns out to depend on it, this reverses.
- **The tile cache stays node-local**, so a second node doubles cold-miss
  datastore load. That is now the concrete cost of refusing Redis, and it is
  where the refusal will first hurt.
- **A decision was taken on absent evidence rather than measurement**, which is
  a departure from `CLAUDE.md` §3 and is recorded as one.

**Ports created.** None. This removes a design, it does not add one.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| A-073 | Warm-state fragmentation across workers costs less than the affinity control system would cost in instability | `UNVALIDATED`, and it is the mirror image of A-014 — the same missing measurement, read the other way. Recorded so the reversal carries its own assumption rather than appearing to be free |

**Downgraded by this decision:** A-003 and A-014, from load-bearing to
informational.

## 8. Dependencies

**Depends on**: [ADR-007](ADR-007-service-runtime.md), which this amends.

**Depended on by**: Q-49 — if the answer becomes *the runtime*, this reopens.
[ADR-010](ADR-010-caching.md), whose L2 stays optional and now stays unbuilt.

## 9. Revisit triggers

- **A second node is actually run**, and the tile cache fragmentation is
  measured rather than predicted.
- **`experiments/affinity-routing/` is run and demonstrates convergence** under
  sustained skew, oscillating skew and a slow ramp across the budget boundary —
  §4.4's own success criterion, which is stability and not hit rate.
- **Describe cost or connection-pool cost is measured to dominate** a hot
  service's request, which is what would make N copies expensive rather than
  merely wasteful.
- **Q-49 is answered with "the runtime"**. Then the differentiator has to exist.

## 10. Conditions

1. **ADR-007 §4.4, §4.5 and §4.12 are marked as amended by this**, so a reader
   arriving at the runtime ADR is not told about a router that was removed. A
   superseded design left readable as current is how the next person implements
   the wrong thing.
2. **`benchmarks/worker-model` is written before affinity is ever reconsidered.**
   Both this decision and the one it reverses rest on not knowing the context
   weight distribution, and whichever way it goes next, it should go there on a
   number.
3. **The node-local tile cache is stated in deployment guidance** before anybody
   is told to run two nodes. Discovering that two servers means two caches, in
   production, is the worst way to learn it. *(Discharged 2026-08-15 —
   [deployment.md](../deployment.md) §1, which also lists what else is per node
   and states plainly that two nodes have never been run.)*
4. **A-003 and A-014 are restored to load-bearing the moment affinity is
   reconsidered**, rather than being quietly carried as informational into a
   design that needs them again.

## 11. Dissent

**Recorded, and it is the differentiator argument.** This project's plausible
claim to being worth building — rather than being a smaller Honua — was that it
holds a thousand services on one machine because it understands where warm state
lives. This ADR sets that aside because the work to prove it did not get done,
and swaps it for an architecture a competitor already ships more of.

Nothing here disputes that. The counter is narrower than it sounds: **the claim
was never true, only intended**, and there is no version of this project in which
an unbuilt router with two unvalidated assumptions beats a competitor's shipped
one. Reversing on the evidence we have is honest; keeping the design as a default
because it would be a good differentiator *if it worked* is the kind of
self-deception this repository exists to prevent.

Anyone reading this later should understand that the reversal is a bet that
simplicity now beats a differentiator later — and that if somebody runs the
experiment and it converges, they should reverse it back without embarrassment.
