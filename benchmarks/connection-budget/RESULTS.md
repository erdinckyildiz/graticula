# Q-04 — the connection budget, counted on a running server

**Run 2026-08-19.** `budget.py` is the harness; `measured.json` holds every sample.
PostgreSQL 17 in `gis-experiment-postgis`, `max_connections = 200`, one Graticula
worker process, thirteen published layers over two data sources.

[Q-04](../../docs/open-questions.md) is one of the five questions carried out of
Phase 0 as blocking: **what is the concrete DB connection budget at 1,000 services,
per provider?** [ADR-007](../../docs/adr/ADR-007-service-runtime.md) §4.8 gives the
formula and §10 condition 3 says it must be produced *with real numbers, per
provider, before any deployment guidance is published*. The formula has existed since
2026-08-12. The numbers have not.

**This counts; it does not time.** A backend is open or it is not, so the census is
immune to the machine being busy — which matters, because this repository has
produced five wrong latency harnesses and the fifth was the measurement setup itself.

## What was measured

Peak backends in `pg_stat_activity`, sampled every 0.4 s while N clients query in a
loop for eight seconds.

| Load | clients | peak backends | peak *active* | requests answered |
|---|---|---|---|---|
| 1 layer, 1 data source | 1 | 4 | 1 | 393 |
| 1 layer, 1 data source | 8 | 15 | 2 | 2,060 |
| 1 layer, 1 data source | 24 | 37 | 7 | 2,587 |
| 1 layer, 1 data source | 48 | **64** | 9 | 2,925 |
| **6 layers**, 1 data source | 8 | 64 | 3 | 1,947 |
| **6 layers**, 1 data source | 24 | 66 | 9 | 2,337 |
| **6 layers**, 1 data source | 48 | **69** | 12 | 3,095 |
| **2 data sources** | 8 | 70 | 2 | 2,199 |
| **2 data sources** | 24 | 73 | 4 | 2,736 |
| **2 data sources** | 48 | **79** | 4 | 3,263 |

**A caveat that belongs at the top rather than the bottom:** the pool does not shrink
between rows, so every row after the first block inherits the previous high-water
mark. The clean series is the first four rows; the later rows show *no new growth*
proportional to layers or sources, which is the claim being tested, but their
absolute numbers are cumulative. A harness that reset the pool between rows would
need to restart the server, and that would measure something else.

## What it says

**1. Pooling is per data source, as ADR-007 §4.8 requires — measured, not read.**
Forty-eight clients over **one** layer peak at 64 backends; the same forty-eight over
**six** layers add five more, not five times more. Six layers on one datastore is one
pool. This is the arithmetic the process-per-service model died on, and it holds in
the code as built (`LayerConnections`, keyed on the connection string).

**2. A request draws on *two* pools, and the formula counts one.** Peak backends run
about **1.3× the client count** — 48 clients, 64 backends — which no single pool
explains. Every query does a per-request catalogue read against the **platform
store** before it touches the layer's data source (D-17 put it there deliberately;
[D-30](../../docs/architecture-debt.md) priced it at 1.8 ms and 70% of a small
query). So the honest formula is:

```text
nodes × workers × (data sources + 1) × pool size
```

The `+ 1` is the platform store. §4.8's version omits it, and it is the one pool
*every* request touches regardless of which service was asked for.

**3. The ceiling is 100 per pool, and nothing in this repository chose it.** No
connection string sets `Maximum Pool Size`, so every pool takes Npgsql's default of
**100**. That is a code fact, read from the absence of the setting — **not measured**,
because reaching the plateau means driving 100 concurrent queries at one pool, and the
peak demand actually observed was 64 backends at 48 clients. So:

