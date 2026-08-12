# ArcGIS Server SOM / SOC / ArcSOC — Runtime Model

**Status:** FIRST PASS — the architectural argument is solid; individual numbers
marked `VERIFY` need confirmation against current documentation.
**Required by:** §16 (dedicated investigation)
**Feeds:** [ADR-007](../adr/ADR-007-service-runtime.md),
[service-runtime.md](../service-runtime.md), assessment §4
**Clean room:** publicly documented behaviour only (§5). No proprietary source,
no undocumented internals. Sources listed at the end.

---

## 1. Why this investigation matters most

§16 asks three questions:

> What problems was ArcSOC solving?
> Which of those problems still exist today?
> How should those problems be solved today?

There is a fourth question that turns out to be more valuable than all three,
and it is available to us for free:

> **Esri changed this architecture twice. What did they change, and why?**

The incumbent ran this model in production across thousands of deployments for
over a decade, hit its limits, and rewrote it — publicly, in documented
releases. That is the most useful evidence available to this project, and it is
better than any reasoning we could do unaided.

## 2. The model as it stood (9.x)

| Component | Role |
|---|---|
| **SOM** — Server Object Manager | Central manager process (`ArcSOM.exe`). Tracked which services ran on which containers, distributed load, started and stopped container processes. |
| **SOC** — Server Object Container | Machine hosting container processes. "All services run on all SOCs" — homogeneous, so every SOC machine needed uniform resources. |
| **ArcSOC** — container process | The actual worker process (`ArcSOC.exe`). Held one or more running service instances. |
| **ArcSOCMon** | Monitor. "The SOM and ArcSOCMon work together to constantly monitor the SOCs." |

**Request lifecycle:** client contacts the SOM; the SOM "finds a free service
instance to assign to the client for the lifetime of the transaction"; the
client works through that instance.

**Instance configuration**, set per service by the administrator:

- **Minimum and maximum instances.** On start, "the GIS server pre-creates and
  initializes the minimum number of instances." `VERIFY` default for map
  services: minimum 1, maximum 2.
- **Pooled services** — shared across application sessions, stateless
  operations only.
- **Non-pooled services** — an instance dedicated to a single application
  session, for stateful work such as editing.
- **Capacity** per SOC, limiting concurrently running instances on that machine.
- **Recycling** — "the server destroys, then re-creates, each instance in the
  service configuration", because "services may become corrupted and unusable".

## 3. Esri's own trajectory — the most important section

### 3.1 At 10.1: SOM and SOC were removed

The SOM–SOC model was replaced by the **ArcGIS Server site**: one or more
machines that all have the same software installed and work together as peers.
There is no separate manager role and no separate container role.

The stated reasons are worth quoting closely. The site architecture is
described as **more robust, reducing the chances of failure, and simplifying
the provisioning and recovery of new machines**. Installation lost its
post-install step, and its separate SOM, SOC and web services accounts.
Multi-machine deployment became: run the same installer everywhere, then join
the machines.

Read as engineering evidence, three lessons fall out:

1. **A distinguished central manager process was a liability.** It was a failure
   point and a recovery problem. Peer machines with no special roles replaced it.
2. **Heterogeneous machine roles were operational cost with insufficient return.**
   Deciding which machine is a SOM and which is a SOC, and configuring separate
   accounts for each, was complexity the architecture did not need.
3. **Provisioning and recovery are first-class architectural requirements**, not
   deployment details. They were named as reasons for a rewrite.

### 3.2 At 10.7: shared instances were added

Dedicated instances remained, and **shared instances** were introduced: a pool
of processes serving many services rather than one process per service.

The documented rationale is memory. Shared instances "conserve memory usage by
pooling several active server processes for use by multiple services, reducing
the memory usage of services that are not actively handling requests", and are
"recommended for services that receive infrequent requests, particularly when
the server site hosts many services".

Dedicated instances remain "ideal to use for services that receive constant or
particularly compute-intensive requests."

**That is exactly the hybrid model of §19, arrived at by the incumbent under
production pressure.** It is the strongest single piece of evidence available
for [ADR-007](../adr/ADR-007-service-runtime.md) — and, per §19, still not a
reason to assume it is our answer without measuring.

### 3.3 The arithmetic that forced it

`VERIFY` Each ArcSOC process is commonly cited at roughly **100–200 MB** of RAM.

With dedicated instances and a minimum of 1, every published service holds a
process whether or not anyone is using it:

