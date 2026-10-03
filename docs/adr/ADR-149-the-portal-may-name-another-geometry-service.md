# ADR-149 — The portal may name another server's geometry service

| | |
|---|---|
| **Status** | `ACCEPTED` |
| **Confidence** | `HIGH` |
| **Decided** | 2026-10-03 by owner decision, asked directly (*"Evet, ayar olsun"*) |
| **Supersedes** | — |
| **Superseded by** | — |
| **Amends** | [ADR-022](ADR-022-geometry-server.md) (which geometry service the portal names) |

---

## 1. Context

ArcGIS clients signed in to a portal take its geometry service from `portals/self`'s `helperServices.geometry`. It was
always this server's own. ArcGIS Enterprise lets an administrator name another, under Utility Services, and the
reviewer's GeometryServer pass asked for it: a team moving to this server may want its existing ArcGIS Server's geometry
service while trying ours.

## 2. Alternatives considered

### Alternative A — A setting, named to everybody when set (chosen)

Settings has a Geometry service card. Empty, the portal names this server's own, offered as before only to a caller it
would answer. Set to a full address ending in `/GeometryServer`, the portal names that one to everybody: that server
answers under its own sharing. The address is checked for its shape and never fetched by this server. An http address
is refused when the portal is served over https, since browsers block it as mixed content; `/GeometryServer` is stored
in ArcGIS's spelling; and this server's own address, typed in, is stored as empty — the default.

### Alternative B — Proxy the other server's geometry service

**Against:** a proxy carries the other server's credentials and outages into this one; a link carries neither.

## 3. Counterarguments to the preferred option

- *A wrong address breaks clients' measuring*: it is the administrator's, as in ArcGIS, and the card says so.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| A MapServer address is refused saying the shape; a GeometryServer address is named in `helperServices`; cleared, this server's own is named again | `GeometryServiceSettingTests` | this repository |

## 5. Decision

As §2.

## 6. Consequences

**Positive.** A team can keep its ArcGIS geometry service while it moves.

**Negative.** None known.

**State.** A setting, `geometry_service`.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | The setting is wanted | Stated by the owner, 2026-10-03 |

## 8. Dependencies

**Depends on:** ADR-022.

**Depended on by:** —

## 9. Revisit triggers

- None.
