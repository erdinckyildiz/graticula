# ADR-218 — A pinned service is kept warm

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `HIGH` for what goes cold, which is measured · `MEDIUM` for the limit, which is not |
| **Decided** | 2026-10-22, by owner decision — *"İyi de belki ben hep sıcak tutmak istiyorum belirli servisleri"*, then *"evet"* to the plan below. The limit's default, who may pin, and the screen are INFERRED (§5). |
| **Supersedes** | — |
| **Superseded by** | — |

---

## 1. Context

The owner asked whether Graticula has ArcGIS's minimum and maximum instances, and shared and dedicated instances. It has
neither: one process serves every service, and a service is a catalogue row and a remembered table shape (ADR-007 §3).
Measured the same day on the local fixture, 1,000 services cost the process about 30 MB, and 64 clients spread over all
of them were served without a failure.

The owner then said they may still want certain services always warm. ADR-007 §4.12 designed exactly that — *pin the
context, not a process* — and it was deferred on 2026-10-16 with the auto-pinning beside it. This is the explicit pin,
built alone.

**What goes cold here, measured before anything was built** (`hosted/ci_many`, 50 rows a query, this PC):

| State | First request | Why |
|---|---|---|
| Warm | 3.5 ms | — |
| After 35 s idle | about 10 ms more | the table's shape expired (`ServiceContexts.Lifetime`, 30 s) and is described again |
| After 5½ minutes idle | about 70 ms more | the data source's pool closed its idle connection; one is opened again |
| First request after a restart | 342 ms, then 18, then 5 | the process's own warm-up, paid by whoever asks first |

An ArcGIS service with a minimum of zero instances pays seconds for a new process. The cost here is two orders smaller,
which is why this is a flag and not a process.

## 2. Alternatives considered

- **A — Renew a pinned service's shape before it expires, on its own pool, and read one feature at start.** Covers all
  three rows above. *Preferred.*
- **B — Lengthen `ServiceContexts.Lifetime` for pinned services.** Removes the 10 ms only; the pool still closes its
  connection and a restart is still cold. And a longer lifetime is a longer window for a changed table to be served
  with its old columns, which is what the lifetime bounds.
- **C — A minimum pool size on the data source.** Keeps a connection and nothing else, for every service on that source
  rather than for the pinned one.
- **D — ArcGIS's model: dedicated workers per pinned service.** ADR-007 §3's arithmetic; nothing measured needs it.

## 3. Counterarguments to the preferred option

- **"Nobody will feel 10 or 70 ms."** Probably true for a person, and the owner asked anyway; a service under an
  agreement that measures its first response is the case ADR-007 §4.12 was written for.
- **"Everything will be pinned."** ADR-007's own warning. The limit (§5) refuses the pin past it and names the pinned
  services, rather than dropping one silently.
- **"PostgreSQL's own cache is the bigger cold."** For a large table read from disk, yes, and a pin does not keep a
  table in PostgreSQL's memory; it keeps the description and a connection, and one read at start. Recorded as a limit,
  not solved here.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| The three costs above | measured before building, idle 35 s / 330 s and a restart | §1 |
| A pin removes the idle costs | 330 s idle: ~70 ms → within noise, behind a control request | condition 1 |
| A restart's first request is mostly not the service's | ~240 ms for any first request; 98 pinned vs 163 unpinned after it | condition 1 |
| One process serves 1,000 services in ~30 MB | 9 → 1,000 hosted services, working set 250 → 280 MB | §1, 2026-10-22 |

## 5. Decision

1. **`service.pinned`**, migration 102, false for every existing service.
2. **`PinnedServices`**, a background service: every 20 s — two thirds of the shape's lifetime — each layer of a pinned
   service is described again on its own pool and replaces its entry (`ServiceContexts.RenewAsync`), so no request
   meets an expired shape and the pool's connection is never idle long enough to be closed. The first time a layer is
   warmed in a process one feature is read. A pin wakes it at once.
