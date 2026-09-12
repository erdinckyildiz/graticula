# Admission control after D-249 — ADR-046 condition 1, re-measured again

**Run 2026-09-11, 23:57 to 00:22.** **Owed by:** [ADR-046](../../../docs/adr/ADR-046-admission-control-bounds-the-queue-not-the-wait.md)
condition 1, which said after [D-249](../../../docs/architecture-debt.md) was repaired that the
clause *is owed a re-measurement rather than standing BREACHED on a cause that is gone*.
**Generator:** [`../flood.js`](../flood.js) under k6 1.3.0, unchanged except for one switch
(`ANONYMOUS=1`, below). **Raw:** `measured-*.json` in this directory, one per run.
**Baseline:** [the 2026-09-09 run](../RESULTS.md), same layer, same request, same generator.

---

## The answer, in four sentences

**D-249's cause is gone.** `/rest/info` answers in **0.5 ms median at every level from 24
callers to 480**, where on 2026-09-09 it grew from 4.2 ms to 117 ms.

**The query path did not move.** It holds **848–872 req/s from 24 callers to 480** and its
admitted median still grows in proportion — **25.1 → 559.9 ms** — so the clause *the admitted
requests' median must stop growing with concurrency* fails exactly as it did.

**A quarter of that path is authentication, measured by removing it.** The same flood sent
anonymously to the same server reaches **1,237–1,268 req/s against 943** for the authenticated
control run in the same minute: an authenticated request reads its session and its grants
from the platform store on every call, and D-249 held only the anonymous answer.

**The rest is not authentication either.** Anonymous, throughput is still flat and the median
still grows — **17.5 → 186.8 → 379.6 ms** at 24, 240 and 480 callers — so the ceiling is on the
query path itself, and it is [D-261](../../../docs/architecture-debt.md).

---

## 1. Environment

| | |
|---|---|
| Server | `Graticula.Host` v1.0.29 (`7cce798`), out-of-tree Debug build, schema `gisloop`, port 8465, started for this measurement only |
| Logging | `Warning`, to `nul` — the rig's own ceiling, see the 2026-09-09 run §7 |
| Layer and request | `hosted/ci_many`, `FeatureServer/0/query`, `where=1=1`, `outFields=*`, geometry on, `resultRecordCount=200` — 82,266 bytes, 5–8 ms unloaded |
| Bound | `PerSourceConcurrency` 24, queue depth 96 (shipped); then 2, depth 8 |
| Database | PostgreSQL/PostGIS in Docker on the same machine; **Docker is given 6 cores** |
| Machine | Windows 11, 16 cores, and it is the machine under test |

The two runner scripts live outside the repository, as the 2026-09-09 one did not: they read
the fixture's key from `.env` and a password from the environment. What they do is the
2026-09-09 `run.sh` — start the server, warm 10–15 s, measure, stop — with the port found by
`:8465` rather than `127.0.0.1:8465`, because the server listens on `0.0.0.0` and the first
version of the stop would have left the first server running under the second phase.

## 2. The warm ramp, at the shipped bound

| callers | in flight | req/s | admitted median | p95 | refused | queue max / non-empty | `/rest/info` median | server cores |
|---:|---:|---:|---:|---:|---:|---|---:|---:|
| 24 | 23.8 | 872.2 | **25.1** | 46.4 | 0 of 52,330 | 0 / 0.0% | **0.5** | 3.77 |
| 60 | 59.8 | 825.4 | **69.2** | 104.4 | 0 of 49,523 | 1 / 0.2% | **0.5** | 3.60 |
| 120 | 119.9 | 856.2 | **133.9** | 185.8 | 0 of 51,372 | 0 / 0.0% | **0.5** | 3.73 |
| 240 | 240.2 | 848.2 | **277.5** | 346.4 | 1 of 50,893 | 66 / 0.5% | **0.5** | 3.72 |
| 480 | 481.8 | 847.9 | **559.9** | 675.7 | 112 of 50,874 | 29 / 1.3% | **0.5** | 3.80 |

2026-09-09, for comparison: 940.8 / 944.4 / 917.9 / 922.6 / 975.1 req/s, medians 24.2 / 61.3 /
127.9 / 251.1 / 485.5 ms, `/rest/info` 4.2 / 12.9 / 30.7 / 60.9 / 117.2 ms. **The query path is
about 8% slower than it was**, which is inside what one run differs from the next here — the
authenticated control in §5 made 943 req/s at 240 callers on another server start, against 848
in this table.

