# ADR-178 — HTTP Basic on the OGC faces, over HTTPS

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `HIGH` — QGIS 3.28 signs in with its own authentication manager and saves a WFS-T insert and delete, seen through the ArcGIS face; the refusals are tested; the credential goes through the existing sign-in, unchanged |
| **Decided** | 2026-10-07 by owner decision — [Q-163](../open-questions.md), answered *"Basic auth, HTTPS'te"* among Basic, a token in the capabilities' URLs, OAuth2 and nothing |
| **Supersedes** | — |
| **Superseded by** | — |
| **Amends** | [ADR-015](ADR-015-authentication.md) — a fourth way to present a credential, on the OGC faces only |

---

## 1. Context

[ADR-169](ADR-169-wfs-t-through-the-one-write-path.md) condition 2, measured on 2026-10-06: QGIS 3.28 opened a layer
from `/wfs?token=…` as editable, and its Transaction was refused with 403. QGIS sends the URL's query string on its
GET requests and not on the Transaction POST, and this server took no other credential from an OGC client. QGIS's
authentication manager can send HTTP Basic, OAuth2 or an identity certificate on every request it makes; it cannot
send a bearer token. Q-163 asked which to accept.

## 2. Alternatives considered

### Alternative A — HTTP Basic, on the OGC faces, over HTTPS (chosen)

**For.** Every OGC desktop client sends it — QGIS, ArcGIS Pro, GDAL, OWSLib — and a user types the name and password
they already have. Nothing new to issue or revoke.

**Against.** The password travels with every request. Over TLS that is what a sign-in form sends once; repeated, it
is a larger target, and a password check per request is an Argon2 derivation per request.

### Alternative B — The token in the capabilities' URLs, as ArcGIS Server does

**For.** No new credential; the client follows the URLs the document gives it.

**Against.** The token is then in every saved QGIS project and every document a user forwards, and it lives as long
as its session.

### Alternative C — OAuth2

**For.** The right shape: no password at the client, scopes, revocation.

**Against.** An administrator registers each client and a user configures the flow in QGIS; for a desktop GIS user
that is the most work, and ADR-076's authorization-code flow is for registered apps rather than desktop clients.

## 3. Counterarguments to the preferred option

*A password on every request is exactly what tokens were introduced to avoid.* It is, and it is bounded here three
ways: only over HTTPS (refused with 403 over plain HTTP, rather than used); only on the OGC faces, where the clients
that cannot send a token are; and the password is checked once per fifteen minutes per credential, after which a
held session answers.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| QGIS edits with it | QGIS 3.28, a Basic auth config, `/wfs` at `version='1.1.0'`: insert committed (*SUCCESS: 1 feature(s) added*), seen through the FeatureServer, then deleted and gone there too | by hand, 2026-10-07 |
| The refusals | Wrong password: 401 with `WWW-Authenticate: Basic realm="Graticula"`; over plain HTTP: 403 saying why; on `/rest/services`, Basic is not read at all | `BasicOnTheOgcFacesTests`, and by hand on both schemes |
| The cost | First request 218 ms (the sign-in), the next 16 ms (a held session) | by hand, 2026-10-07 |

**Found on the way, and recorded rather than fixed:** QGIS 3.28 reads a Transaction response only in WFS 1.0's and
1.1's shape. Given this server's 2.0 response it reported *no features were added* for an insert that was made. A
QGIS user edits through WFS-T with the layer at version 1.1.0, which this server speaks (ADR-168). Also: QGIS caches
a GetCapabilities response on disk across sessions, so a layer added anonymously and then with credentials can keep
the anonymous document, without the Transaction operation, until the cache is cleared.

## 5. Decision

On an OGC face — `/wms`, `/wfs`, `/wcs`, `/wmts`, `/ogc/…`, and a service's own `WMSServer`, `WFSServer`, `WCSServer`
and image-service `WMTS` — a request that carries no token may carry `Authorization: Basic`.

- **Over HTTPS only.** Over plain HTTP the request is refused with 403 and told to use HTTPS, and the credential is
  not checked.
- **Through the existing sign-in.** The credential goes to `LoginService.AuthenticateAsync`, with its throttle and
  audit, and opens a session of fifteen minutes scoped like an ArcGIS token (it does not open the native
  administration API). The session's token is held against an HMAC of the credential under a key made at start-up,
  at most 1,000 credentials, and the next requests in those fifteen minutes cost a session lookup. A session the
  store no longer knows is dropped and the credential checked again.
- **A wrong credential is 401** with `WWW-Authenticate: Basic`, one message for every reason; too many failures are
  429. A request with a token is never asked for a password.
- Everywhere else Basic is not read.

## 6. Consequences

**Positive.** QGIS, and every OGC client with an authentication manager, reads private layers and edits through
WFS-T with the account its user already has.

**Negative.** Passwords cross the network on every OGC request, under TLS. Directory (LDAP) accounts sign in only
where `/rest/auth/login` takes them. A held session survives a password change until the store revokes it.

**State.** In memory, per node: up to 1,000 held credentials, each for fifteen minutes. The sessions they open are
ordinary rows in the session store.

**Ports created.** None.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | OGC clients that need credentials send them preemptively or answer a Basic challenge | QGIS measured; others not |

## 8. Dependencies

**Depends on:** ADR-015 (authentication), ADR-169 (WFS-T).

**Depended on by:** —

## Conditions

1. ArcGIS Pro, added to `/wfs` with a name and password, reads a private layer.

## 9. Revisit triggers

- A request for OAuth2 from a client that has it.
- The OGC request rate makes even one sign-in per fifteen minutes per user visible in the throttle.

## 10. Dissent

None recorded.
