# ADR-059 — Quiescing a data source

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `HIGH` for the refusal and the deadline · `MEDIUM` for the window's length |
| **Decided** | 2026-09-08 |
| **Supersedes** | — |
| **Superseded by** | — |

**Amends** [ADR-007](ADR-007-service-runtime.md) §4.8, whose quiesce bullet says
*hold its requests*. This says **refuse** them, and §3 is why.

---

## 1. Context

**A held connection blocks a DBA's DDL, and on PostgreSQL it blocks everybody
else too.** `ALTER TABLE` needs `ACCESS EXCLUSIVE`; a request already reading the
table holds it off, and every request arriving *after* the waiting DDL queues
behind it. From the DBA's side the table stops responding, and the cause is one
of our connections.

**The number is measured and it is this repository's own.**
[benchmarks/statement-timeout](../../benchmarks/statement-timeout/RESULTS.md),
recorded in [D-08](../architecture-debt.md): a read that takes **296 ms**
unblocked holds its pooled connection for **30.30 s** when it is behind a lock —
a hundredfold — while every layer sharing that data source waits behind it.

[ADR-007](ADR-007-service-runtime.md) §4.8 lists five things that follow from
this. Four are built: the connection budget, the per-source concurrency limit,
the circuit breaker, and shrink-to-zero (which turned out to be Npgsql's, not
ours). **The fifth is quiesce and it has never existed** —
[architecture-completeness.md](../architecture-completeness.md) carries it as an
obligation with every review column blank, and
[ADR-058](ADR-058-the-datastore-schema-is-edited-from-the-screen.md) §6 names it
again as the thing that decision does not touch.

**What its absence costs, exactly.** A DBA who wants to alter a registered table
has three options today: run the DDL and hope no request is mid-read; run it with
a `lock_timeout` and retry until one lands in a gap; or stop the server. The
first is the one they take, and it is the one that stalls the table for the
statement timeout.

## 2. Alternatives considered

### Alternative A — Nothing. The statement timeout is the bound

**Argument for.** It works, in the sense that the stall ends. Thirty seconds is
survivable, `lock_timeout` gives a DBA a fast failure they can retry, and
[D-150](../architecture-debt.md) already made `55P03` say something true.

**Argument against.** *Survivable* is doing a lot of work: thirty seconds of a
whole data source unavailable, at a moment chosen by somebody who did not know
they were choosing it. And retrying into a gap is a race the DBA loses more often
the busier the server is — which is exactly when they are most reluctant to try.

### Alternative B — Hold the requests, which is what ADR-007 §4.8 says

**Argument for.** It is the kinder shape: a client waiting two seconds during a
schema change never learns there was one. It is also what the earlier decision
says, and departing from a decision needs a better reason than a preference.

**Argument against.** **The wait is unbounded and an operator holds the other
end.** ADR-046 already settled the general form of this question — admission
control bounds the *queue*, not the wait — because a wait somebody else controls
is a queue that grows until it collapses. A DBA whose migration takes ten minutes
would have every request of those ten minutes held open, which is the connection
exhaustion §4.8 exists to prevent, reached from the inside.

### Alternative C — Refuse, with a bounded window

**Argument for.** The refusal is honest and immediate: a client is told the
source is being worked on and can retry. Nothing accumulates. And the window
bounds the failure an operator can cause by walking away — which is the failure
mode that matters, because it is silent.

**Argument against.** Clients see errors during a planned change, which
Alternative B would have hidden. That is a real cost and it is accepted: a
five-second stall and a 503 are both visible, and only one of them is
explainable.

## 3. Decision

### 5a. Quiesce refuses, and it does not hold

A quiesced data source answers **503** to every request that would reach it,
immediately, with a sentence naming what is happening and when it ends. Requests
are not queued, not delayed and not retried internally.

