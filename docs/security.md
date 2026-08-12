# Security

**Status:** PARTIAL — the authorization model is written because it was blocking
(debt D-01 and D-02). Everything else in §54 is still outstanding; see §6.
**Required by:** §54, §41, §42
**Resolves:** D-01, D-02

---

## 1. Why this document starts here

Fresh-challenger review G5 found two problems that block implementation rather
than merely needing attention:

- **D-01.** Delegating authorization to row-level security depends on the
  database session's identity. Pooling connections per data source removes it.
  Two decided ADRs are incompatible as written and neither mentions the other.
- **D-02.** The cache key is plan identity plus schema fingerprint, with no
  principal. Two users with different rights can produce the same plan against
  the same layer, so a cache hit crosses an authorization boundary.

Both are resolved below. The rest of §54's threat list is not yet written.

## 2. The authorization model

### 2.0 Two axes: roles and ownership

**Added 2026-08-12.** The model below was written as pure RBAC - permissions
assigned by an administrator. Self-service publishing adds a second axis that
composes with it rather than replacing it.

| Axis | Question it answers | Assigned by |
|---|---|---|
| **Role** | What may this person *do*? Publish hosted content, register a data source, administer the platform. | An administrator |
| **Ownership and sharing** | Who may see *this item*? Its owner decides: private, a group, the organisation, public. | **The item's creator** |

A publisher creates a hosted layer and owns it. They choose its sharing scope
without an administrator. An administrator may override, and must be able to,
but is not in the path.

This is the ArcGIS model and both open-source alternatives lack it. It means
authorization is not a single administrator-assigned matrix, and the design in
§2.1 onward must accommodate an owner-set scope alongside an admin-set role.

**Not yet designed**, and it must be before publishing ships: how an owner-set
sharing scope and an admin-set role compose when they disagree. The safe default
is that the more restrictive wins, but "safe" and "expected" are not the same
thing, and an owner who shares publicly and finds it invisible will file a bug.

### 2.1 Our authorization is the baseline, not a fallback

This resolves D-01, and the resolution turns out to be a reframing rather than a
trade-off.

**We were going to build our own authorization regardless.** Three reasons make
it unavoidable:

- **File providers have no row-level security.** COG, GeoParquet, FlatGeobuf.
- **Not every database will grant us role-switching.** A registered Oracle with
  `SELECT` and nothing else cannot support delegation.
- **Row-level security is per row. Per-layer authorization is a different
  question** and the database has no concept of our layers.

So delegating to RLS was never going to replace our authorization. It was only
ever going to *supplement* it for row-level rules a customer had already
defined.

**The model:**

| Layer | Who enforces | Always available |
|---|---|---|
| Service and layer visibility | **Us** | Yes |
| Field visibility | **Us** | Yes |
| Row filtering — our rules | **Us**, compiled into the query plan | Yes |
| Row filtering — the customer's existing RLS | **The database**, if delegation is available | **No — a capability** |

Assessment §8's takeaway from the thin-server study — *defer to row-level
security where the provider supports it, rather than layering a second model
that can disagree* — survives, but as **an opt-in capability rather than an
architectural assumption.**

### 2.2 RLS delegation is a provider capability

It joins the capability model [ADR-008](adr/ADR-008-query-engine.md) already
defines, alongside "can you clip" and "can you write":

> **Can I assume a principal's identity for the duration of a statement?**

Available where the provider supports transaction-scoped identity switching and
we have been granted it. `VERIFY` the mechanisms differ per engine —
PostgreSQL `SET LOCAL ROLE`, SQL Server `EXECUTE AS`, Oracle proxy
authentication with a VPD context — and each must be checked before it is
claimed.

Where the capability is absent, the layer simply does not offer customer-RLS
delegation. Our own authorization still applies. This is the same
"never degrade silently" discipline applied to security: the capability report
says whether delegation is active, and it is never quietly skipped.

### 2.3 The safety rule that must not be broken

> **Identity switching is transaction-scoped, always.**

`SET LOCAL ROLE` inside an explicit transaction, so the identity cannot outlive
the commit and reach the next user of a pooled connection. A session-scoped
`SET ROLE` on a pooled connection is a privilege escalation waiting for a
missed reset — and the failure is silent, intermittent and severe.

**Consequences that must be designed for, not discovered:**

- **A delegated query runs inside an explicit transaction.** Non-delegated
  queries need not.
- **The reset is guaranteed by the transaction boundary rather than by
  application code.** No `finally` block is trusted with this.
- **A connection whose reset cannot be verified is destroyed, not returned to
  the pool.** Cheaper than the alternative.

### 2.4 The tension this creates with streaming — recorded, not solved

An explicit transaction holds `ACCESS SHARE` on the table for its duration. That
is exactly what [ADR-007](adr/ADR-007-service-runtime.md) §4.8 and §5b say must
not be held long, because it blocks the DBA's DDL.

**Streaming a million features inside a delegated transaction holds that lock
for the whole stream.**

So RLS delegation and large streaming reads are in genuine tension. Three
partial mitigations, none free:

