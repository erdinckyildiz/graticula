# Benchmarks

**Status:** STUB — not written
**Required by:** §58

---

Realistic, repeatable benchmarks. When agents disagree about a measurable
question, the disagreement is settled here rather than by argument (§57).

Every benchmark records: dataset, hardware, configuration, methodology, and raw
numbers. Measure throughput, p50, p95, p99, CPU, memory, and recovery time where
relevant (§58).

A benchmark that cannot be reproduced from what is written down is not evidence.

Planned:

| Benchmark | Question | Settles |
|---|---|---|
| `feature-query/` | Feature query throughput and streaming behaviour at scale | ADR-008 |
| `mvt-generation/` | **Now the primary experiment.** Does in-process MVT encoding meet latency targets at all (**A-019**, load-bearing)? Then `ST_AsMVT` versus our encoder, and what a tile costs from SQL Server and Oracle. Inherited the methodology from the superseded `experiments/lang-slice` | ADR-008, **A-019**, A-021 |
| `tile-seeding/` | How long does seeding a realistic service set take per provider, and what does invalidation cost? | ADR-010, A-020 |
| `geometry-hotpath/` | Cost of library overhead and FFI on tile-path primitives | ADR-003, A-004 |
| `worker-model/` | Memory, cold start and interference across runtime models | ADR-007 |
| `duckdb-compute-layer/` | Is an in-process compute engine worth it, and does reading PostGIS through DuckDB cost too much? | ADR-008, Q-19 |
| `connection-budget/` | Does the budget hold at 1,000 services on each of the three providers, and what do shrink-to-zero pools cost in cold latency? **Plus Q-103, which needs the same run:** where does a fixed admission cap have to sit to keep the queue off the client, and does an adaptive controller beat a fixed cap on the same load? [feature-query](feature-query/RESULTS.md) §3b already has the curve — peak at concurrency 8, then falling throughput with linearly growing latency — but §3b.3 could not separate the database from TLS, per-request authentication and the generator, and **this run is not evidence about admission until it can**. That separation is the first requirement on it, not a caveat to add afterwards | ADR-007 §4.8, Q-04, Q-103 |
| `rendering/` | Rasterisation backend throughput | ADR-004 |
