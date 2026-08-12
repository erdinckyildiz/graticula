# Experiment Environment

**Not production infrastructure.** This exists to unblock experiments and
benchmarks under Phase 0 (§56). Everything here is disposable and none of it is
a deployment recommendation.

---

## Why this exists

As of 2026-08-12 the repository held 48 documents, 9,717 lines, **zero lines of
code and zero measurements**, with 28 untested assumptions — five of them
load-bearing.

Every experiment and benchmark registered in `experiments/` and `benchmarks/`
was blocked on the same thing: a PostGIS instance and a realistic dataset. This
directory is that unblocking.

## What is here

| | |
|---|---|
| `docker-compose.yml` | PostGIS 16 / 3.4, plus a GDAL container so `ogr2ogr` need not be installed on the host |
| `data/` | Dataset staging. **Git-ignored** — spatial data is fetched, never committed |

PostGIS is on host port **55432**, deliberately not 5432, so it cannot collide
with anything real on the machine.

## Bring it up

```bash
cd experiments/_env
docker compose up -d
docker compose exec postgis psql -U gis -d gis -c 'select postgis_full_version();'
```

Connection string for the prototypes:

```text
Host=localhost;Port=55432;Database=gis;Username=gis;Password=gis
```

## The dataset requirement

From [`../lang-slice/README.md`](../lang-slice/README.md) §4, and it is not
negotiable: **real data, not synthetic.** Synthetic uniform data makes every
implementation look good and hides exactly the behaviour we need to see.

Needed:

- a **polygon** layer, at least one million features, with realistic and
  irregular vertex counts — building footprints or administrative boundaries;
- a **point** layer of similar cardinality;
- a **line** layer with long, high-vertex geometries — roads or hydrography,
  where clipping and simplification actually cost something.

Indexed as we would index in production (GiST), `ANALYZE`d, and identical across
every run.

Record the exact source, extract date, feature counts and total vertex counts in
`data/DATASET.md`. A benchmark whose dataset cannot be reproduced from what is
written down is not evidence.

## Deliberate limits

- **Postgres tuning is minimal and explicit** in the compose file. Enough that
  numbers are not dominated by an untuned default; not so much that they reflect
  a tuning exercise rather than the code under test.
- **One instance, one machine.** Multi-node is deferred (ADR-012) and nothing
  here anticipates it.
- **No SQL Server or Oracle yet.** Both are first-class providers (Q-50a) and
  both need their own environment. `benchmarks/mvt-generation` cannot answer
  A-019 for those engines until they exist, and that is a known gap in the first
  round of measurement.
