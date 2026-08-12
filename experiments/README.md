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
| `_env/` | Local PostGIS and GDAL for every experiment and benchmark. Disposable | unblocks everything measurable |
| ~~`lang-slice/`~~ | **SUPERSEDED, not run.** The two-language comparison was dropped deliberately; ADR-001 decided .NET on paper analysis. Effort moved to absolute measurement in `benchmarks/` | see its README for why |
| `geometry-oracle/` | Correctness corpus with the adopted engine as oracle. **Scope set by [geometry-crs-policy.md](../docs/geometry-crs-policy.md):** real pathological data — self-intersecting rings, wrong SRIDs, mixed types, Z/M, curves, oversized features — not synthetic adversarial data. Must also cover the three engines' *differing definitions of validity* | ADR-003, Q-20, G4 |
| `worker-isolation/` | What does process isolation actually cost per worker? | ADR-007, A-007 |
| `affinity-routing/` | Does warmth-aware routing beat blind routing, and where does it break under skew? | ADR-007, ADR-010, A-014 |
