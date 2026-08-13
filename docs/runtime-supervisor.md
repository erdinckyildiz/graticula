# Runtime Supervisor

**Status:** FIRST DESIGN — closes the severe gap found by
[failure-scenarios.md](failure-scenarios.md) N5.
**Required by:** §21. **Depended on by:** [ADR-007](adr/ADR-007-service-runtime.md),
which wrote recycling, draining, quiescing and observed escalation as if this
existed.
**May be promoted to an ADR** if the routing question in §9 turns out to be
contentious. For now it is a design document feeding ADR-007.

---

## 1. The principle

> **A management-plane failure must not become a data-plane failure.**

The supervisor manages workers. If the supervisor dies, workers keep serving and
a restarted supervisor re-adopts them. If the platform store is unreachable,
bound contexts keep serving ([failure-scenarios.md](failure-scenarios.md) N1).
Same principle in both places: **losing the ability to change things must not
mean losing the ability to serve them.**

This is the design constraint that shapes everything below.

## 2. It is not a distinguished node, and the distinction matters

ADR-007 §4.10 forbids a distinguished node, citing Esri's removal of the SOM at
10.1. A supervisor process looks like exactly what was forbidden.

**It is not, and the difference is not a technicality.** ArcSOC's SOM was a
**site-wide manager** that other machines depended on — a single point of
failure for the whole deployment and a thing to provision, recover and
configure separately.

Ours is **per node and local**. Nothing on another node depends on it. A node
whose supervisor is unhealthy is one degraded node, not a degraded site. Every
node has one; none is special.

The rule ADR-007 was protecting is *no component that other nodes depend on*.
A local process manager does not violate it.

## 3. Two levels, each doing what it is good at

```text
systemd / Kubernetes / Windows Service
        │  keeps one process alive. Coarse, reliable, already exists.
        ▼
   SUPERVISOR  (tiny, boring, serves no requests)
        │  spawns, watches, drains, recycles, coordinates
        ▼
   WORKERS  (request workers, job workers)
```

**We do not reimplement init.** The platform restarts the supervisor; the
supervisor does the GIS-specific work the platform cannot: draining in-flight
requests, recycling on memory growth, detecting a stuck request, coordinating a
data source quiesce across workers.

**The supervisor serves no requests and holds no request state.** It is the
component that must not crash, so it stays small. Anything that tempts it toward
the data path — routing, caching, proxying — is a design smell and belongs
elsewhere (§9).

## 4. Responsibilities

From §21, with what each actually means here.

| Responsibility | What it does |
|---|---|
| **Start and stop workers** | Spawn to the configured count, sized to cores and memory, never to service count (ADR-007 §2) |
| **Detect crashes** | Process exit, and distinguish clean shutdown from a fault |
| **Restart** | With backoff. A worker that crashes repeatedly on startup must not spin. |
| **Drain** | Stop new work, finish in-flight, exit within a bounded timeout, then force |
| **Recycle** | On observed memory growth, on administrator request, on deployment. **Not on a schedule** (ADR-007 §4.7) |
| **Health** | Liveness, readiness, and stuck detection — see §5 |
| **Resource limits** | Enforce per-worker memory and concurrency ceilings |
| **Coordinate quiesce** | A data source operation spanning all workers — see §7 |

**Explicitly not its job:** refreshing a service context on a config or schema
change. ADR-007 §4.6 made the context the unit of refresh, not the worker, so
that path never reaches the supervisor. This is §17's layering paying off again.

## 5. Health — heartbeats detect death, not stuckness

The most important thing in this document, and the easiest to get wrong.

| Signal | Answers | Mechanism |
|---|---|---|
| **Liveness** | Is the process alive? | Process state. The platform can do this. |
| **Readiness** | Can it accept work? | Worker reports. Used for drain and startup. |
| **Stuckness** | Is it *making progress*? | **Not a heartbeat.** |

**A heartbeat thread will happily beat while every request thread is
deadlocked.** A worker can be alive, responsive to health checks, and serving
nothing.

So the worker reports **the age of its oldest in-flight request**, and the
supervisor watches that number. A worker whose oldest request keeps ageing while
its completion count stays flat is stuck, however healthy its heartbeat looks.

This also gives the 2 AM answer for free: *"worker 3 has a request that has been
running for 340 seconds"* is diagnosable. *"Worker 3 is unhealthy"* is not.

## 6. Memory — growth, not level, and it is harder than it sounds

ADR-007 §4.7 says recycle on "observed memory growth" without saying how, and
the honest position is that this is genuinely difficult.

**A high level is not a fault.** A worker holding many warm contexts and a full
L1 is doing its job. Recycling it destroys exactly the value it accumulated.

**Growth without corresponding load is the signal** — and separating a leak from
a legitimately growing cache needs a baseline that a fresh deployment does not
have.

