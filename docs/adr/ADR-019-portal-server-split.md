# ADR-019 — One deployable, three fused tiers

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM-HIGH` for the decision · `LOW` for the trigger conditions in §5 |
| **Decided** | 2026-08-14 |
| **Answers** | Owner question: *Portal and Server are two separate apps in the ArcGIS world — does that make sense on our end?* · opens **Q-93** |

---

## 1. What the ArcGIS split actually is

Three products, not two:

| Tier | Holds | Our nearest thing |
|---|---|---|
| **Portal for ArcGIS** | Items, users, groups, roles, user types, sharing. The catalogue and the identity system | `Graticula.Platform` — the platform store, identity, and now ADR-018's sharing model |
| **ArcGIS Server** | Services, workers, pools, compute. Its own admin API and, unfederated, its own token auth | `Graticula.Host`, the providers, ADR-007's runtime |
| **ArcGIS Data Store** | Managed storage backing the *hosting server*'s hosted layers | Our datastore, made **mandatory** by Q-69 and Q-70 |

**Federation** is the join: a Server site is federated with a Portal, which then
becomes its identity provider and item catalogue. One designated federated site
is the *hosting server*, and that is the one with a Data Store attached.

---

## 2. Why Esri has three, and how much of it is about architecture

Four reasons. Only one is technical.

1. **Chronology.** ArcGIS Server predates Portal by roughly eight years. Portal
   exists to bring ArcGIS Online's item-and-sharing model on-premises, and it
   was added beside Server rather than into it. A greenfield design would not
   arrive here by reasoning.
2. **Licensing and packaging.** They are separately sold and separately
   licensed. ~~We are Apache-2.0 with no licence to meter — Q-73 — so this reason~~
   **Corrected 2026-08-25 by [ADR-047](ADR-047-the-outbound-licence-is-elastic-2.md):
   we are Elastic License 2.0.** The argument is unchanged and is now slightly
   stronger — ELv2 has a licence-key clause and this product has no key to enforce,
   so there is still nothing to meter — but the licence name in it was wrong, and
   this reason
   evaporates entirely.
3. **One catalogue over many serving sites.** *This is the real one.* A large
   organisation federates several Server sites under one Portal: imagery on one,
   geoprocessing on another, hosted features on a third, each sized and
   scheduled differently. That genuinely needs the catalogue to be separable
   from the compute.
4. **Portal as the enterprise identity broker** for everything ArcGIS. Real, but
   ADR-015 already puts OIDC and SAML in our own server, so we would be
   duplicating rather than delegating.

**Only reason 3 survives contact with our constraints**, and it only bites above
the scale CLAUDE.md §7 set — 100 to 1,000 services — and §60's rule cuts against
paying for it now: *do not make small deployments painful to serve hypothetical
huge ones.*

---

## 3. Decision — one deployable, and we should say what it is

**Graticula is Portal, Server and Data Store fused into a single deployable.**

That sentence is the decision, and stating it resolves several choices that
otherwise look inconsistent with calling ourselves a *server*:

- **Q-69 and Q-70 made the datastore mandatory.** In ArcGIS, Data Store is
  needed only where hosting happens. We made it universal — that is the Data
  Store tier fused in, and it is why ADR-002's platform store has somewhere
  guaranteed to live.
- **ADR-018 imported items, owners, sharing, roles and user types.** Those are
  *Portal* concepts. A standalone ArcGIS Server has no items and no sharing — it
  has services and a token. We now have both halves in one process.
- **ADR-017's admin API manages users, roles and certificates as well as layers
  and workers.** That is a Portal admin surface and a Server admin surface in
  one place.

None of those were mistakes. But until now the product had been describing
itself as a GIS *server* while quietly assembling all three tiers, and the
fusion is the actual shape.

**Why fusion is the right shape for us, beyond being where we already are:** the
baseline deployment target in CLAUDE.md §6 is one process against one
PostgreSQL. A three-process, federated topology for a deployment publishing
fifty layers is exactly the overengineering §82 exists to refuse. Esri's split
is the correct answer to Esri's problem — thousands of services across
heterogeneous compute, sold as separate products — and we do not have that
problem.

---

## 4. What fusing costs, and the seam that must survive it

Fusion buys simplicity and spends **isolation**. Esri gets one thing for free
that we now have to work for: when Portal is down, a federated Server keeps
serving.

[ADR-017](ADR-017-admin-api.md) §6 already found this and called it the
degraded-mode problem — *the platform store lives in the datastore, so if the
datastore is down the admin API loses users, sessions, layer definitions and job
records, at exactly the moment an administrator most needs to look.* At the time
that read as an awkward consequence of Q-69. It is more than that:

> **It is the price of fusing the tiers, and ADR-017 §6's degraded surface is
> the test of whether the seam between them is real.**

So the seam is kept, inside the one process:

| Domain | Owns | Must survive without |
|---|---|---|
| **Catalogue** | items, identity, sharing, jobs, audit | — |
| **Runtime** | workers, contexts, pools, serving | the catalogue |

Two rules follow, and they are conditions rather than aspirations:

- **The request path must not read the catalogue.** A service context is
  resolved once and held; a query that consults the platform store per request
  makes the catalogue a hard dependency of serving, and the seam is then
  decorative.

  **This rule was not kept, and the refusal is the more interesting decision.**
  The catalogue is read on every feature query, because a catalogue entry
  carries the sharing scope and the started/stopped status — so holding it would
  be caching an authorization decision, and read literally this rule asked for a
  layer made private to stay readable. §7 records the reasoning;
  [Q-95](../open-questions.md) records what was done about the availability cost
  instead. The rule stands as written for everything *except* authorization,
  which is where it turned out to be aimed.
- **Runtime admin works without the catalogue.** ADR-017 §6's minimal surface —
  health, version, workers, certificates — is served from what the process knows
  rather than from the store.

---

## 5. What would make us split, later

Recorded now so the decision is revisitable on evidence rather than on mood:

1. **Heterogeneous compute under one catalogue** — imagery workers with GDAL and
   large memory beside feature workers that need neither. This is reason 3 from
   §2 and is the only one that would force it.
2. **Independent scaling proven by measurement**, not by anticipation: the
   catalogue and the serving tier having genuinely different load curves in a
   real deployment.
3. **A hard isolation requirement** — a tenant or regulator requiring the
   management plane on separate hardware.

**None is a v1 or v2 concern**, and all three are downstream of
[ADR-012](ADR-012-clustering.md), which is deferred. If clustering is ever
reopened, this ADR is reopened with it: multi-node and multi-tier are the same
question asked twice.

---

## 6. What this does *not* decide

**Two API faces is a separate question from two apps.** ArcGIS exposes
`/sharing/rest` (Portal: items, users, groups, sharing) and
`/rest/services` plus `/admin` (Server). We already serve the second shape. A
Portal-compatible `/sharing/rest` face over the same catalogue is protocol work
under ADR-005 and [protocol-surface.md](../protocol-surface.md) — **speaking two
faces is not being two apps**, and it is how an ArcGIS client would recognise
our catalogue at all.

**Being federated *into* somebody else's Portal is a different question again**,
and a more interesting one commercially. An organisation that already runs
Portal for ArcGIS and wants a cheaper serving tier underneath it would need us
to act as a *federated server*: accept Portal-issued tokens, expose the Server
admin API Portal expects, and register ourselves. That is a product decision
with real reach — it is the difference between replacing ArcGIS Enterprise and
slotting into it — and it is **Q-93**, not this ADR.

---

## 7. Consequences

- **[product-context.md](../product-context.md) gains the fusion sentence.**
  Describing ourselves as *a GIS server* has been understating what is being
  built, and it makes the mandatory datastore look arbitrary when it is
  structural.
- **ADR-017 §6 is promoted.** Its degraded surface stops being a robustness
  nicety and becomes the load-bearing test of §4's seam. Its condition 1 —
  *tested by stopping the datastore* — is now this ADR's condition too.
- **A real coupling is exposed today, and it is ours.** *(Amended 2026-08-14.)*
  `PostgresLayerCatalog.FindAsync` is called on every feature query, so the
  request path depends on the catalogue store. §4's first rule was written as a
  principle before it was checked, and it was false.

  It is now **half true, and the other half is a decision rather than a debt.**
  The runtime no longer re-derives the table's shape from the data source on
  every request — that is cached (D-17, measured: 5.76 ms → 1.64 ms of fixed
  overhead). The catalogue read stays, because a catalogue entry carries the
  sharing scope and the started/stopped status, and a runtime that remembered
  those would keep serving a layer after it was made private. **The seam this
  ADR wanted would, taken literally, have cached an authorization decision.**
  So the seam is real for *shape* and deliberately absent for *authorization*,
  and the availability cost of that — serving stops when the store does — is
  [Q-95](../open-questions.md) rather than something quietly accepted here.

  **Q-95 was answered by the owner on 2026-08-15 — serve public-only while
  blind — and built as `CatalogFallback`.** It buys the availability back for the
  only data where a stale grant costs nothing: while the store cannot be reached,
  a service that was public in the last answer is served from that memory, for a
  bounded window. It is deliberately **not** a read-through cache — while the
  store answers, every request still reads it, so revocation stays instant — and
  only connectivity failures fall back, because serving stale data on account of
  a defect in our own SQL would hide the defect behind a degraded mode that looks
  deliberate.

  **§4's seam therefore exists in three grades rather than two:** real for shape
  (cached), absent for authorization (by decision), and time-boxed for
  availability (this). None of it makes the fused deployable as isolated as
  Esri's split — a private layer still stops being servable when the store does —
  and that residue is the price of §3, stated rather than closed.
- **ADR-012 inherits a second axis.** Its state inventory must now say, for each
  piece of state, not only *node-local or shared* but *catalogue or runtime*.
- **Q-93 opened** — federation into an existing ArcGIS Portal.

## 8. Conditions

1. **The degraded surface is tested by stopping the datastore** (ADR-017
   condition 1, inherited). Until that test exists, §4's seam is a claim.
   **PARTLY DISCHARGED**, and the remaining half is the half the condition asked
   for. `CatalogFallbackTests` pins the *policy* in twelve cases — that every
   request reads the store while it answers, that a change of sharing is seen at
   once, that the last answer is served when the store goes quiet, that a service
   never seen is not invented and a deleted one stays deleted, that the memory
   expires, that a zero window serves nothing, and that a server-side error is
   not a degraded mode. All of it runs against a fake store, which is what makes
   the failure modes reachable at all. ~~**Nothing yet stops a real datastore**, so
   ADR-017 §6's minimal admin surface under an actual outage is still unproven,
   and that is the case an operator meets at 2 AM.~~
   ***(Discharged 2026-08-27 — [tools/outage-rehearsal.sh](../../tools/outage-rehearsal.sh)
   stops the real datastore under a running server and reads every surface ADR-017 §6
   names.)*** The run is four walks: with the store up, with it stopped, immediately after it
   comes back, and past the breaker's ten-second cooling window. **What it found is three
   things, and only the first was expected.**
   **`/admin/health` degrades correctly** — `status: degraded`, `platformStore.reachable:
   false`, the error redacted, answering anonymously because sessions live in the store and
   during the outage nobody can authenticate. §4's seam is where the document says it is.
   **Three of §6's four rows do not exist.** `/admin/version`, `/admin/certificates` and
   `/admin/workers` are 404 with the store up and 404 with it down. `/admin/health` is the
   whole of the minimal surface, and it carries the version, the certificate (ADR-014
   condition 1) and the runtime block that `/admin/workers` was for — so what is missing is
   the addresses rather than the answers, which is the same finding
   [ADR-017](ADR-017-admin-api.md) condition 2's walk made about §3.2.
   **And the server told every administrator they were not signed in** — that is
   [D-192](../architecture-debt.md), found here, closed here.
   **Recovery is automatic and bounded**: the breaker keeps refusing for up to ten seconds
   after the database returns, which is what stops an outage becoming a queue collapse, and it
   clears on its own. The rehearsal walks both sides of that window so the ten seconds is
   measured rather than assumed.
   **This does not run in CI**, and that is stated rather than hidden: it needs Docker and a
   container this machine happens to have. `OutageRefusalTests` is the portable half.
2. **A performance claim about serving includes the catalogue read**, rather than
   being made as though §4's first rule had been kept.
   *(Rewritten 2026-08-16, and the previous wording is worth recording.)* This
   condition used to require that the request path **stop** reading the catalogue
   per request. §7 then decided the read stays, because removing it would cache
   an authorization decision — so the condition was asking for the opposite of
   its own ADR's decision and could never be discharged. It sat in the live pile
   anyway, which is how one unclosable condition makes the remaining work look
   larger than it is, and it is the second time a register has been wrong in that
   direction. What is actually owed is the honesty of the measurement: D-17's
   fixed-overhead figures are per request *with* the catalogue read, and any
   later figure that drops it says so.
   **DISCHARGED 2026-08-24.** Every performance claim about serving names the catalogue read
   rather than quietly excluding it.
   [benchmarks/feature-query](../../benchmarks/feature-query/RESULTS.md) decomposes a request
   into lookup, prepare, driver, decode and serialise and reports the catalogue read as
   **1.8 ms flat on every query — 70% of a one-feature request, and 2.6x the cost of the data
   query it precedes**, which is the opposite of dropping it: the read is the headline.
   [benchmarks/statement-timeout](../../benchmarks/statement-timeout/RESULTS.md) round 2 states
   in its own text that its numbers are store-side and why, so a figure that *does* exclude this
   server's overhead says so on the page. The condition asked for honesty rather than for a
   number, and that is what these carry.
3. **Every subsequent ADR states which of its state is catalogue and which is
   runtime**, extending ADR-012's existing inventory requirement.
   *(Discharged 2026-08-27, and it was **1 of 29** when it was measured.* Of the
   twenty-nine ADRs written after this one, exactly one — ADR-029 — said anything about
   where its state lives, and that was *node-local* about the tile cache in passing.
   Twenty-nine decisions had been taken without the inventory this condition and
   [ADR-012](ADR-012-clustering.md) both ask for, and nothing noticed, because **a
   requirement written in one ADR's prose is a requirement nobody reads twice**. That is
   [D-130](../architecture-debt.md)'s shape applied to a rule rather than to a fact.
   All twenty-nine now carry a **`**State.**`** line at the end of their Consequences,
   saying what the decision puts in the catalogue and what it holds at runtime, with
   node-local against shared where they differ. **Most of them store nothing**, and
   saying so is the point. Counted: **twelve of the twenty-nine hold no state at all** —
   five say *None* outright and seven say it on both axes — and **six more put nothing in
   the catalogue** and hold only a node-local cache. Eleven store something. That
   distribution is a fact about this design worth being able to see, and nobody could
   have seen it before today. The ones that do —
   groups, roles, jobs, coverages, styles, the two log tables, the capability ceiling —
   are all catalogue, and the runtime side is almost entirely node-local caches whose
   whole safety argument is that being stale about them costs nothing.
   **And it is checked now**, by `registers-check.py`: an ADR after 019 with no `**State.**`
   line fails the build. What the check cannot do is decide whether the sentence is *true* —
   `**State.** None.` on an ADR that adds a table would pass — and that is stated in the
   check's own docstring rather than left to be discovered. What it buys is that the
   question gets asked, which at 1 of 29 was the whole failure. **Falsified** by deleting
   ADR-044's line: the check names the file and passes when it is put back.)*
## 9. Assumptions

| ID | Assumption | Status |
|---|---|---|
| A-053 | A fused deployable remains operable at the top of the stated scale target — 1,000 services | `UNVALIDATED`. ADR-007's runtime is designed for it; nothing has been run at that scale |
| A-054 | Catalogue load and serving load stay similar enough that separate scaling is never forced | `UNVALIDATED`, and the one most likely to break first. A tile-heavy deployment hammers the runtime and barely touches the catalogue |

## 10. Dissent

**Against fusing.** Esri arrived at three tiers with far more operational
experience than we have, and reason 3 in §2 is a real architectural driver, not
an accident of history. Building the seam only as a module boundary means that
if it is ever needed as a process boundary, we will discover every place the
boundary was crossed for convenience — and those places are found under load, at
the worst moment. §8's conditions are the mitigation, and conditions get
relaxed.

**Against naming ourselves the fusion of three products.** It invites the
comparison on all three fronts simultaneously, against a competitor with two
decades of head start on each. The counter is that we are building it anyway,
and a product that will not say what it is cannot be evaluated — or chosen.
