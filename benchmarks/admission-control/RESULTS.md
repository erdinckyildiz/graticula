# Admission control under a sustained flood — ADR-046 condition 1, re-measured

> **Re-measured 2026-09-11, after D-249 — [2026-09-11/RESULTS.md](2026-09-11/RESULTS.md).** The
> pipeline ceiling §6 describes is repaired and `/rest/info` is flat at 0.5 ms; the query path
> is not, and has a ceiling of its own that is neither admission control nor authentication.

**Run 2026-09-09.** **Settles:** [Q-140](../../docs/open-questions.md),
[D-145](../../docs/architecture-debt.md), and the half of
[ADR-046](../../docs/adr/ADR-046-admission-control-bounds-the-queue-not-the-wait.md)
condition 1 that has been open since 2026-08-23. **Runner:** [`run.sh`](run.sh) ·
**Generator:** [`flood.js`](flood.js) under k6 1.3.0 · **Raw:** `measured-*.json` in this
directory, one per run.

---

## The question, in Q-140's own words

> Measured: the queue holds at its configured depth and a cold flood sheds 15%.
> Unmeasurable here: whether a *sustained* flood sheds, and whether some of the latency
> growth is queued upstream of admission control where nothing in this server can reach
> it — sampling the queue counter through a warm flood found it empty in 22 of 24
> samples, which says the depth is reached in bursts rather than held. **The cause is 480
> Python threads doing TLS handshakes beside the server they are testing.**

The owner chose the second of the three shapes on 2026-09-09: a better generator, one
that pays neither a GIL nor a handshake per request. This is that measurement.

## The answer, in three sentences

**A sustained flood does not shed, and the generator was not the reason.** 164,825
requests at concurrency 480 over three minutes were refused zero times, with 480.3
requests in flight by Little's law — so the old *the client cannot keep the queue at
depth* hypothesis is dead.

**The queue admission control watches cannot fill, because something upstream of it caps
how many requests reach the gate.** A path that takes no permit, no authentication and no
database — `/rest/info` — saturates at the same concurrency with 2.5 of 16 cores in use.

**The mechanism itself works, and works sustained, whenever the source is the narrower
gate:** with two permits instead of twenty-four, 72.6% of a two-minute flood was refused,
the queue sat at its bound in 96% of 1,181 samples, and the shed share was 73.4% in the
first fifteen seconds and 71.3% in the last.

---

## 1. The generator, and why it is believed

`wrk` was ruled out before it was tried: it is POSIX-only, this machine is Windows 11,
and a Cygwin build would put the thing under test and an emulation layer in the same
sentence. **k6 1.3.0** was chosen — one self-contained Go binary from the project's
GitHub release, unzipped into a scratch directory. **Nothing was installed system-wide**;
`winget` offers `GrafanaLabs.k6` and was not used. `bombardier`, `hey`, `oha`, `vegeta`,
`ab` and `siege` are not present on this machine and a Docker image was not needed.

Three properties are what the choice was for, and all three are measured rather than
assumed:

| Claim | Evidence |
|---|---|
| No handshake per request | TLS handshake **0.000 ms median, 0.0005–0.0035 ms mean** in every run, over **1,042,228** measured requests |
| No GIL: it really keeps N in flight | Little's law from throughput × mean latency: **23.7 of 24, 59.7 of 60, 119.8 of 120, 240.3 of 240, 481.4 of 480** |
| The client is not the ceiling | Two independent k6 processes at 240 callers each reached **1,630 + 1,626 = 3,256 req/s**, against **3,293 req/s** for one process at 480 — the same total, and the same 145 ms median. k6's own CPU was **1.2–1.6 of 16 cores** |

Zero transport errors across every run. The script counts a socket failure separately
from a refusal on purpose: a generator that reports its own broken sockets as load
shedding is the failure this re-measurement exists to avoid.

**What is not fixed** is the third cost Q-140 names: the generator still runs on the
machine under test. What the row above changes is that this is now a bounded cost rather
than an unknown one — a client using 1.2 of 16 cores while the server uses 2.5 of them is
not the thing either of them is waiting for.

## 2. Environment

