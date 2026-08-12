# Fresh Challenger Review — Round 2

**Status:** COMPLETE — findings raised, dispositions proposed
**Required by:** §67, §85
**Target:** the whole architecture as of 2026-08-12, after adversarial round 1
**Stance:** §67 — find every serious architectural mistake, hidden assumption,
GIS-specific omission, scalability issue, operational weakness, security problem
and unnecessary complexity. *"Do not defend the architecture."*

---

## A limitation that must be stated first

§67 asks for **a reviewer that did not participate in previous discussions.**

This review was written by the same agent that made every decision under review.
A different *stance* is available; a different *history* is not. Knowing why each
decision was made makes it substantially harder to see what an outsider would
notice immediately, and easier to unconsciously grade on a curve.

**A genuine fresh review by someone uninvolved is still owed**, and this document
does not discharge that requirement. It is the best available substitute, written
deliberately against the premises rather than the mechanisms, and against the
areas round 1 declared out of scope.

Treat its findings as real and its coverage as suspect.

---

| # | Finding | Severity | Kind |
|---|---|---|---|
| G1 | There is no answer to "why this instead of GeoServer" | **Severe** | Hidden assumption |
| G2 | "Governed publication boundary" is false for registered data | **Severe** | Premise failure |
| G3 | Multi-database was never costed against migrating the data instead | **Severe** | Unexamined alternative |
| G4 | The design never meets real spatial data | **High** | GIS omission |
| G5 | Multi-tenant isolation inside shared workers is unexamined | **High** | Security |
| G6 | Four platform stores buys a benefit nobody has evidenced | **High** | **RESOLVED 2026-08-12** — owner cut them (Q-51) |
| G7 | Our error messages and capability reports leak internal topology | Medium | Security |
| G8 | Upgrade and rollback are named in six places and designed nowhere | Medium | Operational |

---

## G1 — There is no answer to "why this instead of GeoServer"

**Severity: severe. This is the finding the other seven should be read through.**

Assessment §1 justifies the *category*: a GIS application server is a governed
publication boundary, and here is why one should exist. §10 defends the category
against static publishing.

**Nothing anywhere justifies this product against the alternatives that already
exist.** GeoServer is free, mature, has an ecosystem, does WMS, does WFS-T, has
been deployed for twenty years, and runs on a JVM every enterprise already
operates.

Now assemble what we have decided, from the perspective of the GIS administrator
we named as our primary user:

| Decision | What the administrator gets |
|---|---|
| Observed, not configured | Less direct control than they have today |
| Refuse rather than degrade | Errors where they used to get results |
| Best-effort cache coherence | A freshness guarantee they did not previously need |
| WMS out of v1 | Cannot migrate a large part of their estate |
| Editing through our API only for data, schema at source | Two places to make changes |
| Registration as a job | A slower first five minutes |
| Four stores, three dialects | More to test, more to break |
| No plugin system | No ecosystem |

Every one is individually defensible. **Collectively they describe a product that
is harder to operate, harder to migrate to, and less capable than the incumbent
it intends to displace.**

That may still be the right architecture. But the reason to adopt it has never
been written down, and until it is, §18's "proposed initial architecture" is
justified only against itself.

**Disposition: the assessment needs a positioning section**, and it is not
optional decoration. It should answer, in one page: what does this do that
GeoServer cannot, for whom, and why is that worth the migration cost? If the
answer turns out to be thin, that is the most important thing Phase 0 could
discover — and discovering it now is worth more than any benchmark.

My own attempt, offered so the gap is visible rather than filled: multi-provider
governance with one capability model, a runtime that holds 1,000 services on one
machine, and vector-first as a deliberate simplification rather than an
accumulation. **None of those has been tested against a real buyer.**

---

## G2 — "Governed publication boundary" is false for registered data, which we said is the normal case

**Severity: severe. The stated purpose and the delivered thing do not match.**

Assessment §1 defines the product as a governed publication boundary and lists
five problems it solves: credentials sprawl, per-layer authorization,
reimplemented hard parts, invisible consumers, no publication boundary.

Now check each against **registered** data — which [data-model.md](../data-model.md)
§3 explicitly calls "the normal case, and everything should be designed around it
being the default":

| Claimed governance | Reality on registered data |
|---|---|
| Per-layer authorization | Partly delegated to row-level security "where the provider supports it" (assessment §8) — we govern by asking the thing we are supposed to be governing |
| Audit and visibility | Incomplete by design: writes bypass us (A-027) and we cannot see them |
| Publication boundary | The schema changes under us; we detect drift and follow |
| Consistency of interpretation | Best-effort cache coherence, bounded by a poll interval |
| Credentials | Genuinely solved — consumers hold ours, not the database's |

**One of five holds unconditionally.**

For hosted data all five hold. So the honest description is: **hosted data gets a
governed publication boundary; registered data gets a capable read proxy with a
catalog.** Both are useful. They are not the same product, and §1 describes only
the first while data-model.md designates the second as the default.

