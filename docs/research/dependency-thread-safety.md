# Dependency Thread Safety — GDAL, GEOS, PROJ

**Status:** FIRST PASS — resolves the blocking part of **A-013**. Version-specific
claims must be re-checked against the exact versions we ship.
**Blocks:** [ADR-007](../adr/ADR-007-service-runtime.md) — cannot be decided
until this is settled
**Also feeds:** [ADR-003](../adr/ADR-003-geometry-engine.md),
[ADR-009](../adr/ADR-009-raster-engine.md), [ADR-001](../adr/ADR-001-core-language.md)

---

## 1. Why this blocks the runtime decision

QGIS Server's entire process architecture — mandatory multiprocessing, per-process
project caches, the documented cache-fragmentation problem — follows from one
library-level fact: its classes are not thread safe
([runtime-models-compared.md](runtime-models-compared.md) §2.2).

A Tier 2 dependency dictated the runtime model of a whole product. If our
dependencies impose a similar constraint, ADR-007 must be designed around it
rather than discover it during implementation.

## 2. The answer, in one line each

| Library | Threading model | Verdict |
|---|---|---|
| **GEOS** | Reentrant C API. One `GEOSContextHandle_t` per thread. | **Threadable.** Clean, explicit, designed for it. |
| **PROJ** | One `PJ_CONTEXT` per thread. Global error state must be avoided. | **Threadable** with discipline. |
| **GDAL** | Re-entrant, *not* thread safe per instance. One `GDALDataset` per thread. | **Threadable with a real constraint** — see §5. |

**None of them forces us into a QGIS-style process-per-worker model.** A threaded
worker is viable. That was the question A-013 needed to answer, and the answer is
yes.

But the constraints are not free, and GDAL's shapes the raster design.

## 3. GEOS

The reentrant C API exists precisely for this. Per GEOS RFC 3, static state was
moved into a handle: "all static variables are placed into a 'handle' which is
initialized on the initGeos call, and once initialized it is passed to all
subsequent GEOS functions, allowing each thread to have its own copy of the
data." Functions carry the `_r` suffix by C convention.

Usage rule: call `GEOS_init_r()` per thread; "each thread that will be running
GEOS operations should create its own context prior to working with the GEOS
API."

The one hard constraint: **"Contexts must only be used from a single thread at a
time."** A context is not a shared resource — it is thread-local state with a
handle attached.

**Design consequence.** The `IGeometryEngine` port
([build-vs-adopt-policy.md](../build-vs-adopt-policy.md) Tier 2) must own
per-thread context lifecycle internally and never expose it. Callers must not
know contexts exist. If a worker uses a thread pool, contexts bind to pool
threads, not to requests — and that binding must survive across requests, or we
pay context creation on every operation.

`VERIFY` the cost of `GEOS_init_r()`. If it is expensive, context pooling becomes
a design element rather than an implementation detail.

## 4. PROJ

Same shape: "It is recommended to create one threading-context per thread used by
the program, which ensures that all PJ objects created in the same context will
be sharing resources such as error-numbers and loaded grids."

Two documented hazards:

1. **Global error state.** "the global `pj_errno` variable is shared between
   threads and makes it essentially impossible to handle errors safely." The
   context-scoped accessor must be used instead. `VERIFY` the modern equivalent
   in current PROJ (the quoted text is from the older API generation).
2. **Grid cache.** "the datum shift using grid files uses globally shared lists
   of loaded grid information", protected by an internal mutex since 4.7.0.
   Safe, but *shared and locked* — so heavy concurrent grid-based
   transformation is a **contention point**, not a correctness problem.

`VERIFY` whether a `PJ` transformation object can be used from more than one
thread. The documentation reviewed states the per-context rule but does not
state this explicitly, and it matters: if a `PJ` is thread-affine, we cannot
cache one prepared transformation and share it across a pool — we need one per
thread, multiplying memory by thread count for a platform that will hold many
CRS pairs warm.

**This is the single most important open item in this note.** Reprojection is on
the hot path for both tiles and rendering, and CRS transformation objects are
exactly the kind of expensive-to-build, cheap-to-reuse resource whose sharing
rules determine cache design. Recorded as Q-23.

## 5. GDAL — the real constraint

GDAL's rule is precise and stricter than "thread safe or not":

> "All GDAL public C functions and C++ methods are re-entrant, except"
> initialization and cleanup. **Each thread must use its own data instance.**

Specifically:

- "you should not call simultaneously GDAL functions from multiple threads on the
  same data instance, or even instances that are closely related through
  ownership relationships."
- "it is not safe to call concurrently GDAL functions on different
  `GDALRasterBand` instances owned by the same `GDALDataset` instance (each
  thread should instead manipulate a distinct `GDALDataset`)."
- The same applies to `OGRLayer` objects owned by one `GDALDataset`.
- Reason given: implementations "are stateful… performs seek/read operations on
  it, thus not allowing concurrent access. Block cache related structures… are
  not thread-safe."
- Initialization and cleanup "should not be called concurrently from several
  threads, and it is general best practice to call them from the main thread."
