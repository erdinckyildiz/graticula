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