**Disposition: §1 must be rewritten to distinguish the two modes**, and the
product must decide which one it is actually selling. If registered is the normal
case, the honest pitch is a governed *access* layer, not a governed *publication*
layer — and several decisions that were justified by "governance" need
re-examination under the weaker claim.

This is not a wording problem. It is the difference between a product that owns
its data's lifecycle and one that observes someone else's.

---

## G3 — Nobody asked whether we should move their data instead of reading it

**Severity: severe. This is the hidden assumption §67 asks for.**

The owner's requirement was concrete and reasonable: an Oracle shop should not
have to run PostgreSQL because of us. We answered it by making Oracle and SQL
Server first-class spatial providers.

**The alternative was never evaluated: support PostGIS excellently, and ship
migration tooling that moves Oracle and SQL Server data into the datastore.**

For a product whose stated goal is *displacing* ArcGIS Server and GeoServer, "we
move your data" is arguably a better answer than "we read your Oracle" — and it
would delete an enormous amount of what we have just designed:

- three dialect compilers, and F1's Phase 1 forcing function
- capability negotiation as a core concern rather than a file-provider detail
- three transaction semantics for editing
- three connection cost profiles
- the `ST_AsMVT` gap, and with it A-019, A-021 and much of ADR-008 §4.8
- four platform stores (see G6)
- A-035's unbounded data source cardinality

That is not a small simplification. It is possibly the largest single
simplification available to this architecture, and **it was never written down as
an option, let alone rejected with reasons.**

Arguments against it exist and are real: organisations will not hand us custody
of their authoritative data; other systems already write to those databases;
copies go stale; and the owner explicitly restored hosting *and* registration as
both first-class. But those arguments were never made, because the question was
never asked.

**Disposition: write the alternative up and reject it explicitly, or adopt it.**
An unexamined alternative of this size is exactly what §8 means by evaluating
alternatives, and the assessment currently has a decision with no counterfactual.

---

## G4 — The design has never met real spatial data

**Severity: high. This is the GIS-specific omission §67 asks for, and it is a large one.**

The query AST models collections, predicates, bboxes, CRS, sort and pagination.
Real spatial data is messier than that, and none of the following appears
anywhere in nine ADRs:

- **Invalid geometry.** Every real dataset has self-intersecting rings, duplicate
  vertices, unclosed polygons. What happens when a source table contains them?
  Serve as-is, repair, skip, refuse? PostGIS `ST_IsValid` disagrees with SQL
  Server's validity model, which disagrees with Oracle's. **This is the single
  most common real-world GIS problem and we have not mentioned it once.**
- **Wrong or missing SRID.** Extremely common in enterprise data. A table
  declared 4326 holding projected coordinates will produce silently wrong output
  everywhere.
- **Datum transformation selection.** When multiple transformation paths exist
  between two CRS, PROJ picks one. Accuracy differs by metres. For cadastral or
  survey work that difference is legally significant. Can an administrator pin a
  transformation? Not designed.
- **Z and M coordinates.** Never mentioned. MVT discards Z. What do feature
  responses do? What does editing do to a Z value it did not send back?
- **Curve geometry.** `CircularString` and friends exist in Oracle and SQL
  Server, partially in PostGIS, and not at all in MVT or GeoJSON. Our AST has no
  concept of them, so the capability model cannot express the gap.
- **Enormous single geometries.** A national coastline as one polygon. Tile
  clipping handles it; a feature response does not, and §49's response-size
  governance would simply refuse it.
- **Mixed geometry types** in one table, and geometry type enforcement.
- **Character encoding and collation.** Oracle NLS settings, Turkish dotless-i
  collation. Attribute filters that work on one provider and not another for
  reasons that have nothing to do with spatial capability.

**Disposition: a geometry and CRS reality pass is required before Phase 1.** Not
an ADR — a specification of policy: what we do with invalid geometry, wrong
SRIDs, Z/M, curves and oversized features, per provider. Every item above will
otherwise be discovered by a user, in production, as a wrong answer rather than
an error.

`experiments/geometry-oracle` was scoped for correctness comparison between
engines. It should be extended to cover this: adversarial *real* data, not
adversarial synthetic data.

---

## G5 — Multi-tenant isolation inside a shared worker is unexamined

**Severity: high. Security.**

ADR-007 puts many services in one multi-tenant worker, sharing L1 memory,
sharing a connection pool per data source, sharing a process. Round 1 declared
this out of scope. It should not have been.

Concrete questions with no current answer:

- **Is the tenant part of the cache key?** ADR-010 says the key is plan identity
  plus schema fingerprint. If two tenants can produce the same plan against the
  same layer with different authorization, **a cache hit is a data breach.**
  Nothing in ADR-010 mentions the principal.
- **Row-level security and connection pooling are in direct tension.** RLS
  depends on the database session's identity. A pooled connection shared across
  services and users either carries one identity — defeating RLS — or must reset
  it per request, which is a per-request round trip nobody has costed. Assessment
  §8 proposes deferring authorization to RLS; ADR-007 §4.8 pools per data source.
  **These two decisions are incompatible as written and neither mentions the
  other.**
