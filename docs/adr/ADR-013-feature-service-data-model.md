# ADR-013 — Feature Service Data Model: Identity, Relationships and Attachments

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` |
| **Decided** | 2026-08-13 |
| **Answers** | Q-57, Q-58a, Q-58b |

---

## 1. Context

[Q-17](../open-questions.md) committed us to full ArcGIS FeatureServer
compatibility including edits. That commitment carries a data model, not just a
protocol, and Q-58 recorded the gap: attachments, relationships, domains,
subtypes and editor tracking. The
[Honua capability matrix](../research/honua-capability-matrix.md) §2 confirms
attachments and related records ship at their **Community** tier, so these are
table stakes for FeatureServer compatibility rather than refinements.

This ADR decides the first three: **feature identity, relationships,
attachments.** Domains, subtypes and editor tracking stay in Q-58 and are not
decided here.

**Identity comes first because the other two are built on it.** Attachments and
relationships both key off a feature identifier, and Esri moved attachments from
`ObjectID` to `GlobalID` precisely because `ObjectID` is not stable across
replication. A registered table may have a composite key, a natural key, or no
usable identity at all.

---

## 2. Decision — identity is declared, not inferred (Q-57)

Consistent with [Q-36](../open-questions.md), which established that the service
definition may describe things the physical table does not have.

**Hosted layers.** We own the schema, so identity exists by construction: a
`globalid uuid` column, indexed and immutable, plus a monotonic `objectid`
integer for ArcGIS compatibility. Both are ours, both are stable.

**Registered layers.** We do not own the schema and
[A-017](../architecture-assumptions.md) says we may not have DDL rights. The
administrator therefore **nominates** the identity column in the service
definition. We do not infer it, do not synthesise it from `row_number()` — which
is not stable across queries — and do not maintain a side mapping table, which
would be state about somebody else's table and would drift the moment they
edited it.

### 2a. The native API and the compatibility layer have different requirements

This is the part that is not symmetric, and it needs saying plainly.

| | Identifier requirement |
|---|---|
| **OGC API Features** | A string `id`. A UUID, a natural key or a composite rendered as text all work |
| **ArcGIS FeatureServer** | An `esriFieldTypeOID` field — a **unique integer** |

So a registered table keyed by UUID or text is fully servable through our native
API and **is not servable through the ArcGIS compatibility layer** unless the
administrator can add an integer column, which requires DDL rights.

**The compatibility layer has a stricter data requirement than the product it
wraps.** That is recorded here rather than discovered during a migration, and
the capability report states it per ADR-008 §2 — never degrade silently.

---

## 3. Decision — relationships are declared, not reverse-engineered (Q-58a)

**Rejected:** reading relationship classes out of the source geodatabase's
system tables (`GDB_ITEMS`, `GDB_ITEMRELATIONSHIPS` — `VERIFY`). Three reasons,
in order of weight:

1. It is reverse-engineering Esri internals, which
   [CLAUDE.md](../../CLAUDE.md) §5 forbids.
2. It only works when the source is a geodatabase. Most PostGIS, Oracle and SQL
   Server schemas are not.
3. It breaks whenever Esri changes the layout, and we would not know until a
   customer's relationships vanished.

**Accepted:** relationships are declared in our service definition, using the
Q-36 mechanism.

```yaml
relationship:
  name: parcels_to_owners
  from: parcels.parcel_id
  to:   owners.parcel_id          # or via an intermediate table for M:N
  cardinality: 1:M                # 1:1, 1:M, M:N
  composite: false                # true ⇒ cascade delete
