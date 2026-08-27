# ADR-026 — Serving through a platform-store outage: public-only, while blind

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `HIGH` for the policy · `MEDIUM` for the window |
| **Decided** | 2026-08-15 |
| **Answers** | Q-95 |
| **Amends** | [ADR-007](ADR-007-service-runtime.md) §4.3 |
| **Supersedes** | — |
| **Superseded by** | — |

---

## 1. Context

**Every feature request reads the catalogue.** [D-17](../architecture-debt.md)
records that as deliberate: reading the store on each request is what makes a
revoked permission take effect on the next one, and what makes *stop this
service* mean stopped rather than stopped-eventually.

The cost is that PostgreSQL becomes a total single point of failure for
**reading**. If the platform store is unreachable, the server stops answering
layers it has served a thousand times — including layers whose data is in a
completely different database, belonging to the customer, that is up and well.
That is the isolation [ADR-019](ADR-019-portal-server-split.md) spent when it
fused the tiers, and [Q-95](../open-questions.md) is the bill.

**And there is a live contradiction to settle.** [ADR-007](ADR-007-service-runtime.md)
§4.3 requires a bound service context to carry its effective authorization data
precisely so that a store outage *freezes* serving rather than stopping it. §4.3
was written for one server. With two servers over one store, the mutation
happens in the other process, so the stale value is not missing — it is present
and wrong, and §4.3's *fails closed* rule never fires. Both positions are
defensible; they cannot both be implemented.

## 2. Alternatives considered

### Alternative A — leave it: an outage stops serving

**Argument for.** The strongest security posture available. Revocation is
instant, stopping a service is instant, and there is no window in which the
server acts on a permission nobody can confirm. It is also the only option with
no new code, and therefore no new bug.

**Argument against.** It makes the platform store a single point of failure for
every read in the product, including reads that touch none of its data. A
registered layer in the customer's own PostGIS goes dark because *our*
bookkeeping database is restarting. And it leaves §4.3 unimplemented and the
contradiction open.

### Alternative B — cache the catalogue entry with a TTL

**Argument for.** Maximum availability, and the direct implementation of §4.3's
intent. Serving survives an outage completely. It also buys back the isolation
ADR-019 spent, in full.

**Argument against.** **It caches the sharing scope and the started/stopped
status along with everything else.** A layer made private would stay readable
for the life of the cache, and a service an operator stopped during an incident
would keep answering — which is the one thing stopping a service is *for*. With
two servers over one store there is no local invalidation that fixes this,
because the change happens in the other process. The exposure is unbounded in
kind: whatever the most sensitive private layer is, that is what leaks.

### Alternative C — serve public-only while blind (chosen)

While the platform store is unreachable, answer from the last catalogue entry
seen, **but only for services whose remembered sharing was `Public`**. Refuse
everything else with a 503 that says why.

**Argument for.** It buys back availability for exactly the data where a stale
grant costs nothing that was not already given away. Public means *anonymous may
read this*; being wrong about that for fifteen minutes discloses nothing new.
Private and organisation data is never served on a remembered permission. And it
resolves §4.3 honestly rather than by ignoring it: authorization does freeze, and
it fails closed for precisely the case §4.3 did not consider.

**Argument against.** It is a third behaviour to understand, it is the only one
of the three that needs new code on the authorization path, and it helps least
in the deployment most people will run — see §3.

### Alternative D — freeze the whole context, per ADR-007 §4.3 as written

**Argument for.** It is already a decision; implementing it would close a
condition rather than open an ADR.

**Argument against.** §4.3 assumed one server. Implementing it unchanged across
two would mean each server honouring its own idea of who may read, diverging
silently, and no operator action able to correct either. This ADR amends §4.3
rather than executing it.

## 3. Counterarguments to the preferred option

**The strongest one: in the baseline deployment this buys almost nothing.**
`CLAUDE.md` §6's baseline is `graticula → PostgreSQL/PostGIS`, one database, and
[Q-69](../open-questions.md) made the datastore mandatory. In that shape the
platform store and the hosted data are the same instance: when it goes, the data
goes with it, and there is nothing to serve. **Measured, not assumed** — pausing
the container made a public layer's document 503 too, because building it needs
the data source (§4).

