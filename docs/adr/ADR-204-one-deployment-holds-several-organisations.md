# ADR-204 — One deployment holds several organisations, each at its own host name

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` *(was `REQUIRES PROTOTYPE` until 2026-10-19, when condition 1 ran)* |
| **Confidence** | `MEDIUM` for the shape · `MEDIUM` for the floor of §5b since 2026-10-19, measured at or under Alternative C's cost on the store statements — [benchmarks/tenant-floor](../../benchmarks/tenant-floor/RESULTS.md) · `MEDIUM` for the connection count since the same day: one shared pool, measured |
| **Decided** | 2026-10-19, the requirement and the four answers of [Q-181](../open-questions.md) by owner decision — *"evet"*. The shape of §5a–§5c is ours, and is not accepted until condition 1 runs |
| **Supersedes** | — |
| **Superseded by** | — |
| **Amends** | [ADR-193](ADR-193-backup-and-restore-are-the-server-s-own-commands.md): restore may take a maintenance connection (condition 9) |
| **Fires** | [D-04](../architecture-debt.md)'s trigger, *"before multi-tenant deployments are supported"* — condition 6 |

---

## 1. Context

### 1a. What the owner said

Verbatim, 2026-10-19:

> *"arcgis portal'ın en büyük eksikliklerinden bir tanesi de multi tenancy. belki ben jv lerin ana portal'e dahil
> olmasını istemiyorum. multi tenancy eklenebilir mi?"*

Asked two questions — does a joint venture share anything with the main organisation, and who runs a tenant — they
answered *"1.3 2. Biz. Tek admin."*: there is sharing between them (option 3 of three offered: full multi-organisation,
rather than a separate deployment per JV or an *external member* type inside one organisation), and one administrator
runs everything. **Minutes later they changed the second answer**, and both are kept here because the change is part
of the requirement:

> *"Kiracıların da kendi yneticileri olabilir. Bir de tane de tüm sistemi yöneten adam yapalım. Olmaz mı?"*

So: **a tenant may have its own administrators, and one person runs the whole system above them.**

**Settled by them:** several organisations in one deployment; sharing between them exists; a tenant administrator
exists; a system administrator above all tenants exists.

~~**`INFERRED`, listed for confirmation in [Q-181](../open-questions.md):** that this enters scope now rather than being
recorded for later — the question was *"can it be added?"*, and answering two design questions is not the same as
amending [v1-scope.md](../v1-scope.md); and the three answers in §5d that the owner was offered as recommendations and
has not yet confirmed.~~ **Confirmed by the owner 2026-10-19 — *"evet"* to all four.** It is in
[v1-scope.md](../v1-scope.md), and the three answers of §5d are theirs.

### 1b. What is true today, measured on `origin/main` at `7c43b890`

The premise — that a JV's members cannot be kept out of the main organisation — holds here exactly as it holds in
Portal, and for the same reason:

- **`organization` means *any authenticated principal*.** [ADR-018](ADR-018-authorization-and-roles.md) §3b, and in
  code `LayerAccess.cs:100`: `if (scope == SharingScope.Organization && !caller.IsAnonymous)`. An account for a JV
  member reads everything shared with the organisation.
- **No table carries an organisation.** The platform store has **55 live tables** (57 created, two dropped by V87);
  none has a tenant or organisation column. Classified: **22 owned content** (item, service, layer, web_map, document,
  folder, coverage, the tile and export tables, recycle_entry/row …), **20 identity** (principal, session, api_key,
  role, sharing_group and its members, external_identity, oauth_code …), **5 organisation settings** (server_setting,
  item_category, member_category, identity_provider, oauth_app) and **8 system** (platform_schema, audit_event,
  request_log, client_event, service_usage, job, setup_token, system_service). `server_setting` is a key/value table
  that **mixes both kinds**: `basemap_gallery`, `default_basemap` and `secondFactor.required` are an organisation's,
  `page_size`, `style_origins` and the backup schedule are the system's.
- **Names are unique server-wide in eleven places**: principal name, group name, service folder, service within
  folder, layer name, data source, field domain, identity provider, item and member categories, coverage path. Two
  tenants each publishing `Parcels` collide today.
- **The read rule is in one place and is asked from 38.** `LayerAccess.Evaluate` has **38 call sites in 21 files**,
  `MayManage` 22, `MayEdit`/`EditRightOf` 6. **No store filters by sharing in SQL** — `ListServicesAsync`,
  `maps.ListAsync`, `documents.ListAsync`, `coverages.ListAsync` return everything and the host filters in memory.
  Three organisation tests bypass `LayerAccess` (`PostgresGroupDirectory.cs:70`, `PortalEndpoints.cs:1125`,
  `AdminEndpoints.MemberProfiles.cs:72`), and **at least eight endpoints enumerate members or groups server-wide** —
  `/admin/members`, `/admin/groups/{name}/candidates` (every enabled member, to any group manager), member profiles,
  item and member categories with server-wide counts, the basemap gallery's group lists, and the portal's group
  search.
- **The organisation id is already derived from the address.** `PortalId` is the first sixteen bytes of SHA-256 of
  `scheme://host + PathBase` (`PortalEndpoints.cs:2800`), stored nowhere. `portalMode` is the constant
  `"singletenant"`. A tenant at its own origin therefore gets its own organisation id **without a line of new code**,
  which is the strongest single fact behind §5a.
