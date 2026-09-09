# Deployment

**Status:** STUB — not written, apart from §0, §1 and §2. §1 exists because
[ADR-029](adr/ADR-029-affinity-routing-is-not-the-default.md) condition 3
required it before anybody is told to run more than one node. **§0 exists
because on 2026-08-15 CI installed this product against an empty database for
the first time and the first command failed** (D-36) — the sequence below was
nowhere in the repository, and the only place it existed in full was a workflow
file. A workflow is not a manual.
**Required by:** §53

---

Deployment profiles in priority order: developer laptop, single enterprise
server, enterprise cluster, Kubernetes.

Also: installation, configuration, upgrade and rollback, backup and disaster
recovery, monitoring and troubleshooting, Linux and Windows, containers, and the
concrete requirements of air-gapped operation (Q-15).

Kubernetes is addressed only after the platform works correctly without it
(§53, §79).

---

## 0. Installing against an empty database

Four steps, in this order. Every one of them is exercised on every push by
[the conformance job](../.github/workflows/ci.yml), which builds a server from
nothing — so if this drifts, that job goes red rather than this page going
quietly wrong.

**1. A database with PostGIS.**

```sql
CREATE DATABASE gis;
\c gis
CREATE EXTENSION postgis;
```

**2. The schema the platform store lives in.**

```sql
CREATE SCHEMA gisserver;
```

**This is a separate step on purpose and the migrator will not do it for you.**
Creating a schema is a privileged act, and doing it silently would mean a typo
in `SearchPath` produces a second, empty installation rather than an error. If
you skip it, `migrate` says so and exits 1; before 2026-08-15 it threw
`3F000: no schema has been selected to create in`, which is Postgres's way of
saying the same thing and nobody's idea of a first impression.

**3. Configuration.** Two settings are required and the server refuses to start
without either:

| | |
|---|---|
| `Graticula__PlatformStore` | The connection string, including `SearchPath=gisserver` |
| `Graticula__SecretKey` | Base64 of **exactly 32 bytes** — the AES-256 key that seals data source credentials (ADR-002 §4.7). Generate one with `head -c 32 /dev/urandom \| base64`, keep it, and understand that losing it means every stored data source credential is unreadable |

Optional: `Graticula__Port` (8443), `Graticula__Listen` (0.0.0.0),
`Graticula__HostName`, `Graticula__CertificatePath` and
`Graticula__CertificatePassword`, `Graticula__StatePath`,
`Graticula__TileCachePath`.

**`Graticula__MapSdkUrl`** deserves its own line, because it is the one optional setting that
changes this server's **security policy**. It is where the console fetches the ArcGIS Maps SDK
from, defaulting to `https://js.arcgis.com/4.29/`, and its origin is written into the console's
`Content-Security-Policy` — so pointing it at your own copy is one change rather than two, and
an air-gapped deployment ([Q-15](open-questions.md)) can serve the library from inside the
network. It must be an absolute `http`/`https` URL **ending in a slash**; the server refuses to
start otherwise and says which key. Nothing else about the console needs it: the map is the only
thing that does, and everything else works with the library unreachable.

**The former `GisServer__*` names still work**, and a server started on them warns once
at startup naming the keys to move — ADR-032 §5. This is not politeness: `SecretKey`
decrypts every stored data-source credential, so a rename that silently stopped reading
it would take a working server and leave it unable to open its own catalogue, reporting a
*missing* setting rather than a *renamed* one.

**The schema is still called `gisserver`, and that is deliberate.** A schema name is a
deployment's choice rather than the product's identity; renaming it would mean a data
migration on every existing installation in exchange for nothing an operator can see.
Name yours whatever you like — the connection string is the only place it appears.

**4. Migrate, explicitly.**

```
Graticula.Host migrate            # prints the plan and changes nothing
Graticula.Host migrate --apply    # applies it
```

**The server does not migrate on startup and will refuse to serve against a
store it does not match** ([ADR-016](adr/ADR-016-packaging-deployment-upgrade.md)
§4b). That is not caution for its own sake: auto-migration is how an old
container started by accident — a stale tag, a rollback, a stray `docker run` —
silently rewrites a newer schema, and the result presents as corruption rather
than as a mistake.

### 0.1 The first administrator

Start the server. It has no accounts, so it refuses everything and prints a
**one-time setup token** to its log, valid for sixty minutes:

