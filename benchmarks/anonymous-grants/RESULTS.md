# An anonymous request stops asking the store who it is

Measured 2026-09-11 for [D-249](../../docs/architecture-debt.md), after the owner's decision
that the anonymous caller's grants are held in memory and the server hears at once when they
change — [ADR-015](../../docs/adr/ADR-015-authentication.md) §3a, migration 44.

## 1. What was measured

Two Release builds against **one** platform store, running side by side, levels
interleaved so that anything drifting on the machine drifts through every column:

| server | build |
|---|---|
| `before` | `origin/main` at `f77bd0d` — every anonymous request calls `GrantsOfAsync` |
| `after` | the working tree — `AnonymousGrants` held while `PostgresGrantsListener` is subscribed |

The build without the cache starts against the store at schema 44 because migration 44 is an
expand: *"server at schema 43 is compatible with the platform store (schema 44, minimum reader
34)"*. So the store, its rows and its connection are the same for both.

Each server answers two paths: `/rest/info?f=json`, which resolves a principal, and
`/healthz/live`, the one path whose middleware skips `ResolveAsync`. The generator is
[pipeline-ceiling](../pipeline-ceiling/RESULTS.md)'s `bare.js`, unchanged: 20 s measured after
6 s of warm-up, connections reused — `tls` and `connect` medians are 0.000 ms in every run.

## 2. The first matrix measured the logger, and it is recorded because it looked like an answer

Every cell of the first pass came back at **about 155 requests a second** — both builds, both
paths, one caller or 128 — with latency growing linearly in callers (863 ms median at 128).
`/healthz/live` was as slow as `/rest/info`, which is the sign that the rig rather than either
build was the ceiling. **It was the console logger.** Both servers had been started with
PowerShell's `Start-Process -RedirectStandardOutput` at the default log level, which writes
three to five lines per request, and that sink serialised the whole process. One restart with
`Logging__LogLevel__Default=Warning` and nothing else changed: **35,662 req/s** on the same
path at 16 callers, against 143.

Read naïvely, that first pass said *the cache changes nothing* — 147.9 before and 154.8 after
at one caller. The control column is what refused it: a control as slow as the thing measured
is not a control. Both servers were restarted at `Warning` and the matrix run again.

## 3. What it measured

Requests per second:

| callers | `before` `/rest/info` | `after` `/rest/info` | `before` `/healthz/live` | `after` `/healthz/live` |
|---:|---:|---:|---:|---:|
| 1 | 995.1 | **4,022.7** | 4,321.4 | 4,266.3 |
| 4 | 3,295.4 | **13,561.8** | 14,538.5 | 14,800.8 |
| 16 | 6,165.9 | **37,638.8** | 40,468.1 | 41,258.7 |
| 32 | 6,379.9 | **41,985.9** | 45,315.2 | 46,449.9 |
| 128 | 6,393.9 | **46,775.3** | 51,122.8 | 50,938.6 |

Median latency, `/rest/info`:

| callers | `before` | `after` |
|---:|---:|---:|
| 1 | 0.96 ms | 0.00 ms |
| 16 | 2.20 ms | 0.51 ms |
| 32 | 4.34 ms | 0.65 ms |
| 128 | 19.32 ms | 2.12 ms |

No request failed in either pass.

## 4. What it means

**The anonymous path is 4.0× faster alone and 7.3× faster at 128 callers, and it no longer
has a ceiling of its own.** `before` reproduces the curve pipeline-ceiling recorded a day
earlier — it flattens between 16 and 32 callers, 6,166 there against 6,594 then — and `after`
keeps climbing to 128 with the same shape as `/healthz/live`. What is left between `after`'s
`/rest/info` and its `/healthz/live` is **8% at 128 callers** — the rest of `ResolveAsync`
(`EnsureFreshAsync`'s clock comparison, resolving the privileges) plus the endpoint's own
work, which a liveness probe does not do.

**The two `/healthz/live` columns agree with each other**, as they must: that path never reached
the store in either build, so it is the check that the builds and the machine were otherwise
equal.

## 5. What it does not say

- **Nothing about a signed-in caller.** A session is still read on every request, deliberately:
  revocation stays immediate ([ADR-015](../../docs/adr/ADR-015-authentication.md) condition 1,
  measured at 8.5% of throughput at 24 callers on 2026-08-27).
- **Nothing about a request that reads data.** A feature query pays its data source, and
  [ADR-046](../../docs/adr/ADR-046-admission-control-bounds-the-queue-not-the-wait.md) bounds that
  separately.

## 6. And the rest of the ceiling was the logger

D-249 left a second question open: *without `ResolveAsync` this pipeline is still 3.8× slower
at 32 callers than a server with no middleware*. The second pass above ran every server at
`Warning`, and `/healthz/live` came back at 51,123 req/s at 128 callers — against 18,595 in
pipeline-ceiling, which had run against the development server at the **default** level. So
the same day, same TLS, same generator, three servers at `/healthz/live`:

| callers | Graticula, `Warning` | Graticula, default level | bare control |
|---:|---:|---:|---:|
| 1 | 3,818.9 | 3,249.6 | 4,749.1 |
| 4 | 14,409.9 | 13,315.2 | 16,298.9 |
| 16 | 41,112.2 | 23,335.0 | 47,153.0 |
| 32 | 45,388.1 | **18,400.8** | 53,713.3 |
| 128 | 49,591.7 | **17,618.8** | 55,574.2 |

The default-level server was started the way `dev-server.sh` starts one — bash, stdout
redirected to a file — so its column is the one pipeline-ceiling measured, and it matches it.
**At `Warning` the gap to a server with no middleware at all is 1.12× at 128 callers; at the
default level it is 3.2×.** In three minutes the default-level server wrote a gigabyte of log.

Per request at the default level it writes five lines: Graticula's own request line, which
replaces the framework's because the framework's logs the raw query string (ADR-015 §4.1), and
**four from ASP.NET Core** — `Routing.EndpointMiddleware` twice and `Http.Result.OkObjectResult`
twice — none of which says anything the first line does not.