- **Does a service's warm context leak anything across services?** Prepared
  statements, cached metadata, connection state.
- **Resource isolation.** One tenant's expensive query degrades another's. §49's
  per-service limits are named but there is no per-tenant budget.

**Disposition: the RLS-versus-pooling conflict is a blocking contradiction** and
must be resolved before either decision is relied on. Options are per-principal
pools, `SET ROLE` per request with the round trip costed, or abandoning RLS
delegation and doing authorization entirely ourselves — which would remove one of
assessment §8's four takeaways from the thin-server study.

Tenant identity in the cache key is a one-line fix and a severe bug if forgotten.

---

## G6 — Four platform stores buys a benefit nobody has evidenced

**Severity: high. Unnecessary complexity, which §67 asks about specifically.**

The platform store is portable across SQLite, PostgreSQL, SQL Server and Oracle.
The justification: an Oracle shop should not have to run PostgreSQL.

**But SQLite already solves that completely.** It is embedded, needs no install,
and the platform store is a few thousand rows of non-spatial data. An Oracle shop
runs our binary with a SQLite platform store and never touches PostgreSQL.

So what do SQL Server and Oracle as *platform stores* actually buy? The stated
reason is that some organisations have policy requiring persistent state in
managed, backed-up database infrastructure.

**That is an assumption with no evidence, and it is expensive:**

- four dialect implementations of the store module
- four sets of migration scripts, forever
- four claim implementations for the job queue, which ADR-011 itself calls the
  strongest argument against the portable store, where locking bugs surface
  rarely and under load
- a four-way CI matrix that must actually run

§82 asks what concrete problem this solves. **The concrete problem is solved by
SQLite. The remaining justification is a policy nobody has confirmed exists.**

**Disposition: cut SQL Server and Oracle as platform stores** unless the policy
requirement can be evidenced with a real deployment constraint. Keep them as
data providers, where the requirement is genuine and owner-stated. Keep SQLite
and PostgreSQL as platform stores — SQLite for embedded, PostgreSQL for
multi-node and for shops that already have it.

This removes two dialect implementations, two migration paths, two claim
implementations and half the test matrix, at the cost of a capability whose
demand is hypothetical. It is the cleanest available application of §82 to our
own work.

**Accepted by the owner, 2026-08-12 (Q-51).** Platform stores are SQLite and
PostgreSQL. SQL Server and Oracle remain first-class providers and datastores.
ADR-011's "strongest argument against the portable platform store" — four
locking implementations — is retired with it.

Residual, stated honestly: SQLite cannot hold shared state across nodes, so a
multi-node Oracle shop runs a small PostgreSQL for metadata. Much smaller than
running PostGIS for their data, and clustering is deferred regardless.

---

## G7 — Our error messages and capability reports leak internal topology

**Severity: medium. Security.**

ADR-008 §4.3 and ADR-005 §3.5 require a refusal to name "the provider, the
unsupported operation, and the alternative". ADR-005 §3.4 publishes a capability
report per collection, derived from provider negotiation.

Both are good for usability and both **tell an unauthenticated or low-privileged
client what database engine sits behind a layer, what it can and cannot do, and
by implication its version.**

That is reconnaissance. It also reveals the internal provider topology of an
organisation to anyone who can reach a public layer.

**Disposition: capability reports and detailed errors are authorization-scoped.**
An authenticated administrator sees the provider and the reason; an anonymous
client sees the capability in abstract terms and a generic refusal. This costs
almost nothing if designed now and is awkward to retrofit once clients depend on
the detailed form.

---

## G8 — Upgrade and rollback are named in six documents and designed in none

**Severity: medium. Operational.**

Q-13 has existed since the first day. ADR-002 §4.5 says migrations exist from day
one. Nothing further.

Under the current architecture an upgrade must handle: schema migration across
every supported platform store; the export/import format's compatibility across
versions; **plan identity stability, or every cache in the deployment silently
invalidates** (Q-45); service definition compatibility; the OGC API version
surface and its extensions; and rolling worker replacement.

That is a substantial subsystem and it has no owner and no ADR. For a product
whose primary user is an administrator, **upgrade is a feature, not an
afterthought** — and the first upgrade is the moment a platform earns or loses
trust permanently.

**Disposition: upgrade and rollback need an ADR before Phase 1**, not before
Phase 6. Deciding it late is how the data model acquires assumptions that make
migration impossible.

---

## What this review still did not examine

- **Performance**, because there are no numbers.
- **The entity model**, because it does not exist.
- **The compatibility layer's design**, beyond scope.
- **Whether the owner's product instincts are right.** Several decisions —
  vector-first, no WMS, editing at source — are owner calls this review took as
  given. A genuinely independent reviewer would question them, and should.
- **The competitive landscape in any depth**, which G1 says is missing and which
  this review is not equipped to supply.
