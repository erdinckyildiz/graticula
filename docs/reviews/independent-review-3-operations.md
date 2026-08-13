# Independent Review 3 — Operability and Failure

> **Provenance, and what this is not. Read before citing it.**
>
> Produced 2026-08-13 by a reviewer with **no access to the conversation,
> reasoning or history that produced this architecture** — only the repository
> and `MASTER_GIS_PLATFORM_PROMPT.md`. Briefed as *the person who gets paged at
> 3 AM*, to cite file and section, and not to praise.
>
> **This does not discharge §67**, for the reasons stated in
> [independent-review-3-architecture.md](independent-review-3-architecture.md).
> **Round 4 remains owed.**
>
> Findings reproduced as written. Dispositions:
> [independent-review-3-synthesis.md](independent-review-3-synthesis.md).

---

## O1 — SEVERE · The version handshake and expand-and-contract contradict each other, and between them delete rolling upgrade *and* the documented rollback

ADR-016 §4 requires exact version agreement or startup refusal; §5 and §6 both
require old code to keep running against the new schema. Both cannot be true.

§4: *"every component reads the platform schema version and refuses to run
against an incompatible one… server, job worker and the datastore's schema stamp
**must agree**."* §5: *"**Expand** — new columns, tables and indexes… **Old code
ignores them**."* §6: *"Expand-and-contract makes the expanded schema **readable
by the previous version** — that is what buys the rollback."*

"Must agree" is exact-match language. Nothing defines a compatible *range*, the
only construct that reconciles the two.

**Scenario.** Operator upgrades. Runs the explicit migration. The stamp is N+1.
Rolling deployment brings up a new server while the old still serves — the old
reads the stamp, sees a mismatch, refuses. **There is no rolling upgrade.** The
operator then hits a problem and invokes §6's rollback: the N‑1 server starts,
reads N+1, refuses. **The rollback path ADR-016 was written to establish does not
execute.** The only remaining option is restore-from-backup — which §6 reserved
for the post-contract case, and which has no design (O2).

Second, unnamed: restore-from-backup silently discards every write since the
pre-migration backup. Nowhere stated. An operator told "rollback is
restore-from-backup" will not assume it means "and you lose today's edits".

---

## O2 — SEVERE · Backup and restore — the only recovery path the architecture names — has no design at all

`docs/deployment.md`, in its entirety: *"**Status:** STUB — not written… Also:
installation, configuration, upgrade and rollback, **backup and disaster
recovery**, monitoring and troubleshooting."*

Against that: ADR-016 §6 *"**The upgrade takes a backup automatically**, before
touching anything. Not advisory"*; §2 ships a datastore image containing *"our
backup agent"*; §6 *"recovery is restore-from-backup"*; ADR-015 §8 *"All small,
all precious, all in the ADR-002 backup path."* Q-48 is open and unowned. ADR-013
§4e already establishes the thing being backed up is unbounded: *"backup size
grows without bound and is no longer a function of feature count."*

No document states where the backup is written, how much space it needs, whether
it is verified, whether it is ever restore-tested, how long it takes, or what the
restore procedure is.

**Scenario.** Single enterprise server — the profile the architecture explicitly
designs for. Datastore volume holds 700 GB; 200 GB free. Operator triggers the
upgrade. The automatic backup writes to the same volume, fills the disk, and
PostgreSQL — platform store *and* hosted data *and* job records — stops accepting
writes. Per `failure-scenarios.md` §6, *"disk-full now takes the platform store,
the hosted data and the cache together… and recovery needs disk."* The migration
is half-applied, the backup truncated, and both the rollback door and the restore
path are gone in the same minute. **A total, unrecoverable data-loss event
triggered by following the documented upgrade procedure.**

---

## O3 — SEVERE · User-supplied Python executes on the most privileged process in the system, with no sandbox and no defined publisher

Q-75: *"the sandbox itself remains open… the largest security surface in the
product by a wide margin… **What is not settled: process user and container
boundary, CPU / memory / wall-clock / disk limits, whether tools get network
access at all.**"* ADR-016 §2 puts it in the job-worker image alongside GDAL and
the wheel set. ADR-011 §1 puts registration, validation, seeding and overview
generation in the *same* pool, and §3.6 says jobs *"draw from the same budget"* —
the same registered-data-source connections.

**Attack.** Six lines of Python read the process environment, recover the
platform-store connection string and the secret-encryption key (O7), connect
directly, and dump every registered Oracle and SQL Server credential in the
estate. `socket` to `169.254.169.254` is also available — ADR-014 §4's SSRF
allow-list is written **only** for the COG proxy and nothing extends it to the
Python runtime, a different process with unrestricted egress.