Where it helps is narrower and real: **registered layers**, whose data is in the
customer's own database; deployments that put the platform store on a separate
instance; and failures that are not the database dying — a failover, a restart,
a connection storm, a pool exhausted by something else. Those are the majority of
real outages by count, and they are the ones where stopping is most obviously
wrong.

**The second: a public service stopped mid-incident keeps answering.** The status
is remembered along with the scope, so a service stopped *before* the outage
stays stopped — but one stopped *during* it keeps serving until the window
expires. This is a genuine hole in the chosen option, it is not fixable without
reaching the store, and it is bounded to public services and to the window.
Recorded rather than mitigated.

**The third: fifteen minutes is a guess.** §5 says so. It is a judgement about
operator response time, which this project has no data on.

## 4. Evidence

Executed 2026-08-15 against a running server. The platform store was reached
through a TCP forwarder on port 55433 so that it could be killed **on its own**,
leaving PostGIS answering on 55432 — because the baseline single-instance shape
cannot exercise the case at all (§3), and a test that cannot fail proves nothing.

| Claim | Evidence |
|---|---|
| A public service is served while the store is dead | `GET /rest/services/buildings/FeatureServer` → **200**, header `x-catalog-age: 5` |
| **And so is a real query, with real rows** | `.../FeatureServer/0/query?where=1=1` → **200**, 1 feature returned, from a datastore the server could no longer look up |
| A non-public service is refused | `GET /rest/services/editable/FeatureServer` as an administrator → **503**, *"answers only services that were public the last time it could read the catalogue"* |
| A never-seen service is 503, not 404 | *"no record of a service named 'nosuchthing' from before it went quiet… which is why this is not a 404"* |
| The load balancer is told | `/healthz/ready` → **503** throughout |
| The baseline shape gains nothing | With the whole container paused, the public document 503s as well — the data source is the same instance |
| A bug in our own SQL is not a degraded mode | `A_server_side_error_is_not_a_degraded_mode`: SQLSTATE 42703 propagates instead of falling back |
| A deleted service does not return during the next outage | `A_service_that_was_deleted_stays_deleted` |

**15 unit tests** on the policy and the failure classification, plus **2 new
round-trip tests** on the defect below. Suite: **804 passing**.

**A shipped authorization defect, found by this work.** The end-to-end test kept
serving a service it had just been told to make private. It was not the outage
path: `PUT /admin/layers/{name}/sharing` wrote `layer.sharing`, and since
migration 11 the serving path reads the **owning service's** scope, because a
service holding three layers with three scopes cannot answer *who may see this
service*. So an administrator making a layer private received `200` and
`{"from":"public","to":"private"}`, a column changed, and the layer stayed
readable by anybody.

**Nothing caught it because both halves were tested and the join was not.** The
write side asserted the column changed; the read side asserted the service scope
was honoured. The bug lived exactly in the gap. It is fixed, and the gap now has
a test in it — written with the admin catalogue, read with the serving
catalogue, verified by reverting the fix and watching three tests fail.

## 5. Decision

**The serving path resolves services through `CatalogFallback`, which remembers
the last answer the catalogue gave and uses it only when the store cannot be
reached at all.**

- **The healthy path is unchanged.** This is not a read-through cache: while the
  store answers, every request reads it. Revocation stays instant, stopping a
  service stays instant, and D-17's deliberate half stays deliberate.
- **While blind, only `Public` is served.** Any other remembered scope is
  refused with 503. A name with no remembered entry is also 503 — never 404,
  because a 404 would be a claim, and it would be wrong for every service
  published since this process last read the catalogue.
- **Only connectivity failures count as blind**: anything Npgsql raised before
  the server answered, a timeout, and the four SQLSTATEs where the server's own
  answer is *I cannot serve you* — 57P01, 57P02, 57P03, 53300. Everything else
  is a bug and propagates.
- **The memory expires after fifteen minutes**, configurable as
  `Graticula:CatalogFallbackMinutes`, **and zero disables degraded serving
  entirely** for a deployment that would rather stop.
- **Every blind response carries `X-Catalog-Age`**, in seconds, so this state is
  visible on tiles and query results and not only on the two documents with room
  for a field.
