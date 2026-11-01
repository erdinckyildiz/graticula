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

## Clean run, 2026-10-31/11-01 — one factor at a time, then the fix

Run for the owner's *"Önce temiz ölç, sonra düzelt"*: measure cleanly first, then fix what the measurement shows.
What the first run lacked, it has: **a Release build**, logging at **Warning**, and **its own PostgreSQL 16 cluster**
(portable binaries, `D:/temp/cluster-d`, port 55575, `max_connections` 300, nothing else on it), stopped afterwards.
Before every run the rig sampled the whole machine for 10 s and **waited until it was under 20 % busy** (idle above
80 %), re-checking every 30 s; another session's ArcGIS Pro Python held it above that for several minutes at a time and
the runs waited. "Machine busy before" in the record is that sample; no process this run did not start was stopped.
Same data as before: ten organisations (550 + 9 × 50 services) in one database, one organisation (the same 1,000) in
another, each organisation's services clones of its own hosted point layer of 200 features; the same generator
(`Program.cs`), 30 s after 3 s of warm-up, 4 and 64 callers, half the load on the default organisation. Every
variant twice, interleaved (A B C D E F, then again). `measured-2026-11-01.json` holds every run.

**The variants**, each changing one thing against **B** (ten organisations as built):

- **A** one organisation (the one-organisation database).
- **C** the floor off: `alter table … disable row level security` on every table with a policy — same pools, same shares.
- **D** each organisation's pool the full 24 rather than `max(2, 24/10)` = 2 — same floor, same shares.
- **E** the per-organisation admission shares (condition 6) off — same floor, same pools.
- **F** one shared pool and no tenant on the connection (the floor's policy short-circuits) — the shape the fix
  approaches, without the tenant.

C, D, E and F were switches in a measuring build only (environment variables, never committed); A and B are the build
as it was.

### Busy organisation, p95 ms, and throughput — two runs each

| variant | callers | req/s | search | services | query | others' query | peak conns | failures |
|---|---|---|---|---|---|---|---|---|
| A one organisation | 4 | 104 · 112 | 75 · 68 | 61 · 55 | 11 · 10 | — | 18–19 | 0 |
| **B ten, as built** | 4 | 158 · 154 | 92 · 82 | 71 · 67 | **31 · 30** | 11 · 10 | 38–39 | 0 |
| C ten, floor off | 4 | 131 · 132 | 78 · 76 | 62 · 57 | 24 · 24 | 10 · 9 | 39–40 | 0 |
| **D ten, full pools** | 4 | 186 · 164 | 55 · 61 | 44 · 50 | **9 · 11** | 9 · 12 | 41–43 | 0 |
| E ten, shares off | 4 | 139 · 149 | 93 · 87 | 80 · 70 | 36 · 32 | 10 · 9 | 38–39 | 0 |
| F ten, one pool, no tenant | 4 | 105 · 110 | 74 · 71 | 63 · 60 | 12 · 10 | 12 · 11 | 18–19 | 0 |
| A one organisation | 64 | 175 · 202 | 704 · 566 | 490 · 424 | 308 · 239 | — | 57 | 0 |
| **B ten, as built** | 64 | 154 · 170 | **1,418 · 1,227** | 1,044 · 922 | **550 · 455** | 36 · 25 | 57–58 | 0 |
| C ten, floor off | 64 | 153 · 154 | 1,545 · 1,518 | 859 · 831 | 629 · 605 | 55 · 50 | 58 | 0 |
| **D ten, full pools** | 64 | **356 · 378** | 425 · 414 | 303 · 288 | **192 · 185** | 158 · 133 | **136–137** | 0 |
| E ten, shares off | 64 | 153 · 164 | 1,399 · 1,274 | 1,043 · 1,006 | 521 · 455 | 33 · 30 | 54–60 | 0 |
| F ten, one pool, no tenant | 64 | 187 · 197 | 608 · 587 | 445 · 421 | 259 · 245 | 256 · 245 | 47–50 | 0 |

No breaker line, no 503 and every organisation still signed in after every run; the cluster's 300 were never near.

### What it says

1. **The pools per organisation are the cost, and nothing else is.** Only the pool size moves the busy organisation:
   at 4 callers its query p95 goes from 30–31 ms to 9–11 with full pools, at 64 callers from 455–550 to 185–192 and
   throughput doubles. At 64 callers B used under half the machine (39–47 % busy) while its busy organisation queued
   — the signature of a queue, not of work.
2. **The floor costs nothing measurable at HTTP.** Off (C) it was no faster than on (B) at 4 callers by more than the
   run-to-run spread, and slower at 64 — with the floor off the default organisation's search reads every
   organisation's rows. §9's trigger (the floor at more than 20 % on the p95) **does not fire on the floor**; the 1.33–
   2.61× of the first run was the pools.
3. **The admission shares cost nothing measurable** (E within B's spread at both levels).
4. **Full pools are not the fix**: they bought the speed with 136 connections at 64 callers against 57, which is
   the budget condition 14 exists to protect. F shows that one pool gives the one-organisation numbers back at 47–50
   connections — but F carries no tenant, so it is not a floor.

### The fix, measured: one pool, the tenant set ahead of each statement

`TenantDataSources` now gives each store a `TenantSource` — the one platform pool and the request's tenant — whose
commands carry `SET graticula.tenant = '…'` in the same batch ahead of the statement, and whose connections are set
once when opened; Npgsql's reset on return clears it. **G** is ten organisations on that build, **H** one organisation
on it, **A2** one organisation on the measured build again beside them (two runs each, interleaved A2 G H):

| variant | callers | req/s | search | services | query | others' query | peak conns | failures |
|---|---|---|---|---|---|---|---|---|
| A2 one organisation, build before | 4 | 124 · 103 | 67 · 74 | 55 · 62 | 11 · 12 | — | 20–21 | 0 |
| **G ten organisations, fixed** | 4 | 157 · 144 | 73 · 89 | 64 · 74 | **11 · 13** | 11 · 15 | 19–20 | 0 |
| H one organisation, fixed | 4 | 105 · 105 | 70 · 75 | 58 · 61 | 11 · 10 | — | 19 | 0 |
| A2 one organisation, build before | 64 | 189 · 185 | 624 · 617 | 435 · 433 | 252 · 257 | — | 51–57 | 0 |
| **G ten organisations, fixed** | 64 | **367 · 359** | 325 · 337 | 242 · 245 | **140 · 142** | 140 · 142 | **46–51** | 0 |
| H one organisation, fixed | 64 | 185 · 190 | 614 · 596 | 443 · 436 | 260 · 251 | — | 57 | 0 |

- **Ten organisations against one, at 4 callers**, means of the two runs each against the four one-organisation runs
  (A2 and H, same machine, same hour): query 12 against 11 ms (**1.09×**), search 81 against 72 (1.13×), the services
  directory 69 against 59 (1.17×) — **each under §9's 20 %**, the directory nearest it. At 64 callers ten
  organisations are faster than one (query 141 against 255) because each organisation's search and directory read a
  smaller catalogue, and they hold fewer connections (46–51 against 51–57).
- **One organisation is unchanged by the fix** (H against A2: the same within the spread), as it should be — a
  deployment of one names no tenant and runs exactly the statements it ran.