**And the named mitigation is not a mitigation.** ADR-015 §7 says *"Code
publication defaults to administrators."* But **Q-59 — what the role set is — is
open.** "Administrator" is ambiguous between platform and organisation
administrator, and security.md §2.0 already introduces publishers who are not
administrators but own items. **A control that depends on a role set nobody has
defined is not a control.**

---

## O4 — SEVERE · The break-glass authentication bypass is gated on a condition an attacker can create

ADR-017 §6: *"Bootstrap authentication | A break-glass path, audited to disk,
**valid only while the store is unreachable**."* Condition 3 requires it be
unusable while the store is reachable, *"otherwise it is an authentication
bypass"*. §11: *"conditions get relaxed."*

**What the documents do not consider:** the bounding condition is not an act of
God, it is a state of the network. An attacker who can exhaust the datastore's
connections, black-hole it at the firewall, or simply saturate it (there is no
data-plane rate limiting — O5) **turns the bypass on at will.** The gate is not
"the store is down"; it is "the attacker has decided the store is down".

Compounding: the credential cannot live in the platform store, so it is a file or
environment secret. Nothing defines it. By construction it is a static shared
secret with no rotation, no session listing and no revocation — **the three
properties ADR-015 §3 chose opaque tokens specifically to obtain.** The surface
it unlocks includes `GET /admin/certificates`.

---

## O5 — SEVERE · No data-plane rate limiting, no per-principal cost accounting, and anonymous access is a designed default

Every rate limit points outward or at login: ADR-011 §3.6 limits *jobs against
source databases*; ADR-015 §5 limits *failed logins*; ADR-009 §2.4 calls rate
limiting something the COG proxy is *"a place to enforce"*. `security.md` §6
lists *"denial of service and the interaction with ADR-007's backpressure"* as
not written. ADR-007 §4.9's admission control is queue-depth based, not
principal-aware. D-04 records that per-tenant limits do not exist and is `OPEN`.

Meanwhile ADR-015 §2 makes anonymous first-class, and ADR-010 §2 leaves the
expensive path uncached by design: *"feature query responses have an unbounded
key space."*

**Scenario.** Open-data deployment. One unauthenticated script issues distinct
CQL2 filters — each a guaranteed cache miss, each a real query, each allocating
on the order of ADR-007 §4.14's numbers. The worker becomes GC-bound at 18% CPU.
Admission control begins rejecting — **for everybody**, because rejection is by
queue depth, not principal. There is no endpoint to identify the offender, no
per-principal counter in ADR-017 §4, and no lever to block it. The documented
diagnostic walk ends at "pin the context", which does nothing.

---

## O6 — SEVERE · "Revocation that works" is ADR-015's central argument, and nothing designs how revocation reaches the caches downstream of it

ADR-015 §3 chose opaque tokens for *"**Revocation that works.**"* Then: *"it is
cacheable in-process with a short TTL **bounded by the revocation delay we are
willing to accept**"* — a number that does not exist. Second cache: ADR-007 §4.3
puts *"effective authorization data"* **inside the service context**, bound
lazily, evicted LRU, pinned indefinitely for hot services. §4.6 defines refresh
only for *service definition* changes — **a user's grant change is not a service
definition change**, and no invalidation path is defined. Third: ADR-010 §7's
cross-node invalidation is polled, and Q-30 flags that the number must be
documented.

**Scenario.** An employee is dismissed at 09:00. Account disabled, grant revoked.
Their session dies at next request — correct. But their **API key**, or a
colleague's account sharing the group grant, continues to be authorized by a
pinned service context on worker 2 holding a stale copy, on a node that has not
polled. No documented bound, no endpoint to force a re-bind for an authorization
change, no metric that would reveal it.

---

## O7 — SEVERE · The secret-encryption key is absent from the "completed" state inventory, has no rotation design, and its loss destroys every registered data source

ADR-002 §4.7: *"Secrets are encrypted at rest in the platform database, with the
encryption key supplied externally at startup… Data source credentials must never
be readable by someone with a database dump."* ADR-016 §3 — *"the state
inventory, **completed**"* — lists platform database, hosted data, certificates,
sessions, Python tool code, glyphs, L3 cache. **The encryption key is not in the
table.** `security.md` §6 still lists *"secret handling beyond 'encrypted at
rest'"* as unwritten.