Zero transport errors in every run, TLS handshake 0.000 ms median, and Little's law puts
23.8–481.8 requests in flight against 24–480 offered: the generator is not the limit, as it
was not on 2026-09-09.

## 3. Sustained: 480 callers for three minutes

**154,080 requests, 72 refused**, all 72 in the first fifteen seconds; 856.0 req/s, admitted
median 554.6 ms, p95 652.3, queue above zero in 0.7% of samples and at most 32 of 96. The
2026-09-09 run refused none of 164,825. **The shipped default still does not fire on this path.**

## 4. The narrower source — `PerSourceConcurrency=2`

| callers | refused | admitted median | admitted req/s | queue median | at depth | non-empty |
|---:|---:|---:|---:|---:|---:|---:|
| 24 | **58.3%** | 34.4 ms | 427.6 | 8 | 56.7% | 100.0% |
| 60 | **75.2%** | 70.4 ms | 282.4 | 8 | 72.2% | 99.8% |
| 120 | **75.2%** | 121.3 ms | 288.7 | 8 | 72.2% | 99.8% |
| 240 | **75.5%** | 225.9 ms | 271.1 | 8 | 77.8% | 99.3% |
| 480 | **76.1%** | 394.0 ms | 286.1 | 8 | 65.9% | 97.1% |

**The first clause holds, as it did on 2026-09-09**: three requests in four refused and the queue
at its bound. Admitted throughput is higher than 2026-09-09's ~185 req/s (~280), which is what
D-249 bought the refusal path. **And the second clause fails here too** — the admitted median
grows 34.4 → 394.0 ms while the bound fires — so the growth is not in the part of the server
this decision governs, which is 2026-09-09's §6 conclusion again, on a faster pipeline.

## 5. The control: the same query, anonymously

`flood.js` gained one switch for this: `ANONYMOUS=1` sends the flooded query and the warm-up
without the token. Login and the `/admin/health` samples keep it, because the queue counter is
an administrator's to read. `hosted/ci_many` is public in `gisloop`; the runner checks that an
anonymous query returns a feature before flooding, so the control cannot quietly measure a
refusal path.

| run | req/s | admitted median | p95 | refused | queue max / non-empty | server cores |
|---|---:|---:|---:|---:|---|---:|
| anonymous, 24 | **1,267.5** | 17.5 | 31.6 | 0 of 76,049 | 0 / 0.0% | 4.76 |
| anonymous, 240 | **1,259.7** | 186.8 | 232.9 | 162 of 75,583 | 77 / 3.5% | 5.00 |
| anonymous, 480 | **1,236.9** | 379.6 | 459.6 | 253 of 74,213 | **96** / 8.9% | 4.96 |
| authenticated, 240, same server | **943.4** | 249.7 | 305.1 | 0 of 56,602 | 1 / 0.2% | 4.21 |

**Removing authentication is worth 34% at 240 callers**, measured against a control that
differs in nothing else. The code says what it removes: `Authentication.ResolveAsync` calls
`FindSessionAsync` and then `GrantsOfAsync` for every request that carries a token, and since
D-249 an anonymous caller's grants are held in memory while the store's announcements are
heard.

**It is not the ceiling.** Anonymous throughput is flat from 24 callers to 480 and the median
grows in proportion, exactly the shape of §2 at a higher level.

**With the pipeline faster, the shipped bound begins to fire** — the queue reached 96 of 96 and
refused 253 at 480 anonymous callers, against 112 and a maximum of 29 authenticated. That is
the mechanism ADR-046 built, doing what it says; it refuses too little too late to hold the
median, because most of the wait is still outside the gate.

## 6. The database, sampled

`docker stats` on the PostGIS container every ten seconds through every run. Samples between
runs (under 10%) are left out.

| phase | database container |
|---|---|
| shipped ramp, authenticated | 3.2–3.9 of 6 cores |
| sustained 480, authenticated | 3.4–3.8 of 6 |
| `PerSourceConcurrency=2`, authenticated | 3.3–4.1 of 6 |
| anonymous, 24–480 | 3.1–3.8 of 6 |
| authenticated control, 240 | 3.4–3.9 of 6 |

