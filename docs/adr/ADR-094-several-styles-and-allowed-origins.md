# ADR-094 — A service may carry several styles, and a style may name hosts an administrator allowed

| | |
|---|---|
| **Status** | `ACCEPTED` |
| **Confidence** | `MEDIUM` |
| **Decided** | 2026-09-29, by owner decision. The owner asked for the vector tile face's remaining gaps to be closed one at a time; this is item 7 on that list, and the owner stated two things directly: **a service may carry several named styles, one of them the default**, and **a style may reference sources on other servers, but only hosts an administrator has allowed**. The storage shape, the addresses, the name rules, the origin rules and the console's policy are the session's design and are marked **INFERRED** where they matter (§5). |
| **Depends on** | [ADR-028](ADR-028-style-documents.md), [ADR-092](ADR-092-sprites-are-uploaded-per-service.md), [ADR-027](ADR-027-glyphs-and-sprites.md), [ADR-017](ADR-017-admin-api.md), [ADR-075](ADR-075-a-layer-is-edited-by-its-owner.md), [ADR-084](ADR-084-the-page-size-is-one-number.md) |
| **Discharges** | [ADR-028](ADR-028-style-documents.md) condition 2 |
| **Amends** | [ADR-028](ADR-028-style-documents.md) §5 (the external-URL rule); [ADR-092](ADR-092-sprites-are-uploaded-per-service.md) §5 (the sheet is checked against every style) |
| **Supersedes** | — |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

Two limits of the style document were written down on the day it arrived.

- **One style per service.** [ADR-028](ADR-028-style-documents.md) §3 called it *the strongest
  counterargument* and made it condition 2: *more than one style per service, before anybody is told this
  product supports theming. Light and dark is not an exotic request.* [A-071](../architecture-assumptions.md)
  waited for the first request for a dark theme. The owner made it.
- **No URL may leave this server.** ADR-028 §5 refuses every absolute URL in a style, for two reasons: an
  air-gapped deployment cannot fetch one (Q-15), and a style is handed to every viewer's browser, so an
  absolute URL lets whoever wrote the style send everybody's browser to a host of their choosing. A
  deployment that draws its data over a vendor's basemap tiles cannot express that in a style at all.

The owner's answer to the second keeps the reason and moves the decision: the rule stays the default, and
an **administrator** — not the style's author — may name the hosts a style may use.

## 2. Alternatives considered

### Named styles

#### Alternative A — every style in a new table, the old column migrated into it *(not chosen)*

`service_style (service_id, name, style, updated_at, is_default)`, with `service.style` copied in as the
default and dropped by a later contract.

**Argument for.** One place for every style; the default is a flag.

**Argument against.** A build before this one reads `service.style` and nothing else. After a rollback it
would serve the generated style for every service whose default had been written by the new build, and a
default written by the old build would be invisible to the new one when it came back. Keeping both columns
in step on every write is the second-writer problem [D-46](../architecture-debt.md) records, built on
purpose.

#### Alternative B — the default stays in `service.style`, the others go beside it *(chosen)*

`service.style` keeps meaning exactly what it meant: *the document `resources/styles/root.json` serves*.
`service.style_name` names it (null reads as `default`). Every other style is a row in `service_style`.
Making another style the default swaps the two documents in one transaction.

**Argument for.** An older build reads and writes the default correctly without knowing any other style
exists, so the rollback window stays open without a contract. Which style is the default is *where* it is
stored, so two defaults cannot be written.

**Argument against.** A style's name is unique across two tables, which no constraint can see; the writers
keep it under a row lock on the service (§5). Listing needs a union.

#### Alternative C — a portal item per style, as ArcGIS does *(not chosen)*

**Argument against.** For the reason ADR-028 §2 gave: there is no portal item model, and ADR-019 fused the
tiers to avoid building one for a JSON document.

### External sources

#### Alternative D — an administrator's allowlist of origins, checked on write and on serve *(chosen)*

**Argument for.** It is what the owner said. The person deciding where every viewer's browser may go is
the person responsible for the server, and the list is audited. With the list empty, ADR-028's rule is
unchanged, so an air-gapped deployment is unaffected.

**Argument against.** An allowed host can serve anything, including a TileJSON that points elsewhere (§3).

#### Alternative E — let a publisher allow a host per style *(not chosen)*

