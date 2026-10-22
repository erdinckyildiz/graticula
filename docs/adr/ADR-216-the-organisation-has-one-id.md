# ADR-216 — The organisation has one id, at every address

| | |
|---|---|
| **Status** | `ACCEPTED` |
| **Confidence** | `HIGH` |
| **Decided** | 2026-10-22, overnight, under the owner's *"ben uyurken açık konuları kapatır mısın?"* — the ArcGIS veteran's second review, finding N4. **That an existing deployment's id changes once is INFERRED as acceptable** (§5). |
| **Supersedes** | — |
| **Superseded by** | — |

---

## 1. Context

`portals/self.id`, and every item's `orgId`, was a hash of the request's origin with its path base
(`PortalEndpoints.PortalId`). Measured live on the demo by the veteran:

| Address | id |
|---|---|
| `/`, `/arcgis/` | `94D5EC659348BB87` |
| `/studio/` | `389D68AE296D8ECA` |
| `/portal/` | `20F52E65351E2CF0` |

One deployment was three organisations, and an internal and an external host name would be two more. Pro must
publish through `/studio`; browsers and the JS SDK use `/`. A client comparing `item.orgId` with `portal.id` —
organisation sharing, *my organisation* filters — compared two organisations. ADR-204's multi-tenancy builds on host
names and would compound it. In ArcGIS the id is a fixed property of the portal.

The derivation's own comment gave its reason — *"a client caches it and a portal whose id changes is a portal that has
been replaced"* — and then made an id that changed with the address.

## 2. Alternatives considered

### A — Made at random once, kept in `server_setting` *(chosen)*

**For.** Fixed for the deployment whatever address, path or host name a client uses; survives restarts and a
container replacement with the platform store; costs one named row in a table built for server-wide values
(migration 54).

**Against.** Every existing deployment's id changes once.

### B — Hash a canonical origin from configuration

**For.** Deterministic. **Against.** There is no canonical-origin setting, and one more value an operator must get
right — and change when the host name changes, which changes the organisation.

### C — Hash the host without the path base

**For.** `/`, `/studio` and `/portal` agree. **Against.** Two host names are still two organisations.

## 3. Counterarguments to the preferred option

- *"Changing the id breaks clients that cached it."* They cached one of three; whichever it was, two thirds of the
  clients already disagreed with it. Pro and the SDKs read the id from `portals/self` at sign-in.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| One id at `/`, `/arcgis`, `/studio`, `/portal` | Live: `23C60449A14346B8` at all four | fixture, 2026-10-22 |
| It survives a restart | Live: same after stop and start | same |
| Clients that compare orgIds agree | `PortalConformanceTests.The_organisation_is_one_at_every_address_the_portal_answers`, and the existing orgId test | conformance |

## 5. Decision

The organisation's id is sixteen upper-case hexadecimal digits made at random on the first start and kept as
`portal_id` in `server_setting` (`PortalIdentity`), read before the server takes requests. Every address answers it.
Where there is no platform store, the origin's hash remains the fallback. **INFERRED, for the owner:** that the one-time
change on an existing deployment — the showcase and dev included — is acceptable.

## 6. Consequences

**Positive.** `item.orgId` equals `portal.id` whichever path either was read through; organisation-scoped searches
agree between Pro and a browser.

**Negative.** After the upgrade, a client holding the old id until its next sign-in sees an organisation that does
not match; an `orgid:<old>` search in a saved query finds nothing.

**State.** One row in `server_setting` (`portal_id`), shared by every node; held in memory per node after start. Two
nodes starting together for the first time could each make one; each reads back after writing, so they agree unless
their writes interleave exactly — and nothing here has run on two nodes.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | Clients read the id from `portals/self` at sign-in rather than persisting it across sessions. | INFERRED |

## 8. Dependencies

**Depends on:** migration 54 (`server_setting`). **Depended on by:** ADR-204 (multi-tenancy), whose tenants each need
an id of their own — see its own conditions.

## 9. Revisit triggers

- Multi-tenancy is built: one id per tenant, not per deployment.

## 10. Dissent

None recorded.