**This amends [ADR-007](ADR-007-service-runtime.md) §4.8's wording**, which says
*hold its requests*. Holding is a wait whose length an operator controls, and
[ADR-046](ADR-046-admission-control-bounds-the-queue-not-the-wait.md) has already
decided what this project thinks of those: it bounds the queue rather than the
wait, because a queue behind an unbounded hold is one that collapses. The four
built bullets of §4.8 are all about not letting a slow thing accumulate callers;
holding would be the fifth doing the opposite.

### 5b. It lapses, and the deadline is the point rather than a detail

A quiesce carries a deadline. When it passes the source answers again, with no
second act required from anybody.

**The failure this prevents is the one nobody notices.** An operator quiesces a
source, is called away, and the service is down until somebody works out why —
with a server that is behaving exactly as instructed and a status page that says
so in a place nobody was looking. Every other refusal in this server ends by
itself: the breaker cools in ten seconds, admission control clears as the queue
drains. This is the only one a person starts, so it is the only one that needs a
clock.

**Fifteen minutes, and the number is argued rather than measured.** It is longer
than any single `ALTER TABLE` this product's own migrations issue and shorter
than a working session, so a DBA doing one change does not meet it and a DBA who
has gone to lunch does. It is a setting, and resuming is one request — so a
migration that genuinely needs longer is a second quiesce rather than a hostage.
The honest limit is that nobody has measured how long a real customer's DDL takes
on a real table; §7's condition 3 is that measurement.

### 5c. The refusal is what frees the lock. The pool is closed for two smaller reasons

~~Quiescing closes the source's connection pool. **A refusal that left the
connections open would be a slower way of doing nothing** — the DDL is blocked by
*connections*, not by requests, so a source that refuses politely while holding
eight backends open is still the reason the DBA cannot work.~~

**Struck before it shipped, and measured rather than argued — 2026-09-08.** That
paragraph had the mechanism backwards, and
[ADR-007](ADR-007-service-runtime.md) §4.8's own table had already said so:

| What is held | ALTER TABLE |
|---|---|
| Open connection, no transaction — **what a pool holds** | **0.410 s**, completes |
| Idle in transaction, having touched the table | **5.262 s**, `lock_timeout` fires |

Measured against this repository's PostGIS container with a five-second
`lock_timeout`. **An idle pooled connection does not block DDL**, so closing the
pool frees nothing that was blocking. **What frees the lock is the refusal**: no
new query starts, the running ones finish, and the DBA's `ALTER TABLE` takes its
lock as the last of them ends.

**The pool is still closed, and here is what that is actually for.**

- **A DBA verifies by looking**, and `pg_stat_activity` is where they look. A
  source that refuses while showing eight of our backends is one they cannot tell
  from a source that has ignored them.
- **A delegated query is idle-in-transaction and does block** — §4.8's second row
  and A-036. RLS delegation runs inside an explicit transaction that holds
  `ACCESS SHARE` for the whole stream, so those are exactly the connections worth
  removing, and they are the ones a pool close reaches once the request ends.
- **The connection count goes back**, which matters on a database near
  `max_connections` at the moment somebody is trying to work on it.

Reopening happens on its own: the pool is keyed by connection string and rebuilt
on the next request after the quiesce ends, which is the same path a cold start
takes.

**What this correction changes about the feature: nothing, and that is worth
saying.** The refusal was always going to be the mechanism; the ADR simply
credited the wrong half of it. Recorded rather than quietly fixed because the
wrong version was written *with §4.8 open in the next tab*, which is how a claim
survives being obviously checkable.

### 5d. It is per database, which is the unit the lock lives on

~~It is per data source~~ — **corrected 2026-09-08 by running it.** Not per
service and not per layer: a hundred services can share one registered database
and the DBA is altering a table in *that*, so quiescing a service would leave the
other ninety-nine holding the connections that block them. That much was right.