**Argument against.** It is ADR-028 §5's objection exactly: the author of a style choosing where every
viewer's browser is sent.

#### Alternative F — proxy external sources through this server *(not chosen)*

**Argument for.** The browser never leaves this server, and the CSP never widens.

**Argument against.** It makes this server an open fetcher of whatever an allowed host answers, which is a
server-side request surface ADR-028 never had, and it doubles the traffic of every basemap tile. Nobody
asked for it.

## 3. Counterarguments to the preferred option

- **An allowed origin is trusted with everything it serves.** A `sources.*.url` names a TileJSON; the tile
  URLs inside it are read by the browser, not by this server, and may name any host. The same is true of a
  sprite index or a glyph range's redirects. The allowlist decides which hosts a style names, not which hosts
  those hosts send the browser to. The console's Content-Security-Policy is the second fence for the console's
  own pages, and it blocks exactly that second hop there; an ArcGIS or MapLibre client elsewhere has only its
  own page's policy.
- **A DNS name can resolve to a private address.** The rule refuses private, loopback, link-local and
  reserved **IP literals**, and `localhost`. A host name that resolves to `10.0.0.5` is accepted, because the
  browser resolves it, not this server, and an administrator allowing an intranet tile host is a legitimate
  use. The refusal of literals guards against a pasted metadata address, not against an administrator.
- **A wildcard trusts every name under a host**, including one somebody forgets to renew and somebody else
  takes over. It is allowed because tile vendors commonly spread load across subdomains; it needs two labels
  after the `*.`, and it does not match the host itself — the reading a Content-Security-Policy gives the same
  text. **INFERRED**; drop it if the owner prefers exact hosts only.
- **Two parsers must agree on the host.** This server reads a URL's host with its own tokenizer; the browser
  uses the WHATWG parser. Rather than bet they agree, every input on which they could disagree is refused —
  `@`, `\`, `%`, whitespace and control characters, a bracketed IPv6 literal, a host ending in a number that is
  not a plain dotted quad (which WHATWG would read as an address) — and what remains is read the same way by
  both. The tests are the classic tricks (`https://allowed.com@evil.com`, `https://allowed.com.evil.com`).
- **Removing an origin does not edit anybody's style.** A stored style that names a removed origin stays
  stored, byte for byte, and stops being served; the generated style is. That is ADR-028 condition 3's answer
  for a style that no longer fits, and the read-back says why. The alternative — refusing the removal while a
  style names it — would make withdrawing trust from a host wait on every publisher.
- **Thirty seconds on another node.** The list is held for thirty seconds, as the page size is (ADR-084). A
  node that did not take the write keeps serving the old list that long.
