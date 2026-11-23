# ADR-319 — The platform store fails over to a standby, and the nodes are its witnesses

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` for the mechanism, which uses PostgreSQL's own replication, promotion and timelines and is tested against real processes (condition 1–4) · `LOW` for the compose half, which has not been run (condition 7) |
| **Decided** | 2026-11-23, by owner decision. Asked whether the store's availability should be a standby promoted by hand (the recommendation) or automatically, the owner answered *"Otomatik devralma da"* — automatic failover too, as ArcGIS Data Store's primary/standby pair does. Everything below that public documentation or an earlier ADR does not settle is marked **INFERRED** and listed in §5.12. |
| **Supersedes** | — |
| **Superseded by** | — |

---

## 1. Context

[ADR-012](ADR-012-clustering.md) made the **server tier** available: phases 0–4 are built and discharged — a site of
nodes over one platform store, shared server directories, Caddy in front, rolling upgrades with no failed request.
It left the store itself out on purpose. §3 says *"PostgreSQL is still a single point of failure. True, and out of
this ADR's reach: replicating the store is the database's availability, decided separately"*, and condition 4 records
the owner confirming that *"PostgreSQL's availability [is] a decision of its own"*. This is that decision.

**A correction to the gap table.** [enterprise-12-2-gap.md](../research/enterprise-12-2-gap.md) lists *"HA and
multi-machine — Phases 0–1"* and an item *"HA phase 2"*. Both are stale: ADR-012's phases 0–4 were all built by
2026-11-01 (its condition 3). What was missing on 2026-11-23 is not a phase of ADR-012 but this: **the store's own
availability.** Two nodes over one PostgreSQL make the server available; when that PostgreSQL stops, every node
answers 503 together. *(The gap document is the lead's to correct; this paragraph is the record of why.)*

What already holds, and what this decision must not break:

- **Every durable thing is in one PostgreSQL database** (ADR-002, ADR-012 §1b): the catalogue, identity, documents,
  the hosted tables. Replicating that one database replicates the product's state; the files under the server
  directories are ADR-012 phase 1's and not this ADR's.
- **Schema changes are an explicit command under a handshake** ([ADR-016](ADR-016-packaging-deployment-upgrade.md)
  §4a/§4b). A physical standby replays the primary's WAL, so it carries the same stamp; there is nothing to migrate
  on it and nothing to hand-shake twice.
- **Backup and restore are the server's verbs** ([ADR-193](ADR-193-backup-and-restore-are-the-server-s-own-commands.md)).
  `pg_dump` must dump the primary, and the restore's database rename is a catalogue change that replicates.
- **The baseline is `graticula → PostgreSQL/PostGIS`** (CLAUDE.md §6). A deployment that does not ask for a standby
  must be exactly what it was.

### 1a. How ArcGIS Data Store's primary/standby works — public documentation only

Read 2026-11-23. Nothing here is taken from an implementation.

- **Two machines, one primary.** *"Both machines contain the same data. Data is replicated from the primary to the
  standby machine. The standby becomes primary if the primary machine fails."* A relational store in this mode is
  limited to two machines. — [Data store deployment modes (latest)](https://doc.esri.com/en/arcgis-enterprise/latest/deploy/data-store-deployment-modes.html),
  mirrored for 12.1 at [edg.epa.gov](https://edg.epa.gov/arcgis/help/en/12.1/deploy/data-store-deployment-modes.html).
- **When it fails over.** The data store stops and cannot be restarted on the primary; or *"ArcGIS Data Store fails
  over to the standby machine if three consecutive attempts to connect to the primary machine fail"* (10.6 said five);
  or a human takes the primary offline deliberately, or *"the ArcGIS Server site administrator runs the makePrimary
  REST command on the standby machine"*. A planned Windows shutdown of the primary does **not** fail over; the advice
  is to promote the standby by hand first. — same page; [Add a standby machine (10.6)](https://enterprise.arcgis.com/en/data-store/10.6/install/linux/add-standby-machine.htm).
- **How long.** *"You will only be without your hosted feature layer data for the short period of time"* until the
  standby becomes primary, which *"can take up to a few minutes"*. — Add a standby machine (10.6).
- **When the old primary returns.** *"the former primary becomes the standby machine for the data store."* — same
  page. Esri's knowledge base describes the failure of that step: *"Replication could not catch up with Primary
  machine"*, fixed by reconfiguring the standby. — [Error 000023741](https://support.esri.com/en-us/knowledge-base/error-arcgis-data-store-has-detected-a-replication-fail-000023741).
- **Not documented, and so not copied:** whether replication is synchronous; what witness, if any, decides that the
  primary is gone rather than merely unreachable from the standby; how a returning primary is kept from accepting
  writes before it is made a standby. **The pages describe a pair of two that decides by its own connection
  attempts.** Split brain is the question the owner's dialog named, and Esri's public pages do not answer it, so the
  answer below is this project's, marked INFERRED where it is a choice.
- **Not read:** `enterprise.arcgis.com/en/portal/latest/administer/windows/add-standby-machine.htm` refused the fetch
  (connection reset) twice; the 10.6 page and the 12.1 mirror stood in for it.

## 2. Alternatives considered

### Alternative A — Patroni (or repmgr, or pg_auto_failover) with etcd/Consul as the witness

**For.** The established answer. A distributed consensus store holds the leader key; a leader that loses it demotes
itself; fencing and rejoin (`pg_rewind`) are built in; it has years of production behind it. If the question were
"what is the most robust PostgreSQL failover in general", this is the honest answer, and this ADR says so.

**Against — CLAUDE.md §6's question, *what concrete problem does it solve here?*** Patroni's leader key exists to
stop **any client** from writing to a demoted primary, because in general the database cannot know who its writers
are. Here it can: **the only writers to the platform store are Graticula nodes** (and an administrator at `psql`,
who is not the case to design for). The nodes already form a site that meets in the store (ADR-012) and they share a
key. A witness made of the writers themselves answers the split-brain question for this product without a
three-member etcd cluster beside a two-machine database — which on the small deployment (§60) would be more
processes than the product. Patroni also wants to own PostgreSQL's process (it starts and stops it), which is not
how the appliance compose runs the datastore image. **Rejected for the default, not ruled out**: a deployment that
already runs Patroni can point the server at it with the same multi-host connection (§5.2) and turn the server's
own promotion off (`Graticula:StoreFailover = manual`). The server's half — writing only to a read-write primary on
the newest timeline — is useful in front of any failover manager.

### Alternative B — Manual promotion only (the recommendation the owner declined)

**For.** No split-brain risk from automation at all: a human decides. **Against.** The owner's decision. It is kept
as the floor (§5.7): where the automatic rule cannot be satisfied, a human can still promote from the console.

### Alternative C — The server's own failover, with the nodes and the standby as witnesses *(chosen)*

A streaming-replication standby; the server connects to both hosts and writes only to the read-write one on the
newest timeline; the nodes decide together with the standby when to promote; the old primary is fenced when it
returns and refused even before it is fenced. §5.

### Alternative D — A load balancer or virtual IP in front of PostgreSQL (keepalived, pgbouncer, HAProxy)

**Against.** Moves the question (*who decides the address moves?*) to another process with its own health check and
its own split brain, and adds a component to every deployment that turns the standby on. The multi-host connection
moves the selection into the client, where the epoch check can also live.

## 3. Counterarguments to the preferred option

- **"A two-member database with no third machine cannot have a quorum."** True for the database by itself. The
  decision here is not taken by the two database machines: it needs **the standby's own view and every node's**
  (§5.4). On a site of one node, the parties are the primary, the standby and the node — the only writer. If the node
  and the standby both cannot reach the primary, then either the primary is gone, or it is alive and nothing can
  write to it; both are safe to promote over, **provided the node never goes back to the old primary**, which §5.5
  and §5.6 make mechanical rather than hoped for.
- **"Asynchronous replication loses the last transactions."** True: what the primary committed and had not shipped
  when it died is lost on failover. The console shows the lag so the loss is bounded by a visible number; an operator
  who wants zero loss sets `synchronous_standby_names` and accepts that the primary then stops committing while the
  standby is down. Not the default (INFERRED §5.12 item 3): the default must not make a standby's outage the
  primary's.
- **"A node can still be partitioned in a way the rule does not see."** A node the deciding node cannot ask holds
  the failover (§5.4) rather than being assumed down — the conservative failure, which costs availability (a human
  then promotes, §5.7) and never consistency. D-497.
- **"A brand-new node remembers no timeline."** True, and a node started for the first time while the new primary
  is unreachable and the old one has returned unfenced would accept the old one. Narrow — it needs a new node, a
  returned old primary, an unreachable new one and no running node to have fenced it — and recorded as D-496.
- **"Hand-written failover is how databases get corrupted."** The parts that would corrupt are PostgreSQL's own —
  `pg_promote()`, timelines, `pg_rewind` — and none is reimplemented. What is written here is the decision of *when*
  and the refusal of *where*, both tested against real clusters (conditions 1–4).

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| ArcGIS Data Store fails over after three failed connection attempts, or on `makePrimary`; the former primary returns as the standby | public documentation | §1a |
| Npgsql 9 refuses `Target Session Attributes` in a multi-host connection string given to a data source builder (*"When creating a multi-host data source, TargetSessionAttributes cannot be specified"*); `BuildMultiHost().WithTargetSession(ReadWrite)` is the way | measured 2026-11-23 against two local clusters (55510 primary, 55511 streaming standby) | scratch probe, outside the repository |
| An exception thrown by a physical connection initializer makes Npgsql's multi-host source move to the next host | measured, same probe: the initializer refused 55510 and the connection opened on 55511 | same |
| A primary with `default_transaction_read_only = on` is not chosen for `ReadWrite` | measured, same probe: *"No suitable host was found"* with the standby in recovery | same |
| A role that is not a superuser may read `pg_control_system()` and `pg_walfile_name(pg_current_wal_lsn())` but sees `pg_stat_wal_receiver` columns as null | measured, same clusters | same |
| Time from a killed primary to the first write accepted through the server; a returned old primary written nothing | conditions 1 and 2 | `TheStoreFailsOverTests` |

## 5. Decision

### 5.1 A streaming-replication standby, off by default

A second PostgreSQL made by `pg_basebackup -R` from the primary, streaming, `hot_standby = on`. Both run with
`wal_log_hints = on` (or data checksums) so the old primary can be rewound (§5.8). Nothing changes for a deployment
that names one host.

### 5.2 The server names both hosts and writes only to the read-write one — its own selection over Npgsql's

`Graticula:PlatformStore` may name two hosts: `Host=db-a:5432,db-b:5432;…`. **The selection is Npgsql's multi-host
data source, driven by our code**, because Npgsql 9 does not accept the target in the string (§4): one function,
`StoreHosts.Build`, builds every pool over the store — the request pool, the job pool, the datastore pool, the
layer pools, the site's lock connection — as `BuildMultiHost().WithTargetSession(ReadWrite)` when the string names
more than one host, and as before when it names one. A connection string that is not a pool's
(`new NpgsqlConnection(…)`: the listeners, `pg_dump`'s environment) is given `Target Session Attributes=read-write`
by `StoreHosts.ForConnection`, which Npgsql accepts there. `pg_dump` and `pg_restore` receive the hosts and ports as
libpq lists with `PGTARGETSESSIONATTRS=read-write`. **What this changes in connection-string handling is exactly
that and nothing else**; a one-host string passes through every one of these functions unchanged.

`Host Recheck Seconds` defaults to 1 for a multi-host store (Npgsql's default of 10 would keep a just-promoted
standby marked *standby* for ten seconds).

### 5.3 The epoch is PostgreSQL's timeline, and a node never writes below the newest it has seen

Every promotion starts a new **timeline**. Each physical connection a multi-host pool opens first asks the server
three things in one round trip — whether it is in recovery, its system identifier, and the timeline of its current
WAL position — and **refuses a primary whose timeline is below the newest this node has seen for that system
identifier**. The refusal is an exception in Npgsql's physical connection initializer, so Npgsql goes on to the next
host (§4). The newest timeline is kept per system identifier in memory and in `store-epoch.json` under the node's
state path, so a restarted node remembers it. A cluster with a new system identifier (rebuilt from nothing) starts
its own count. *INFERRED (§5.12 item 4).*

### 5.4 Who decides to promote: the standby and every node, unanimously

Each node runs `StoreFailover` when the store names more than one host. Every second it probes each host on a
connection of its own (two-second timeout, `Graticula:MaintenanceStore`'s role where set, since reading the standby's
WAL receiver and promoting need one), and keeps what it saw: role, timeline, fenced or not, the standby's receiver,
the primary's replication lag.

**Promotion happens when, and only when, all of these hold:**

1. **This node** has failed to reach the primary on **three consecutive probes** — ArcGIS's documented three
   (§1a), a second apart (*INFERRED*, §5.12 item 2).
2. **The standby** is reachable, in recovery, and **its own WAL receiver is not streaming** — the standby's vote,
   read from `pg_stat_wal_receiver`. A standby still streaming from the primary proves the primary is alive.
3. **Every other node of the site** that was up at the last heartbeat this node read answers, within two seconds,
   that **it** cannot reach the primary either. The question is `GET /healthz/store`, authenticated by a keyed hash of
   the site's `Graticula:SecretKey` (every node has the same key, ADR-012 item 6), and the answer is signed the same
   way. **It names the primary by its system identifier and timeline, never by an address**: two nodes may reach one
   host by two names, and the first build asked by address — the node asked did not know the name, answered *no*, and
   the other promoted beside a primary it could still reach (found by condition 3's two-node test, fixed the same
   day). **A node that does not answer holds the failover** — it might be partitioned from us and still writing to
   the primary. *(D-497.)*

Then the node calls `pg_promote(true, 60)` on the standby, clears any fence the standby inherited (§5.6), writes a
row to `store_failover` (migration 148, given 149 and renumbered at the merge) on the new primary — when, from where to where, the timeline, which node
decided, how long the primary had been unreachable — and raises its epoch. Two nodes deciding at once is harmless:
the second `pg_promote` finds a primary and does nothing.

**Before it votes, a node stops writing to the primary it votes against.** When this node's three probes fail, or it
tells another node so, it marks the primary *suspect* for 30 s; a suspect host is refused by §5.3's check even if it
answers again. Without this a node could vote *down*, reach the primary a second later, and write a transaction to
it after the standby was promoted. If no promotion follows within the 30 s, the mark lapses.

**On a site of one node** the parties are the primary, the standby and the one node, which is the only writer. The
rule then needs the node and the standby to agree; no majority of machines exists, and none is needed: if both
cannot reach the primary, either it is down or nothing can write to it, and §5.3 and §5.6 stop the node from going
back to it. **Automatic failover is therefore offered on a one-node site.** What it cannot protect is a writer that is
not a Graticula node (an administrator's `psql` on the old primary during a partition); those writes are on a
timeline nobody follows and are lost when it is rewound. Said in deployment.md §1.6.

### 5.5 What the server does while the primary is gone

Requests that need the store answer as in any store outage (ADR-026: 503, the breaker). `/healthz/ready` and
`/rest/info/healthcheck` answer 503 with **their own named state**, `store-failover`, and its reason (*the platform
store's primary at db-a:5432 is unreachable; failover is waiting for …* or *… promoting db-b:5432*), separate from
the draining, warming and store-unreachable states that were there before, so another reason can be added beside it
(ADR-317/318's) without the two meeting. Liveness is unchanged: the process does not exit.

### 5.6 Fencing the old primary when it returns

A returned old primary comes back **read-write on the old timeline**, because it does not know it was replaced. Two
things keep it from being written:

1. **Refused at once** by every node that knows the newer timeline (§5.3) — before anything else happens.
2. **Fenced within a second** by any node's `StoreFailover` that sees two read-write hosts of one system: the one on
   the lower timeline gets `ALTER SYSTEM SET default_transaction_read_only = on` and a configuration reload, through
   the maintenance role. Npgsql's `ReadWrite` then never selects it (§4), whatever a node remembers — including a node
   that remembers nothing (D-496 is the case where no running node is there to fence it).

The fence stays until the host is rejoined (§5.8). Promotion clears it on the host promoted
(`ALTER SYSTEM RESET default_transaction_read_only`), so a host fenced once can be promoted later.

### 5.7 Manual promotion is the floor, and `Graticula:StoreFailover` chooses

`Graticula:StoreFailover` is `automatic` (default when two hosts are named) or `manual`. In both,
**`graticula store promote`**, run on a machine of the site with its settings, promotes the standby — ArcGIS's
`makePrimary` — and **refuses while it can reach a read-write primary on the newest timeline**: promoting beside a
live primary is the split brain this ADR exists to prevent, so a planned switchover stops the primary first, as Esri
advises before a planned shutdown (§1a). A held automatic failover (a node that did not answer, D-497) is resolved
this way, by a human who can see why it was held.

*Amended the same day, by a test.* This was first an admin route, `POST /admin/site/store/promote`, with a button on
System › Database. `A_manual_site_waits_for_an_administrator_to_promote_the_standby` showed it could never succeed:
it is needed exactly when the primary is gone, and then the server cannot read the session that proves the caller an
administrator, so it answered 401 — by design (Authentication refuses rather than assume, and changing that is not
this ADR's). The route and the button were removed rather than left as a call site with nothing behind it; the page
says the command instead. Whoever can run the binary with the site's settings already holds the store's credentials,
which is the authority promoting needs. *(INFERRED, §5.12 item 7.)*

### 5.8 Rejoining the old primary: `pg_rewind`, not a re-clone, and not by the server

The fenced old primary is stopped, rewound against the new primary, and started as its standby:

```text
pg_ctl -D <old> stop -m fast
pg_rewind -D <old> --source-server="host=<new> port=… user=postgres dbname=postgres" --write-recovery-conf
pg_ctl -D <old> start          # standby.signal written by -R; it streams from the new primary
```

`pg_rewind` needs `wal_log_hints` or checksums (§5.1) and copies the source's configuration, so the fence goes with
it. A re-clone (`pg_basebackup`) is the fallback when the rewind cannot find a common point. **The server does not do
this itself**: it runs where the server is, not where the database's files are. There is **no automatic failback**:
the rejoined host stays the standby, as ArcGIS's former primary does (§1a). *(INFERRED, §5.12 items 5 and 9.)*

### 5.9 The appliance compose: an override file, off by default

`compose.standby.yaml`, used as `docker compose -f compose.yaml -f compose.standby.yaml up -d`, adds
`datastore-standby` (the same image, cloned from `datastore` by `pg_basebackup -R` on an empty volume), turns on
replication and `wal_log_hints` on the primary, and points the server at both hosts. Both datastore containers run
`deploy/standby/datastore-ha.sh` before PostgreSQL's own entrypoint: on an empty volume of the standby it clones; on a
host that finds its peer a primary on a newer timeline than its own, it rewinds itself (§5.8) and starts as the
standby. **An override file, not a profile**: a profile can only add a service, and the primary's own settings and
entrypoint change too. The baseline `compose.yaml` is not touched, so `graticula → PostgreSQL` is exactly what it
was. *(INFERRED, §5.12 item 6; not run, condition 7.)*

### 5.10 What the console and the Admin API say

`GET /admin/site/store` answers `{ mode, hosts: [{ address, role, timeline, reachable, fenced, lagBytes, replayLag,
receiver }], primary, epoch, held, lastFailover }`: each host's role (*primary*, *standby*, *fenced*, *unreachable*),
the replication lag the primary reports for its standby (bytes and time), whether a failover is held and why, and the
last failover from `store_failover`. **System › Database** in the console shows the same — the store's role per host,
the lag, the last failover — and, while no primary is reachable or a failover is held, says to run
`graticula store promote` (§5.7). On a one-host store it says that no
standby is configured and links deployment.md §1.6.

### 5.11 What this does not do

- **No read routing to the standby.** Reads go to the primary; using the standby for reads is the store-capacity
  question ADR-012 condition 1 named, and its own ADR.
- **No more than one standby.** ArcGIS's pair is two machines (§1a); so is this.
- **No witness process.** §2 A says when one is the honest answer.

### 5.12 INFERRED — for the owner to confirm

1. The witness is the standby's receiver plus **every** node, unanimously; a node that cannot be asked holds the
   failover (§5.4).
2. Three failed probes **one second apart** (Esri documents the three, not the interval) (§5.4).
3. Replication is **asynchronous** by default; synchronous is the operator's choice (§3).
4. The epoch is the **PostgreSQL timeline**, kept per system identifier in the node's state path (§5.3).
5. The old primary is fenced by `default_transaction_read_only` and rejoined by `pg_rewind`, by an operator or the
   compose wrapper, **not by the server** (§5.6, §5.8).
6. The compose standby is an **override file**, not a profile (§5.9).
7. Manual promotion is **a command, `graticula store promote`**, not an admin route, and it **refuses while a primary
   is reachable** (§5.7).
8. Failovers are recorded in a table, `store_failover` (migration 148, given 149 and renumbered at the merge) (§5.4).
9. **No automatic failback** (§5.8).
10. Nodes ask each other over HTTP, authenticated by a keyed hash of the site key (§5.4).

## Conditions

1. **A failover with real processes.** Two local PostgreSQL clusters, one streaming from the other, and real
   `Graticula.Host` processes over them: the primary is killed (`pg_ctl stop -m immediate`), the standby is promoted
   by the server without a human, and a write through the server succeeds on it. **The time from the kill to the first
   accepted write is measured and written here.**
   **DISCHARGED 2026-11-23** — `TheStoreFailsOverTests.A_killed_primary_is_replaced_by_its_standby_and_never_written_to_again`
   (a site of one node: the case with no majority) and `Two_nodes_that_both_lose_the_primary_agree_and_promote_the_standby`,
   each over two clusters made by `initdb` and `pg_basebackup -R` from the portable PostgreSQL 16.10 with PostGIS, and
   real `Graticula.Host` processes; the primary stopped with `pg_ctl stop -m immediate`, a group created through
   `POST /admin/groups` every 100 ms until one is accepted. **Killed primary to first accepted write: 12.26, 12.35 and
   12.36 s on one node; 12.30 and 12.40 s on two** (three runs and two, this PC, 2026-11-23). The parts: three missed
   one-second probes (~3–4 s, each a refused connection rather than a two-second timeout on one machine), the
   promotion (`pg_promote` waits for it), and the pools' one-second recheck; the rest is most likely the platform
   store's breaker (ADR-007 §4.8), which the outage opens for ten seconds at a time and whose message the node logs —
   *inferred from the log, not measured part by part*; shortening it is not this ADR's to decide (revisit trigger). Within Esri's
   *"up to a few minutes"* (§1a). Not measured: across machines, where an unreachable host costs the two-second
   connect timeout per probe rather than an instant refusal.
2. **Split brain: the returned old primary is never written to.** After condition 1, the old primary is started again
   as it was — read-write, on the old timeline, **first in the connection string** — and writes keep going through the
   server: none lands on it (counted on both hosts), and it is fenced.
   **DISCHARGED 2026-11-23** — the same test: the old primary restarted as it was (read-write, timeline 1, listed
   first), twenty writes through the node, **0 new rows on the old primary and 20 on the new one**, the old primary's
   `default_transaction_read_only` `on` within ten seconds, `/admin/site/store` naming it *fenced*, and the node's log
   saying it *came back as a primary on timeline 1*.
3. **The rule holds a failover it must hold.** A node that loses the primary while the standby still streams from it
   promotes nothing; and on a site of two nodes, one that still reaches the primary stops the other from promoting.
   **DISCHARGED 2026-11-23** — `A_node_cut_from_a_live_primary_holds_while_the_standby_still_streams` (the node's path
   to the primary cut through a TCP relay, the standby's left: `/healthz/ready` 503 with `"state": "store-failover"`
   and *still streams*, the standby still in recovery five seconds on, and writes back on the same primary once the
   path mends and the 30 s hold lapses) and `A_node_that_still_reaches_the_primary_stops_the_other_from_promoting`
   (node A's path and the standby's both cut, node B direct: A holds, naming B, nothing promoted, B still writes).
   **The second test failed on the first build**, which named the primary by address: B did not know A's name for it,
   answered *no*, and A promoted beside a live primary. It passes since the question names the primary by system
   identifier and timeline (§5.4).
4. **The old primary rejoins** by §5.8's commands and streams from the new primary; the server keeps writing.
   **DISCHARGED 2026-11-23** — the end of condition 1's test: the fenced old primary stopped, `pg_rewind
   --write-recovery-conf` against the new one, started; it is in recovery and *streaming* within 30 s, five more
   writes through the node arrive on it, and `/admin/site/store` names it *standby*. By hand, as §5.8 says; the
   compose wrapper that automates it is condition 7.
5. **The console's System › Database** shows role, lag and last failover from `GET /admin/site/store`, and passes the
   ux-designer review every new screen gets.
   **PARTLY DISCHARGED 2026-11-23** — built (`store-ha.js`, `/system/database`) and rendered against a fixture over two
   local clusters at 1280 and 390 px with Chrome: both hosts with role pills, timeline, *standby up to date* and replay
   lag, *No failover has happened*, the mode; no horizontal scroll at 390; the status line is `aria-live`, and the timed
   redraw leaves an unchanged table alone so focus is not dropped. **Not yet reviewed by the ux-designer agent**, which
   is what keeps this open.
6. **Manual promotion works when the primary is gone.** **DISCHARGED 2026-11-23** —
   `A_manual_site_waits_for_an_administrator_to_promote_the_standby`: with `Graticula:StoreFailover=manual`,
   `graticula store promote` exits 1 naming the live primary; the primary killed, readiness says the failover waits
   for an administrator and nothing is promoted; then the command exits 0, writes resume, and `store_failover` says
   `manual`. *(Added the same day, when the first form of §5.7 failed it.)*
7. **The compose override runs**: `docker compose -f compose.yaml -f compose.standby.yaml up` on a clean machine, the
   primary container killed, the server writing to the standby, the old container restarted and found a standby.

## 6. Consequences

**Positive.** The store stops being the one thing whose loss is an outage with no automatic way back. No new
process, no new dependency: PostgreSQL's replication and promotion, Npgsql's multi-host selection, and the site the
nodes already form. The server's refusal of an old timeline helps in front of any failover manager.

**Negative.** A deployment with a standby runs two databases and must keep their versions together. Asynchronous
replication loses what was not shipped. A held failover needs a human. A brand-new node is the one that can be
fooled (D-496). Every physical connection to a two-host store costs one round trip more.

**Ports created.** None: no Tier 2 library is adopted. Npgsql is already the store's driver.

**State.** In the store: `store_failover`, one row per promotion (migration 148, given 149 and renumbered at the merge) — shared, and replicated with the
rest. Node-local: the newest timeline seen per system identifier, in memory and in `store-epoch.json` under the
node's state path (§5.3), and each host's last probe, the suspect marks and the held reason, in memory only. On the
database hosts: the fence, as `default_transaction_read_only` in the fenced host's `postgresql.auto.conf`.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | The only writers to the platform store are Graticula nodes | INFERRED from ADR-002 and ADR-012; an administrator's `psql` is the documented exception |

## 8. Dependencies

**Depends on:** ADR-002, ADR-012 (the site, its key, its heartbeat), ADR-016 (the handshake), ADR-026 (serving
through an outage), ADR-193 (backups dump the primary).

**Depended on by:** —

## 9. Revisit triggers

- A deployment needs more than one standby, or reads from the standby: the capacity ADR, and probably a failover
  manager (§2 A).
- A held failover (D-497) is reported from a real deployment.
- A writer other than Graticula nodes has to be protected: §2 A's leader key is then the honest answer.
- The ~12 s to the first write (condition 1) is found to be mostly the store breaker's ten seconds: closing the
  breaker when a promotion is seen is then the change, decided with ADR-007.

## 10. Dissent

The brief that asked for this named the split-brain risk as *"the risk that needs a witness"*. This ADR's witness is
the writers themselves, not a third process; a reviewer who holds that only a separate quorum store is a witness
would call §5.4 a quorum by another name. Recorded, not settled.
