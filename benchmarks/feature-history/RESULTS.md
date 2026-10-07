# What keeping a layer's history costs a write — Results

**Run:** 2026-09-19 on the development machine; 2026-10-07 on the showcase's arm64 datastore image
(ADR-078 conditions 1 and 6). **Settles:** [ADR-078](../../docs/adr/ADR-078-a-hosted-layer-can-keep-its-history.md) §4a.
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

## On the datastore image — 2026-10-07 (ADR-078 condition 1)

| | |
|---|---|
| Host | the showcase's VPS: 6 × Neoverse-N1 (arm64) at 2.0 GHz, 8 GB, shared with the running showcase |
| Server | `ghcr.io/erdinckyildiz/graticula:1.0.307`, its own compose project, port and volume — the showcase untouched |
| Database | `ghcr.io/erdinckyildiz/graticula-datastore:1.0.307`: PostgreSQL 16.4 on aarch64, PostGIS 3.4.3, one database for both stores as the shipped compose file has it |
| Layer | `hosted/ci_many` from `tools/seed-conformance.py`; for the 1,000-feature batch it was grown to 1,200 polygons by copying its own features with history off |
| Method | as above; the script and the server on the same host, so no network in the number |

Two runs of each.

| applyEdits batch | history off | history on | ratio |
|---|---|---|---|
| 500 updates | 619 / 689 ms | 956 / 861 ms | **1.55× / 1.25×** |
| 500 adds | 861 / 852 ms | 1038 / 1111 ms | 1.21× / 1.30× |
| 500 deletes | 530 / 571 ms | 684 / 724 ms | 1.29× / 1.27× |
| 1,000 updates | 1352 / 1343 ms | 2109 / 2113 ms | **1.56× / 1.57×** |
| 1,000 adds | 1835 / 1755 ms | 2268 / 2444 ms | 1.24× / 1.39× |
| 1,000 deletes | 1279 / 1180 ms | 1557 / 1539 ms | 1.22× / 1.30× |

4. **The ratio travelled; the absolute numbers did not.** The arm64 machine is about twice as slow per
   write as the development machine with or without history, and history costs an update about the same
   share there — 1.25× to 1.57× against 1.41× to 1.45×. An add and a delete cost a little more on the
   arm64 machine (1.2× to 1.4×), where the development machine could not tell them from noise.
5. **ADR-078 §10's revisit trigger is not met.** It is *more than twice the unarchived write for a
   1,000-feature applyEdits*, and the worst of the four 1,000-feature runs is 1.57×.

## A historic read on a million features — 2026-10-07 (ADR-078 condition 6)

Same deployment. `hosted/ci_editable` given 1,000,000 points spread over Türkiye's extent in EPSG:3857,
history turned on, then 100,000 of them moved 500 m and changed through the trigger — 1,100,003
versions. The moment is between the two. The query is a FeatureServer `query` with a 20 km envelope
near Ankara, seven runs after two warm-ups, median, end to end on the same host.

| | today | at the moment, as v1.0.308 builds it | ratio |
|---|---|---|---|
| envelope, `returnCountOnly` | 26 ms | 1,730 ms | 67× |
| envelope, the features | 31 ms | 1,712 ms | 55× |
| no filter, `returnCountOnly` | — | 1,671 ms | |

6. **The filter was not the cost; the rebuild was.** Counting the whole layer at the moment took as
   long as counting a 20 km square of it. The historic relation rebuilds every column from the version's
   JSON, the geometry included, and a filter on the rebuilt geometry is a filter on a computed value: no
   index can answer it, so every version valid at the moment — a million — was rebuilt before one was
   tested.
7. **The same filter put on the history's own geometry, ahead of the rebuild, is the whole repair.**
   The statement the server writes, run in `psql` on the same data, both forms, three runs each:

   | | at the moment, filter after the rebuild | filter also on `h.geom` | today |
   |---|---|---|---|
   | 20 km envelope, count | 1,723 / 1,695 / 1,663 ms | **3.7 / 2.2 / 2.2 ms** | 1.5 / 0.9 / 0.8 ms |

   Same answer — 207 versions, where today has 205 because two points have moved out since. The plan is
   a bitmap scan of the history's GiST index, 249 candidates, 207 rebuilt. The server writes the second
   form from the release after v1.0.308, and `FeatureHistoryTests` holds it to testing the version's
   geometry rather than today's: a feature moved since is found where it was.
8. **A historic read with no spatial filter still rebuilds every version valid at the moment** — 1.7 s
   for a million — and that is what *the layer as it was* costs when the question is the whole layer.
   A filter on attributes alone does not help either; it tests the rebuilt row.
9. **Turning history on for the million failed before any of this was measured.** The copy took longer
   than Npgsql's thirty-second `CommandTimeout`, the request answered 502 and no history was kept,
   although the transaction had already set `statement_timeout = 0` — the server's half of the bound,
   not the client's. Fixed in the same release; with the client's half lifted the copy took **64.6 s**
   for 1,000,003 rows under its lock.

## An ArcGIS client reading a moment — 2026-10-07 (ADR-078 condition 2)

The ArcGIS Maps SDK for JavaScript 4.29, in headless Chrome, against the local fixture: a hosted layer
with two points, history on, then one moved and relabelled and the other deleted.
[`sdk-historic-moment.html`](sdk-historic-moment.html) and its setup script are the whole method.

```text
isDataArchived true
capabilities.query.supportsHistoricMoment true
today: Ankara, after @ 3660000,4860000
layer.historicMoment: Ankara, before @ 3650000,4850000 | İzmir, deleted later @ 3020000,4650000
query.historicMoment: Ankara, before @ 3650000,4850000 | İzmir, deleted later @ 3020000,4650000
count then 2
```

10. **The SDK answered the moment correctly the first time and said it could not.** Against v1.0.308
    `capabilities.query.supportsHistoricMoment` was **false**: the SDK reads it from the layer's
    `archivingInfo.supportsQueryWithHistoricMoment`, which the server did not write, and not from
    `advancedQueryCapabilities`, where it did. A client that checks the flag before offering a time
    control would never have offered one. The layer document now carries `archivingInfo` with
    `startArchivingMoment` — the first version's start — and the run above is after that change.
