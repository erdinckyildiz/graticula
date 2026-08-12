# Adversarial Architecture Review — Round 1

**Status:** COMPLETE — findings raised, dispositions proposed, not yet all actioned
**Required by:** §6 (Agent 12), §85
**Target:** [architecture-assessment.md](architecture-assessment.md) and the nine
decided ADRs, as of 2026-08-12
**Reviewer stance:** §6 — *"Do not optimize this agent for agreement. Optimize it
for finding flaws."*

---

## How to read this

Twelve findings, ordered by severity. Each states the flaw, why it matters, and a
proposed disposition. §85 requires every material criticism to be **resolved or
documented** — not answered away.

Three findings (F1, F3, F9) are judged severe enough to change decisions rather
than merely annotate them.

| # | Finding | Severity | Disposition |
|---|---|---|---|
| F1 | The provider abstraction has one implementation and we deferred fixing that | **Severe** | Change the plan |
| F2 | The routing control system has never been modelled for stability | **Severe** | New experiment condition |
| F3 | "Never degrade silently" is hostile to the migration goal | **Severe** | Amend ADR-008 |
| F4 | Every ADR is conditional and no condition is discharged | High | Add failure-impact tracking |
| F5 | The 2 AM test is invoked constantly and never applied | High | New required exercise |
| F6 | Context budget counts services, not weight | High | Amend ADR-007 |
| F7 | Deleted rendering returns through the compatibility door, uncosted | High | **RESOLVED 2026-08-12** — owner removed the requirement; WMS is out of v1 |
| F8 | Data source cardinality is assumed, not bounded | Medium-high | New assumption |
| F9 | No behaviour defined for platform store unavailability | Medium-high | New failure analysis |
| F10 | Hosted data is authoritative and has no backup story | Medium | New question |
| F11 | Air-gapped is still a slogan, and more decisions now depend on it | Medium | Q-15 elevated |
| F12 | The process failed to catch a scope inversion | Medium | Process change |

---

## F1 — The provider abstraction has exactly one implementation, and we planned to keep it that way

**Severity: severe.**

We decided the Query AST must target multiple dialects from day one (Q-21), that
capability negotiation is core rather than a refinement (ADR-008), and that "an
abstraction exercised by one implementation is not an abstraction; it is a
wrapper that will not survive the second."

Then the roadmap builds PostGIS in Phase 1 and a second provider in Phase 4.

**So by our own stated reasoning, everything we wrote about capability
negotiation is currently unfalsifiable.** Between Phase 1 and Phase 4 the query
engine, the tile path, the cache key model and the feature service will
accumulate a year of assumptions, every one of them PostGIS-shaped, with nothing
pushing back.

The assessment already flags this as a "deliberate departure" and moves the
second provider from Phase 4 to "as early as it can". **That is not enough.**
"As early as it can" is not a commitment, and the whole multi-database decision —
which the owner made for a concrete migration reason — rests on it.

**Disposition: change the plan.** A second dialect compiler must exist in **Phase
1**, even in a deliberately minimal form: enough to compile the walking
skeleton's queries against SQL Server and run them in CI. Not a supported
provider — a *forcing function*. It costs little in Phase 1 and it is close to
priceless as a constraint on everything after.

The rule to adopt: **no query engine feature is complete until it compiles on two
dialects.**

---

## F2 — Affinity routing, LRU eviction, pinning and auto-escalation form a control system nobody has modelled

**Severity: severe.**

ADR-007 stacks five interacting feedback mechanisms:

1. Contexts bind lazily on first request
2. Contexts evict LRU under a per-worker budget
3. Requests prefer workers already warm
4. Affinity "degrades to plain balancing under skew"
5. Hot services are auto-pinned, and pins compete for a bounded budget

**These interact, and the interaction is not analysed anywhere.**

A concrete failure path:

