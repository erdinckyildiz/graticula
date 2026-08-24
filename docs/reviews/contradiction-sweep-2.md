# Contradiction sweep, round 2

**Run:** 2026-08-15 · **Scope:** all 22 ADRs, the policy documents, and the code
as built · **Status of round 1:** `REOPENED`, because it caught only
forward-facing contradictions — decisions disagreeing with each other — and not
decisions disagreeing with what was subsequently built.

Round 2 is run from the other direction. Everything below was found while
implementing against these documents, or by checking a claim in one against the
code that now exists.

---

## What round 1 could not see

Round 1 ran before there was any production code. Its method — read the ADRs and
look for disagreements — cannot detect the largest class of contradiction a
project accumulates, which is **a decision that is still stated and is no longer
true**. Nine of the twelve findings below are that shape.

---

## C-01 — Twelve ADRs still describe a three-database product

**Severity: high. This is the propagation debt (review findings A2, A5, A6, P11,
P14) measured rather than described.**

[v1-scope.md](../v1-scope.md) §3a cut Oracle and SQL Server on 2026-08-13:
*"PostGIS only."* It is explicit that this is "the largest single simplification
available to the project".

| | |
|---|---|
| ADRs mentioning Oracle or SQL Server | **12 of 22** |
| Total mentions | **70** |
| ADRs that mention the cut | **2** (ADR-003 and ADR-022, both written after it) |

ADR-002 is the worst case: fourteen mentions, and its §113 amendment still reads
*"the owner decided PostgreSQL is not mandatory; Oracle Spatial and SQL Server
Spatial are first-class."* A reader arriving at the primary data architecture
decision is told the opposite of the scope.

**Why this matters beyond tidiness.** ADR-008's per-dialect pushdown table,
ADR-013's three-engine attachment storage note, ADR-007's connection budget per
provider and ADR-010's per-engine change detection are all *designed for a
product that v1 is not*. Somebody implementing from those documents builds
abstraction nobody needs — and the abstraction is exactly what the cut was
meant to remove.

**Not repaired here.** Rewriting twelve ADRs is a scope decision, not a sweep
finding: some of those paragraphs are *deferred*, not wrong, and deciding which
is which per paragraph is the owner's. What the sweep can do is stop the
documents being silently misleading, so **[D-27](../architecture-debt.md)**
records it with the count.

---

## C-02 — ADR-007 §4.3 requires authorization in the service context; the code deliberately does not

**Severity: high. Already recorded in the ADR itself, 2026-08-14.**

§4.3: *"a bound context must be self-sufficient for serving … Authorization data
is part of the context for this reason."* The implementation reads sharing and
status from the catalogue on every request.

Both positions are defensible; they cannot both be implemented. §4.3 prefers
surviving a store outage, the code prefers a revocation taking effect. §4.3 did
not consider two servers over one store, where the stale value is not missing but
present and wrong — so its *fails closed* rule never fires.

**Open as [Q-95](../open-questions.md).** The ADR is annotated rather than
edited to agree with the code.

---

## C-03 — ADR-010 §5.1 requires a purge that §4 makes unnecessary

**Severity: medium. Resolved by reasoning, recorded in code.**

§5.1: *"Layer unpublished or permissions changed | Wrong | Purge, and this one
is a security matter."*

§4, written later in the same document: tile authorization is **uniform** — a
layer is readable or it is not, the check runs *before* the cache lookup, and
every authorized caller shares one entry.

Under §4, a sharing change cannot make a cached tile wrong. It changes who
reaches the cache, not what it holds. Purging would discard a seeded pyramid on
the most ordinary administrative act there is.

**Resolved in favour of §4**, with the reasoning written into `SetSharingAsync`
where somebody will look for the missing purge. §5.1's row is correct for
row-level and field-level filtering, which do not exist; when they do, §4's grant
fingerprint is the fix rather than a purge.

---

## C-04 — build-vs-adopt makes the tiling pipeline Tier 1; ADR-021 moves its encode stage into PostGIS

**Severity: medium. Argued in ADR-021 §4 rather than skirted.**

[build-vs-adopt-policy.md](../build-vs-adopt-policy.md) Tier 1: *"tiling
pipeline — written by us, always."* ADR-021 encodes tiles with `ST_AsMVT`.

The defence is that `ST_AsMVT` is a datastore capability reached the same way
ADR-008 reaches `ST_Intersects`, that the datastore is ours, and that most of the
pipeline — addressing, cache, seeding, service model, metadata — remains Tier 1.
The narrowing is real and stated: a tile can no longer be served from anything
that is not PostGIS.

**No action.** The contradiction is recorded at the point of decision, which is
what §2's decision hygiene asks for.

---

## C-05 — ADR-005 calls GeometryServer "near-zero marginal cost"; measurement disagrees

**Severity: high, and newly discovered by this sweep.**

ADR-005 §99: GeometryServer is *"a thin REST surface over PROJ and
NetTopologySuite, both of which ADR-003 already puts in-process. **Near-zero
marginal cost**."*

[benchmarks/geometry-overlay](../../benchmarks/geometry-overlay/RESULTS.md)
measured the overlay half at 153 seconds and 16.7 GB for a 6,408-vertex
adversarial input, and the benchmark run took the host down. A-042 is
`INVALIDATED`, ADR-022 ships half the service, and the other half is blocked on
[Q-97](../open-questions.md).

