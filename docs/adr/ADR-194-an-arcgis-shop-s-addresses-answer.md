# ADR-194 — An ArcGIS shop's addresses answer: `/arcgis` and `/server/rest`, always

| | |
|---|---|
| **Status** | `ACCEPTED` |
| **Confidence** | `HIGH` |
| **Decided** | 2026-10-17, by owner decision (*"iyi de neden arcgis. arcgis yerine server yazsak olmuyor mu?"*, then *"yap"* to answering both) |
| **Supersedes** | [ADR-191](ADR-191-arcgis-pro-publishes-hosted-feature-layers.md) §5.8's *"the hosting server under `/arcgis`"* as part of `Graticula:PublishFromPro` |
| **Superseded by** | — |

---

## 1. Context

The ArcGIS gap review of 2026-10-17 listed *URL continuity* — `/arcgis/rest/services` — as missing. It was not:
ADR-191 had added the rewrite the same day, because Pro asked a hosting server's address under a path while
publishing. But it had added it **inside the switch for publishing from Pro**, so a deployment that turned
publishing off would have broken every old address without being told. The owner then asked why `/arcgis` at all,
and whether `/server` would not do instead.

## 2. Alternatives considered

### Alternative A — answer both, always (chosen)

**Argument for.** Neither name is this product's choice; both are written down in the clients. A stand-alone ArcGIS
Server has answered at `/arcgis` since 9.x, so it is what every Pro connection, ArcPy script, web map and bookmark of
one says. ArcGIS Enterprise's default Web Adaptor name for its hosting server is `server`, so an Enterprise shop's say
`/server/rest/services`. Answering both makes a migration a change of host name.

**Argument against.** Three addresses for one directory: logs and caches see the same document under each.

### Alternative B — `/server` instead of `/arcgis`

**Argument for.** One alias, and a word that is not another product's name.

**Argument against.** `server` is also ArcGIS's word, so it is not neutral; it trades the stand-alone shops'
addresses for the Enterprise shops'. The owner's question was answered with this, and the decision is both.

### Alternative C — neither; the root only

**Argument against.** Every address an ArcGIS shop has would need rewriting by hand, which is the migration cost the
compatibility layer exists to remove.

## 3. Counterarguments to the preferred option

- **`/server/` is this product's Server screens** (ADR-187, ADR-192). Only `/server/rest` is taken; `/server/`,
  `/server/services…` and `/server/roles` stay pages, and a test says so.
- **`/arcgis/admin` is not this server's `/admin`.** It is Pro's view of a hosting server's admin API and stays with
  publishing; with publishing off it is a 404 rather than this server's own admin API under a name that promises
  another.

## 4. Evidence

On the showcase, 2026-10-17, before the change: `/arcgis/rest/services`, `/arcgis/rest/info`, a layer and
`/arcgis/sharing/rest` answered 200; `/server/rest/services` answered the Server page. The Server screens use
`/server/service…` and `/server/roles`; nothing used `/server/rest`.

## 5. Decision

1. **`/arcgis/…` and `/server/rest/…` are rewritten to this server's root routes** before routing, whatever
   `Graticula:PublishFromPro` says. `/arcgis` itself answers a page, as an ArcGIS Server's base does.
2. **`/arcgis/admin/…` exists only while Pro may publish** (ADR-191 §5.8); otherwise it is a 404.
3. **The portal's `/portal` and `/studio` addresses stay with publishing**, as ADR-191 has them.
4. The rule is one function, `ArcGisAddresses.Map`, so the addresses and their tests are in one place.

## 6. Consequences

**Positive.** A shop moving from either ArcGIS Server or ArcGIS Enterprise keeps its addresses; turning off
publishing from Pro no longer breaks them.

**Negative.** A document reached under an alias names its links from the root, as before.

**State.** None: a rewrite of the request path, nothing stored.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | No future Server screen needs a path under `/server/rest` | `INFERRED` |

## 8. Dependencies

**Depends on:** [ADR-191](ADR-191-arcgis-pro-publishes-hosted-feature-layers.md), [ADR-192](ADR-192-a-screen-is-a-path.md).

**Depended on by:** —

## 9. Revisit triggers

- A shop whose Web Adaptor has another name (`/gis`, `/agsserver`) asking for it: that is a setting, not another
  constant.

## 10. Dissent

None recorded.

## 11. Conditions

None.