```
Service A gets busy
  → auto-pinned, occupies pin budget
  → pin budget pressure evicts service B's pin
  → B's contexts now evict under LRU
  → B rebinds constantly, gets slower
  → B's latency makes it look hot
  → B auto-pins, evicting C
  → …
```

That is textbook oscillation, and the design has no damping: no hysteresis on
pinning, no minimum residency, no cost accounting for a rebind, no explicit
stability criterion.

Worse, the escape hatch is vague. "Affinity degrades to plain balancing under
skew" — **at what threshold, measured how, over what window, and with what
behaviour during the transition?** An unspecified switching rule between two
control regimes is where systems flap.

**Disposition: `experiments/affinity-routing` must model stability, not just
hit rate.** Its success criterion is not "affinity improves L1 hits" but
"the system converges under adversarial load patterns." Specifically: sustained
skew, oscillating skew, and a slow ramp across the budget boundary.

Add to ADR-007 as a condition. If stability cannot be demonstrated, the correct
answer is plain balancing with pinning as the only affinity — which is simpler
and provably stable.

---

## F3 — "Never degrade silently" is a good greenfield principle and a hostile migration policy

**Severity: severe. This is a contradiction between two decisions, not a weakness in one.**

ADR-008 refuses queries a provider cannot execute, on the grounds that answering
them slowly hides cost from the operator. The mitigation is a capability report
published in advance.

**The capability report is useless to a migration.** The client is already
written. It was written against GeoServer, which answered the query. The customer
migrates, the application breaks, and the error message explains — correctly and
uselessly — that Oracle cannot execute that predicate and here is the capability
report they could have consulted before writing software they wrote three years
ago.

We made displacing GeoServer and ArcGIS Server a **confirmed product goal**.
Refusal-by-default is directly opposed to it.

The two positions cannot both stand unqualified.

**Disposition: amend ADR-008.** The principle survives; the default changes.

- **Refusal remains the default for new services on the native API.** The
  reasoning holds there: the client is being written now, and honest limits
  produce better software.
- **The compatibility layer defaults to best-effort**, with the residual executed
  in-process where possible, and **every degradation logged and surfaced as a
  metric and a warning header.** Slow-but-working beats broken for a client that
  cannot be changed.
- **The distinction is a per-service policy** with a documented default per
  surface, not a global stance.

This is a genuine weakening of ADR-008's principle and it should be recorded as
such rather than presented as a refinement. The principle was too pure to
survive a migration requirement it was written before we fully absorbed.

---

## F4 — Nine ADRs decided, roughly twenty-five conditions, zero discharged

**Severity: high.**

Every decided ADR is `ACCEPTED WITH CONDITIONS`. Not one condition has been met.
This is not a decided architecture — it is a conditional one, and the conditions
are load-bearing.

Worse, **nothing tracks what breaks when a condition fails.** The assumption
register lists "depended on by", but that is a link, not an impact analysis.
Trace A-015 (warm state is small) failing:

- ADR-007 §4.3 lazy binding becomes a latency problem on every cold service
- ADR-007 §4.4 LRU eviction becomes expensive, so the budget must shrink
- ADR-007 §4.12 pinning becomes the norm rather than the exception, which
  recreates per-service resource allocation — the thing §3 said killed ArcSOC
- ADR-010's L1 model changes shape

**One assumption failing partially reverses the flagship decision**, and that is
not written down anywhere.

**Disposition: add a failure-impact column** to the assumption register for the
load-bearing entries — not "which ADRs reference this" but "what specifically
changes if this is false". Do it for A-014, A-015, A-019, A-024 and A-027 at
minimum.

---

## F5 — The 2 AM test is invoked as a principle and has never been performed

**Severity: high.**

It appears in product-context, in five ADRs and in the assessment. Nobody has
walked a single scenario.

Try one. *At 02:00 an administrator is told one layer's tiles are wrong.*

