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
