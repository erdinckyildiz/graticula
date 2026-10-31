# ADR-204 conditions 13 and 14 — the HTTP cost of ten organisations against one

**Run 2026-10-31** on the shared development PC (i7-11800H, 16 logical), the portable PostgreSQL 16 + PostGIS at
55499 (`max_connections` 100, shared with other fixtures), two servers of **the same Debug build** (schema 116, bound
role `graticula_gd`, logging at Warning). Disposable rig; `Program.cs` is the load generator and
`measured-2026-10-31.json` holds every run.

## What was compared

- **Ten organisations** (18571, database `gisgrapyd`): the default holds 550 services, nine others 50 each — 1,000 in
  all, three layers each, every organisation's services clones of its own hosted point layer of 200 features.
- **One organisation** (18573, `gisgrapyd_one`, a copy of the first with every row moved to the default organisation
  and the nine organisations removed): the same 1,000 services, the same layers, the same hosted tables.
- **The same requests**: each round picks an organisation's layer — the default's with weight 9, each other's with
  weight 1, so half the load is one busy organisation — and asks `/sharing/rest/search?q=hp&num=10`, the services
  directory of the `hosted` folder, and a FeatureServer `query` of 50 rows, signed in, at that organisation's host
  (ten organisations) or at the one host (one organisation).
- **Interleaved**: one, ten, one, ten … 30 s each after a 3 s warm-up, because the machine was not quiet — another
  session's test host was using about two-thirds of its CPU when this began, and the one-organisation throughput moved
  from 74 to 28 requests a second across three runs of the same thing.

## 4 callers — latency, ms, p95 over every request

| run | one: search | ten: search | one: services | ten: services | one: query | ten: query | ten ÷ one (query) |
|---|---|---|---|---|---|---|---|
| 1 | 169 | 280 | 104 | 175 | 29 | 76 | 2.61 |
| 2 | 394 | 425 | 277 | 246 | 88 | 125 | 1.41 |
| 3 | 333 | 414 | 276 | 271 | 78 | 103 | 1.33 |

Throughput (requests a second): one 74.1 · 41.8 · 28.5, ten 62.4 · 33.8 · 37.9. No failure; every organisation's token
still signed in after each run; no breaker line. The platform store's connections peaked at 18–19 with one organisation
and 33–36 with ten.

## 64 callers — saturation

| run | mode | req/s | busy organisation p95 search / services / query | failures | breaker lines | still signed in | peak conns |
|---|---|---|---|---|---|---|---|
| 1 | ten | 56.0 | 6,150 / 2,288 / 1,938 | 0 | 9 | 10/10 | 50 |
| 1 | one | 113.7 | 1,430 / 1,110 / 643 | 3 × 503 | 32 | 1/1 | 38 |
| 2 | ten | 256.5 | — | **7,037 × 503** | **346** | **0/10** | 33 |
| 2 | one | 121.6 | 1,048 / 723 / 441 | 0 | 0 | 1/1 | 42 |

The second ten-organisation run ran the shared cluster out of connections — `53300: remaining connection slots are
reserved for roles with the SUPERUSER attribute`, 172 times — and the platform store's breaker opened again and
again: every request after it was a 503 answered in under a millisecond, which is why its throughput is the highest
and why nobody was signed in afterwards. Both servers had said at startup that they may hold up to 120 connections
against the cluster's 97; the datastore image's `max_connections=300` is the configuration that note asks for, and
this cluster does not have it.

## What it says, and what it does not

1. **The FeatureServer query is slower with ten organisations in every 4-caller pair — 1.33× to 2.61× at p95 — past
   §9's 20 %.** Search and the directory are mixed (0.89× to 1.68×), inside the noise of this machine. Read as the
   condition asks, **§9's trigger fires on the query.**
2. **What it does not say is why.** Ten organisations differ from one in three ways at once: the row-level policies
   apply (one organisation's connections name no tenant, and the policy short-circuits), each organisation's statements
   go to a pool of `max(2, 24/10)` = 2 connections rather than the one pool of 24, and the per-organisation shares of
   the process's and the datastore's admission (conditions 6 and 14) apply. Condition 11's plans show the floor keeping
   every per-request index; the pools' share of two is what the prototype measured making the busy organisation queue
   behind itself. Separating them needs a build that varies one at a time, on a machine nobody else is using.
3. **At 64 callers the pools per organisation fail condition 14's test**: the busy organisation's query p95 was
   1.9 s against 0.44–0.64 s with one organisation, and its throughput half — queueing past §9's trigger, which the
   condition says is when the per-statement batch replaces them.
4. **Not measured:** a Release build; a quiet machine; a cluster with the image's 300 connections.
