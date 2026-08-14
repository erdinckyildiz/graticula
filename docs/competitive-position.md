# Competitive Position

**Status:** FIRST PASS — and its conclusion is uncomfortable.
**Answers:** Q-49, insofar as it can be answered from desk research
**Required by:** fresh-challenger review G1, which found the assessment justifies
the *category* of GIS server and never justifies *this product*
**Clean room:** published documentation only (§5).

---

## 1. The field

`VERIFY` all figures and capability claims.

| | GeoServer | GeoNode | Honua | ArcGIS Enterprise | Us |
|---|---|---|---|---|---|
| Age / maturity | ~20 years, large base | Mature, OSGeo | 4,608 commits, 4 stars, no release | Decades, dominant | Nothing built |
| Licence | GPL | GPL | Elastic 2.0, open core | Commercial | Copyleft, TBD |
| Governance | OSGeo, multi-vendor | OSGeo | Single vendor | Esri | — |
| Self-service publishing | **No** | **Yes** | No | **Yes** | Planned |
| Item ownership and sharing | No | **Yes** — groups, fine-grained | No | **Yes** | Planned |
| Managed data store | No | Partly — ingests to its own DB | **No** — bring your own PostGIS | **Yes** — Data Store | Planned |
| ArcGIS FeatureServer surface | No | No | **Yes** | Native | Planned |
| Multi-provider write | Yes, via GeoTools | Via GeoServer | **No** — PostGIS only | Yes, via geodatabase | Planned |
| Architecture | One server | **CMS + GeoServer** | One server | Multi-component suite | One server |

## 2. The claim I was about to make, and why it fails

The working answer to Q-49 had become: *an open-source GIS server where users
publish their own content, not just administrators.* Neither GeoServer nor Honua
has that, and it is what made ArcGIS Enterprise spread inside organisations.

**GeoNode already does it.** `VERIFY`: users upload vector and raster data and
style it themselves; uploaded resources are "initially only visible by the
uploader"; there are groups, fine-grained permissions and sharing across teams.
It is GPL, OSGeo-governed and mature.

So self-service publishing is **not** a gap in open-source GIS. The claim is
withdrawn.

## 3. What survives, assessed honestly

### 3.1 Native versus bolted on — real, and hard to sell

GeoNode is a **Django CMS in front of GeoServer**. The self-service layer sits on
top of a server that has no concept of users, so permissions must be
synchronised between Django's model and GeoServer's security subsystem, and
there are two catalogs to keep aligned.

Our design puts ownership, sharing and publishing **inside** the server. That is
architecturally cleaner and shows up as operational simplicity — one process
model, one authorization model, one catalog, one thing to upgrade.

**But "we do in one system what they do in two" is a weak sales argument.** It is
felt by whoever operates it and invisible to whoever buys it. And GeoNode's
two-piece design buys them GeoServer's twenty years of protocol maturity for
free, which we would have to build.

### 3.2 The ArcGIS exit path — the sharpest thing we have

Consider an organisation running ArcGIS Server that wants out.

| What they need | GeoServer | GeoNode | Honua | Us |
|---|---|---|---|---|
| Their existing apps keep working (FeatureServer) | No | No | Yes | **Yes** |
| Self-service publishing they already have | No | Yes | No | **Yes** |
| Migration tooling | No | No | **Paid** | **Free** |
| Data stays in Oracle or SQL Server, writable | Yes | Via GeoServer | **Read-only** | **Yes** |
| Fully open licence | Yes | Yes | **No** | **Yes** |

**No one else fills that whole row.** GeoNode has the self-service half and no
ArcGIS compatibility. Honua has the ArcGIS half, charges for migration, keeps
non-PostGIS read-only, and is not open in the sense that matters to a public
body.

That is a coherent position: **the fully open ArcGIS Server exit path.**

It is a niche rather than a category. But it is a real, identifiable audience —
public bodies and utilities on ArcGIS Server, under licence-cost pressure, with
applications they cannot rewrite — and nothing currently serves it completely.

### 3.3 The runtime — real, and rarely felt

Workers flat in service count is genuinely better than ArcSOC and than a
GeoNode-plus-GeoServer stack at scale. An administrator with 80 services never
notices.

## 4. The honest conclusion

**Q-49 has now been attempted three times and has no strong answer.**

- *Multi-protocol access over PostGIS* — taken by Honua.
- *Self-service publishing* — taken by GeoNode.
- *Native rather than bolted-on* — true, and weak as a reason to switch.

What is left is §3.2: a specific niche, defensible, and much narrower than "a
next-generation enterprise GIS platform".

**This is a finding, not a failure.** G1 said it plainly: if the answer is thin,
discovering that now is worth more than any benchmark. Nothing has been built.
This is the cheapest moment it will ever be to change direction.

Three honest options:

1. **Accept the niche and build for it.** Target ArcGIS Server displacement
   specifically. It sharpens everything — FeatureServer compatibility becomes the
   priority, migration tooling becomes the wedge, and the runtime becomes a
   supporting claim rather than a headline. Narrower scope, clearer product.
