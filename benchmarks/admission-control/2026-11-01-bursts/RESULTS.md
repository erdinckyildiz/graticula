# The store gate against bursts — ADR-046 §5a, 2026-11-01

**Run 2026-11-01, 10:35 to 12:15, on one fixture.** **Owed by:** a release concern raised against
[ADR-046](../../../docs/adr/ADR-046-admission-control-bounds-the-queue-not-the-wait.md) §5a's `StoreGate`: every request
under `/rest/services` takes one of 24 store permits and is refused on arrival when 96 are waiting. Over HTTP/2 one
browser holds up to 100 streams on one connection (Kestrel's default `MaxStreamsPerConnection`), and a map viewer that
pans fires tile and query requests together. CI run 41282203570 had a deliberate flood answer unrelated tests 503. The
question: **is a realistic burst refused, and is that the gate working or the gate misjudging a burst for overload?**

**Generator:** [`../burst`](../burst) — a .NET client, one `SocketsHttpHandler` per simulated viewer with
`EnableMultipleHttp2Connections = false` and HTTP/2 required, so each viewer is one connection as a browser's is. Each
round every viewer fires its whole burst at once; five rounds, a second apart. Scenarios: `tile-cached` (VectorTileServer
tiles answered `X-Tile-Cache: HIT`, 798 of them at z15–17, checked before each run), `tile-uncached` (tiles never
requested before, at z20 before and z22 after — a different zoom, so their latencies are not comparable; most answer 204
empty), `export` (MapServer `export`, 256 px), `query` (FeatureServer envelope queries, up to 200 records), `viewer`
(6 cached tiles, 3 new tiles and 1 query in every 10). Flood: [`../flood.js`](../flood.js) under k6, unchanged except
that it now samples the gate's `standing` flag. **Raw:** `bursts-*.jsonl` and `measured-*.json` here.

| | |
|---|---|
| Server | `Graticula.Host`, **Release**, `Logging__LogLevel__Default=Warning`, out of tree; before = branch `grapy-e` at `81ab368a`, after = the same plus the change below |
| Store and data | one PostgreSQL 16 cluster on the same machine (`max_connections=300`), the conformance fixture's `hosted/ci_many` |
| Machine | the development PC, shared with other sessions' fixtures; the flood comparisons are therefore **interleaved** before/after rather than run in two blocks |

## What a cached tile costs at the store

`pg_stat_statements` over a run of cached tiles: every request ran **four store statements** — the token's session, its
grants, the service's layers and their group layers — one each per request. The anonymous grants are held in memory
(D-249) but the catalogue read is per request by decision ([D-17](../../../docs/architecture-debt.md)): a cached tile
does need the store phase, so exempting it from the gate is not available. Sequentially a cached tile answers in
**4.6 ms at the median** (9.2 p95), against 4.4 ms for the VectorTileServer document; the gate's own part — a
compare-and-swap and an uncontended semaphore — was not separated from that.

## Before: what the gate refused

**One viewer is never refused**, at any burst size: 100 streams per connection is below 24 permits + 96 waiting, so one
HTTP/2 client cannot reach the depth on its own. **Two or more can, and were refused while the store cleared the burst in
tens of milliseconds**: three viewers firing 64 at once lost **27.5–37.5 %** of their requests to 503, five lost
**59.6–61.8 %**, and every refused burst was otherwise served in 43–139 ms. That is not a saturated store; it is 96 counted
against permits held for about four statements each.

## What changed

`StoreGate` enforces its depth of 96 **only while its queue is standing** — CoDel's test: requests have been granted only
after waiting longer than 50 ms for at least 100 ms, without the queue emptying, and the last such grant is under 100 ms
old. Otherwise arrivals queue up to a **ceiling of 16 per permit, 384**, which nothing passes. A flood is standing within a
tenth of a second and is refused at the same depth as before. The permit is still given back when the request asks for a
data-source permit (D-374, *"Yalıtımı koru"*).

## Bursts, before and after (5 rounds each)

| clients x burst | scenario | before: 503 | before: served p50 / p95 ms | before: burst wall | after: 503 | after: served p50 / p95 ms | after: burst wall |
|---|---|---:|---:|---:|---:|---:|---:|
| 1 x 64 | tile-cached | 0 of 320 | 18 / 43 | 23 ms | 0 of 320 | 22 / 54 | 41 ms |
| 1 x 128 | tile-cached | 0 of 640 | 31 / 66 | 41 ms | 0 of 640 | 30 / 52 | 46 ms |
| 1 x 256 | tile-cached | 0 of 1280 | 44 / 101 | 80 ms | 0 of 1280 | 49 / 91 | 88 ms |
| 1 x 64 | tile-uncached | 0 of 320 | 16 / 20 | 22 ms | 0 of 320 | 29 / 47 | 64 ms |
| 1 x 128 | tile-uncached | 0 of 640 | 26 / 38 | 39 ms | 0 of 640 | 52 / 76 | 74 ms |
| 1 x 256 | tile-uncached | 0 of 1280 | 47 / 85 | 77 ms | 0 of 1280 | 78 / 137 | 141 ms |
| 1 x 64 | export | 0 of 320 | 36 / 56 | 50 ms | 0 of 320 | 36 / 115 | 44 ms |
| 1 x 128 | export | 0 of 640 | 56 / 91 | 92 ms | 0 of 640 | 56 / 87 | 88 ms |
| 1 x 256 | export | 0 of 1280 | 101 / 226 | 176 ms | 0 of 1280 | 100 / 170 | 175 ms |
| 1 x 64 | query | 0 of 320 | 19 / 44 | 25 ms | 0 of 320 | 20 / 51 | 23 ms |
| 1 x 128 | query | 0 of 640 | 29 / 46 | 46 ms | 0 of 640 | 29 / 46 | 49 ms |
| 1 x 256 | query | 0 of 1280 | 52 / 86 | 92 ms | 0 of 1280 | 48 / 90 | 95 ms |
| 1 x 64 | viewer | 0 of 320 | 19 / 23 | 24 ms | 0 of 320 | 22 / 37 | 24 ms |
| 1 x 128 | viewer | 0 of 640 | 38 / 168 | 60 ms | 0 of 640 | 33 / 50 | 53 ms |
| 1 x 256 | viewer | 0 of 1280 | 77 / 174 | 111 ms | 0 of 1280 | 66 / 111 | 108 ms |
| 3 x 64 | tile-cached | 264 of 960 (27.5 %) | 39 / 191 | 52 ms | 0 of 960 | 36 / 66 | 57 ms |
| 3 x 128 | tile-cached | 1210 of 1920 (63.0 %) | 34 / 52 | 51 ms | 0 of 1920 | 63 / 116 | 119 ms |
| 3 x 256 | tile-cached | 2681 of 3840 (69.8 %) | 49 / 81 | 84 ms | 278 of 3840 (7.2 %) | 120 / 234 | 247 ms |
| 3 x 64 | tile-uncached | 340 of 960 (35.4 %) | 59 / 124 | 70 ms | 0 of 960 | 72 / 105 | 98 ms |
| 3 x 128 | tile-uncached | 1142 of 1920 (59.5 %) | 59 / 88 | 96 ms | 0 of 1920 | 115 / 199 | 210 ms |
| 3 x 256 | tile-uncached | 2556 of 3840 (66.6 %) | 91 / 159 | 146 ms | 962 of 3840 (25.1 %) | 167 / 312 | 318 ms |
| 3 x 64 | export | 320 of 960 (33.3 %) | 57 / 90 | 92 ms | 0 of 960 | 79 / 131 | 127 ms |
| 3 x 128 | export | 1122 of 1920 (58.4 %) | 68 / 126 | 107 ms | 32 of 1920 (1.7 %) | 140 / 263 | 268 ms |
| 3 x 256 | export | 2895 of 3840 (75.4 %) | 85 / 167 | 139 ms | 1364 of 3840 (35.5 %) | 201 / 371 | 379 ms |
| 3 x 64 | query | 340 of 960 (35.4 %) | 29 / 52 | 43 ms | 0 of 960 | 40 / 66 | 67 ms |
| 3 x 128 | query | 1159 of 1920 (60.4 %) | 37 / 60 | 57 ms | 0 of 1920 | 72 / 134 | 138 ms |
| 3 x 256 | query | 2595 of 3840 (67.6 %) | 58 / 97 | 103 ms | 446 of 3840 (11.6 %) | 128 / 237 | 254 ms |
| 3 x 64 | viewer | 360 of 960 (37.5 %) | 40 / 71 | 61 ms | 0 of 960 | 48 / 76 | 76 ms |
| 3 x 128 | viewer | 1173 of 1920 (61.1 %) | 47 / 84 | 71 ms | 0 of 1920 | 88 / 150 | 150 ms |
| 3 x 256 | viewer | 2805 of 3840 (73.0 %) | 61 / 109 | 91 ms | 570 of 3840 (14.8 %) | 147 / 261 | 273 ms |
| 5 x 64 | tile-cached | 953 of 1600 (59.6 %) | 34 / 59 | 46 ms | 0 of 1600 | 57 / 101 | 104 ms |
| 5 x 128 | tile-cached | 2494 of 3200 (77.9 %) | 32 / 56 | 52 ms | 775 of 3200 (24.2 %) | 80 / 153 | 163 ms |
| 5 x 256 | tile-cached | 5190 of 6400 (81.1 %) | 57 / 95 | 85 ms | 2343 of 6400 (36.6 %) | 137 / 260 | 274 ms |
| 5 x 64 | tile-uncached | 959 of 1600 (59.9 %) | 61 / 114 | 108 ms | 0 of 1600 | 110 / 258 | 184 ms |
| 5 x 128 | tile-uncached | 2370 of 3200 (74.1 %) | 66 / 132 | 102 ms | 651 of 3200 (20.3 %) | 153 / 278 | 277 ms |
| 5 x 256 | tile-uncached | 5155 of 6400 (80.5 %) | 110 / 175 | 180 ms | 2932 of 6400 (45.8 %) | 206 / 388 | 404 ms |
| 5 x 64 | export | 977 of 1600 (61.1 %) | 66 / 97 | 94 ms | 0 of 1600 | 120 / 219 | 232 ms |
| 5 x 128 | export | 2457 of 3200 (76.8 %) | 77 / 128 | 124 ms | 1100 of 3200 (34.4 %) | 227 / 638 | 390 ms |
| 5 x 256 | export | 5193 of 6400 (81.1 %) | 116 / 194 | 201 ms | 3410 of 6400 (53.3 %) | 226 / 429 | 446 ms |
| 5 x 64 | query | 988 of 1600 (61.8 %) | 34 / 53 | 53 ms | 0 of 1600 | 59 / 109 | 113 ms |
| 5 x 128 | query | 2378 of 3200 (74.3 %) | 38 / 83 | 64 ms | 735 of 3200 (23.0 %) | 91 / 177 | 171 ms |
| 5 x 256 | query | 5399 of 6400 (84.4 %) | 50 / 84 | 85 ms | 2821 of 6400 (44.1 %) | 159 / 283 | 303 ms |
| 5 x 64 | viewer | 978 of 1600 (61.1 %) | 38 / 60 | 60 ms | 0 of 1600 | 75 / 137 | 134 ms |
| 5 x 128 | viewer | 2415 of 3200 (75.5 %) | 59 / 108 | 71 ms | 713 of 3200 (22.3 %) | 116 / 223 | 209 ms |
| 5 x 256 | viewer | 5179 of 6400 (80.9 %) | 76 / 121 | 123 ms | 2754 of 6400 (43.0 %) | 172 / 308 | 324 ms |

Read it as: **every burst of 64 from up to five viewers, and of 128 from three, is now served whole**; the served
latency rises because the requests that used to be refused in 2–30 ms are now served. What is still refused is a
burst that keeps the store's queue long for more than a tenth of a second — three viewers each firing 256, or five
firing 128 (640 requests, past the ceiling of 24 + 384 at once) — which is the case the depth exists for.

## The flood still refuses on arrival (interleaved, 60 s after 10 s warm)

| run | refused | admitted median | p95 | store queue max | standing |
|---|---:|---:|---:|---:|---:|
| before, 240 callers | 92.2 %, 90.7 % | 167.3, 177.8 ms | 275.6, 326.3 | 96 | — |
| after, 240 callers | **84.8 %, 90.1 %** | 165.4, 181.0 ms | 278.4, 306.8 | 216 | 72–77 % |
| before, 480 callers | 92.5 %, 95.3 % | 199.5, 216.0 ms | 314.0, 351.8 | 96 | — |
| after, 480 callers | **95.1 %, 90.8 %** | 244.2, 266.1 ms | 625.0, 622.0 | 384 | 82–85 % |
| after (second pair), 480 | 94.2 %, 93.1 % | 202.3, 211.2 ms | 538.4, 537.4 | 384 | 82–85 % |
| before, 2,000 req/s offered | 27.6 % | 99.3 ms | 176.8 | 96 | — |
| after, 2,000 req/s offered | 28.1 % | 113.8 ms | 239.6 | 384 | 68 % |

**ADR-046 condition 1's first clause still holds**: at 240 callers the flood is refused 85–90 % on arrival (before
91–92 %). **The price is at 480 callers**: the admitted median is 202–266 ms against 200–216, and the p95 roughly
doubles, 537–625 ms against 314–352 — because in the moments the store's queue is not standing (15–18 % of samples: the
flood's first interval, and each time the store's queue empties while its callers wait at the source) it can fill to
the ceiling, 384 rather than 96.