**Start crude and say so:**

- A hard ceiling, per worker, that triggers a drain and recycle. Blunt, works,
  prevents the OOM kill in [failure-scenarios.md](failure-scenarios.md) §7.
- A growth-rate check against a rolling window, which will produce false
  positives early and improve with data.
- **Every recycle is logged with the reason and the numbers.** A recycle that
  nobody can explain is worse than a leak, because it looks like instability.

Recorded as Q-64. Sophisticated leak detection is a later refinement and
pretending otherwise would be the kind of hand-wave this project keeps catching.

## 7. Quiesce is cross-worker, and that is why it needs a supervisor

ADR-007 §4.8 requires quiescing a data source so a DBA can run DDL. That is not
a worker operation — **every** worker holds a pool to that source, so it must
happen everywhere at once, and it must be reversible.

The supervisor owns the sequence: tell all workers to stop accepting work for
that source, wait for in-flight requests to finish or time out, confirm all
pools are closed, report quiesced, and resume on command.

This is the clearest case for the supervisor existing at all. Nothing else has
the vantage point.

## 8. If the supervisor dies

**Workers keep serving.** Losing management must not lose service (§1).

That requires:

- Workers do not exit when their parent does.
- Workers are **discoverable** by a restarted supervisor — a local state file or
  a known control socket, written at startup.
- **Adoption is idempotent.** A restarted supervisor finds running workers,
  re-attaches, and does not spawn duplicates.
- Anything mid-flight when the supervisor died — a drain, a quiesce — has a
  recorded state so the new supervisor can finish or abandon it deliberately.

The alternative, workers dying with their parent, means a supervisor bug is a
site outage. That is the wrong trade for a component whose entire job is
reliability.

Recorded as Q-65 — the adoption protocol needs designing, and it is the part
most likely to be got subtly wrong.

## 9. What this does not decide — where routing lives

The supervisor serves no requests, so **something else must decide which worker
receives one.** ADR-007 §4.4's affinity routing needs that decision to be
warmth-aware, which rules out the simplest options.

Three candidates, none chosen:

| Option | How | Cost |
|---|---|---|
| **Front router process** | A separate process accepts and forwards | On the data path, must be fast and reliable, and it is a new single point of failure per node |
| **`SO_REUSEPORT` plus a shared warm map** | All workers accept; each reads a shared map of who is warm for what | No central component. But the kernel chooses the receiver, so affinity must be achieved by forwarding or by accepting the miss |
| **Accept and bind locally** | Whoever receives it binds the context | Simplest. **Discards affinity entirely** and reduces ADR-007 §4.4 to plain balancing |

**This is deliberately left open**, because ADR-007 already marks affinity
routing as an unproven hypothesis requiring a prototype, and the routing
mechanism should be chosen by that prototype rather than assumed here.

What this document does establish is that **the supervisor is not the router**.
Putting them together would make the component that must not crash into the
component that handles every request.

Recorded as Q-63.

## 10. Open questions

| # | Question |
|---|---|
| Q-63 | Where does the routing decision live, given the supervisor is not on the data path? Front router, `SO_REUSEPORT` with a shared warm map, or abandon affinity. Should be settled by `experiments/affinity-routing`. |
| Q-64 | How is a memory leak distinguished from a legitimately growing cache, without a baseline? |
| Q-65 | What is the worker adoption protocol after a supervisor restart, and how is it made idempotent? |

## 11. What this closes

[failure-scenarios.md](failure-scenarios.md) N5 — the severe gap. ADR-007 §4.13
recorded that recycling, draining, quiescing and observed escalation were
written as if the mechanism existed. They now have one, and the parts that are
still hard (memory growth, adoption, routing) are named rather than assumed.

It also answers the loose end from N5: **does the router detect a dead worker
before or after routing to it?** Under §8 the supervisor marks a worker
unavailable on crash detection, and whatever routes reads that state — so before,
provided the routing component observes supervisor state. That is a constraint on
Q-63's answer.


---

## Certificate expiry monitoring

**Added 2026-08-13 by [ADR-014](adr/ADR-014-tls-and-certificates.md) §2c.**

A certificate expiry is a **total data-plane outage with a known date**. That
makes it the most predictable outage this system can suffer, and being surprised
by one is not a failure of luck.

The supervisor monitors expiry for every certificate it holds — the serving
certificate, client certificates for data sources, and trust anchors — and
surfaces it with lead time: **warning at 30 days, escalating at 7, critical at
1.** It appears in the admin API health surface and in whatever §46 exports.

This fits the supervisor's governing principle rather than straining it. The
supervisor exists so that a management-plane concern does not become a
data-plane failure; an unnoticed expiry is precisely a management-plane concern
becoming a total data-plane failure, on a timer, in public.