**What was wrong is the unit.** The register is keyed by connection string,
because that is what a *pool* is keyed by (ADR-007 §4.8) — and two registered
data sources may point at one database. Quiescing either takes both out. Measured
on a fixture where a source was registered against the same PostgreSQL the
datastore uses: quiescing the datastore left both rows refusing.

**That behaviour is correct and the wording was not.** The DBA's lock is on the
database, so taking one source out and leaving the other holding connections
would have been the bug. What needed fixing was the response, which named only
the source that was asked for: it now lists the others that went out with it, and
says why.

The datastore can be quiesced too. It is a data source like any other here, and
[ADR-058](ADR-058-the-datastore-schema-is-edited-from-the-screen.md)'s field
endpoints go through the same pool.

### 5e. The refusal says who, why and how long is left

*Taken out of service by `erdinc` — a schema change. It answers again in about
twelve minutes unless it is resumed sooner. Nothing is wrong with the database.*

**A duration rather than a clock time, and the first version got that wrong.** The
instants here are UTC, so a sentence formatting them as `HH:mm` printed a UTC wall
clock into prose read by an operator in their own timezone — *answers again at
14:17* to somebody whose clock says 17:14 reads as a fact they can check, and is
not one. Three hours wrong for this project's own owner. The exact instant is in
the response body as a proper offset for anything that needs to compute with it.

**Because the alternative is a 503 that looks like an outage.** This server
already has one refusal an operator can mistake for a broken database —
[D-150](../architecture-debt.md) was exactly that mistake — and a planned,
deliberate, self-ending unavailability that reads like a network fault would send
whoever is on call to the wrong place at the one moment somebody already knows
the answer.

### 5f. Quiescing needs `admin:manageServer`, not a content privilege

Nothing about a layer changes. This is an operational act on the process and on
somebody's database, and it makes every service over that source unavailable —
which is the shape `admin:manageServer` already guards.

## 4. Consequences

**Positive.**

- A DBA can be given a window in which our connections are gone, deliberately,
  instead of racing a `lock_timeout` against traffic.
- The one thing §4.8 asked for and never got is built, so
  [architecture-completeness.md](../architecture-completeness.md)'s blank row
  stops being blank.
- The refusal is honest and bounded, and it ends by itself.

**Negative.**

- **A planned change now shows clients errors** that Alternative B would have
  hidden for short changes. Accepted in §2.
- **An operator can make a service unavailable in one request.** That is the
  point of the feature and it is still a new way to break serving; the deadline
  and the privilege are what bound it.
- **It does nothing about a request already in flight.** A read that started
  before the quiesce holds its connection to the end, so the DBA still waits for
  the longest in-flight statement — up to the statement timeout. Bounding *that*
  is [D-144](../architecture-debt.md), which ADR-046 created deliberately and
  which this does not close.

**State.** None new in the catalogue. A quiesce lives in the process, beside the
circuit breaker's tripped set, and is deliberately **node-local**: it is about
*this* worker's connections, and a second worker holding its own is a second
quiesce. That is a real limit and §7's condition 2 is the test that says so out
loud rather than leaving an operator to discover it.

**Ports created.** None.

## 5. Assumptions

| ID | Assumption | Status |
|---|---|---|
| A-036 | A delegated query runs inside a transaction and holds `ACCESS SHARE` for its whole stream | Unchanged, and quiesce does not help it: §4's third negative is the same fact |

## 6. Dependencies

**Depends on** — [ADR-007](ADR-007-service-runtime.md) §4.8 (which it amends),
[ADR-046](ADR-046-admission-control-bounds-the-queue-not-the-wait.md) (whose rule
about bounded queues decides §5a), [ADR-049](ADR-049-a-face-refuses-in-its-own-vocabulary.md)
(each face says this in its own words).

**Depended on by** — [ADR-058](ADR-058-the-datastore-schema-is-edited-from-the-screen.md),
whose §6 names quiesce as the thing it does not touch.

## 7. Conditions

