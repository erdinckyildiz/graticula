# ADR-076 — OAuth 2.0 authorization code with PKCE, for registered apps

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` |
| **Decided** | 2026-09-16, by owner decision. Asked how to answer V-30 — ArcGIS Pro and the Maps SDK's own sign-in dialog already use `generateToken` (ADR-040, measured), and OAuth is what web apps registering an `OAuthInfo` and the field apps need — the owner chose **authorization code with PKCE for registered apps, plus refresh tokens and ready registrations for the field apps**, knowing the mobile half cannot be verified here without a device. **That OAuth is built, and that the field apps are in scope, is the owner's. The grants, lifetimes, where the sign-in page lives, what `supportsOAuth` says and which apps ship registered are this ADR's** |
| **Supersedes** | — |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

Every sign-in this server offers is a password posted to `generateToken`. That is enough for
ArcGIS Pro (ADR-040 condition 1, measured) and for the Maps SDK's built-in dialog, and it is not
enough for two kinds of client:

- **A web app that registers an `OAuthInfo`** with an `appId` sends its user to the portal's
  `/sharing/rest/oauth2/authorize` and expects a code back at its own address. It never shows a
  password field of its own.
- **ArcGIS Field Maps and Survey123** sign in to an Enterprise portal through OAuth with a
  client id Esri fixed for each app and a custom-scheme redirect, and keep the user signed in with
  a refresh token.

`portals/self` has said `supportsOAuth: false` since ADR-040, with a comment explaining that
advertising a capability a client then fails to use is how Pro was lost three times.

The protocol is Esri's published REST reference —
`developers.arcgis.com/rest/users-groups-and-items/authorize` and `…/token` — which is the public
specification §5 of CLAUDE.md asks for. Field Maps' registration comes from Esri's own published
script, `github.com/Esri/field-maps-scripts`, notebook *Add Field Maps App ID to ArcGIS
Enterprise*: client id `fieldmaps`, redirects `urn:ietf:wg:oauth:2.0:oob`, `arcgis-fieldmaps://auth/`
and `arcgis-fieldmaps-beta://auth/`.

## 2. Alternatives considered

### Alternative A — authorization code, PKCE, a sign-in page served here, access tokens as ArcGIS-scoped sessions *(chosen)*

**Argument for.** Everything a code grant produces already has a home: an access token is a
session with `scope = 'arcgis'` (Q-154), so it opens every ArcGIS surface and not `/admin`, is
revoked like any session, and is checked by the same middleware. The password is verified by
`LoginService`, so the throttle, the timing equalisation and the audit are the ones every other
door uses. What is new is small and separate: registered apps, one-use codes, and refresh tokens.

**Argument against.** It adds a server-rendered sign-in form — the first page this server shows to
somebody who is not already in the console — and a form that accepts a password is a phishing
target and a login-CSRF target. §4 is how it is kept narrow.

### Alternative B — delegate to an external identity provider

**Rejected for this step.** OIDC and SAML are V-36 and need their own decision; an Enterprise
portal is itself the authorization server for these apps, and the apps do not care where the
password is checked.

### Alternative C — the implicit grant (`response_type=token`)

**Rejected.** It puts the access token in a URL fragment and has no refresh token, so it serves
none of the field apps and is the grant OAuth 2.0 Security BCP advises against. A request for it is
refused with a sentence rather than half-served.

## 3. Decision — the protocol

1. **`GET /sharing/rest/oauth2/authorize`** with `client_id`, `response_type=code`, `redirect_uri`,
   and optionally `state`, `code_challenge`, `code_challenge_method` (`S256` or `plain`, default
   `plain` as Esri documents), `expiration` and `locale`. An unknown client or a redirect that is
   not registered for it is **shown as an error page and never redirected to**, because redirecting
   to an unverified address is the open redirect OAuth warns about. Anything else wrong is sent back
   to the registered redirect as `error=` with the `state`.

   1a. **Measured against the Maps SDK, and not in the reference**: the same endpoints answer under
   `/sharing/oauth2/…` as well as `/sharing/rest/oauth2/…`, because that is where the SDK sends its
   user; and a requested redirect matches a registered one that has no query when the scheme, host,
   port and path are identical and only the requested one carries a query — the SDK sends its own
   page's URL. A path that merely starts with the registered one does not match.
   **Opening the sign-in page twice invalidates the first form** — its token is matched against the
   cookie the newer page set — and the refusal says to start again from the app.