1. **Statement timeouts bound it**, which is mandatory anyway — but a timeout
   long enough to stream a large result is long enough to be a problem.
2. **Delegated layers may carry a lower maximum feature count**, making the
   tension a documented limit rather than a surprise.
3. **Delegation is per layer, not global**, so the layers that need it are
   usually the sensitive ones, which are usually not the bulk-export ones.

**Recorded as A-036.** If it turns out that the layers wanting RLS are also the
layers wanting bulk export, this needs a better answer than three mitigations.

### 2.5 Pools do not fragment

The original fear behind D-01 was per-principal connection pools, which would
multiply the connection budget by the user count and destroy
[ADR-007](adr/ADR-007-service-runtime.md) §4.8.

**Transaction-scoped identity switching avoids it entirely.** One pool per
(worker, data source), as decided. Identity is set and released within the
transaction, so any connection can serve any principal.

The cost is one extra statement per delegated request, pipelined with the query
rather than a separate round trip. That is a real cost and it is bounded, unlike
pool fragmentation which is not.

## 3. Caching and authorization

Resolves D-02.

### 3.1 Two kinds of authorization, two placements

The naive fix — put the principal in the cache key — is correct and
catastrophic: every user gets their own tile, and the cache stops working.

The right split:

| Authorization | Where it acts | Effect on the cache |
|---|---|---|
| **Uniform across authorized users** — layer visibility, deny/allow | **Before the cache lookup** | None. All authorized users share one entry. |
| **Varies between users** — row filters, field visibility | **Part of the cache key** | Users with identical effective rights share; others do not. |

So the check happens *before* the lookup for the common case, and the key only
grows where the result genuinely differs.

### 3.2 The key includes a grant fingerprint, not a principal

Where authorization varies, the key carries a **hash of the effective
authorization that affects the result** — the row filter expression, the visible
field set, and whether RLS delegation is active.

Two users with identical rights produce the same fingerprint and share the
entry. A user with different rights cannot reach the first user's bytes.

This preserves cache sharing where it is safe and prevents it exactly where it
is not, which is what the naive fix fails to do.

### 3.3 The rule

> **A cache entry may be shared by any two requests that would produce
> byte-identical output under their own authorization.** If that cannot be
> proven from the key, it is not shared.

For tiles on a layer with no row-level rules — the overwhelmingly common case —
the fingerprint is constant and the cache behaves exactly as
[ADR-010](adr/ADR-010-caching.md) designed it.

### 3.4 Invalidation

A permission change is already a **wrong**-class invalidation
([ADR-010](adr/ADR-010-caching.md) §5.1), purged rather than aged out. The grant
fingerprint makes that structural: changing the effective grant changes the key,
so old entries become unreachable rather than needing to be found.

The multi-node invalidation window (ADR-010 §7) still applies and is now clearly
a **disclosure window** rather than a freshness one. It needs a number, and that
number is a security parameter.

## 4. What this changes elsewhere

- **[ADR-007](adr/ADR-007-service-runtime.md) §4.8** — the conflict noted there
  is resolved. Pools stay per data source. Delegated queries run in explicit
  transactions, which interacts with the DDL discipline (§2.4).
- **[ADR-008](adr/ADR-008-query-engine.md)** — the capability model gains
  "can I assume a principal's identity", alongside its query and write
  dimensions.
- **[ADR-010](adr/ADR-010-caching.md)** — the cache key gains a grant
  fingerprint, and authorization splits into pre-lookup and in-key.
- **Assessment §8** — RLS delegation is demoted from a takeaway to a capability.
  Our own authorization was always going to exist.

## 5. Disclosure surfaces

Debt D-03, from fresh-challenger review G7, and it belongs here.

Capability reports and detailed refusals name the provider and the unsupported
operation. That is good for usability and it tells any client what database
engine sits behind a layer, and by implication its version and the
organisation's internal topology.

**Rule: detail is authorization-scoped.** An authenticated administrator sees
the provider and the reason. An anonymous client sees the capability in abstract
terms and a generic refusal. Cheap now, awkward once clients depend on the
detailed form.

## 6. Not yet written

§54's list, minus what is above. Named so the gap is visible:

SQL injection (mitigated in [ADR-008](adr/ADR-008-query-engine.md) §4.6 but not
reviewed as a whole) · SSRF, which matters more than usual because
[ADR-009](adr/ADR-009-raster-engine.md) proxies range requests to URLs ·
path traversal · authentication itself (§41) — local accounts, JWT, OAuth 2.0,
OIDC · privilege escalation paths · malicious geometry beyond
[ADR-008](adr/ADR-008-query-engine.md)'s filter bounds · decompression bombs
(registration-time only so far) · denial of service and the interaction with
[ADR-007](adr/ADR-007-service-runtime.md)'s backpressure · secret handling
beyond "encrypted at rest" · dependency vulnerability process ·
**multi-tenant resource isolation**, which G5 raised and this document does not
address: one tenant's expensive query still degrades another's, and §49's limits
are per service rather than per tenant.

**The whole-system security review required by §66 has not been run.** These are
per-decision mitigations, not a reviewed composition.