| | |
|---|---|
| Server | `Graticula.Host` from `C:/temp/gisbuild/d232`, schema `gisloop`, port 8465, started for this measurement only |
| Logging | **`Warning`, to `nul`** — see §7; the fixture's own Info-level console log wrote 98 MB during one three-minute flood |
| Layer | `hosted/ci_many` — 600 rows, one data source shared by every hosted layer |
| Request | `FeatureServer/0/query`, `where=1=1`, `outFields=*`, geometry on, `resultRecordCount=200` — **82,266 bytes**, 5–7 ms unloaded |
| Bound | `PerSourceConcurrency` **24**, `QueueWaitersPerPermit` **4**, so a queue depth of **96**; worker budget 64 |
| Database | PostgreSQL/PostGIS in Docker on the same machine, port 55432 |
| Machine | Windows 11, 16 cores, and **it is the machine under test** |

Every run warms at the same concurrency for 10–15 s and measures nothing during that
window. The queue counter and the server's own GC, CPU and allocation counters are read
from `/admin/health` ten times a second throughout the measured window.

## 3. The warm ramp, at the shipped bound

| callers | in flight | req/s | admitted median | p95 | refused | queue: median / max / non-empty | `/rest/info` median | server cores |
|---:|---:|---:|---:|---:|---:|---|---:|---:|
| 24 | 23.7 | 940.8 | **24.2 ms** | 38.6 | 0 | 0 / 1 / 0.2% | 4.2 ms | 3.81 of 16 |
| 60 | 59.7 | 944.4 | **61.3 ms** | 86.0 | 0 | 0 / 0 / 0.0% | 12.9 ms | 3.87 of 16 |
| 120 | 119.8 | 917.9 | **127.9 ms** | 162.3 | 0 | 0 / 4 / 0.2% | 30.7 ms | 3.85 of 16 |
| 240 | 240.3 | 922.6 | **251.1 ms** | 325.1 | **0 of 55,358** | 0 / 54 / 0.5% | 60.9 ms | 3.79 of 16 |
| 480 | 481.4 | 975.1 | **485.5 ms** | 557.5 | 17 (0.03%) | 0 / 38 / 0.2% | 117.2 ms | 3.95 of 16 |

60 s measured per level after 10 s of warm-up. GC pause 1.7–3.1% throughout.

**Throughput is flat from concurrency 24 to 480 and latency grows in exact proportion** —
twenty times the callers for twenty times the wait. That is the same finding the 2026-08-23
run reported, and it is not the generator: 481 of 480 callers had a request outstanding.

**The queue is not what is growing.** At concurrency 240 the counter was above zero in 3
of 601 samples and never came within 42 of its bound of 96. Nothing is refused because
nothing is waiting for a permit.

## 4. Sustained: 480 callers for three minutes

| from | ok | refused | shed | admitted median | p95 | queue max |
|---:|---:|---:|---:|---:|---:|---:|
| 0 s | 14,467 | 0 | 0.0% | 476 ms | 571 | 18 |
| 15 s | 13,840 | 0 | 0.0% | 512 ms | 615 | 0 |
| 30 s | 9,644 | 0 | 0.0% | 638 ms | 1,316 | 0 |
| 45 s | 12,062 | 0 | 0.0% | 562 ms | 851 | 10 |
| 60 s | 13,230 | 0 | 0.0% | 505 ms | 920 | 0 |
| 75 s | 15,007 | 0 | 0.0% | 479 ms | 514 | 0 |
| 90 s | 15,378 | 0 | 0.0% | 468 ms | 502 | 32 |
| 105 s | 14,808 | 0 | 0.0% | 483 ms | 538 | 0 |
| 120 s | 13,070 | 0 | 0.0% | 497 ms | 971 | 0 |
| 135 s | 14,172 | 0 | 0.0% | 490 ms | 705 | 0 |
| 150 s | 14,384 | 0 | 0.0% | 495 ms | 567 | 0 |
| 165 s | 14,192 | 0 | 0.0% | 504 ms | 578 | 0 |

**164,825 requests, none refused.** 480.3 in flight, 915.7 req/s, 71.8 MB/s out, 3.68 of
16 cores, GC pause 1.8%. The queue counter was above zero in **4 of 1,797 samples** and
reached 32 of 96 at its highest.

