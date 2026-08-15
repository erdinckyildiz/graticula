# ADR-006 — Plugin Model

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` — re-closed 2026-08-15, see §0. Reopened 2026-08-13 by Q-17b and closed again when v1-scope §3c cut GPServer | `ACCEPTED WITH CONDITIONS` — decision is "not yet" |
| **Confidence** | `MEDIUM-HIGH` |
| **Decided** | 2026-08-12 |

---

## 0. `REOPENED` 2026-08-13 — by the exact trigger this ADR named

> **RE-CLOSED 2026-08-15. The trigger was removed before it was answered.**
>
> Q-17b put a Python GPServer in scope and that is what reopened this. **v1-scope
> §3c then cut GPServer, the Python SDK, the sandbox and the curated wheel set
> out of v1 entirely**, and says so in its own words: *"ADR-006 can re-close —
> the plugin model was reopened by exactly this."* It never did, and the
> conditions of a decision nobody is implementing have been counted as
> outstanding work since.
>
> **So this reverts to what §2 decided: internal extension points only**, no
> published contract for third parties. Nothing below is retracted — the
> analysis of *why geoprocessing is the safest place to start* is the right
> starting point for whoever reopens this, and the reopening trigger is unchanged
> and precise: somebody needing a provider, format or operation we have decided
> not to build. **GPServer returning to scope is that trigger arriving again.**
>
> Recorded as bookkeeping rather than as a decision: v1-scope is authoritative
> and had already stated this consequence. What was missing was somebody applying
> it.

This ADR decided **internal extension points only**, and stated its reopening
trigger precisely: *someone needing a provider, format or operation we have
decided not to build.* It went further and predicted the order — **geoprocessing
first, because job workers already provide the isolation**; data providers last,
because they sit in the request path with no cheap isolation story.

**Q-17b is that trigger, arriving by that route.** The owner has put GPServer in
scope with tools written in **Python**. That is a third-party plugin system for
geoprocessing in everything but name, and pretending otherwise would be the kind
of quiet scope drift §62 exists to prevent.

**What the prediction got right, and it is worth saying because it constrains
the redesign:** geoprocessing genuinely is the safest place to start. Job workers
are already isolated from request workers (§4.2), already have their own memory
budget, and already run GDAL, so an out-of-process Python interpreter fits the
existing shape rather than forcing a new one.

**What this ADR must now answer that it previously did not:**

- A published contract, versioned, for third parties — the thing §2 declined to
  build. Q-74 defines its data boundary.
- A sandbox model. Arbitrary user code is a different risk class from an
  internal extension point, and Q-75 owns it.
- A dependency story, which Q-76 owns and which collides with air-gapped
  installs.
- Who may publish. §2's reasoning assumed extensions came from us; the
  self-service model (Q-59–Q-62) introduced publishers who upload **data**.
  **Uploading code is a different grant** and should not inherit the same role
  by default.

The rest of this ADR stands for **providers, formats and API surfaces**, which
remain internal-only. Only the geoprocessing axis is reopened.

## 1. Context

§43 asks for versioned extension contracts. §82 asks what concrete problem a
technology solves before it is adopted, and names extension systems implicitly
by asking the question of everything.

The distinction that resolves this ADR:

| | What it is | Cost |
|---|---|---|
| **Internal extension point** | An interface we define and implement. Providers, auth backends, output formats. | Ordinary design |
| **Plugin system** | A stable public contract for code we did not write: versioning, isolation, security review, an SDK, documentation, compatibility across releases | Large and permanent |

We need the first. Nobody has asked for the second.

## 2. Decision

**Internal extension points now. No third-party plugin system.**

### 2.1 What this means concretely

The provider port ([ADR-008](ADR-008-query-engine.md) §7), the platform store
port ([ADR-002](ADR-002-primary-data-architecture.md) §4a), the geometry and CRS
ports ([build-vs-adopt-policy.md](../build-vs-adopt-policy.md)) are **interfaces
we implement**, not a public SDK.

They should be well designed, because good interfaces make our own work easier
and because they are what a plugin system would later be built from. They should
**not** be documented as a stable public contract, versioned for external
consumers, or defended against breaking changes across releases. Those are the
expensive parts and they buy nothing until someone external is on the other side.

### 2.2 Why not now

- **No demand.** §82's question — what concrete problem does this solve — has no
  answer yet. Every provider on the list (§27) is one we intend to write.
- **Interfaces designed without a consumer are wrong.** A contract with one
  implementer is a wrapper. Publishing it before a second, independent
  implementation exists means publishing our first guess and then living with it.
- **It is not reversible cheaply.** Once third parties depend on a contract, it
  is frozen. §10 says nothing is sacred, but a published plugin API comes close
  in practice.

### 2.3 The concrete trigger that reopens this

Not a feeling that plugins would be nice. A specific event:

> **Someone needs a provider, output format or geoprocessing operation that we
> have decided not to build ourselves.**

That is the demand signal. Until it happens, this ADR stays closed.

### 2.4 If it reopens — geoprocessing first, providers last

Recorded now because the ordering is not obvious and it follows from
[ADR-007](ADR-007-service-runtime.md).

| Extension kind | Where it runs | Isolation | Difficulty |
|---|---|---|---|
| **Geoprocessing operation** | Job worker | **Free** — job workers are already isolated processes, already restarted freely, already rate-limited | Easiest |
| Output format / encoder | Request path, but pure and bounded | Manageable — no I/O, no state | Medium |
| Auth backend | Request path, security-critical | Hard — a defective auth plugin is a breach, not a crash | Hard |
| **Data provider** | Request path, in-process, holds connections and state | **Hardest** — this is the case with no cheap isolation story | Last |

**Geoprocessing plugins are nearly free** because ADR-011 already built the
isolation boundary they need. Provider plugins are expensive because they live
in the request path and would need either in-process trust or an out-of-process
protocol we do not have.

So if plugins ever happen, they start where the isolation already exists.

### 2.5 What we do now, cheaply, to keep the door open

- Keep the ports narrow and coherent. A narrow interface is a better future
  contract than a wide one.
- Do not scatter provider-specific knowledge outside provider implementations.
  The rule already exists in the build-vs-adopt policy; it is also what makes a
  future plugin boundary possible.
- Keep the job worker isolation genuine rather than nominal, since that is the
  boundary a plugin system would use first.

None of these cost anything. They are good design regardless, which is the test
for whether preparation is legitimate or speculative.

## 3. Counterarguments

- **An ecosystem is a competitive advantage, and ecosystems take years.**
  Waiting for demand means waiting years more. QGIS's plugin ecosystem is a large
  part of why QGIS won, and it did not appear on request.
- **Designing the interface with a plugin in mind produces a better interface**,
  even if no plugin ever ships. Deferring may mean we design something merely
  convenient for ourselves.
- **"We will add it when someone asks" often means never**, because by the time
  someone asks the internals have grown assumptions that make a clean boundary
  impossible. §2.5 is the mitigation and it may not be enough.

The first of these is the strongest, and it is the reason this ADR is
`ACCEPTED WITH CONDITIONS` with an explicit trigger rather than a flat rejection.

## 4. Consequences

**Positive.** No SDK, no compatibility guarantees, no plugin security surface,
no versioned public contract. Interfaces stay free to change while we are still
learning what they should be.

**Negative.** No ecosystem, and no path to one until the trigger fires. Everything
on the provider list is work we must do. If demand arrives suddenly we will be
slower to answer it than a project that prepared.

## 5. Assumptions

| ID | Assumption | Status |
|---|---|---|
| A-034 | Internal ports designed for our own use will be good enough to become a plugin contract later, without a redesign | `UNVALIDATED` — §3's third counterargument is the risk |

## 6. Dependencies

**Depends on:** [ADR-007](ADR-007-service-runtime.md) (job workers are the
isolation boundary a plugin system would use), [ADR-011](ADR-011-job-system.md),
[ADR-008](ADR-008-query-engine.md) (the provider port).

**Depended on by:** provider architecture (§27), geoprocessing (§36), security
(§54) — which is materially simpler while there is no third-party code.

## 7. Conditions

1. **Ports are not documented as public contracts** while this decision stands.
   Publishing them informally is how a contract is created by accident.
2. **The trigger in §2.3 is checked at each phase gate**, so this is a decision
   that gets revisited rather than one that expires quietly.

## 8. Revisit triggers

- Someone needs a provider, format or operation we have decided not to build.
- A partner or deployment requires custom logic inside the platform.
- Geoprocessing demand outgrows what we are willing to implement ourselves —
  which is the cheapest place to start, per §2.4.

## 9. Dissent

**This is the decision most likely to be regretted, and the regret would arrive
slowly.** Nothing breaks by deferring. What happens instead is that in three
years the internals have grown a hundred small assumptions that no clean plugin
boundary can be drawn around, and the answer becomes "we cannot" rather than
"we chose not to".

§2.5's mitigations are cheap and real, but they are discipline rather than
structure, and discipline erodes. A stronger version of this decision would
build one extension point properly — geoprocessing, where isolation is already
free — as a real plugin boundary with a real contract, purely to keep the
capability alive. That was not chosen, and it should be reconsidered at the first
phase gate rather than left to the trigger alone.
