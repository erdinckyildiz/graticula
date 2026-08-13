# ADR-014 — TLS and Certificates

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM-HIGH` |
| **Decided** | 2026-08-13 |
| **Answers** | Q-55 · closes failure-scenario **N8** (severe) · blocker **B3** |

---

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

### 2c. Certificate expiry is a supervisor duty

A certificate expiry is a **total data-plane outage with a known date**, which
makes it the most predictable outage in existence and inexcusable to be surprised
by.

The [runtime supervisor](../runtime-supervisor.md) monitors expiry for every
certificate it holds — serving, client, and trust anchors — and surfaces it with
lead time: a warning at 30 days, escalating at 7, critical at 1. It appears in
the admin API's health surface and in whatever §46 eventually exports.

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
2. **Handshake cost enters Q-04's connection budget measurement** rather than
   being assumed negligible against shrink-to-zero pools.
3. **The forwarded-header trusted-proxy default is empty**, and a test proves a
   spoofed `X-Forwarded-Proto` from an untrusted source is ignored.
4. **Data-source certificate expiry produces a named diagnosis**, not a generic
   connection error — tested by expiring one.

## 10. Assumptions

| ID | Assumption | Status |
|---|---|---|
| A-044 | Certificate rotation can be made to take effect on the next handshake without recycling the listener or the worker | `UNVALIDATED` — load-bearing for §2b, and ADR-007 §4.4's warm state depends on it |
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