**A cold server behaves the same way here.** Flooded at 240 callers one second after
start-up — first request 22.0 ms against 5–7 ms warm, so the cold path is genuinely
slower — 24,776 requests were refused **zero** times and the queue reached 8 of 96. The
15% shed the 2026-08-23 run recorded on a cold server does not reproduce on this fixture;
that run drove `hosted/tr_il` on the development server rather than this layer, so this is
**not** a refutation of it, and §8 says what would be needed to make the two comparable.

## 5. The same mechanism, when the source is the narrower gate

Everything above is repeated with `PerSourceConcurrency=2`, so the bound is 2 permits and
a queue depth of 8. Nothing else changed.

| callers | refused | admitted median | admitted req/s | queue median | at depth | non-empty |
|---:|---:|---:|---:|---:|---:|---:|
| 24 | **52.1%** | 32.7 ms | 461.5 | 8 | 50.7% | 99.8% |
| 60 | **73.7%** | 78.5 ms | 179.6 | 7.5 | 50.0% | 93.7% |
| 120 | **72.6%** | 149.4 ms | 187.8 | 8 | 57.7% | 97.3% |
| 240 | **72.6%** | 275.0 ms | 185.3 | 8 | 59.8% | 96.0% |
| 480 | **73.8%** | 472.5 ms | 189.2 | 7 | 49.7% | 93.2% |

And sustained, at 240 callers for two minutes, in fifteen-second windows:

| from | 0 s | 15 s | 30 s | 45 s | 60 s | 75 s | 90 s | 105 s |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| shed | 73.4% | 73.0% | 72.3% | 72.0% | 74.1% | 71.5% | 72.3% | 71.3% |
| queue median | 8 | 8 | 8 | 8 | 8 | 8 | 8 | 7 |

**This is the decision working, and working for two minutes rather than for a burst.** The
bound is held, the refusal share is flat to within two points across the whole run, and
81,116 requests produced 58,880 refusals and no truncated document.

**Two things this table also says, and one of them is bad news.**

The queue counter reads **up to 10 against a bound of 8**. The count is incremented before
the depth is compared (`ConnectionBudget.EnterAsync`), so a caller who is about to be
refused is briefly counted as a waiter. The bound is enforced; the *reported* number can
sit two above it, and an operator reading `/admin/health` during a flood should know that.

**The admitted median still grows in proportion to concurrency — 32.7 → 78.5 → 149.4 →
275.0 → 472.5 ms — while the bound is firing.** Admitted throughput is flat at ~185 req/s
from 60 callers upward, so this is not the source getting faster or slower. It is the same
growth as §3, and §6 says where it is.

## 6. Where the latency actually is

~~`/rest/info` takes no permit, authenticates nobody, and touches no database.~~ **Corrected 2026-09-10 — [benchmarks/pipeline-ceiling](../pipeline-ceiling/RESULTS.md): `/rest/info` authenticates and reads the platform store.** The authentication middleware returns early for one path only, `/healthz/live`; every other request runs `ResolveAsync`, which calls `GrantsOfAsync` unconditionally — a `left join` over `principal` with two correlated subqueries, paid in full by an anonymous caller for no rows. Measured against the one path that skips it, that step is worth **2.6x to 3.9x**. What the sentence was reaching for is still true and is now narrower: this path takes no **data-source** permit, so the latency is upstream of admission control — and the thing upstream has a name. **The control this section relied on was therefore measuring the thing it was chosen to exclude**, which is why §6 could name four things the ceiling was not and nothing it was. Flooded on
its own, over plain HTTP, with the request log off — every one of those three tested
separately:

| callers | req/s | median | server cores |
|---:|---:|---:|---:|
| 1 | 548.4 | 1.7 ms | 0.36 of 16 |
| 2 | 971.9 | 2.1 ms | 0.61 of 16 |
| 4 | 1,768.6 | 2.1 ms | 1.17 of 16 |
| 8 | 2,640.2 | 2.8 ms | 1.69 of 16 |
| 16 | 3,354.5 | 4.3 ms | 2.09 of 16 |
| 32 | 3,411.7 | 8.5 ms | 2.44 of 16 |
| 480 | 3,422.3 | 138.0 ms | 2.25 of 16 |

**The request pipeline scales 6.1× from 1 caller to 16 and then stops, at 2.1 of 16
cores.** Past that point every extra caller is extra latency and no extra work: 3,412
req/s at 32 callers and 3,422 at 480.

