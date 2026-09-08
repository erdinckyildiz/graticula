# Does quiescing let the DBA's DDL through? — ADR-059 condition 1

**Run 2026-09-08** against a local fixture: Graticula on 127.0.0.1:8451, PostgreSQL
16.4 / PostGIS 3.4.3 in Docker on the same machine. Script:
[ddl-under-load.py](ddl-under-load.py). Raw output: [measured.json](measured.json).

**The condition asks whether refusing requests actually frees the lock**, because
those are different things and
[ADR-059](../../docs/adr/ADR-059-quiescing-a-data-source.md) §5c had already been
caught claiming the wrong mechanism once.

**The answer is that on this server, as built, a DBA's `ALTER TABLE` is almost
never blocked by us in the first place** — so quiesce guarantees a window that
usually exists anyway. That is not the answer this benchmark was written
expecting, and it is the one the numbers give.

---

## What blocks `ALTER TABLE`, from first principles

[ADR-007](../../docs/adr/ADR-007-service-runtime.md) §4.8 states the rule and this
run measures it, on a purpose-made table:

| What is held while the DDL arrives | `ALTER TABLE` |
|---|---|
| An open connection with no transaction — **what a pool holds** | **0.410 s**, completes |
| A connection idle **in a transaction** that has touched the table | **5.262 s**, `lock_timeout` fires |

So closing a pool of idle connections frees nothing. Only a transaction holding
the table blocks, and §4.8's discipline — short statements, never idle in
transaction — is precisely about not having one.

## Under concurrent load, not quiesced

Eight threads querying one layer continuously while the DDL is issued.

| Layer | Page | Requests during the window | `ALTER TABLE` |
|---|---|---|---|
| `ci_buildings`, 8 features | 1,000 | 1,612 answered | **0.394 s**, got the lock |
| `zzzload`, 200,000 polygons | 50,000 | 8 answered | **0.526 s**, got the lock |

**The DDL was never blocked.** Each statement holds `ACCESS SHARE` for its own
duration and lets go; between statements the connection is idle with no
transaction, which does not block. Eight threads leave gaps, and the DDL takes
the first one.

## Under a throttled reader, which is the case that should have blocked

**This is Q-37's own construction** — a client reading a large response slowly, at
about 200 kB/s, so the response is still streaming when the DDL arrives.
[D-144](../../docs/architecture-debt.md) measured a request holding its permit for
minutes exactly this way.

| | |
|---|---|
| Reader still streaming when the DDL was issued | **yes** |
| `ALTER TABLE` | **0.27 s**, got the lock |

**And [Q-37](../../docs/open-questions.md) already explained why, on 2026-08-26.**
Sampled at the moment a DDL was issued during a 42 MB throttled response, the
backend was **`idle`**: the rows had been read out of PostgreSQL before the first
byte reached the client. The server streams from its own buffer, not from an open
cursor, so a slow client holds *our* memory and *our* permit — but not the
table's lock.

## What this means for quiesce

**Three things, and only the first is comfortable.**

1. **The discipline works.** §4.8's rules were written to stop us blocking a DBA,
   and they do. Nothing this server issues through any face holds `ACCESS SHARE`
   long enough to matter — which
   [benchmarks/statement-timeout](../statement-timeout/RESULTS.md) reached from the
   other direction: the most expensive statement reachable through any face over a
   million rows is a full-extent render at 330 ms.

2. **So quiesce is insurance rather than a repair.** ADR-059 §1 presents it as
   fixing a measured problem, and the problem it names — *the DBA runs the DDL and
   hopes no request is mid-read* — is a race the DBA wins essentially every time
   today. The value that survives is narrower and worth stating plainly: an
   operator can **guarantee** a clear window rather than relying on one, and can
   show a DBA an empty `pg_stat_activity` rather than asking them to trust it.

3. **The case it is genuinely for has not been built yet.** RLS delegation
   (A-036, §4.8) runs inside an explicit transaction that holds `ACCESS SHARE` for
   the whole stream. *That* is a reader which blocks DDL for as long as a slow
   client takes, and it is the shape every measurement above failed to produce
   because this server does not do it yet. When it does, quiesce stops being
   insurance.

**What this does not change:** the refusal, the deadline, the node-local limit and
the pool close are all still right, and the second of those is the one that
matters most in practice — a feature that could leave a service down until somebody
noticed would be worse than the problem either way.

**What it should change** is how ADR-059 describes itself, and it does: §1 now
says what is measured rather than what was assumed, and the confidence on the
premise is `LOW` where the confidence on the mechanism stays `HIGH`.

---

## Reproducing

```sh
python benchmarks/quiesce/ddl-under-load.py \
  --url https://127.0.0.1:8451 --user ci --password '...' \
  --layer hosted/zzzload --table hosted.zzzload \
  --source <datastore id> --threads 8 --page 50000
```

The 200,000-polygon table was made from an OSM extract already in the fixture
database and dropped afterwards; nothing here depends on it existing.
