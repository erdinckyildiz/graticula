# ADR-068 — Responses are compressed, on an allowlist rather than everywhere

| | |
|---|---|
| **Status** | `ACCEPTED` |
| **Confidence** | `HIGH` |
| **Decided** | 2026-09-13, by owner decision — response compression, alongside two other
  changes other agents were making the same night (query-response `Cache-Control`/`ETag`,
  GeoParquet vector tiles) |
| **Supersedes** | — |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

Every ArcGIS JS SDK polygon layer draws by sending many `FeatureServer` `query` requests
(`f=json`, `resultType=tile`), and the server answered every one of them **uncompressed**, even
when the browser sent `Accept-Encoding: gzip, br`. Measured on real tile responses captured from
the showcase: a 3,937,039-byte response is 1,608,820 bytes with gzip-6 (2.4x, 197 ms) and
1,373,852 with brotli-4 (2.9x, 84 ms); a 633,260-byte one is 186,284 gzip-6 / 170,668 brotli-4
(3.7x, 21 ms). A grep of `/src` before this change found no response compression anywhere, and no
ADR had ever decided on it — it was simply never considered, which is a different fault from
having been considered and rejected.

This server is HTTPS-only by default (`settings.RequireHttps`), and compression over HTTPS is
BREACH's precondition: a compressed response, a secret in the body, and attacker-influenced
input reflected into it lets an attacker recover the secret byte by byte from the compressed
length. So the question this ADR answers is not just *should responses be compressed* but *which
ones, and how is the rest kept out of reach of that*.

## 2. Alternatives considered

### Alternative A — compress everything, deny-list the secret-bearing responses

Turn on `ResponseCompressionMiddleware` for the whole app and exclude
`/rest/auth/*`, `/rest/generateToken`, `/sharing/rest/generateToken`, `/rest/setup` and
`/admin/*` by path.

**Argument for.** Every future data-bearing endpoint is compressed automatically; nothing has to
be added by hand as routes are added.

**Argument against.** The list has to be *complete* and stay complete. A new endpoint that
returns a token — the next sign-in variant, the next admin surface that echoes back a
credential — is compressed by default until somebody remembers to add it to the exclusion list,
and the failure is silent: nothing breaks, nothing is refused, a response is merely compressed
that should not have been. That is the shape of every access-control gap this repository's own
debt register warns about (§62's *"an undocumented permanent decision wearing a disguise"* is the
same shape from the other direction) — a list whose omissions are invisible until exploited.

### Alternative B — the allowlist (chosen)

Nothing is compressed unless its path is named in `ResponseCompressionPolicy.IsAllowed`:
`/rest/services` (FeatureServer, MapServer, GeometryServer, VectorTileServer),
`/ogc/features/v1` (OGC API Features), `/wfs`, `/wms`, and the two static console
surfaces `/server` and `/studio`.

**Argument for.** A new endpoint is excluded by default. An author who wants it compressed has
to add it — which means the one that returns a credential has to be a *deliberate* choice rather
than an omission nobody noticed. `/rest/auth/*`, `/rest/generateToken`,
`/sharing/rest/generateToken`, `/rest/setup`, `/rest/whoami`, the console's cookie→token exchange
at `/rest/auth/session`, and everything under `/admin` are excluded by never having been added —
nothing had to be written by hand to keep BREACH away from a token, which is the opposite of
Alternative A's failure mode.

**Argument against.** A genuinely large, genuinely secret-free response outside the six surfaces
above (there are none known today) goes uncompressed until somebody adds its path. That is a
missed optimisation, not a hole — the cost of being wrong is a slower response, not a leaked
credential, and that asymmetry is the whole argument for B over A.

## 3. Counterarguments to the preferred option

**The allowlist is coarse — `/rest/services` also serves images, tiles and attachments.**
`MapServer/export` answers PNG, `ImageServer/exportImage` answers PNG, `AttachmentEndpoints`
answers arbitrary uploaded bytes, and `VectorTileEndpoints` answers MVT — all under the same
path prefix that is compressed. A path allowlist alone would compress an already-compressed PNG
for no gain and some CPU cost, and would compress an attacker-supplied attachment through a
compression oracle without any of the JSON/XML shape that makes BREACH practical (the attacker
does not control the compressed *plaintext* the way a token-in-JSON case would, but this was not
proven, only reasoned about) — see §9's benchmark trigger.