**Its effective concurrency is about six.** 3,422 req/s at an unloaded service time of
1.8 ms (1 / 548.4 at concurrency 1) is Little's law giving six requests actually being
served, however many are outstanding. **The per-source permit count is 24.**

**And that the permit set is never full is measured rather than derived.** With 24
permits and 480 callers the waiter counter was zero in **99.8% of samples**, and a
`SemaphoreSlim` with every permit taken produces waiters the moment a 481st caller
arrives. So fewer than 24 requests were inside the gate at any sampled instant while 480
were outstanding — the other 456 were held upstream of it. That is not *the depth is
reached in bursts*; it is *the depth is unreachable at these settings*.

The arithmetic on the query path agrees, and it agrees as a ceiling rather than as a
point. At 480 callers, 24 permits and 975 admitted req/s, a request can hold a permit
for **at most 24.6 ms of its 485.5 ms median** — at most, because that figure assumes
every permit busy all the time and the waiter counter says they are not. So **at most
5.1% of the latency is inside the part of the server admission control governs**, and at
least 94.9% is upstream of it.

**Four candidate causes are excluded by measurement, not by argument:**

| Candidate | Test | Result |
|---|---|---|
| The generator | Two k6 processes at 240 each vs one at 480 | 3,256 vs 3,293 req/s — **same ceiling** |
| Per-request authentication | `/rest/info` is anonymous | **Same ceiling and the same shape** |
| TLS | `RequireHttps=false`, same flood over HTTP | 3,523 vs 3,407 at 24 callers — **3.4%** |
| The request log (ADR-045) | `Graticula:RequestLog=false` | 3,473 vs 3,355 at 16 callers — **inside the noise** |

And it is not the obvious resources either: **2.1–2.6 of 16 cores** at the ceiling, GC
pause 1.2–2.3%, and shrinking the response from 82 kB to 4 kB moved the query path from
975 to 1,332 req/s — 1.37×, not the 20× that a bandwidth ceiling would give.

**What it is has not been identified**, and this document does not guess. What can be said
is that the server spends 1.7 ms of wall clock on a request that costs it 0.66 ms of CPU
even at concurrency 1, and that the surplus does not scale away. That is
[D-249](../../docs/architecture-debt.md).

## 7. Two things about the measurement itself

**The first flood was run against the 8459 fixture and it was the wrong server to use.**
That server logs at Information to a redirected file: one three-minute run wrote **98 MB**
into it, on a disk with **0 bytes free**. Its `/rest/info` control read a **23.5-second
p95** at concurrency 240 — a number that does not appear anywhere in this document,
because it is the console logger and the full disk and not the server. This is the sixth
time a harness in this repository has measured itself; D-30 records the fifth, which was
the same cause.

**Every number above is from the 8465 server, which logs at Warning to `nul`.** A control
at 8 callers on both servers agrees within 3% (817 vs 793 req/s), so the logging does not
distort the *low*-concurrency path — it distorts the loaded one, which is the only one
this measurement is about.

**The disk was full and is a standing condition of this machine, not something this run
caused.** It is recorded because a timing measurement on a machine with no free disk is
worth less than one taken on a machine with free disk, and the reader should be able to
discount it.

## 8. What this does not settle

- **One layer, one machine, one shape of request.** The layer is 600 rows and the request
  asks for 200 of them; a render, a tile or a 50,000-feature page holds a permit far
  longer and would reach the bound at a lower concurrency. §5 emulates that by narrowing
  the gate rather than by lengthening the work, and those are not the same experiment.
- **The 2026-08-23 cold result is not reproduced or refuted**, because it was measured
  against a different server and a different layer. Making the two comparable needs
  `hosted/tr_il` on the development server, whose credentials are not recorded here.
- **The generator still shares the machine.** Q-140's first shape — a second host — is
  still unbought. What has changed is that its remaining value is narrower: it would
  sharpen the absolute throughput figures, and it would no longer change any conclusion
  about *where* the queue is, because both the client and the server have been shown to
  be far from saturated while the ceiling holds.
- **Why the pipeline stops scaling between 8 and 16 callers is unknown.** Four causes are
  excluded above.
  Naming the fifth needs a profiler on the server rather than a load generator in front of
  it.
