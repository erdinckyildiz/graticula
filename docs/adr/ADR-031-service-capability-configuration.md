# ADR-031 — A service's capabilities are configured, and configuration only ever restricts

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` |
| **Date** | 2026-08-17 |
| **Supersedes** | — |
| **Related** | [ADR-013](ADR-013-feature-service-data-model.md), [ADR-018](ADR-018-authorization-and-roles.md), [ADR-020](ADR-020-admin-console-and-service-status.md), [ADR-007](ADR-007-service-runtime.md) §4.8, [Q-67](../open-questions.md), [Q-105](../open-questions.md) |

## 1. Context — what a service can do is currently not a setting at all

Asked by the owner on 2026-08-17: a screen for a service's settings — its timeout,
and **checkboxes for its capabilities**, so that a service can offer vector tiles
and not a FeatureServer.

Two things stand in the way, and neither is the screen.

**Capabilities today are derived from the caller, not from the service.**
`Program.CapabilitiesFor` builds the ArcGIS `capabilities` string per request from the
requesting principal's privileges: `Query` always, `Create` with `features:edit`,
`Update` and `Delete` with `features:fullEdit`. So two callers reading the same service
document see different capabilities, which is correct — it describes what *you* may do —
and it means **there is no such thing as a read-only service** here. An administrator
cannot say *this service does not accept edits*; they can only take the privilege away
from everyone.

**Which faces a service exposes is not modelled either.** A published layer gets a
FeatureServer unconditionally, and a VectorTileServer if and only if it is hosted —
`VectorTileEndpoints` refuses a registered layer with *"registered rather than hosted, so
it has no vector tile service"* ([Q-67](../open-questions.md)). Neither is a stored
decision; both are consequences of the data. There is no row anywhere that says *this
service serves tiles and not features*, and no schema column named for a capability.

**So this is a model decision before it is a UI one**, which is why it is an ADR and not
a console change. [ADR-020](ADR-020-admin-console-and-service-status.md) §2 forbids the
console using any capability the admin API does not expose, and that constraint is doing
its job here.

## 2. Decision — a ceiling, and it never grants

**A service carries a configured capability set, and it is a ceiling on what the
service will do — never a grant.** The effective capability of a request is the
**intersection** of three things:

1. what the data can support (an ArcGIS-servable layer needs an integer object id — ADR-013 §2a; tiles need hosted storage — Q-67),
2. what the service is configured to offer (this ADR),
3. what the caller's privileges allow (ADR-018).

**Intersection, in that order, and the order does not matter because intersection is
commutative — which is the point.** No element may widen what a narrower one allowed.
That is the invariant [Q-105](../open-questions.md) named and could not previously
locate in this codebase, *because until now there was no service-level policy to compose
with*. This ADR creates one, so the invariant stops being abstract: it becomes the rule
this feature is implemented against, and the thing its tests assert.

**Why a ceiling rather than a policy that can also grant.** A grant would mean a service
could hand a caller something their role does not carry, which turns every service
document into a place privilege escalation can hide. It would also make the answer to
*may this person edit?* depend on where they asked, and ADR-018 §3b-i already records
this project shipping one instance of that class of fault.

### 2a. What is configurable

| Knob | Values | Notes |
|---|---|---|
| **Faces** | `features`, `tiles` | Which service documents exist at all. Turning a face off makes it 404 — the same refusal as absent, per ADR-018, so nothing leaks about *why* |
| **Feature capabilities** | `Query`, `Create`, `Update`, `Delete`, `Extract` | A ceiling intersected with the caller's privileges. `Query` may be turned off: a service that exists and answers nothing is a legitimate state during an incident, and is distinct from stopped |
| **Cache lifetime** | seconds, or unset | Already exists — `PUT /admin/layers/{name}/cache`. Moves onto this screen rather than being re-implemented |
| **Statement timeout** | seconds, within bounds | **May only lower.** ADR-007 §4.8 makes a per-connection `statement_timeout` mandatory and D-42 records it being silently removable once already; an override that can raise or unset it re-opens that hole. A service may ask for *less* time than the source allows and never more |
| **Request deadline** | seconds, or unset | **May only lower**, same rule. How long a client may occupy this service, counting the *whole* request rather than the statement. Added 2026-08-18 by owner requirement; §3b |

**The tiles checkbox is shown for a registered layer and disabled, with the reason.**
Hiding it would make Q-67 invisible at exactly the moment somebody is looking for it;
showing it enabled would promise something the runtime refuses. This is the same choice
ADR-020 §5h made about identity-column candidates: state the limit where the operator
meets it.

### 2b. What is *not* configurable, and why

**Sharing is not a capability.** It answers *who may read*, is already a scope on the
layer and the service (ADR-018 §3b), and folding it into a capability checkbox would give
two controls over one fact — which is how they drift apart.

**Started/stopped is not a capability either.** ADR-020 §3 already separates status from
sharing; a stopped service is *not running*, a service with `Query` unchecked *is running
and refusing*, and an operator diagnosing an incident needs to tell those apart.

## 3. Consequences

- **The catalogue gains a capability set per service**, and the schema gains its first
  column named for a capability. Nothing reads it until the runtime does, so the
  migration is additive and the default is *everything the data supports* — which makes
  every existing service behave exactly as it does today.
- **`CapabilitiesFor` stops being the whole answer** and becomes one of three inputs. It
  keeps its current shape; what changes is that its result is intersected rather than
  returned.
- **Q-105 gets its answer in code** — the invariant is stated once, in the intersection,
  and asserted rather than re-derived per surface. The question stays open for the
  *rule* being written down as a property; this ADR is the first surface to which it
  applies.
- **Q-67 becomes visible in the product.** A disabled checkbox with a reason is a
  standing question in front of the person who can answer it.
- **The console gains the screen the owner asked for**, and only after the API exists.

### 3a. Corrections, 2026-08-17

Two things this decision implied and the first implementation did not do. Both were
found by using the screen rather than by reading the code, which is the pattern worth
noting more than either fault.

**A ceiling has to be readable, not only writable.** The API shipped with
`PUT /admin/services/{name}/capabilities` and no `GET`. So the console — the screen this
ADR exists to enable — drew every control from nothing, asked for the current
configuration, received `405`, and reported the refusal in a corner while showing an
operator six empty boxes above a ceiling that was actually in force. **Unticking a box
you were never shown is how a limit gets cleared by accident.** `GET` now returns the
same document the `PUT` accepts, plus `configured`, so a caller can tell *nothing is
set* from *everything happens to be at the default* — those diverge the moment the
server's own defaults move.

**A ceiling the client is not told about is a ceiling the client walks into.** The
FeatureServer documents reported the server's `maxRecordCount` — 50,000 — regardless of
the service's own row ceiling, while the query path enforced the lower figure. A service
capped at 20,000 therefore advertised 50,000, and an SDK client that sizes its paging
from that number (which is what the number is for) pages against a limit that does not
exist. Both documents now advertise the smaller of the two, measured: with a ceiling of
20,000 the layer and service documents say 20,000, and an unconfigured service beside it
still says 50,000. §2's *never grants* has a companion — **never over-declares** — and
ADR-008 §2's never-degrade-silently is the same rule from the other end.

### 3b. The request deadline, added 2026-08-18

**Owner requirement, and it had been asked twice.** With the reference's *Pooling* page
open and an arrow on *The maximum time a client can use a service: 600 seconds*:
*"sadece geometri değil, tüm servislerde timeout olmalı"* — every service needs a
timeout, not only the geometry service. The first delivery of that requirement had been
narrowed to the geometry service, where a settable deadline already lives (ADR-022), and
the narrowing was the fault rather than the timing.

**What existed before, stated exactly, because the gap was smaller than it sounds and
worse than it sounds.** One fixed 30-second `statement_timeout` on the connection pool
(ADR-007 §4.8), shared by every layer on a data source. That bounds a *database
statement*. Projecting geometry, encoding JSON and writing thirty-five megabytes to a
client all happen after the statement has returned, and none of it was bounded by
anything. So the honest answer to *how long can one client occupy this server* was: for
as long as they like. The two bounds are not versions of each other, and the console now
says so where an operator sets them.

**Two stages, because a token has to exist before the handler is invoked.** A
minimal-API handler's `CancellationToken` is bound from `HttpContext.RequestAborted`
before the body runs, so replacing it after the service has been resolved is too late for
the parameter the handler already holds; and resolving the service in middleware, to
learn its deadline, would read the catalogue a second time on every request. So
middleware starts every request on the server's `Graticula:RequestDeadlineSeconds`
(default 600, nought meaning no bound), and `ServiceLookup` — the single place a URL
becomes a service, which has just done the only catalogue read — lowers it. **Lowering is
the only operation the API offers**, which is §2a's *may only lower* expressed as a
signature rather than as a check somebody must remember to write.

**Measured, not asserted.** `tr_yol` (46,041 features) with `request_deadline_seconds =
1`, `tr_ilce` (25,280) with nothing set, forty concurrent full-table queries each,
reprojected to 3857 — the condition a request deadline exists for rather than a
synthetic slow path:

| | 504 | connection aborted | 200 | slowest |
|---|---|---|---|---|
| `tr_yol`, deadline 1 s | **31** | 9 | 0 | 1.80 s |
| `tr_ilce`, no deadline | 0 | 0 | **40** | 13.74 s |

Every 504 landed between 1.13 s and 1.80 s, and carried the number that applied: *"This
request reached the time a client may occupy a service — 1 second — and was stopped."*
The control service, under the same load in the same server, was served in full and took
up to 13.74 s to do it. That is the per-service lowering demonstrated rather than argued:
one number in one row changed what happened to forty requests, and changed nothing for
the service beside it.

**The nine aborted connections are the honest limit of any request deadline, and the
server already said so.** Once bytes are on the wire the status line has been sent, so a
deadline can only hang up: those nine were logged as *"A response failed after the body
had begun, so the client received a truncated document and no status. The connection was
aborted, which is the only signal available once bytes are on the wire."* Nine log lines,
nine clients with no status — the accounting closes. This is not a defect to fix but a
property to document: **a deadline can refuse a request or truncate it, and which one
depends on whether the answer had started.** The consequence for an operator is that a
deadline short enough to fire mid-response will show up in client logs as a network
fault, which is an argument for setting it above the time an ordinary response takes
rather than at it.

**The reference's other two Pooling rows have no analogue here, and saying so is part of
answering the request.** That page carries three numbers, and only one of them transfers:

| Reference | Here |
|---|---|
| *The maximum time a client can use a service* | **This.** ADR-031 §2a, per service, measured above |
| *The maximum time a client will wait to get a service* | **Nothing to wait for.** That number is how long a request queues for a free *service instance*, and this server has no per-service instances — ADR-007 chose a single process serving every service from a shared pool, so there is no queue with a service's name on it. The nearest real thing is the wait for a database connection, which belongs to the data source and not to the service, and which ADR-007 §4.8 bounds through the pool |
| *The maximum time an idle instance can be kept running* | **Nothing to keep running.** Same reason: there is no process per service to reap. What this server has instead is the catalogue cache, whose lifetime is a different decision with a different failure mode |

**This is a difference in architecture, not a gap to fill.** Adding a per-service instance pool
so that two more numbers could be configured would be §82's question answered backwards — the
numbers exist in the reference because the instances do.

### 3c. And the statement timeout beside it, which had never been applied

**Found by building the request deadline next to it.** §2a has listed a per-service statement
timeout as configurable since this decision; the API accepted it, the store kept it, the `GET`
said it back and the console drew a box for it. Nothing in any query path read the value. The
only bound on a statement was ADR-007 §4.8's fixed 30 seconds on the pool. Putting a second time
bound on the same screen is what made *which of these two actually does anything* a question, and
the answer was one of them. D-67.

**It is now applied, and the mechanism was chosen against two alternatives rather than picked.**
The service's figure is carried onto the layer — where the connection is opened — lowered against
the pool's 30 seconds, and applied at the one factory every command in the PostGIS source goes
through. It is `NpgsqlCommand.CommandTimeout`: client-driven, on top of a server-side floor that
a registration cannot opt out of. A server-side per-service value would need either a pool per
timeout (§60: a thousand-service deployment must not pay for this) or a `SET` leading the command
text, which was **measured safe over the pool** — Npgsql discards connection state on return,
checked against the database rather than assumed — but which shifts the result set a streaming
reader opens on. D-68 records the cost of the choice: a firing timeout discards its physical
connection.

**Two things the fix had to get right and one it got wrong first.**

- **A sub-second value is refused, not rounded.** The bound is enforced in whole seconds, so
  500 ms would be applied as 1,000 — a service asking for *less* time being given *more*, the one
  direction §2a forbids. Adjusting an operator's number upward silently is worse than refusing it.
  No service anywhere had a sub-second value set; checked, not assumed.
- **A timeout is a timeout, not an unreachable database.** The first working version answered
  *"A database this server depends on is unreachable"* to 19 of 30 queries against a service whose
  own one-second bound had just fired — because Npgsql's command timeout does not raise `57014`;
  it throws `NpgsqlException` wrapping `TimeoutException`, which fell through to the connectivity
  case. So an operator who set the bound themselves had their clients sent to check the network.
  This is the third time that costume has fooled this mapping — 42883 and 42703 were the first two
  — and the branch is written narrowly on purpose: matching every `NpgsqlException` as a timeout
  would report a database that is genuinely down as a slow query, which is worse because it
  reassures.

## 4. Conditions

1. **The intersection is tested for the direction that matters** — that a configured
   capability cannot grant what a privilege withholds, and that a privilege cannot grant
   what the configuration withholds. Both directions, in one test class, because a
   one-directional test would pass on an implementation that ors instead of ands.
2. **Turning a face off is tested to produce the same refusal as absent**, not a
   distinguishable one, so that the capability configuration cannot be used to enumerate
   what exists.
3. **The timeout override is tested at its bound** — that it can lower and cannot raise
   or unset — because D-42 is the record of that exact control being wrong once already,
   and it was wrong in the permissive direction.
4. **The default is proven to be a no-op** on an existing catalogue: a service with no
   configured set must produce the byte-identical document it produced before this
   change. Asserted against the conformance suite rather than argued.
   *(Discharged 2026-08-17 — the whole conformance suite, 178 tests, passes against an
   instance whose catalogue holds both configured and unconfigured services, and a
   layer with no configuration still advertises the server's own 50,000.)*
5. **Whatever is configured is readable through the same API that writes it, and the
   read is tested field for field** — because a partial read is worse than none: the
   fields it omits come back unset, the screen shows them blank, and the next Save
   clears a limit nobody was shown.
   *(Discharged 2026-08-17 — `GET …/capabilities` added, with a round-trip test over all
   nine fields, an unset-versus-absent test, and a folder-and-case test in
   `PostgresAdminCatalogTests`.)*
6. **What is advertised matches what is enforced.** A row ceiling appears in both
   FeatureServer documents, so no client can be told a page size the server will not
   honour.
   *(Discharged 2026-08-17 — `AdvertisedMaxRecordCount` takes the smaller of the two and
   is exercised from both call sites; measured against a live service at 20,000 and an
   unconfigured one at 50,000.)*

## 5. Assumptions

| ID | Assumption | Status |
|---|---|---|
| A-074 | An operator wants per-service capability control more than per-layer control, so the set lives on the service | `UNVALIDATED`. It is where the peer puts it — service-level settings shared by every layer — and where ArcGIS puts it. A layer-level override is the obvious extension and is deliberately not built |
| A-075 | Turning `Query` off is a state an operator actually wants, distinct from stopping the service | `UNVALIDATED`, and the weakest one here. It is offered because it costs nothing once the set exists; if nobody uses it, it is a checkbox rather than a design |

## 6. Dissent

**Against having the knobs at all.** Every one of them is a way to configure a
deployment into a state its clients did not expect, and this project's own §82 asks what
concrete problem a technology solves. The concrete problem is real — an operator with a
registered table who wants tiles and not an editable feature surface has no way to say so
— but three of the four knobs are conveniences, and a capability set is a place for
configuration to disagree with reality, which is the failure this repository has hit
repeatedly this week in exactly that shape.

**Against the ceiling being the only direction.** An organisation that wants a service to
be writable by a role that does not otherwise hold `features:edit` now cannot express
that, and will express it by granting the privilege more widely instead — which is worse
than what they asked for. The counter is that the answer to that is a role, and roles are
ADR-018's business.
