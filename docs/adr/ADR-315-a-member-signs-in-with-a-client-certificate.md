# ADR-315 — A member signs in with a client certificate (PKI)

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` — the mechanism is measured end to end against real server processes with certificates made in the test, through both ways a certificate can arrive. It is not `HIGH` because no ArcGIS client has yet signed in with a certificate here (conditions 3 and 4), and because the revocation policy (§5.6) is a reading of ADR-014 §7 rather than something an organisation asked for |
| **Decided** | 2026-11-23, by owner decision through a question dialog, on Enterprise 12.2 band 2 group 2 ([enterprise-12-2-gap.md](../research/enterprise-12-2-gap.md)): *"İkisi de, önce PKI"* — build both IWA and PKI, PKI first. The certificate is validated by Kestrel or forwarded by the proxy, then mapped to an account. Every reading below that public Esri documentation and the earlier ADRs do not settle is marked `INFERRED` and listed in §5.10 |
| **Supersedes** | — (amends [ADR-015](ADR-015-authentication.md) §2 and §5, where mTLS was a *service* principal's credential, and builds the transport half [ADR-014](ADR-014-tls-and-certificates.md) §6 promised; repays the mTLS half of [D-10](../architecture-debt.md)) |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

Defence and government sites sign people in with a smart card or a soft certificate rather than a password, and ArcGIS
Enterprise serves them: the gap table lists *PKI and client certificates* as missing, with *"ADR-015 names mTLS. It is
not built"*. ADR-014 §6 said the transport would *carry and validate* a client certificate and hand the identity on;
ADR-015 §5 said *this ADR interprets the identity in it*, and called that boundary deliberate. Neither half was written.

How ArcGIS does it, from Esri's public documentation:

- **The web tier authenticates, not the portal.** *"The user can choose to provide a certificate, which is validated
  against the trust store of the web server"*, and the Web Adaptor *"passes the username to the component, which
  establishes a session. This process does not require the client certificate to be sent to or trusted by ArcGIS
  Enterprise"* — [ArcGIS Architecture Center, PKI and client certificates](https://architecture.arcgis.com/en/framework/architecture-pillars/security/pki-and-client-certificates.html).
- **The account is found by a field of the certificate.** *"The userSearchAttribute is the value of the Subject
  parameter of the PKI certificate"*; an organisation that uses another attribute, such as e-mail, changes it to match —
  [Use LDAP and PKI to secure access to your portal](https://enterprise.arcgis.com/en/portal/latest/administer/windows/use-ldap-and-pki-to-secure-access-to-your-portal.htm)
  (read 2026-11-23 through the copy of the 11.5 page Fairfax County publishes, because enterprise.arcgis.com reset the
  connection; the same page on [doc.esri.com for Kubernetes](https://doc.esri.com/en/arcgis-enterprise-k8s/latest/deploy/use-ldap-and-pki-to-secure-access-to-your-portal.html)
  adds that a Java Web Adaptor *may return the user's full distinguished name* instead of a name).
- **Accounts are enterprise accounts**, added beforehand or made at a first sign-in (*"automatically"*), and by default
  see what is shared with the organisation — the same page.
- **TLS 1.3 must be off** in the Java application server for client certificate authentication to work — the same page.
  That is a renegotiation design: the certificate is asked for after the handshake, on one path.

Not documented publicly, and so not copied: which certificate field ArcGIS reads by default beyond *Subject*, what it
does about revocation, and what ArcGIS Pro and the API for Python send to a PKI portal.

## 2. Alternatives considered

### Alternative A — the certificate is asked for at the handshake, and a sign-in route reads it *(chosen)*

Kestrel asks every connection for a certificate (`AllowCertificate`), or demands one (`RequireCertificate`); a person
signs in by opening one route that reads the certificate the connection carries, checks it against the authorities the
deployment configured, maps it to a member, and ends in this server's own session (ADR-015 §3). Behind a proxy that
ended TLS, the proxy forwards the certificate in a header, read from a proxy the deployment trusts and nobody else.

**Argument for.** It works with TLS 1.3 and HTTP/2, which a renegotiation design does not. The identity becomes the
same session every other sign-in makes, so revocation, lifetimes, the audit record and the OAuth page work unchanged.

**Argument against.** `AllowCertificate` asks *every* visitor's browser for a certificate: one that holds any shows a
picker on the first page, even to somebody who meant to type a password. ArcGIS's web tier does the same when PKI is on.

### Alternative B — ask for the certificate on one path only (renegotiation, `DelayCertificate`)

**Argument for.** Only the sign-in route shows a picker.

**Argument against.** It needs TLS 1.2 and HTTP/1.1: browsers do not do TLS 1.3 post-handshake authentication, and
HTTP/2 forbids renegotiation. Esri's own instruction to turn TLS 1.3 off is this design's cost; ADR-014 §2e chose the
platform's defaults over a hand-written protocol list, and turning TLS 1.3 off server-wide to save a picker is the
wrong trade.

### Alternative C — every request authenticated by its certificate, no session

**Argument for.** The web-tier model: nothing to sign out of, nothing stolen but the card.

**Argument against.** It is a second credential on the request path beside the opaque token, read by every middleware
that asks *who is this*. ADR-015 §3 made the session the one thing a request is authenticated by, and revocation of that
session the reason. A certificate here is a way to *get* a session, as a password is.

**Taken in part, after measuring (§5.5a).** The ArcGIS API for Python, given `cert_file` and `key_file`, sends the
certificate with every request and never asks for a token: against a build without per-request certificates it asked
`/sharing/rest/info` and `portals/self` and gave up as anonymous (arcgis 2.4.3, 2026-11-23). So a request that carries
no token is the certificate's member — on the ArcGIS faces only, and not for a request from another site.

### Alternative D — mTLS identifies a service principal only, as ADR-015 §2 said

**Argument for.** It is what ADR-015 wrote down.

**Argument against.** It is not what ArcGIS Enterprise means by PKI, and not what the owner asked for: a person with a
smart card. Service accounts have API keys and `client_credentials` (ADR-200). This ADR amends ADR-015 §2.

## 3. Counterarguments to the preferred option

- **A forwarded certificate is only a public certificate.** Anybody can copy one; what proves possession of the key is
  the TLS handshake the *proxy* made. So a header read from anybody but the proxy is a password everybody knows, and a
  proxy that passes a client's own header through unchanged (Caddy's `reverse_proxy` passes request headers through by
  default) makes the deployment's trust in it worthless. §5.3 reads the header only when the deployment names it, only
  from a trusted proxy, and docs/deployment.md tells the operator to make the proxy overwrite it.
- **The chain is checked twice behind a proxy** — by the proxy and here. That is deliberate: the authorities are one
  list, held here, and a proxy configured with a broader trust store than the server's would otherwise widen who gets in.
- **The account name comes from a field the CA writes.** A CA that issues two people the same CN gives them one
  account. That is the deployment's CA policy and the reason the field is chosen per provider (§5.4).

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| ArcGIS validates the certificate at the web tier and the portal sees a username | Esri's architecture page | §1 |
| The username is a field of the certificate, the Subject by default, changeable to e-mail | Esri's portal PKI page | §1 |
| Both arrival ways sign a member in; a header from an untrusted peer is refused; another CA's certificate is refused; each mapping field names the account; a revoked certificate is refused | `ACertificateSignsAMemberInTests` (tests/Graticula.Host.Tests), against real server processes | §5, condition 1 |
| ArcGIS clients' password `generateToken` keeps working with certificates requested | the same tests | condition 1 |
| A real Caddy with `client_auth` forwards a certificate the server believes, and replaces a client's own header | Caddy 2.10.2 against the fixture | condition 2 |
| The ArcGIS API for Python sends the certificate on every request and never asks for a token | arcgis 2.4.3 against the fixture | condition 4, §5.5a |

## 5. Decision

5.1 **A PKI provider is a sign-in provider of kind `pki`** (migration 147, which also admits ADR-316's `iwa`): its name
is what the sign-in button says; its *username claim* is the certificate field that names the account — `cn` (the
subject's common name, the default, as Esri's *Subject*), `email` (the first RFC 822 name in the subject alternative
names, else the subject's `E=`), or `upn` (the first Microsoft user principal name in the subject alternative names, as
a CAC or PIV card carries it). Whether a first sign-in makes an account, its role and its user type are the provider's,
as for OIDC, SAML and LDAP. Its issuer is stored as `client-certificate` and its client id empty: the authorities are
not the provider's (§5.2). **At most one PKI provider is on in an organisation** — one certificate cannot sensibly be
two people — `INFERRED`.

5.2 **The transport is configuration, as ADR-014 §6 and ADR-015 §5 divided it**, under `Graticula:ClientCertificates`:

| Key | Values | Default |
|---|---|---|
| `Mode` | `Off`, `Allow` (Kestrel asks every connection for a certificate; one without still connects), `Require` (a connection without a certificate the authorities vouch for is refused at the handshake) | `Off` |
| `Authorities` | a PEM file of the certificate authorities, roots and intermediates, a client certificate must chain to | none |
| `Header` | the header a trusted proxy forwards the certificate in; empty means no header is ever read | empty |
| `Revocation` | `SoftFail`, `HardFail`, `Off` (§5.6) | `SoftFail` |

**A setting that cannot work refuses to start**, as `TrustedProxies` does — *since ADR-318 (the same day's merge) a refusal stays up and answers 503 with state `configuration` and the key named, rather than exiting 78*: `Mode` other than `Off`, or a `Header`,
without readable `Authorities`; a `Header` with `TrustedProxies` empty (a header nobody may send is a mistake, not a
setting); a `Mode` with `RequireHttps=false` (there is no handshake to ask in).

5.3 **Two ways in, one check.** The certificate is the connection's own when `Mode` is not `Off`; otherwise, when
`Header` is set and the **socket's** address is in `Graticula:TrustedProxies`, the header's — base64 DER (Caddy's
`{http.request.tls.client.certificate_der_base64}`, IIS ARR's), PEM, or URL-encoded PEM (nginx's
`$ssl_client_escaped_cert`). **From any other peer the header is ignored and the log says so**; the sign-in then finds
no certificate and says that. A value that is a placeholder left as written (`{…}`) is no certificate: Caddy 2.10.2's
`header_up` sends `{http.request.tls.client.certificate_der_base64}` literally when the client presented none (measured
2026-11-23 under `verify_if_given`). The connection's certificate wins over a header when both are present.

5.4 **The check**: the chain is built to `Authorities` alone (`CustomRootTrust`; the machine's store is not consulted),
the certificate is within its dates, and if it lists extended key usages one of them is client authentication. Then the
provider's field is read; a certificate without it is refused by name. The account is found by that value, compared
without case; **the account's name is its part before an `@`, with spaces as dots** (`Ada Lovelace` →
`Ada.Lovelace`) — `INFERRED`, because a name with a space breaks ArcGIS clients' `community/users/{name}` addresses.

5.5 **Sign-in is `GET /rest/auth/pki/{id}/start`**, and it ends in ADR-088's door (`OidcEndpoints.FinishAsync`): the
same account rules, never joined to a local account, and this server's own session cookie, or an app's code when it was
started from the OAuth sign-in page (`?oauth=`). So **the provider is a button wherever providers are buttons**: the
sign-in panel of Studio and the console (`/rest/auth/providers`), the portal's `/rest/login` and the OAuth page ArcGIS
Pro and the API for Python open — no new markup, because those pages draw the providers they are given. A refusal is a
page that says why: no certificate, an authority not trusted, revoked, expired, or no `cn`/`email`/`upn` in it.

5.5a **A request that carries a certificate and no token is the certificate's member** — measured to be what the ArcGIS
API for Python needs (§2, Alternative C). Its rules are the session cookie's, because a certificate is just as ambient —
a browser presents it on a connection another site's page opens: not for a request whose `Origin` is another origin or
whose `Sec-Fetch-Site` is `cross-site`, not for JSONP; and, as a `generateToken` token, never for the native
administration API (ADR-015 §4). A token sent wins over the certificate. The answer is held for a minute per host and
certificate, so a disabled member or a provider turned off reaches a certificate holder within that minute — `INFERRED`.

5.6 **Revocation soft-fails by default**, as ADR-014 §7 item 2 requires for air-gapped sites: the chain is built with
online revocation checking (the CRL distribution points and OCSP the certificate names), a certificate the CA says is
revoked is refused, and one whose status cannot be learned is accepted and logged. `HardFail` refuses it too; `Off`
checks nothing. **No CRL file is configured here** — the platform's chain engine fetches and caches what the
certificate names — `INFERRED`; an air-gapped site that publishes its CRL internally points its certificates there.

5.7 **`generateToken` takes a certificate**: a request with no `username` and no `password` whose certificate the
organisation's PKI provider maps to a member gets an ArcGIS token, as a password would — so a script with `requests` and
a certificate, and an ArcGIS client that asks for a token on a PKI connection, are served. With a name and password
nothing changes. `INFERRED`: Esri does not document what a PKI portal's `generateToken` takes. Both doors take it: `/rest/generateToken` and the portal's
`/sharing/rest/generateToken`.

5.8 **`Require` checks the chain at the handshake too**, with the same authorities and revocation policy, so a
connection from a certificate nobody vouches for never reaches a route — `RequireCertificate` with the platform's own
validation would consult the machine's store, which is not this deployment's list.

5.9 **Not done here.** A service principal by certificate
(ADR-015 §2's row; API keys serve that). The authorities from the admin API (ADR-014 §7 item 3): they are a file, as the
serving certificate is, read at start — D-486. Groups: a certificate carries none, so ADR-089's mappings do not apply;
the provider's default role does.

5.10 **`INFERRED`, for confirmation:** at most one PKI provider per organisation (§5.1); the account name's spaces as
dots (§5.4); revocation soft-fail by default with no configured CRL file (§5.6); `generateToken` by certificate (§5.7);
`cn` as the default field (§5.1, from Esri's *Subject*); the per-request certificate's cookie rules and its minute
(§5.5a); a proxy placeholder left unreplaced (`{…}`) read as no certificate (§5.3, measured on Caddy 2.10.2).

## 6. Consequences

**Positive.** A smart-card site signs its people in without a password, through Kestrel or its own proxy, and they land
in the same session, role and audit trail as everybody else.

**Negative.** `Allow` shows a certificate picker on every browser that holds a certificate — and a headless Chrome
stalls at it: the console suite timed out on every page against a fixture in `Allow` on a machine whose user holds
certificates, and passed with `Mode=Off` (measured 2026-11-23; the guide says to keep such jobs off an `Allow` address). The authorities are read at
start, so adding a CA is a restart (D-486). A proxy misconfigured to pass the client's header through is a way in for
anybody holding a copy of a member's certificate, which the documentation can warn about and the server cannot detect.

**Ports created.** None: `System.Security.Cryptography.X509Certificates` is the platform's.

**State.** `identity_provider.kind` admits `pki` (migration 147, expand).

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | A deployment's CA writes the chosen field uniquely per person | The deployment's policy; revisit trigger |
| — | Browsers send a client certificate at the handshake when asked, over TLS 1.3 and HTTP/2 | Measured for .NET's client in the tests; browsers per §2 |

## 8. Dependencies

**Depends on:** [ADR-014](ADR-014-tls-and-certificates.md) §2d, §6, §7; [ADR-015](ADR-015-authentication.md);
[ADR-088](ADR-088-sign-in-through-an-openid-connect-provider.md) (the shared door).

**Depended on by:** ADR-316 (Windows sign-in), which shares the migration and the door.

## 9. Revisit triggers

- An organisation whose people's certificates are not unique in any of the three fields.
- A site that needs a CA added without a restart.
- An ArcGIS client that sends a PKI portal something §5.5 and §5.7 do not answer.

## 10. Dissent

None recorded.

## 11. Conditions

1. **Both arrival ways, the refusals and the mapping are tested against real server processes**, with a CA made in the
   test: a certificate through Kestrel signs in and makes the account; the same through a trusted proxy's header; a
   header from a peer that is not a trusted proxy is refused; a certificate from another authority is refused; `cn`,
   `email` and `upn` each name the account; a revoked certificate is refused while `SoftFail` accepts one whose CRL
   cannot be reached and `HardFail` does not; `generateToken` with a certificate gives a token and with a password still
   does.
   **DISCHARGED 2026-11-23** — `ACertificateSignsAMemberInTests` (tests/Graticula.Host.Tests, 11 pass against four
   single-node sites of real server processes over the local PostGIS, a CA and its CRL made in the test, including
   §5.5a's per-request certificate and both `generateToken` doors), and `ACertificateIsReadByItsFieldTests` (8 pass: the
   three fields, the account name, the header's three shapes, a trusted proxy's header against a stranger's and an
   unreplaced placeholder, the settings that refuse to start).
2. **A real Caddy with `client_auth` forwards the certificate** and a member signs in through it, with the
   `docs/deployment.md` example as written. **DISCHARGED 2026-11-23** — Caddy 2.10.2 (the release binary for Windows,
   run on this machine against the local fixture on 18631, `mode verify_if_given`, `trust_pool file` the fixture's CA,
   `header_up X-Client-Cert {http.request.tls.client.certificate_der_base64}`): through Caddy a client with the
   certificate signed in at `/rest/auth/pki/{id}/start` (302, `/rest/whoami` named `Ada.Lovelace`) and got a token from
   both `generateToken` doors; a client without one was told it *sent no certificate*; and a client with no certificate
   that sent its own `X-Client-Cert` holding a real member's certificate got no token — Caddy replaced the header. Two
   things were learned and are in the guide: Caddy sends the placeholder literally when no certificate was presented
   (§5.3), and with `client_auth` on it answers `421` to a request whose `Host` is not the TLS server name, so the site
   is named by its DNS name, not an address.
3. **ArcGIS Pro signs in to a PKI portal here** through the OAuth page with a certificate from the Windows store. Needs
   the owner's machine and a certificate there; not driven by this work.
4. **The ArcGIS API for Python signs in with `cert_file` and `key_file`**, or what it sends is recorded and answered.
   **DISCHARGED 2026-11-23** — arcgis 2.4.3, in a local virtual environment, against the local fixture, at the root and at
   `/studio`: `GIS(url, cert_file=…, key_file=…)` sent `/sharing/rest/info`, `portals/self`, `community/users/{name}` and
   `search` with the certificate and no token, and `users.me` was `Ada.Lovelace` (made at that first request, role
   `org_user`). Before §5.5a the same call asked `info` and `portals/self` only and was anonymous.
5. **The design review of Server › Sign-in's PKI form**, with its findings repaired.
