# Honua Server — the closest direct peer found so far

**Status:** FIRST PASS — from the public README only. Everything marked `VERIFY`.
**Source:** <https://github.com/honua-io/honua-server>
**Raised by:** project owner, 2026-08-12
**Feeds:** Q-49 (competitive position), Q-50 (providers versus migration),
Q-17 (ArcGIS-compatible surface), [ADR-001](../adr/ADR-001-core-language.md)

---

## ⚠ Clean-room boundary — read this first

Honua Server is **Elastic License 2.0**. Its source is visible, which makes it
more tempting to read and no less dangerous to.

**Permitted:** the README, published documentation, the protocol surface it
advertises, its architectural choices as publicly described, and its observable
behaviour if we ever run it.

**Not permitted:** reading the implementation, then writing our own version of
what we read. That is a clean-room violation under our own §5 and a legal risk
under ELv2. It applies with more force here than to ArcGIS, not less, because
the code is right there.

The value in this project is *what it supports* and *which bets it took*. Both
are fully visible without opening a source file.

## 1. What it is

`VERIFY` all of the following against the current README.

> "One container exposes the same PostGIS-backed data through every major GIS
> protocol" — without duplication or ETL.

| | |
|---|---|
| Runtime | .NET 10 |
| Primary backend | PostGIS, read and write |
| Other sources | DuckDB, SQL Server, Oracle, MySQL/MariaDB, Redshift, Snowflake, Databricks — **read-only** |
| Cache and jobs | Redis, with in-memory fallback |
| Deployment | Stateless, container-first, Kubernetes via Helm |
| Observability | OpenTelemetry |
| Licence | Elastic License 2.0, open core with paid entitlements |

**Protocols:** GeoServices REST (FeatureServer, MapServer, ImageServer,
GeometryServer, GPServer), OGC API (Features, Maps, Tiles, Coverages, Processes,
Records, EDR, Styles), classic WMS/WFS/WMTS/WCS, STAC, OData v4, MVT, 3D Tiles,
gRPC with h2c, and MCP over JSON-RPC for AI agents.

**Maturity:** `VERIFY` 4 stars, 1 fork, 4,608 commits, 56 open issues, **no
tagged releases** — nightly container builds only.

## 2. The one idea we should probably take

**PostGIS is read/write; every other source is read-only.**

We framed Q-50 as a binary: read their Oracle in place, or move their data into
our datastore. Honua takes a third path we never wrote down, and it is smaller
than either.

What read-only providers delete from our design:

- provider-dependent **transaction semantics** across three engines — isolation
  levels, locking, what a conflict looks like — which
  [ADR-008](../adr/ADR-008-query-engine.md) currently carries as a known gap
- **write-side capability negotiation** entirely
- editing concurrency against a database we do not control, and with it much of
  **A-027**, the assumption that concurrency can be correct against writes we
  never see
- **Q-41**, the companion-schema question, since bookkeeping only matters where
  we write
- the editing half of the **RLS-versus-pooling conflict** (debt D-01), though
  the read half remains

What it costs: an organisation on Oracle cannot edit through us. They edit at
source — which the owner already said is how schema changes work anyway
([data-model.md](../data-model.md) §5), so the gap is narrower than it sounds.

**This is a serious candidate answer to Q-50 and it should be written into that
question as a third option.** It preserves the owner's requirement — an Oracle
shop is served without running PostgreSQL for their data — while removing most
of what makes multi-provider expensive.

## 3. What it tells us about ADR-001

Someone built a full multi-protocol GIS server in **.NET**, one of our two
prototype candidates, with 4,608 commits behind it.

That is not a benchmark and must not be treated as one. It is evidence that the
runtime is *adequate* for this workload, which is a weaker claim than our
prototype needs to make but a real one. It slightly raises the prior on .NET and
changes nothing about the requirement to measure.

## 4. Where it bet the opposite way

The contrast is more useful than the overlap, because each difference is a place
where one of us is wrong.

| Question | Honua | Us | Note |
|---|---|---|---|
| Protocol surface | **Everything, natively** | Narrow native API plus a compatibility layer | Ours assumes a narrow surface is easier to keep correct. Theirs assumes breadth is the product. |
| Redis | Cache **and durable job queue** | Optional cache, never load-bearing; jobs in the platform store, no broker | We rejected a broker under §82. They took the dependency. |
| Kubernetes | First class, Helm charts | Deprioritised until the platform works without it (§79) | |
| Rendering | MapServer and ImageServer surfaces — rendered output | Vector-first, client renders | Different products, not different implementations of one |
| Non-PostGIS sources | Read-only | Read and write, first class | §2 above |
| Licence | ELv2 open core, no managed-service rights | Copyleft acceptable, undecided | A product versus a commons |

**The protocol breadth difference is the sharpest.** Their pitch is that one
container speaks everything. Ours is that a narrow, well-specified native API
plus honest compatibility adapters is easier to keep correct. Those cannot both
be the better answer, and neither of us has evidence.

## 5. What it means for Q-49

Q-49 asks what we do that GeoServer cannot, and why it is worth a migration.
Honua sharpens rather than answers it, in two directions.

**In our favour:** someone else independently identified the same gap —
multi-protocol access over PostGIS with an ArcGIS-compatible surface — and
committed thousands of commits to it. The gap is not imagined.

**Against us:** `VERIFY` 4,608 commits have produced 4 stars, 1 fork and no
tagged release. Whatever the gap is, it is **hard to convert into users**, and
that is a caution rather than an encouragement. It may be very new, it may be a
solo effort, it may be under-promoted — but a competitor's difficulty finding an
audience is data about the market, not only about them.

**And it narrows the space.** If our answer to Q-49 turns out to be "multi-
protocol access over PostGIS", that answer is now taken, by a project with a
four-year head start in commits and a commercial licence. Our differentiator has
to be something else.

Candidates, none tested: the runtime that holds 1,000 services on one machine;
governance as the product rather than protocol breadth; vector-first as a
deliberate reduction; genuinely open licensing against an open-core competitor.

## 6. Also worth noting

**MCP over JSON-RPC for AI agents.** We have not considered this at all. Whether
it belongs in a GIS server is a real question — it is either a genuine emerging
surface or protocol fashion, and there is no evidence yet either way. Recorded
rather than adopted.

**OData v4.** Also absent from our thinking. Relevant to enterprise BI
integration, and cheap if the query AST is genuinely protocol-neutral
([ADR-005](../adr/ADR-005-api-architecture.md) §3) — which is a decent test of
whether our neutral interface really is neutral.

## 7. What to do next

1. **Read their published docs properly**, not just the README, and build an
   accurate protocol-coverage comparison. Source stays closed to us.
2. **Add read-only-providers as a third option to Q-50.**
3. **Run it**, if a container is available. Observing behaviour is permitted and
   more informative than reading about it — especially for the GeoServices REST
   surface, which is Q-17's feasibility question.
4. **Do not let this become a feature-parity exercise.** §1 of the master prompt
   is explicit that the goal is architectural excellence rather than feature
   imitation, and a competitor's protocol matrix is exactly the kind of thing
   that quietly becomes a backlog.