3. **`GET` and `PUT /admin/services/{name}/pinned?folder=`**, `{"pinned": true}`. Reading is for whoever manages the
   service; changing is `admin:manageServer`'s, because a pin spends the server's capacity. **INFERRED.**
4. **A limit, `Graticula:PinnedServiceLimit`, default 50**, across the deployment and every organisation on it, because
   one process warms them all. Past it the pin is refused with 409 and the list of pinned services. **INFERRED**, the
   number above all.
5. **The screen** (the ux-designer agent, 2026-10-22): a *Kept warm* section at the top of Server › service ›
   Parameters, where Server Manager keeps minimum instances; a quiet *Kept warm* badge beside the status in the head
   card, so the state shows on every tab; a button that acts at once rather than with the page's Save, because the
   server may refuse it; "Kept warm" rather than "pinned" on the screen, the API and setting keeping their names. No
   control in Studio. **INFERRED, all of it.**
6. **A refusal names only the caller's own organisation's pinned services** and counts the rest — found by the
   screen's review: the first build listed every pinned service on the deployment, so one organisation's
   administrator refused a pin was shown another's service names (ADR-204). Checked live with the limit set to one.

Not done: ArcGIS's `minInstancesPerNode` is not mapped to a pin; no ArcGIS administration endpoint Graticula answers
carries it.

## Conditions

1. **The measurement of §1 repeated with the service pinned** shows the idle and restart costs gone. **DISCHARGED
   2026-10-22, with what remains named.** Each idle case was measured with a control first — `/rest/info`, which
   touches no service, so the client's new TLS connection and the machine waking are counted there and not against
   the service. *After 330 s idle:* control 30.6 ms, then the pinned query 11.8 ms against a warm 4.9 — the ~70 ms the
   pool's reconnect cost before is gone. *After 35 s:* within this PC's noise. *After a restart* (5 s after start,
   two runs each): the first request of any kind pays **~240 ms whatever it is**, the process's own warm-up, which no
   pin can own; after it the first query is **98 ms pinned against 163 unpinned**, and what is left is the query
   path's own first run in the process, paid once by the first query to any service. A self-request at start would
   remove both and is not built. *Found while measuring:* Npgsql prunes a pool by counting idle connections at
   samples, not by when each was last used, so the renewal does not keep the connection from being closed — it
   reopens it within one interval, which is why a request rarely meets the gap.
2. **A conformance test** pins a service, sees it warmed within seconds and renewed within thirty, and unpins it. **DISCHARGED 2026-10-22** — `APinnedServiceIsKeptWarmTests`.
3. **Each node warms its own** (ADR-012): nothing here is shared between nodes, and with two nodes each renews its own
   entries. Recorded so the clustering phases do not mistake it for a singleton — **a proxy that may send a request to
   any node needs every node warm**, so warming per node is the point rather than waste. Four things are owed before
   two nodes, and each is a phase 0 item of ADR-012 rather than work here: a pin announced to every node rather than
   only the one reached (item 2); the connections pins keep counted per node (item 3); a node not ready until its pinned
   services are warm, after a request to itself (item 4); and *when last warmed* kept per node, so the screen can say on
   how many nodes it is (item 5). *(Added 2026-10-22 at the owner's request, after the question whether pinning still
   makes sense in a cluster.)*

## 6. Consequences

**Positive.** A pinned service answers its first request as fast as its hundredth, after idle and after a restart.

**Negative.** Each pinned layer is described every 20 s — two catalogue round trips — and the warmer lists the
catalogue each round while anything is pinned. At the default limit that is a few queries a second.

**State.** The pin is in the store; when each service was last warmed is this process's memory, per node.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| A-025 | A small number of pinned services is enough, and administrators will not pin everything | UNVALIDATED (ADR-007) |

## 8. Dependencies

**Depends on:** ADR-007 §4.12, ADR-012 (per node), ADR-204 (the limit spans organisations)

## 9. Revisit triggers

- An administrator meets the limit: the number, not the mechanism, is what to revisit.
- A cold read from PostgreSQL's own storage turns out to be what a pinned service still pays.

## 10. Dissent

None recorded.