- **Seven absolute addresses are pinned to the host root**, ignoring the path base: `/rest/info`'s
  `tokenServicesUrl` (`Program.cs:2291`), `portals/{id}/servers` (`PortalEndpoints.cs:962`), Pro publishing's
  `serviceurl` (`PortalEndpoints.ProPublish.cs:463, 614`, deliberately), the pro-admin `owningSystemUrl`
  (`PortalEndpoints.ProCapture.cs:108`), the token-exchange origin check (`AuthEndpoints.cs:1054`) and
  `OgcFeaturesEndpoints.cs:84`; and relative `/studio/...` links are written literally in at least six files.
- **One schema per process.** One `NpgsqlDataSource` from `Graticula__PlatformStore`, schema chosen by `SearchPath`;
  no request selects a data source.
- **`administrator` passes every check by name.** `Authorization.Allows => IsAdministrator || …`
  (`Privilege.cs:871`), and `IsAdministrator` is the role string equalling `"administrator"`. There is no difference
  between running an organisation and running the server.

## 2. Alternatives considered

### Alternative A — a deployment per tenant

**Argument for.** It costs nothing to build: the schema is already a deployment's choice, a second process on another
port with `SearchPath=jv1` is a JV. Isolation is total — memory, pool, schema, logs — and D-04 is answered by the
operating system. It is what Esri tells an Enterprise customer who needs separation.

**Argument against.** **Both of the owner's answers rule it out.** Nothing crosses between deployments, so *sharing
between them* does not exist; and there is no system above them, so *one person who runs everything* is a person with
N consoles, N upgrades and N backups. It remains the answer for a tenant that must be physically separate (§9).

### Alternative B — a schema per tenant inside one process

**Argument for.** Isolation by construction: a forgotten filter cannot leak a row that is not in the schema the
connection points at. The 38 call sites and the eight enumerating endpoints are safe without being touched. Deleting a
tenant is `DROP SCHEMA`.

**Argument against.** It makes hard exactly the thing the owner asked for. **Sharing between tenants** means a request
in one schema reading another's rows — through a second connection, or a cross-schema query that has to know every
table — and that is the isolation argument given back. Usernames, sessions and tokens must resolve before a tenant is
known, so identity lives outside the tenant schemas anyway and the "one search path" story is already two. Every
migration runs N times and a half-migrated deployment is a new failure state; [ADR-203](ADR-203-a-deleted-item-waits-in-the-recycle-bin.md)'s
foreign-key walk and every architecture test that reads the catalogue gain a loop.

### Alternative C — a tenant column, filtered by the application

**Argument for.** It is the obvious change and the cheapest to start: add `tenant_id`, add `and tenant_id = @t` and
`caller.Tenant == row.Tenant`. Sharing between tenants is one more clause in the same place the scope is decided.

**Argument against.** **This repository has already rejected this shape once, on this evidence.**
[Q-180](../open-questions.md) turned down a `deleted_at` column because *"the `service` table is read by 47
statements … one forgotten `where` away from serving a deleted service"*. Here it is 38 + 22 + 6 call sites, three
bypasses, eight enumerations and stores that do not filter in SQL at all — and the failure is not a deleted service
reappearing but **one company's data shown to another**. A filter every reader must remember is a leak waiting for
the first reader who does not.