2. **The sign-in form posts to `/sharing/rest/oauth2/signin`**, carrying the request's parameters
   and a form token. The password goes through `LoginService.AuthenticateAsync`. On success
   a **code** is issued — 32 random bytes, stored hashed, **five minutes**, **one use** — and the
   browser is sent to `redirect_uri?code=…&state=…`. For `urn:ietf:wg:oauth:2.0:oob` it is sent to
   `/sharing/rest/oauth2/approval?code=…`, whose page title and text carry the code. **That this is
   what a native SDK reads is `INFERRED`** — the title is where the out-of-band convention has
   historically put it, and condition 4 is a device.
3. **`POST /sharing/rest/oauth2/token`**:
   - `grant_type=authorization_code` with `client_id`, `code`, `redirect_uri` (must equal the one
     the code was issued for) and `code_verifier` when a challenge was sent — **required then, and
     compared in constant time**. **A code is spent by its first exchange, whether or not the
     verifier answered**, so a guessed verifier gets one attempt per code. A code used twice is
     refused, and **the second use revokes the refresh token the first use issued**: a replayed code
     means it leaked.
   - `grant_type=refresh_token` with `client_id` and `refresh_token` → a new access token.
   - `grant_type=exchange_refresh_token` with `client_id`, `redirect_uri` and `refresh_token` → a
     new access token **and a new refresh token**, the old one revoked.
   - `client_credentials` is refused: this step issues no client secrets.
   Responses carry `access_token`, `expires_in` (seconds), `username`, `ssl`, and for the first and
   third grants `refresh_token` and `refresh_token_expires_in`. Errors are Esri's shape:
   `{"error":{"code":400,"error":"invalid_grant","error_description":…,"message":…,"details":[]}}`.
4. **Lifetimes.** An access token defaults to **30 minutes**, Esri's documented default for this
   grant; the token request's `expiration` (minutes) asks for another, and the deployment's own
   session lifetime is the ceiling — the rule every other token door follows (ADR-015 §4). A refresh
   token lives **two weeks**. **`expiration` on the authorize request is not read**: Esri uses it to
   size the refresh token up to 90 days, and a longer-lived refresh token is a decision about how
   long a stolen phone stays signed in, which is the owner's to take rather than a default to copy.
5. **`POST /sharing/rest/oauth2/revokeToken`** revokes a refresh token or an access token presented
   with its `client_id`.

## 4. Decision — keeping the sign-in page narrow

- **It shows which app is asking**, by the title an administrator registered, and the address the
  browser will return to — the two things a person can check before typing a password.
- **No script.** The page is HTML and a `<style>` block under the existing no-script policy. Its
  `form-action` is `'self'` plus the one registered redirect it will reach, because browsers apply
  `form-action` to the redirect that follows a form post and a custom scheme is otherwise blocked.
- **`frame-ancestors 'none'`**, so `display=iframe` is not offered: a sign-in form inside somebody
  else's page is the clickjacking case.
- **A form token matched against a cookie set with the page** — `SameSite=Strict`, `HttpOnly`, scoped
  to `/sharing/rest/oauth2` — ties the post to the page this server rendered, so a page elsewhere cannot
  sign a visitor into an account of its choosing: it can neither read the cookie nor make the browser
  send it. No table: the pair is checked and forgotten.
- **Failures say what the generateToken door says** — one message for a wrong name, a wrong password
  and a disabled account, and the throttle's own words when it applies.

## 5. Decision — registered apps

- **An administrator registers an app** — `admin:manageSecurity` — with a title and its redirect
  URIs through `GET`/`POST /admin/oauth/apps` and `DELETE /admin/oauth/apps/{clientId}`. Deleting an
  app ends its codes and refresh tokens, not the access tokens already issued, which expire on their
  own lifetime. A redirect must be `https://`, `http://localhost` or `http://127.0.0.1` on any port,
  a custom scheme, or `urn:ietf:wg:oauth:2.0:oob`; plain `http` to anywhere else is refused.
