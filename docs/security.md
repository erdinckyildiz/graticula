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
path traversal · ~~authentication itself (§41)~~ — **written 2026-08-13, [ADR-015](adr/ADR-015-authentication.md)** · privilege escalation paths · malicious geometry beyond
[ADR-008](adr/ADR-008-query-engine.md)'s filter bounds · decompression bombs
(registration-time only so far) · denial of service and the interaction with
[ADR-007](adr/ADR-007-service-runtime.md)'s backpressure · secret handling
beyond "encrypted at rest" · dependency vulnerability process ·
**multi-tenant resource isolation**, which G5 raised and this document does not
address: one tenant's expensive query still degrades another's, and §49's limits
are per service rather than per tenant.

**The whole-system security review required by §66 has not been run.** These are
per-decision mitigations, not a reviewed composition.


---

## User-uploaded content

**Added 2026-08-13 by [ADR-013](adr/ADR-013-feature-service-data-model.md) §4d.**

Attachments are the **first surface in this product that accepts arbitrary bytes
from a user.** Nothing else designed so far does. That is a change to the threat
model rather than a feature, and this document did not cover it.

Requirements, not suggestions:

| | |
|---|---|
| **`Content-Disposition: attachment` always** | Never rendered inline, for any content type |
| **Separate origin, or a CSP that forbids execution** | An uploaded SVG or HTML served same-origin as the admin API is stored XSS against the GIS administrator — our **primary user** (Q-06a), the one with the most authority in the system |
| **Client `Content-Type` is not trusted** | Sniff it, store what we determined *alongside* what was claimed, serve ours |
| **Filenames are data, never paths** | No filename component reaches a filesystem call or a URL path segment unescaped |
| **Hard size cap and per-layer quota** | The cap follows [geometry-crs-policy.md](geometry-crs-policy.md) §6's principle: refuse *that attachment* by id, not the query that touched it |
| **No decompression on upload, with one exception that is written down** | We store bytes. Archives are not opened, inspected or expanded — decompression bombs are not our problem if we never decompress. **2026-08-14: this rule decided a product question**, and hosted-data import accepted GeoJSON and not shapefile because a shapefile is a ZIP of three to six files. **2026-08-15: the owner took the exception** ([Q-98](open-questions.md), [ADR-024](adr/ADR-024-shapefile-import.md)) rather than lose the format most of the target audience actually has. The exception lives in exactly one class, `BoundedArchive`, and nothing else in the server opens an archive. See the row below for what bounds it |
| **What the archive exception is bounded by** | Added 2026-08-15 with shapefile import. Five bounds, and they are not interchangeable. **(1) Total uncompressed 256 MB and (2) per-member 256 MB**, both enforced *while reading* — the size a ZIP declares for its members is written by whoever made the file, so it is checked for a cheap early refusal and never believed. **(3) At most 32 members.** **(4) A compression ratio above 100× is refused**: real shapefile content compresses 2–20×, and a bomb is nearer 100,000×. **(5) Only four extensions are read — `.shp`, `.dbf`, `.prj`, `.cpg` — and none is `.zip`**, which makes a nested archive impossible rather than depth-limited. Nothing is written to disk, and a member whose name contains a directory separator is refused rather than flattened: flattening makes `a/roads.shp` and `b/roads.shp` the same file, and honouring the path is how `../` escapes |
| **An encoding is asked for, never guessed** | Added 2026-08-15. A DBF has no reliable encoding declaration. Reading Windows-1254 bytes as UTF-8 does not throw and does not look broken — it produces a string, and the damage surfaces months later in somebody's map labels. When neither a `.cpg` nor the caller states one and the records hold bytes above 127, the import is refused. This is a *correctness* rule in a security table because the failure mode is the same one: silent, plausible, and discovered by the wrong person |
| **A parser is a decompressor too** | Added 2026-08-14 with hosted-data import, which parses rather than stores. A JSON document nested a thousand deep costs an attacker nothing and exhausts the stack; the reader caps depth at 32, feature count, total coordinates and distinct property names, and every cap is checked while parsing rather than after |
| **A framework limit is not a designed limit** | Added 2026-08-15 after the third instance. Kestrel's 30 MB request body cap, ASP.NET's 4 MB form value cap and its multipart buffering each fired *ahead* of a documented limit and answered 500 — telling the caller the server had failed, for a request it refused on purpose. Every surface that accepts a body must raise the framework's bound above its own and state its own in the refusal |
| **Identifiers are generated, never taken** | The same rule as *filenames are data, never paths*, in a different dialect. A hosted table's name is derived from the requested one, sanitised to `[a-z][a-z0-9_]*`, truncated and suffixed with random hex. The name the caller chose is the service's; the table it lives in is ours |