- **The catalogue also remembers its last *listing*, added 2026-08-23.** This
  decision as first written said *resolves services*, and that is all
  `CatalogFallback` could do: it answered one named service and could not
  enumerate. So the four protocol faces that begin by enumerating — WFS and WMS
  build capabilities over every published layer, OGC API Features answers
  `/collections`, the portal searches — and the REST directory at
  `/rest/services` had no degraded path at all, which is not what this ADR reads
  as promising. [D-127](../architecture-debt.md) is the row that found it, and
  its first axis is why this bullet exists.
  - **A blind listing is public-only, by the same rule and in one place.** The
    faces filter by sharing, but while blind the sharing value is itself
    remembered, so the filter is applied in the fallback where five callers
    cannot each forget it.
  - **Nothing remembered is null rather than an empty list**, and every face
    refuses 503. An empty capabilities document is the claim *this server
    publishes nothing*, and a client that believes it stops asking. A server
    that genuinely publishes nothing keeps saying so; one whose services are all
    private refuses, because it has something to publish and cannot say what.
  - **And a capabilities document needs more than the catalogue.**
    `EX_GeographicBoundingBox` is mandatory on a WMS 1.3.0 named layer, so the
    document makes one projection call per distinct spatial reference; with the
    listing remembered and the projector not, WMS still cost 4.0 s per request
    and OGC Features never finished. `IProjector` is behind the same circuit
    breaker now. Measured both ways:
    [benchmarks/catalogue-outage](../../benchmarks/catalogue-outage/RESULTS.md).

**ADR-007 §4.3 is amended, not implemented.** Its rule — freeze rather than stop
— survives for public services. Its *fails closed* guarantee is restored for
everything else by refusing rather than by freezing, which is what §4.3 would
have said had it considered two servers over one store.

## 6. Consequences

**Positive.**

- A platform-store failure stops being total. Public layers keep serving,
  including their data, from a datastore the server can no longer look up.
- The §4.3 contradiction is closed by a decision rather than left as two
  incompatible documents.
- `X-Catalog-Age` makes a degraded server visible in any log or dashboard
  without anybody having to know this decision exists.
- The failure classification is now stated in one place and tested, so
  *unreachable* stops being a judgement each call site makes.

**Negative.**

- **A public service stopped during the outage keeps answering** for up to the
  window. §3.
- **Relationships are not reported while blind.** They live only in the platform
  store and there is no remembered copy, so a layer document served blind cannot
  report them. ~~`X-Catalog-Age` is the only thing distinguishing that from a layer with
  no relationships, which is thinner than it should be — condition 3.~~ **Repaired
  2026-08-27, condition 3**: the document itself now carries `relationshipsKnown: false`
  and `catalogStale: true`, so the distinction survives being saved. `relationships` is
  still an empty array, because ArcGIS has no field for *unknown* and every client would
  read anything else as none anyway — what changed is that the document says which.
- **A third behaviour on the authorization path**, which is the path where extra
  behaviours are most expensive to reason about.
- **Almost no benefit in the baseline deployment**, and somebody will read the
  ADR and expect otherwise.
- Memory grows with the number of distinct services asked for, bounded at 4,096
  entries, at which point the whole memory is dropped rather than evicted
  cleverly.

**Ports created.** None. `CatalogFallback` wraps our own catalogue.

**State.** *Catalogue*: none — it reads the catalogue and writes nothing to it.
*Runtime*: the **remembered catalogue entries**, and they are the whole subject. Per worker
process and therefore **node-local**; bounded at 4,096 entries and dropped whole rather than
evicted cleverly; never authoritative — while blind, only a remembered `Public` scope is
honoured, because that is the one stale value being wrong about costs nothing that was not
already given away.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| A-066 | A stale `Public` grant discloses nothing that was not already disclosed | `VALIDATED` by reasoning rather than measurement, and it is close to a tautology: `Public` means anonymous may read it, and the window cannot make that more true |
| A-067 | Platform-store failures that leave the data sources up are common enough to be worth this | `UNVALIDATED`. Reasoned from failure shapes — failover, restart, pool exhaustion, separate instances, registered sources — and this project has run no deployment long enough to count them |
| A-068 | Fifteen minutes is longer than a transient outage and shorter than one nobody is handling | `UNVALIDATED`. A judgement about operator response, made configurable because the right value is a property of the deployment |

