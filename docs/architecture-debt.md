# Architecture Debt Register

Temporary architecture must not silently become permanent (§62).

Every entry records what was compromised, why it was acceptable at the time, and
**the observable condition that makes it unacceptable**. An entry without a
trigger is not debt — it is an undocumented permanent decision wearing a
disguise.

---

| ID | Debt | Taken on | Why it was acceptable | Trigger to repay | Cost if unpaid | Status |
|---|---|---|---|---|---|---|
| | | | | | | |

*(empty — Phase 0 has not made any implementation compromises yet)*

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
