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
| `mvt-generation/` | `ST_AsMVT` versus our own encoder, and what a tile costs from SQL Server and Oracle. Decides whether a second code path is worth keeping | ADR-008, A-019, A-021 |
| `tile-seeding/` | How long does seeding a realistic service set take per provider, and what does invalidation cost? | ADR-010, A-020 |
| `geometry-hotpath/` | Cost of library overhead and FFI on tile-path primitives | ADR-003, A-004 |
| `worker-model/` | Memory, cold start and interference across runtime models | ADR-007 |
| `duckdb-compute-layer/` | Is an in-process compute engine worth it, and does reading PostGIS through DuckDB cost too much? | ADR-008, Q-19 |
| `connection-budget/` | Does the connection budget hold at 1,000 services? | ADR-007, Q-04 |
| `rendering/` | Rasterisation backend throughput | ADR-004 |