To answer, they must determine: is the cache stale or wrong-class; what is this
layer's declared volatility and TTL; has schema drift been detected and when was
it last polled; which workers hold this service's context; is the context pinned
or being evicted; is the pin budget contended; is the source database slow or
quiesced; did a seeding job run; was there an invalidation and did it propagate
within the poll window.

**That is seven subsystems and we have no design for composing them into one
answer.** ADR-007 §4.11 says health "composes from the service, its data source
and the workers serving it" — composing is precisely the hard part, and naming
it is not designing it.

An architecture whose operational story is "the information exists somewhere" has
not passed the test it keeps invoking.

**Disposition: a required Phase 0 exercise.** Write three scenarios end to end —
stale tiles, a slow service, a failed registration — and specify what the
administrator sees, from which endpoint, composed from what. If it cannot be
written, the observability model is missing and should be treated as a gap rather
than as a later phase.

---

## F6 — The context budget counts services, and services are not comparable units

**Severity: high.**

ADR-007 §4.4 gives each worker a budget of *N service contexts*, LRU-evicted.

**A service over a 500-million-row table with complex geometry, three registered
CRS transforms and a large style is not one unit of anything.** A worker holding
fifty of those behaves nothing like a worker holding fifty point layers.

Counting by service is the same category error as ArcSOC counting by instance: it
uses a unit that is administratively convenient and physically meaningless. We
criticised them for it in §3 and then did it ourselves in a different currency.

**Disposition: amend ADR-007 §4.4.** The budget is a **resource budget** —
measured in bytes of retained context, or a weighted count — not a service count.
`benchmarks/worker-model` must measure context weight distribution across
realistic layer types, and if the distribution is wide, count-based budgeting is
invalid.

---

## F7 — We deleted server-side rendering, and it came back through the compatibility layer uncosted

**Severity: high. This is a contradiction (§63), not a weakness.**

- Vector-first removed server-side rendering from the core, and ADR-004 is
  `DEFERRED`.
- The compatibility layer is a **product requirement**, not optional (Q-07).
- The compatibility layer includes WMS.
- WMS produces raster images. **That requires a rasteriser.**

So a deferred ADR is a precondition for a required deliverable. We also recorded
that the likely rasteriser, MapLibre Native, `VERIFY` needs X server simulation
in containers — which collides with air-gapped and container deployment.

The assessment presents vector-first as removing rendering. **It removed it from
the core and left it in a required deliverable, and nobody costed the remainder.**

**Disposition: resolve the contradiction explicitly.** Either:

1. WMS in the compatibility layer is **out of v1**, and migration for WMS clients
   is not supported initially — a real product decision that must be stated, not
   drifted into; or
2. ADR-004 is un-deferred for the narrow compatibility-rendering case, with the
   headless and air-gapped constraints as first-class requirements.

Currently we have neither. That is the worst of the three states.

**Resolved 2026-08-12 (Q-47): option 1.** The owner removed the requirement
rather than un-deferring the ADR. WMS is out of v1, WMS-client migration is
unsupported initially, and the limit is documented in
[../product-context.md](../product-context.md) rather than left to be
discovered. A rendered map service may return later as a product capability with
its own justification.

---

## F8 — "Many services share one data source" is asserted and never bounded

**Severity: medium-high.**

ADR-007 §4.8's connection budget survives because pools are per data source
rather than per service: "many services share one registered database, which is
what makes the arithmetic survivable at all."

**There is no assumption recorded for data source cardinality, and no evidence
for it.**

A large enterprise plausibly registers a departmental database per team. Fifty
data sources is not exotic. At four workers, fifty sources and a modest pool of
three, that is 600 connections before job workers and the platform store — and
the whole point of §4.8 was that per-service pooling gave numbers like that.

We replaced an unbounded multiplier with a different unbounded multiplier and
called it solved.

**Disposition: record it as an assumption with a bound**, and design the
per-worker global cap (already in §4.8) as the actual mechanism rather than as a
backstop. Auto-discovery as a first-class publishing mode (assessment §8) makes
high source counts *more* likely, not less.