**Two independent gates is more to reason about than one.** The path allowlist and the MIME-type
allowlist (`ResponseCompressionPolicy.MimeTypes`) both have to admit a response before it is
compressed. That is deliberate — see §5 — but it means a reader has to check two lists to answer
*is this compressed*, not one.

**`IHttpsCompressionFeature` was the first design and it was wrong, twice, before this one was
measured to work.** See §4 — the framework's own per-request override mechanism does not behave
the way its name suggests, and a reader trusting the obvious reading of that API would ship the
BREACH exposure this ADR exists to prevent.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| Uncompressed query responses are large and compress well | 3,937,039 B → 1,373,852 B brotli-4 (2.9x, 84 ms); 633,260 B → 170,668 B brotli-4 (3.7x, 21 ms) | Owner's measurement on live showcase captures, cited in the task that produced this ADR |
| `CompressionLevel.Fastest` is the right level for a per-request cost, not `SmallestSize` | Re-run against a 633,260 B capture and a 7,681,042 B capture: brotli Fastest 167,289 B (3.79x) in 11 ms / 3,198,832 B (2.40x) in 41 ms; brotli SmallestSize 137,622 B (4.60x) in **898 ms** / 2,273,559 B (3.38x) in **12,567 ms** | `ResponseCompressionPolicy`'s own remarks; measured on the same captures, `scratchpad/bench` |
| `IHttpsCompressionFeature.Mode = DoNotCompress` does not disable compression once `EnableForHttps = true` | Set on an excluded path in an in-process `TestServer`; the response was compressed anyway | `tests/Graticula.Host.Tests/ResponseCompressionPolicyTests.cs`, development history of this change |
| `IHttpsCompressionFeature.Mode = Compress` does not enable compression while `EnableForHttps = false` either | Same harness, opposite direction; the response was never compressed | Same |
| `UseWhen`-branched registration works in both directions | Three tests: compressed when asked, not compressed when not asked, never compressed on an excluded path regardless of what is asked | Same file, falsified and restored per test — see §6 |
| No response in `/src` sets `Content-Encoding` before this change | `grep -rn "Content-Encoding" src/Graticula.Host` returned nothing | This change's own investigation |

## 5. Decision

`Microsoft.AspNetCore.ResponseCompression` (base class library, no new production dependency) is
registered with Brotli preferred, gzip the fallback, both at `CompressionLevel.Fastest`, and
`EnableForHttps = true`. It is wired into the pipeline with
`app.UseWhen(context => ResponseCompressionPolicy.IsAllowed(context.Request.Path), branch =>
branch.UseResponseCompression())` in `Program.cs`, placed immediately after `app.UseRouting()`
and ahead of the exception handler and the access log, so a request outside the allowlist never
passes through the compression middleware at all — there is no per-request feature to set or
misread, and every response this server sends that carries a secret is excluded by construction
rather than by an exclusion list somebody has to maintain.

`ResponseCompressionPolicy.IsAllowed` admits `/rest/services` (FeatureServer, MapServer,
GeometryServer, VectorTileServer query and metadata), `/ogc/features/v1` (OGC API Features),
`/wfs`, `/wms`, and the two static console surfaces `/server` and `/studio`.
`ResponseCompressionPolicy.MimeTypes` is the framework's default set
(`text/plain`, `text/css`, `application/javascript`, `text/html`, `application/xml`, `text/xml`,
`application/json`, `text/json`, `image/svg+xml`) plus `application/geo+json`,
`application/problem+json` and `application/gml+xml` — the three GeoJSON/XML media types this
server answers with that the default set does not already cover. Both gates have to admit a
response before it is compressed; see §3 for why two.

**Vector tile bytes (`application/vnd.mapbox-vector-tile`, `application/x-protobuf`) are
deliberately excluded** — not in `MimeTypes`, even though their path (`/rest/services`) is on the
allowlist. No response in `/src` sets `Content-Encoding` today, so double-encoding is not the
reason; there is simply no measurement yet of what compression buys on a format that is already a
packed binary structure, and the GeoParquet vector tile work (a separate change, the same night)
was active in `VectorTileEndpoints.cs` while this was written. Left for a benchmark once that
work has landed — see §9.