**The database does about the same work per second whichever the mix**, and that is the one
observation here that is not a table of the server's own counters. At ~3.5 cores it is 3.7 ms
of database CPU per authenticated request and 2.8 ms per anonymous one — **inferred** from
throughput and the container's CPU, not measured per statement — and the 0.9 ms between them is
the size the session and grants lookups would have to be. The narrow runs point the same way:
at most two data queries can be inside the gate there, and the container still used 3.3–4.1
cores, so most of what the database does during a flood is not the query the permit governs.

**What holds it near 3.5 cores rather than the 6 Docker is given is not measured.** The server
reaches the database through Docker Desktop's port forward on this machine, which spends CPU in
the same virtual machine the container runs in; that is a candidate, not a finding.

## 7. What this does not settle

- **Where the query path's ceiling is.** Two things are excluded by measurement — the request
  pipeline D-249 repaired (`/rest/info` is flat) and authentication (the anonymous control is
  flat too) — and one is suggested and not shown: the database side. Naming it needs a profile
  of one query under load, or the same flood against a database that is not in Docker on the
  machine under test.
- **Whether the session and grants lookups should be held the way the anonymous answer is.**
  That is a revocation question as much as a performance one — a held grant is one that a
  revocation has to reach — and ADR-015 §3a answered it for the anonymous caller only.
- **One layer, one request shape, one machine**, as on 2026-09-09.

## 8. Where the ceiling is: round trips, not the query — 2026-09-12

§7 said two things were excluded and one was suggested and not shown. This is the
measurement that shows it. Four floods, 240 callers, 30 s measured, one server, taken with
PostgreSQL's own counters read either side of each run (`pg_stat_database`) and divided by
the requests the generator counted.

| path | req/s | median | out | server cores | **database transactions per request** | rows returned per request |
|---|---:|---:|---:|---:|---:|---:|
| `/rest/info` | **34,973** | 4.9 ms | 6.1 MB/s | 4.82 | **0.00** | 0.1 |
| layer document, anonymous | 1,460 | 158.6 ms | 3.3 MB/s | 2.50 | **10.89** | 53.9 |
| `query`, 200 rows, anonymous | 1,358 | 172.3 ms | 106.5 MB/s | 5.28 | **6.40** | 235.8 |
| `query`, 200 rows, with a token | 843 | 266.6 ms | 66.1 MB/s | 3.99 | **10.67** | 266.3 |

**Every path under `/rest/services` pays six to eleven short round trips to the database;
the one path that pays none runs twenty-seven times faster on the same server at the same
concurrency.** That is the ceiling named. It is not the query — the layer document asks
for 54 rows and caps where an 82 kB query asking for 236 does — and it is not the response
size, which §5 had already made unlikely and this makes plain: 3.3 MB/s and 106.5 MB/s
reach the same rate.

**A token costs about four round trips.** 10.67 against 6.40 on the same query, which is
the session lookup and the grants read that `Authentication.ResolveAsync` does on every
authenticated request — D-249 held that answer for the anonymous caller and for nobody
else.

**Two hypotheses died here, and one of them was mine an hour earlier.**

- *A serialization point — a lock somewhere on the services path.* Refuted by Little's
  law: throughput times median latency puts **225 to 234 of 240 callers in flight** on
  every services path, so requests are waiting in parallel, not queueing single-file
  through one gate. The first version of this section said the opposite from a division
  done the wrong way round.
- *One saturated counter in the database.* Refuted by its own numbers: the transaction
  rate is **15,900 a second** on the document path and **8,700** on the query path, so
  no single per-second figure is the wall. What the two share is that the work is
  round-trip shaped and the database container sits at **3.1 to 4.1 of the 6 cores Docker
  is given** in every sampled run, against a server using 2.5 to 5.3 of 16.

**What this still does not name** is which of the six to eleven trips are avoidable. The
repair direction is fewer of them — holding the catalogue and identity reads the way
[ADR-015](../../../docs/adr/ADR-015-authentication.md) §3a holds the anonymous caller's
grants — and that is a revocation decision before it is a performance one, so it is
recorded in [D-261](../../../docs/architecture-debt.md) rather than taken here.

**Two things about the method.** The counters are database-wide and the platform store and
the data source share one database, so a path's trips are not split between them here. And
the counters span the warm-up while the request count does not, so each figure is about
12% high as an absolute — the comparisons between paths, which is what this section is
for, are unaffected.