---

## F9 — Nothing defines behaviour when the platform store is unavailable

**Severity: medium-high.**

Everything routes through it: service definitions, routing state, cache index,
job records, roles, styles.

If it is down, what happens? We never say.

- Can a worker keep serving from already-bound contexts?
- Can the router route without it?
- Do requests fail, or degrade?
- Does a job worker holding a lease know it can no longer renew?
- Does the whole platform stop because a metadata database restarted?

§59 requires explicit failure scenario analysis and **we have not done any of
it.** This is the single largest untouched requirement in the master prompt.

**Disposition: a failure scenario pass is a Phase 0 exit requirement**, not a
later phase. At minimum: platform store unavailable, data source unavailable,
data source slow, worker crash, disk full, job worker partition. The desired
answer for the first is almost certainly "read-only continued service from bound
contexts, no publishing, no new bindings" — but it must be designed, and it
constrains what a context is allowed to depend on.

---

## F10 — We host authoritative data and have no backup or restore story

**Severity: medium.**

The datastore is "the system of record for hosted layers… large, authoritative,
must be backed up" ([data-model.md](data-model.md) §1).

**By whom, with what, and restored consistently with what?**

The platform store holds the service definitions that describe the datastore's
contents. They are two databases, possibly two engines, and a restore that mixes
a Tuesday platform store with a Wednesday datastore produces services pointing at
tables that do not match their definitions.

Backup and restore consistency across the two stores is unaddressed, and it is
the kind of gap that is invisible until the day it matters most.

**Disposition: new question, and it belongs to deployment.** A consistent
backup requires either a documented ordering with a quiescing step, or a version
stamp shared across both stores so a mismatched restore is detected rather than
silently wrong.

---

## F11 — Air-gapped is a first-class requirement and still a slogan, and we keep adding to it

**Severity: medium.**

Q-15 already says this. Since it was raised we have added dependencies on:
offline PROJ grids, GDAL driver data, bundled fonts, **MapLibre glyph packs and
sprite sheets**, COG-capable clients, and possibly a headless rasteriser that may
want an X server.

§2 lists air-gapped operation as a deployment requirement. **Every new decision
has increased what air-gapped must mean, and nobody has written the checklist.**

**Disposition: Q-15 is elevated to a Phase 0 exit item.** A concrete checklist,
tested by attempting an install with no network. Doing this late means
discovering that a core client capability requires a font CDN.

---

## F12 — The process failed to catch a scope inversion, and the failure was invisible

**Severity: medium. A process finding, raised because §85 asks for material criticism and this is material.**

Editing scope inverted twice in one working session: in scope, then out, then in.
Each inversion rewrote ADRs.

The assumption register caught A-009's invalidation cleanly, because ADR-002 had
written a condition in advance. **It caught nothing here, because the error was
not a wrong assumption — it was a wrong *interpretation* of an owner statement,
recorded as fact.**

"Editing is out of scope (Q-42)" was written into three documents as settled.
Nothing distinguished it from a decision the owner had actually made.

**Disposition: a small process change.** Where a decision is derived by
inference from something the owner said rather than stated by them directly, it
is recorded as `INFERRED` and listed for confirmation, not written as decided.
Cheap, and it would have prevented two rewrites.

---

## What this review did not examine

Stated so a second reviewer knows where to look, and so this is not mistaken for
completeness.

- **Security in depth.** §20 lists mitigations per decision; nobody has attacked
  the composition. Multi-tenant isolation between services in a shared worker is
  entirely unexamined.
- **Upgrade and rollback** (§80.35–37, Q-13). Named repeatedly, designed nowhere.
- **The entity model**, which does not exist yet.
- **Anything numeric**, since there are no numbers.
- **The compatibility layer**, beyond F7 — its design has not started.
- **Whether the product should exist**, which §10 of the assessment argues but
  which a genuinely fresh reviewer (§67) should attack rather than accept.