2. **Test the premise before building.** Ask three GIS teams on ArcGIS Server
   whether they would move for this. Desk research cannot answer Q-49; only they
   can. Cheapest possible next step and the only one that produces evidence.
3. **Reconsider whether to build at all.** GeoNode plus GeoServer covers a great
   deal of this ground, free. If the answer is "we would build a slightly cleaner
   version of something that exists", that is worth knowing before Phase 1.

**Recommendation: 2, then 1.** The premise is testable and untested, and every
architectural decision after this one is cheaper to make with an answer than
without.

## 5. What this does not say

It does not say the architecture is wrong. Most of the decisions in this
repository would survive any of the three options above — the runtime, the query
engine, the storage model and the security model are all sound work and largely
independent of positioning.

It says the **product** has no demonstrated reason to exist yet, and that no
amount of further architecture will supply one.


---

## 6. Q-49 answered by the owner, 2026-08-13

**§4's honest conclusion was that desk research could not answer why this
product should exist. It could not, and did not need to.**

### Why it exists

> *"I will give this to the world, with better capabilities than GeoServer."*

The first half is sufficient on its own. A gift needs no market case, and QGIS,
GeoServer and PostGIS each began exactly this way. It is consistent with the
licensing posture and with the owner's stated motive throughout.

**It also dissolves a Phase 0 exit criterion.** §81 required Q-49 to be tested
with real GIS teams. That requirement assumed a commercial-style justification
— *is there a market worth the cost* — which a gift does not owe. The
conversations remain valuable for prioritisation; they are no longer a gate.

### The second half needs sharpening, and the reason is measurable

**On raw capability count we are behind GeoServer, and every good decision of
the last two days moved us further behind:**

| Decision | Capability GeoServer has | Does it matter? |
|---|---|---|
| Vector-first | Server-side raster rendering | **Deferred, not rejected** — see §6a. The owner prefers an ArcGIS MapServer-style rendered service to WMS |
| Q-47 | WMS in v1 | Yes for migration, and it is recorded as such. But the owner rejects WMS on its merits, not as a scope cut |
| Q-67 | Tiles from any registered store | Owner: no. Tiles are wanted from hosted data, which is where they are |
| Q-70 | Deployment without PostgreSQL | **Overstated in the first draft of this table.** PostgreSQL is bundled inside the appliance; the operator never installs or manages it, and data still comes from any of three engines. The narrow fact survives — a site forbidding PostgreSQL binaries anywhere cannot run us — but the operational impact is close to zero |
| Q-28 / A-016 | GDAL in the serving container | **Overstated in the first draft.** A-016 moves GDAL to the job-worker image; it does not remove format support. GDAL is available where conversion happens |
| Never planned | WCS, WPS, CSW, SLD/CSS/YSLD, the extension ecosystem | Genuinely absent, genuinely deliberate |

**Two rows of this table were wrong when first written, and the correction came
from the owner rather than from review.** The original version conflated
*capability removed* with *capability that matters*, which is the same mistake
the table was written to warn against. Recorded rather than silently edited,
because the failure mode — building a rhetorical case and then believing it —
is exactly what §2 of this document was already about.

What survives the correction: **on the axes GeoServer competes on today, we are
narrower**, and no amount of framing changes that. — **Qualified 2026-08-13
(sweep S8): this is now true of what is *shipped*, which is nothing, and false of
what is *scoped*.** Q-78 and Q-83 put full parity and more in scope hours after
this was written. The sentence was a scope claim when written and is a shipping
claim now; both readings are useful, and conflating them is not. Leading with *"better
capabilities than GeoServer"* in v1 would still be refutable in a minute by
anyone who knows GeoServer. What changes is the *horizon* over which the claim
becomes true — see §6a.

## 6a. The owner's positioning, and the two horizons

**2026-08-13.** Pressed on the table above, the owner made three points that
change the shape of the answer rather than the facts in it:

> *"I hate WMS. Super slow. Prefer ArcGIS MapServer capability."*
> *"We can design a better symbology."*
> *"Ours shall work on all DBs as well. PostgreSQL is a builtin db inside."*

**Recorded as a stated preference and direction, not as a decision.** ADR-004
remains `DEFERRED` and v1 scope is unchanged — confirmed by the owner on being
asked directly. What is now on the record is *why* WMS is out: it is rejected on
its merits, not merely cut for scope, and the preferred future shape is a
REST-style rendered map service in the manner of an ArcGIS MapService.

This gives the product two positioning horizons, and they should not be
conflated:

| | Claim | Status |
|---|---|---|
| **v1** | **The ArcGIS Server exit path.** FeatureServer compatibility including edits, free migration tooling, a real service runtime, never-degrade-silently. | True today, derived from decisions already taken, testable |
| **Later** | **Better rendered maps than GeoServer.** A fast REST map service with a symbology model that is not SLD. | **Not yet true, and the more interesting claim.** GeoServer's WMS genuinely is slow and its styling genuinely is painful, so this is a real capability comparison on an axis users care about |

