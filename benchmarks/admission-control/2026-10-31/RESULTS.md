# Admission control after D-261's first cut — ADR-046 condition 1, re-run

**Run 2026-10-31, 19:15 to 19:31.** **Owed by:** [ADR-046](../../../docs/adr/ADR-046-admission-control-bounds-the-queue-not-the-wait.md)
condition 1. The brief of 2026-10-31 asked for the §1 table again. The question was whether the 2026-09-11 verdict still
holds after [D-261](../../../docs/architecture-debt.md) took a request's catalogue reads from five statements to three.
The verdict was: first clause met, second clause BREACHED on a cause outside the gate.
**Generator:** [`../flood.js`](../flood.js) under k6, unchanged. **Raw:** `measured-*.json` in this directory, one per run.

## The answer

**Both halves of the 2026-09-11 verdict still hold.** The path is faster: about 1,170–1,220 req/s against 848–872.
The shape is the same as before.

- **First clause: met.** At `PerSourceConcurrency=2` the narrow source refused **64–82%**, and the queue sat at 7–8 of 8.
- **Second clause: breached.** The admitted median still grows with concurrency at both bounds:
  - shipped bound: **18.5 → 425.6 ms**;
  - narrow bound: **58.6 → 481.3 ms**, while four requests in five are refused.
- **The shipped default does not fire to any useful degree.** It refused 81 of 70,632 requests at 240 callers and 526 of
  62,938 at 480. The queue reached 96 of 96 in 0.5% of samples.

## Environment

| | |
|---|---|
| Server | `Graticula.Host` from branch `grapy-d` (`d11dbe55` plus this work, before it was committed). Out-of-tree Debug build, schema `gisserver` in database `gisgrapyd`, port 18571. Started for this run only. |
| Logging | `Warning` |
| Layer and request | `hosted/ci_many`, `FeatureServer/0/query`, `where=1=1`, `outFields=*`, geometry on, `resultRecordCount=200` |
| Bound | `PerSourceConcurrency` 24 with queue depth 96 (shipped); then 2 with depth 8 |
| Database | Portable PostgreSQL 16 / PostGIS on the same machine (`D:\temp\ngw-bench`), **not Docker**, unlike 2026-09-11 |
| Machine | Windows 11, 16 cores. It is also the machine under test. |

The runner started the server, ran the five levels at 60 s each after a 10 s warm-up, restarted the server with
`Graticula__PerSourceConcurrency=2`, and ran the five levels again. The runner script lives outside the repository
because it holds the fixture's password.

## The ramp at the shipped bound

| callers | req/s | admitted median | p95 | refused | queue max / non-empty | `/rest/info` median | server cores |
|---:|---:|---:|---:|---:|---|---:|---:|
| 24 | 1,167.3 | **18.5** | 32.0 | 0 of 70,040 | 1 / 0.2% | 4.2 | 4.49 |
| 60 | 1,170.2 | **48.9** | 79.2 | 0 of 70,209 | 25 / 18.0% | 8.3 | 4.42 |
| 120 | 1,223.5 | **95.3** | 131.8 | 0 of 73,413 | 47 / 12.3% | 6.4 | 4.64 |
| 240 | 1,177.2 | **198.7** | 259.9 | 81 of 70,632 | 87 / 15.0% | 7.2 | 4.53 |
| 480 | 1,049.0 | **425.6** | 664.8 | 526 of 62,938 | **96** / 19.3% | 6.0 | 4.12 |

## The narrower source: `PerSourceConcurrency=2`

| callers | refused | admitted median | admitted req/s | queue median | at depth | non-empty |
|---:|---:|---:|---:|---:|---:|---:|
| 24 | **64.3%** | 58.6 ms | 211.4 | 7 | 42.8% | 98.5% |
| 60 | **80.6%** | 94.6 ms | 186.3 | 8 | 63.9% | 99.3% |
| 120 | **81.6%** | 159.2 ms | 175.3 | 8 | 67.5% | 99.7% |
| 240 | **80.7%** | 352.7 ms | 141.8 | 8 | 58.8% | 95.2% |
| 480 | **81.4%** | 481.3 ms | 180.2 | 7 | 49.3% | 95.6% |

## What changed since 2026-09-11, and what did not

- **Throughput rose by about 38%**, from 848–872 req/s to 1,049–1,224 req/s, at the same request. Two things changed at
  once, so this run cannot split the gain between them:
  - D-261's cut of catalogue reads per request;
  - the database moved from Docker to a local PostgreSQL.
- **The median still grows in proportion to concurrency.** At 1,170 req/s, 480 callers in flight means about 410 ms by
  Little's law, and 425.6 ms was measured. The queue is at the gate's bound in under 1% of samples. So the wait is
  still outside the part of the server that ADR-046 governs, which is the 2026-09-09 and 2026-09-11 conclusion again.
- **The control path was not flat this time.** `/rest/info` took 4.2–12.5 ms median, against 0.5 ms on 2026-09-11.
  This run did not measure why. Two candidates:
  - this server's request pipeline has grown since v1.0.29;
  - the generator, the server and the database share 16 cores here, with the database outside Docker.

  This number is recorded, not explained.
- **The narrow source's admitted throughput fell**, to 141–211 req/s from ~280. The run did not look into this either.

## What this does not settle

- One layer, one request shape, one machine, as on every earlier run.
- The control path's rise above. Whether it is the server or the rig needs a run of `TARGET=/rest/info` alone, and
  that was not run here.