**What this section does not cover, and should before attachments ship:** virus
scanning, whether we offer it at all or declare it the operator's business, and
whether an attachment inherits its feature's row-level security or carries its
own grant. The second is not obvious — the whole point of RLS is that a user
may see some rows and not others, and an attachment id is a guessable integer.


---

## Transport security

**Added 2026-08-13 by [ADR-014](adr/ADR-014-tls-and-certificates.md), which owns
the decisions.** This section records what they mean for the security model.

**We terminate TLS ourselves by default**, HTTPS from first start with a
generated self-signed certificate. Plain HTTP requires an explicit flag and warns
at every startup.

Three items belong here rather than in the ADR:

### Forwarded headers are a trust boundary

When a reverse proxy terminates TLS, `X-Forwarded-Proto`, `-For` and `-Host`
determine the scheme in generated URLs and the client address in any policy that
uses one. **A client able to reach the server directly can forge all three.**
They are honoured only from explicitly configured trusted proxy addresses, and
the default trusted set is empty.

### Outbound fetches are SSRF, and §6 already knew

This was listed as unwritten. The COG proxy (ADR-009 §2.4) fetches URLs supplied
when a raster source is registered. An administrator — or anyone who can
register a source — can point one at `169.254.169.254`, at an internal admin
endpoint, or at loopback, and the fetch carries **the server's network
position**, which is usually far better than the attacker's. TLS does nothing
about this.

- Outbound targets are allow-listed by host or prefix.
- Link-local, loopback and private ranges are refused unless explicitly
  permitted — an on-premises MinIO on a private address is a legitimate case and
  must be configured rather than assumed.
- Cross-host redirects outside the allow-list are not followed.

### Revocation soft-fails offline, and that is a real weakening

OCSP and CRL endpoints are unreachable in an air-gapped install. Hard-fail would
make such an install validate nothing successfully, so the default is soft-fail.
**This is a genuine reduction in the guarantee, not a technicality**, and it
belongs in deployment documentation where an operator will see it rather than
here where they will not.


---

## Authentication

**Decided 2026-08-13 in [ADR-015](adr/ADR-015-authentication.md), which owns it.**
Three things belong in this document rather than in the ADR, because they are
security positions rather than design choices.

### Anonymous is a principal, not the absence of one

Open data portals are a normal deployment of a GIS server. Modelling anonymous
access as *no identity* scatters `if (user == null)` through every authorization
check, which is where bugs live. Anonymous gets a name, can hold roles, and can
be granted a layer. Refusing anonymous access is then configuration rather than
a special case.

### ArcGIS compatibility puts credentials in URLs, and we accepted it

Q-17 requires unmodified ArcGIS clients to work, and those clients send their
token as a `token=` query parameter. **A credential in a URL leaks by design** —
into our access logs, into every proxy and load balancer in front of us, into
browser history, and into `Referer` headers sent to third parties.

We cannot refuse it without discarding the compatibility we chose. Four
mitigations bound it, all required rather than advisable: query strings are
**redacted in the logging code path** on those routes; the header form is
preferred and advertised; compatibility tokens are short-lived and **cannot
reach the admin API**; and they are revocable and listed like any other session.

**This is a deliberate weakening of the posture in exchange for the migration
path.** Recorded here rather than in a footnote, because if never-degrade-
silently applies to capabilities it applies to security trade-offs.

### An outage narrows what is served, it does not widen it — added 2026-08-15