**Scenario A (loss).** ADR-016 §8 promises developer and single-server are *"the
same compose file, one command"*. The key will be an environment variable in that
file, beside the volume it protects — so "never readable by someone with a
database dump" holds only until someone backs up the host. **Scenario B
(rotation).** No rotation or re-encryption design, no key version alongside the
ciphertext. An operator rotates after a suspected exposure, then restores a
pre-rotation backup two weeks later. Every registered credential in the estate is
undecryptable, and the symptom is an authentication failure at each provider
individually — the diagnosis appears in no documented surface.

---

## O8 — MODERATE · The degraded admin surface cannot report the most likely cause of the outage it exists to survive

ADR-017 §6 enumerates five things that work without the platform store: health,
version, certificates, workers, break-glass. Its principle: *"A 500 from the
admin API during an outage is the worst possible response."* But
`failure-scenarios.md` §6 identifies **disk-full** as the failure that takes the
platform store down. No document names free disk space as a monitored signal; no
reserved headroom is specified; Q-61 quotas and the L3 budget are unimplemented,
and ADR-013 §4e concedes attachments grow the volume without bound.

**Scenario.** 03:00. A publisher's overnight attachment upload fills the volume.
PostgreSQL stops. The admin API degrades correctly and reports: workers up,
certificates valid, version N. **Every signal it can produce is green.** The
supervisor, which ADR-014 taught to watch certificate expiry at 30/7/1 days, was
never taught to watch the one resource whose exhaustion takes down three
subsystems at once.

---

## O9 — MODERATE · A data source that hangs is not covered; no connect or socket timeouts, and the circuit breaker has nothing to count

`failure-scenarios.md` §3 bounds slowness with *"Statement timeouts bound the
individual query"*. A repository-wide search finds **no** connect timeout, socket
read timeout, or TCP keepalive anywhere. The circuit breaker (N3) has no stated
trip condition. ADR-014 §3 adds a TLS handshake beneath the statement timeout on
every pool refill, combined with §4.8's shrink-to-zero pools.

**Scenario.** A firewall rule **drops** rather than rejects packets to Oracle.
Pools have shrunk to zero, so every request begins with a `connect()` that hangs
until the OS timeout — minutes, entirely beneath the statement timeout everyone
relies on. No error, so the breaker never trips. The source never enters
`UNREACHABLE`, so health composition reports `ACTIVE`. Requests pile until
admission control rejects, and the visible symptom is **a healthy database and a
server refusing traffic.**

---

## O10 — MODERATE · Runaway in-process work has no cancel path; the only lever punishes every other tenant

`runtime-supervisor.md` §5 detects precisely: *"worker 3 has a request that has
been running for 340 seconds"*. But §4 offers only Drain, Restart and Recycle —
whole-worker operations. ADR-017 §4's runtime resources are *"workers, service
contexts, pins, drain"*: no in-flight request list, no per-request cancel, no
kill-session. ADR-007 §4.9's cancellation is client-initiated only.

Statement timeouts do not cover this: the runaway is in our process, after the
query returns. `failure-scenarios.md` §9 states it and leaves it: *"a repair
attempt on a pathological geometry can consume unbounded CPU"*.

**Scenario.** A publisher stores one self-intersecting polygon with 400,000
vertices. A query touches it. A predicate evaluation pins a thread indefinitely.
The only documented action is to recycle worker 3 — evicting every warm context
(the exact storm ADR-014 §2b refuses to trigger for certificates) and killing
every other tenant's in-flight request. Repeat the request and it happens again:
**there is no way to remove the offending feature from service without dropping
the layer.**

---

## O11 — MODERATE · Stale-while-error's non-negotiable exception depends on information that arrives late

ADR-010 §5.1a: *"**With one exception that is not negotiable:** this never
applies to the *wrong* class… serving a purged tile during an outage would turn
an availability event into a disclosure."* §5.1 places *"**permissions
changed**"* in the wrong class. §7: invalidation is **polled**. §8 concedes: *"a
real correctness gap for the 'wrong' class."*

**Scenario.** A grant is tightened at 10:00. At 10:00:30, before node B polls,
the data source becomes unreachable. Node B holds a tile it believes is merely
*stale*. §5.1a instructs it to serve stale content during a source outage. It
serves the tile to a user who is no longer authorized. Two accepted decisions
compose into the disclosure one of them declares non-negotiable.

Note also §4's structural claim — *"changing the effective grant changes the key,
so old entries become unreachable"* — means an entry is **orphaned rather than
purged**, so "a purged entry stays purged" describes a mechanism that does not
exist in the design it appears in.

---

## O12 — MODERATE · Clock skew is acknowledged as unwalked and is load-bearing in five mechanisms

