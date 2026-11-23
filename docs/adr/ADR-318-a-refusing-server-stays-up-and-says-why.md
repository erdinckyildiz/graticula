# ADR-318 — A server that refuses to serve stays up and says why

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `HIGH` |
| **Decided** | 2026-11-23, by owner decision — *"Reddederken ayakta kal"* (stay up while refusing), the fourth answer of band 2 group 2's question dialog, on [D-391](../architecture-debt.md)'s three options. The details below §5.1 are the dr agent's and are marked `INFERRED` where nothing settles them. |
| **Supersedes** | [ADR-016](ADR-016-packaging-deployment-upgrade.md) §4b's *"refuses to run"* — the refusal stands; the exit does not. `compose.yaml`'s `restart: on-failure:5` |
| **Superseded by** | — |

---

## 1. Context

[ADR-016](ADR-016-packaging-deployment-upgrade.md) §4b makes every component read the platform schema at start and
refuse to run against one it does not match, and the server did that by **exiting**: `HandshakeAsync` returned false
and `Main` returned 1. A refusal is permanent until somebody acts, so `compose.yaml` restarted the server
`on-failure:5` — bounded, because `unless-stopped` turned the refusal into a crash loop that scrolled its own
explanation away five times a minute (ADR-016 condition 4's walk).

[D-391](../architecture-debt.md) is the price. Docker brings back only `always` and `unless-stopped` containers when its
daemon starts, and a server stopped by the host's own shutdown exits 0, which `on-failure` does not count. On
2026-11-08 the showcase's VPS was shut down gracefully and came back an hour later with everything running but the
server, and demo.graticula.com answered 502 until somebody started it by hand.

The owner chose between D-391's three options with *"Reddederken ayakta kal"*: the process does not exit when it
refuses. It keeps answering, 503 with the reason, so `unless-stopped` has nothing to loop on.

**Every refusal the serving path makes, read from `Program.cs` on 2026-11-23** (the CLI verbs — `migrate`, `backup`,
`restore`, `tools …` — are commands, not services, and keep their exit codes):

| Where | What | Exit before |
|---|---|---|
| `HostSettings.Read` | a missing or unusable setting ([D-171](../architecture-debt.md)) | 78 |
| `HandshakeAsync` | the platform store does not answer | 1 |
| `HandshakeAsync` | the store has no schema | 1 |
| `HandshakeAsync` | the store is older than this build (not migrated) | 1 |
| `HandshakeAsync` | the store's minimum reader is above this build (a contract ran) | 1 |
| `HandshakeAsync` | several organisations over a role row-level security does not bind (ADR-204 condition 9) | 1 |
| `SiteNode.JoinAsync` | the node's name is taken, its key or settings differ from the site's, its server directories are not the site's (ADR-012) | 1 |

## 2. Alternatives considered

### Alternative A — stay up, answer 503 with a named state, re-check (chosen)

**Argument for.** One restart policy that is right in both directions: a host reboot brings the server back, and a
refused start is one process that says why on every request instead of five that each said it once. A store migrated
from another container brings the refusing server into service by itself.

**Argument against.** A process that is up and useless is a new state to understand; a monitor that only asks *is the
container running* now sees *running* where it saw *exited*.

### Alternative B — keep `on-failure`, document a boot-time `docker compose up -d`

**Argument for.** Nothing in the server changes.

**Argument against.** Every deployment has to add a systemd unit or a cron line it will not know it needs until the
first reboot. The owner did not choose it.

### Alternative C — exit non-zero on SIGTERM

**Argument for.** `on-failure` would then restart after a host shutdown.

**Argument against.** Every deliberate stop would look like a failure, to Docker and to whoever reads the exit code.

## 3. Counterarguments to the preferred option

- **Liveness answering 503 is what an orchestrator that restarts on liveness acts on.** Kubernetes would restart the
  pod, which is the loop this decision removes. The owner's answer named the health endpoints, and Docker — the only
  deployment this repository ships — does not restart on a failed health check (`compose.yaml`'s comment on `init`
  says so); an `unhealthy` container is the signal an operator should see. Recorded, not hidden: a deployment on an
  orchestrator that restarts on liveness gets a slow loop (one restart per probe window) rather than a fast one.
- **A refusal no longer shows as an exit code.** CI and scripts that waited for the process to exit wait for ever.
  `TwoNodeSite.StartRefusedAsync`, the only one in this repository, now waits for the 503 instead.
- **Re-checking means a refused node keeps connecting.** One handshake every ten seconds — a stamp read, a count and a
  site query — which is less than the heartbeat a serving node writes.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| A host reboot leaves an `on-failure` container stopped | The showcase, 2026-11-08: `graticula-server-1 Exited (0)` after a graceful shutdown | [D-391](../architecture-debt.md) |
| Docker restarts only `always` / `unless-stopped` containers when the daemon starts, and does not restart on a failed health check | Docker's documentation of restart policies | https://docs.docker.com/engine/containers/start-containers-automatically/ |
| `compose.yaml` is the only file in the repository that sets a restart policy for the server | `git grep -n "restart:"` and `--restart` across the tree, 2026-11-23: `compose.yaml` lines 59 (the datastore, already `unless-stopped`) and 151 | this repository |
| The refusals above are all the serving path makes | `Program.cs` `Main`, `HandshakeAsync`, `SiteNode.JoinAsync`, read 2026-11-23 | this repository |

## 5. Decision

1. **Every refusal in §1's table keeps the process alive.** It listens where it would have served and answers every
   request with **503**, a named state and the sentence it used to exit with:

   | State | Refusal |
   |---|---|
   | `configuration` | a setting is missing or unusable |
   | `store-unreachable` | the platform store does not answer |
   | `store-not-initialised` | the store has no schema |
   | `store-not-migrated` | the store is older than this build |
   | `store-newer` | the store's minimum reader is above this build |
   | `unbound-role` | several organisations over a role the tenant floor does not bind |
   | `node-refused` | the site refused this node |

   - `/healthz/live` and `/healthz/ready` answer `{"status":"refusing","state":…,"reason":…}`.
   - `/rest/info/healthcheck` answers ArcGIS's shape, `{"success":false,"error":{"code":503,"message":…,"details":[state]}}`.
   - Every other path answers `{"error":{"code":503,"message":…,"details":["refusing: <state>"]}}` and nothing else:
     no route behind it runs, so no data, no console and no sign-in.
   - Every answer carries `Retry-After: 10`.

   These states are their own family, beside the not-ready reasons a serving node already gives (maintenance,
   warming, a store outage), so another state — the store's primary gone, which ADR-319 (the platform store's standby, written beside this one)
   adds — is one more row, not a rewrite. *(`INFERRED`: the names.)*
