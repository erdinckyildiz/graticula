# Architecture Debt Register

Temporary architecture must not silently become permanent (§62).

Every entry records what was compromised, why it was acceptable at the time, and
**the observable condition that makes it unacceptable**. An entry without a
trigger is not debt — it is an undocumented permanent decision wearing a
disguise.

---

| ID | Debt | Taken on | Why it was acceptable | Trigger to repay | Cost if unpaid | Status |
|---|---|---|---|---|---|---|
| D-05 | **A-019 is validated only on PostGIS — the one engine that does not need the path it validates.** In-process MVT encoding exists because `ST_AsMVT` is absent from SQL Server and Oracle. It has been measured against neither. | 2026-08-12 | Neither engine is installed on the development machine and the owner has declined to install them. Deferring is the honest option; the alternative was to leave a benchmark permanently listed as "next" and never run. The PostGIS result is still informative — it bounds our own CPU and allocation cost, which is engine-independent — but it says nothing about the two engines' WKB output, driver materialisation cost, or spatial index selectivity. | **Before any commitment is made that Oracle or SQL Server layers serve tiles at parity.** Concretely: before ADR-008 §4.8's tile path is called decided, or before the multi-database claim appears in user-facing material. Repayable in Docker — SQL Server 2022 Developer and Oracle Database Free both ship images — on a machine with headroom. | The multi-database promise is the product's main differentiator and rests on one untested inference. If either engine is materially slower, ADR-010's seeding becomes mandatory rather than an optimisation and ADR-001's weighting shifts again. Discovering that after implementation is the expensive order. | **OPEN — accepted deliberately, 2026-08-12** |
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
