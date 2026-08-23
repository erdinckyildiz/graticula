# Is the request log on the request path?

**ADR-045 condition 1.** The objection to persisting one row per request is that it puts a
write on the database that is also serving the request. The design answers it with a bounded
queue and a background flusher; this is the measurement that says whether the answer works.

Run 2026-08-22 against the development server, `hosted/tile_gray/ImageServer?f=json`, 400
requests per row after a warm-up, same machine, same process, one variable: the
`Graticula:RequestLog` setting.

| Concurrency | Log on — median | Log off — median | Log on — p95 | Log off — p95 |
|---|---|---|---|---|
| serial | 17.89 ms | 18.25 ms | 22.03 ms | 22.50 ms |
| 16 at once | 22.03 ms | 21.54 ms | 30.00 ms | 30.04 ms |
| 64 at once | 61.99 ms | 64.55 ms | 115.30 ms | 112.68 ms |

**The log is not measurable on the request path, and the shape of the result is what says
so.** In two of the three pairs the run *with* logging is the faster one. That is not the log
making requests quicker; it is the difference being smaller than the run-to-run noise, which
is the only honest reading when a difference changes sign.

**Nothing was dropped.** After 1,200 logged requests at up to 64 concurrent, the writer
reported `dropped: 0, waiting: 0` — so the queue never came close to its 4,096 bound at this
load, and the batching flusher kept up.

## Re-run 2026-08-23, after four things were added to the request path

Admission control ([ADR-046](../../docs/adr/ADR-046-admission-control-bounds-the-queue-not-the-wait.md)),
the circuit breaker ([D-131](../../docs/architecture-debt.md)), the response-outcome marker
([D-132](../../docs/architecture-debt.md)) and the access line moving into a `finally` all sit
on every request now. Same harness, same endpoint, same counts.

| Concurrency | Log on — median | Log off — median | Log on — p95 | Log off — p95 |
|---|---|---|---|---|
| serial | 16.40 ms | 16.03 ms | 21.94 ms | 21.38 ms |
| 16 at once | 14.56 ms | 13.75 ms | 23.74 ms | 21.97 ms |
| 64 at once | 38.45 ms | 39.89 ms | 66.57 ms | 66.04 ms |

**The answer this run was for is unchanged: none of it is measurable on the request path.**
The log-on and log-off columns differ by under a millisecond at serial and at 16, and at 64
the difference changes sign again — which is the same reading the first run gave and the only
honest one when a difference is smaller than the noise.

**Every figure is also lower than 2026-08-22's, and this measurement cannot say why.** 62 ms
became 38 at 64 concurrent, and p95 115 became 67. The candidates are the queue bound and a
machine in a different state a day apart, and **nothing here isolates them** — the two runs
share an endpoint and a harness and not a controlled machine. It is recorded because leaving
it out would make the table read as though the numbers had not moved.

## What this does not show

**It does not show the behaviour under sustained load with a slow store**, which is the case
the drop counter exists for. The queue is 4,096 entries and the flusher writes in batches of
256; a store slow enough to fall behind would fill it and start dropping, and the Logs screen
would say so. Reproducing that needs a deliberately degraded store, which is the failure
gate's instrument rather than this one's.

**The concurrency scaling here is the server's, not the log's.** Median goes 18 → 22 → 62 ms
from serial to 64-at-once in *both* columns, so it is the request path itself — the same
figure the performance gate measures.

## How to run it again

```
GRATICULA_REQUEST_LOG=false bash dev-server.sh restart   # or true
python logbench.py
```

`logbench.py` lives in the session scratchpad rather than the repository: it is nine lines of
`urllib` and a thread pool, and promoting it would make it a thing to maintain. The table
above is the artefact; the recipe is in this paragraph.