[ADR-026](adr/ADR-026-serving-through-a-platform-store-outage.md) answers Q-95:
when the platform store is unreachable, the server keeps answering from the last
catalogue entry it saw — **but only for services whose remembered sharing was
`Public`.** Everything else is refused with a 503.

**The rule, stated so it cannot drift:** a remembered authorization decision may
only ever be acted on when acting on it wrongly discloses nothing. `Public` is
the only scope that qualifies, because it means *anonymous may read this* and a
stale window cannot make that more true.

Three properties hold this up, and each is a place a future change could quietly
remove it:

- **The healthy path is unchanged.** While the store answers, every request
  reads it. This is not a read-through cache, and turning it into one — for
  performance nobody has asked for — would end instant revocation.
- **Only connectivity failures degrade.** A defect in our own SQL propagates as
  an error. If falling back ever became *catch everything*, a typo would switch
  the server into serving remembered authorization and it would look designed.
- **A name with no remembered entry is 503, never 404.** A 404 would be a claim
  about a catalogue nobody can read.

**What this does not fix, stated rather than left to be found:** a *public*
service stopped by an operator during the outage keeps answering until the
memory expires. Stopping a private one takes effect, because private is not
served while blind at all.

**And the defect this found, which was worse than the thing it was testing
for.** `PUT /admin/layers/{name}/sharing` wrote `layer.sharing`; the serving path
reads the owning service's scope. An administrator making a layer private got
`200`, a column changed, and the layer stayed readable by anybody. Both halves
had tests — the write asserted the column changed, the read asserted the service
scope was honoured — and the bug lived in the join between them. **A test on
each side of a boundary is not a test of the boundary**, and on an authorization
path that gap is the whole vulnerability.

### A browser holds a read-only credential — added 2026-08-15

The REST Services Directory ([ADR-023](adr/ADR-023-rest-services-directory.md))
created a client class that cannot hold a bearer header. Until 2026-08-15 that
meant every browser was permanently anonymous, and every organisation-shared
service was invisible in the surface built for browsing.

**A browser now signs in and gets `gis-session`, and that cookie authenticates
`GET` and `HEAD` and nothing else.**

Three flags and one rule, and the rule is the important part:

- `HttpOnly` — script cannot read it, which matters because this directory
  renders user-supplied layer names and that is where an XSS would come from.
- `Secure` — never on plaintext.
- `SameSite=Strict` — another origin cannot cause the browser to send it.
- **Method restriction** — `Authentication.CookieToken` returns null for any
  method that is not `GET` or `HEAD`.

The first three are the conventional answer and the fourth is why this is safe
to have. A cookie is attached by the browser whatever caused the request; that
is the whole of cross-site request forgery, and `SameSite` is one flag honoured
by a browser, and browsers have had bugs. Because the credential does not work
for the methods that matter, a forged request can only read — and there is no
antiforgery token to get wrong, because there is no token.

**What it costs.** An HTML form cannot `POST` to this server on a cookie. Any
write surface in a browser needs a deliberate design rather than a `<form>` tag.
GeometryServer's operation pages are the first thing to meet this and the reason
they use `GET`: every operation there is a pure function, so the safe method is
also the honest one.

**Two rules this adds:**

- **A sign-in page's `return` parameter is filtered, always.** Only a path
  beginning with a single slash is honoured. An open redirect on a credential
  prompt is a phishing primitive — our link, our prompt, somebody else's landing
  page — and `//host` is the case that catches people, because it passes a
  "starts with a slash" check and is a protocol-relative URL.
- **A credential channel is a client class.** The bearer-only rule was complete
  until a surface arrived whose clients cannot use it. Adding a client class
  without asking what credential it can carry is how a security surface acquires
  a hole that reads as a missing feature.

### Publishing data and publishing code are different grants

Q-75 asked who may publish a Python geoprocessing tool. Partly answered: **not
the publisher role, by default.** A publisher uploading a shapefile and a
publisher uploading executable code that runs on our server are not the same
risk, and a permission that covers both is wrong in one direction or the other.
Code publication defaults to administrators.