- Concurrent *reads* of multiple datasets work, but "performance issues may arise
  when writing several datasets from several threads, due to lock contention in
  the global structures."

### 5.1 The ownership rule is the trap

The dangerous clause is *"or even instances that are closely related through
ownership relationships"*. Two threads working on two different bands of the same
file is unsafe. Two threads on two different layers of the same GeoPackage is
unsafe.

The naive design — cache one open `GDALDataset` per data source and share it
across a worker's threads — is **wrong**, and wrong in the worst way: it will
mostly work, and fail intermittently under load with corrupted reads rather than
clean errors.

**Design consequence.** The raster provider cannot cache datasets per source. It
must cache them **per source per thread**, or serialise access behind a lock, or
use a checked-out-dataset pool. Each of those is a different memory and latency
profile, and the choice belongs in ADR-009.

### 5.2 GDAL 3.10 offers a way out

`VERIFY` RFC 101 (GDAL 3.10) adds read-only raster thread safety: open with
`GDAL_OF_RASTER | GDAL_OF_THREAD_SAFE`, or wrap an existing dataset with
`GDALGetThreadSafeDataset()`, which "accepts a (generally non thread-safe) source
dataset and returns a new dataset that is a thread-safe wrapper around it".

Reported behaviour: "similar scalability as the default mode of opening a dataset
in each thread, sometimes with a slightly decreased efficiency, but not in a too
problematic way."

Two limits that matter:

- **Raster only.** Vector/OGR gets nothing from this. Our file-based vector
  providers (GeoPackage, FlatGeobuf, Shapefile) still need per-thread datasets.
- **Read-only only.** Any raster write path — cache seeding, format conversion,
  overview building — remains under the original rules.

This is genuinely useful and it makes a threaded raster read path viable without
per-thread dataset duplication. It also creates a **hard minimum GDAL version**
if we depend on it, which is a real constraint for air-gapped and
distribution-packaged deployments where the system GDAL may be older. Recorded
as Q-24.

## 6. What this settles, and what it changes

**A-013 is resolved for the blocking question.** No dependency forces
process-per-worker. A threaded worker model is available, so ADR-007 can
evaluate options 1–3 on their merits rather than having option 3 forced on it.

**Three new design constraints, all belonging to the port layer:**

1. **Per-thread context lifecycle is a port responsibility.** GEOS contexts and
   PROJ contexts must be managed inside `IGeometryEngine` and `ICrsTransformer`,
   bound to pool threads, invisible to callers. This is a concrete requirement on
   the Tier 2 seam that the build-vs-adopt policy demanded but did not specify.
2. **GDAL datasets are thread-affine resources**, not shareable cache entries.
   This shapes the raster provider and belongs in ADR-009. It is the most likely
   place for a subtle, load-dependent correctness bug in the whole platform.
3. **Two contention points exist even when everything is correct**: PROJ's
   mutex-protected grid cache, and GDAL's global block cache under concurrent
   writes. Both are throughput questions for the benchmark suite, not
   correctness questions.

**Effect on the L1 cache inventory.** [ADR-010](../adr/ADR-010-caching.md)'s
starting list (borrowed from GeoServer: store connections, feature type
definitions, external graphics, fonts, CRS definitions) now needs a thread
dimension. "CRS definitions" may not be a single shared cache entry — pending
Q-23, it may need to be per-thread, which multiplies its cost by pool width.

**Effect on ADR-001.** Marginal but real: a language with cheap thread-local
storage and deterministic resource cleanup makes per-thread context lifecycle
easier to get right. Not decisive — every candidate can do it — but the
ergonomics differ, and this is exactly the kind of friction the language
prototype should record honestly.

## 7. Open items

| # | Question | Why it matters |
|---|---|---|
| Q-23 | Can a `PJ` transformation object be used from more than one thread, or is it thread-affine? | Decides whether prepared transformations can be shared or must be duplicated per thread. Hot path for tiles and rendering. |
| Q-24 | Do we require GDAL ≥ 3.10 for `GDAL_OF_THREAD_SAFE`? | A hard minimum version constrains air-gapped and distro-packaged deployments. |
| — | `VERIFY` the cost of `GEOS_init_r()` and `proj_context_create()` | If expensive, context pooling is a design element, not a detail. |
| — | Thread-safety of any additional Tier 2 choice (Skia, DuckDB, the rasterisation backend) | Same question, not yet asked of those. DuckDB in particular, if P3 proceeds. |

## Sources

- [GDAL — Multi-threading](https://gdal.org/en/stable/user/multithreading.html)
- [GDAL RFC 101 — Raster dataset read-only thread-safety](https://gdal.org/en/stable/development/rfc/rfc101_raster_dataset_threadsafety.html)
- [GEOS RFC 3 — Thread Safe CAPI](https://libgeos.org/project/rfcs/rfc03/)
- [GEOS — C API Programming](https://libgeos.org/usage/c_api/)
- [PROJ 8.0 — Threads](https://proj.org/en/8.0/development/threads.html)
- [PROJ 7.2 — Threads](https://proj.org/en/7.2/development/threads.html)
