# ADR-072 — Pages on other origins may read service responses, never with credentials

| | |
|---|---|
| **Status** | `ACCEPTED` |
| **Confidence** | `MEDIUM` |
| **Decided** | 2026-09-15. The owner asked for the gaps an experienced ArcGIS user would see to be worked through (*"kalandan devam et"*), and this was on that list as *CORS yok*. **That cross-origin reads should work is the owner's, through that instruction. The default of every origin, the refusal of credentials and the closed administrative paths are `INFERRED`** and are listed for confirmation as [Q-151](../open-questions.md) |
| **Supersedes** | — |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

A review from an ArcGIS user's side, against the showcase on 2026-09-15, sent a request with
`Origin: https://example.com` to a public layer. No response carried
`Access-Control-Allow-Origin`, and `OPTIONS …/query` answered `405 Allow: GET, POST`. Nothing in
`/src` mentioned CORS and nothing in `/docs` had decided it.

The consequence is total for one kind of client: a Maps SDK for JavaScript application served from
any address other than this server's own cannot read a layer, a tile or a token response, because
the browser withholds every response it has not been told it may hand over. That is the first thing
a web developer meets, and it is not how the product this server is compatible with behaves:
ArcGIS Server has had an `allowedOrigins` setting since 10.1 whose default is every origin.

## 2. Alternatives considered

### Alternative A — every origin by default, no credentials, administrative paths closed *(chosen)*

**Argument for.** Matches what an ArcGIS client already expects of a server, so an application
that worked against ArcGIS Server works here without a configuration step nobody would know to
take. Refusing credentials keeps the one thing CORS can leak — a response a browser obtained with
the user's cookie — out of reach: `Access-Control-Allow-Credentials` is never sent, so a browser
neither attaches the session cookie to a cross-origin request nor gives a page the response to one
that carried it. The cookie is `SameSite=Strict` and honoured on GET and HEAD only besides. What a
page on another origin reads is what an anonymous caller reads, or what a token the page itself
holds reads — and a page holding a token could have asked its own server to fetch the same thing.

**Argument against.** It widens the set of pages that can make a visitor's browser query this
server. Those queries carry no credentials, but they do cost the server work and they come from
the visitor's network rather than the page owner's, which matters for a server reachable only
from inside an organisation: a public page could read an intranet server's anonymous layers
through an employee's browser.

### Alternative B — off by default, an operator names origins

**Argument for.** Nothing crosses an origin that an operator did not name, which answers the
intranet case above completely.

**Argument against.** The failure it produces is silent and lands on the wrong person. The
operator sees nothing; the web developer sees a browser console error that names a header, on a
server whose documentation they did not write. ArcGIS Server's users have never had to configure
this, so they will not look for the setting.

### Alternative C — every origin, with credentials, by reflecting the origin

**Argument for.** The console's session would work from other pages too.

**Argument against.** That is the configuration CORS exists to prevent: any page could read
anything the visitor's session can read. Refused outright.

## 3. Counterarguments to the preferred option

The intranet case is real and this decision does not answer it by default. An operator whose server
must not be readable through a visitor's browser has to set `Graticula__CorsOrigins` to `none` or to
a list, and nothing prompts them to. The defence is that the same server already answers those
layers to anybody who can reach it without a browser, so the default adds a path to data that is
already anonymous rather than exposing anything that was not — but a path through a browser on the
inside is a different path, and that is why confidence is `MEDIUM` and the default is listed as an
inference.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| No response allowed a cross-origin read | `curl -H "Origin: https://example.com" …/FeatureServer/0?f=json` — no `Access-Control-Allow-Origin`; `OPTIONS …/query` → `405` | Showcase, 2026-09-15 |
| ArcGIS Server allows every origin by default | The `allowedOrigins` server property, default `*` | Esri's public ArcGIS Server administration documentation |
| A credentialed cross-origin response is withheld without `Access-Control-Allow-Credentials` | The Fetch standard's CORS check | WHATWG Fetch, §3.2 |
| The console cookie is not usable across sites | `SameSite=Strict`, honoured on GET and HEAD only | `Authentication.CookieToken` |
| Preflights answer, errors stay readable, `/admin` does not answer, a list varies by `Origin` | `CrossOriginReadsTests`, in process through `TestServer` | `tests/Graticula.Host.Tests` |

## 5. Decision

`CrossOriginReads` runs ahead of the exception handler and every endpoint. For a request carrying
an `Origin` on any path outside `/admin`, `/server`, `/studio` and `/console`, it answers a preflight
with `204` and the methods and headers asked for, and adds `Access-Control-Allow-Origin` and
`Access-Control-Expose-Headers` (`ETag`, `Age`, `Retry-After`, `X-Tile-Cache`) to every other
response, including errors. `Access-Control-Allow-Credentials` is never sent. `Graticula__CorsOrigins`
is `*` by default; `none` turns it off; a comma-separated list of origins allows only those and adds
`Vary: Origin`. A value that is not an origin refuses to start.

## 6. Consequences

**Positive.** A Maps SDK application, a Leaflet or OpenLayers page, or a notebook in a browser can
read this server from its own address, as it could read ArcGIS Server.

**Negative.** A browser inside an organisation can be made to read an intranet server's anonymous
responses by a page outside it, until an operator sets the list (§3). Preflights are answered before
the access log and are not recorded. The console still cannot be driven from another origin, which
is intended and will surprise somebody who tries.

**State.** None in the catalogue. The allowed origins are read from configuration at startup and
held in memory on each node; nodes with different settings answer differently, which is the
operator's to keep consistent.