- **ArcGIS has no address for a second style**, so `resources/styles/{name}.json` is ours (§4). An ArcGIS
  client never asks for it and is not disturbed; a MapLibre client has to be given the address.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| ArcGIS has no convention for more than one style on a VectorTileServer | The VectorTileServer document gives `defaultStyles: "resources/styles"`, and the *Vector Tile Style* child resource describes one style per service. Other styles of the same tiles are separate portal items, each with its own `root.json`. No named-style path is documented | [VectorTileServer](https://developers.arcgis.com/rest/services-reference/enterprise/vector-tile-service/), [Vector Tile Style](https://developers.arcgis.com/rest/services-reference/enterprise/vector-tile-style/) — read 2026-09-29 |
| An old build keeps working after a rollback | `Named_styles_round_trip_and_the_column_always_holds_the_default` asserts after every write and switch that `service.style` — the only thing an older build reads — holds the default | this change. **Compiled, not yet run**: needs a database |
| Names are one per service without case, and the store refuses a bad one from a second writer | `The_store_refuses_a_bad_name_and_a_second_spelling_of_one`; `StyleNamesTests` | this change (the store half needs a database) |
| An origin entry is one https origin, normalised | `StyleOriginsTests`: case, trailing dot, trailing slash, default port, IDN to punycode; refused: http, a path, user information, a port out of range, `*.com`, a mid-label wildcard, `_`, `%`, IPv6 literals, `localhost`, RFC 1918, loopback, link-local, shared, and the decimal, hex and short forms of an address | this change |
| A URL resembling an allowed origin is not admitted | `StyleOriginsTests.A_url_that_only_resembles_the_allowed_origin_is_not_admitted`: `https://allowed.com@evil.com`, `https://allowed.com.evil.com`, `https://evilallowed.com`, a backslash, a tab, a percent-encoded slash, a subdomain without a wildcard | this change |
| The style check reads every place a style names a URL | `StyleDocumentOriginTests`. **Found while writing this:** an array `sprite` and a video source's `urls` were never read, so a style could name any host through either and pass ADR-028's check; an inline GeoJSON `data` object threw instead of being accepted | this change |
| A removed origin stops a style being served; the console's policy follows the list | `EveryStyleOfAServiceIsCheckedTests`; `NamedStyleConformanceTests` | this change. **Conformance written and compiled, not yet run against a live server** |
| The sheet is held by every style | `EveryStyleOfAServiceIsCheckedTests.A_sheet_that_only_the_default_would_accept_is_refused_for_the_other_style`; `NamedStyleConformanceTests.A_sheet_is_held_by_an_icon_any_style_names` | this change |

## 5. Decision

**A vector tile service may carry up to twenty named styles, one of them the default; and a style may name
https origins an administrator has allowed, checked where it is stored and again where it is served.**

- **Storage — migration 62, expand.** `service.style` stays the default's document and gains
  `style_name` (null reads as `default`). Other styles are rows in
  `service_style (service_id, name, style, updated_at)`, removed with the service, bounded at 1 MiB like the
  column, unique on `(service_id, lower(name))`. Both carry the name rule as a check constraint. Every writer
  of a service's styles takes `select … for update` on the service row first, which is what keeps a
  non-default style from taking the default's name. **Rollback:** a build before 62 reads and writes
  `service.style` as the default and ignores the rest; the minimum reader does not move. **INFERRED**: this
  shape rather than Alternative A.
- **Names** (`StyleNames`). Letters, digits, `-` and `_`, starting with a letter or digit, at most 40
  characters, compared without case, stored as first written. `root` is refused: `root.json` is the default's
  ArcGIS address whatever it is called. **INFERRED**: the pattern, the length, and the bound of twenty.
- **Serving** (`VectorTileEndpoints`). `resources/styles` and `resources/styles/root.json` serve the default,
  as before. `resources/styles/{name}.json` serves a named style — the default too, by its own name. Both run
  ADR-028 condition 3's check — layers, the 1x sprite sheet, and now the allowed origins — and a style that
  fails is replaced by the generated one with `Graticula-Style-Stale`. An unknown name is 404. **ArcGIS has no
  address for a second style (§4), so this path is ours**, and nothing is added to the VectorTileServer
  document: a field no ArcGIS client reads would be a claim about the protocol Q-17 does not let this server
  make. The list is on the admin API and the console only.
- **Admin API**, behind `content:publishFeatures` and ADR-075's manage check, addressed by `?folder=` and
  name (D-275), written by the service's id after the check (D-276), audited:
  - `GET /admin/services/{name}/styles` — every style, which is the default, each one's size, date, public
    address, and why it is not served when it is not.
  - `GET /admin/services/{name}/styles/{style}` — the document byte for byte, with `Graticula-Style-Stale`
    and `Graticula-Style-Default`. 404 for a name the service does not carry.
  - `PUT /admin/services/{name}/styles/{style}` — 400 for a bad name or a refused document, 413 past 1 MiB,
    **409** past twenty styles. **A service with no default takes the first style stored as its default**,
    and the answer says so.
  - `DELETE /admin/services/{name}/styles/{style}` — 404 when absent. Removing the default puts the generated
    style back at `root.json` and promotes nothing.
  - `PUT /admin/services/{name}/default-style` with `{"style": "dark"}`, or `{"style": null}` for the generated
    style; the old default is kept as an ordinary style. 404 for an unknown name, **409** for a style that
    would not be served.
  - **`/admin/services/{name}/style` stays**, as an alias for the default whatever it is called — the address
    every console, script and test uses (ADR-020 §5c). `PUT` stores the default (named `default` when there was
    none); `DELETE` removes it. `GET` adds `Graticula-Style-Name`.
- **Allowed origins** (`StyleOrigins`, `StyleOriginList`). One named value, `style_origins`, in
  `server_setting` — migration 54's table, made so a server-wide setting arrives without a migration (ADR-017
  §5c). `GET` and `PUT /admin/settings/style-origins` (`{"origins": [...]}`), behind `admin:manageServer`,
  audited as `server.settings`; the console's place is **Server › Settings › Style sources**. An entry is
  `https://host`, `https://host:port` or `https://*.host`: https only, no path, no user information, no IP
  literal in a private, loopback, link-local, shared or reserved range, not `localhost`, a wildcard only as the
  whole first label with two labels after it, at most fifty entries, stored normalised (lower case, punycode,
  no default port). **INFERRED**: the wildcard, the bound, and allowing public IP literals.