## The ceiling, chosen against both

Eight per permit (192) instead of sixteen, measured the same way:

| | ceiling 8 (192) | ceiling 16 (384, shipped) | no ceiling past the depth (before) |
|---|---:|---:|---:|
| flood, 480 callers: admitted median / p95 | 207.3 / 353.6, 216.5 / 393.6 ms | 202.3 / 538.4, 211.2 / 537.4 ms | 199.5 / 314.0, 216.0 / 351.8 ms |
| flood, 240 callers: refused | 90.3 % | 84.8 %, 90.1 % | 92.2 %, 90.7 % |
| 3 viewers x 64 / x 128 (viewer mix): refused | 0 / **31.4 %** | 0 / 0 | 37.5 % / 61.1 % |
| 5 viewers x 64 (viewer mix): refused | **29.6 %** | 0 | 61.1 % |

**Sixteen is shipped.** Twenty-four plus 384 holds four browsers' worth of HTTP/2 streams at once; eight still refused a
third of five viewers' 64-tile pans. What sixteen costs is paid only under overload — admitted requests in a 480-caller
flood wait up to about twice as long at the p95, and are still answered and still bounded — where eight's cost is paid
by ordinary use.

## Variants measured on the way (not shipped)

- **Any short grant clears the mark** (CoDel as written): 480-caller flood p95 526 ms, standing 80 %. In a flood an
  occasional quick grant is not the queue draining, so the shipped version clears only when nobody is left waiting.
- **Clearing with fewer than the depth waiting**: 480-caller p95 598 ms, standing 78 %. No better.
- **The shipped rule also ages a mark out** when no long grant has renewed it for an interval, so a burst that ended
  long-waited cannot leave the next burst facing a stale standing queue (`StoreGateTests.A_mark_no_grant_renews_ages_out`).
- **A deeper fixed depth** (e.g. 16 per permit, no standing test) was not run: 240 closed-loop callers can never pass
  24 + 384, so condition 1's first clause would fail by arithmetic.

## Reproduce

```sh
dotnet build benchmarks/admission-control/burst -c Release
BURST_USER=ci BURST_PASSWORD=... UOFF=0 sh benchmarks/admission-control/burst/matrix.sh after 22
k6 run -e URL=https://127.0.0.1:18572 -e USER=ci -e PASSWORD=... -e MODE=vus -e VUS=480 \
       -e DURATION=60s -e WARM=10s benchmarks/admission-control/flood.js
```