### Alternative D — a tenant column, a row-level floor in the database, and the application's rule above it *(preferred)*

**Argument for.** It keeps C's single schema — so sharing between tenants is a row, not a second connection — and
takes B's property where it matters: PostgreSQL's row-level security refuses another tenant's row **whatever the
query says**, so the reader who forgets is safe. `LayerAccess` keeps deciding *private / group / organization /
public* exactly as now, over rows the floor has already narrowed to those the request may possibly see.

**Argument against.** Section 3.

## 3. Counterarguments to the preferred option

- **Row-level security has a cost nobody here has measured**, on a product whose list endpoints already read whole
  tables into memory. A policy with an `exists` against group membership is evaluated per row. That is why the status
  is `REQUIRES PROTOTYPE` and not `ACCEPTED`.
- **The floor depends on a per-request setting reaching every connection**, and the pool is where that goes wrong: a
  connection returned with tenant A's setting and handed to tenant B's request is a leak *produced by* the safety
  mechanism. Npgsql resets session state on return by default; whether every path in this code — the jobs pool, the
  datastore connection with `SearchPath` cleared, `LISTEN graticula_grants` — goes through a reset is unmeasured.
- **A policy is silently bypassed by the table's owner** unless the table is `FORCE ROW LEVEL SECURITY`, and the server
  very likely connects as the owner — it runs the migrations. A floor that does not apply to the one role that uses it
  is a floor in the documentation only.
- **Host names cost the operator something.** A tenant per host needs DNS and a certificate per tenant, or a wildcard
  of both. Path prefixes (`/t/jv1/studio`) need neither — and the middleware in `Program.cs:1162` is already where a
  prefix would be stripped.
- **Most deployments have one organisation**, and §60 says not to make small deployments pay for large ones. Every
  query now carries a predicate that, in the common case, is always true.
- **One data source per tenant multiplies connections.** Ten tenants at today's pool of 24 is 240, against
  PostgreSQL's default `max_connections` of 100. *(Measured the same day and true: refusals and a 400 ms p95 for
  the busy tenant. Answered by one shared pool, §5b.)*
- **The floor is paid for in planning, on every statement.** The product does not keep plans, so every statement is
  planned with its policies on every call: a lookup that took 0.30 ms at p95 takes 0.48–0.62 ms under the floor at
  realistic load. With plans kept the floor costs 0.16–0.19 ms against 0.15 for the same predicate by hand — and
  both beat today's unkept 0.30. Condition 12.

**Measured 2026-10-19 ([benchmarks/tenant-floor](../../benchmarks/tenant-floor/RESULTS.md)):** the first three are answered — the cost is at or under Alternative C's in the
flat form, the pool's reset holds against a control that shows the leak, and `FORCE` binds the owner. **The third
was worse than written**: the role that runs the migrations is not merely the owner on a shipped deployment, it is a
superuser, and nothing binds that.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| A JV member reads everything shared with the organisation today | `organization` ⇒ `!caller.IsAnonymous` | `LayerAccess.cs:100`, [ADR-018](ADR-018-authorization-and-roles.md) §3b |
| An application-only filter has 66 places to be forgotten, plus 3 bypasses and 8 enumerations | Counted 2026-10-19 on `7c43b890` | §1b |
| The organisation id already follows the origin | `PortalId = SHA-256(Origin)[..16]`, not stored | `PortalEndpoints.cs:2800` |
| Host-root-pinned addresses are correct under a host per tenant and wrong under a path prefix | Seven sites read `Request.Host` and drop `PathBase` | §1b |
| Row-level security costs little enough on these list reads | **Measured 2026-10-19, and only in its second shape.** Policies that ask each other cost 2.7× and 7.7× Alternative C at p95 on `ListServicesAsync`; flat policies over reach tables cost 0.69–0.98× of it, and the worst ratio on any read is 1.10, on 0.2 ms. 1,000 services, ten tenants, three runs | [benchmarks/tenant-floor](../../benchmarks/tenant-floor/RESULTS.md) |
| A pooled connection never carries one request's tenant into another | **Measured 2026-10-19:** 0 of 2,000 with Npgsql's reset on close, against **2,000 of 2,000** with it off — the control — and 0 of 4,800 reads crossing between two tenants' pools under 16 callers | [benchmarks/tenant-floor](../../benchmarks/tenant-floor/RESULTS.md) |
| The floor binds the role the server connects as | **False on every shipped deployment**: `compose.yaml` connects as `gis`, the image's superuser, and a superuser read 1,000 services as `jv6`, which owns 50, with `FORCE` set | [benchmarks/tenant-floor](../../benchmarks/tenant-floor/RESULTS.md), condition 9 |
| ArcGIS Online gives each organisation its own host name, keeps usernames unique across all organisations, and lets a group admit members of other organisations | **From memory, not yet cited** — §5 of CLAUDE.md makes a public page the citation | Condition 7 |