| Services | Processes at min=1 | Memory at 150 MB (indicative) |
|---|---|---|
| 10 | 10 | ~1.5 GB |
| 100 | 100 | ~15 GB |
| **1,000** — our target | **1,000** | **~150 GB** |
| 5,000 | 5,000 | ~750 GB |

**This is the answer to our §24 service-explosion test, handed to us in
advance.** At our stated scale of 100–1,000 services, a process-per-service
model with a warm minimum is not tunable — it is arithmetically dead. Esri did
not add shared instances as an optimisation; they added it because the model
did not reach the scale customers had.

Our [ADR-007](../adr/ADR-007-service-runtime.md) should treat this as a settled
starting constraint rather than an open question, and spend its effort on the
harder problem: what the sharing unit is, and when to escalate to isolation.

### 3.4 What shared instances cannot do — the most informative constraint

The limits Esri documents on shared instances are more instructive than the
feature itself:

- "Only map and image services can be configured to use the shared instance
  pool. Other service types, such as geoprocessing services, are not supported."
- For map services, only certain capabilities qualify: "feature access, WFS, WMS,
  and KML".
- `VERIFY` Default pool size is derived from physical CPU cores, with guidance to
  consider **twice the number of physical cores**, and not to go below the core
  count.
- `VERIFY` Each shared instance caches service information, default **50 cached
  services per instance**.

The pattern behind those limits: **sharing works when per-service state is small
and can be cheaply bound and unbound; it fails when a workload holds heavy or
exclusive state.** Geoprocessing is excluded precisely because it does not fit
that shape.

This reframes the question our ADR-007 has been asking. "Shared or dedicated?"
is the wrong axis. The real axis is:

> How much per-service state must a worker hold, how expensive is it to bind and
> unbind, and does the workload tolerate a neighbour?

That question has different answers for feature, tile, render, raster and
geoprocessing workloads — which is precisely the §20 specialised-worker
hypothesis, now with supporting evidence.

The "50 cached services per instance" detail is the same insight in numeric
form: sharing is bounded by how many service contexts a worker can hold, not by
how many services exist. That is a design parameter we will have too.

## 4. Problems the model was solving — and their status today

| # | Problem | Still real? | How it should be solved today |
|---|---|---|---|
| P1 | Native GIS code crashes; a crash must not take down the whole server | **Yes**, and it is our A-007 | Process isolation for the paths that actually crash — native code on untrusted input (GDAL/raster) — rather than for everything uniformly. Isolation targeted by risk, not applied by default. |
| P2 | Expensive service initialisation must not happen per request | **Yes** | Warm workers, but with cheap binding. The real target is making initialisation cheap enough that this stops being an architectural problem. |
| P3 | Stateful editing sessions need a consistent context | **Mostly dissolved** | Transactions, optimistic concurrency and versioning in the database (§28). Pinning a process to a user session is solving a database problem in the runtime layer. Non-pooled services should not be reproduced. |
| P4 | Concurrency must be bounded so the machine is not overwhelmed | **Yes** | Bounded queues and explicit backpressure (§48), not an instance count standing in for a concurrency limit. |
| P5 | Long-running work must not block interactive requests | **Yes** | A job system (§36, ADR-011) and a separate worker class. Esri's exclusion of geoprocessing from shared instances says the same thing. |
| P6 | Memory leaks and state corruption in long-lived processes | **Partly** | Recycle on observed memory growth, not on a schedule. Scheduled recycling is a leak-concealment device: it keeps a defective process alive as policy and removes the pressure to find the defect. |
| P7 | Load distribution across machines | **Yes**, later | Deferred with ADR-012. The 10.1 lesson applies: do not do it with a distinguished manager process. |
| P8 | Administrator control over per-service resources | **Yes, but inverted** | See below. |

### P8 deserves its own note

Esri's model made **min/max instances per service the primary tuning surface**,
and the guidance is to "pare down the number of running service instances to as
many as are needed without affecting performance."

That is a per-service manual optimisation task. At 10 services it is
reasonable. At our target of 1,000 it is not work anyone will do, and our
primary user is a GIS administrator whose job is the estate, not the service.

**This is direct evidence for assumption A-008** (administrators will not
correctly hand-tune per-service settings). It should be promoted from a guess to
an evidence-backed assumption.

The inversion we should aim for: the system observes and adapts; the
administrator sets policy and limits, and overrides specific services when they
have a reason. Per-service tuning becomes an escape hatch, not the interface.

## 5. What to take, and what to refuse

**Take:**

- The core separation the master prompt already states in §17 — service
  definition is durable configuration, the worker is disposable. Esri's model
  had this concept; it bound the two too tightly in practice.
