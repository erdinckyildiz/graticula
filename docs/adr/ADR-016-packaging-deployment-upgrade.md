# ADR-016 — Packaging, Deployment and Upgrade

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` |
| **Decided** | 2026-08-13 |
| **Answers** | Q-71 · Q-13 · Q-76 · closes failure-scenario **N9** · blocker **B5** |

---

## 1. Context

Q-71 asked what the one-artefact story is now that the baseline is three images.
Q-13 asked for the upgrade and rollback story and has been **named in six
documents** without an owner; fresh-challenger review G8 said it needs its own
ADR before Phase 1, N9 said the same from a third direction, and Q-45 implied it.
This ADR takes all of them, because they are one problem seen from four angles:
**what do we ship, what persists, and what happens when the version changes.**

---

## 2. Decision — three images, and the count is not the interesting part

| Image | Contains | Why separate |
|---|---|---|
| **server** | The request-serving runtime, admin API, supervisor. **No GDAL, no Python.** | A-016's rule: the serving container ships no GDAL. Small, and the smallest attack surface of the three |
| **datastore** | PostGIS, our initialisation, our backup agent, a version stamp | Mandatory (Q-69). A thin derived image rather than stock `postgis/postgis`, because Q-32 promised an appliance **we** configure, back up and upgrade — and we cannot promise that about an image we do not build |
| **job-worker** | GDAL, PROJ grids, the Python runtime and a curated wheel set | Where the heavy, risky and slow things live: format conversion, raster processing, and user-supplied code |

**Rejected: a single all-in-one image.** It cannot scale workers independently
of the request path, which ADR-007's whole worker model depends on, and it puts
GDAL and user Python in the serving container, which A-016 and ADR-015 §7 both
forbid for good reasons.

**Not split further.** The job worker is large — GDAL plus a Python wheel set —
and there is an obvious temptation to split it into a GDAL worker and a Python
worker. §82 says no until size or scheduling actually hurts. Recorded as a
**trigger**: if the air-gapped bundle becomes unwieldy, or if the two workload
classes need different resource limits in practice, split then.

**The interesting number is not three. It is what lives in a volume**, which is
§3.

---

## 3. Decision — the state inventory, completed

[ADR-002](ADR-002-primary-data-architecture.md) §5 produced most of this. Three
things have been added since and were not in it, and one ambiguity it recorded
has been resolved.

### Must persist across a container replacement

| State | Where | Note |
|---|---|---|
| Platform database | datastore volume | ADR-002 §5's list — definitions, catalog, roles, jobs, cache index, schema version |
| Hosted data | datastore volume | Including **attachment BLOBs** ([ADR-013](ADR-013-feature-service-data-model.md) §4) |
| **Certificates and trust anchors** | secret volume | **New, and easy to miss.** [ADR-014](ADR-014-tls-and-certificates.md) §2b installs them through the admin API, which makes them *state*, not configuration. Losing them on a container replacement regenerates a self-signed certificate and every client fails validation at once |
| **Sessions, API keys, accounts** | platform database | New, from [ADR-015](ADR-015-authentication.md). Losing them logs out an organisation |
| **Python tool definitions and code** | platform database | New, from Q-17b. This is user-authored source; losing it destroys work that exists nowhere else |
| Glyphs, sprites, uploaded artefacts | node-local or shared | ADR-002 §5 |
| L3 tile cache | node-local volume | Regenerable, and expensive to regenerate — 204 MB allocated per z12 tile, run 3 |

**Resolved ambiguity.** ADR-002 §5 said the datastore *remains optional*, so both
the with-datastore and without-datastore cases had to be designed. **Q-69 made it
mandatory**, so only one case exists. That halves the storage design and removes
a branch from every backup and restore path.

### Deliberately not persisted

In-process caches, warm service contexts (ADR-007 §4.3), and connection pools.
Losing them costs latency, not correctness — which is exactly why ADR-014 §2b
refuses to restart for a certificate.

---

## 4. Decision — the version handshake refuses; it does not migrate

> **Amended 2026-08-13** — [independent review 3](../reviews/independent-review-3-operations.md)
> **O1**, severe. As first written, §4 required exact version agreement
> (*"must agree"*) while §5 and §6 both required old code to keep running against
> the expanded schema. **Both cannot be true**, and between them they deleted
> rolling upgrade *and* the rollback §6 exists to provide: once the migration
> stamps N+1, the N−1 component reads the stamp, sees a mismatch, and refuses to
> start.
>
> **The fix is §4a. The stamp carries two numbers, not one.**

### 4a. Two numbers, and the contract phase is what closes the rollback door

The stamp in the platform store records:

| Field | Meaning |
|---|---|
| `schema_version` | The migration level actually applied |
| `minimum_reader_version` | **The oldest component version that may safely operate against it** |

A component declares what it needs and the handshake asks two questions:

1. **Am I too old?** `component_version >= stamp.minimum_reader_version`.
   Fails when a **contract** migration has removed something this component
   still uses.
2. **Am I too new?** `stamp.schema_version >= component.required_schema`.
   Fails when the migration has not been run yet.

**Expand raises `schema_version` and leaves `minimum_reader_version` alone.**
That is precisely why the previous version keeps running, and it makes §6's
rollback window a *mechanical* property rather than a documented promise:
**contract is the operation that raises `minimum_reader_version`, and raising it
is what shuts the door.**

This also answers the question §4 could not. A released component cannot declare
forward compatibility with a schema that did not exist when it shipped — so the
**schema** declares backward compatibility instead. The information lives where
it is known.

**Refusal messages name both numbers and the direction**, per §10 condition 1:
*"this component is version 4 and the platform store requires at least version 5
— a contract migration has run and this component cannot be rolled back to"*, not
*"incompatible schema"*.

---

### 4b. The original decision, retained

**On startup, every component reads the platform schema version and refuses to
run against an incompatible one.** It does not auto-migrate.

Auto-migration on startup is how an old container started by accident — a stale
tag, a rollback, a stray `docker run` — silently rewrites a newer schema. The
failure is unrecoverable and looks like corruption rather than a mistake.

Migration is therefore an **explicit operation**: an admin API call or a command,
which takes a backup first (§6), reports what it will do, and does it once.

Three components, one version: server, job worker and the datastore's schema
stamp must agree. A mismatch produces a startup refusal naming **which two
disagree and in which direction** — not "incompatible schema".

## 5. Decision — migrations are expand-and-contract (closes N9)

A migration may only **add** in the version that introduces it. Removal happens a
version later, once no old instance can still be running.

- **Expand** — new columns, tables and indexes, all nullable or defaulted. Old
  code ignores them; new code writes both shapes.
- **Contract** — dropping the old shape. A separate, later, explicitly triggered
  step.

N9 called this standard discipline, unstated, and **impossible to retrofit after
the first migration that breaks it.** That is the reason it is here rather than
in Phase 1: the constraint costs nothing today and cannot be added later.

## 6. Decision — rollback has a precise limit, and saying so is the point

> **Rollback is supported to exactly one prior version, and only before that
> version's contract phase has run. Beyond that, recovery is restore-from-backup.**

This is honest rather than generous. Expand-and-contract makes the expanded
schema readable by the previous version — that is what buys the rollback. Once
contract runs, the old shape is gone and the old code cannot read the new one, so
"rollback" would mean data loss dressed as a procedure.

Two mechanics follow:

- **The upgrade takes a backup automatically**, before touching anything. Not
  advisory, not a documented step someone skips at 2 AM.
- **Contract is never automatic and never same-session.** It is a separate,
  deliberate action taken after the operator is satisfied, and until then the
  rollback door stays open.

## 7. Decision — the air-gapped bundle, which answers Q-76

**Delivery is a single verifiable bundle**: the three images, a compose file, a
manifest, and checksums. Downloaded on a connected machine, carried in, loaded.

**Nothing is fetched at runtime. That is the rule, and it decides Q-76.**

| Would fetch | Decision |
|---|---|
| `pip install` for Python tools | **No.** The job-worker image carries a **curated wheel set** that we version and patch. Q-76 asked convenience versus offline; offline wins, and the cost is that the wheel set becomes ours to maintain |
| ACME certificates | No — [ADR-014](ADR-014-tls-and-certificates.md) §7 |
| PROJ grid downloads | No — `PROJ_NETWORK=OFF`, grids ship in the image |
| GDAL driver data | Ships in the image |
| Fonts and glyph packs | Ship, when rendering exists (ADR-004) |
| OCSP / CRL | Soft-fail — ADR-014 §7, A-045 |

**The manifest is not paperwork.** *Did the transfer complete correctly* is a real
2 AM question in an air-gapped install, and a checksum answers it in seconds
where a subtly truncated image layer otherwise produces a baffling runtime error.

This also closes most of Q-15's checklist. What remains open there is whatever
the rendering engine needs, since ADR-004 is deferred.

## 8. Decision — profiles, and no second code path for developers

| Profile | Shape |
|---|---|
| **Developer** | The same compose file, one command. **Not a different architecture** |
| **Single enterprise server** | One node, all three images, datastore co-located. **The target we design for** |
| **Enterprise cluster** | Several peer nodes, shared platform store. Deferred to ADR-012 |
| **Kubernetes** | A Helm chart derived from the compose file. Never required, never the reference |

Q-69 and Q-70 removed the SQLite single-binary developer profile, and the exit
plan asked whether developers get an escape hatch. **No** — a second storage path
existing only in development is a category of bug that ships. One command that
starts the datastore is a smaller cost than a divergent code path, and the
developer then runs what the customer runs.

---

## 9. Consequences

- **Q-13 answered**, after being named in six documents without an owner.
- **N9 closed.**
- **Q-76 answered** by §7's no-runtime-fetch rule.
- **Q-15's checklist** is mostly satisfied; the remainder is rendering-dependent.
- **ADR-002 §5's state inventory gains three entries** — certificates, sessions
  and Python tool code — and loses a branch, since Q-69 made the datastore
  mandatory.
- **The admin API gains** migrate, contract, backup and version-status
  operations.

## 10. Conditions

1. **A version-mismatch refusal is tested by deliberately starting a stale
   image**, and the message must name which two components disagree and in which
   direction.
   ~~**PARTLY DISCHARGED.** The refusal has been observed twice against a real
   store — a schema-5 server against a schema-2 store, and against an empty one
   — and both messages name the two versions and the direction. What has *not*
   been tested is the other direction: a **stale image against a newer store**,
   which is the dangerous case §4b exists for. That needs two published image
   tags, so it is owed with the first release rather than now.~~
   *(**Discharged 2026-08-27, and the premise for deferring it was wrong.** It does not
   need two published image tags. What makes a component stale is the store's
   `minimum_reader_version` standing above the version that component was built for, and
   that is arrangeable on a scratch schema — which is what
   [tools/stale-component-rehearsal.sh](../../tools/stale-component-rehearsal.sh) does, so
   the run repeats rather than being a story about one afternoon.
   **Both directions, observed against a real server and a real store.**
   A build at schema 37 against a store at **applied 40, minimum reader 39** refuses at
   startup: *"server is built for schema 37, and the platform store requires at least 39
   (it is at 40). A contract migration has removed something this version still uses, so it
   cannot be rolled back to."* Two versions, the direction, and what to do — which is what
   this condition asks the message for.
   **And the case that must not be refused is not refused.** The same build against
   **applied 40, minimum reader 1** — a store ahead by expand only — starts and says
   *"server at schema 37 is compatible with the platform store (schema 40, minimum reader
   1)"*. That is §4b's whole design working: a stale component is refused when a contract
   has passed it and admitted when one has not, and a check that refused both would have
   made every expand migration a synchronised restart.
   `UpgradeAndRollbackTests` covers both outcomes as a function; this is the same two
   answers from a process at startup.)*
2. **A rollback is rehearsed**, not assumed: upgrade, roll back before contract,
   confirm the previous version serves correctly.
   *(Discharged 2026-08-27 —
   [tools/rollback-rehearsal.sh](../../tools/rollback-rehearsal.sh), against a **copy of a
   real store with nine layers in it**, because *serves correctly* cannot be shown against
   an empty schema. Four steps, in the condition's own order.
   **(1)** This build serves the copy: the `ci_buildings` layer document, 2,018 bytes.
   **(2)** The store is upgraded past the build — applied 41, minimum reader 1, an expand.
   **(3)** The previous version is rolled back to. It starts, reports *"server at schema 37
   is compatible with the platform store (schema 41, minimum reader 1)"*, answers
   `/rest/info` with 200, returns the **byte-identical** layer document, and answers a query
   with its 8 features. Byte-identical is the strongest available form of *serves
   correctly*: not merely a 200, and not merely the same field names.
   **(4)** And a contract past it is refused, with the message condition 1 asks for.
   **The rehearsal is a script rather than an afternoon**, so it can be run again before a
   release rather than remembered.)*
3. **The bundle is tested by installing on a machine with no network route**,
   which is the only way Q-15 gets tested rather than asserted.
   ***(Discharged 2026-08-27 —
   [tools/airgap-rehearsal.sh](../../tools/airgap-rehearsal.sh), and it is a deployment
   rather than a process.)***
   **`--internal`, not `--network none`, and the difference is the whole test.** A container
   with no network at all cannot reach its database either, so it proves that a server
   without a database does not start — which nobody doubted. What Q-15 asks is whether
   anything is *fetched from the internet*: PROJ grids, GDAL driver data, fonts, a telemetry
   endpoint, an OCSP responder. A Docker `--internal` network gives container-to-container
   routing and no default gateway, which is a datacentre with no way out, and the datastore
   runs on it.
   **The isolation is proved before it is trusted**: the run fails outright if `nuget.org` or
   `cdn.proj.org` resolves from inside, because a test that assumes the network is cut and is
   wrong proves nothing.
   **What the walk does, all of it offline:** applies **all 37 migrations**; starts the
   server, which listens on TLS with a certificate it generated itself; takes the one-time
   setup token from its own log and completes setup; and asks `/rest/services`, which answers
   **200** with its folders. **And its log complains about nothing it could not reach.**
   **Two failures on the way, both in the rehearsal rather than the product**, and both worth
   the sentence: `pg_isready` inside the datastore answers *ready* while PostgreSQL is still
   on the unix socket alone for its initialisation phase, so the server got *connection
   refused* from a database that had just been declared up — the probe goes over TCP now.
   And the readiness probe used `wget`, which the server image does not carry, so a server
   whose own log read *Listening on https://0.0.0.0:8443* was reported as NOT SERVING. **A
   probe that cannot succeed is indistinguishable from a server that cannot answer.**
   **What is still not covered** is what §8's note already says: whatever the rendering engine
   needs, since [ADR-004](ADR-004-rendering-engine.md) is deferred and v1 cut it.
4. **Certificate material survives a container replacement**, tested — §3 lists
   it as state, and it is the entry most likely to be treated as configuration
   by whoever writes the compose file.
   **DISCHARGED 2026-08-14, and the prediction was correct: it was wrong.** The
   server generated a fresh self-signed certificate on every start, so every
   replacement changed its identity and every client that had accepted the old
   one failed at the same moment. It is now written to the state volume on first
   run and loaded thereafter, replaced only when within a day of expiry.
   Verified by capturing the SHA-256 fingerprint, running `docker compose down`
   — which destroys the containers — then `up`, and comparing. Identical.

   Two further quickstart defects came out of the same walk, both from running
   it rather than reading it: compose's `${VAR:?}` guard on the secret key fired
   on *every* command including the `keygen` that produces the value, making the
   documented first step impossible; and `restart: unless-stopped` turned
   §4b's deliberate, permanent refusal to start against an un-migrated store
   into a crash loop that scrolled its own explanation away five times a minute.

## 11. Assumptions

| ID | Assumption | Status |
|---|---|---|
| A-048 | Expand-and-contract is achievable for every migration we will need, including index and constraint changes on large hosted tables | `UNVALIDATED` — the discipline is standard, but a `NOT NULL` addition or a unique index on a large table can lock, and ADR-007 §5b already says we must not block a DBA's DDL. The interaction is unexamined |
| A-049 | A curated Python wheel set can cover realistic geoprocessing without pip at runtime | `UNVALIDATED` — §7's cost. If users routinely need packages we did not anticipate, either the set grows unmanageably or the offline promise breaks. The escape hatch is a customer-built worker image, which shifts the burden rather than removing it |

## 12. Dissent

**Against building our own datastore image.** Stock `postgis/postgis` is
maintained by people who do this full time, and deriving from it means tracking
their updates plus our own. The counter is Q-32's promise: an appliance we
configure, back up and upgrade. We cannot promise operational ownership of an
image we do not build. The derivation should stay thin enough that the argument
does not curdle.

**Against refusing rather than auto-migrating.** Refusal means an operator must
take an action during an upgrade, and some will find that worse than a server
that "just works". They are wrong once, expensively — but the objection is real
and the refusal message has to be good enough that the action is obvious.