## 5. Decision

**One deployment holds several tenants. A tenant is an organisation: its own members, groups, content, roles,
categories, identity providers and administrators, reached at its own host name. Every tenant-owned row carries
`tenant_id`, and PostgreSQL row-level security refuses rows of another tenant unless a group that admits other tenants
links them to the caller. The existing deployment becomes the `default` tenant and nothing about it changes. Above all
tenants is a system administrator, who is not a member of any of them.**

### 5a. A tenant is addressed by host name

`jv1.example.com/studio` is JV1's portal; `example.com/studio` is the default tenant's. Pro, the JavaScript SDK and the
ArcGIS API for Python see a separate portal because they are connected to a separate origin, and `PortalId` follows
without change. **Host, not path**, because seven addresses already ignore the path base and are therefore *correct* per
host and *wrong* per prefix — and an address that resolves to the wrong tenant still answers, quietly.

- **Unknown host, more than one tenant: `421 Misdirected Request`**, never the default tenant. With only the default
  tenant, every host is it — which is today's behaviour, and keeps access by IP and local development working.
- **A token carries its home tenant.** The host chooses *whose catalogue you are looking at*; the token says *who you
  are*. A default-tenant member who opens `jv1.example.com` is a signed-in outsider there: they read JV1's `public`
  content and whatever JV1 shared into a group they belong to, and nothing shared with JV1's organisation.
- `portalMode` becomes `"multitenant"` only when a second tenant exists. **What ArcGIS clients do with that value is
  unmeasured** and is part of condition 3.

### 5b. The floor is row-level security; the rule is still `LayerAccess`

- `tenant_id` on the 22 content, 20 identity and 5 settings tables of §1b; none on the 8 system tables, which gain a
  nullable `tenant_id` where a row is about one (`audit_event`, `request_log`, `job`).
- ~~Each request sets `graticula.tenant` and `graticula.principal` before its first statement~~ — **corrected 2026-10-19
  by the prototype: there is no request-shaped connection to set it on.** The stores run
  `_dataSource.CreateCommand(sql)`, which borrows a pooled connection inside the call. ~~**The tenant is in the
  connection string** — `Options=-c graticula.tenant=<key>`, one data source per tenant, chosen by the host — and
  the pool's reset on close returns every connection to it~~ — **corrected again the same day, by the second half
  of the prototype: one pool per tenant breaks the connection budget.** At 24 each the role ran out and refused
  39–74 requests with `53300`, and the busy tenant's p95 reached 375–432 ms; at 24 *shared out* nothing was refused
  but the busy tenant queued behind its own share. **There is one pool, as today, and every statement carries its
  tenant in the same round trip** — a batch whose first command is `set_config('graticula.tenant', …)` — so the
  budget is 24 whatever the number of tenants, and a statement that forgets to carry it **reads nothing**: the pool's
  reset on close clears what the previous request set (0 of 2,000 alternating requests, [benchmarks/tenant-floor](../../benchmarks/tenant-floor/RESULTS.md)).
- **The floor keys on the tenant, never on the caller.** Every tenant table is `ENABLE` **and** `FORCE ROW LEVEL
  SECURITY`, with one policy: *rows of this tenant*, or *rows a group that admits this tenant reaches*. Who the caller
  is stays `LayerAccess`'s question.
- **Policies consult only reach tables, never each other.** `tenant_group_reach`, `tenant_item_reach` and
  `tenant_principal_reach` say which groups, items and members a tenant is reached by; each policy is one
  primary-key probe into one of them. The first prototype let policies ask each other — layer asked service, which
  asked item, which asked its groups — and cost **2.7× to 7.7×** Alternative C; the flat form costs at or under it.
  **The reach tables are kept by triggers that run as a role the floor does not bind**: filled by the floored owner,
  they came out empty and every tenant silently lost what was shared with it.
