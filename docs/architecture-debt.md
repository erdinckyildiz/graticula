# Architecture Debt Register

Temporary architecture must not silently become permanent (§62).

Every entry records what was compromised, why it was acceptable at the time, and
**the observable condition that makes it unacceptable**. An entry without a
trigger is not debt — it is an undocumented permanent decision wearing a
disguise.

---

| ID | Debt | Taken on | Why it was acceptable | Trigger to repay | Cost if unpaid | Status |
|---|---|---|---|---|---|---|
| D-06 | **Dependency licensing is deliberately unexamined.** Every row in [DEPENDENCY-LICENSES.md](../DEPENDENCY-LICENSES.md) is `UNVERIFIED`, including the two the contradiction sweep flagged as newly sharper: the Oracle driver, and the MySQL driver where Oracle's `MySql.Data` is GPLv2-with-exception and cannot be sublicensed under our Apache-2.0 outbound while the community `MySqlConnector` is MIT and can. | 2026-08-13 | **Owner decision, and correct for the phase.** Licensing constrains what we may *ship*, not what we may *decide*, and nothing ships in Phase 0. Verifying now would be work done against a dependency list that is still moving — three providers and a Python runtime were added the same day. | **Before the first binary that bundles a database driver, a GDAL build or a Python wheel set.** Not before the first line of code, and not at the end of Phase 0 | Apache-2.0 warrants to every downstream user that they may redistribute freely. Shipping something we may not redistribute breaks that warranty **for everyone who forks us**, and a fork cannot detect it. Discovered late, the fix is not a licence swap but a packaging change — drivers become customer-supplied, which is a Q-71 consequence. | **OPEN — deliberately deferred** |
| D-05 | ~~A-019 is validated only on PostGIS.~~ **Rewritten 2026-08-12 by Q-67 and now much smaller: no engine except PostGIS serves tiles, so the tile half of this debt is gone. What remains is that the *feature* path — the day-one workload — has never been measured on SQL Server or Oracle at all.** | 2026-08-12 | Neither engine is installed on the development machine and the owner has declined to install them. Deferring is the honest option; the alternative was to leave a benchmark permanently listed as "next" and never run. The PostGIS result is still informative — it bounds our own CPU and allocation cost, which is engine-independent — but it says nothing about the two engines' WKB output, driver materialisation cost, or spatial index selectivity. | **Before any commitment is made that Oracle or SQL Server layers serve tiles at parity.** Concretely: before ADR-008 §4.8's tile path is called decided, or before the multi-database claim appears in user-facing material. **Repayment path found 2026-08-12**, from Honua's dependency manifest ([research/honua-server.md](research/honua-server.md) §1a): `Testcontainers.MsSql` and `Testcontainers.Oracle` start both engines as throwaway containers from the test run itself. Nothing is installed and nothing persists. That removes the stated obstacle — what remains is only machine headroom, and the Oracle image is the heavy one. | Reduced by Q-67 but not eliminated. Registered Oracle and SQL Server layers still serve features, still return WKB into an NTS graph, and A-037's allocation ceiling was measured on exactly that machinery. If the feature path has the same ceiling on those engines — and there is no reason yet to think it does not — then the multi-database promise has an unmeasured limit on the workload the product leads with. | **OPEN, narrowed 2026-08-12** — tile measurement no longer required; feature-path measurement now required |
| D-01 | ~~RLS delegation and per-data-source pooling are incompatible.~~ | 2026-08-12 | Found by fresh-challenger review G5 | — | — | **RESOLVED same day** — [security.md](security.md) §2. Our authorization is the baseline and was always going to exist; RLS delegation becomes an opt-in provider capability using transaction-scoped identity switching, so pools do not fragment. |
| D-02 | ~~Tenant identity is not part of the cache key.~~ | 2026-08-12 | Found by G5 | — | — | **RESOLVED same day** — [security.md](security.md) §3. Authorization splits into pre-lookup (uniform) and in-key (varies), and the key carries a grant fingerprint rather than a principal, so sharing survives where it is safe. |
| D-03 | Capability reports and detailed refusals disclose provider type and internal topology to any client that can reach a layer. | 2026-08-12 | Designed for usability without an authorization dimension. Found by G7. | Before the capability report ships | Reconnaissance surface; reveals an organisation's internal database topology | **Rule stated** in [security.md](security.md) §5 — detail is authorization-scoped. Open until implemented. |
| D-04 | **Multi-tenant resource isolation is not designed.** One tenant's expensive query degrades another's in a shared request worker; §49's limits are per service, not per tenant. | 2026-08-12 | Raised by G5, not addressed by the D-01/D-02 resolution | Before multi-tenant deployments are supported | A noisy tenant becomes an availability problem for everyone on the worker | OPEN |

---

## What belongs here

- A decision taken for schedule reasons that we already believe is wrong.
- A simplification that is correct at the current scale and known to break at a
  larger one.
- A dependency adopted knowing it must be replaced.
- A missing capability that downstream design is quietly assuming exists.
- A criticism from adversarial review that was accepted as valid but deferred
  (§85 requires every material criticism to be resolved *or documented* — this
  register is the "or documented" half).

## What does not belong here

- Deliberate scope decisions. Clustering is deferred by design (§79), not owed.
  It lives in [architecture-completeness.md](architecture-completeness.md).
- Unanswered questions. Those go in [open-questions.md](open-questions.md).
- Unvalidated assumptions. Those go in
  [architecture-assumptions.md](architecture-assumptions.md).

## Review cadence

Reviewed at every phase gate (§65). At each review, for every open entry, ask
whether the trigger has fired — and whether the entry is still honest, or has
quietly become the permanent architecture while nobody was looking.
