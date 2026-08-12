# Architecture Debt Register

Temporary architecture must not silently become permanent (§62).

Every entry records what was compromised, why it was acceptable at the time, and
**the observable condition that makes it unacceptable**. An entry without a
trigger is not debt — it is an undocumented permanent decision wearing a
disguise.

---

| ID | Debt | Taken on | Why it was acceptable | Trigger to repay | Cost if unpaid | Status |
|---|---|---|---|---|---|---|
| D-01 | **Row-level security delegation and per-data-source connection pooling are incompatible as written.** RLS depends on session identity; a pooled connection shared across services and users either carries one identity, defeating RLS, or resets it per request at a cost nobody has measured. | 2026-08-12 | Neither decision mentioned the other. Found by fresh-challenger review G5. | **Blocking** — must be resolved before either ADR-007 §4.8 or the §8 RLS-delegation takeaway is implemented | Authorization that silently does not apply. A security failure, not a performance one. | **OPEN — blocking** |
| D-02 | **Tenant identity is not part of the cache key.** ADR-010 keys on plan identity plus schema fingerprint. Two principals with different authorization can produce the same plan against the same layer. | 2026-08-12 | Oversight. Found by G5. | Before any caching is implemented | A cache hit becomes a data breach. One line to fix now, severe if forgotten. | **OPEN — blocking** |
| D-03 | Capability reports and detailed refusals disclose provider type and internal topology to any client that can reach a layer. | 2026-08-12 | Designed for usability without an authorization dimension. Found by G7. | Before the capability report ships | Reconnaissance surface; reveals an organisation's internal database topology | OPEN |

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