- The hybrid shared/dedicated model as the **starting hypothesis** for ADR-007,
  now with the incumbent's production experience behind it.
- Health monitoring as a distinct always-on responsibility (`ArcSOCMon` existed
  for a reason).
- Recycling as a capability — but triggered by evidence, not by the clock.
- Bounded per-worker service capacity as an explicit, tunable design parameter.

**Refuse:**

- **A distinguished central manager process.** Esri removed it and named
  robustness and recovery as the reasons. We should not reintroduce it.
- **Heterogeneous machine roles.** Same rationale.
- **Per-service min/max instances as the primary control surface.** It does not
  survive our scale target or our user.
- **Session-pinned instances (non-pooled).** The problem moved to the database
  and should stay there.
- **Uniform process isolation.** Isolate what actually crashes; do not pay for
  it everywhere.
- **Scheduled recycling as a default.** It hides the defect it mitigates.

## 6. Consequences for ADR-007

1. Process-per-service with a warm minimum is **excluded by arithmetic**, not by
   preference. §3.3 supplies the numbers. ADR-007 should record this as a
   settled constraint and move on.
2. The evaluation axis changes from "shared or dedicated" to **per-service state
   size, binding cost, and neighbour tolerance** — evaluated per workload class
   (§20). ADR-007 §4 should be rewritten around that.
3. Worker capacity — how many service contexts one worker can hold — becomes an
   explicit design parameter with a number, not an emergent property.
4. Escalation from shared to dedicated should be **driven by observed behaviour**,
   with an administrator override. Not by a form field filled in at publish time.
5. A-008 moves from `UNVALIDATED` guess to evidence-supported: the incumbent's
   own guidance requires exactly the manual tuning that will not happen at scale.
6. **No central manager process.** Routing and placement state must be
   recoverable without a distinguished node — even before clustering (ADR-012)
   is on the table, because the single-node design will otherwise bake it in.

## 7. Still to investigate

- `VERIFY` all numbers in §3.3 and §3.4 against current documentation.
- What ArcGIS Server does on **mass restart** — is startup staged, or does it
  thunder? Directly relevant to §26; not yet found in public documentation.
- Idle timeout and usage timeout semantics, and how a service returns from zero
  instances. The cold-start cost is the reason min=0 is not the default, and if
  we make cold start cheap we remove a whole category of tuning.
- How the 10.1 site model handles **placement** — which machine runs which
  service — without a SOM.
- Whether **containers per service** were ever offered later, and with what
  result.
- The equivalent runtime models in GeoServer, MapServer (CGI/FastCGI) and QGIS
  Server, for contrast. MapServer's process-per-request model is the opposite
  extreme and is worth studying for the same reasons.

## Sources

- [How the GIS server works — ArcGIS Server 10.0 help](https://help.arcgis.com/en/arcgisserver/10.0/help/arcgis_server_dotnet_help/0093/0093000000m8000000.htm)
- [How the GIS server works — ArcGIS Server 9.2 webhelp](https://webhelp.esri.com/arcgisserver/9.2/java/manager/administration/how_gis_svr_works.htm)
- [Guidelines for configuring ArcGIS Server components — 9.2](https://webhelp.esri.com/arcgisserver/9.2/java/manager/administration/guide_config_hardware.htm)
- [Tuning and configuring services — 9.2](https://webhelp.esri.com/arcgisserver/9.2/java/manager/publishing/tuning_services.htm)
- [What's new in ArcGIS 10.1 for Server](http://resources.arcgis.com/EN/HELP/MAIN/10.1/016w/016w00000036000000.htm)
- [What to expect when migrating ArcGIS Server 10.0 to later versions](https://enterprise.arcgis.com/en/server/10.7/deploy/windows/what-to-expect-when-migrating-arcgis-server-10-0-to-later-versions.htm)
- [Configure service instance settings — ArcGIS Enterprise 11.4](https://enterprise.arcgis.com/en/server/11.4/administer/windows/configure-service-instance-settings.htm)
- [Tune and configure services — ArcGIS Enterprise 11.1](https://enterprise.arcgis.com/en/server/11.1/publish-services/windows/tuning-and-configuring-services.htm)
- [Anticipate and accommodate users — ArcGIS Enterprise 11.5](https://enterprise.arcgis.com/en/server/11.5/deploy/windows/anticipating-and-accommodating-users.htm)
- [Introducing shared instances in ArcGIS Server 10.7 — Esri blog](https://www.esri.com/arcgis-blog/products/arcgis-enterprise/administration/shared-instances-arcgis-server-107) (referenced from search results; direct fetch returned 403)
