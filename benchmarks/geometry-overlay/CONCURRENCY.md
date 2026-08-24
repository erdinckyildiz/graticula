# D-31 — overlay with the pool busy, rather than one request on an idle machine

**Run 2026-08-24** against the development server: two overlay workers, a ten-second
work deadline, a ten-second queue wait, a thirty-minute idle keep, 1 GB per worker.
The scripts are `concurrency.py`, `killed-worker.py` and `queue-wait.py` in this
directory; each takes `GRATICULA_TEST_URL`, `GRATICULA_TEST_USER` and
`GRATICULA_TEST_PASSWORD` from the environment, and the last two read the first for
its payload builder.

[D-31](../../docs/architecture-debt.md) says every figure in
[RESULTS.md](RESULTS.md) is a single request on an idle machine, and names three things
the arithmetic assumes and nobody measured: **that the ceiling is respected under real
concurrent load, that a killed worker's memory is returned promptly, and that process
launches under contention stay inside the start-up allowance.** All three are measured
below, and a fourth thing turned up that the row did not ask about.

## The load

`intersect` of two 300,000-vertex rings offset by a twentieth of their radius — maximal
edge interaction, the same shape the first benchmark used. One alone takes **2.06–2.12 s**
and answers 6.1 MB.

| Concurrent | Median | Worst | Answers |
|---:|---:|---:|---|
| 1 | 2,118 ms | 2,118 ms | 200 |
| 2 | 2,687 ms | 2,687 ms | 200 |
| 4 | 4,391 ms | 4,397 ms | 200 |
| 8 | 6,515 ms | 7,829 ms | 200 |
| 12 | 9,666 ms | 12,561 ms | 200 |
| 16 | 13,029 ms | 17,034 ms | 200 |

**Two workers running in parallel, and a queue that is not doing anything clever.** Eight
requests of ~2.1 s of work through two workers is ~8.4 s of work; the last answered at
7.83 s. Sixteen is ~16.8 s; the last answered at 17.03 s. There is no throughput surprise
in either direction.

**The server's own log agrees with the client**, so the queueing is inside the server
rather than on the wire: the twenty longest `intersect` rows in `request_log` for this run
top out at 17,020 ms against the client's 17,034 ms.

## The ceiling, under load

**Exactly two worker processes throughout, at every concurrency.** Working sets over the
whole run peaked at **754 MB and 618 MB**, both under the 1 GB ceiling, with eight
300,000-vertex overlays in flight. The arithmetic holds: two workers at 1 GB is 2 GB, and
neither worker got there.

## A killed worker

A worker holding **644 MB** was killed outright while the pool was warm.

| | |
|---|---|
| Memory returned | Within 1 second — the process is gone at the first sample |
| Replaced | **Lazily, not eagerly.** The pool ran on one worker until concurrency needed a second |
| Next request | 2,361 ms, against a warm single-request median of 2,118 ms — a cold start costs about **240 ms** |
| Next burst of four | 4,103 ms median, against 4,370 ms warm — inside the noise |

So the third doubt is answered: **a process launch under contention costs about a quarter
of a second**, which is inside any start-up allowance this pool has, and the burst after a
kill is not slower than the burst before it.

## The queue wait, which needed a different shape of work

The intersect load never reached the ten-second wait, and the reason is worth writing
down: **the wait bounds queueing for a worker, not the request.** A 300,000-vertex operand
is a 25 MB form body, so sixteen of them arrive staggered — a client can watch 17 seconds
pass while never waiting ten for a worker.

So the wait was measured with work that is long in the server and short on the wire:
`union` of **4,000 overlapping 60-vertex rings**, 7.5 MB sent, **5.4 s** alone.

| Concurrent | Median | Answers |
|---:|---:|---|
| 4 | 11,024 ms | 200 ×4 |
| 6 | 11,042 ms | **200 ×4, 503 ×2** |

The two refusals came at 10,786 ms and 11,042 ms carrying the pool's own sentence — *every
geometry worker is busy and this request waited 10 seconds for one* — so the bound is
enforced and it says what it is.

## What turned up that the row did not ask about

**A request body over Kestrel's 30 MB default was closed rather than refused.** A
600,000-vertex intersect encodes to about 37 MB and the connection ended after 23
milliseconds; the client saw `EOF occurred in violation of protocol`, not a status code it
could read. Recorded as [D-148](../../docs/architecture-debt.md) and **repaired the same
day**: the size is compared against the ceiling before the body is read, the answer is a
413 naming both bounds, and the service document reports `maximumRequestBytes` beside
`maximumVertices`. A client that sends `Expect: 100-continue` reads the refusal; one that
streams without asking still meets a reset, which is HTTP rather than this server.

**And 300,000 vertices is already close to that ceiling** at about 25 MB, which is why the
load above stops there: the largest overlay this server will accept over the wire is
smaller than the largest one its worker could compute.
