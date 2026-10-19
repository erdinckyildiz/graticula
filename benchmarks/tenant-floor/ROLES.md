# ADR-204 condition 9 — the server served as a role row-level security binds

**Run 2026-10-19** on this machine, PostgreSQL 16 + PostGIS 3.6 (port 55499), against this branch's build.
The fixture recipe is CI's — `seed-conformance.py`, `ci-corpus.sql`, `ci-free-tables.sql` — in a throwaway
database. Disposable, like everything under `/benchmarks`.

[ADR-204](../../docs/adr/ADR-204-one-deployment-holds-several-organisations.md) condition 9:

> The server connects as a role that is neither a superuser nor `BYPASSRLS`, in `compose.yaml`, the
> deployment guide and the showcase, and a startup check refuses to serve more than one tenant over a
> connection whose role `rolsuper` or `rolbypassrls` says the floor does not bind it.

The question this answers is the one before any of that: **does the product work as such a role at all?**
The prototype ([RESULTS.md](RESULTS.md)) found the floor inert under a superuser; nothing had ever run this
server as anything else.

## The role

`gt_app`: `LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE`, owner of its database. PostGIS created once
by a superuser, as the datastore image's init script does. Nothing else granted.

## 1. A deployment built as that role — the product needs nothing more

| Suite | As the superuser `gis` | As `gt_app` | Different |
|---|---|---|---|
| `Graticula.Platform.Postgres.Tests`, `Needs!=RealCorpus` | 11 failed / 526 passed | 16 failed / 521 passed | **5**, all `42501: permission denied to drop role` — tests that make a role of their own to test a restricted credential. The fixture's need, not the product's |
| `Graticula.Conformance.Tests`, a whole server built from nothing and seeded | 10 failed / 853 passed | 10 failed / 853 passed | **none** — the same ten, all environmental (a seed without a stored password, the local PROJ) |

Feature history's triggers and functions, the importer, `ALTER` under a lock, hosted tables, the recycle bin,
exports, tiles: everything the suites drive works as the database's owner.

The first Postgres run had **48** different, and that was the harness: both roles shared one database, the
superuser's run had created `hosted`, and the owner could not use a schema somebody else owned. On a fresh
database the 48 became the 5 above.

## 2. Backup and restore — restore did not work, and does now

| Step | As `gt_app` alone | With `Graticula:MaintenanceStore` (a superuser) |
|---|---|---|
| `pg_dump` | works | works |
| `pg_dumpall --roles-only` | refused (`pg_authid`), tolerated as before — no `roles.sql` | **works** — read through the maintenance connection |
| `create database` | `42501: permission denied to create database` | works, **owned by `gt_app`** |
| restore with `CREATEDB` granted | the dump's `create extension postgis` needs a superuser, every geometry table failed, and the check refused: *8 hosted layer(s) name a table that is not in it. The current database is untouched.* | PostGIS created by the maintenance connection first; `pg_restore` runs as `gt_app`; **restored**, 8 hosted and 55 platform tables, every one `gt_app`'s |

The refusal is the product working: ADR-193's check caught the half-restored database and changed nothing.
What changed is `Backups`: with a maintenance store configured, the database-level steps — connections,
create, PostGIS, terminate, rename — go through it, and the dump is still restored by the serving role.
Without one, nothing is different from before.

Two errors `pg_restore` ignores either way: the extension's comment, and `spatial_ref_sys`'s rows, which only a
superuser may write. **A deployment that added its own rows to `spatial_ref_sys` loses them on a restore by a
role that is not a superuser** — not measured here, because the fixture adds none.

## 3. An existing deployment handed over — `tools/serving-role.sql`

Every deployment shipped until now was built by a superuser and everything in it is that role's. **Not
`REASSIGN OWNED`**: it also hands over cluster-wide objects — every database the old role owns, and the
showcase's cluster holds a development database beside the production one. The script moves the server's
own schemas (`gisserver`, `hosted`, `hosted_*`, `graticula_recycle`) and what is in them, grants `CREATE` on the
database, and nothing else.

Measured on a fixture built and seeded entirely as `gis`, then handed to `gt_mig` and restarted as it, against
the same fixture served as `gis` — the same recipe, the same environment, one after the other:

| | As `gis` (control) | Handed to `gt_mig` |
|---|---|---|
| Conformance | 10 failed / 853 passed | 10 failed / 853 passed, **the same ten** |
| `42501` in the server's log | 0 | 0 |
| Startup warning (1091 when measured; renumbered 1094 because `CoveragePyramids` held 1091) | said, *'gis', which is a superuser* | not said |
| Owner of `cifree` (a table the server did not make) | `gis` | `gis` — untouched; granted, not taken |

Run twice: once with the measuring copy of the script, once with `tools/serving-role.sql` itself. Its guards
were run too: no role named, and a superuser as the target, each stop before changing anything.

**A second pair was run and is not the evidence.** With the test environment exported whole — the first
runs had cut `GRATICULA_TEST_PG` at its first `;` — 243 Conformance tests failed, identically under both
roles: a DDL test takes the datastore out of service for 120 s and the tests after it cascade. It shows no
difference between the roles over the 620 that passed, and it is reported because a reader rerunning this
will meet it.

## What this does not cover

- **The showcase.** Nothing here touched it. Handing it over is a change to a running deployment and the
  owner's to approve.
- **`compose.yaml`.** It still connects as the superuser: a datastore volume made before an init script that
  creates the serving role has no such role, so switching the default would stop every existing deployment
  that pulls the new image. The switch and its upgrade step are a decision, not a measurement.
- **Registered data sources.** Their credentials are their own and unaffected; a registered source whose
  credential is a superuser is a separate question.
- **The startup refusal** the condition names — serving a second organisation over an unbound connection —
  waits for there to be a second organisation. What exists is the warning.