| Deployment | Pools | Ceiling |
|---|---|---|
| The baseline: one node, one worker, the datastore only | 2 | **200** |
| 1,000 services over 5 registered databases + the datastore | 7 | **700** |
| Four workers, same 5 databases (§4.8's own example) | 7 per worker | **2,800** |

Against a default PostgreSQL's `max_connections` of **100**. So on default settings
**one worker with two busy data sources can exhaust the database on its own**, and
§4.8's *"at fifty data sources it is 1,000, which is not [survivable]"* is optimistic
by 100/5 = twentyfold: the pool size in that sentence is 5 and the pool size in the
build is 100.

**And nothing enforces a budget.** §4.8 requires *a global connection cap per worker,
enforced across all pools*, so that many data sources degrade by queueing rather than
by exhausting the database. It is not built — `LayerConnections` says so in its own
remarks — so today the ceiling is whatever PostgreSQL refuses first, and the failure
mode at the ceiling is `FATAL: sorry, too many clients already` on an arbitrary
request rather than a queue.

**4. It does give the connections back, and *further* than the code claims.**
`LayerConnections` records shrink-to-zero as not implemented. Measured, from 79
backends with the load stopped:

| after | backends |
|---|---|
| 0–123 s | 79 |
| 154 s | 58 |
| **184 s** | **0** |
| 215 s | 2 |
| 245 s | 6 |
| 276 s onward | **16, and stable** |

It reaches **zero in about three minutes** — Npgsql prunes idle connections above
`MinPoolSize`, which is 0 because nothing sets it, on its own interval. So the
*budget* half of §4.8's shrink-toward-a-floor arrives free from the driver's
defaults, which is worth knowing before implementing it.

**5. The floor is not zero on an idle server. It is 8, and it is our own polling.**
After the fall, sixteen backends settle in and stay. Asked what they last ran:

```
with taken as ( select id from job where status = 'queued' and kind = $1 ...   8
select "objectid", "objectid", "name", st_asbinary(st_simplify…                7
```

Eight are `IJobStore.ClaimAsync`. `GeodatabaseInspector` and `GeodatabaseImporter`
each poll every two seconds when idle, round-robin over the platform-store pool, so
**every connection in it is touched again long before `ConnectionIdleLifetime`
expires and none of them can ever age out.** The minimum idle time observed across
the sixteen was two seconds, which is the poll. A pool that prunes correctly cannot
prune a pool somebody keeps knocking on.

That is [D-110](../../docs/architecture-debt.md): the cost is eight permanent
sessions per worker on a server doing nothing, and it grows with the number of job
kinds, because each background service polls independently.

## The answer to Q-04, for the one provider v1 has

> **Per worker process: `(data sources + 1) × 100` potential, `≈1.3 × concurrent
> requests` in practice, and `8` at rest.** At 1,000 services the service count does
> not enter it — five registered databases plus the datastore is 700 potential and
> perhaps 60 in use at 48 concurrent requests. The number that matters for a
> deployment guide is not the ceiling, it is that **the ceiling is unenforced**: the
> cap §4.8 requires does not exist, so *potential* is what the database will actually
> be asked for under a burst across many sources.

**Recommended settings, which are now derivable rather than guessed:** set
`Maximum Pool Size` explicitly per data source — 10–20 is ample for the measured
demand of 1.3× concurrency — and size the platform store's pool for the request rate
rather than for the layer count, because every request touches it.

**Per provider**, the shape is kept and only PostgreSQL is filled in: Oracle and SQL
Server price sessions differently and are not in v1. What transfers is the structure
of the formula — including the `+ 1`, which is ours and not the provider's.

## What this does not settle

- **Pool size 100 is read, not measured.** Driving one pool to its plateau needs 100+
  concurrent queries at one data source; the run stopped at 48 clients.
- **One worker, one node.** The `nodes ×` and `workers ×` factors are arithmetic here,
  not observations. Nothing in this repository runs two workers yet.
- **No cap means no measurement of what happens at the cap.** The interesting number
  — what a deployment does when it runs out — cannot be measured until the cap exists,
  and today the answer is PostgreSQL's refusal.
- **The per-source concurrency limit (N4), the circuit breaker (N3) and quiesce** are
  all §4.8 policy and all unbuilt. This measures the world without them.

---

# D-110 — where the idle floor is, and whose pool it is in

**Run 2026-08-24**, `idle-floor.py` in this directory. Twenty seconds at 24 clients
over the same six hosted layers, then five minutes of watching `pg_stat_activity`
grouped by `application_name`.

[D-110](../../docs/architecture-debt.md) measured the floor once and found **sixteen
backends, of which eight last ran the job claim**. The workers claim on the pool that
serves requests, round-robin, so every connection in it is touched again long before
`ConnectionIdleLifetime` can prune it — *a pool that prunes correctly cannot prune one
somebody keeps knocking on*. The backoff (2026-08-23) made the knocking fifteen times
rarer and left the floor exactly where it was.

## What the two runs say

| | Peak under load | Shared pool at t+300 s | Pollers' pool at t+300 s |
|---|---:|---:|---:|
| Pollers on the shared pool | 46 | **2, and they are the pollers'** | — |
| Pollers on their own pool | 46 | **0** | **2** |

The shared pool's drain, sampled every 15 s: **46 → 46 → 23 → 2** with the pollers in
it, and **46 → 46 → 0** without them. The last two connections in the first run were
idle 18 s when the run ended, which is the backoff's own interval — they are the
knocking, seen directly.

Reproduced twice with the change in place, once with it taken back out. Taking it out
is how the number was verified: the same script, the same load, the same five minutes,
and the shared pool stops at two.

## What it is bounded by

`MaxPoolSize` on the pollers' pool is `Enum.GetValues<JobKind>().Length` — one
connection per kind, so **the floor is a construction rather than an observation**. A
third job kind adds exactly one, which is the arithmetic ADR-007 §4.8 asks for and the
thing the row said was true by luck.

It is a ceiling, not a reservation: Npgsql opens on demand, and a server that has never
been asked to import anything holds none of them. Two is what a server that has run the
workers once holds.

## What this does not settle

- **The floor is not zero, it is bounded.** Two connections for two job kinds is the
  polling cost, and polling is still the mechanism. `LISTEN`/`NOTIFY` would replace it
  with a connection held open for ever per node, which is this row's own complaint
  arriving from the other side (§82).
- **One node.** The `nodes ×` factor is arithmetic here as it is everywhere else in this
  file.
- **The drain took longer in the run without the change** — 276 s against 214 s to fall
  below the plateau — which is consistent with the round-robin keeping connections warm,
  and is one observation rather than a measurement.