**The owner's instinct on this is probably better than the sharpening in §6.**
That section argued the *"better than GeoServer"* claim was false, which is
correct for v1 scope and misses that the owner was describing a product the docs
do not yet contain. The honest position is that the claim is **premature rather
than wrong**.

**What it would cost, so the deferral is an informed one.** Un-deferring ADR-004
is not adding an endpoint. It reopens **Q-26**, cross-tile label consistency,
which is currently recorded as *closed, not answered* on the grounds that labels
are placed client-side — server-side rendering makes label placement ours, and
it is one of the genuinely hard problems in cartography. It makes symbology a
Tier 1 subsystem: style model, symbol library, label engine, font handling. It
puts fonts and glyph packs into the air-gapped checklist (Q-15). And rendering is
CPU- and allocation-heavy, which
[benchmarks/mvt-generation/RESULTS.md](../benchmarks/mvt-generation/RESULTS.md)
run 3 showed this runtime is not yet sized for (A-037).

None of that is an argument against building it. It is the reason it is a
separate decision with its own ADR rather than a scope note.

### The version that is true, testable, and prioritises the work

> **Everything an ArcGIS Server shop needs in order to leave, which GeoServer
> does not provide.**

Each of these is a genuine GeoServer gap rather than a preference:

| | Why GeoServer does not cover it |
|---|---|
| **Full ArcGIS FeatureServer compatibility, including `applyEdits`** (Q-17) | GeoServer has no ArcGIS REST surface. Existing clients keep working through the migration instead of being rewritten alongside it |
| **Free migration tooling** (Q-16) | Scan the estate, report honestly what can and cannot come across, import definitions. Honua charges for the equivalent; GeoServer offers none |
| **A real service runtime** (ADR-007) | Affinity routing, warmth-aware, bounded per-worker context budget, supervisor. GeoServer has no equivalent concept, and §3 of this document found that no existing GIS server does warmth-aware routing at all |
| **Never degrade silently** (ADR-008 §2) | Published capability reports and explicit refusal rather than quietly dragging data back to the server. A philosophy difference, not a feature |
| **Self-service publishing with a publisher role** | GeoNode provides this, but as a separate stack layered on GeoServer rather than as the server's own model |

This is narrow. That is the point: it is falsifiable, it tells us what to build
first, and it is a claim that survives contact with someone who knows GeoServer
well.

### What this still does not settle

The positioning above is derived from decisions already taken, not from anyone
outside the project confirming it matters. **§5's caveat stands: no GIS team has
been asked.** The difference Q-49's answer makes is that this is now a
prioritisation risk rather than an existential one. If nobody wants an ArcGIS
exit path, the project is still worth building and giving away — it is simply
built in the wrong order.


## 6b. The 2026-08-14 positioning makes this document harder, not easier

The owner's target is *people who cannot or will not pay for ArcGIS licences*
([product-context.md](product-context.md)). That sounds like it aims away from
Esri and therefore away from difficulty. It does the opposite, and §4's honest
conclusion needs re-reading in its light.

**Q-49's answer rested on a differentiator that this narrows.** It said, in as
many words, that *better capabilities than GeoServer* is **currently false and
getting more so** — and that the defensible claim was instead *everything an
ArcGIS Server shop needs in order to leave, which GeoServer does not provide*.
That claim survives only for the **can-no-longer-pay** population in
product-context's table. It says nothing to the **never-could** population,
which is the larger of the two and is already served — adequately, for free, and
for twenty years — by GeoServer, MapServer and QGIS Server.

**So for most of the stated market, we are entering GeoServer's market with
fewer capabilities than GeoServer.** WCS, WPS, CSW, SLD and its plugin ecosystem
are all absent from v1, several by decision. That is not an argument against the
positioning — it is the argument the positioning now has to answer, and it is
better to have it written down than discovered in a forum thread.

**What genuinely differentiates us for the never-could population**, on current
scope, is short and worth being honest about:

- **One deployable against one PostgreSQL** ([ADR-019](adr/ADR-019-portal-server-split.md)),
  where GeoServer plus a catalog plus a tile cache is three moving parts. For an
  organisation with no dedicated GIS administrator, this may be the whole
  argument.
- **A managed datastore and self-service publishing** (Q-69, ADR-018) — neither
  GeoServer nor MapServer has the ArcGIS-style item, ownership and sharing model
  we adopted yesterday.
- **Never degrade silently** (ADR-008 §2), which is a correctness posture rather
  than a feature, and which nothing in the field currently offers.

**What does not differentiate us for them:** ArcGIS FeatureServer compatibility,
which is the largest single body of work in v1. A QGIS shop that never had
ArcGIS does not need it.

That is not a call to cut it — the can-no-longer-pay population needs exactly
that, and Q-88 already committed the schedule. It is a call to notice that **the
v1 scope is currently optimised for the smaller half of the stated market**, and
that [Q-94](open-questions.md) is the question that follows.
