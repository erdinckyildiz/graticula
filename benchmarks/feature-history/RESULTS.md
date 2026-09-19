# What keeping a layer's history costs a write — Results

**Run:** 2026-09-19. **Settles:** [ADR-078](../../docs/adr/ADR-078-a-hosted-layer-can-keep-its-history.md) §4a.
**Script:** [`bench.py`](bench.py).

## Environment

| | |
|---|---|
| Host | Windows 11, the development machine |
| Server | Graticula built from the ADR-078 worktree, Debug, on 127.0.0.1:18472 |
| Database | PostgreSQL 16.10 (portable, native Windows), PostGIS 3.6.2 — datastore and platform store in one database |
| Layer | `hosted/ci_many` from `tools/seed-conformance.py`: 600 polygons, five attributes |
| Method | the same `applyEdits` batches with history off, then on: 500 attribute updates in one batch; 500 adds in one batch, then those 500 deleted in one batch. Two warm-ups, seven timed, median, end to end over HTTPS |

## Results

### The first trigger — one shared function, statements built with `format` and `execute`

| 500 features in one applyEdits | history off | history on | ratio |
|---|---|---|---|
| updates | 274 ms | 561 ms | **2.05×** |
| adds | 518 ms | 495 ms | 0.96× |
| deletes | 331 ms | 365 ms | 1.10× |

### The trigger as built — one function per table, static statements

Two runs.

| 500 features in one applyEdits | history off | history on | ratio |
|---|---|---|---|
| updates | 286 / 304 ms | 416 / 429 ms | **1.45× / 1.41×** |
| adds | 491 / 381 ms | 463 / 458 ms | 0.94× / 1.20× |
| deletes | 296 / 223 ms | 325 / 360 ms | 1.10× / 1.61× |

## Findings

1. **An update is the write history costs, and it costs about 40 %.** An update closes the current
   version and opens another — two statements against the history per row, on top of the row's own. An
   add opens one version and a delete closes one, and their differences sit inside the run-to-run noise
   of this machine (the *off* column alone moved from 381 to 491 ms between runs).
2. **The first design doubled the update, and the reason was planning, not work.** PL/pgSQL plans an
   `execute` afresh on every row; a function with the table's names written into it keeps its plans for
   the session. Same statements, same indexes: 2.05× became 1.43×. ADR-078 §10's revisit trigger —
   *more than twice the unarchived write for a 1,000-feature applyEdits* — was met by the first version
   and is not by the second.
3. **What this does not say.** One machine, one layer of 600 polygons, one database for both stores.
   ADR-078 condition 1 is the same measurement on the showcase's arm64 datastore.