2. **The handshake is run again every ten seconds**, and when it passes the refusing listener closes and the server
   starts exactly as a first start does. So `migrate --apply` from another container, a database that comes back, or
   the other node with the same name stopping brings this one into service **without a restart**. The refusal is
   logged once, and again only when its state changes; a node that comes into service says so. *(`INFERRED`: ten
   seconds, the site's heartbeat.)*
3. **A configuration refusal is not re-checked** — the environment of a process does not change — and listens where
   the parts of the configuration it can read say, defaulting the rest (`0.0.0.0`, 8443, HTTPS with the certificate
   under the state directory). When even that cannot listen — no certificate can be loaded or made — it exits 78 as
   before, because there is then nothing to answer with. *(`INFERRED`: the owner's answer named the store; a missing
   setting under `unless-stopped` is the same loop.)*
4. **Stopped while refusing**, it exits with the code the refusal used to exit with — 78 for configuration, 1 for the
   rest — so a person reading `docker ps -a` still sees why it last stopped.
5. **`compose.yaml` restarts the server `unless-stopped`**, and its comment says why that is now safe. No other file
   in the repository carries the policy (§4).
6. **ADR-016 §4b's sentence becomes *"refuses to serve"*.** It still never migrates on its own.

## 6. Consequences

**Positive.** A host reboot brings the server back ([D-391](../architecture-debt.md)). A refusal is readable from any
client — a browser, a balancer's health check, `curl` — instead of from a container's log. An upgrade becomes
*start the new image, migrate*, in either order.

**Negative.** A refused server is a running container: a monitor that watched for an exit must watch health instead.
Liveness reports the refusal, which an orchestrator that restarts on liveness acts on (§3). The refusing listener is
a second, small web application in the same process, and its Kestrel set-up is a copy of the serving one's
certificate choice, not of its whole configuration.

**State.** None — nothing new is stored.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | No deployment of this product restarts its process on a failed liveness probe | `INFERRED` — true of the compose file this repository ships; untrue of a Kubernetes manifest with a liveness probe, which this repository does not ship |

## 8. Dependencies

**Depends on:** [ADR-016](ADR-016-packaging-deployment-upgrade.md) §4b, [ADR-012](ADR-012-clustering.md) (the node's
join), [ADR-204](ADR-204-one-deployment-holds-several-organisations.md) condition 9.

**Depended on by:** [ADR-317](ADR-317-validate-and-restore-through-the-admin-api.md) (a restore's readiness state is
built beside these).

## 9. Revisit triggers

- A deployment target that restarts on a failed liveness probe is added to the repository.
- A refusal is found that a re-check cannot clear and that loops a log anyway.

## 10. Dissent

None recorded.

## 11. Conditions

1. **A real process against an un-migrated store stays up** and answers `/healthz/live`, `/healthz/ready`,
   `/rest/info/healthcheck` and a data path 503 with `store-not-migrated`; it is still running after twenty seconds;
   and `migrate --apply` from another process brings it into service without a restart. The test fails with the
   change removed.
2. **A configuration refusal stays up** and answers 503 `configuration` naming the missing setting.
3. **The site's own refusal tests hold under the new contract** — the node with another key, another setting, a taken
   name, other server directories, a store whose minimum reader is above it — each answering 503 `node-refused` or
   `store-newer` with its sentence, instead of exiting.
4. **In a container**, with `compose.yaml`: a restart of the Docker daemon (or the host) brings the server back, and a
   server started against an un-migrated store stays `Up (unhealthy)` with a restart count of 0, then turns healthy
   after `docker compose run --rm server migrate --apply`. Needs Docker, which this machine does not have.
