# Admission control where the wait is — ADR-046 condition 1, 2026-11-01

**Run 2026-11-01, 01:13 to 02:20.** **Owed by:** [ADR-046](../../../docs/adr/ADR-046-admission-control-bounds-the-queue-not-the-wait.md)
condition 1. The 2026-10-31 re-run ([../2026-10-31](../2026-10-31/RESULTS.md)) left the second clause BREACHED: at 480
callers Little's law put about 410 ms of every request in a queue that the data-source gate did not see. This run
finds that queue, bounds it (`StoreGate`), and measures the same probe before and after on the same fixture, machine and
hour. **Generator:** [`../flood.js`](../flood.js) under k6, unchanged except that it now also samples the new gate's
counters from `/admin/health`. **Raw:** `measured-*.json` in this directory.

## Where the wait was

A 480-caller flood of the old build with `dotnet-counters` on the server: the platform store's pool (24 connections,
`Maximum Pool Size=24`) had **22 of 24 connections in use at the median and 24 at the peak**, its statements 2.6 ms at
the median; the data source's pool **7 of 24** at the median. So the data-source permits were mostly free and the store's
pool was full: a request under `/rest/services` reads the store (its token's session and user type, then its service)
before it asks for a data-source permit, and at about 1,300 requests a second and five statements each that pool is the
ceiling. Npgsql queues for a pool connection without a bound and nothing refuses there. That is also why narrowing the
source to two permits refused 84 % and still let the admitted median grow: a refused request had already queued at the
store to be refused (its refusal took **252–332 ms** at 480 callers).

## What changed

`StoreGate`: a request under `/rest/services` takes one of 24 permits (the store pool's size) before authentication, or
waits, or is **refused on arrival when 96 are already waiting** (4 per permit, `QueueWaitersPerPermit`), with the same 503
and `Retry-After` as a data-source refusal. It gives the permit back when it asks for a data-source permit
(`ConnectionBudget.EnterAsync`), so a request waiting for a slow source holds nothing here, or when it ends. The count
`/admin/health` reports under `admissionControl.store` is taken by compare-and-swap and never passes the depth.

## The ramp at the shipped bound (closed loop, 60 s per level after 10 s warm)

| callers | before: admitted median | p95 | refused | after: admitted median | p95 | refused | store queue (after) |
|---:|---:|---:|---:|---:|---:|---:|---|
| 24 | 19.2 | 36.7 | 0 | **18.5** | 27.6 | 0 | 0 |
| 60 | 44.7 | 65.6 | 0 | **51.1** | 83.5 | 0 | max 36 |
| 120 | 88.1 | 111.9 | 0 | **93.7** | 141.4 | 0 | max 95 |
| 240 | 187.1 | 260.1 | 0.1 % | **163.2** | 259.0 | **93.2 %** | 96 of 96 in 74 % of samples |
| 480 | 383.8 | 539.5 | 0.4 % | **185.1** | 279.4 | **94.3 %** | 96 of 96 in 75 % |
| 960 | — | — | — | **213.4** | 353.0 | **90.3 %** | 96 of 96 in 49 % |

Before: throughput 1,100–1,331 req/s, all admitted, the median doubling with the callers. After (second build, with the
compare-and-swap counter; 60 and 120 from the first build of the same change): admitted 1,207 req/s at 24 callers and
816–897 at 240 to 960, the rest refused in **16–52 ms** at the median.

## The narrow source, `PerSourceConcurrency=2` (first build of the change)

| callers | before: refused | admitted median | after: refused | admitted median |
|---:|---:|---:|---:|---:|
| 24 | 68.8 % | 29.8 | 68.2 % | 34.6 |
| 60 | 83.9 % | 66.9 | 87.6 % | 81.3 |
| 120 | 84.1 % | 105.7 | 90.2 % | 127.1 |
| 240 | 84.3 % | 188.2 | 98.5 % | 173.4 |
| 480 | 83.6 % | 369.7 | 99.2 % | 249.2 |

## Open loop: what an arrival process sees (`MODE=rate`)

| offered req/s | before: admitted median | refused | after: admitted median | refused |
|---:|---:|---:|---:|---:|
| 500 | 6.6 | 0 | 6.8 | 0 |
| 1,000 | 8.2 | 0 | 8.3 | 3.1 % |
| 2,000 | **4,809** | 13.7 % | **135.5** | 44.4 % |
| 4,000 | 1,089 | 2.6 % | 250.6 | 56.1 % |

At 2,000 offered the old build queued nearly five seconds of every admitted request; the new one refuses what it cannot
serve and answers the rest in 136 ms. At 1,000 and 4,000 both builds also lose connections to the operating system's
listen backlog (k6's new connections refused at TCP; 7,003 and 28,058 after, 0 and 65,984 before) — client
connection setup, not admission, and not counted as either answer.

## The verdict

- **First clause: met**, at the shipped bound and not only at a narrowed one — 93.2 % refused at 240 callers, where the
  old build refused 0.1 %.
- **Second clause: much nearer, not met.** The admitted median no longer doubles with the callers: 163, 185 and 213 ms
  at 240, 480 and 960, against 187 and 384 before at 240 and 480. What still grows is bounded: an admitted request can
  wait in two queues — 96 for the store, then 96 for its source — so at most about 24 + 96 + 24 + 96 = 240 admitted
  requests are in the server, which at the measured 800–900 admitted a second is a ceiling of roughly 270–300 ms; the
  admitted median times the admitted rate rose from 134 to 151 to 191 towards it. Making it flat means one queue for the
  whole request, and the simple way — keeping the store permit until a source permit is granted — gives back the
  blast-radius property (a request queued behind a slow database would hold a permit every other service needs). That
  choice is [D-374](../../../docs/architecture-debt.md).

## `/rest/info`, the control path, at 4–12 ms

It is not the server's pipeline: in the open loop it answered in **0.0–0.5 ms** at 500 and 1,000 requests a second on
both builds, which is the 2026-09-11 figure. It rises with the closed loop's load — 3.4–7.3 ms at 24 to 480 callers
before, and 18–28 ms when the new build is refusing 10,000–14,000 requests a second — on a machine where k6, the server
and PostgreSQL share 16 cores. The 2026-10-31 run's 4–12 ms is that contention, with the database moved out of Docker
onto the same cores; the run did not separate the client's share from the server's.

## Environment

| | |
|---|---|
| Server | `Graticula.Host` from branch `grapy-b`, Debug, out of tree; before: `3c71ca37` (its host code; `9af77c8b` touched only grapy); after: the same plus `StoreGate`. Schema `gisserver` in database `gisgrapyb`, port 18576, `Logging__LogLevel__Default=Warning`. |
| Layer and request | `hosted/ci_many`, `FeatureServer/0/query`, `where=1=1`, `outFields=*`, geometry, `resultRecordCount=200`, with a token |
| Database | Portable PostgreSQL 16 / PostGIS on the same machine (`D:\temp\ngw-bench`), shared with other fixtures |
| Machine | Windows 11, 16 cores, also the generator's. Another agent's Python job used about one core during part of the runs. |

## What this does not settle

One layer, one request shape, one machine, as on every earlier run; and a closed loop whose refused callers return at
once, so its refused share overstates what a client honouring `Retry-After` would see (the open loop is the other view).
