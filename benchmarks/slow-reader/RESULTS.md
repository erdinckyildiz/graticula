# A slow client holds a database connection for as long as it likes

**Run 2026-08-24**, against the development server on the machine described in
[benchmarks/README.md](../README.md), to answer the half of
[D-144](../../docs/architecture-debt.md) and [Q-139](../../docs/open-questions.md)
that had never been measured: *how long can one request hold a database permit?*

The register's answer, in D-144's own status cell, was:

> The statement timeout already bounds the database's half of a long request,
> which is where a pathological query spends its time, so what is left is the
> server's own half.

**That is false, and the shape of the failure is the opposite of the one the
design was protecting against.** It is not an expensive query. It is a cheap
query read slowly.

---

## 1. What was measured

A hosted FeatureServer layer over `public.big_buildings` — 500,000 polygons,
built for this run as 25 copies of `osm_buildings` and dropped afterwards. Every
request asks for the full 50,000-row page with geometry, simplified and
reprojected:

```
/rest/services/hosted/big-buildings/FeatureServer/0/query
  ?where=1=1&outFields=*&returnGeometry=true
  &resultRecordCount=500000&maxAllowableOffset=0.00001&outSR=4326&f=json
```

17,813,675 bytes. **The service's statement timeout was set to 1,000 ms** — the
smallest the admin API will accept, because `AdminEndpoints` refuses sub-second
values on the ground that they are enforced in whole seconds.

The only variable is how fast the client reads, set with `curl --limit-rate`.

## 2. One reader

| Client rate | Response | Wall time | Database connection |
|---|---|---|---|
| unthrottled | 200, 17,813,675 bytes | **0.65 s** | active 0.65 s |
| 150 kB/s | 200, 17,813,675 bytes | **115.7 s** | **active for the whole 115.7 s** |

Sampled from `pg_stat_activity` every two seconds for the first forty: one
connection, `state = active`, for every sample.

**The 1-second statement timeout did not fire.** The same service, the same
query, the same timeout — the only difference is the client's reading speed, and
it multiplied the hold by 178×.

**Why it does not fire** is worth stating because the first reading of
`pg_stat_activity` was misleading: `query_start` stays about half a second in the
past on every sample rather than ageing, so the backend is not running one
115-second statement. The rows are pulled as the writer consumes them, and each
network round trip restarts the clock the timeout measures. `statement_timeout`
bounds *a statement*; nothing bounds *a request*.

## 2a. It is size-dependent, and a smaller layer does not show it — 2026-08-26

**Re-run on the same server against a smaller response, because a reproduction that
does not reproduce is worse than none.** `tiles-buildings`, 20,000 polygons,
**8,452,230 bytes** — half the size of §2's response — read at 200 kB/s:

| Client rate | Response | Wall time | Database connection |
|---|---|---|---|
| unthrottled | 200, 8,452,230 bytes | 0.17 s | active 0.17 s |
| 200 kB/s | 200, 8,452,230 bytes | **40.8 s** | **idle at every one of ten samples** |

Sampled every three seconds for the first thirty, on the same query and the same
`pg_stat_activity` filter §2 used. **Not one sample was `active`**, and terminating
the backend a second into the read changed nothing: the response completed, all
8,452,230 bytes, valid JSON, `exit 0`.

**So §2's finding is real and its threshold is not stated.** Somebody reproducing
D-144 on an ordinary layer will watch a slow reader hold nothing at all and
conclude the row is wrong.

**The likely mechanism, offered as an explanation rather than as a measurement.**
Nothing here buffers the response deliberately — Kestrel's output limit is its
default and the writer streams. What differs is the pipe *behind* the server: a
result small enough to fit in the backend's send buffer and Npgsql's receive
buffer drains out of PostgreSQL before the slow client has read a tenth of it, and
the backend goes idle with the rows already in the server's process. §2's
17,813,675 bytes do not fit, so the backend stays active feeding a socket nobody
is draining. **The threshold was not bisected** — that would need a layer built for
it, and what matters for the row is that one exists.

**What this does not change.** §2's hold is not an artefact: at 17.8 MB the
connection was active for 115.7 s against a 1-second statement timeout, sampled
every two seconds. The hazard is real and it is worse on exactly the responses
that matter most.

---

## 3. Eight readers

Eight concurrent clients at 60 kB/s each — about 480 kB/s in total, which is
less bandwidth than one ordinary page load:

```
okuyucu1..8: http=200 bytes=17813675 time=289.38s   (all eight, within 8 ms of each other)
pg_stat_activity, sampled every 10 s for a minute: aktif=8, bosta=11
```

**Eight of the data source's 24 permits, held for four minutes and forty-nine
seconds, for half a megabyte per second.** `PerSourceConcurrency` defaults to 24
and `QueueWaitersPerPermit` to 4, so 24 such readers take every permit, the next
96 queue, and everything after that is refused — at a cost to the client of about
1.4 MB/s and no expensive query anywhere.

**A false start, recorded because it nearly became the finding.** An earlier run
counted `pg_stat_activity` rows matching the table name without filtering on
`state`, and reported 24 connections while a normal request answered in 33 ms.
The 24 were pooled connections sitting **idle** with their last query text still
attached. Counting what a pool leaves behind as work in progress would have
inverted the result.

## 4. The bound that exists and is switched off

Kestrel has `MinResponseDataRate`, which aborts a connection whose client reads
below a floor. **This server never sets it**, so it sits at the framework default
of 240 bytes/second — 250 times slower than the readers above, which is why it
never fired.

Set temporarily to 100 kB/s and rebuilt, against the same 60 kB/s reader:

| | Bytes delivered | Wall time | Outcome |
|---|---|---|---|
| default (240 B/s) | 17,813,675 of 17,813,675 | 115.7 s | 200, complete |
| **100 kB/s** | **11,642,336 of 17,813,675** | 189.5 s | **connection aborted, curl exit 56** |

The change was reverted immediately; it is in no commit.

**So the mechanism works, and what it produces is [D-07](../../docs/architecture-debt.md).**
The client is left holding 11.6 MB of valid JSON prefix with no error in it and
no status line to carry one — a truncated page that an ArcGIS paging client
cannot distinguish from a short one, which is exactly the silent data loss D-07
names. The difference is that here it would be **policy**: the server ending a
response it decided not to keep paying for.

## 5. What this settles and what it does not

**Settled.**

- A request's hold on a database permit is bounded by nothing on the database
  side. The statement timeout does not do it, and D-144's status cell said it
  did.
- `LayerConnections.WithStatementTimeout`'s own doc comment — *"without one, a
  single expensive query holds a pooled connection until the client gives up"* —
  describes a failure the timeout does **not** prevent, because the client giving
  up is the part that was never bounded.
- The cheapest way to exhaust a data source is not an expensive query. It is
  ordinary queries read slowly, which needs no privilege beyond reading a public
  service.

**Not settled, and it is one number rather than a design.** What floor is right.
Too high refuses a genuine mobile client on a large export; too low is what is
there now. That is [Q-139](../../docs/open-questions.md), and it stays open
because the number is a product judgement about who is allowed to be slow — but
the question has changed from *is there a mechanism* to *what is the number*, and
those need different work.

**Also not settled:** whether the abort can ever say why. It cannot, over
HTTP/1.1, which is [Q-91](../../docs/open-questions.md) — so choosing a floor
means choosing to produce D-07's truncated document deliberately, and the two
decisions should be taken together rather than one at a time.
