# ADR-046 — Admission control bounds the queue, not the wait

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` |
| **Decided** | 2026-08-23 |
| **Supersedes** | — |
| **Superseded by** | — |

> Amends [ADR-007](ADR-007-service-runtime.md) §4.8, which specified admission control as a
> wait with a timeout. The bound stays; what decides a refusal changes.

---

## 1. Context

**[D-128](../architecture-debt.md) says the connection budget sheds load on the rendering
faces and not on the query faces. That is a symptom, and the cause is neither about faces
nor about which face is slower.**

The budget is a pair of semaphores — 24 permits per data source, 64 per worker — and a
request waits up to five seconds for one. If the wait expires the request is refused with a
503 and a `Retry-After`. Nothing in the code distinguishes one face from another:
`LayerConnections.SourceFor` wraps every data source identically, which is exactly why
nobody would have found this by reading.

**Re-measured 2026-08-23 rather than inherited, and the measurement corrects the row in both
directions.** Against the development server, `hosted/tr_il`, 24 permits per source:

| Load | Concurrency | Refused | Median | Throughput |
|---|---|---|---|---|
| `query`, 200 rows | 24 | none | 79 ms | 277 req/s |
| `query`, 200 rows | 60 | none | 187 ms | 275 req/s |
| `query`, 200 rows | 120 | none | 348 ms | 275 req/s |
| `query`, 200 rows | 240 | **none** | **611 ms** | 245 req/s |
| `export`, 4000×3000 | 40 | **none** | **4,521 ms** | 7.6 req/s |

**The query column is the defect stated properly: throughput is flat from 24 to 240
concurrent while median latency grows in proportion — 79 to 611 milliseconds, ten times the
concurrency for eight times the wait — and not one request is turned away.** That is a queue
growing without bound with admission control watching it happen. It is Little's law with
nothing on the other side of the equation: if arrivals exceed service capacity and nothing is
refused, the only free variable is the queue, and the queue is latency.

**And the row's other half does not reproduce.** It records `MapServer/export` refusing 35% of
40 concurrent renders. At 4,000 by 3,000 — 4.5 seconds a render, more than the four seconds
the row cites — **forty out of forty were admitted.** A smaller 1,200 by 900 render at the same
concurrency was also fully admitted, at 250 ms each.

**Which gives the mechanism exactly.** A refusal needs one caller to wait the *whole* five
seconds. With 24 permits and 40 arrivals, the queue is sixteen deep — less than one permit-set
— so a queued request waits about one service time, 4.5 seconds, and is admitted with half a
second to spare. **Refusal requires the queue to be deeper than the wait window divided by the
service time**, and for any workload fast enough to matter that is a queue thousands deep.
The control does not fire late; it very nearly never fires.

**So this is not *the query faces are exempt*.** It is *a bound on waiting cannot shed load
from a workload whose individual requests are faster than the bound*, which is every workload
worth shedding.

## 2. Alternatives considered

### Alternative A — Bound the hold: kill a request that has held a permit too long

**Argument for.** This is what D-128 names as the repair and it addresses the thing that is
actually scarce: a permit held for thirty seconds is thirty seconds no other caller can use.
It also catches the case a queue bound cannot — one pathological request, admitted alone,
holding a connection while a database does something quadratic. And it is the only option that
puts an upper bound on how long a *single* request can hurt everybody else.

**Argument against.** A request killed after admission has already begun writing. **A
truncated document is a worse failure than a refusal and this server cannot answer it
cleanly** — the row says so itself: `ErrorResponse` writes a status and a body, and there is
no status left to write once bytes are on the wire. A GeoJSON response cut mid-feature is
invalid JSON that a client will report as a parse error, pointing at the wrong thing. The
statement timeout already bounds the database's half of a long request
([ADR-007](ADR-007-service-runtime.md) §5), which is where a pathological query actually
spends its time.

### Alternative B — Raise the permit count until nothing queues

**Argument for.** The simplest possible change, no new concept, and it is the right answer when
the bound is genuinely too low for the hardware.

**Argument against.** It does not bound anything; it moves the number at which the same
unbounded queue forms. And the measurement says throughput is already flat at 24 permits —
275 req/s at concurrency 24 and at 240 — so the source is saturated at the current count.
More permits would buy more concurrent waiting, not more work done.

### Alternative C — Shed load by response time: refuse when observed latency exceeds a target

**Argument for.** The most direct expression of what an operator wants: *refuse rather than
serve anybody slower than two seconds.* Adaptive, needs no capacity estimate, and copes with a
database whose speed changes under load — which is the case here, since the collapse is the
database's and not this server's.

**Argument against.** A controller with feedback, a window, and a hysteresis rule, all of
which need tuning and none of which can be tested deterministically. §6 asks what concrete
problem a technology solves; a latency controller solves this one and brings a tuning surface
with it. **Worth revisiting if the queue bound proves too blunt**, and recorded here so that
the next person does not have to rediscover it.

### Alternative D — Bound the queue: refuse on arrival when too many are already waiting

**Argument for.** The bound is on the thing that grows. A caller arriving to find 24 in flight
and 96 waiting has an expected wait of four service times whether or not anybody times it, and
telling them so immediately is both truthful and cheap. **Nothing in flight is ever
interrupted, so no document is ever truncated** — the refusal happens before a byte is
written, which is the one place `ErrorResponse` can answer cleanly. And it composes with the
wait it replaces as the decisive test: the timeout stays as a backstop for the pathological
case A is about.

**Argument against.** It needs a capacity number nobody can derive from first principles —
*how many may wait per permit* is a judgement, and a wrong one refuses work the server could
have done. ~~It also cannot bound a single slow holder, which is the case A exists for.~~
**Corrected 2026-09-09: a queue bound cannot bound a holder, but something else already
does.** `RequestDeadline` shipped 2026-08-18 — five days before this decision was written —
and puts every request under a wall-clock deadline installed before authentication,
defaulting to 600 s, lowerable per service and never raisable. The permit is held in a
`using` around the enumeration (`BudgetedFeatureSource`), so cancelling that token releases
it. **What is true is the weaker sentence**: ten minutes is a bound and not a defence — the
slow-reader measurement held permits for 289 s, well inside it. See
[D-144](../architecture-debt.md).

## 3. Counterarguments to the preferred option

**A queue bound refuses work the server would have completed.** At concurrency 240 every one
of those 720 requests was answered, median 611 ms. Under this decision most of them would be
refused. **An operator watching 503s appear where none were before will read it as a
regression**, and they will be right that something got worse — the server was serving that
load. What they cannot see from the graph is that it was serving it by making everybody wait,
and that the next doubling of arrivals doubles the wait again with no floor.

The answer is that a 503 with `Retry-After` is a fact a client can act on and a slowly growing
latency is not, and that the number is settable: an operator who prefers the queue can raise
the depth. But *shedding load looks like breaking* is a real cost and it is why condition 3
asks for the refusal to be visible on the Logs screen rather than only in a status code.

**Little's law is an argument about averages and this is a bound on an instant.** Queue depth
at the moment of arrival is a noisy sample; a burst that would have drained in 200 ms can trip
a depth bound and be refused for nothing. A latency controller (C) would not make that
mistake.

Mitigated rather than solved: the depth is set in units of permits rather than requests, so
the bound scales with configured capacity, and it is generous enough that a burst shorter than
the depth passes through. A workload that trips it repeatedly is one where the queue is not a
burst.

**It does not fix the case D-128's own repair was aimed at.** One request holding a permit for
a minute still holds it. That is A, it is still true, and this decision does not pretend
otherwise — see §6.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| A query flood is never refused | 720 requests at concurrency 240, zero 503s | §1 table, measured 2026-08-23 |
| Latency grows in proportion to concurrency | 79 → 187 → 348 → 611 ms for 24 → 60 → 120 → 240 | same |
| Throughput is already saturated at 24 permits | 277 req/s at 24, 245 at 240 | same |
| A slow render is not refused either | 4,000×3000, 4.5 s each, 40 of 40 admitted | same |
| The row's 35%-of-40 refusal does not reproduce | 0 of 40 at a slower render than the row's | same, and this contradicts D-128 as written |
| Refusal needs a queue deeper than window ÷ service time | 24 permits, 40 arrivals, 4.5 s each: one service time of waiting, admitted | derived from the two above |

### After the change, and it is not all good news

| Load | Refused | Admitted median | Reading |
|---|---|---|---|
| 720 at concurrency 240, **cold** | **109 (15%)** | — | the bound fires |
| 1,440 at concurrency 480, **warm** | **none** | 1,179 ms | it does not |
| depth 1 / 2 / 4 / 8, 720 at 240, cold | 149 / 118 / 87 / 39 | 1,172 / 1,134 / 1,059 / 1,076 ms | refusals track the depth; **the admitted median does not** |
| warm ramp, depth 4, 24 → 480 | none at any level | 90 → 230 → 530 → 847 → 1,179 ms | latency still grows in proportion |

**The queue is bounded and it is observable: peak waiters on a source measured exactly 96,
the configured depth, and never exceeded it.** So the mechanism works. What the measurement
does *not* show is the thing §6 was first written to claim — a ceiling on end-to-end latency.
Warm, at concurrency 480, the median is still growing and nothing is refused, and sampling the
counter through the flood found the queue empty in 22 of 24 samples.

**The most likely reason, stated as a hypothesis because it is one:** the load generator is
the limit. 480 Python threads doing TLS handshakes on the same machine as the server cannot
keep 480 requests in flight, so the depth is reached in bursts and not sustained. **A second
possibility is that some of the growth is upstream of admission control entirely** — queued in
Kestrel, before any code that could shed it — in which case bounding this queue cannot bound
that latency and no setting here will. Distinguishing them needs a load generator that is not
this machine, which is condition 1 and is not discharged.

### Distinguished 2026-09-09, and it is the second

[Q-140](../open-questions.md) chose a better generator over a second host, and
[benchmarks/admission-control](../../benchmarks/admission-control/RESULTS.md) is the
re-measurement. **The hypothesis above is wrong.** k6 keeps the requests in flight that
the Python probe could not — Little's law gives **23.7 of 24, 59.7 of 60, 119.8 of 120,
240.3 of 240 and 481.4 of 480** — at a TLS handshake cost of **0.000 ms median** over
1,042,228 measured requests, and two independent client processes at 240 callers each
reach the same total as one at 480. The client was never what kept the queue empty.

| Load, warm, `PerSourceConcurrency` 24 | Refused | Admitted median | Queue non-empty | `/rest/info` |
|---|---|---|---|---|
| 24 callers | none | 24.2 ms | 0.2% of 601 samples | 4.2 ms |
| 60 | none | 61.3 ms | 0.0% | 12.9 ms |
| 120 | none | 127.9 ms | 0.2% | 30.7 ms |
| 240 | **none of 55,358** | 251.1 ms | 0.5% | 60.9 ms |
| 480 | 17 of 58,507 | 485.5 ms | 0.2% | 117.2 ms |
| **480 for three minutes** | **none of 164,825** | 494.3 ms | 0.2% of 1,797 | 123.6 ms |

**A sustained flood does not shed, and the reason is upstream of everything here.**
~~`/rest/info` takes no permit, authenticates nobody and touches no database~~, and its
latency grows in the same proportion across the same ramp. **Corrected 2026-09-10 — [benchmarks/pipeline-ceiling](../../benchmarks/pipeline-ceiling/RESULTS.md): `/rest/info` authenticates and reads the platform store.** The authentication middleware returns early for one path only, `/healthz/live`; every other request runs `ResolveAsync`, which calls `GrantsOfAsync` unconditionally — a `left join` over `principal` with two correlated subqueries, paid in full by an anonymous caller for no rows. Measured against the one path that skips it, that step is worth **2.6x to 3.9x**. What the sentence was reaching for is still true and is now narrower: this path takes no **data-source** permit, so the latency is upstream of admission control — and the thing upstream has a name. *(And for an anonymous caller it is gone: since 2026-09-11 the answer is held while the store's announcements are heard — [ADR-015](ADR-015-authentication.md) §3a, [D-249](../architecture-debt.md) — and `/rest/info` went from 6,394 to 46,775 req/s at 128 callers.)* Flooded on its own it
stops scaling **between 8 and 16 callers, at 2.1 of 16 cores** — 2,640 req/s at 8, 3,355
at 16, 3,412 at 32 and 3,422 at 480, an effective concurrency of about **six** against an
unloaded service time of 1.8 ms. **`PerSourceConcurrency` is 24.** That the permits are
therefore never all taken is measured rather than inferred: with 480 callers outstanding
the waiter counter was zero in **99.8% of samples**, and a full semaphore produces a
waiter the moment the next caller arrives. Fewer than 24 requests were ever inside the
gate, so the queue this decision bounds cannot fill. At 480 callers a request can hold a
permit for **at most 24.6 ms of its 485.5 ms median** — at most, because 24 permits over
975 admitted req/s assumes every permit busy all the time and the counter says they are
not — so **at most 5.1% of the latency is inside the part of the server this decision
governs**. That ceiling is [D-249](../architecture-debt.md).

**The mechanism is not what is wrong, and narrowing the gate shows it working.** With
`PerSourceConcurrency=2` — a bound of 2 permits and a queue depth of 8 — a two-minute
flood at concurrency 240 refused **58,880 of 81,116 (72.6%)**, the queue sat at its
bound in **96% of 1,181 samples**, and the shed share was 73.4% in the first fifteen
seconds and 71.3% in the last. **And the admitted median still grew with concurrency
while all of that was true** — 32.7, 78.5, 149.4, 275.0, 472.5 ms for 24 to 480
callers — which is condition 1's second clause failing with the bound firing, and is
why that clause is now marked breached rather than open.

**One reporting defect, found by the same run.** The waiter count is incremented before
the depth is compared, so `/admin/health` can read **10 against a bound of 8** for the
instant a caller is being refused. The bound is enforced; the number an operator reads
during a flood can sit above it.

## 5. Decision

**Admission control refuses on arrival when the number of callers already waiting for a
permit exceeds a bound, and keeps the five-second wait as a backstop rather than as the
decisive test.** The bound is expressed in permits — **four waiting per permit**, so 96 for a
source with 24 — because a bound in requests would have to be re-chosen every time capacity
is. A refusal is the 503 and `Retry-After` this server already answers, and it happens before
any part of a response is written, so no document is ever truncated. `ConnectionBudget` counts
its own waiters; a semaphore does not expose them.

## 6. Consequences

**Positive.** The queue is bounded and observable, which it was not: the count is on
`/admin/health` and it holds at the configured depth. A cold flood — the case where shedding
matters, because service is slowest exactly when a server has just restarted under load — does
refuse: 15% of 720 at concurrency 240. The refusal is the one this server already knows how to
write, at the one moment it can be written cleanly. And the depth scales with the permit count,
so raising capacity raises the queue with it.

**What is not claimed: a ceiling on latency.** The first draft of this section said the bound
gives one, and the measurement does not support it — warm, at concurrency 480, nothing is
refused and the median is still climbing. ~~Either the load generator cannot sustain the depth
or part of the queue is upstream of admission control; both are open in condition 1.~~
**Settled 2026-09-09: it is the second, and the first is refuted.** The queue is
upstream — see §4's re-measurement — and no bound in `ConnectionBudget` can flatten a
latency that is 94.9% outside it. **A bound
that fires on a cold burst and not on a warm flood is worth having and is not the whole
answer.**

**Negative.** Work the server would have completed is refused, and an operator will see 503s
where they saw slow success. Queue depth at an instant is noisier than latency over a window,
so a short burst can be refused for nothing. And **the case D-128 named — a single request
holding a permit far too long — is not addressed here at all**: alternative A remains the
answer to it and remains unbuilt, because a truncated response is a failure this server
cannot report. That is a debt this ADR creates deliberately rather than a gap it overlooked.

**Ports created.** None. `ConnectionBudget` is already the seam.

**State.** *Catalogue*: none. *Runtime*: the **per-source permits and waiter
counts**, per worker process and therefore **node-local** — which is a real limit of this
design and is stated rather than hidden: the budget bounds what one worker does to a database,
and two workers can still do twice that. Nothing here survives a restart, and nothing needs
to.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | Four waiters per permit is a useful default | **A judgement, not a measurement.** Condition 2 asks for it to be measured against the numbers in §1 rather than left as a guess |
| — | The database is the saturating resource, not this server | **Its evidence is retired, 2026-09-09.** *Flat throughput while CPU is not the bound* does not establish this: the same flatness appears on `/rest/info`, which never reaches a database — 3,412 req/s at 32 callers and 3,422 at 480, at 2.1 of 16 cores. The assumption may still be true and is no longer supported by the measurement it cited ([D-249](../architecture-debt.md)) |

## 8. Dependencies

**Depends on** ADR-007 (the runtime and §4.8's admission control, which this amends),
ADR-045 (the Logs screen, where condition 3's refusals become visible).

**Depended on by** nothing yet.

## 9. Conditions

1. **A saturated source refuses, and it is measured with the same probe that showed it did
   not.** The §1 table re-run: at concurrency 240 a meaningful share must be refused, and the
   admitted requests' median must stop growing with concurrency. A change that refuses
   without flattening latency has bounded the wrong thing.

   **PARTLY DISCHARGED 2026-08-23 — superseded below, where the second clause is marked
   BREACHED on a re-measurement rather than left open.** The
   first clause holds: 109 of 720 refused at concurrency 240 on a cold server, against zero
   before, and the queue counter holds at its configured depth. **The second clause does not.**
   Warm, at 480, nothing is refused and the median has grown from 90 ms to 1,179 ms.

   **This is not discharged by declaring the clause too strict.** Either the load generator —
   480 Python threads on the same machine as the server — cannot keep the queue at depth, or
   some of the growth is queued upstream of admission control where nothing here can reach it.
   The counter says the queue was empty in 22 of 24 samples during that flood, which favours
   the first, and *favours* is not *shows*. **It needs a load generator that is not this
   machine**, and until then this condition names what is unproven rather than pretending
   otherwise.

   ***RE-MEASURED 2026-09-09, and it settles both clauses — the first one further than
   2026-08-23 could reach, the second one against this decision.*** k6 1.3.0 replaced
   the Python probe by owner decision on [Q-140](../open-questions.md);
   [benchmarks/admission-control](../../benchmarks/admission-control/RESULTS.md) holds
   the runs and the raw JSON.

   **The generator hypothesis is dead.** Little's law across the ramp says the client
   held **23.7 of 24, 59.7 of 60, 119.8 of 120, 240.3 of 240 and 481.4 of 480**
   requests in flight; TLS handshaking cost **0.000 ms median** over 1,042,228 measured
   requests; and two independent client processes at 240 callers each reached the same
   total as one at 480. *480 Python threads cannot keep the queue at depth* was a
   reasonable guess and it was wrong.

   **The first clause holds, and it holds sustained rather than cold.** With
   `PerSourceConcurrency=2` — a source narrower than the pipeline that feeds it — a
   two-minute flood at concurrency 240 refused **58,880 of 81,116, 72.6%**, and the
   shed share was 73.4% in the first fifteen seconds and 71.3% in the last. The queue
   sat at its bound in **96% of 1,181 samples**. That is the mechanism working, held,
   for two minutes — the thing this clause asked for, which no amount of argument about
   the old measurement could have supplied.

   ***BREACHED in its second clause, and that is a fourth state rather than an open
   condition.*** *The admitted requests' median must stop growing with concurrency* has
   now been tested at both bounds and fails at both: **32.7, 78.5, 149.4, 275.0 and
   472.5 ms** for 24 to 480 callers **while 73% is being refused and the queue is at
   its bound**. The clause's own words are *a change that refuses without flattening
   latency has bounded the wrong thing*, and that sentence has now been run rather than
   awaited.

   *(**The ceiling this clause ran into is repaired, 2026-09-11 — [D-249](../architecture-debt.md).**
   The pipeline's stop at eight to sixteen callers was two things: the anonymous grants lookup,
   now held while the store's announcements are heard (ADR-015 §3a), and four ASP.NET Core log
   lines per request at the default level, now `Warning` by default. `/rest/info` at 128 callers
   went from 6,394 to 43,092 req/s with nothing configured
   ([benchmarks/anonymous-grants](../../benchmarks/anonymous-grants/RESULTS.md)). **So the clause
   is owed a re-measurement rather than standing BREACHED on a cause that is gone** — and it is
   not marked met here, because nobody has run the flood again.)*

   ***RE-MEASURED 2026-09-11, after D-249 — the cause above is gone and the clause is still
   breached, on a different and measured cause.***
   [benchmarks/admission-control/2026-09-11](../../benchmarks/admission-control/2026-09-11/RESULTS.md),
   the same ramp against the same layer with the same generator. **The control path is flat
   now**: `/rest/info` answers in **0.5 ms median at every level from 24 callers to 480**,
   where it grew from 4.2 to 117 ms on 2026-09-09. **The query path is not**: 848 to 872 req/s
   throughout, and an admitted median of **25.1, 69.2, 133.9, 277.5 and 559.9 ms** — in
   proportion to concurrency, as before. The shipped default still does not fire: 112 refused
   of 50,874 at 480 callers, and 72 of 154,080 over three sustained minutes, every one of them
   in the first fifteen seconds. **The first clause holds again**: with `PerSourceConcurrency=2`
   the source refused **58 to 76%** with the queue at its bound. **The second fails there too**:
   the admitted median grows from 34.4 to 394.0 ms while three requests in four are refused.

   **The control that removes authentication** — the same flood with the query sent
   anonymously, the same server, the same minute — reaches **1,237 to 1,268 req/s against 943**
   for the authenticated control: a quarter of the path's capacity is the session and grants
   lookups an authenticated request pays on every call, which D-249 held for the anonymous
   caller only. It flattens nothing — **17.5, 186.8 and 379.6 ms** at 24, 240 and 480 callers —
   though with the pipeline faster the bound does begin to fire: the queue reached **96 of 96**
   and refused 253 at 480 anonymous callers. That is the mechanism this decision built, and it
   refuses too little too late to hold a median whose growth is outside the gate. **The ceiling
   is now on the query path itself** — [D-261](../architecture-debt.md) — with the server at 3.6
   to 5.0 of 16 cores and the database container at 3.1 to 4.0 of the 6 Docker is given. So the
   second clause stands BREACHED on a cause that is measured rather than guessed, and §6's
   concession — *what is not claimed: a ceiling on latency* — is the part of this decision
   that has held every time it was run.

   **What this decision bounded is not wrong; what the clause asked for is unreachable
   from inside it.** ~~`/rest/info` takes no permit, authenticates nobody and touches no
   database~~ (see §4's correction of 2026-09-10), and it grows in the same proportion across the same ramp — 4.2, 12.9,
   30.7, 60.9, 117.2 ms — because the request pipeline stops scaling **between 8 and 16
   callers, at 2.1 of 16 cores**, an effective concurrency of about six against an
   unloaded service time of 1.8 ms. At the shipped bound a request holds a
   permit for **at most 24.6 ms of its 485.5 ms median** — at most, because 24 permits
   over 975 admitted req/s assumes every permit busy all the time and the counter says
   they are not — so **at most 5.1% of the latency is inside the part of the server this
   decision governs** and no queue bound anywhere in `ConnectionBudget` can flatten the
   rest. §6 conceded the substance of that on
   the day this was written — *what is not claimed: a ceiling on latency* — and the
   condition never absorbed it. The ceiling is [D-249](../architecture-debt.md).

   **And the shipped default cannot fire on this hardware at all**, which is the part
   an operator needs. `PerSourceConcurrency` is 24 and the pipeline serves about six at a
   time, so the permits are never all taken — measured rather than inferred, because the
   waiter counter would be non-zero the moment they were: **164,825 requests at concurrency 480 over three
   minutes, none refused**, with the counter above zero in 4 of 1,797 samples and a
   maximum of 32 against a bound of 96. The bound is not *reached in bursts*; it is
   unreachable until either the pipeline gets faster or the permit count comes down
   below it. **A cold server did not change that here** — flooded one second after
   start-up at concurrency 240, 24,776 requests, none refused — though that ran against
   a different fixture and layer from the 2026-08-23 cold run, so it neither reproduces
   nor refutes the 15%.

2. **The depth is chosen against the measurement rather than asserted.** Four per permit is a
   judgement; the condition is to run the probe at several depths and write the table down, so
   the number in the code is one somebody picked with the numbers in front of them.

   **DISCHARGED 2026-08-23, and the table is in §4.** Depths of 1, 2, 4 and 8 refused 149, 118,
   87 and 39 of 720 while the admitted median stayed between 1,059 and 1,172 ms — **so the depth
   buys refusals and does not buy latency**, which is not what choosing it was expected to
   trade. Four is kept: it sheds a sixth of a cold flood while refusing least, and the numbers
   give no reason to prefer a tighter one. **It is a setting now — `Graticula:QueueWaitersPerPermit`
   — because a number this flat across its range is a number a deployment should be able to
   move without a build.**

3. **A refusal is visible as a refusal, not only as a status code.** ADR-045's request log
   records status and duration, so a flood shows as a band of 503s in the Logs screen with the
   latency of the admitted requests beside it. Without that, shedding load and breaking look
   identical to the operator who has to decide which is happening.

   **DISCHARGED 2026-08-23.** The request log records every 503 with its duration and path, and
   `/admin/health` now reports `admissionControl` — the permits, the depth, the wait, and how
   many callers are waiting for a source and for the worker. **The counter is what turned this
   from an argument into a measurement**: it is how the queue was found holding at exactly 96,
   and how the warm flood was found not to reach it. Behind `admin:manageServer`, because how
   close a deployment is to its bound is a capacity fact about the server.

   **One defect in the counter, found 2026-09-09 by the re-measurement and small enough to
   state here rather than open a row for.** The count is incremented before the depth is
   compared (`ConnectionBudget.EnterAsync`), so a caller who is about to be refused is
   briefly counted as a waiter: `/admin/health` was observed reading **10 against a bound of
   8**. The bound is enforced and nothing else about the counter is affected; the number an
   operator reads during a flood can sit a little above the bound it is being compared to.

4. **No response is ever truncated by this.** A refusal happens before the response begins;
   asserted from outside by a test that floods a source and checks that every non-503 answer
   is a complete, parseable document.

   **DISCHARGED 2026-08-23** by `AdmissionControlConformanceTests`, over HTTP against a running
   server: 160 callers released from one gate at a layer whose answer is 342 kB, every body read
   to its last byte, every one parsed, and every one carrying the same feature count as the
   unloaded request. **None was refused in that run and the test does not ask for one** —
   whether this machine can drive the queue to its bound is condition 1's problem, and a test
   that demanded a 503 would fail on a fast host for the right reason and be deleted for the
   wrong one. The invariant asserted is the one that separates this decision from alternative A,
   and it holds either way.

   **What the first version of that test proved was nothing, and it passed.** It flooded
   `GRATICULA_TEST_QUERYABLE` — eight rows, 2.3 kB, one socket write — in 946 milliseconds. A
   body that small cannot be cut in half, so the test would have stayed green on the day
   somebody implemented alternative A and broke every large response. It now names its own
   layer and **asserts that the body is at least 64 kB before asserting anything about it**,
   because a vacuous assertion that reports success is worse than no test.

5. **The pathological single holder is recorded as still open.** Alternative A is the answer
   to it and this decision does not implement it. A debt row saying so, referring here, so
   that *admission control was fixed* is not read as *admission control is finished*.

   **DISCHARGED 2026-08-23**, and it took two rows rather than one, because measuring this
   decision left two different things open. [D-144](../architecture-debt.md) is the one this
   condition asked for: ~~nothing bounds how long one request may hold a permit~~ — **corrected
   2026-09-09, and the correction is the same one §2 needed: a wall-clock bound arrived on
   2026-08-18, five days before this ADR, and neither knew.** What was created deliberately
   here is the absence of a *rate* floor; ten minutes of wall clock is a bound and not a
   defence — and the reason a tighter one is not free is that the alternative cannot be
   reported to a client. [D-145](../architecture-debt.md) is the second, and it is the one that
   would otherwise have gone unwritten — **the load generator runs on the machine under test**,
   so what is unproven about condition 1 is recorded as a property of the measurement rather
   than left as a gap in the ADR. **D-145 was repaid 2026-09-09, and not by the repair it
   named**: the generator still runs on the machine under test and a better one was enough
   to show that the machine was never the reason. What it leaves behind is
   [D-249](../architecture-debt.md), which is a bigger row than the one it closed.