- **A per-request lookup compares a stored key, not a function of a column.** Under a policy PostgreSQL may not
  push `lower(s.name) = lower(@name)` into an index — `lower` is not leakproof — and `FindServiceAsync`, which runs
  on every request every face answers, became a scan of every service. A stored `lookup_key` compared with `=`
  keeps the index. The lookup also names the host's tenant outright, because a reachable foreign service of the
  same name would otherwise be a second answer.
- **What is looked up by a secret and never listed stays outside the floor** — `session`, `api_key`,
  `local_credential`, `principal_second_factor`, `login_attempt`, `oauth_code`, `oauth_refresh`,
  `saml_assertion_used` — so a token from another tenant resolves; the principal it names is then visible at this
  host only if a group that admits this tenant reaches them, and otherwise the caller is anonymous here, which reads
  exactly what a signed-in outsider would: `public`. Condition 2's test holds that list, and fails on a listing
  query against any of them.
- **The server connects as a role that is neither a superuser nor `BYPASSRLS`.** A superuser is never bound, `FORCE`
  or not, and the role `compose.yaml` uses today is one (condition 9).
- **`LayerAccess` gains one fact:** `organization` means *signed in **and** of the row's tenant*. Every other rule —
  the monotone ordering of [ADR-018](ADR-018-authorization-and-roles.md) §3b-iii, additive sharing of
  [ADR-185](ADR-185-sharing-is-additive.md) — stands unchanged.
- The eleven server-wide unique names become unique per tenant, **except the principal's name, which stays unique
  across the deployment**, so a username names one person wherever it is typed and a sign-in does not need to know its
  tenant first.
- Hosted tables live in a datastore schema per tenant — `hosted` for the default, `hosted_<key>` for the others —
  because `layer.name` becomes a table name and two tenants' `Parcels` cannot share one.

### 5c. Sharing between tenants is a group that admits other tenants

A group belongs to one tenant and may be marked as **admitting members of other tenants**; only that tenant's
administrator can set the mark. Content shared into it reaches its members wherever they come from, by the rule groups
already follow. There is no other route between tenants — no "share with tenant X" scope — so ADR-018's four scopes
stay four and the question *what can JV1 see of ours?* has one answer: list the groups that admit it.

### 5d. Two administrators

- **Tenant administrator** — today's `administrator` role, bounded by its tenant. It manages members, roles, groups,
  categories, sharing and content inside it. **`IsAdministrator` stops meaning "passes every check"**: it passes every
  check *about its tenant*.
- **System administrator** — a principal flag, not a role in any tenant. It creates, suspends and deletes tenants and
  assigns their first administrator; and it alone holds what is about the machine: server settings, certificates, the
  datastore, workers and jobs, logs, backups, migrations — the **Server** surface of
  [ADR-034](ADR-034-server-and-studio.md), which becomes system-only. `server_setting` splits along the same line.

**Decided by the owner 2026-10-19 — [Q-181](../open-questions.md), *"evet"*; offered as recommendations, and the
wording below is still ours:**

1. **A tenant administrator may register its own PostGIS database**, readable by its tenant only; the managed datastore
   stays the system's.
2. **Identity providers are per tenant**, so a JV signs in with its own company's directory rather than holding
   accounts in the main one.
3. **The system administrator can read inside any tenant, and every such read is audited and visible to that tenant's
   administrator** — the `AdministrativeOverride` audit that `admin:viewAllContent` already writes, carried across the
   tenant line.

## 6. Consequences

**Positive.**

- A JV is kept out of the main organisation without being kept out of the product, and what it shares is a list of
  groups anybody can read.
- A forgotten filter stops being a leak; it becomes, at worst, a read of the caller's own tenant.
- An existing deployment migrates into one tenant and behaves exactly as before; no ArcGIS client sees a change until a
  second tenant exists.

**Negative.**

- **The largest single migration this store has had**: 47 tables gain a column, eleven unique constraints are
  rewritten, every tenant table gains a policy. It is expand-only — the column defaults to the default tenant — but it
  touches everything [ADR-203](ADR-203-a-deleted-item-waits-in-the-recycle-bin.md)'s recycle bin walks.
