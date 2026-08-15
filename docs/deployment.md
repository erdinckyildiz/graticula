# Deployment

**Status:** STUB — not written, apart from §1, which exists because
[ADR-029](adr/ADR-029-affinity-routing-is-not-the-default.md) condition 3
required it before anybody is told to run more than one node.
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
node's `GisServer:TileCachePath` at shared storage. Shared storage has not been
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