**Already-compressed media is excluded by the same MIME-type gate without a special case**: PNG,
JPEG, WebP, zip and Parquet bytes carry no MIME type in the allowlist, so they pass through
`/rest/services` uncompressed exactly as they did before this change.

**Streaming is preserved.** `FeatureServerQueryWriter` writes to `context.Response.Body` through
a `Utf8JsonWriter`, flushing on a 32 KB threshold (ADR-062), and
`ResponseCompressionMiddleware` wraps the response body stream with a compressing stream that
writes through as bytes are produced rather than buffering the whole response — this is the
middleware's own documented design, not something this change re-implements, and the 41 ms
figure against a 7.68 MB capture above is consistent with a stream that is not buffered end to
end before the first byte leaves the process.

**`Vary: Accept-Encoding` is added by the middleware itself** on every response it compresses.
This matters beyond the immediate request: `FeatureServerQueryWriter`'s query responses are
gaining `Cache-Control`/`ETag` the same night, in parallel, and a cache that stored a brotli body
against a URL with no `Vary` would serve it to a client that never asked for brotli.

## 6. Consequences

**Positive.** The measured showcase workload — many `FeatureServer` `query` tile requests per
polygon layer draw — gets a 2.4–3.8x reduction in bytes on the wire for single-digit-to-double-
digit milliseconds of CPU, which is the dominant cost for a client on a slow or metered
connection loading a map. `Vary: Accept-Encoding` protects the concurrent caching work from
serving a compressed body to a client that did not ask for one.

**Negative.** CPU is spent compressing on every request rather than once; `Fastest` was chosen
specifically to keep that cost small (§4), but it is not zero, and a deployment that is CPU-bound
rather than bandwidth-bound gains nothing from this change and pays a little for it. The
allowlist is a second thing to update when a new data-bearing, secret-free endpoint is added —
Alternative A's cost moved from *the exclusion list is silently wrong* to *the allowlist is
silently incomplete*, which is the safer direction to be wrong in but is still a maintenance
surface. Vector tiles are not compressed pending a benchmark that has not been run.

**Ports created.** None — `Microsoft.AspNetCore.ResponseCompression` is Tier 1 configuration
(the host composition root), not a Tier 2 adapter behind a port: nothing in this repository's
domain code calls into it, and `Program.cs` is the only file that references its types beyond
`ResponseCompressionPolicy` itself.

**State.** None. *Catalogue*: nothing — the allowlist and the MIME types are compiled into the host, and nothing about compression is stored or configurable. *Runtime*: per request only, the compressor's buffer for the life of the response; node-local by construction and nothing to share.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | No assumption in [architecture-assumptions.md](../architecture-assumptions.md) names response compression; none added — this ADR does not rest on a listed assumption beyond ADR-014's plain-HTTP-is-for-development-only stance, which `EnableForHttps = true` here continues rather than revisits | N/A |

## 8. Dependencies

**Depends on:** ADR-062 (the query face streams) — this decision's claim that compression does
not defeat streaming rests on that ADR's own design still holding. ADR-034 (Server and Studio) —
the two static console surfaces this ADR compresses.

**Depended on by:** none yet. The GeoParquet vector tile work (concurrent, undecided ADR number
at the time of writing) should read §5's vector-tile exclusion before deciding whether its own
tile bytes need a `Content-Encoding` story.

## 9. Revisit triggers

- **A benchmark measuring brotli/gzip against representative MVT bytes**, once the GeoParquet
  vector tile work has landed — if it shows a worthwhile reduction at an acceptable CPU cost,
  `application/vnd.mapbox-vector-tile` and `application/x-protobuf` are added to
  `ResponseCompressionPolicy.MimeTypes`.
- **A new data-bearing, secret-free endpoint outside the six allowlisted surfaces** — add its
  path to `ResponseCompressionPolicy.IsAllowed` deliberately, with the same reasoning about what
  it does and does not carry that this ADR gives for the six that are already there.
- **If profiling under real load shows compression CPU cost competing with query CPU cost** —
  `Fastest` was chosen from a benchmark against JSON-shaped captures at rest, not against this
  server's actual concurrency profile; if p95 latency at the server's measured peak (942
  requests/second, D-196) rises after this change, that is the number to look at first.

## 10. Dissent

None recorded. This is an owner-directed addition; no adversarial review has run against it yet.
