# D-121 — what HEAD answers, before and after

**Run 2026-08-24** against the development server, `curl -I` beside `curl` on the same address.
No load generator: the question is what a status line says, and a census of eight addresses
answers it.

[D-121](../../docs/architecture-debt.md) was opened as cosmetic — ArcGIS Pro sends `HEAD` before
its `GET`, read 405, and fell back. It stopped being cosmetic on 2026-08-20, when Pro's portal
connection failed on the same thing: Pro reads 405 on a discovery probe as a dead end, so a 405 on
`/sharing/rest/portals/self` stopped a connection whose `GET` answered 200 a moment later. Those
routes were fixed then. The rest of the server was not.

## Before

| Address | HEAD | GET |
|---|---:|---:|
| `/rest/services?f=json` | **405** | 200 |
| `/rest/services/hosted/look_buildings/FeatureServer/0?f=json` | **405** | 200 |
| `/rest/info?f=json` | **405** | 200 |
| `/healthz/live` | **405** | 200 |

## After

| Address | HEAD | GET | HEAD body |
|---|---:|---:|---:|
| `/rest/info?f=json` | 200 | 200 | 0 bytes |
| `/rest/services?f=json` | 200 | 200 | 0 bytes |
| `/rest/services/hosted/look_parcels/VectorTileServer?f=json` | 200 | 200 | 0 bytes |
| `/wms?service=WMS&request=GetCapabilities` | 200 | 200 | 0 bytes |
| `/wfs?service=WFS&request=GetCapabilities` | 200 | 200 | 0 bytes |
| `/sharing/rest/portals/self?f=json` | 200 | 200 | 0 bytes |
| `/arcgisuris.xml` | 200 | 200 | 0 bytes |
| `/server/` | 200 | 200 | 0 bytes |
| `/ogc/collections?f=json` | 404 | 404 | — |
| `/admin/featureservices` | 401 | 401 | — |
| `/rest/auth/login` (POST only) | **405** | 405 | — |

The last three are the interesting rows. **The property under test is that the two agree**, not
that they say 200: an absent collection and an unauthenticated request answer the same to both,
and a route with no `GET` at all still answers 405 — which is correct, and is what a blanket
"answer HEAD everywhere" would have got wrong. A `HEAD /rest/auth/login` returning 200 would tell
a client a credential was accepted.

**The headers are the same eight**, compared line by line on `/rest/info`: content type, and the
five security headers, and the date. HTTP asks for the headers a `GET` would send, and Kestrel
drops the body on its own — it decides that from the method it parsed, which is not the property
the middleware rewrites.

## What the rewrite costs

Nothing measurable: one string comparison per request, and a dictionary write on the HEAD ones.
There is no second dispatch — the request is routed once, as a `GET`.

## One thing seen and not chased

A single HEAD in an early run was logged `- 499`, *the client left*. Twenty HEADs and twenty GETs
afterwards produced **zero** of them on either method. So it is a race between the client's `FIN`
and the access log reading `RequestAborted` after the handler returned, and it is not specific to
HEAD — a `GET` whose client closes promptly can meet the same window.
[D-132](../../docs/architecture-debt.md)'s own note says `RequestAborted` alone cannot tell a
departure from an abort; this is a third case, where it cannot tell either from a normal finish.
Recorded here rather than repaired, because one observation in forty-one is a lead and not a
finding.