```

**This is strictly more capable than Esri's model.** It works on a plain
PostGIS, Oracle or SQL Server schema with ordinary foreign keys — no geodatabase
required — and an administrator can relate two tables that were never designed
to be related.

Consequences:

- `queryRelatedRecords` is one `IN` query with predicate pushdown
  (ADR-008), not N+1.
- Composite relationships cascade on delete. Where the source database already
  declares `ON DELETE CASCADE`, the database performs it and we must not
  duplicate the work; where it does not, we perform it in the same transaction.
- Registered and hosted behave identically, because declaration is metadata.
- A declared relationship can be **wrong** — nothing validates that the join
  keys correspond. Validation on publish is a condition in §7.

---

## 4. Decision — attachments are stored in the database (Q-58b)

**Owner decision, 2026-08-13: Esri's model — bytes in a companion table beside
the feature.**

Chosen over object storage with proxied delivery, which would have reused
ADR-009 §2.4's mechanism. The reasons the owner's choice is defensible:

- **Transactional with the edit.** An attachment cannot be orphaned by a failed
  feature write, and there is no reconciliation job.
- **Backed up with the data**, by whatever backs up the datastore.
- **Byte-compatible with a migrated `__ATTACH` table**, which matters directly
  for Q-16.
- **One storage tier.** §6 anti-overengineering would have asked what concrete
  problem a second one solves.

All three engines support this and support streaming out of it: PostgreSQL
`bytea`, SQL Server `varbinary(max)`, Oracle `BLOB`.

### 4a. Streaming is mandatory, not an optimisation

**This is the condition the decision rests on.**
[benchmarks/mvt-generation/RESULTS.md](../../benchmarks/mvt-generation/RESULTS.md)
run 3 measured **80.9% GC pause at 18% CPU** on a workload allocating 20 MB per
request. An attachment is arbitrarily large and user-supplied. Materialising one
into a `byte[]` puts it straight onto the large object heap and reproduces
A-037's ceiling on demand — except the user chooses the size.

Therefore: **attachment bytes are never materialised.** Sequential-access
provider stream straight to the response body, and the reverse on upload. No
`byte[]`, no `MemoryStream`, no buffering the whole payload at any layer.

### 4b. The problem this creates for the connection budget

Streaming from the database means **a pooled database connection is held for as
long as the client takes to receive the bytes.** A slow client — or a malicious
one reading at one byte per second — holds that connection indefinitely. With
enough of them the pool is exhausted and the *whole layer* stops serving, not
just attachments.

This is slowloris pointed at the connection pool, and it is a direct consequence
of choosing database storage over object storage. Object storage would not have
had it, because the proxy reads from a store that is not the connection budget.

**Mitigation, which is a condition rather than a decision:** attachment reads
draw from a **separate, small, bounded pool**, isolated from the pool ADR-007
§4.8 budgets for query work. Exhausting it degrades attachments only. Whether
that is sufficient, or whether a size threshold above which we buffer and
release the connection is also needed, is a measurement question — see §7.

### 4c. Registered sources

| Case | Behaviour |
|---|---|
| Registered layer with an existing Esri `__ATTACH` companion table | **Read it.** This is a migrated ArcGIS estate's photographs, and reading them is the difference between *your attachments came across* and *your attachments are gone*. Directly serves Q-16 and Q-17 |
| Registered layer, we hold write and DDL rights | Full support; we create the companion table |
| Registered layer, no DDL rights (A-017) | **Attachments unavailable**, stated in the capability report, per ADR-008 §2 |

### 4d. This is the product's first user-uploaded-content surface

Nothing else we have designed accepts arbitrary bytes from a user. That is a
threat-model change rather than a feature, and
[security.md](../security.md) currently covers none of it. Minimum bar, all of
which are requirements rather than suggestions:

- **`Content-Disposition: attachment` always.** Never render inline.
- **Served from a separate origin, or under a CSP that forbids execution.** An
  uploaded SVG or HTML file served same-origin as the admin API is stored XSS
  against the GIS administrator — our primary user.
- **Client-supplied `Content-Type` is not trusted.** Sniff, and store what we
  determined alongside what was claimed.
- **Filenames are treated as data, never as paths.**
- **A hard size cap**, and a per-layer quota. The cap interacts with
  [geometry-crs-policy.md](../geometry-crs-policy.md) §6's three tiers and
  should follow the same principle: refuse the attachment, by id, not the query
  that happened to touch it.
- **Decompression is not performed** on upload. We store bytes.

### 4e. Operational consequence for the appliance

The datastore is mandatory (Q-69) and now contains arbitrary user binaries. Its
backup size grows without bound and is no longer a function of feature count.
Since Q-32 ships it as a managed appliance we back up, that is our problem
rather than the customer's. Per-layer quotas are the control, and they must
exist before attachments ship rather than after the first full disk.

---

### 4f. What §4a cost, discovered by trying to obey it

*Added 2026-08-15, after building it.*

**A single `bytea` parameter cannot stream, and it looks exactly as though it
can.** Handing Npgsql the request stream as a parameter value compiles, runs, and
buffers the entire attachment inside the driver: `StreamByteaConverter.GetSize`
calls `CopyTo`, because PostgreSQL's binary protocol needs a parameter's length
before its bytes.

**The probe written to check this beforehand missed it**, because it tested with
a `MemoryStream` — which is seekable, so its length is free. A request body is
not. The lesson is narrower than *test with real data*: the property under test
was *can this driver write without knowing the length*, and the fixture answered
a different question by being more capable than the real input.

So the bytes are **chunked**: metadata in one row, content in 64 KB blocks in a
companion table, written through one pooled buffer. Memory is one buffer whatever
the attachment's size. Measured on a 40 MB round trip: the process working set
moved 4 MB on upload and did not grow at all on download.

**What that costs is §4's "byte-compatible with a migrated `__ATTACH` table"**,
and on inspection it costs nothing — §4c's migration case is *reading somebody
else's* `__ATTACH`, which is a different query however we store our own.

**Where the time actually goes**, measured rather than assumed, on a 40 MB
upload against a containerised PostgreSQL:

| | |
|---|---|
| Reading the multipart body | 45 ms |
| Framing and writing chunks | 356 ms |
| COPY flush + commit | ~3,500 ms |

The database write dominates, and an earlier comparison suggesting otherwise was
wrong: `insert … select repeat('x', 65536)` generates its bytes *server-side* and
never crosses the wire, so it measured PostgreSQL's speed at inventing data
rather than at receiving it.

---

## 5. Scope — both in v1

**Owner decision, 2026-08-13.** Relationships and attachments both ship in v1,
for full FeatureServer data-model parity and the strongest possible migration
story.

Recorded honestly: this is a material addition to the first release. Attachments
alone bring a storage tier, a streaming requirement, a separate connection pool,
a quota system and a security surface. The
[phase 0 exit plan](../phase-0-exit-plan.md) sequences features first; this
enlarges that slice rather than following it.

---

## 6. Consequences

- **Q-57 answered**, and it constrains the data model everywhere else.
- **ADR-007 gains a requirement**: a second, bounded connection pool for
  attachment streaming, isolated from the query budget (§4b).
- **security.md gains a section** it does not have: user-uploaded content.
- **ADR-005 gains endpoints**: `queryRelatedRecords`, the attachment CRUD set,
  `queryAttachments`, and the OGC-native equivalents.
- **The capability report carries three new statements**: whether the layer has
  a usable integer OID, whether relationships are declared, whether attachments
  are available and why not.
- **Quotas must exist before attachments ship** (§4e).

### 6a. What is built, as of 2026-08-15

| §  | Requirement | State |
|---|---|---|
| §4 | Bytes in a companion table beside the feature | **built**, chunked — see §4f |
| §4a | Never materialised, either direction | **built**, and tested with a stream that cannot be read twice |
| §4b | Separate bounded pool for attachment traffic | **built**, eight connections, no statement timeout |
| §4d | `Content-Disposition: attachment` always | **built** |
| §4d | CSP forbidding execution | **built** — `default-src 'none'; sandbox`, plus `nosniff` |
| §4d | Client content type not trusted | **built** — sniffed from magic bytes, both stored |
| §4d | Filenames are data, never paths | **built** |
| §4d | Hard size cap | **built**, 128 MB, enforced while streaming |
| §4e | Per-layer quota | **built**, schema 8, enforced inside the write transaction |
| §4c | Read a migrated `__ATTACH` on a registered source | **not built** — refused with 501 saying so |
| §4c | Create a companion table where we have DDL rights | **not built** |
| §4d | Virus scanning | **not built, and not decided** — §4d left it open and it stays open |
| §3 | Relationships declared, not reverse-engineered | **built**, schema 9 |
| §3 | `queryRelatedRecords` is one query, not N+1 | **built**, and the reader counts its own statements so a test can prove it |
| §7 | Publish validates a declaration's join keys | **built** — both columns must exist and be comparable |
| §3 | Many-to-many via an intermediate table | **not built** — refused with the reason. The ADR sketches it and does not specify the second declaration it needs |
| §3 | Composite relationships cascade on delete | **not built.** The flag is stored and nothing acts on it, which is worse than not having it — see below |

**`composite` is stored and does nothing, and that is the worst state.** §3 says
a composite relationship cascades on delete — performed by the database where it
already declares `ON DELETE CASCADE`, and by us in the same transaction where it
does not. Neither happens. An administrator can set the flag, see it reported in
the layer document, and reasonably conclude deleting a parcel removes its owners.
Recorded as [D-26](../architecture-debt.md) rather than left to be discovered by
somebody's orphaned rows.

**The slow-reader mitigation is built and unmeasured.** §4b asks whether a
separate pool is sufficient *or* whether a size threshold above which we buffer
and release the connection is also needed. Eight connections is a bound chosen
by argument; nobody has pointed a slow reader at it.

---

## 7. Conditions

1. ~~**Streaming is verified, not assumed.**~~ **DISCHARGED 2026-08-15.** A 40 MB round trip moves the process working set by 4 MB on upload and not at all on download, bytes identical. And the test that would have caught the original design hands over a stream reporting no length, refusing to seek, and yielding its bytes once — which is what a request body is, and what a `MemoryStream` is not. Original: A test that uploads and downloads a
   large attachment while watching allocation must show flat memory. If any
   layer materialises the payload, §4a has failed silently.
2. **The separate pool is sized by measurement**, not by guess, and the slow
   client case is tested deliberately — including whether a size threshold plus
   buffer-and-release is needed on top of pool isolation.
3. ~~**Declared relationships are validated on publish**~~ **DISCHARGED 2026-08-15.** A declaration is refused unless both columns exist and can be compared, and the refusal lists the columns that do exist. What is *not* checked, and is said in the response rather than implied, is whether the values mean the same thing. Original:, at minimum that the join
   columns exist and are type-compatible. An unvalidated declaration fails at
   query time in front of a user.
4. **`__ATTACH` read support is verified against a real migrated geodatabase**,
   not against our own reconstruction of the schema.
5. ~~**Per-layer attachment quotas ship with the feature**~~ **DISCHARGED 2026-08-15.** Schema 8, enforced inside the write transaction and rolled back — checking before races two concurrent uploads, checking after without a transaction leaves the bytes on disk. Original:, not after it.

## 8. Assumptions

| ID | Assumption | Status |
|---|---|---|
| A-040 | All three engines can stream BLOBs out of a query without materialising them in our process | `UNVALIDATED` — load-bearing for §4a. PostgreSQL `bytea`, SQL Server `varbinary(max)` and Oracle `BLOB` all document streaming access, but with different APIs and different behaviour under TOAST / row-overflow / out-of-line storage |
| A-041 | A bounded separate pool is sufficient protection against slow-client connection exhaustion | `UNVALIDATED` — §4b. If false, attachments need buffer-and-release above a size threshold, which reintroduces the memory problem §4a exists to avoid |
| A-027 | Optimistic concurrency is correct against writes we never see | Unchanged, and now also covers attachment and related-record edits |

## 9. Dissent

**Recorded because §8 of the project rules requires it, not because the decision
is wrong.**

Database BLOB storage was chosen over object storage. The case for object
storage was that ADR-009 §2.4 had already decided the identical question for COG
delivery — proxy by default, signed URLs optional — and reusing that mechanism
would have given one delivery path for all binary content, no connection-pool
exposure (§4b), and no unbounded growth in the appliance's backup (§4e).

The case against it, which prevailed, is that it is not transactional with the
feature edit, needs orphan collection, and is not byte-compatible with a
migrated `__ATTACH` table.

**The revisit trigger is concrete:** if §7's condition 2 finds that pool
isolation is insufficient and buffer-and-release is required, then the database
path has acquired both of object storage's drawbacks — non-transactional
behaviour under buffering, and memory pressure — while keeping its own. At that
point this decision should be reopened rather than patched.