`failure-scenarios.md`, "Not yet walked": *"Clock skew, which affects leases and
TTLs and was not on §59's list **but should have been**."* Depended on by:
ADR-011 §3.4 job leases across nodes; ADR-015 session and token expiry; ADR-014
§2c certificate expiry at 30/7/1 days; ADR-010 §5.3 TTL and §6b generation time;
ADR-017 §6's break-glass audit record.

**Scenario.** Two nodes, one five minutes ahead — routine after a VM snapshot
restore, and normal in an air-gapped install where NTP has no upstream. Node B
considers a lease expired that node A holds. Both run the job. A job declared
`NEITHER` is *"marked failed, require an operator decision"* spuriously and
repeatedly, with no signal pointing at time. Certificate expiry warnings fire
against the wrong date on one node — degrading the one mechanism the architecture
designated *"the most predictable outage this system can suffer"*.

---

## O13 — MODERATE · "Observed, not configured" has no off switch, contradicting ADR-007's own condition

ADR-007 §10 condition 4: *"**Manual override must exist for every adaptive
behaviour** in §4.5. An administrator who disagrees with the system must be able
to win."* §12: *"'observed, not configured' is the least defensible choice
here."* §4.4 describes five interacting feedback mechanisms with no damping.
ADR-017's runtime surface offers only workers, contexts, pins and drain.

**Scenario.** Auto-pinning oscillates at 02:00. Latency is erratic across many
services with no single cause. There is no endpoint to freeze the system into
deterministic behaviour, none that says *why* service X was escalated, and no pin
churn history. Pinning one layer manually — the only available action —
increases pin-budget pressure and, by §4.4's own chain, evicts someone else's
pin. **The one documented remedy accelerates the failure.**

---

## O14 — MODERATE · Unwrapped provider errors are required by the diagnostic design and forbidden by the disclosure rule

ADR-017 §3.3: *"`GET /admin/jobs/{id}/log` | **The provider's actual error, not a
wrapped one**."* Against `security.md` §5 (D-03): *"detail is
authorization-scoped."* D-03 is *"Open until implemented"*.

**Scenario.** Registration is an interactive-class job and self-service
publishing means non-administrators submit jobs. Oracle and ODBC errors routinely
embed the full TNS descriptor or DSN — host, port, service name, sometimes the
user. A publisher who can read the log of a job they submitted — the obvious
default nobody has ruled out, because Q-59 is undefined — obtains the internal
database topology D-03 exists to withhold. **The reconnaissance surface is
created by a requirement written three sections after the rule that forbids it.**

---

## O15 — MODERATE · The air-gapped patch burden is transferred wholesale to the customer, with no security-response process to transfer it back

ADR-016 §7: *"**Nothing is fetched at runtime.**"* The wheel set *"becomes ours to
maintain"* (A-049). `security.md` §6 lists *"dependency vulnerability process"*
as not written. D-06: *"**Dependency licensing is deliberately unexamined.**"* —
so there is not even a bill of materials to check a CVE against. Q-72 is open:
*"**A public server product with no security contact is a liability rather than a
gift.**"*

**Scenario.** A critical GDAL or libxml CVE lands. The customer — the air-gapped
defence or government site ADR-014 names as a plausible buyer — cannot patch the
image, cannot enumerate what is in it, and has no contact to ask. Their security
team scans the three images at the next audit and finds unpatched components with
no vendor advisory and no SBOM. In a regulated environment that is a removal
order.

---

## The operational failure most likely to make a customer remove this product

Not a breach — an upgrade. O1, O2 and O8 compose into a single foreseeable
Saturday night. An administrator follows the documented procedure: run the
explicit migration, which the architecture promises takes a backup first. The
backup has no designed destination and lands on the one volume holding the
mandatory datastore, the hosted data that is the system of record, and the L3
cache; the volume fills; PostgreSQL stops; the migration is half-applied. The
operator reaches for rollback and discovers ADR-016 §4's exact-version refusal
will not let the previous version start against the expanded schema — the
rollback §6 was written to guarantee cannot execute. They reach for
restore-from-backup and find a truncated file produced by a mechanism no document
specifies, restoring into a two-store consistency problem (Q-48) nobody has
solved. They open the admin API, which degrades exactly as designed and reports
workers up, certificates valid, version N — every signal green, because the
degraded surface was never taught to report disk. **The customer's hosted data
was the system of record, so this is not downtime, it is loss. A product that
eats a customer's authoritative data while they are following its own upgrade
instructions does not get a second maintenance window.**