- **The style check** (`StyleDocument`). `glyphs`, `sprite` (a string, or since MapLibre's multiple sprites an
  array of `{id, url}`), and each source's `url`, `tiles`, `data` and `urls` must be a relative URL on this
  server or an https URL whose origin is on the list. A refusal names the origin and where an administrator
  allows it. **INFERRED**: `data` and `urls` are held to the same rule as the four the owner listed, because
  each is a URL the browser fetches.
- **The console's policy.** `/server` and `/studio` pages carry a Content-Security-Policy whose `connect-src`
  and `img-src` now include the allowed origins, read from the same list, so the console's maps draw a style
  the server serves. Nothing else widens. The list read fails closed: when the store cannot be read and nothing
  is held, the list is empty — styles naming an external origin are then served as the generated style and
  the policy stays narrow.
- **The sprite sheet (amends ADR-092 §5).** A sheet replaced or removed is refused with **409** when *any* of
  the service's styles names an icon it would take away by literal, and the refusal names the styles. The
  admin read-back's `styleUses` covers every style.
- **Console.** Studio's symbology rail: the override fold lists the service's styles, stores under a chosen or
  a new name, deletes one, and makes one the default; the folder travels on every call. Renaming is not
  offered — store under the new name and delete the old one. **INFERRED**.

## 6. Consequences

**Positive.**
- Light and dark on one service. ADR-028 condition 2 is discharged; A-071 has its answer.
- A style can draw a vendor's basemap under this server's data, where an administrator allowed it.
- The URL check reads two spellings it had missed, which closed a way around ADR-028 §5 that existed before
  this decision.
- A rollback to the previous build keeps every default style.

**Negative.**
- An allowed origin is trusted with what it serves, including hosts its TileJSON names (§3).
- A style's name is unique across two tables by a lock, not a constraint.
- A named style is read from the store on each request, with no remembered copy; while the store is
  unreachable it fails (503) rather than falling back.
- An older build that clears the default leaves `style_name` behind; it is ignored while there is no default
  document, and the next default stored through a new build replaces it.
- The console's policy costs a held read of the list on each page request.

**Ports created.** None.

**State.** *Catalogue*: `service.style_name`, the `service_style` table, and the `style_origins` row of
`server_setting`. *Runtime*: the list is held for thirty seconds per node.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| A-071 | One style per service is enough to be useful | **Answered 2026-09-29 by this ADR** — the owner asked for several |
| — | Twenty styles per service and fifty allowed origins are above any real deployment | Reasoned, not measured |
| — | An administrator who allows an origin knows what that host serves | Unvalidated, and the reason §3's first counterargument is written down |

## 8. Dependencies

**Depends on:** [ADR-028](ADR-028-style-documents.md) (the style document and its checks),
[ADR-092](ADR-092-sprites-are-uploaded-per-service.md) (the sheet the icons are checked against),
[ADR-017](ADR-017-admin-api.md) §5c (runtime state through the API), [ADR-084](ADR-084-the-page-size-is-one-number.md)
(`server_setting`, and the thirty-second hold), [ADR-075](ADR-075-a-layer-is-edited-by-its-owner.md) (who may change a
service), [ADR-020](ADR-020-admin-console-and-service-status.md) §5c (frozen URLs, which keeps `/style`).

**Depended on by:** —

## 9. Revisit triggers

- Somebody asks for a style shared by several services, or for a style item a portal lists. Alternative C
  comes back.
- An allowed host is found sending viewers' browsers to a host nobody allowed. The first counterargument has
  cost something; a proxy (Alternative F) or a TileJSON rewrite is the next step.
- An administrator needs an exact-host-only mode, or a wildcard is abused. The wildcard goes.
- A client needs to discover a service's styles through the ArcGIS face. Then a field there is a protocol
  question, and Q-17 is where it is answered.

## 10. Dissent

None recorded.