## 8. Dependencies

**Depends on**: [ADR-007](ADR-007-service-runtime.md) §4.3, which this amends;
[ADR-018](ADR-018-authorization-and-roles.md) for what `Public` means;
[ADR-019](ADR-019-portal-server-split.md) §4, whose fused tiers created the
exposure.

**Depended on by**: [D-17](../architecture-debt.md), narrowed by this;
[ADR-017](ADR-017-admin-api.md) §6's degraded surface, which is the same problem
for the admin API and is **not** solved here.

## 9. Revisit triggers

- **A deployment reports being served stale data it did not expect.** The
  policy, the window, or both are wrong.
- **The admin API gets its own degraded surface** (ADR-017 §6, A-051). The two
  degraded modes must then agree about what *blind* means, and this ADR is the
  older one.
- **Relationships or any other platform-store-only fact becomes load-bearing for
  a public layer.** Today the omission is tolerable; the moment a client breaks
  on it, condition 3 is not enough.
- **A second server is actually run.** Everything here reasons about two servers
  over one store and nothing has ever run that way.

## 10. Conditions

1. **The blind path is exercised against a genuinely unreachable store**, not a
   mocked one, and the test kills the platform store while leaving the data
   sources up — because the baseline shape cannot distinguish the two.
   *(Discharged 2026-08-15, §4, via a TCP forwarder. The forwarder is a
   scratch tool and is not in the repository, so this discharge is a record of a
   run rather than something CI repeats — see condition 4.)*
2. **Falling back never hides a defect in our own SQL.** *(Discharged —
   `A_server_side_error_is_not_a_degraded_mode`, plus
   `An_unrelated_failure_is_not_swallowed`.)*
3. **A layer document served blind says that its relationships are unknown**,
   rather than reporting none and relying on a header. Today it reports none.
   *(Discharged 2026-08-27.* `RelationshipsForAsync` now returns **null for *could not ask*
   and empty for *there are none***, which it could not distinguish before — both paths that
   fail returned `[]`, one when the catalogue was already blind and one when the relationship
   read is the request that discovers the store has gone. The layer document carries
   `relationshipsKnown: false` and `catalogStale: true` when it could not ask.
   **`relationships` keeps its empty array deliberately**: ArcGIS has no vocabulary for
   *unknown* in that field, so null or absent would be read as none by every client while
   breaking the ones that read its length — the field a client reads is left alone and the
   fields that say what it means are added beside it. **Neither field appears on an ordinary
   document**, asserted by a test that compares the two key by key, because two new keys on
   every layer document would be two more things every client discounts.
   **And the condition was hiding a second defect.** The remark on `RelationshipsForAsync`
   said the caller *is* told, *"which is what `catalogStale` on the layer document is for"* —
   **and there was no such field anywhere in the code.** A comment naming a mechanism that
   does not exist is worse than no comment, because it stops the next reader looking; the
   field now exists and the remark is rewritten to say what the method does. **Falsified** by
   returning the document unchanged: `A_layer_document_served_blind_says_its_relationships_are_unknown`
   fails and passes on restore.)*
4. **The outage test becomes something CI runs**, or this decision is verified by
   hand at each release and that is written into the release checklist. A
   behaviour that only exists during a failure is a behaviour that rots.
5. **The window's default is revisited against a real operator response time**
   before the first deployment that matters. A-068 is a guess.

## 11. Dissent

**Recorded, and it is Alternative A's.** The safest server is the one that stops.
Every argument here for serving through an outage is an argument for acting on
information nobody can currently confirm, and the fact that the information is
*"this was public"* makes the consequence small rather than the principle sound.

There is a second dissent worth keeping, aimed the other way: **Alternative B is
right that this is a half-measure.** It helps least in the deployment most people
will run, and an operator whose private layers went dark during an outage while
the public ones stayed up may reasonably ask what the point was. The answer is
that the alternative was serving those private layers on a permission nobody
could check — but "we did the small safe thing" is not the same as "we solved
it", and nothing here should be read as claiming otherwise.