- **Field Maps ships registered**, as client id `fieldmaps` with the three redirects Esri's script
  gives, because an Enterprise portal ships it that way and the owner asked for the field apps.
- **Survey123 does not ship registered.** Its client id could not be read from a public source today
  — the support articles that give it answer 403 to an unauthenticated fetch — and a guessed client
  id is a registration that fails on the device with nothing to say why. An administrator who knows
  it registers it; condition 4 is the owner confirming it.

## 6. Decision — what `portals/self` says

**`supportsOAuth` stays `false` in this step.** The Maps SDK sends a user to `oauth2/authorize` for
a portal it holds an `OAuthInfo` for without reading that flag, which is the half measured here
(condition 1). What the flag changes in ArcGIS Pro's sign-in is not known, and Pro is the client
this server lost three times to a capability it advertised; turning it on is condition 3, gated on
signing Pro in afterwards.

## 7. Conditions

1. **The ArcGIS Maps SDK for JavaScript completes the flow against this server**: an `OAuthInfo`
   with a registered `appId`, the sign-in page, the code back at the app, the token exchange with
   PKCE, and a query that spends the token — driven in a headless browser as ADR-073's PBF check was.
   **DISCHARGED 2026-09-16**, against the VPS test fixture, with the SDK 4.30 in headless Edge:
   `OAuthInfo({ popup: false, flowType: "authorization-code" })` → the sign-in page → a code back at
   the app → `POST /sharing/rest/oauth2/token` with `code_verifier` → the SDK spent the token on
   `/sharing/rest` and `portals/self`, which answered as the signed-in account. **The first attempt
   failed twice, and both were facts about the client rather than about the reference**: the SDK
   opens **`/sharing/oauth2/authorize`**, a prefix the REST reference does not name, which answered
   404; and it sends **the page it is on, query string included,** as `redirect_uri`, which no app
   can register in advance. Both are served now (§3.1a), and the conformance scenario asserts both.
   It also sends `expiration=20160` to authorize, which §3.4 does not read.
2. **The protocol's refusals are pinned by tests against PostgreSQL**: a code used twice (and the
   refresh token revoked with it), a wrong `code_verifier`, a redirect not registered, an expired
   code, a refresh token after its app is deleted, and an access token from this flow refused by
   `/admin`.
3. **`supportsOAuth` is turned on only after ArcGIS Pro signs in to the showcase with it on.**
4. **Field Maps signs in on a device** against the showcase; and the owner supplies Survey123's
   client id, or confirms it, so it can ship registered too. *(Open — needs a device and the owner.)*
5. **The console gets a screen for registered apps**, reviewed like every other screen. Until then
   they are registered through the admin API. **DISCHARGED 2026-09-16**: Server › Apps lists every
   registration with the addresses it returns people to in full, registers one (name, addresses one per
   line, optional app ID) and removes one; there is no edit, because an app's addresses are its identity.
   Pinned by `AppsScreenTests` — first-run state with Field Maps, nothing sent without a name, the
   request, a polite live region for the result, focus moved the moment the form closes (the test's
   first run found it left on a hidden field) — against the VPS fixture. Reviewed by ux-designer: its
   finding that removing the built-in Field Maps row warned like any other is fixed, the confirmation
   now names the app ID and addresses it would take to register it again; its finding that the console
   is unreadable at phone width is real, shared by every screen, and is [D-273](../architecture-debt.md).

## 8. Consequences

- A web app with an `appId` signs its users in against this server without handling passwords.
- Field Maps can be pointed at the showcase — whether it then signs in is condition 4.
- This server shows a password form to people arriving from somebody else's app.

**State.** Three tables in the catalogue: `oauth_app` (client id, title, redirect URIs, whether it
shipped registered), `oauth_code` (hash, app, principal, redirect, challenge, expiry, used) and
`oauth_refresh` (hash, app, principal, expiry, revoked). Access tokens are sessions and add nothing.
All shared across nodes, nothing node-local.
