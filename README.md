# gis-server

An enterprise GIS application server, designed from first principles and given
away. Apache-2.0.

**Working title.** Final product name: TBD.

---

## Status

**Phase 1 — implementation**, since 2026-08-13. Phase 0's architecture work is in
[`docs/`](docs/); what carried forward as debt rather than completion is listed
in [CLAUDE.md](CLAUDE.md) §1 and tracked in
[architecture-completeness.md](docs/architecture-completeness.md).

**v1 scope** is [docs/v1-scope.md](docs/v1-scope.md) and is authoritative:

> A PostGIS-backed GIS server that speaks ArcGIS — feature services, vector tile
> services and a geometry service — over data that is either hosted in our
> datastore or registered in the customer's own PostGIS.

OGC API Features, the other databases, rendering, geoprocessing and the rest of
the protocol surface are **deferred, not cancelled**; the map is
[protocol-surface.md](docs/protocol-surface.md).

## Project status

```
python tools/status-page.py          # writes docs/status.html
```

Reads the ADR headers, [open-questions.md](docs/open-questions.md),
[architecture-debt.md](docs/architecture-debt.md),
[architecture-assumptions.md](docs/architecture-assumptions.md) and git, and
writes a single self-contained page. **Nothing on it is typed by hand** — the
two versions that were went stale within a day, and a status page that is wrong
is worse than none, because it is most confidently wrong about whatever changed
last. The first run of the generator found five questions still filed as
blocking a phase that had ended.

## Quickstart

Docker and Docker Compose. Nothing else — no .NET SDK, no PostgreSQL, no
`openssl`.

```bash
# 1. A key to seal registered data source credentials. There is no default,
#    deliberately: a published default key would make every credential in
#    every deployment that forgot to change it readable from a backup.
docker compose run --rm --no-deps server keygen
echo "GIS_SECRET_KEY=<the value it printed>" > .env

# 2. Create the schema. Explicit, never automatic — an old image started by
#    accident must not silently rewrite a newer schema (ADR-016 §4b).
docker compose run --rm server migrate --apply

# 3. Start.
docker compose up -d

# 4. The server has no administrator, so it refuses everything except setup and
#    writes a one-time token to its log.
docker compose logs server | grep -A2 "SETUP REQUIRED"

curl -k -X POST https://localhost:8443/rest/setup   -H "Content-Type: application/json"   -d '{"token":"<the token>","name":"root","password":"a properly long password"}'
```

`-k` because the server generates a self-signed certificate on first run and
keeps it in a volume. It survives a container replacement; install a real one
when you have one.

### Publishing something

```bash
TOKEN=$(curl -sk -X POST https://localhost:8443/rest/auth/login   -H "Content-Type: application/json"   -d '{"name":"root","password":"a properly long password"}' | jq -r .token)

# Try a database before registering it. This creates nothing, and it tells you
# which of the three failure classes you have: unreachable, connected but
# unprivileged, or connected but with nothing publishable.
curl -sk -X POST https://localhost:8443/admin/datasources/test   -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json"   -d '{"connectionString":"Host=your-db;Port=5432;Database=gis;Username=...;Password=..."}'

# Register it. The credential is sealed inside the server with the key from
# step 1; it is never written anywhere in the clear, including the audit log.
curl -sk -X POST https://localhost:8443/admin/datasources   -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json"   -d '{"name":"my-postgis","connectionString":"..."}'

# Publish a table from it. Layers are private to their owner until shared.
curl -sk -X POST https://localhost:8443/admin/layers   -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json"   -d '{"name":"places","dataSourceId":"<id>","schemaName":"public",
       "tableName":"places","geometryColumn":"geom","identityColumn":"objectid",
       "objectIdColumn":"objectid","srid":4326,"geometryType":"Point",
       "sharing":"public"}'
```

It is then a FeatureServer at
`https://localhost:8443/rest/services/places/FeatureServer/0`, discoverable from
`/rest/info` the way any ArcGIS client expects.

### What survives

`docker compose down` and `up` keeps everything: the platform database, your
registrations, accounts and sessions, and the serving certificate. Only
`down -v` destroys it.

---

## Building

```bash
dotnet build
dotnet test --filter "Category!=Integration"
```

Integration tests need a PostgreSQL and are excluded by default:

```bash
export GISSERVER_TEST_PG="Host=localhost;Port=55432;Database=gis;Username=gis;Password=gis"
dotnet test --filter "Category=Integration"
```

They **fail rather than skip** when asked for without a database configured. A
test that goes green with its subject absent is worse than no test — this
project has caught three instruments lying already, and each was found by trying
to make it fail rather than by reading it.

## Layout

| | |
|---|---|
| `src/GisServer.Core` | **Tier 1.** Geometry model and domain. No package references, enforced |
| `src/GisServer.Platform` | **Tier 1.** Platform store schema, migrations, the version handshake |
| `src/GisServer.Platform.Postgres` | **Tier 2 adapter.** Where Npgsql is allowed to be |
| `tests/GisServer.Architecture.Tests` | Fails the build if a library reaches Tier 1 |
| `benchmarks/` | **Disposable.** Never promoted; see below |
| `docs/` | ADRs, registers, reviews |

[build-vs-adopt-policy.md](docs/build-vs-adopt-policy.md) §4 governs the tiers:
Tier 1 is written by us always, Tier 2 libraries are permitted only behind our
own port, and no library type may appear in a Tier 1 signature. That last rule is
a build failure, not a convention.

## Benchmarks are specifications, not code to reuse

[`benchmarks/mvt-generation/RESULTS.md`](benchmarks/mvt-generation/RESULTS.md)
holds three measured rounds against 6.5 million OpenStreetMap polygons. The code
there is disposable and is never promoted (CLAUDE.md §1); what carries forward is
the findings, and they shaped the production types directly:

- **Allocation, not CPU, is the ceiling.** 80.9% GC pause at 18% CPU utilisation
  under concurrency. A profiler showing only CPU reports an idle worker and
  explains nothing.
- **A coordinate must not be an object.** The adopted library made a
  556,728-vertex tile into 556,728 heap objects; flat arrays halved a z12 tile's
  allocation from 404 MB to 204 MB.
- **A tile's cost floor is set by the largest geometry overlapping it.** A z16
  tile read 201,580 vertices to emit 2,080, because four administrative polygons
  overlap every tile in the city. Pushdown is structural, not tuning.

## Documents worth reading first

- [v1-scope.md](docs/v1-scope.md) — what is being built
- [architecture-assessment.md](docs/architecture-assessment.md) — the synthesis.
  **Known stale**; see review finding A2
- [open-questions.md](docs/open-questions.md) — uncertainty is recorded, not hidden
- [architecture-debt.md](docs/architecture-debt.md) — temporary compromises, with
  repayment triggers
- [reviews/](docs/reviews/) — including three independent adversarial reviews
  that found 41 issues, 17 severe. §67 still owes a fourth, by someone who did
  not participate

## Licence

Apache-2.0. See [LICENSE](LICENSE), [NOTICE](NOTICE) and
[DEPENDENCY-LICENSES.md](DEPENDENCY-LICENSES.md).