```
crit: startup[1009]
      SETUP REQUIRED. This server has no administrator. One-time setup token,
      valid for 60 minutes:

    <token>
```

POST it with a name and a password of at least eight characters:

```
POST /rest/setup
{"token": "<token>", "name": "admin", "password": "..."}
```

**It is printed once and is not reprinted.** A restart does not issue a second
one — that would mean two live credentials for a one-time act — so if it is lost,
delete the row from `setup_token` and restart. Everything else is refused until
this is done, which is the whole point:
[ADR-015](adr/ADR-015-authentication.md) has no default account and no default
password.

### 0.2 Checking it worked

`GET /healthz/live` answers 200 once the process is up; `GET /healthz/ready`
answers 200 once it can reach the platform store.

`GET /admin/health` says more. **It answers anonymous callers as well, with the
detail redacted** — that is [D-18](architecture-debt.md), recorded there as the
wrong trade and the only one available, because a readiness endpoint a load
balancer can reach is a readiness endpoint a stranger can reach.

---

## 1. Running more than one node

The baseline is one server against one PostgreSQL/PostGIS (`CLAUDE.md` §6), and
that is the shape everything has been tested in. More than one node works — the
server holds no session or catalogue state of its own — but **three things are
per node, and one of them will surprise somebody.**

### 1.1 What is shared, and what is not

| | Where it lives | Multi-node |
|---|---|---|
| Catalogue, sharing, service status | PostgreSQL | **Shared.** Read on every request, so a change on one node is seen by the others on their next request |
| Sessions and identity | PostgreSQL | **Shared.** Signing out on one node signs out everywhere |
| Audit records | PostgreSQL | **Shared** |
| Style documents | PostgreSQL | **Shared** |
| Glyph ranges | The container image | Identical on every node; they are files that ship with the build |
| **Tile cache** | **Local disk** | **Per node** — see below |
| Table-shape cache (30 s) | Memory | Per node. Harmless: it expires quickly and every node describes independently |
| Last-known catalogue, for a store outage ([ADR-026](adr/ADR-026-serving-through-a-platform-store-outage.md)) | Memory | Per node. A node that has never served a service has no memory of it and answers 503 rather than guessing |

### 1.2 The tile cache is per node, and that is the cost to plan for

`FileSystemTileCache` writes to local disk. **Two nodes mean two caches**, so a
cold pyramid is built twice — once per node — and the datastore sees double the
cold-miss load. Four nodes, four times.

This is a deliberate refusal rather than an oversight.
[ADR-029](adr/ADR-029-affinity-routing-is-not-the-default.md) §2 declined Redis
for it: tiles are large and mostly cold, which is the workload you keep on disk
rather than in memory, and a shared cache would have bought the smaller half of
the problem for a permanent dependency.

**Put a caching reverse proxy in front.** Every deployment already needs one for
TLS termination unless the server terminates it itself
([ADR-014](adr/ADR-014-tls-and-certificates.md)), and tile responses are already
shaped for it:

- `Cache-Control: public, max-age=<the layer's own lifetime>` — per layer, set by
  whoever knows how volatile the data is (D-25).
- `ETag`, strong, computed from the bytes. After expiry a revalidation costs a
  header rather than a tile.
- `X-Tile-Cache: HIT | MISS | COALESCED` — what the *origin* did, for diagnosis.

With a proxy cache in front, the fan-out across nodes stops mattering for
anything the proxy holds. Enable request collapsing if the proxy supports it
(`proxy_cache_lock` in nginx, request coalescing in Varnish): the server does
this within a node already, and the proxy is the only place it can be done
across them.

**If you cannot put a proxy in front**, the options are to accept the
duplication — bounded, and each node warms independently — or to point every
node's `Graticula:TileCachePath` at shared storage. Shared storage has not been
tested and the cache was not written for concurrent writers from several hosts;
treat it as unsupported until somebody measures it.

### 1.3 What has never been run

**No deployment of this server has ever had two nodes.** Everything in §1.1 is
derived from where the state is written, not from having watched it. The
multi-node reasoning in ADR-026 — two servers over one store, where a stale value
is present and wrong rather than missing — is likewise reasoned and untested.

