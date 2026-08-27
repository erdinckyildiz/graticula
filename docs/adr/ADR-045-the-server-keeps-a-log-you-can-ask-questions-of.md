# ADR-045 — The server keeps a log you can ask questions of

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` |
| **Decided** | 2026-08-22 |
| **Supersedes** | — |
| **Superseded by** | — |

---

## 1. Context

**The owner asked for a screen that answers questions about what the server and the studio
have been doing, and said the application has no log structure.** That is very nearly true,
and the exact shape of what is missing is worth writing down, because one of the three
things is already built and two are not.

**What exists and cannot be read.** `audit_event` has been written on every administrative
action since the platform store's first migrations: `occurred_at`, `principal_id`,
`principal_name`, `source_address`, `action`, `resource`, a `jsonb` `detail`, `succeeded`,
and an index on `occurred_at desc` with the comment *newest first is the only order anyone
reads an audit trail in*. Measured on the development store on 2026-08-22: **18,215 rows
across dozens of action kinds** — `service.delete` 5,501, `layer.applyEdits` 863,
`datasource.register` 288, `principal.password` 350. **Nothing reads it.** There is no
route, no API, no screen. Somebody built a careful audit trail and it has never been looked
at, which is a strange kind of missing: the data is there and the question is unanswerable.

**What does not exist: request activity.** Every request is logged to standard output by the
`requests` logger and nowhere else. **The cost of that is measured rather than argued**:
[D-138](../architecture-debt.md) was opened on 2026-08-22 claiming the request log recorded
almost nothing, and withdrawn the same day when the real file was found with 8,997 request
lines in it — the development script computed its path before `cd`-ing and wrote it
somewhere else. A log whose location can be lost for two days by a one-line path bug is not
a log an operator can be asked to rely on, and the diagnosis it was needed for
([Q-134](../open-questions.md)) was done by putting a proxy in front of the server instead.
An operator cannot put a proxy in front of production.

**What does not exist at all: anything the studio does.** The viewer surface under
`/studio/` runs in a browser, and the browser is where its failures happen. **The strongest
evidence for this is from the same day**: `map.html`'s layout wrapper and `map.js`'s button
both used `id="frame"`, so the frame-the-layer handler was bound to the whole page and every
click reset the view. Every request succeeded. Every status code was 200. **No
server-side log could ever have shown it**, and it was found only because a person drove the
page with a browser and clicked.

## 2. Alternatives considered

### Alternative A — Keep standard output and ship it somewhere that can query it

**Argument for.** This is what the industry does and it is the right answer at scale. The
server stays out of the query business entirely: structured lines to stdout, a collector,
and an operator who already runs a log stack points it here and gets retention, full-text
search, alerting and correlation across services for free. It keeps write load off the
database that is serving the data — which is the single strongest objection to the option
chosen below. It also composes: an operator with fifty services of other kinds does not want
this one to have its own private log UI.

**Argument against.** §6 asks what concrete problem a technology solves and §60 says not to
make small deployments painful in order to serve hypothetical large ones. The baseline
deployment is `graticula → PostgreSQL` and nothing else. Under this option, *who deleted that
service* is unanswerable on the baseline deployment — the answer requires installing a
second system first. **The product is a gift**, and a gift whose audit trail requires an
accompanying log stack has an audit trail its recipient does not have.

### Alternative B — Files on disk, read with a text editor and `grep`

**Argument for.** Free, already half-true, no schema, no write path to design, and every
operator already knows the tools.

**Argument against.** This *is* the current state and D-138 is what it costs. A log you find
by knowing which directory the process happened to be in when it started is not a queryable
log, and *which principal did this* is not a `grep` question.

### Alternative C — A ring buffer in memory, exposed on an endpoint

**Argument for.** No schema, no write amplification, no retention policy, no migration. Fast
enough to be free. Good enough for *what is happening right now*, which is a real question.

**Argument against.** It answers only that question. A log is most often consulted after the
thing it recorded caused a restart, and a restart is exactly what empties this. The audit
trail in particular must survive: an administrative action from last Tuesday is the case it
exists for.

### Alternative D — Persist all three to PostgreSQL and give them one query surface

**Argument for.** The audit half is already here and already durable; this makes it readable.
The baseline deployment answers every question with nothing else installed. One screen, one
set of filters, three sources.

**Argument against.** It puts a write on the same database that serves the data, for every
request. That is the real objection and it is answered in §3 rather than dismissed.

## 3. Counterarguments to the preferred option

**A row per request is a write per request, on the database that is also answering the
request.** At the stated scale — 100 to 1,000 services (§7) — a busy server can serve
thousands of requests a minute, and every one of them would add an insert to the store that
is concurrently running the queries those requests are for. This is not a small objection
and *it is the reason this ADR's confidence is MEDIUM rather than HIGH.*

The answer is that **no request may ever wait on the log**. Records go to a bounded
in-memory queue and a background worker flushes them in batches; when the queue is full the
record is **dropped and counted**, and the count is visible on the screen. A dropped log
line is a bad outcome; a request that got slower because of logging is a worse one, and
silence about the drop would be worse than both. That is condition 1 and condition 6.

**It is also the reason for a retention cap rather than a promise to add one.** An unbounded
table on the same store is the same objection arriving a month later.

**Persisting the query string persists credentials.** [D-120](../architecture-debt.md) is
exactly this: Esri clients send a session token as `?token=` because that is how they have
always worked, so the query string of an ordinary request contains one. Writing request
lines into a table the console can read would copy that debt into a place with an index on
it. `QueryRedaction.Redact` already exists and is asserted from outside by ADR-015 condition
2's test; the request log must go through it, and condition 2 asserts that it does.

**An endpoint that accepts events from a browser is an unauthenticated write.** The studio is
usable anonymously, so the ingest route must be too, and *anything a stranger can insert
rows with* deserves suspicion. Hard body-size cap, a per-address rate limit, a bound on rows
per address per interval, and the stored text treated as untrusted wherever it is rendered.
Condition 4.

**Three sources in one screen risks being three screens wearing a coat.** The filters that
matter differ: an audit reader wants principal and action, a request reader wants status and
path and duration, a studio reader wants the page and the message. If the shared surface
forces a lowest common denominator it will serve none of them. The answer is one screen with
a source selector and *source-specific* filters beside the shared ones — not one filter set
pretending to fit.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| The audit trail is rich and unread | 18,215 rows, dozens of actions; no route matches `/admin/audit` | measured on the development store, 2026-08-22 |
| Standard output is not a dependable log | D-138: opened and withdrawn in a day; the file `tail` was reading was two days stale | [D-138](../architecture-debt.md) |
| A proxy was needed to answer a real question | Q-134's four parser defects were found from a proxy trace | [Q-134](../open-questions.md) |
| Server-side logs cannot see studio failures | the duplicate `id="frame"` reset the view on every click; every request returned 200 | 2026-08-22 viewer review |
| Redaction already exists and is tested | `QueryRedaction.Redact`, asserted by `TokenIsNotLoggedTests` | ADR-015 condition 2 |
| A request-rate write is the real risk | not yet measured — this is why confidence is MEDIUM and why condition 1 exists | — |

## 5. Decision

**This server keeps three logs in its own platform store and gives them one query surface in
the console.** `audit_event` stays as it is and gains a read API. A new `request_log` records
one row per request — method, path, redacted query, status, duration, principal, source
address, the face it reached and the service it named — written through a bounded queue by a
background flusher that a request never waits on. A new `client_event` records what the
studio reports from the browser: unhandled errors, failed layer loads, and the page they
happened on, accepted on a rate-limited, size-capped, anonymous-allowed endpoint. Both new
tables have a stated retention cap enforced by a sweeper. The console gains a **Logs** screen
with a source selector, shared filters for time and principal and free text, and
source-specific filters beside them.

## 6. Consequences

**Positive.** *Who deleted that service, and from what address* becomes answerable on the
baseline deployment with nothing else installed, against 18,215 rows that already exist.
*Which request was slow* and *what failed in the viewer* become answerable at all. The
diagnosis method this repository has actually needed twice — read the requests a client
really sent — stops requiring a proxy.

**Negative.** A write per request on the serving database, mitigated but not eliminated; the
mitigation makes the log lossy under load by design, which means the log is not evidence of
absence. Two new tables to migrate and sweep. A new unauthenticated write endpoint, which is
a new attack surface however tightly bounded. And a screen whose value depends on the
retention window being long enough to contain the question being asked, which is a setting
somebody will get wrong.

**Ports created.** None new for the read side. The request and client writers sit behind
interfaces in `Graticula.Platform` with the Postgres implementations beside the existing
audit one, so the store is replaceable on the same terms as the rest of the platform.

**State.** *Catalogue*: **two tables** — the request log and the audit trail — with a
retention cap this ADR states as a number, swept rather than allowed to grow. Both are catalogue
because the question they answer is *what happened on this deployment*, not on this node.
*Runtime*: a **bounded in-process queue** in front of each, node-local, whose drops are counted
and shown, because a log that silently loses records under load is worse than one that says how
many it lost.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | A batched insert per request is affordable on the baseline deployment | **Unverified, and condition 1 is what verifies it.** If it is not affordable, the request log becomes sampled rather than complete, and this ADR is reviewed |
| — | Operators want the log in the product rather than in their own stack | Inferred from the owner's request, and from §60 |

## 8. Dependencies

**Depends on** ADR-007 (the service runtime and its admission control), ADR-015
(authentication, whose condition 2 owns the redaction), ADR-024's absence — there is no
observability ADR, which is part of why this one exists.

**Depended on by** nothing yet.

## 9. Conditions

1. **A request never waits on the log, and it is measured rather than asserted.** A
   benchmark comparing request latency with the request log on and off, at a concurrency
   that matters, with the numbers written down. If the difference is not near zero the
   design is wrong, not the measurement.

   **DISCHARGED 2026-08-22.** [benchmarks/request-log](../../benchmarks/request-log/RESULTS.md),
   400 requests a row, one variable. Serial 17.89 against 18.25 ms; 16 at once 22.03 against
   21.54; 64 at once 61.99 against 64.55. **The reading is the sign change, not the size of
   the gap**: in two of three pairs the run with logging is the *faster* one, which is what a
   difference smaller than the noise looks like and is the only honest conclusion available.
   Nothing was dropped across 1,200 logged requests.

   **The comparison needed a switch, so `Graticula:RequestLog` exists.** A condition that can
   only be argued is not a condition — and an operator running a busy read-only deployment has
   a reason to want it off anyway.

2. **No token, password or secret reaches either new table, asserted from outside.** A test
   that sends a request carrying a sentinel token in the query string and then reads the
   table back, in the shape of `TokenIsNotLoggedTests` — which asserts the same thing about
   the text log and is the reason that mechanism exists to reuse.

   **DISCHARGED 2026-08-22.** `A_token_in_a_query_string_never_reaches_the_request_log` sends
   `?f=json&mark=…&token=SENTINEL…`, waits for the row, and asserts the sentinel is absent
   while `REDACTED` is present. **The second parameter is the finding**: the first version
   polled for the path and passed against a row somebody else's test had written, because
   every suite in the assembly hits `/rest/info`. The one thing that cannot be polled for is
   the token, which is the whole point of the test — so a non-secret marker rides along.

3. **Retention is enforced, and the cap is a number this document states.** A sweeper, a
   test that proves the table stops growing, and the cap written here rather than left to a
   default nobody chose.

   **DISCHARGED 2026-08-22. The cap is thirty days** — `LogRetention.Keep` — swept hourly,
   starting at boot rather than an hour in, and the sweep says how many rows it took.

   **`PostgresLogReaderTests` is the one place in this feature that writes rows directly, and
   it does so for a reason it can state.** Proving a thirty-day window needs a row thirty days
   old and no request produces one, so the row is inserted and **only its age is contrived** —
   the sweep itself runs the shipped code, and the result is read back through
   `ILogReader` rather than with a `count(*)`, so the delete and the reader are asserted to
   agree. Both sides of the boundary are present in one sweep: a forty-day row and a one-hour
   row of each kind, two swept, two left.

   **The audit trail is not swept, and that has its own test.**
   `The_sweep_never_touches_the_audit_trail` writes a four-hundred-day-old action and asserts
   it survives — because *who deleted that service last quarter* is the question the trail
   exists for, and a deliberate asymmetry with no test is exactly what somebody generalises
   away.

4. **The client-event endpoint cannot be used to fill the store.** Body size capped, rate
   limited per address, and both asserted from outside by a test that tries.

   **DISCHARGED 2026-08-22.** `The_studio_reports_a_failure_the_server_never_saw_and_cannot_flood_it`
   posts a 64 KB body against an 8 KB cap and asserts nothing of it is stored, then posts 90
   events against a 60-a-minute limit and asserts fewer than 90 rows arrive. Every attempt is
   answered 204, including the refusals.

   **It is one test because the two halves cannot be independent, and pretending otherwise
   made both flaky.** They were written as *it accepts* and *it refuses a flood*; the rate
   limit is per source address and every test in the suite comes from one, so whichever ran
   second found the minute's budget spent — the flood half read that as a broken endpoint and
   the accept half waited five seconds for a row that was never coming. Merging makes the
   dependency deliberate: accept first, then spend the budget on purpose.

5. **The screen answers a real question end to end.** Not *the screen renders*: a test that
   performs an administrative action, then finds that action on the Logs screen by
   filtering for it. The same for a request and for a studio event.

   **DISCHARGED 2026-08-22.** The API half is asserted for all three sources:
   `The_audit_trail_can_be_filtered_to_one_action` reads the action list, filters by the first
   of them and asserts every returned row is that action;
   `The_request_log_records_what_was_asked_and_how_long_it_took` makes a request and finds it
   with its duration and face; the studio test above finds its own event.
   `Paging_by_cursor_does_not_repeat_a_row` asserts the property the cursor exists for.

   **A browser review found two defects that every API test passed through, and
   `LogsScreenTests` now holds the line.** The per-source filter was inert on all three
   sources — `drawLogControls` rebuilt the control before the query read it, so the chosen
   value went into an element that no longer existed — and switching source was one click
   behind, because a debounced keystroke's read resolved after the click's and painted the old
   source's rows under the new tab. **Both requests the server received were correct**, which
   is why nothing outside the browser could see either.

   **The two tests are written against the symptom rather than the mechanism.**
   `The_action_filter_actually_filters` asserts on the *content* of the rows and not their
   count, because a screen ignoring its filter still has rows.
   `Switching_source_shows_that_source_and_not_the_previous_one` types into the shared filter
   and switches source in the same tick — the sequence that put two reads in flight — then
   requires the highlighted tab and the rows in the table to agree.

   Beside them: the screen opens from a bare `#/logs` with every control visible by
   `offsetParent` and a box rather than by presence; a row's detail opens by keyboard; the
   dropped notice appears on the log it describes and nowhere else; and an empty studio log
   says that empty is the good outcome.

6. **A dropped record is visible.** The queue is bounded, so records will be dropped under
   load; the count is exposed and shown on the screen. A log that quietly stops recording
   is worse than one that says it stopped, and this is the condition that makes the
   §3 mitigation honest rather than convenient.

   **DISCHARGED 2026-08-22.** `/admin/logs` reports `writer.dropped` and `writer.waiting`, and
   `The_index_says_how_much_the_request_log_has_dropped` asserts both are present — the number
   may be zero, the field may not be missing. The screen says either *nothing has been dropped
   since this server started, so this is every request* or *N entries were dropped … a gap here
   is not proof that nothing happened.*

   **Shown only on the Requests tab, which took a review to notice.** It was on all three, and
   it describes one: the audit trail fails the request rather than dropping the row, and studio
   events are written straight through. Telling a reader of the audit trail that nothing had
   been dropped invited them to wonder what could be.
