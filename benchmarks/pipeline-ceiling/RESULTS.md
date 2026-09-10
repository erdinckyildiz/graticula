# The pipeline ceiling has a control now, and the control it had was not one

Measured 2026-09-10, against the development server on 8443 and a second server started
for this run. It answers the question
[D-249](../../docs/architecture-debt.md) left open — *nothing this repository has
measured explains it* — and the answer is in two parts, one of which corrects the
premise the debt was written on.

## 1. What was missing

[admission-control](../admission-control/RESULTS.md) §6 excluded four candidates by
measurement: the generator, per-request authentication, TLS and the request log. Every
one of them was excluded **by removing a piece of Graticula**. None of them removed
Graticula.

So a ceiling at 3,400 req/s on a 16-core machine had two families of explanation left
standing, and no measurement separated them:

- something in this server's request pipeline, or
- something about this machine, this loopback, this generator or this runtime.

The second family was never tested. This run tests it, by driving the same generator at
a **different server** on the same machine over the same protocol.

## 2. The three targets

One k6 script (`bare.js`), one machine, five concurrencies, levels interleaved so that
anything drifting on the machine drifts through all three curves rather than landing on
one of them.

| target | what it is |
|---|---|
| `info` | Graticula `/rest/info?f=json` — the path D-249 measured and called a control |
| `live` | Graticula `/healthz/live` — the **one** path whose middleware skips `ResolveAsync` |
| `bare` | `control/Program.cs` — a different server, no middleware at all, answering the same 182 bytes over TLS on 8555 |

`info` against `live` isolates one middleware inside one process. `live` against `bare` is
everything else the pipeline costs. Neither number means anything without the other.

Connection reuse is reported per run and is the evidence that no handshake is being paid
per request: `tls` and `connect` medians are **0.000 ms** in all fifteen runs, which is
the check the 2026-08-23 measurement failed and
[D-145](../../docs/architecture-debt.md) recorded.

## 3. What it measured

Requests per second, 20 s measured after 6 s warm:

| callers | `info` | `live` | `bare` |
|---:|---:|---:|---:|
| 1 | 1,022.4 | 4,030.1 | 5,309.0 |
| 4 | 3,366.7 | 13,007.5 | 18,064.5 |
| 16 | **6,594.4** | 17,875.1 | 50,203.7 |
| 32 | 5,879.5 | 15,117.9 | 57,438.7 |
| 128 | 5,358.9 | **18,594.8** | 59,797.8 |

Median latency, same runs:

| callers | `info` | `live` | `bare` |
|---:|---:|---:|---:|
| 1 | 0.92 ms | 0.00 ms | 0.00 ms |
| 16 | 2.14 ms | 0.54 ms | 0.00 ms |
| 32 | 4.45 ms | 1.58 ms | 0.51 ms |
| 128 | 19.19 ms | 5.89 ms | 1.58 ms |

## 4. The first finding: the machine is excluded

**A different server on this machine reaches 59,798 req/s and is still climbing where
Graticula has stopped at 5,359.** Same loopback, same TLS, same generator, same 182-byte
body, same `ServerGarbageCollection`. So the ceiling is not the machine, not the loopback,
not k6, not TLS and not the runtime's defaults.

That was the family of explanation nobody had tested, and it is now closed. **D-249's
headline survives it**: the ceiling is this server's.

## 5. The second finding: the control was never a control

D-249 introduces `/rest/info` as the path that *reads nothing, authenticates nobody and
touches no database*. **All three are false**, and the repository already knew it.

`Program.cs`'s authentication middleware returns early for exactly one path —
`/healthz/live` — and every other request, anonymous or not, goes through
`Authentication.ResolveAsync`. That method calls
`PostgresIdentityStore.GrantsOfAsync` unconditionally, and `GrantsOfAsync` is a real
statement against the platform store: a `left join` from `principal` to `principal_role`
with two correlated subqueries over `sharing_group_member`. An anonymous caller pays it
in full and gets no rows.

**The same repository says so in the same file.** `ResolveAsync`'s own remark, written for
[D-131](../../docs/architecture-debt.md), reads: *"Measured with the platform store
stopped: `/rest/info`, which reads nothing, answered 200 in 4.03 s — all of it here.
Every route pays it, because every route resolves a principal first."* That sentence and
D-249's *touches no database* describe the same path and cannot both be true. This is the
propagation shape [D-130](../../docs/architecture-debt.md) records, and it cost a
measurement rather than a document: the control was chosen on the false half.

**What removing it is worth, measured:** `live` against `info` is 3.9× at one caller,
2.7× at 16, 2.6× at 32 and 3.5× at 128.

## 6. And the shapes differ, which is the part that names the cause

Throughput against concurrency is not merely lower on `info` — it is a different curve.

- **`info` peaks at 16 callers and then falls**: 6,594 → 5,880 → 5,359. Past the peak,
  every added caller is added latency and *less* work.
- **`live` does not fall**: 17,875 → 15,118 → 18,595, flat inside the run-to-run spread.
- **`bare` does not stop**: 50,204 → 57,439 → 59,798.

A CPU limit produces a plateau. **A curve that peaks and declines is a bounded resource
being contended for**, and the only resource on `info` that is not on `live` is a
platform-store connection and the statement it runs.

Little's law on `info` agrees: unloaded service time is 1/1,022 = 0.98 ms, so 5,359 req/s
is **5.2 requests actually being served** however many are outstanding — which is
D-249's *about six*, arrived at from a different run.

## 7. What this does not establish

**Which part of the store lookup dominates is not measured here.** `ResolveAsync` does
three things — `FindSessionAsync`, `GrantsOfAsync`, and `EnsureFreshAsync` on
`PostgresRoleGrants` — and this run separates none of them. It measures their sum against
zero.

**The `live`-to-`bare` gap is real and unexplained.** At 32 callers the pipeline without
`ResolveAsync` is still 3.8× slower than a server with no middleware. That is a second
finding of its own size, and nothing here narrows it.

**Nor is the repair decided.** *Do not read the store for an anonymous caller* is the
obvious one and it is not obviously right: ADR-015 §2a made the anonymous lookup
deliberate so that a public grant is a row rather than a branch, and skipping it would
put that branch back. Caching it is a different answer with a staleness question
attached. Both belong to a decision, not to a benchmark.

## 8. How to run it again

```sh
# the control server, on 8555
dotnet run -c Release --project benchmarks/pipeline-ceiling/control

# the matrix, with k6 on the path (one self-contained binary; K6 points at it)
sh benchmarks/pipeline-ceiling/matrix.sh
```

`matrix.sh` names the dev server's port and the control's; change them there rather than
in `bare.js`, which takes any URL. The 182-byte body in `control/Program.cs` was read out
of the dev server with `curl` rather than retyped, so response size is not a difference
between the two servers.