**Near-zero marginal cost was wrong**, and it was the sentence that made
GeometryServer look like a cheap addition when the scope was set. ADR-005 has not
been amended.

**Action: amend ADR-005 §99** to point at ADR-022 and the measurement. Done below.

---

## C-06 — ADR-013 §4 claims byte-compatibility the storage no longer has

**Severity: low. Already recorded in ADR-013 §4f, 2026-08-15.**

§4 lists *"byte-compatible with a migrated `__ATTACH` table"* among the reasons
for storing attachment bytes in the database. The implementation chunks them,
because a single `bytea` parameter cannot stream and §4a forbids materialising.

The claim was load-bearing for Q-16 and turns out not to be: §4c's migration case
is *reading somebody else's* `__ATTACH`, which is a different query however we
store our own.

---

## C-07 — Six ADRs are `REOPENED` or `DEFERRED` and three of those have shipped code

**Severity: medium.**

| ADR | Status | Reality |
|---|---|---|
| ADR-002 | `REOPENED` → re-decided | Implemented, PostGIS only |
| ADR-005 | `REOPENED` | Three API surfaces built against it |
| ADR-006 | `REOPENED` | No plugin code exists — status is honest |
| ADR-004 | `DEFERRED` | Correct; rendering is out of v1 |
| ADR-012 | `DEFERRED` | Correct; no clustering code |
| ADR-018 | `REOPENED` by owner | Implemented and in force |

**A status that has not moved since before the code was written is a status
nobody is maintaining.** ADR-002, ADR-005 and ADR-018 all govern shipped
behaviour while carrying a status that says the decision is unsettled.

**Action: none taken.** Re-grading a decision is the owner's, and doing it in a
sweep would be exactly the silent drift this document exists to catch.

---

## C-08 — `composite` on a relationship is stored, reported, and does nothing

**Severity: high, and found while writing this sweep.**

ADR-013 §3: *"Composite relationships cascade on delete."* Neither the database
path nor the same-transaction path is implemented, and the endpoint accepts the
flag and reports it in the layer document.

A flag that is stored and reported but not honoured is worse than one that is
absent: an administrator sets it, sees it, and concludes deleting a parcel
removes its owners.

**Recorded as [D-26](../architecture-debt.md).** The endpoint should refuse
`composite: true` until it is honoured — which is a change, not a sweep finding,
and is named in the debt.

---

## C-09 — ADR-013 §4a's "no buffering at any layer" cannot be met as written by the read path's caller

**Severity: low, and it is a wording problem rather than a defect.**

§4a says *"no `byte[]`, no `MemoryStream`, no buffering the whole payload at any
layer."* The implementation honours it. But the rule as written also forbids
anything a *client* does, which is not ours to control, and forbids the
64 KB chunk buffer that makes streaming possible at all.

The rule's *reason* — A-037's allocation ceiling — is met exactly: memory is one
pooled buffer regardless of size, measured at 4 MB of working-set movement for a
40 MB upload. The letter is stricter than anything achievable.

**No action beyond noting it.** A rule whose letter cannot be satisfied gets
quietly reinterpreted, and reinterpretation is how requirements decay. Better to
record that the reason governs.

---

## C-10 — Three ADRs' error-handling assumptions were contradicted by the framework, not by each other

**Severity: medium, and now generalised.**

ADR-008 §2 and ADR-017 §6 both assume the server's refusals are the ones a caller
sees. Three times this week a framework limit fired first and answered 500:
Kestrel's 30 MB body cap, ASP.NET's 4 MB form value cap, and its multipart
buffering.

Not a contradiction between documents — a shared assumption that the stack
underneath honours them.

**Resolved:** [security.md](../security.md) now carries the rule *a framework
limit is not a designed limit*, and each surface raises the framework's bound
above its own.

---

## C-11 — `layer.is_hosted` was read by nothing and written false by everything

**Severity: was high; resolved 2026-08-14 by schema 7.**

A column present since schema version 1, written `false` by every insert, and
trusted by [Q-67](../open-questions.md)'s rule that tiles come only from hosted
data. Nothing was ever hosted, so the rule refused every layer — and the defect
was invisible because no tile surface existed to be refused by it.

Now derived from `data_source.is_datastore`. The dead column remains as
[D-24](../architecture-debt.md) because dropping it is a contract migration.

---

## C-12 — The v1 scope table listed GeometryServer and VectorTileServer as single items

**Severity: low, and corrected.**

Both turned out to be two things with different risk: tiles from hosted versus
registered data (Q-67), and geometry operations that are linear versus those that
are not (A-042). The scope table now says so for both.

---

## What this sweep did not cover

- **The §66 review gates**, which are a different exercise and remained 0 of 9
  when this sweep ran. Seven of the nine had run by 2026-08-20; the live tally is
  the §66 table in [architecture-completeness.md](../architecture-completeness.md).
- **ADR conditions**, roughly 60 across 22 documents. Whether each is discharged
  is a per-condition check, not a contradiction search.
- **Contradictions between an ADR and a document neither it nor the code
  references.** The method here was *read the decision, look at the code* — it
  cannot find a stale claim in a research note nothing links to.
- **A-003**, the load-bearing assumption under ADR-007, which lost its validation
  route when Q-49's criterion was dissolved and still has none.
