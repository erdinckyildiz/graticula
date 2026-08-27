# ADR-014 — TLS and Certificates

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM-HIGH` |
| **Decided** | 2026-08-13 |
| **Answers** | Q-55 · closes failure-scenario **N8** (severe) · blocker **B3** |

---

> **Scope note, 2026-08-18 — v1 serves PostGIS only, and the other engines are
> deferred rather than cut.** This decision reasons about several database engines.
> Owner decision: *"Şimdilik postgis ile gideceğiz. Sonra diğer db'ler eklenecek. V1'de
> sadece Postgis olarak kalabiliriz."* — [v1-scope](../v1-scope.md) §3a, which is the one
> place that says what the deferral means.
>
> **The multi-engine reasoning here is kept on purpose**, because it is what the second
> engine will be built from and because deleting it would make it be re-derived later
> from nothing. What it is not is a description of what v1 does. Where a sentence below
> reads as *the server supports Oracle today*, it has been corrected; where it reads as
> *this is how several engines would be supported*, it stands and waits.
>
> [D-27](../architecture-debt.md).

## 1. Context

TLS appeared nowhere in this architecture until now — not in
[security.md](../security.md), not in deployment, not in air-gapped planning.
The failure-scenario pass found it as **N8** and rated it severe; Q-55 has been
open since.

**It is a blocker rather than a gap** because it cannot be retrofitted. TLS
touches configuration, deployment, the admin API, the reverse-proxy question,
the connection budget and the supervisor simultaneously. A server built without
it acquires an assumption in every one of those places that has to be unpicked.

There are **four** TLS surfaces, and only the first is obvious:

| | Surface | Direction |
|---|---|---|
| **T1** | The serving endpoint — API, tiles, admin | inbound |
| **T2** | Data source connections — PostGIS, SQL Server, Oracle, MySQL, MariaDB | outbound |
| **T3** | Object storage and the COG proxy's fetches ([ADR-009](ADR-009-raster-engine.md) §2.4) | outbound |
| **T4** | Supervisor ↔ worker, and node ↔ node | internal |

---

## 2. Decision — T1: we terminate TLS ourselves, by default

**The server terminates TLS natively. A reverse proxy is supported and is never
required.**

The reasoning is [Q-69](../open-questions.md)'s, reused. The datastore was made a
mandatory managed appliance on the grounds that the ask is *run our container*,
not *acquire a PostgreSQL DBA*. Requiring a reverse proxy for TLS breaks the same
promise in the same way: it turns *run our container* into *also acquire an nginx
administrator*. Our primary user is a **GIS administrator** (Q-06a), not a web
infrastructure engineer.

It is also what the product we are displacing does — ArcGIS Server terminates
its own TLS and ships with a generated certificate. GeoServer, which leans on the
servlet container, is the counter-example and is not the better experience.

### 2a. HTTPS by default, generated certificate on first start

On first start with no configured certificate, the server **generates a
self-signed certificate** and serves HTTPS. It does not serve plain HTTP.

Plain HTTP is available only behind an explicit configuration flag, and enabling
it logs a warning at every startup — not once, every time. A quiet option is one
that ends up in production.

**The honest cost, recorded rather than glossed:** a self-signed default means
clients show a warning, and warnings that appear routinely train people to click
through them, which is its own harm. The alternative — HTTP by default — is
worse, because it fails silently instead of loudly. The mitigation is that
installing a real certificate must be *easy*, which is §2b, and that the admin
API reports certificate status prominently rather than burying it.

### 2b. Certificate installation and rotation must not require a restart

**This is the load-bearing requirement in this ADR**, and it is not primarily a
convenience.

N8 already noted that a 2 AM restart to install a certificate is the kind of
thing that gets a product removed. The stronger reason is architectural:
[ADR-007](ADR-007-service-runtime.md) §4.3–4.4 binds service contexts lazily and
keeps them warm, and §4.4's affinity routing exists specifically to preserve that
warmth. **Restarting a worker to load a certificate evicts every warm context on
it** — so a certificate rotation would trigger exactly the cold-start storm the
runtime is designed to avoid, on a schedule, for a reason unrelated to any
service.

Therefore:

- Certificates are installed through the **admin API** and take effect on the
  next handshake. Existing connections finish on the old certificate.
- The same applies to the trust store for T2 and T3.
- No configuration file edit, no signal, no restart, no container replacement.

**Built 2026-08-27, and by a file watch rather than the admin API — which is a decision and
not a shortcut.** `CertificateReload` watches the file `Graticula:CertificatePath` names and
installs a replacement on the next handshake. The four things forbidden above are all still
forbidden: replacing the certificate file is not a configuration edit, a signal, a restart or
a container replacement, and it is what every certificate tool already does — `cert-manager`
writes a secret into a mounted path, `certbot --deploy-hook` copies a file, and an operator
with a new PFX copies a file. **An upload endpoint would need all of that *plus* a new
authorization story, a new disclosure surface and validation of an uploaded blob before it
can replace a working certificate.** That is a larger decision, it is still open, and
[ADR-017](ADR-017-admin-api.md) §3.4 step 3 still records the route as absent rather than
quietly counting this as it.

**A bad replacement changes nothing**, which is the property that makes an automatic reload
safe: a half-written file, a wrong password and a certificate with no private key all leave
the running certificate in place, after four attempts two seconds apart, and say so at
`Error`. A server that stopped answering because somebody was halfway through a copy would be
a worse outage than the expiry the rotation was for. **Only when the operator supplied the
path** — a generated development certificate is rotated by deleting it and restarting, and
watching it would mean this server reacting to its own writes.

### 2c. Certificate expiry is a supervisor duty

A certificate expiry is a **total data-plane outage with a known date**, which
makes it the most predictable outage in existence and inexcusable to be surprised
by.

The [runtime supervisor](../runtime-supervisor.md) monitors expiry for every
certificate it holds — serving, client, and trust anchors — and surfaces it with
lead time: a warning at 30 days, escalating at 7, critical at 1. It appears in
the admin API's health surface and in whatever §46 eventually exports.

**The ladder is built 2026-08-27; the supervisor is not.** `GET /admin/health` carries
`servingCertificate` with a `state` of `valid`, `warning`, `escalating`, `critical` or
`expired` on exactly those thresholds, plus the dates, the signed days remaining and a
sentence. The ladder does not need a supervisor to be true, and the operator reading that
page is the person this section is written for. **The boundaries are tested rung by rung and
that is not ceremony**: written first with `<`, which made a certificate with exactly seven
days left a *warning* and would have moved the page six days later than this paragraph says.
**Serving only** — there are no client certificates and no trust anchors in this server to
monitor, measured 2026-08-27, so *every certificate it holds* is one.

**This also supplies one of the three 2 AM scenarios that F5 requires** and
[phase-0-exit-plan](../phase-0-exit-plan.md) still lists as unmet — *the
certificate expired* is a complete, walkable scenario with a clear signal, a
clear diagnosis and a clear remedy. Written up when the other two are.

### 2d. Behind a proxy: trusted proxies are configured, never assumed

When a reverse proxy terminates TLS, the server needs `X-Forwarded-Proto`,
`X-Forwarded-For` and `X-Forwarded-Host` to generate correct absolute URLs — and
OGC API documents are full of absolute URLs, so getting this wrong produces a
catalogue that points at the wrong scheme.

**Blindly trusting those headers is a vulnerability, not a convenience.** A
client that can reach the server directly can then claim any scheme or source
address, defeating any policy that depends on either.

So: forwarded headers are honoured **only from explicitly configured trusted
proxy addresses**, and the default trusted set is empty. Running behind a proxy
is a configuration step, and it is a short one.

### 2e. Protocol versions and HTTP/2

TLS 1.2 minimum, TLS 1.3 preferred, renegotiation disabled. **Cipher suites
follow platform defaults rather than a hand-written list** — hand-rolled cipher
lists are correct on the day they are written and wrong two years later, and the
platform is maintained by people who track this full time. An override exists for
sites with a compliance regime that dictates otherwise.

**HTTP/2 is not optional here.** Q-78 put gRPC in scope, and gRPC requires
HTTP/2. Native termination gives us h2 directly. A reverse proxy deployment must
carry HTTP/2 end to end or gRPC clients must fall back to gRPC-Web — a real
constraint on the proxy story, and one the deployment documentation has to state
rather than let people discover.

**HSTS is off by default.** On an internal server with a self-signed
certificate, HSTS pins a browser to a host it cannot validate. Opt-in, for sites
with real certificates and a public name.

---

## 3. Decision — T2: data source connections

**TLS is required for remote data sources and optional for local ones**, since a
unix socket or loopback connection to a co-located datastore gains little from
encryption and pays a handshake for it.

- Remote connections default to the driver's *verify* mode, not merely *require*
  — `require` without verification accepts any certificate and is
  encryption-without-authentication, which is a weaker guarantee than it appears.
- The trust anchors for data sources are ours to manage, installed through the
  admin API alongside serving certificates (§2b).

**Diagnosis matters more than the setting.** N8 observed that a data-source
certificate expiry *looks like a generic connection failure* and produces a
confusing error. A layer that stops working because the database's certificate
expired must say **that**, by name, in the admin API — not *connection failed*.
This is the never-degrade-silently principle applied to an operational failure
rather than a capability difference.

**Cost note:** ADR-007 §4.8 shrinks idle pools to zero. Every pool refill is then
a fresh TLS handshake, which is far more expensive than a plain connect. This is
a real interaction between two decisions and belongs in Q-04's connection budget
measurement rather than being assumed away.

---

## 4. Decision — T3: outbound fetches, and the SSRF problem underneath

The COG proxy (ADR-009 §2.4) makes outbound HTTPS requests to object storage.
Certificate validation is standard; the harder problem is the one N8 pointed at
and [security.md](../security.md) §6 already lists as unwritten.

**Outbound fetch targets are allow-listed, not free-form.** A registered raster
source supplies a URL, and an administrator can register a URL pointing at
`169.254.169.254`, at an internal admin endpoint, or at localhost. That is
**server-side request forgery with the server's own network position**, and TLS
does nothing about it.

- Outbound targets are restricted to configured hosts or prefixes.
- Link-local, loopback and private ranges are refused unless explicitly allowed,
  because a legitimate on-premises MinIO on a private address is a real case.
- Redirects are not followed across hosts outside the allow-list.

This is recorded here because it arrived with T3, and moved into security.md as
its proper home.

---

## 5. Decision — T4: internal traffic

Supervisor ↔ worker communication is **local to a node** by construction
([runtime-supervisor.md](../runtime-supervisor.md)), so it uses a unix socket or
named pipe with filesystem permissions rather than TLS. Encrypting a loopback
channel to defend against an attacker who already has local code execution buys
little and costs handshake on every worker restart.

Node ↔ node is **deferred with clustering** (ADR-012), and this ADR records the
requirement so it is not rediscovered: cross-node traffic carries the platform's
trust and must be mutually authenticated when it exists.

---

## 5a. Decision — T5: what a reverse proxy is allowed to say

**Added 2026-08-24, repaying [D-12](../architecture-debt.md).** §2 lets a deployment
put a reverse proxy in front of this server. Nothing said what the server may believe
from it, and the answer in the code was *nothing at all*: every request was attributed
to the socket address, so behind a proxy the per-address rate limit became one shared
bucket — fifty failures anywhere disabling sign-in for everybody.

**The decision: a forwarded header is read from a peer this deployment has named, and
from nobody else.** `Graticula:TrustedProxies` takes addresses or CIDR ranges. It is
**empty by default**, and with it empty the server behaves exactly as it did.

Three properties, and the order matters:

1. **A caller who is not a listed proxy is themselves**, whatever they wrote in
   `X-Forwarded-For`. This is what makes the header unforgeable. The alternative —
   reading it from anybody — lets every caller choose their own rate-limit bucket, which
   is a limit of zero. **Too coarse is recoverable; forgeable is not**, and that sentence
   is why D-12 stood open rather than being repaired the obvious way.
2. **The chain is read right to left**, stopping at the first address that is not a
   listed proxy. The header is appended to, so the rightmost entry is what the nearest
   proxy saw and the leftmost is whatever the client claimed. Reading from the right
   means entries a client invented sit behind the real one and are never reached.
3. **A mistyped entry refuses to start.** Ignoring it would leave the deployment behaving
   exactly as it did before the setting existed, with nothing to say so — and the symptom
   would be somebody's sign-in rate-limited by a stranger.

**One notion of *the caller*, resolved once**, before authentication, and read by the
rate limiter and by every path that records a source address. Two notions is how a log
and a limiter come to disagree about who was there.

**`X-Forwarded-Proto` is not read at all**, and that is deliberate rather than pending:
§2a decides HTTPS is terminated here by default, so the scheme this server serves under
is its own fact and not the proxy's to assert. If a deployment terminates TLS at the
proxy, what it needs is `RequireHttps=false` and a note in its own runbook — not a header
that changes what the server believes about its own listener.

---

## 6. mTLS

**Supported, off by default.** Q-83 put client-certificate authentication in
scope, and it is a genuine requirement in defence and government deployments,
which are plausible customers for this product.

It is an **authentication** mechanism and therefore belongs to §41 and B4 rather
than here — this ADR only establishes that the transport can carry and validate a
client certificate, and that the identity it yields is handed to the
authentication layer rather than interpreted here.

---

## 7. Air-gapped (Q-15)

Three consequences, all of which change the checklist:

1. **No ACME.** Let's Encrypt and every ACME provider require outbound internet.
   ACME may be offered as an optional convenience for internet-facing
   deployments; it is never a dependency and never the default path.
2. **Revocation checking must soft-fail.** OCSP and CRL endpoints are
   unreachable offline. A hard-fail policy turns an air-gapped install into a
   server that validates nothing successfully. Soft-fail is the correct default
   and it is a real, stated weakening of the guarantee.
3. **The install must accept an internal CA.** Air-gapped enterprises run their
   own certificate authority, and the trust store must be populated from the
   admin API without network access.

---

## 8. Consequences

- **[security.md](../security.md)** gains a transport section and the SSRF
  allow-list from §4.
- **[ADR-007](ADR-007-service-runtime.md)** gains a constraint: certificate
  rotation must not restart a worker (§2b), and pool refill now pays a TLS
  handshake (§3).
- **[runtime-supervisor.md](../runtime-supervisor.md)** gains expiry monitoring
  as a duty (§2c).
- **Q-71 packaging** gains certificate material as state that must survive a
  container replacement.
- **Q-15's air-gapped checklist** gains three items (§7).
- **Deployment documentation** must state the HTTP/2 constraint on proxies
  (§2e), or gRPC will fail confusingly behind a working proxy.
- **One of F5's three 2 AM scenarios** is now writable.

## 9. Conditions

1. **Rotation is verified without a restart**, by test, watching that warm
   service contexts survive it. If they do not, §2b has failed and ADR-007 §4.4
   is paying for it.
   ***(Discharged 2026-08-27 — `CertificateRotationTests`.)*** **The listener holds a
   selector now, not a certificate**, and that one word is the whole condition: Kestrel reads
   `ServerCertificate` once when the listener is built, so a rotation with it set means a new
   listener, which means a restart, which evicts every warm context ADR-007 §4.4 exists to
   keep. `ServerCertificateSelector` is consulted on **every** handshake.
   **Tested against a real Kestrel, because the claim is about Kestrel.** A test of this
   repository's own indirection would pass either way. The test starts a listener, completes
   a handshake, rotates, and completes another on a **fresh** connection —
   `PooledConnectionLifetime` is zero, because reusing the first connection would show the
   old certificate and prove nothing, and that is the way this test would most easily have
   lied. The two thumbprints differ and the listener was never restarted.
   **The second half is the one the condition is actually about**, and it is measured by what
   a cold context would cost: a warm service context is established before the rotation and
   the describe count is asserted **unchanged** afterwards, with the same `LayerDescription`
   instance coming back. Written first as `Assert.Same` on the feature source, which failed —
   `GetAsync` hands back a fresh handle every call and caches the description behind it. The
   handle is cheap; the round trip is the warmth. A count of entries alone would have passed
   over an eviction and a rebuild, which is the exact failure being watched for.
   **Falsified** by putting `https.ServerCertificate = first` back in the test's own listener:
   the rotation case fails, the other two pass, and the difference between those two lines is
   what §2b forbids.
   **What is not discharged is the admin route** — [ADR-017](ADR-017-admin-api.md) §3.4 step 3.
   The mechanism it would use exists; the decision about an upload surface does not. See §2b.
2. **Handshake cost enters Q-04's connection budget measurement** rather than
   being assumed negligible against shrink-to-zero pools.
   ***(Discharged 2026-08-27 — measured.)***
   [benchmarks/tls-handshake](../../benchmarks/tls-handshake/RESULTS.md). Six runs against
   PostgreSQL 16 with `ssl = on`, TLS 1.3: a plain connect plus the SSLRequest exchange takes
   **1.8–2.3 ms** and the same connect with the handshake takes **4.7–5.8 ms**, so the
   handshake is **about 2.8 ms, roughly 2.5×** a plain connect. §3's *far more expensive than
   a plain connect* was right and is now a number rather than an adjective.
   **The minimum is the figure, and the reason is stated rather than assumed away.** Across
   six runs the minima sit inside 0.5 ms of each other and the medians move by a factor of
   nearly three — the path is a Docker port proxy on Windows and its median measures the
   scheduler. Both are reported.
   **What it means for the budget**, which is why the condition exists:
   [ADR-046](ADR-046-admission-control-bounds-the-queue-not-the-wait.md) bounds a worker at
   64 concurrent database operations and one source at 24, and a pool that has shrunk to zero
   pays the connect on every one — **112 ms to refill a source's budget, 299 ms to refill the
   worker's, at best**, of which 67 ms and 178 ms is handshake. That is the cost of an idle
   period, paid by whoever arrives after it, per source.
   **It is a floor rather than an estimate**: this is loopback, and a remote database adds
   round trips to both arms and *more* of them to the handshake — one extra for TLS 1.3, two
   for 1.2 — so the handshake's share grows with distance. Authentication, the startup packet
   and Npgsql's own bookkeeping are deliberately outside the number, because what §3's note is
   about is the cost a refill pays **extra** for being encrypted.
   **This does not say whether pools should shrink to zero.** It says what that decision
   costs, which is what was asked.
3. **The forwarded-header trusted-proxy default is empty**, and a test proves a
   spoofed `X-Forwarded-Proto` from an untrusted source is ignored.
   ***(Discharged 2026-08-24 — §5a.)*** `Graticula:TrustedProxies` is empty by default and
   `CallerAddressTests` proves the ignoring, on `X-Forwarded-For` rather than
   `X-Forwarded-Proto`: the proto header is read nowhere, which §5a now records as a
   decision instead of leaving it as an absence. Twelve cases, including a client that
   writes its own hops in front of the proxy's, and a mistyped range that refuses to
   start.
4. **Data-source certificate expiry produces a named diagnosis**, not a generic
   connection error — tested by expiring one.
   ***(Discharged 2026-08-27 — tested by expiring one, which is what the condition asked
   for and is why it found two things instead of one.)*** The probe's message was
   *"Could not reach the server: Exception while performing SSL handshake"*: Npgsql's outer
   sentence, with an `AuthenticationException` under it whose own wording is the platform's
   callback message or an OS error number. **None of the three carries a date**, and the
   operator's next move is a replacement on the database rather than anything on this
   network.
   **A refused handshake now gets a second look** — `SourceCertificate.WhyRefusedAsync`
   opens one more connection, answers the postgres SSLRequest, takes the certificate in a
   validation callback that **returns false**, and reads its dates. It changes no outcome and
   cannot make a rejected certificate work; it turns the message into *"The database's TLS
   certificate expired on 2026-08-26 15:04 UTC, 1 days ago"*, with the subject. Not expired
   is answered too — not yet valid is a clock, and the remaining case names the issuer,
   whether it is self-signed, and that `VerifyFull` wants the name as well as the issuer,
   because a self-signed certificate is usually also issued to the wrong name and an operator
   told only one of the two fixes it and fails again. Null when the second look fails, and
   then the generic sentence stands: a wrong diagnosis is worse than a vague one.
   **Tested against Npgsql's real code path without a database.** A PostgreSQL container
   built with TLS and a backdated certificate is a heavy dependency for one exception shape,
   and postgres announces TLS before it authenticates anything — eight bytes in, one `S`
   back, then an ordinary handshake. `ExpiredSourceCertificateTests` is those two steps and a
   certificate that expired yesterday. It asserts the **date**, not the word: asserting
   *certificate* alone would have passed on Npgsql's own wording.
   **What the test found on the way is [D-190](../architecture-debt.md), and it is the more
   serious half.** It was written for three modes on the belief that `Require` validates.
   Npgsql 9 follows libpq, where `Require` means *encrypt* and nothing more: the expired
   certificate was **accepted**, and the run failed later on the startup packet. So a source
   registered with `Require` has confidentiality and no authentication, and the probe reports
   it exactly like a `VerifyFull` one. That is a caution the probe should give and does not,
   and it is open.

## 10. Assumptions

| ID | Assumption | Status |
|---|---|---|
| A-044 | Certificate rotation can be made to take effect on the next handshake without recycling the listener or the worker | **`VALIDATED` 2026-08-27** — `CertificateRotationTests` rotates a certificate on a running Kestrel and reads the new one off the next handshake, on a fresh connection, with the listener never restarted; a warm service context is established before and its describe count is unchanged after. Kestrel's `ServerCertificateSelector` is the mechanism, and the assumption held |
| A-045 | Soft-fail revocation checking is an acceptable security posture for the air-gapped profile | `UNVALIDATED` — it is a real weakening, and the alternative is a server that cannot validate anything offline. Needs stating in deployment documentation rather than buried |

## 11. Dissent

**Against native termination.** Enterprises with an established web tier already
terminate TLS at an F5, an Application Gateway or an IIS front end, and a server
that also wants to do it is redundant there. The counter-argument is that §2 does
not *require* native termination — it defaults to it — and the deployment that
already has a proxy configures one (§2d). The cost of the default falling the
other way is much higher: a first-run experience that serves plaintext until
someone stands up a proxy.

**Against self-signed by default.** It trains click-through, as §2a admits. No
better option exists that is also secure by default and works offline on first
start.
