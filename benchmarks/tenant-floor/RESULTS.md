# ADR-204 condition 1 — what a row-level tenant floor costs, and whether the pool leaks it

**Run 2026-10-19** on this machine, PostgreSQL 16 + PostGIS 3.6 (the portable cluster on port 55499), one
caller, `Program.cs` is the harness. Disposable, like everything under `/benchmarks`.

[ADR-204](../../docs/adr/ADR-204-one-deployment-holds-several-organisations.md) condition 1:

> Prototype the floor on three tables — `service`, `item`, `sharing_group` — and measure it. The p95 of
> `/sharing/rest/search`, `/rest/services` and a FeatureServer `query` with and without the policies … and a
> test that interleaves two tenants' requests on a pool of one connection and asserts neither ever reads the
> other's row.

## What was built

- **The real schema**: a fresh database owned by `tf_app` — *not* a superuser — and the shipped migrations
  (schema 94) applied through the product's own `SchemaMigrator`.
- **The scale target**: 1,000 services in ten tenants (the default holds 550, nine JVs 50 each), three layers
  and a group layer each, 200 members, 53 groups. Three groups admit other tenants — default + jv1 + jv2,
  default + jv3, jv4 + jv5 — and each member tenant shares some of its items into them.
- **The floor on eight tables**, not three: `service`, `item`, `layer`, `group_layer`, `principal`,
  `sharing_group`, `sharing_group_member`, `item_group`. A `tenant_id` column, `ENABLE` and `FORCE ROW LEVEL
  SECURITY`, one policy each.
- **The tenant comes from the connection string** — `Options=-c graticula.tenant=jv1`, one data source per
  tenant — not from a per-request `set_config`. The stores run `_dataSource.CreateCommand(sql)`, which takes a
  pooled connection and gives it back inside the call, so there is no request-shaped connection to set
  anything on. The floor keys on the **tenant only**; who the caller is stays `LayerAccess`'s question.
  **Superseded the same day by *Connections* below**: one pool per tenant breaks the connection budget, and the
  tenant now travels with each statement over one pool. The cost and correctness results that follow are
  unaffected — they measure the policies, not where the setting comes from.

## Correctness — all passed

