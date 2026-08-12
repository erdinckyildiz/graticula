# Runtime Schema Evolution

**Status:** FIRST PASS — written 2026-08-12 from an owner question.
**Question:** ArcGIS lets an administrator add and remove fields, and change
field properties, on a running service from its own UI. How?
**Feeds:** publishing (§38), [ADR-007](../adr/ADR-007-service-runtime.md),
[ADR-010](../adr/ADR-010-caching.md),
[ADR-008](../adr/ADR-008-query-engine.md), Q-31, Q-32.
**Clean room:** publicly documented REST API behaviour only (§5).

---

## 1. The short answer

**Because it is their database.** The capability exists only for *hosted*
layers, which live in a datastore ArcGIS owns and has DDL rights on.

This is the clearest evidence yet for the hosted/registered distinction in
[hosted-datastore-and-tiles.md](hosted-datastore-and-tiles.md) §4. Runtime schema
editing is not a clever feature — it is a direct consequence of owning the
store, and it is unavailable on registered data for exactly the reason our
analysis predicted.

## 2. What the documented API actually does

`VERIFY` against current documentation before relying on details.

- **`addToDefinition`** adds fields, indexes (including full-text search
  indexes on string fields) and subtypes.
- **`updateDefinition`** modifies existing properties, with the documented
  caveat that "not all of these properties may be updated".
- **Restricted to hosted services.** The documentation states it "supports
  adding a definition property in a **hosted** feature service layer".
- **Synchronous or asynchronous.** An async submission returns a `statusURL` for
  polling.

That last point is the most informative. **They built an async path because DDL
can be slow**, which means they hit the problem in production. We will too.

## 3. The structural idea worth taking

**The service definition is authoritative and separate from the physical table.**

The layer's fields, types, aliases, domains and subtypes belong to the service
definition. The table is the materialisation. One administrative operation
changes both, transactionally where possible.

This is a small idea with large consequences, and it is one we should adopt:

- The definition can describe things the table does not — computed fields,
  aliases, hidden columns, per-role field visibility.
- The definition can be validated before any DDL runs.
- Rollback has something to roll back *to*.
- Publishing (§38) becomes one mechanism rather than two: a schema change is a
  small republish.

## 4. The hard part is not the DDL

`ALTER TABLE ADD COLUMN` is easy. What follows is not, and this is where the
work actually lives:

| After a schema change | Why it matters |
|---|---|
| **Refresh every worker holding warm state for the service** | Otherwise a worker keeps serving the old field list. Concrete instance of §22's "configuration change" recycling trigger, and it interacts directly with the warm-state and affinity-routing design in [runtime-models-compared.md](runtime-models-compared.md) §3. |
| **Invalidate caches** | Removing a field makes every cached feature response and every tile carrying that attribute wrong. Adding one makes them merely stale. Different severities, and the cache must know the difference. |
| **Client compatibility** | Adding a field is backward compatible. Removing one breaks consumers. Narrowing a field can break writers. The administrator must be told which they are doing. |
| **In-flight requests** | A request that started before the change and finishes after it must produce a coherent response, not a half-migrated one. |
| **Rollback** | DDL succeeded, cache invalidation failed — now what? Publishing already needs rollback (§38); this extends it to schema. |

## 5. Where the three dialects diverge

**Which DDL is cheap and which rewrites the table differs per engine**, so the
set of schema changes we can offer safely is dialect-dependent.

`VERIFY` all of this before designing the feature:

- Adding a nullable column is metadata-only on PostgreSQL. Behaviour on SQL
  Server and Oracle differs, particularly with defaults.
- Changing a column's type generally rewrites, and may be impossible without
  data loss. ArcGIS declines to offer type changes; that is probably the right
  call and it is probably why.
- Widening a `varchar` is usually cheap; narrowing is not, and may fail on
  existing data.
- Lock behaviour under load differs sharply. A change that is instant on an idle
  table can block a busy one for a long time on the wrong engine.

**Design consequence.** Offer a *classification*, not a list of operations:

| Class | Example | Behaviour |
|---|---|---|
| Safe and instant | add nullable field, add index concurrently | Apply synchronously |
| Slow but safe | widen a field, build a large index | Apply asynchronously with progress, as ArcGIS does |
| Destructive | drop a field | Require explicit confirmation; state what breaks |
| Not offered | change field type | Direct the administrator to republish |

The classification is per dialect. The API surface stays the same; what falls
into each class does not.

## 6. On the Portal claim

The owner noted that ArcGIS Portal also uses the datastore for its own
information. `VERIFY` — Portal for ArcGIS appears to keep its own content store
separate from the relational data store used for hosted layer data, but this was
not confirmed and should not be relied on.

It does not change the conclusion either way. The principle holds regardless:
**you can evolve a schema where you have write permission, and not where you do
not.**

## 7. Consequences for us

1. **Runtime schema evolution is a hosted-only capability.** Confirms Q-31's
   answer and sharpens Q-32 — this is a concrete, demonstrable thing an
   administrator gets from the datastore, and a good argument for including it.
2. **Service definition separate from physical schema** should be adopted, and
   it belongs in the data model (§37, §38) rather than being invented later.
3. **A definition change is a recycling and cache-invalidation event.**
   [ADR-007](../adr/ADR-007-service-runtime.md) and
   [ADR-010](../adr/ADR-010-caching.md) both need to name it explicitly.
4. **The schema-change pipeline is publishing with a smaller blast radius:**
   validate, apply DDL, update definition, invalidate cache, refresh workers,
   roll back on failure. One mechanism, not two.
5. **Async with progress is required, not optional.** ArcGIS built it because
   DDL is slow; we should assume the same rather than rediscover it.

## 8. New questions

| # | Question |
|---|---|
| Q-35 | Which schema changes do we offer, per dialect, under the §5 classification? Needs the DDL cost and locking behaviour of all three engines verified first. |
| Q-36 | Can the service definition describe fields the physical table does not have — computed, aliased, role-hidden? Cheap to allow now, expensive to retrofit. |
| Q-37 | What is the contract for a request in flight across a schema change? |

## Sources

- [Add To Definition (Feature Layer) — ArcGIS REST API, Enterprise](https://developers.arcgis.com/rest/services-reference/enterprise/add-to-definition-feature-layer/)
- [Update Definition (Feature Layer) — ArcGIS REST API](https://developers.arcgis.com/rest/services-reference/online/update-definition-feature-layer/)