- Every catalogue connection must carry a request context, including the jobs pool and background work, which today
  do not know about requests at all.
- An operator who wants a second tenant needs DNS and a certificate for it.
- The `administrator` role changes meaning. Anything that relied on "administrator passes everything" for a
  machine-level act will refuse a tenant administrator, which is intended and will surprise somebody.
- D-04 stops being parked (condition 6).

**State.** In the catalogue: a `tenant` table (key, host names, status), `tenant_id` on the tables of §5b, the three
reach tables, the system-administrator flag on `principal`, and the system half of `server_setting`. At runtime: the
request's tenant, sent with each statement and cleared from the connection when it goes back to the one pool, and a
host-to-tenant map each node reads from the
catalogue and refreshes on `graticula_grants` — node-local, shared through the catalogue, never authoritative on its
own.

**Ports created.** None; no Tier 2 dependency is adopted.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| A-086 | A deployment holds tens of tenants, not thousands | `UNVALIDATED` — new 2026-10-19 with this ADR |

## 8. Dependencies

**Depends on:** [ADR-018](ADR-018-authorization-and-roles.md) (sharing scopes), [ADR-034](ADR-034-server-and-studio.md)
(the Server/Studio line becomes the system/tenant line), [ADR-036](ADR-036-groups.md) and
[ADR-185](ADR-185-sharing-is-additive.md) (groups carry sharing between tenants), [ADR-046](ADR-046-admission-control-bounds-the-queue-not-the-wait.md)
(admission, for D-04), [ADR-191](ADR-191-arcgis-pro-publishes-hosted-feature-layers.md) (Pro's `/studio` portal path, now per host),
[ADR-203](ADR-203-a-deleted-item-waits-in-the-recycle-bin.md) (the recycle bin walks rows that now carry a tenant).

**Depended on by:** none yet.

## 9. Revisit triggers

- *(Fired 2026-10-19 on the first prototype, whose policies asked each other — 2.7× and 7.7×; answered by the flat
  form of §5b rather than by moving the floor, and the trigger stands for the flat form.)* **Condition 1 measures the
  floor at more than 20 % on the p95 of `/sharing/rest/search` or a FeatureServer `query`
  against the same catalogue without it** — the floor moves into the stores' SQL and Alternative C's risk is taken
  with an architecture test instead.
- **A tenant must be physically separate** — its own machine, its own database, a regulator's requirement: that tenant
  is Alternative A, and this ADR is not the place for it.
- **A-086 fails** — a deployment with hundreds of tenants changes what a per-tenant schema for hosted data and a
  per-tenant identity provider list cost.

## 10. Conditions

1. **Prototype the floor on three tables — `service`, `item`, `sharing_group` — and measure it.** The p95 of
   `/sharing/rest/search`, `/rest/services` and a FeatureServer `query` with and without the policies, on the showcase's
   catalogue; and a test that interleaves two tenants' requests on a pool of one connection and asserts neither ever
   reads the other's row. Until this runs, the status stays `REQUIRES PROTOTYPE`. **PARTLY DISCHARGED 2026-10-19** —
   [benchmarks/tenant-floor](../../benchmarks/tenant-floor/RESULTS.md): on eight tables rather than three, at the scale target, through the real `ListServicesAsync`; the flat
   floor at or under Alternative C at p95, and 0 leaks of 2,000 with a control that shows 2,000. **What is still
   owed:** the HTTP p95 — the store statements were measured, the endpoints do not exist yet — and **the connection
   count** of one pool per tenant, against [connection-budget](../../benchmarks/connection-budget). The status stays
   `REQUIRES PROTOTYPE` until the second is measured, because it can change the shape: a single pool with the tenant
   set per command is the alternative if it fails. **DISCHARGED 2026-10-19 for the prototype; the HTTP half moves to
   condition 13.** The connection count was measured and did change the shape: one pool per tenant was refused by
   the budget, and §5b now sends the tenant with each statement over the one pool — 64 callers, ten tenants, half
   on one: no refusals, 24 connections, the busy tenant's p95 1.70–2.04 ms with plans kept against 3.24–3.67 for
   pools shared out. [benchmarks/tenant-floor](../../benchmarks/tenant-floor/RESULTS.md).
2. **No list of tenant tables is kept anywhere.** An architecture test reads `pg_class` for every table in the platform
   schema and fails on one that has neither `tenant_id` with a forced policy nor a place on the system list of §1b —
   so the table a later migration adds is covered without anybody remembering it, as ADR-203 does for the recycle bin.
3. **Every absolute address is checked under a second tenant's host**, the seven of §1b and the literal `/studio`
   links first; and what Pro, the JavaScript SDK and the ArcGIS API for Python do with `portalMode = "multitenant"` is
   measured rather than assumed.
4. **A token from one tenant opens another's host as an outsider and nothing more**, tested on every face that
   `LayerAccess` guards — not on the portal alone.
5. **A tenant administrator is refused every system-only act**, tested by enumerating the privileges §5d moves rather
   than by picking three.
6. **D-04 is answered before a second tenant is created on the showcase**: at minimum, each tenant has its own share of
   [ADR-046](ADR-046-admission-control-bounds-the-queue-not-the-wait.md)'s admission, so one JV's heavy query queues
   behind that JV's work and not the main organisation's.
7. **The three ArcGIS Online behaviours §4 states from memory are cited from public documentation**, or the sentence
   that relies on them is changed.
8. **[v1-scope.md](../v1-scope.md) is amended and [Q-181](../open-questions.md) is answered** before a line of the
   migration is written. **DISCHARGED 2026-10-19** — the owner answered *"evet"* to all four, and v1-scope §2 has
   the row.
9. **The server connects as a role that is neither a superuser nor `BYPASSRLS`**, in `compose.yaml`, the deployment
   guide and the showcase, and a startup check refuses to serve more than one tenant over a connection whose role
   `rolsuper` or `rolbypassrls` says the floor does not bind it. Found by the prototype: the shipped role is a
   superuser, and the floor is inert under it. **PARTLY DISCHARGED 2026-10-19** — [benchmarks/tenant-floor/ROLES.md](../../benchmarks/tenant-floor/ROLES.md). **The product
   works as such a role**: Conformance 853 passed and the same 10 failed, built as one or as a superuser; the
   Postgres suite differs only by five tests that make roles of their own. **Restore did not**, and does now through
   an optional `Graticula:MaintenanceStore` (ADR-193, amended). **An existing deployment is handed over** by
   `tools/serving-role.sql` — the server's schemas only, not `REASSIGN OWNED` — measured the same way, 853 and the
   same 10. **The server warns at startup** (event 1094) when its role is not bound. **Still open:** `compose.yaml`
   still connects as the superuser, because switching it stops every existing volume that has no serving role;
   and the refusal to serve a second tenant waits for there to be one. **Decided by the owner 2026-10-19** — *"2 si
   de tamam"*, to the two recommendations put to them: **`compose.yaml` switches with the first second tenant**, not
   before, because until then the floor protects nothing and the switch costs every existing deployment an upgrade
   step; and **the showcase is handed over**, backup and a rehearsal on a copy first. The showcase's own inventory
   adds one thing the fixture had not: a datastore layer reads the `showcase` schema, which the server did not make,
   so the handover grants the serving role that schema as well.
10. **The reach tables are kept by triggers that run as a role the floor does not bind**, and tested by sharing
    across tenants as a member who belongs to only one of them — the case that left them empty in the prototype.
11. **Every statement that runs per request keeps its index under the floor**, held by a test that reads each one's
    plan as a floored role over a catalogue at the scale target and fails on a sequential scan of a floored table.
    `FindServiceAsync` failed exactly that way in the prototype, and nothing about it looked wrong from outside.
12. **Plans are kept — Npgsql's automatic preparation on the platform pool — or the planning cost of the floor is
    accepted by name.** Measured: kept plans make the floored lookup faster than today's. Not measured: what a
    migration does to plans a running server holds, which is the reason this is a condition and not a line in
    the connection string.
13. **The HTTP p95 of `/sharing/rest/search`, `/rest/services` and a FeatureServer `query` is measured on the built
    floor against the same build without a second tenant**, and §9's 20 % trigger is read from that rather than
    from the store statements condition 1 measured.

## 11. Dissent

None recorded. The case for B — total isolation by construction — was argued in §2 and lost on the owner's requirement
that tenants share, not on its merits; if that requirement is withdrawn, B is the better design.