| Check | Result |
|---|---|
| Each of ten tenants sees exactly its own services plus those shared into a group that admits it, against the answer computed without the floor | 10 of 10; zero unreachable foreign rows; layers follow their service |
| A connection with no tenant set | reads **no** services and **no** members — fail-closed |
| Two tenants' pools, 16 callers, 4 connections each, 4,800 reads | 0 answered with the other tenant's catalogue |
| A request that sets another tenant four ways — `set_config`, `SET`, `SET` in a committed transaction, `SET` in a transaction abandoned after an error — then the next request on the same connection, pool of one, 2,000 rounds | **0 of 2,000** saw the other tenant (Npgsql's reset on close returns the session to the startup value) |
| **Control**: the same with `No Reset On Close=true` | **2,000 of 2,000** saw the other tenant — the test can see a leak, so its zero above means something |

## What the floor does not bind — the finding that matters most

| Connection | Services read with tenant `jv6` (owns 50) |
|---|---|
| **Superuser** | **1,000**, `FORCE` or not |
| The owner, without `FORCE` | 1,000 |
| The owner, with `FORCE` | 50 |

**`compose.yaml` connects the server as `gis`, the image's `POSTGRES_USER`, which is a superuser.** On every
deployment shipped today the floor would be inert. The server has to connect as a role that is not a
superuser and does not have `BYPASSRLS`, and that is a deployment change, not a code change.

## Cost — the first shape failed, the second passed

Three modes, interleaved, order rotated every round, 400 rounds after 30 warm-up, p95 in ms:
**today** (no tenant at all — every row), **C** (Alternative C: the same predicate written into the SQL, run
by a `BYPASSRLS` role), **floor** (the policies, no change to the SQL). The real `PostgresAdminCatalog.ListServicesAsync` is
the first row; the others are the base reads behind portal search, member lists and group lists.

### First shape: policies that ask each other — **2.7× and 7.7× Alternative C on `ListServicesAsync`**

`layer`'s policy asked `service`, whose policy asked `item`, whose policy asked `item_group`, whose policy
asked `sharing_group` — each under its own policy, per row — and `ListServicesAsync` reads `layer` four times a
row in correlated subqueries.

| tenant | read | today | C | floor | floor ÷ C |
|---|---|---|---|---|---|
| default | ListServicesAsync | 19.47 | 14.12 | **38.12** | **2.70** |
| default | members | 0.22 | 0.50 | 2.35 | 4.66 |
| jv1 | ListServicesAsync | 21.53 | 3.80 | **29.14** | **7.67** |
| jv1 | members | 0.24 | 0.48 | 2.17 | 4.47 |

ADR-204 §9's first trigger (more than 20 %) fired on this shape.

### Second shape: flat policies over reach tables — **at or under Alternative C**

Every policy consults only tables nothing floors: `tenant_group_reach`, `tenant_item_reach` and
`tenant_principal_reach` (tenant → group, item, member), each one primary-key probe. Three runs:

| tenant | read | rows | today | C | floor | floor ÷ C |
|---|---|---|---|---|---|---|
| default | ListServicesAsync | 1000 / 565 / 565 | 23.20 · 23.66 · 23.47 | 14.96 · 15.61 · 15.21 | **10.63 · 10.76 · 13.04** | 0.71 · 0.69 · 0.86 |
| default | items | 1000 / 565 / 565 | 0.51 · 0.51 · 0.49 | 0.72 · 0.73 · 0.70 | 0.76 · 0.74 · 0.71 | 1.05 · 1.02 · 1.02 |
| default | members | 201 / 30 / 30 | 0.18 · 0.19 · 0.19 | 0.42 · 0.44 · 0.43 | 0.30 · 0.31 · 0.30 | 0.71 · 0.70 · 0.71 |
| default | groups | 53 / 7 / 7 | 0.13 · 0.14 · 0.13 | 0.19 · 0.21 · 0.20 | 0.20 · 0.22 · 0.22 | 1.04 · 1.07 · 1.09 |
| jv1 | ListServicesAsync | 1000 / 75 / 75 | 21.05 · 22.33 · 18.72 | 4.19 · 4.01 · 3.60 | **3.54 · 3.44 · 3.53** | 0.84 · 0.86 · 0.98 |
| jv1 | items | 1000 / 75 / 75 | 0.58 · 0.51 · 0.50 | 0.58 · 0.60 · 0.56 | 0.55 · 0.54 · 0.55 | 0.94 · 0.90 · 0.97 |
| jv1 | members | 201 / 27 / 27 | 0.17 · 0.20 · 0.21 | 0.43 · 0.45 · 0.47 | 0.31 · 0.33 · 0.33 | 0.73 · 0.73 · 0.71 |
| jv1 | groups | 53 / 6 / 6 | 0.14 · 0.14 · 0.15 | 0.20 · 0.21 · 0.23 | 0.22 · 0.22 · 0.25 | 1.10 · 1.02 · 1.09 |

The worst ratio is 1.10, on a 0.2 ms read. `ListServicesAsync` under the floor is **faster than today** for
every tenant, because it returns fewer rows, and no slower than writing the predicate by hand.

**A second trap, found by the harness failing:** the reach tables were first filled as `tf_app`, after the
old policies had been dropped and with RLS still forced, and came out **empty** — every tenant lost what was
shared with it, silently. Whatever keeps them — a trigger on `sharing_group_member` and `item_group` in the
product — must run as a role the floor does not bind, or it computes reach from one tenant's rows only.

## Connections — `pools` stage, the same day, and it changed the shape

The first run left one question owed: one data source per tenant multiplies pools, and ten tenants at today's
pool of 24 is 240 connections against a stock PostgreSQL's 97 usable. Measured with the per-request catalogue
read — [`FindServiceAsync`](../../src/Graticula.Platform.Postgres/PostgresLayerCatalog.cs)'s statement, one
service by name — on ten tenants, **half the callers on one hot tenant**, 6 s per mode, two rounds, modes
interleaved. Other sessions share this cluster, so the budget is a **role connection limit of 36** rather than
`max_connections`: running out fails the same way, `53300`, without taking anyone else's connections.

### A trap before the numbers: the per-request lookup lost its index

Under a policy, `lower(s.name) = lower(@name)` is not pushed into the index, because `lower` is not leakproof.
Measured with `EXPLAIN ANALYZE` on the statement as written: an index scan without the floor, **a sequential
scan of all 1,000 services** under it. Every request every face answers runs this lookup. A stored
`lookup_key` (`coalesce(lower(folder), '') || chr(31) || lower(name)`, unique per tenant) compared with `=`
keeps the index under the floor; every floored mode below uses it, and also names the host's tenant outright
(`tenant_id = current_setting(…)`), because a reachable foreign service of the same name is a second answer.

### Modes

- **today**: one pool of 24, today's statement and index, no tenant.
- **C**: one pool of 24, the key and the tenant written into the statement, no policy.
- **a pool per tenant, 24 each**: the first run's shape, tenant in the connection string.
- **a pool per tenant, shared out**: the same, but the 24 divided — 6 for the hot tenant, 2 for each other.
- **one pool, tenant set on open**: `set_config` on a borrowed connection, then the statement — two round trips.
- **one pool, tenant batched in**: an `NpgsqlBatch` of `set_config` and the statement — one round trip.
- **plans kept**: the same with `Max Auto Prepare = 32`. The product does not switch it on.

### Correctness of the shared pool

A pool of one connection, ten tenants alternating every request, 2,000 requests, with and without plans kept:
**0 answered with another tenant's rows, and 0 requests that set no tenant read a row** — the reset on close
clears the previous request's setting, so forgetting fails closed.

### 64 callers — saturating, ms

| mode | peak conns | answered (×2 runs) | refused | p95 | hot tenant p95 |
|---|---|---|---|---|---|
| today | 24 | 188,292 · 184,987 | 0 | 2.85 · 2.77 | 2.84 · 2.76 |
| C | 24 | 172,493 · 171,107 | 0 | 3.25 · 3.30 | 3.25 · 3.28 |
| **a pool per tenant, 24 each** | **36** (the limit) | 60,216 · 62,016 | **39 · 48** | 6.41 · 6.20 | **432 · 419** |
| a pool per tenant, shared out | 24 | 108,466 · 100,797 | 0 | 7.83 · 8.23 | 9.67 · 10.44 |
| one pool, set on open | 24 | 85,446 · 69,469 | 0 | 6.36 · 9.95 | 6.33 · 9.93 |
| one pool, batched | 24 | 108,394 · 91,777 | 0 | 4.77 · 6.78 | 4.74 · 6.75 |
| C, plans kept | 24 | 414,695 · 391,472 | 0 | 1.38 · 1.44 | 1.38 · 1.44 |
| a pool per tenant shared out, plans kept | 24 | 346,093 · 327,908 | 0 | 2.39 · 2.53 | 3.24 · 3.67 |
| **one pool, batched, plans kept** | 24 | 341,196 · 306,780 | 0 | **1.70 · 2.04** | **1.70 · 2.03** |

(An earlier run of the first five modes, before the lookup key existed, is not reported: every floored mode was
scanning, and that is the trap above, not the floor.)

### 4 callers — the load this server actually sees, ms

D-30 and the comment on `PlatformStorePool` put the server's measured peak at 942 requests a second and the
catalogue's concurrent demand at about three connections. At that load the question is latency, not throughput:

| mode | p50 | p95 |
|---|---|---|
| today | 0.23 · 0.23 | 0.32 · 0.30 |
| C | 0.23 · 0.25 | 0.30 · 0.37 |
| a pool per tenant, shared out | 0.37 · 0.39 | 0.50 · 0.58 |
| one pool, set on open | 0.44 · 0.48 | 0.57 · 0.68 |
| one pool, batched | 0.41 · 0.42 | 0.62 · 0.62 |
| C, plans kept | 0.10 · 0.10 | 0.15 · 0.15 |
| a pool per tenant shared out, plans kept | 0.11 · 0.12 | 0.16 · 0.18 |
| **one pool, batched, plans kept** | **0.11 · 0.13** | **0.16 · 0.19** |

### What it says

1. **One pool per tenant is out.** At 24 each it ran into the budget, refused requests and gave the hot tenant a
   400 ms p95; shared out, it refused nothing but made the hot tenant queue behind its own small share — 3.2–3.7 ms
   at p95 with plans kept, against 1.7–2.0 over one pool.
2. **One pool, tenant batched into each statement**, is the shape: the budget stays 24 whatever the number of
   tenants, and a statement that forgets the tenant reads nothing.
3. **The floor's cost is planning.** Without kept plans the floored lookup is about twice C's (0.62 against 0.30–0.37
   at p95, 4 callers) — the policies are planned on every call. With plans kept it is 0.16–0.19 against 0.15, and
   faster than today's 0.30. Keeping plans is a product-wide change with its own question — what a migration does
   to the plans a running server holds — and ADR-204 makes it a condition rather than a line in a connection string.

## What this does not measure

- **The HTTP p95** the condition names. Measured here: the store statements underneath. The host work above
  them does not change with the floor except for having fewer rows, so the statements bound the difference —
  but the endpoints have not been timed, because the endpoints do not exist yet.
- **Every other per-request statement.** `FindServiceAsync` lost its index to a non-leakproof function and
  nothing looked wrong from outside; the others have not been looked at. ADR-204 condition 11.
- **Writes.** The reach tables' triggers and the cost of keeping them are not built.
- **A reachable item's owner.** `ListServicesAsync` left-joins `principal` for the owner's name; an item shared
  into a group by a member who is not in it would show no owner to the other tenant. The seed does not produce
  that case; the product would.
