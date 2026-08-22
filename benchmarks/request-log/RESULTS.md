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
