# Experiments

**Status:** STUB — not written
**Required by:** §56

---

Code here answers architectural questions. It is **not** production code and is
never promoted to production (§56). If an experiment validates an approach, the
production implementation is written fresh.

Every experiment states, in its own README: the question it answers, the method,
the result, and which ADR or assumption it settles. An experiment that decides
nothing should not have been run.

Planned:

| Directory | Question | Settles |
|---|---|---|
| `lang-slice/` | Which language, measured on the same vertical slice? | ADR-001, A-001 |
| `geometry-oracle/` | Correctness corpus with the adopted engine as oracle | ADR-003, and any future own-engine proposal |
| `worker-isolation/` | What does process isolation actually cost per worker? | ADR-007, A-007 |
| `affinity-routing/` | Does warmth-aware routing beat blind routing, and where does it break under skew? | ADR-007, ADR-010, A-014 |
