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
  of both. *(Since 2026-10-25 a third way for the certificate: issued on demand by the proxy, asking this server
  first — §5e item 19.)* Path prefixes (`/t/jv1/studio`) need neither — and the middleware in `Program.cs:1162` is already where a
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
| ArcGIS Online gives each organisation its own host name | The short name "uniquely defines the URL to your organization", `orgurl.maps.arcgis.com` | [Configure general settings](https://doc.arcgis.com/en/arcgis-online/administer/configure-general.htm), [Activate subscription](https://doc.arcgis.com/en/arcgis-online/administer/activate-subscription.htm) |
| ArcGIS Online keeps usernames unique across all organisations | "The same username can't be used for multiple organizations"; one email may hold several accounts with different usernames. **Unlike here since §5e item 16, by owner decision** | [Esri FAQ 000033402](https://support.esri.com/en-us/knowledge-base/faq-can-the-same-arcgis-online-username-be-used-for-mul-000033402) (read through a search; the page refused a direct fetch) |
| ArcGIS Online lets a group admit members of other organisations | "Who can be in this group?": my organisation only; partnered collaboration and my organisation; any organisation's members — the last "as long as they have privileges to join external groups" | [Create groups](https://doc.arcgis.com/en/arcgis-online/share-maps/create-groups.htm), [Create a partnered collaboration](https://doc.arcgis.com/en/arcgis-online/administer/create-partnered-collaboration.htm) |
| ArcGIS Online names a service within its organisation | `portals/[portalID]/isServiceNameAvailable` — "already published to the organization" | [Check service name](https://developers.arcgis.com/rest/users-groups-and-items/check-service-name/) |
| ArcGIS Online lets an organisation define roles | "Create role"; the name "must be unique within your organization"; made by the administrator or a custom role with the Member roles privilege | [Configure member roles](https://doc.arcgis.com/en/arcgis-online/administer/configure-roles.htm), [Member roles](https://doc.arcgis.com/en/arcgis-online/administer/member-roles.htm) |

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
already follow. ~~There is no other route between tenants — no "share with tenant X" scope — so ADR-018's four scopes
stay four and the question *what can JV1 see of ours?* has one answer: list the groups that admit it.~~ **Amended
2026-10-26 by owner decision —** [ADR-220](ADR-220-an-item-may-be-shared-with-every-organisation.md)**:** a fifth level,
*all organizations on this server*, read by every organisation's signed-in members and by nobody anonymous. Still no
"share with tenant X": the answer to *what can JV1 see of ours?* is the groups that admit it and what is shared at that
level.

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

### 5e. As built, 2026-10-22 — and where it departs from §5a–§5d

Built overnight under the owner's *"açık konular biterse, multi tenancy e gir ve onu bitir"*. **Every departure below
is INFERRED** — the owner decided the requirement and the four answers of §5d, not these — and is listed for
confirmation.

**What exists.** Migration 100: a `tenant` table (the existing deployment is `default`, id
`10000000-0000-0000-0000-000000000001`, keeping its portal id); `tenant_id` on the 35 tables an organisation owns, its
default the connection's tenant, so no insert names it; `ENABLE` and `FORCE ROW LEVEL SECURITY` with one policy,
`tenant_floor`. Every Postgres store reads through `TenantDataSources`. A request's tenant is chosen by its host
(`TenantRouting`, before authentication); a member of another tenant is an outsider (`Authorization.AsOutsider`):
no privilege, no `organization` content; the machine's administration answers only the system administrator
(`TenantRouting.SystemOnly`); jobs run as their tenant; `portals/self` gives each tenant its id, name and
`multitenant`; organisation settings (basemap gallery, default basemap, second-factor requirement) are kept per tenant;
`/admin/tenants` makes, changes, removes and purges tenants — a purge dropping the tenant's hosted tables, views and recycle bin, and refused while a registered layer of it keeps a history or attachments (owner decision 2026-10-27, [D-316](../architecture-debt.md)); `/admin/groups/{name}/tenants` marks a group as admitting
other tenants, after which a member of another tenant is added by name. Measured end to end on a fixture served as a
bound role (`TenantsAreKeptApartTests`), and the conformance suite passes as before on both a bound role and a
superuser.

**Departures.**

1. **The tenant is a startup option of a pool per tenant, not a `set_config` ahead of each statement.** The stores run
   about three hundred statements as connectionless commands that no hook reaches; rewriting each is the change most
   likely to forget one, and a pool whose every connection starts with `-c graticula.tenant=…` cannot forget. §5b's
   prototype measured pools per tenant as costing the busy tenant queue time and the connection budget; each tenant's
   pool here is a share, `max(4, 24 / tenants)`, of the request pool's ceiling. **Unmeasured** — condition 14.
2. **A deployment of one tenant never routes**: the pool names no tenant and the policy reads every row for such a
   connection. The floor engages when a second tenant exists — which is what §6 promised, *nothing changes until then*.
3. **Names stay unique across the deployment** — service, folder, layer, group, data source and the rest — rather than
   per tenant. Several caches are keyed by name, and relaxing uniqueness before each is keyed by tenant would let one
   organisation's request be answered from another's cached entry. A JV cannot call a service `Parcels` while the main
   organisation has one. Condition 15. ~~Names stay unique across the deployment.~~ **Reversed the same day by owner
   decisions, in three steps:** services and folders (item 14), every other name but the datastore's (item 15), and
   usernames (item 16) are unique within their organisation.
4. ~~**Roles, role privileges and user types stay the deployment's**, and changing a role is the system administrator's
   while there are several tenants. A tenant's custom roles are condition 16.~~ **Changed 2026-10-25 (§5e item 18):**
   the built-in roles and user types stay the deployment's and nobody changes a built-in role while there are several
   organisations; a custom role is its organisation's, made and changed by its own administrator.
5. **A member is seen across tenants through a policy that asks group membership**, not through reach tables kept by
   triggers (§5b). It is the one cross-asking policy, on the member table only; its cost is unmeasured — condition 10
   is not discharged by this.
6. **The default tenant with no host names answers every host no other tenant claims** — so access by IP and an
   existing host name keep working after a second tenant is made. Give it its host names and an unknown host is `421`.
7. ~~**A token of another tenant whose member is not reachable here is refused (`498`)**, not served as anonymous~~
   **Changed 2026-10-26 by [ADR-220](ADR-220-an-item-may-be-shared-with-every-organisation.md) §5.6:** every
   organisation's member is reachable everywhere, for what is shared with every organisation, and is an outsider at
   any host but their own. What follows was the reason for the refusal: the
   session's member is not visible here, so the token is unknown here.
8. **Closed with it:** two of §1b's three bypasses — the group listing (and with it the portal's group search) shows an
   `organization` group only to its own tenant's members, and a member profile's `organization` visibility excludes an
   outsider; the third, `PostgresGroupDirectory`'s own organisation test, is that listing.
9. **Not built:** the system administrator reading inside a tenant (§5d.3); per-tenant datastore schemas (hosted tables
   share `hosted`; their names already carry a random suffix); per-tenant admission (condition 6 — the per-tenant
   pools bound connections, not ADR-046's queue); `compose.yaml`'s serving role
   (condition 9). A purge leaves a tenant's hosted tables in the datastore.

10. **The console** (built by the ux-designer agent the same night, as every screen is): Server › Organizations for
    the system administrator — list, make (the first administrator's password shown once, with its sign-in address),
    change, suspend, remove, remove with everything behind the typed key — and a way to issue that administrator a new
    password (`POST /admin/tenants/{key}/password`); in Studio's group settings the switch that admits other
    organisations and a field to add their members by username; the organisation's name in the bar and window title
    while there are several; a note for an outsider. ~~While there are several organisations the console hides Server
    from an organisation's own administrator, whose every Server screen would answer 403.~~ **Reversed the same
    morning by owner decision** — *"Kiracıların kendi yöneticileri de serverlarına girebilecek"*, *"Her tenantın kendi
    datasources i olabilir, kendi servis listesi kesin olur"*, *"Logları kendine aittir"*: an organisation's
    administrator has its own Server — its services, publishing, data sources and logs — and only Organizations,
    Site › Status, Site › General and the server's process log stay the system administrator's. Migration 101 gives
    the audit and Studio-event logs a tenant and the floor; the request log carries a tenant and is narrowed by its
    reader, because it is written with `COPY`, which PostgreSQL refuses on a table whose row-level security is forced
    — found when the floored log stopped being written without a word. A make that fails after the
    tenant was written removes it again. **INFERRED, the screen's choices:** the tab's place, *Organization ID* showing
    the portal id, removal trying the plain removal first, the switch saving on tick, and the name shown only with
    several organisations.

11. **The audit of an organisation's own administrator, 2026-10-22** — every Server and Studio screen opened as that
    administrator and as an outsider reached through a group, after item 10's reversal. What it found, and what was done:
    - *Changes aimed at something that is not the organisation's.* Three are refused by what they are aimed at rather
      than by their path (`Tenancy.SharedOrForeignTargetAsync`): a member whose home is another organisation — a password
      set here would have signed them in at home — a system service such as Geometry, which serves every organisation,
      and pausing the managed datastore, which every organisation shares. Reads are not refused. Roles are the
      deployment's (§5e), so changing one is the system administrator's; reading them is not.
    - *Who is a member.* The members list was every principal the floor let through, which included an outsider
      admitted by a group; it is now the organisation's own members only.
    - *Jobs.* `/admin/jobs` was the system administrator's alone, so an organisation's administrator could start an
      import and never read its progress. The job table is the system's queue and keeps no floor, so each read is
      narrowed to the request's tenant instead; a job is stamped with the tenant that made it, and runs as it.
    - *The console* (the ux-designer agent): the health pill, the datastore and Geometry rows, the basemap ground and
      Utility services are read-only or absent for an organisation's administrator; an outsider sees Content without
      making items, maps, folders or groups. **INFERRED, the screen's choices:** what is hidden rather than shown
      read-only, and the wording of each *whose this is* note.
    - **Not done:** an organisation's administrator has no signal of its own that one of its services is failing — the
      health pill is the server's, and is hidden from it. Recorded as a gap, not as a decision.

12. **The ArcGIS veteran's review of 2026-10-22, and the three it found would block a second organisation in
    production** — the owner: *"Öncelikle multi tenancy kısmına bir giriş yapıp oraları düzeltelim."*
    - *Every organisation is a single-tenant portal to its clients.* `portals/self` said `multitenant` beside
      `isPortal: true` once a second organisation existed — a pair no ArcGIS portal sends — and so changed what the main
      organisation's clients were told the moment a joint venture was made, against §6's own promise. Each organisation
      has its own host and is its own portal; it says `singletenant`, always.
    - *Each organisation holds a share of the datastore* (condition 6). Every organisation's hosted layers are on the
      one managed datastore, and its 24 permits were one gate for all of them. An organisation now holds at most
      `2/(n+1)` of them — two thirds with two organisations, never fewer than two (`ConnectionBudget.ShareOf`) — before
      the source's own bound; refused past it with a sentence that says it is the organisation's share. A ceiling, not
      a reservation: alone, an organisation still has two thirds. A source only one organisation uses is unchanged.
      Which source is the datastore is decided by host, port and database, not the whole string.
    - *Each organisation's files are its own folder.* The row-level floor keeps rows; it never reached files, and one
      `Graticula:GeoParquetRoot` let an organisation's administrator register the folder or `.duckdb` file another
      publishes from. With several organisations each registers from `<root>/organizations/<key>/` — a name is read
      there, a path outside it refused, `..` included — and the main organisation from the root without the others'
      folders. A server of one is as before. The folder is not made for the organisation; the system administrator
      makes it, as the root itself is.
    - *Each organisation holds a share of the pins* (ADR-218): the same `2/(n+1)` of the deployment's limit, refused
      past it with the organisation's own pinned services named.
    - ~~**Not done:** the tile cache's budget and the process's own 64 permits.~~ **Done 2026-10-24:** an organisation
      holds at most `2/(n+1)` of the process's permits across every source, its own databases included
      (`ConnectionBudget`, taken before the datastore's share), and of the tile cache's budget — each cache entry
      knows the organisation it was written for, and a write past the share evicts that organisation's tiles only,
      in the cache's own order (`FileSystemTileCache`). Tiles adopted from disk at start are nobody's until rewritten. **INFERRED** that the
      datastore was the one to do first, because it is the one every request of every organisation's hosted layers
      passes through.

13. **An allowlist beside the denylist** — the veteran's review: the system-only acts were a list of paths, so every
    administrative route added later was open to an organisation's administrator by default. `TenantRouting.
    OrganisationOwn` now names the prefixes that are an organisation's — each reaches rows the floor keeps, or reads
    nothing that is anyone's — and a route on neither list is refused to everyone but the system administrator while
    there are several organisations. `EveryAdministrativeRouteIsClassifiedTests` reads every `/admin` route the server
    maps and fails until each is on one list, with a control that an unlisted route is caught. Every route was
    classified on the day it was built; none had to move.

14. **A service's name, and a folder's, is its organisation's** — owner decision 2026-10-22: *"her bir tenant için
    servis ismi unique olmalı. farklı tenantlarda aynı servis ismi olabilir. ama birisi mesela atos.graticula.com dan
    servis edilirken, diğeri batos.graticula.com'dan servis edilecek"*, and *"servislerde tenantid tutup, o tenant'a o
    servisleri getirmek lazım"*. Migration 103 makes `service_name_in_folder_ci` and `folder_name_unique` include
    `tenant_id`. A request already reached only its organisation's services through the floor; what condition 15 asked
    first was everything that knew a service by its name *outside* the floor, and an inventory of the code found five:
    - `CatalogFallback` remembered a service by folder and name, and the listing in one slot, for every organisation —
      so during a store outage one organisation's public services were served at another's host. **That listing was a
      leak before names were per organisation.** Both are keyed by organisation now.
    - `ServiceUsageCounter` tallied by folder and name and flushed through whichever pool the flush ran on, so two
      `hosted/Parcels` were credited each other's traffic. A count carries the organisation the request was made to,
      the flush joins on it, and the store writes and reads on its own pool.
    - The catalogue manifest tool read every organisation's rows and keyed them by name; with several organisations
      it now refuses without `--organization <key>` and works on that organisation's pool.
    - `CoveragePyramids` ran outside any organisation and updated every coverage of a service's name; it updates by
      the service's id.
    - `ItemAnnotations` held every organisation's categories in one snapshot and gave the tree, its schema and a path's
      resolution to everyone — **a leak of category names before names were per organisation**; each is narrowed to
      the request's organisation, and the snapshot is loaded on the store's own pool so one organisation's refresh no
      longer replaces the others'.

    Checked and keyed by id already: the tile cache, `ServiceContexts`, initial extents, thumbnails, sprites, pins,
    portal item ids, hosted table names. Jobs resolve a name inside their organisation and check the id.

15. **Every other name is its organisation's too, but the datastore's** — owner decisions 2026-10-22: *"evet tenant
    başına tabii ki"*, and *"sistemde tek bir datastore olabilir. herkesin kendi registered database'i olabilir ama
    datastore tek."* Migration 104 adds the organisation, last, to the unique names of data sources, groups, field
    domains, relationships, identity providers and both kinds of category. A second inventory found what knew those
    names outside the floor:
    - **The managed datastore is known by its name** — the hosted-layer paths find it as `datastore`, the guards that
      refuse removing or editing it compare the name, the console labels it by it — so an organisation's own source
      called `datastore` would have been taken for the one every organisation shares, and a hosted layer bound to the
      organisation's database. The name stays the deployment's: one row may carry it in any case
      (`data_source_datastore_name_reserved`), and registering or renaming to it is refused in words.
    - Two inserts named the old index as their conflict target (`on conflict ((lower(name)))`, domains and identity
      providers) and would have failed on every create; they name the new one.
    - Sign-in reads identity providers by id, groups and domains are read inside the request's organisation, and
      layers keep their domains by id, so nothing else changed.

    **Not done:** the health page's list of sources it cannot open is keyed by name across organisations.

16. **A username is its organisation's too** — owner decision 2026-10-22, reversing §5e item 3: *"bence organizasyon
    başına olsun"*, asked after *"aynı emaille 2 tenantta olma ihtimali var mı? username'in email olduğunu
    düşünürsek"*. With usernames as email addresses, one person in two organisations has to be two accounts of one
    name. Migration 105 makes `principal_name_key` `(name, tenant_id)`.

    **The danger was not the index but everything that found an account by name.** An organisation's host sees its own
    members and the outsiders its groups admit, so after the change it can see two `ali`s; an inventory found an
    administrator's password reset, removal and second-factor reset reaching both, a sign-in landing on either, the
    own-only edit rule comparing names, the Basic credential cache and the failed-sign-in counter shared across
    organisations, and three creates naming the old index. So:

    - **One rule for what a name means, in the store:** `graticula_principal_named(name)` — a bare name is a member of
      the request's organisation (the default one where there is none), `organisation\name` a member of that one,
      found only where the floor lets this organisation see them. Every by-name statement asks it; exact case wins.
      `graticula_principal_label(name, tenant)` is its inverse, so a list shows an outsider as `organisation\name`.
    - **An outsider is named with their organisation everywhere here:** admitted to a group as `atos\ali@firma.com`
      (a bare name now finds only this organisation's member), signing in at this host by that name, and written so
      in the editor columns, the audit, request and client-event logs (`RequestPrincipal.QualifiedName`). Two `ali`s
      therefore never edit each other's rows under the own-only rule, and their log lines stay apart.
    - Failed sign-ins count per organisation (`login_attempt.tenant_id`); the Basic credential cache keys on it.
    - A backslash cannot be part of a member's name; a directory's `DOMAIN\user` arrives as `DOMAIN.user`.
    - A tenant's first administrator no longer collides with another organisation's member of the name.

    Checked live and kept by `AUsernameIsItsOrganisationsTests`: one name made in the main organisation and a JV, each
    signing in at its own host and not the other's, the JV's reset and removal leaving the main one's account, the main
    one admitted to a JV group as `default\name` beside the JV's own and signing in there by it.
    **INFERRED:** the backslash as the separator (the Windows domain form), outsiders signing in by the qualified name.

    **The screens say it — design review 2026-10-24.** Every place a name is typed to sign in (the console's card,
    the services directory's `/rest/login`, and the OAuth page ArcGIS Pro and the Python API open) carries one line
    under the name field, only while the server holds several organizations: *"From another organization? Sign in as
    `organization\name`, with your organization's key before the backslash."* A server of one shows nothing. A refused
    sign-in at such a server whose typed name has no backslash adds *"If your account is in another organization, sign
    in as organization\name."* — decided only by the deployment being multi-organisation, which `whoami` already says
    to anybody, and the caller's own input, so it is the same whether or not the name is an account anywhere. The
    group's Add member offers the qualified-name field only on a group that admits other organizations (otherwise one
    sentence leads to Settings), checks for the backslash before sending, keeps the picker open with the refusal
    beside the field, and on success says where and as what the person signs in, since this server sends no mail; a
    member row of the form `organization\name` reads *From another organization*. Kept by `OutsiderSignInTests`.
    **INFERRED**, for the owner: the wording; the hint under the field rather than above the form; the extra sentence
    on a refused bare name; the example key in the group hint (`default`, or `partner` at the main organization's own
    host); the directory page's refusal getting no extra sentence, since its form round-trip does not carry the
    typed name.

17. **ArcGIS Online, set beside this — 2026-10-25**, from the documents §4 cites, at the owner's word.
    - *The same:* an organisation per host (its short name); service names and folder names within an organisation
      (`isServiceNameAvailable` is per portal); a group that admits members of other organisations (ArcGIS Online's
      *any organization's members*).
    - *Different, by decision:* usernames are unique within an organisation here and across ArcGIS Online there
      (§5e item 16). One person in two organisations is two accounts in both products; here they may share a name.
    - *Different, not decided — owed:*
      1. **Roles per organisation.** ArcGIS Online's administrator makes custom roles, named uniquely within the
         organisation, by the *Member roles* privilege. ~~Here roles are the deployment's (condition 16). ArcGIS Online is
         the model to build it on.~~ **Built 2026-10-25 on that model — item 18.**
      2. **Who may join another organisation's group.** ArcGIS Online asks the joining member for a *join external
         groups* privilege, so their own organisation decides. Here the group's organisation admits anyone it names.
      3. **Partnered collaboration** — two organisations agreeing to work together, coordinators who invite their own
         members, and groups limited to the partners. ~~Here there is only the open switch.~~ **Built 2026-10-26 —
         [ADR-219](ADR-219-two-organisations-partner-before-their-groups-mix.md):** partnerships, each side's member
         visibility, and groups limited to partners; coordinators are not built.
      4. **Where the outsider works.** In ArcGIS Online the member keeps one account and the other organisation's group
         appears on *their* Groups page. Here their home token already reaches the other host as an outsider (tested),
         ~~but their own console does not list the other organisation's groups; they find them by signing in there.~~
         **Built 2026-10-25:** `GET /admin/groups` answers `elsewhere` — the groups of other organisations the member
         is in that still admit outsiders, with whose each is and the address to open it at — read on the root pool,
         narrowed to the member's own membership rows, and answered at home only — and the member's own Groups page
         lists them under its own groups, each opening at `/studio/group/{name}` on its organisation's host in a new
         tab (ux-designer; **INFERRED**: new tab, the title as the link). The member still
         works in the group at its organisation's host, as an outsider; ArcGIS Online lets them work from home.
         Tested by `AMembersGroupsElsewhereAreListedAtHomeTests`.
18. **An organisation's custom roles are its own — 2026-10-25**, the first of item 17's owed four, at the owner's
    *"sıra ile"*. Migration 106; built on ArcGIS Online's model rather than invented:
    - **The five built-in roles stay the deployment's** (`tenant_id` null, read by everyone), as ArcGIS Online's
      default roles are the product's. **While there are several organisations nobody changes one** — not the system
      administrator either, because a built-in role is every organisation's and a change to it changes every
      organisation's members. `PUT /admin/roles/{built-in}/privileges` answers `409` with a sentence that says to make
      a custom role instead, and the listing marks it not editable. On a server of one organisation nothing changes.
    - **A custom role is its organisation's**, floored like every other organisation table, and named uniquely within
      it — two organisations may each have a *Field editor* with different privileges. It is stored as usernames are
      (item 16): bare in the default organisation, `key\name` in any other, so the primary key and every foreign key
      that hangs from a role name keep working; `graticula_role_named` and `graticula_role_label` turn one into the
      other, and a request names and reads a role bare. A custom role may not take a built-in's name, in any case, nor
      hold a backslash (`400`). Every custom role that existed before is the default organisation's.
    - **An organisation's administrator makes, changes and deletes its own** — `/admin/roles` left the system-only
      list; the built-ins are refused by the rule above rather than by the path.
    - **The privilege cache reads every organisation's roles** on the store's own pool, keyed by stored name — read on
      a request's pool it held one organisation's, and every other organisation's members resolved to nothing until
      the next refresh.
    - **Service accounts are given roles their organisation has**, checked by the endpoint rather than left to a
      foreign key that reads past the floor.
    - **A custom role may carry any privilege in the catalogue, the administrative ones included** — found by the
      ux-designer agent's pass, which made one holding `admin:manageServer` as an organisation's administrator. It
      is not an escalation: the system's acts are refused by the member's system flag, not by a privilege
      (`TenantRouting`, §5e item 13), so such a role reaches no further than the organisation's own `administrator`
      role, which that administrator already holds. ArcGIS Online lets a custom role carry administrative
      privileges too. **INFERRED** that this stays; listed for the owner.
    - The console (ux-designer, the same day): an organisation's administrator manages its custom roles; a built-in
      role shows its privileges fixed, with *Copy as a custom role* — every built-in but `administrator`, which ArcGIS
      Online does not offer as a template either (**INFERRED**).
    - Tested by `AnOrganisationsRolesAreItsOwnTests`: one name in two organisations, each member holding its own
      organisation's privileges, a change in one leaving the other's alone, and the built-ins refused to both.
19. **A new organisation's host needs no change to the proxy — 2026-10-25**, at the owner's question *"her yeni
    wildcard açtığımda caddy file mı güncellenecek"*. A wildcard DNS record brings every name to the server, but a
    proxy terminating TLS still needed a site and a certificate per organisation, so making one in the console was
    not enough to reach it. `GET /healthz/certificate?domain=` answers `200` when an organisation lists the name and
    `404` when none does — the question a proxy issuing certificates on demand asks first (Caddy's `on_demand_tls`
    `ask`). **It is not `Resolve`**: a default organisation listing no host names answers every host, and a yes for
    every name a wildcard sends is a certificate requested for each, against the certificate authority's weekly limit
    for the whole domain. A suspended organisation's names are still ones, so it answers its `503` over TLS. Under
    `/healthz` because the proxy asks by address, which reaches no tenant. Tested by
    `ACertificateIsAskedForOnlyByANameAnOrganisationListsTests`; the Caddy configuration is in the deployment guide
    §4.1.

**State.** As §6 says, plus: the pools per tenant, node-local, emptied whenever the tenants are read again; each
member's home, read once per thirty seconds.

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
   **DISCHARGED 2026-10-22** — `EveryTableIsATenantsOrTheSystemsTests` reads `pg_class` and `pg_policies` of the migrated
   schema; the tenant purge reads the floored tables from `pg_policies` the same way.
3. **Every absolute address is checked under a second tenant's host**, the seven of §1b and the literal `/studio`
   links first; and what Pro, the JavaScript SDK and the ArcGIS API for Python do with `portalMode = "multitenant"` is
   measured rather than assumed.
4. **A token from one tenant opens another's host as an outsider and nothing more**, tested on every face that
   `LayerAccess` guards — not on the portal alone. *(Partly, 2026-10-22: the portal, FeatureServer, its query, MapServer,
   VectorTileServer and the administration API in `TenantsAreKeptApartTests`; by hand the same with warm caches both
   ways, WMS GetCapabilities (no layer of the other tenant named), the services directory, the OGC collections list and
   the portal search. A WFS GetFeature, an OGC item read and the tile endpoints themselves are not walked.)*
   **DISCHARGED 2026-10-27** — the three walked, by `AnOutsidersTokenReadsNothingOnEveryFaceTests`: a layer one
   organisation shares with its own members, read at its host by a member of the main organisation through WFS 2.0
   GetFeature, OGC API Features items and one item, and vector tile 0/0/0, gives no feature and no tile; each read is
   made first by the organisation's own administrator, who gets the feature and a non-empty tile, so the address is right
   and there was something to leak. On a fixture served as a bound role.
5. **A tenant administrator is refused every system-only act**, tested by enumerating the privileges §5d moves rather
   than by picking three. *(Partly, 2026-10-22: the acts are enumerated as paths in `TenantRouting.SystemOnly`; the test
   walks two of them.)* **DISCHARGED 2026-10-27** — `AnOrganisationsAdministratorReachesNoSystemActTests` reads every
   `Map{Method}("/admin/…")` in the host and both of `TenantRouting`'s lists from the source, and sends every route that
   is not the organisation's own — 31 with each system prefix also sent as an address, which caught that
   `/admin/logs/server` is mapped as `/admin/logs/{source}` under the organisation's prefix and was missed by reading the
   patterns alone — as an organisation's administrator at its host: all 403, beside a control read of its own members.
   **What it cannot catch:** a route moved onto `OrganisationOwn`, since it reads the lists it checks; that move is a
   decision, made where the list is.
6. **D-04 is answered before a second tenant is created on the showcase**: at minimum, each tenant has its own share of
   [ADR-046](ADR-046-admission-control-bounds-the-queue-not-the-wait.md)'s admission, so one JV's heavy query queues
   behind that JV's work and not the main organisation's. **PARTLY DISCHARGED 2026-10-22** (§5e item 12): each
   organisation holds at most `2/(n+1)` of the shared datastore's permits, tested by
   `AnOrganisationsShareOfTheDatastoreTests`. ~~The process's own budget, the tile cache and the pins are still one
   for everyone.~~ **DISCHARGED 2026-10-24:** the pins (§5e item 12), the process's whole budget and the tile cache
   are shared the same way, tested by `AnOrganisationsShareOfTheServerTests`.
7. **The three ArcGIS Online behaviours §4 states from memory are cited from public documentation**, or the sentence
   that relies on them is changed. **DISCHARGED 2026-10-25**, at the owner's word — *"Multi tenancy soruları için Agol
   dokümanlarına bakabilirsin"*: all three are cited in §4, and two more were added (service names per organisation,
   roles per organisation). One no longer describes Graticula — usernames, per organisation here since §5e item 16 —
   and §4 says so; §5e item 17 sets the rest beside ArcGIS Online.
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
   **And CI, found by v1.0.452's run (2026-10-23):** CI's server connects as a superuser, so it refuses a second
   organisation and every tenancy test — conformance and console — returns at that refusal. The release's own tenancy
   was therefore proved only on the local fixture with the bound role; ~~**in CI none of it runs**~~. **Closed the same
   day:** CI hands the store to `graticula_ci` with `tools/serving-role.sql` after the migration, grants it by default
   what `gis` makes later, and starts the server as it. The first run made seven organisations (`POST /admin/tenants`
   answered 201 seven times in its `server.log`), and conformance 875/875 and console 322/322 passed with the tenancy
   tests running rather than returning.
   **And the showcase, 2026-10-24**, as decided: a full backup, a rehearsal on a copy served by a temporary server as
   `graticula_serving` (every datastore layer answered as it does on the live showcase, no permission refused), then
   the real store handed over — `gisserver` and `hosted` the role's, the `showcase` schema granted to it — and the
   server restarted on it through the VPS's `compose.override.yaml`; `compose.yaml` itself is unchanged, as decided.
   **What remains of this condition is `compose.yaml` for everyone else**, which switches with the first second
   organisation on a deployment.
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
14. **The pools per tenant (§5e.1) are measured against the connection budget** — ten tenants, one busy — as condition
    1 measured the shapes it rejected; if they refuse or queue past the trigger of §9, the per-statement batch replaces
    them.
    **PARTLY DISCHARGED 2026-10-22**, on a fixture served as a bound role, 64 callers for 30 s, half on one tenant, a
    hosted point layer's count query per tenant. **The first shape failed:** each tenant's pool at `max(4, 24/n)` held
    83 connections and the platform store's breaker opened again and again — public reads went on answering, as
    anonymous, so the run *looked* clean and was not. **The second passes:** shares of `max(2, 24/n)`, idle connections
    let go after 10 s, and a member's home read once per 30 s: one tenant p50 51.7 / p95 78.5 ms, ten tenants p50 53.9
    / p95 81.5 ms (+4 %), the busy tenant p95 83.1 ms (+6 %), no breaker trip, the administrator still signed in. **Still
    owed:** peak connections rose from 55 to 75, and the startup check that compares the server's connections with the
    store's `max_connections` did not count the tenants' pools. *(It does since the same night: with a second tenant
    the server's ceiling reads 118 rather than 94, and against a stock PostgreSQL's 97 it says at startup that it does
    not fit — which is true, and is the configuration to change before a second organisation is served **The datastore image now starts with
    `max_connections=300`, by owner decision the same morning** — *"Postgresql in varsayılan değerini sen değiştir"*.)*
15. **Names become unique per tenant (§5b)** only after every cache keyed by a name is keyed by tenant as well, each
    shown by a test that publishes the same name in two tenants and reads each back at its own host. **PARTLY
    DISCHARGED 2026-10-22** for services and folders (§5e item 14): the five places found are keyed by organisation,
    and `AServiceNameIsItsOrganisationsTests` publishes one name in two organisations and reads each back at its own
    host. **DISCHARGED 2026-10-22** for the rest (§5e item 15): data sources, groups, domains, relationships, identity
    providers and categories are named within their organisation, tested by
    `Groups_domains_and_categories_are_named_within_an_organisation_and_the_datastore_name_is_the_servers`.
16. **A tenant may define roles of its own** without changing another tenant's — ~~today roles are the deployment's
    (§5e.4)~~. **DISCHARGED 2026-10-25 (§5e item 18):** a custom role is its organisation's, made by its own
    administrator; the built-in roles are changed by nobody while there are several organisations; tested by
    `AnOrganisationsRolesAreItsOwnTests`.

## 11. Dissent

None recorded. The case for B — total isolation by construction — was argued in §2 and lost on the owner's requirement
that tenants share, not on its merits; if that requirement is withdrawn, B is the better design.