1. **The refusal is measured against a real DDL**: a source is quiesced, an
   `ALTER TABLE` that would otherwise wait completes promptly, and the source
   answers again by itself when the window ends. Without that measurement this is
   a feature that refuses requests and may still not free the lock.
   ***(PARTLY DISCHARGED 2026-09-08, and the measurement found the ADR wrong.)***
   The two lock states are measured and are in §5c: an idle pooled connection does
   not block DDL at all (0.410 s), and an idle-in-transaction one does (5.262 s to
   `lock_timeout`). That answers *what blocks* and it corrected this decision's
   own account of why quiesce works.

   **What is still owed is the end-to-end run**: a service under load, a source
   quiesced, an `ALTER TABLE` timed against the same statement issued without
   quiescing. The conformance suite asserts what a client sees — the refusal, its
   sentence, its `Retry-After`, and the source answering again — and that is not
   the same as showing the DBA got their lock.
2. **The node-local limit is tested rather than only written down.** §4 says a
   second worker holds its own connections; a test that quiesces one and shows
   the other still serving is what stops that becoming a surprise in a
   deployment.
   ***(Discharged 2026-09-08 — measured with two workers.)*** Two processes against
   one platform store, on 8451 and 8453, both answering a count on the same layer:

   | | 8451 | 8453 |
   |---|---|---|
   | before | 200 | 200 |
   | after quiescing **on 8451** | **503** | **200** |
   | after resuming on 8451 | 200 | — |

   **So the limit is real and it is exactly what §4 says.** An operator who quiesces
   one worker and hands the database to a DBA has left the other worker's
   connections in place; the DDL still waits behind them. The response says so in
   its own words rather than leaving it to be discovered from a DBA who is still
   blocked.

   **Not turned into a suite test, and why.** The conformance suite is pointed at
   one server by `GRATICULA_TEST_URL`; a second is a harness change for one
   assertion, and the assertion is about deployment topology rather than about the
   server's behaviour. Coordinating quiesce across workers is
   [runtime-supervisor.md](../runtime-supervisor.md) §7 and
   [Q-65](../open-questions.md), and that is where a test of it belongs.
3. **The window is measured against a real customer's DDL** before fifteen
   minutes is defended as anything but a guess. §5b says so; this is the row that
   keeps it from being forgotten.
4. **The console offers it where the DBA's problem is visible**, and goes through
   the ux-designer first — the owner's standing instruction.
   ***(PARTLY DISCHARGED 2026-09-08.)*** The Data sources screen carries *Quiesce…*
   and, on a held source, *Resume* and a line saying who took it out and until
   when. That is the screen somebody is on when a DBA tells them the database will
   not let them work.

   **Running it found the wording of §5d wrong**, which is recorded there: two
   registered sources pointing at one database share a pool, so quiescing either
   takes both out — correct behaviour, and a response naming only the one that was
   asked for was a half-truth. It now lists the others.

   **The review has not run.** The two before it each found a paragraph with no
   live region and a control a redraw threw the cursor off; this screen has both
   shapes.

## 8. Revisit triggers

- **Anybody asks for the hold** rather than the refusal, with a case where a
  short change should be invisible to clients. §2's Alternative B is written to
  be picked up again.
- **A deployment runs more than one worker.** The node-local limit in §4 becomes
  a coordination problem, which is
  [runtime-supervisor.md](../runtime-supervisor.md) §7's *coordinate quiesce* and
  [Q-65](../open-questions.md).
- **A DDL that needs longer than the window** is met in practice.

## 9. Dissent

**Refusing during a planned change is worse for clients than a short hold, and
this decision knows it.** A two-second stall during a column addition is
invisible; a 503 is a support call. The argument that wins is not that refusing
is nicer — it is that the hold's length is set by a person who has already walked
away once in every organisation that has this feature, and there is no bound to
give it that does not turn back into a refusal at the end.