Before relying on any of it, run two and check at least: a sharing change on one
node taking effect on the other, a session revoked on one being refused by the
other, and the tile cache fan-out actually costing what §1.2 says it does.

---

## 2. Backup and restore

**Written 2026-09-09 answering [Q-48](open-questions.md), and it is owed rather
than new.** `SchemaCompatibility` and `SchemaMigrator` both tell an operator, in
their own refusal text, that *recovery is restore-from-backup* — and this manual
had never said how to take one. That is independent review 3's finding **O2**,
and it is the half of it that a document can close.

### 2.1 There is one database, and that is the whole reason this is short

The catalogue and the hosted tables are **not two stores**. The datastore's
connection is the platform store's own connection string with the search path
cleared (`Program.DatastoreConnection`), it is upserted from
`Graticula:PlatformStore` on every start, and the admin API **refuses** to change
it — a change there would work, report success and be undone by the next restart.

So a single `pg_dump` of that database is an atomic snapshot of the catalogue and
of every hosted table **at the same instant**. There is no ordering rule to get
right and no version stamp to keep in step, because there is nothing to keep in
step with. [ADR-033](adr/ADR-033-symbology.md) already reasons from this — *one
dump restores the catalogue and its cartography at the same instant* — so the
property is being relied on and is worth stating where an operator reads.

```bash
# Back up. One database, one file, one instant.
pg_dump --format=custom --file=graticula-$(date +%F).dump "$GRATICULA_DB"

# Restore, into an empty database.
pg_restore --dbname="$GRATICULA_DB" graticula-2026-09-09.dump
```

### 2.2 The one rule: back up the database, never a schema

**Do not use `pg_dump -n` or `pg_restore -n` to take or restore a backup.** The
catalogue lives in one schema (`gisserver` by default) and hosted tables live in
another (`hosted`), so a schema-selective dump splits the two halves that have to
move together. Restoring one without the other produces a catalogue describing
tables that are not there, or tables no catalogue knows about.

*(`tools/rollback-rehearsal.sh` does use `pg_dump -n`, correctly: it copies the
platform schema to a second schema to rehearse a migration rollback. That is a
schema-copy tool, not a backup, and it is named here so nobody reads it as the
house pattern for one.)*

### 2.3 What a mismatch looks like, so it is recognised

**This is the part worth reading before it happens**, because the server does not
announce it. With a catalogue row whose table is missing:

| Surface | What it answers |
|---|---|
| `/healthz/ready` | **200 `ready`** — it lists layers and pings the datastore; neither touches a layer's own table |
| `/admin/health` | **`ok`**, and the layer count includes the ghost |
| `/rest/services` | lists the service normally |
| `FeatureServer/0?f=json` | **200**, with `fields` empty and no extent — a missing relation returns *no rows* from `pg_class` rather than an error, and the extent probe catches and returns null |
| `FeatureServer/0/query` | **503**, and this is the only place the truth appears |

The 503 says it well — *the table behind this layer no longer exists. The
registration and the database have diverged; this is a catalogue problem, not a
transient one, and retrying will not help.* **But the first person to read it is a
client, not the operator who did the restore**, and every surface an operator
would check first says the server is healthy. If a layer document comes back with
an empty `fields` array after a restore, that is this.

### 2.4 What this does not cover

**Divergence is reachable without any restore**, so §2.3 is not only a
restore-gone-wrong story: a crashed import can leave a hosted table the catalogue
never learned about, an unpublish whose `DROP` fails leaves the table behind
(measured, with the datastore quiesced), and a DBA's own `DROP TABLE` reaches the
same state trivially. Nothing sweeps for either direction.

**No backup mechanism ships**, and that is a decision rather than an omission:
what a backup verb would do is run `pg_dump`, which the operator's own tooling,
schedule and retention policy already do better. It is worth revisiting on the day
attachments ship — [ADR-013](adr/ADR-013-feature-service-data-model.md) §4e says
the datastore *"is about to contain arbitrary user binaries, so its backup size
stops being a function of feature count and grows without bound"* — because that
is the day one dump stops being a comfortable answer.

### 2.5 What has never been run

**Nobody has restored this product from a backup.** Everything above is derived
from where the state is written and from reading what each surface answers, in
the same way §1.1 is. Before relying on it, take a dump, restore it into an empty
database, and check that the service list, one layer document and one query all
come back — and that the administrator you sign in as still exists.
